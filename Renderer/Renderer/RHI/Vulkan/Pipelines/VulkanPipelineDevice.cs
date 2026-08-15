using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;
using ValveResourceFormat.Renderer.RHI.Vulkan.Descriptors;
using ValveResourceFormat.Renderer.Shaders.Spirv;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// Creation parameters for a <see cref="VulkanPipelineDevice"/>.
/// </summary>
public sealed record VulkanPipelineOptions
{
    /// <summary>Gets where the <c>VkPipelineCache</c> is loaded from and saved to, or
    /// <see langword="null"/> to use <see cref="VulkanPipelineCache.DefaultPathFor"/>.</summary>
    public string? PipelineCachePath { get; init; }

    /// <summary>Gets a value indicating whether a disk cache is kept at all. Turn it off for a test or
    /// a benchmark that must measure cold compilation.</summary>
    public bool EnableDiskCache { get; init; } = true;

    /// <summary>Gets a value indicating whether the reserved sets declare their whole slot range rather
    /// than only what each shader touches. See <see cref="VulkanDescriptorLayoutCache"/>.</summary>
    /// <remarks>Ignored on a device whose limits cannot hold them; see
    /// <see cref="VulkanDescriptorLayoutCache.CanonicalLayoutsFit"/>.</remarks>
    public bool UseReservedTemplate { get; init; } = true;

    /// <summary>Gets how many pipelines may be compiled at once by <see cref="VulkanPipelineDevice.WarmAsync"/>.
    /// Zero means <see cref="Environment.ProcessorCount"/>.</summary>
    /// <remarks>The counterpart of the driver-side <c>MaxShaderCompilerThreads</c> the OpenGL backend
    /// sets in <c>GLEnvironment.EnableParallelShaderCompile</c>. Vulkan has no such driver setting:
    /// <c>vkCreateGraphicsPipelines</c> compiles on the calling thread, so the parallelism has to come
    /// from calling it on several.</remarks>
    public int MaxCompileThreads { get; init; }

    /// <summary>Gets where diagnostics about the pipeline layer are reported, or <see langword="null"/>
    /// to drop them. Separate from the device's validation messenger, which is an instance-creation
    /// parameter.</summary>
    public RhiMessageCallback? MessageCallback { get; init; }

    /// <summary>Gets a value indicating whether an interface problem fails pipeline creation rather than
    /// being reported. On for a test, off for the viewer.</summary>
    public bool TreatInterfaceProblemsAsErrors { get; init; }

    /// <summary>Gets where the command list writes descriptor bindings, or <see langword="null"/> while
    /// no <see cref="IVulkanDescriptorBinder"/> implementation exists, in which case the binding calls
    /// refuse and everything else records.</summary>
    /// <remarks>Here rather than on <see cref="VulkanCoreOptions"/> because this is the layer that owns
    /// the descriptor set and pipeline layout caches a binder has to write against. When the adapter
    /// joining <c>VulkanDescriptorWriter</c> and <c>VulkanDescriptorAllocator</c> to the command list is
    /// written, this is where it is supplied.</remarks>
    public IVulkanDescriptorBinder? DescriptorBinder { get; init; }

    /// <summary>Gets which triangle winding is front facing.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="FrontFace.CounterClockwise"/>, the same winding OpenGL calls front, because the
    /// negative-height viewport has already put framebuffer space the way round OpenGL's window space
    /// is. Two inversions compose here and only counting both gives the right answer:
    /// </para>
    /// <list type="number">
    /// <item><description>Vulkan's framebuffer Y points down where OpenGL's window Y points up, so with a
    /// positive-height viewport the signed area of a triangle has the opposite sign in the two APIs and
    /// OpenGL's counter-clockwise front would indeed be <see cref="FrontFace.Clockwise"/>
    /// here.</description></item>
    /// <item><description>The contract does not use a positive-height viewport. <c>FlipViewport</c>
    /// submits a negative height, which inverts that axis a second time, and the sign of the area goes
    /// back to matching OpenGL's.</description></item>
    /// </list>
    /// <para>
    /// Change this only alongside the viewport the command list sets, and change both together. Getting
    /// it backwards does not error and does not look like a pipeline bug: it culls exactly the triangles
    /// that should have been drawn. On a closed mesh that means <b>every</b> triangle, so a whole scene
    /// renders as an empty frame with no validation message, no exception and a depth buffer still
    /// holding its clear value &#8212; which is how this stood for a full wave, diagnosed as everything
    /// from a zeroed projection to a missing upload before culling was measured directly.
    /// </para>
    /// </remarks>
    public FrontFace FrontFace { get; init; } = FrontFace.CounterClockwise;
}

/// <summary>
/// The complete Vulkan device: everything <see cref="VulkanRecordingDevice"/> can do, plus pipelines
/// built from SPIR-V reflection, an in-memory cache on <see cref="PipelineCacheKey"/>, a
/// <c>VkPipelineCache</c> that survives the process, and compilation that can be spread across threads.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the type to construct.</b> It is the only one in the backend that satisfies
/// <see cref="IDevice"/> with no member left throwing, and it sits at the end of the same chain the
/// OpenGL backend has: <see cref="VulkanDevice"/> creates resources,
/// <see cref="VulkanRecordingDevice"/> adds recording and submission, and this adds pipelines, mirroring
/// <c>GLDevice</c> to <c>GLRendererDevice</c> to <c>GLRecordingDevice</c>.
/// </para>
/// <para>
/// It derived from <see cref="VulkanDevice"/> directly until the golden harness went to render through
/// Vulkan and found it could not: recording and pipeline creation lived on two <i>siblings</i>, so no
/// single device had both, and the harness had to stand two devices over one core and forward pipeline
/// creation between them. That cost two upload staging rings and two default samplers per device, one
/// of each never used. Deriving here rather than composing there is what removes the need.
/// </para>
/// <para>
/// <see cref="VulkanDevice"/> deliberately cannot create a pipeline, for the same reason
/// <c>GLDevice</c> cannot: a pipeline needs machinery the resource layer has no business owning. This
/// subclass supplies it and nothing else, which is why the two creation methods are
/// <see langword="virtual"/> on the base rather than abstract.
/// </para>
/// <para>
/// <b>A module's SPIR-V has to be registered before a pipeline can be built from it.</b> A Vulkan
/// pipeline layout is derived from what the shaders declare &#8212; descriptor sets, binding types and
/// the size of the push constant block &#8212; and <see cref="VulkanShaderModule"/> keeps only a handle
/// and a hash, not the code. Use <see cref="CreateReflectedShaderModule"/>, which creates and registers
/// in one call, or <see cref="RegisterModuleInterface"/> for a module created through
/// <see cref="IDevice.CreateShaderModule"/>. A pipeline over an unregistered module throws with a
/// message naming both, rather than falling back to a guessed layout: a layout that disagrees with the
/// shader binds the wrong resource without erroring, which is precisely what the contract's descriptor
/// section warns about.
/// </para>
/// <para>
/// <b>Pipeline creation is thread safe, unlike the rest of the backend.</b> That is not an accident of
/// implementation: <c>vkCreateGraphicsPipelines</c> and a <c>VkPipelineCache</c> passed to it are
/// internally synchronised by the specification, and the caches here are guarded. Everything else on
/// this device keeps the single-thread rule.
/// </para>
/// </remarks>
public class VulkanPipelineDevice : VulkanRecordingDevice, Shaders.Spirv.ISpirvModuleRegistry
{
    private readonly ConcurrentDictionary<PipelineCacheKey, Lazy<VulkanGraphicsPipeline>> GraphicsPipelines
        = new(VulkanPipelineKey.Comparer.Instance);

    private readonly ConcurrentDictionary<PipelineCacheKey, Lazy<VulkanComputePipeline>> ComputePipelines
        = new(VulkanPipelineKey.Comparer.Instance);

    private readonly ConcurrentDictionary<ulong, SpirvReflectionResult> Reflections = new();
    private readonly RhiMessageCallback? MessageCallback;
    private readonly bool TreatInterfaceProblemsAsErrors;
    private readonly bool AllowNonSolidFill;
    private readonly FrontFace FrontFace;
    private readonly int OwningThreadId;

    private bool PipelinesDisposed;

    /// <summary>Gets the counters this device raises, including the shader-variant instrumentation.</summary>
    public VulkanPipelineStats Stats { get; } = new();

    /// <summary>Gets the descriptor set layout cache.</summary>
    public VulkanDescriptorLayoutCache DescriptorSetLayouts { get; }

    /// <summary>Gets the pipeline layout cache.</summary>
    public VulkanPipelineLayoutCache PipelineLayouts { get; }

    /// <summary>Gets the driver-side pipeline cache, which is warmed from and saved to disk.</summary>
    public VulkanPipelineCache PipelineCache { get; }

    /// <summary>Gets how many pipelines may be compiled at once by <see cref="WarmAsync"/>.</summary>
    public int MaxCompileThreads { get; }

    /// <summary>Gets the number of distinct pipelines this device has handed out.</summary>
    public int PipelineCount => GraphicsPipelines.Count + ComputePipelines.Count;

    /// <summary>Creates a device that can build pipelines, and the Vulkan core underneath it.</summary>
    /// <param name="messageCallback">Where to route validation and driver diagnostics.</param>
    /// <param name="coreOptions">Core creation parameters, or <see langword="null"/> for the defaults.</param>
    /// <param name="pipelineOptions">Pipeline layer parameters, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="VulkanException">No suitable device exists, or creation failed.</exception>
    public VulkanPipelineDevice(
        RhiMessageCallback? messageCallback = null,
        VulkanCoreOptions? coreOptions = null,
        VulkanPipelineOptions? pipelineOptions = null)
        : this(
            new VulkanCoreDevice((coreOptions ?? new VulkanCoreOptions()) with { MessageCallback = messageCallback }),
            ownsCore: true,
            pipelineOptions)
    {
    }

    /// <summary>Creates a device that can build pipelines, over a core the caller already built.</summary>
    /// <param name="core">The bring-up layer to use.</param>
    /// <param name="ownsCore">Whether disposing this device should dispose <paramref name="core"/>.</param>
    /// <param name="options">Pipeline layer parameters, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="core"/> is <see langword="null"/>.</exception>
    /// <exception cref="VulkanException">Creation failed.</exception>
    public VulkanPipelineDevice(VulkanCoreDevice core, bool ownsCore = false, VulkanPipelineOptions? options = null)
        : base(core, ownsCore, options?.DescriptorBinder)
    {
        ArgumentNullException.ThrowIfNull(core);

        options ??= new VulkanPipelineOptions();

        MessageCallback = options.MessageCallback;
        TreatInterfaceProblemsAsErrors = options.TreatInterfaceProblemsAsErrors;
        MaxCompileThreads = options.MaxCompileThreads > 0 ? options.MaxCompileThreads : Environment.ProcessorCount;
        OwningThreadId = Environment.CurrentManagedThreadId;

        AllowNonSolidFill = QueryNonSolidFill(core);
        FrontFace = options.FrontFace;

        var template = options.UseReservedTemplate;
        var limits = core.Adapter.Properties.Limits;

        if (template && !VulkanDescriptorLayoutCache.CanonicalLayoutsFit(in limits))
        {
            template = false;

            MessageCallback?.Invoke(RhiMessageSeverity.Warning,
                $"'{core.Adapter.Name}' cannot hold the reserved descriptor template within its per-stage descriptor limits, so set layouts will be built from each shader's own declarations. Pipelines will not be layout compatible for the global sets.");
        }

        // Vulkan's floor for maxBoundDescriptorSets is four and the contract now needs five, so a device
        // at the floor cannot bind the scheme at all. Reported rather than thrown, because the pipeline
        // layer is not where a device is chosen and a caller may only be reflecting layouts.
        if (limits.MaxBoundDescriptorSets < DescriptorSets.Count)
        {
            MessageCallback?.Invoke(RhiMessageSeverity.Error,
                $"'{core.Adapter.Name}' binds at most {limits.MaxBoundDescriptorSets} descriptor sets, and the contract's scheme needs {DescriptorSets.Count}. Pipelines will be created but cannot have all their sets bound.");
        }

        var stats = Stats;

        DescriptorSetLayouts = new VulkanDescriptorLayoutCache(
            core.Api,
            core.Handle,
            core.DebugNames,
            template,
            created => stats.Count(created
                ? VulkanPipelineCounter.DescriptorSetLayoutsCreated
                : VulkanPipelineCounter.DescriptorSetLayoutCacheHits));

        PipelineLayouts = new VulkanPipelineLayoutCache(core.Api, core.Handle, core.DebugNames, Stats, DescriptorSetLayouts);

        var path = options.EnableDiskCache
            ? options.PipelineCachePath ?? VulkanPipelineCache.DefaultPathFor(core.Adapter)
            : null;

        PipelineCache = new VulkanPipelineCache(
            core.Api,
            core.Handle,
            core.Adapter,
            core.DebugNames,
            Stats,
            path,
            MessageCallback);
    }

    private static unsafe bool QueryNonSolidFill(VulkanCoreDevice core)
    {
        // VulkanCoreDevice enables fillModeNonSolid exactly when the device reports it, so what the
        // device supports is what the logical device has. Asking beats assuming: a pipeline that names
        // PolygonMode.Line without the feature fails creation outright rather than falling back.
        core.Api.GetPhysicalDeviceFeatures(core.Adapter.Handle, out var features);
        return features.FillModeNonSolid;
    }

    /// <summary>
    /// Creates a shader module and records its SPIR-V interface, so pipelines built from it can derive
    /// their layout.
    /// </summary>
    /// <param name="code">The SPIR-V words.</param>
    /// <param name="stage">The stage the code was compiled for.</param>
    /// <param name="name">Debug name.</param>
    /// <returns>The module.</returns>
    /// <exception cref="InvalidSpirvException">The code is not SPIR-V this backend can reflect.</exception>
    /// <remarks>The reflection is keyed on the module's content hash, so two modules over identical
    /// SPIR-V share one result and reflecting the same code twice costs one scan.</remarks>
    public VulkanShaderModule CreateReflectedShaderModule(ReadOnlySpan<byte> code, ShaderStage stage, string name)
    {
        var module = (VulkanShaderModule)CreateShaderModule(code, stage, name);

        try
        {
            RegisterModuleInterface(module, code);
        }
        catch
        {
            module.Dispose();
            throw;
        }

        return module;
    }

    /// <summary>Records a module's SPIR-V interface.</summary>
    /// <param name="shaderModule">The module the code was compiled into.</param>
    /// <param name="spirv">The SPIR-V the module was created from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shaderModule"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidSpirvException">The code is not SPIR-V this backend can reflect.</exception>
    /// <remarks>For a module created through <see cref="IDevice.CreateShaderModule"/>, which the contract
    /// makes non-virtual and which therefore cannot record this itself.</remarks>
    public void RegisterModuleInterface(IShaderModule shaderModule, ReadOnlySpan<byte> spirv)
    {
        ArgumentNullException.ThrowIfNull(shaderModule);

        if (Reflections.ContainsKey(shaderModule.ContentHash))
        {
            return;
        }

        var reflection = SpirvReflection.Reflect(spirv);

        if (Reflections.TryAdd(shaderModule.ContentHash, reflection))
        {
            Stats.Count(VulkanPipelineCounter.ShaderModulesReflected);
        }
    }

    /// <summary>Gets the recorded interface of a module.</summary>
    /// <param name="module">The module to look up.</param>
    /// <returns>Its reflection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="module"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The module's interface was never registered.</exception>
    public SpirvReflectionResult ReflectionOf(IShaderModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        if (Reflections.TryGetValue(module.ContentHash, out var reflection))
        {
            return reflection;
        }

        throw new InvalidOperationException(
            $"The SPIR-V interface of shader module '{module.Name}' was never registered, so no pipeline layout can be derived from it. Create it with {nameof(CreateReflectedShaderModule)}, or call {nameof(RegisterModuleInterface)} with the same SPIR-V after creating it.");
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="desc"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A module's SPIR-V interface was never registered.</exception>
    /// <exception cref="VulkanException">The driver rejected the pipeline.</exception>
    /// <remarks>Returns the cached pipeline when one with the same <see cref="PipelineCacheKey"/> already
    /// exists, as the contract requires. Two threads asking for the same uncached pipeline compile it
    /// once and both wait on that one compile rather than racing to build two.</remarks>
    public override IGraphicsPipeline CreateGraphicsPipeline(GraphicsPipelineDesc desc)
    {
        ArgumentNullException.ThrowIfNull(desc);
        ObjectDisposedException.ThrowIf(PipelinesDisposed, this);

        var key = VulkanPipelineKey.ForGraphics(desc);

        if (GraphicsPipelines.TryGetValue(key, out var cached))
        {
            Stats.Count(VulkanPipelineCounter.GraphicsCacheHits);
            return cached.Value;
        }

        var lazy = GraphicsPipelines.GetOrAdd(
            key,
            static (k, state) => new Lazy<VulkanGraphicsPipeline>(
                () => state.Device.CompileGraphics(k, state.Desc),
                LazyThreadSafetyMode.ExecutionAndPublication),
            (Device: this, Desc: desc));

        return lazy.Value;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The module's SPIR-V interface was never registered.</exception>
    /// <exception cref="VulkanException">The driver rejected the pipeline.</exception>
    public override IComputePipeline CreateComputePipeline(in ComputePipelineDesc desc)
    {
        ObjectDisposedException.ThrowIf(PipelinesDisposed, this);
        ArgumentNullException.ThrowIfNull(desc.ComputeShader);

        var key = VulkanPipelineKey.ForCompute(in desc);

        if (ComputePipelines.TryGetValue(key, out var cached))
        {
            Stats.Count(VulkanPipelineCounter.ComputeCacheHits);
            return cached.Value;
        }

        var description = desc;

        var lazy = ComputePipelines.GetOrAdd(
            key,
            _ => new Lazy<VulkanComputePipeline>(
                () => CompileCompute(in description),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    private VulkanGraphicsPipeline CompileGraphics(PipelineCacheKey key, GraphicsPipelineDesc desc)
    {
        var reflections = new List<SpirvReflectionResult>(2) { ReflectionOf(desc.VertexShader) };

        if (desc.FragmentShader is not null)
        {
            reflections.Add(ReflectionOf(desc.FragmentShader));
        }

        if (desc.SampleCount > Limits.MaxSampleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(desc), desc.SampleCount, string.Create(CultureInfo.InvariantCulture,
                $"Pipeline '{desc.Name}' asks for {desc.SampleCount} samples; this device supports at most {Limits.MaxSampleCount}."));
        }

        var layout = PipelineLayouts.GetOrCreate(reflections, Limits.MaxPushConstantSize, desc.Name);

        var started = Stopwatch.GetTimestamp();

        var pipeline = new VulkanGraphicsPipeline(
            Core.Api,
            Core.Handle,
            Core.DebugNames,
            PipelineCache.Handle,
            layout,
            desc,
            in key,
            reflections,
            AllowNonSolidFill,
            FrontFace);

        RecordCompile(started);
        Stats.Count(VulkanPipelineCounter.GraphicsPipelinesCreated);
        Stats.RecordVariant(in key);

        ReportProblems(pipeline.Problems, desc.Name);

        return pipeline;
    }

    private VulkanComputePipeline CompileCompute(in ComputePipelineDesc desc)
    {
        var reflection = ReflectionOf(desc.ComputeShader);
        var layout = PipelineLayouts.GetOrCreate([reflection], Limits.MaxPushConstantSize, desc.Name);

        var started = Stopwatch.GetTimestamp();

        var pipeline = new VulkanComputePipeline(
            Core.Api,
            Core.Handle,
            Core.DebugNames,
            PipelineCache.Handle,
            layout,
            in desc,
            reflection);

        RecordCompile(started);
        Stats.Count(VulkanPipelineCounter.ComputePipelinesCreated);

        ReportProblems(pipeline.Problems, desc.Name);

        return pipeline;
    }

    private void RecordCompile(long startedTimestamp)
    {
        var elapsed = (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;

        Stats.Count(VulkanPipelineCounter.CompileMilliseconds, elapsed);
        Stats.Max(VulkanPipelineCounter.SlowestCompileMilliseconds, elapsed);

        if (Environment.CurrentManagedThreadId != OwningThreadId)
        {
            Stats.Count(VulkanPipelineCounter.PipelinesCompiledAsynchronously);
        }
    }

    private void ReportProblems(IReadOnlyList<string> problems, string pipelineName)
    {
        if (problems.Count == 0)
        {
            return;
        }

        if (TreatInterfaceProblemsAsErrors)
        {
            throw new InvalidOperationException(
                $"Pipeline '{pipelineName}' has {problems.Count} interface problems: {string.Join("; ", problems)}");
        }

        foreach (var problem in problems)
        {
            MessageCallback?.Invoke(RhiMessageSeverity.Warning, problem);
        }
    }

    /// <summary>Creates a graphics pipeline on the thread pool.</summary>
    /// <param name="desc">Creation parameters.</param>
    /// <param name="cancellationToken">Cancels the wait, not a compile already under way.</param>
    /// <returns>The pipeline.</returns>
    /// <remarks>The synchronous and asynchronous paths share one cache entry, so a pipeline requested
    /// both ways is compiled once and the second caller waits on the first.</remarks>
    public Task<IGraphicsPipeline> CreateGraphicsPipelineAsync(
        GraphicsPipelineDesc desc,
        CancellationToken cancellationToken = default)
        => Task.Run(() => CreateGraphicsPipeline(desc), cancellationToken);

    /// <summary>Creates a compute pipeline on the thread pool.</summary>
    /// <param name="desc">Creation parameters.</param>
    /// <param name="cancellationToken">Cancels the wait, not a compile already under way.</param>
    /// <returns>The pipeline.</returns>
    public Task<IComputePipeline> CreateComputePipelineAsync(
        ComputePipelineDesc desc,
        CancellationToken cancellationToken = default)
        => Task.Run(() => CreateComputePipeline(in desc), cancellationToken);

    /// <summary>
    /// Compiles a batch of pipelines in parallel, so first load does not serialise on the driver.
    /// </summary>
    /// <param name="descriptions">What to compile. Already cached entries cost nothing.</param>
    /// <param name="cancellationToken">Stops handing out further work.</param>
    /// <returns>How many pipelines were compiled, not counting cache hits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="descriptions"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// This is the Vulkan answer to <c>GLEnvironment.EnableParallelShaderCompile</c>. OpenGL hands the
    /// work to driver threads with one call and the renderer never sees them; Vulkan compiles on
    /// whichever thread calls, so the same effect needs the calls spread across
    /// <see cref="MaxCompileThreads"/>.
    /// </remarks>
    public Task<int> WarmAsync(
        IEnumerable<GraphicsPipelineDesc> descriptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptions);

        var before = Stats[VulkanPipelineCounter.GraphicsPipelinesCreated];

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxCompileThreads,
            CancellationToken = cancellationToken,
        };

        // Parallel.ForEach rather than ForEachAsync, and inside a Task.Run rather than an async method:
        // compiling a pipeline is a synchronous, CPU bound driver call with nothing to await, so the
        // honest shape is one blocking call spread over several threads.
        return Task.Run(
            () =>
            {
                Parallel.ForEach(descriptions, parallelOptions, desc => CreateGraphicsPipeline(desc));

                return (int)(Stats[VulkanPipelineCounter.GraphicsPipelinesCreated] - before);
            },
            cancellationToken);
    }

    /// <summary>
    /// Queues a pipeline for destruction once every frame that could reference it has retired.
    /// </summary>
    /// <param name="pipeline">The pipeline to destroy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pipeline"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="pipeline"/> is not a pipeline this device created.</exception>
    /// <remarks>
    /// Separate from <see cref="IDevice.DeferredDestroy"/>, which is not virtual and so cannot learn
    /// about pipeline handles: it falls through to a list disposed at the next <c>BeginFrame</c>, which
    /// is sooner than the frames in flight have retired. Pipelines are owned by this device's caches and
    /// live for its lifetime, so nothing in the renderer needs this today; it exists for a shader hot
    /// reload, which is the one case that destroys a live pipeline.
    /// </remarks>
    public void DeferredDestroyPipeline(IRhiResource pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        var serial = Core.FrameRing.CurrentSerial;

        switch (pipeline)
        {
            case VulkanGraphicsPipeline graphics:
                graphics.EnqueueDestroy(Core.DeletionQueue, serial);
                break;

            case VulkanComputePipeline compute:
                compute.EnqueueDestroy(Core.DeletionQueue, serial);
                break;

            default:
                throw new ArgumentException(
                    $"Expected a {nameof(VulkanGraphicsPipeline)} or {nameof(VulkanComputePipeline)}, got {pipeline.GetType().Name}. Use {nameof(IDevice)}.{nameof(DeferredDestroy)} for everything else.",
                    nameof(pipeline));
        }
    }

    /// <summary>Renders the pipeline counters and the shader-variant report as text.</summary>
    /// <returns>The report.</returns>
    public string DescribePipelines()
    {
        var report = Stats.ToString();

        return string.Create(CultureInfo.InvariantCulture,
            $"{report}  pipeline layouts                   {PipelineLayouts.LayoutCount:N0}\n  descriptor set layouts             {DescriptorSetLayouts.LayoutCount:N0}\n  disk cache                         {(PipelineCache.LoadedFromDisk ? "warm" : PipelineCache.Rejection.ToString())}\n");
    }

    /// <summary>Destroys every pipeline, layout and cache this device owns, saving the disk cache first.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    /// <remarks>First in the chain, and the order is load bearing: pipelines and layouts go before the
    /// command list's cached image views, which go before the resources those views alias, which go
    /// before the core. Each level of <c>Dispose</c> releases what it owns and then calls the next.</remarks>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !PipelinesDisposed)
        {
            PipelinesDisposed = true;

            // Destroying a pipeline is only legal once nothing is executing, and the base class waits
            // again afterwards for the resources it owns.
            Core.WaitIdle();

            foreach (var lazy in GraphicsPipelines.Values.Where(static l => l.IsValueCreated))
            {
                lazy.Value.Dispose();
            }

            foreach (var lazy in ComputePipelines.Values.Where(static l => l.IsValueCreated))
            {
                lazy.Value.Dispose();
            }

            GraphicsPipelines.Clear();
            ComputePipelines.Clear();
            Reflections.Clear();

            // Saved by its own Dispose, and only worth saving once the run's pipelines are all in it.
            PipelineCache.Dispose();
            PipelineLayouts.Dispose();
            DescriptorSetLayouts.Dispose();
        }

        base.Dispose(disposing);
    }
}
