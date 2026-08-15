using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ValveResourceFormat.Renderer.RHI.OpenGL;

/// <summary>
/// An <see cref="IShaderModule"/> that names an already linked <see cref="Shader"/> rather than carrying a
/// compiled stage of its own.
/// </summary>
/// <remarks>
/// <para>
/// OpenGL cannot build a pipeline out of loose stages the way Vulkan can: a program is linked as a whole,
/// and <see cref="ShaderLoader"/> is what links it. A caller that already holds a <see cref="Shader"/>
/// therefore has nothing a <see cref="GLShaderModule"/> could describe, so this stands in for one and lets
/// a <see cref="GraphicsPipelineDesc"/> be filled in without compiling the source a second time. Obtain one
/// from <see cref="GLRendererDevice.ModuleFor"/>.
/// </para>
/// <para>
/// Both the vertex and the fragment module of a program are backed by that one program and report the same
/// <see cref="ContentHash"/>, which is correct: the hash identifies the program, and a pipeline pairing the
/// same program with the same state is the same pipeline whichever stage it is reached through.
/// </para>
/// <para>
/// The hash covers the shader name and its static combos rather than the GLSL text, because that is the
/// identity <see cref="ShaderLoader"/> caches under and the program handle is not stable across runs. It is
/// FNV-1a like <see cref="GLShaderModule.ContentHash"/> and so survives a persisted cache, but the two are
/// hashes of different things and are not comparable.
/// </para>
/// </remarks>
public sealed class GLProgramModule : IShaderModule
{
    /// <summary>Gets the linked program this module stands for.</summary>
    public Shader Program { get; }

    /// <inheritdoc/>
    /// <remarks>The shader name the program was loaded under, such as <c>vr_complex.vfx</c> or <c>grid</c>.</remarks>
    public string Name => Program.Name;

    /// <inheritdoc/>
    public ShaderStage Stage { get; }

    /// <inheritdoc/>
    public ulong ContentHash { get; }

    internal GLProgramModule(Shader program, ShaderStage stage, ulong contentHash)
    {
        Program = program;
        Stage = stage;
        ContentHash = contentHash;
    }

    /// <summary>Releases the module. The program belongs to <see cref="ShaderLoader"/> and outlives it.</summary>
    public void Dispose()
    {
        // Nothing to release: this module compiled nothing of its own.
    }
}

/// <summary>
/// A <see cref="GLDevice"/> that can link programs, because it can reach the <see cref="ShaderLoader"/> of
/// a <see cref="RendererContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GLDevice"/> is deliberately unable to create a pipeline: on OpenGL a pipeline needs a linked
/// program, programs are linked from <c>.slang</c> sources by <see cref="ShaderLoader"/>, and the device
/// itself has no route to one. This subclass supplies that route and nothing else, which is why
/// <see cref="GLDevice.CreateGraphicsPipeline"/> and <see cref="GLDevice.CreateComputePipeline"/> are
/// <see langword="virtual"/> rather than abstract.
/// </para>
/// <para>
/// Pipelines are cached on <see cref="PipelineCacheKey"/>, so creating one twice from the same program and
/// the same state returns the same object, as <see cref="IDevice.CreateGraphicsPipeline"/> requires. The
/// key is <c>Pack = 1</c> plain-old-data holding no references, so it is hashed and compared over its raw
/// bytes with FNV-1a &#8212; never <see cref="HashCode"/>, whose seed is randomized per process and would
/// silently miss every entry of a cache that outlives a run.
/// </para>
/// <para>
/// This type is not sealed. Recording is still <see cref="GLDevice.BeginCommandList"/>'s unimplemented
/// hole, and the command list layer is expected to derive from this device to fill it.
/// </para>
/// <para>
/// Not thread safe, like the rest of the backend: it must be used on the thread holding the GL context.
/// </para>
/// </remarks>
public class GLRendererDevice : GLDevice
{
    private readonly RendererContext rendererContext;
    private readonly Dictionary<PipelineCacheKey, GLGraphicsPipeline> graphicsPipelines = new(PipelineCacheKeyComparer.Instance);
    private readonly Dictionary<PipelineCacheKey, GLComputePipeline> computePipelines = new(PipelineCacheKeyComparer.Instance);

    /// <summary>Gets the loader every program this device links comes from.</summary>
    public ShaderLoader ShaderLoader => rendererContext.ShaderLoader;

    /// <summary>Gets the number of distinct pipelines this device has handed out.</summary>
    /// <remarks>Cache occupancy, not a count of linked programs: many pipelines share one program.</remarks>
    public int PipelineCount => graphicsPipelines.Count + computePipelines.Count;

    /// <summary>Gets the range describing the renderer's standard per-draw block.</summary>
    /// <remarks>The 92 bytes of <see cref="DrawPushConstants"/> at offset 0, readable from both graphics
    /// stages. The only range <see cref="GLPushConstantBlock"/> can write, so a graphics pipeline that
    /// wants push constants at all wants this one.</remarks>
    public static PushConstantRange DrawConstants { get; } = new(0, DrawPushConstants.SizeInBytes, ShaderStage.AllGraphics);

    /// <summary>Creates a device over the current OpenGL context.</summary>
    /// <param name="rendererContext">The context whose <see cref="ShaderLoader"/> links this device's programs.</param>
    /// <param name="messageCallback">Where to route driver diagnostics, or <see langword="null"/> to leave
    /// whatever debug callback the context already has installed alone.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rendererContext"/> is <see langword="null"/>.</exception>
    public GLRendererDevice(RendererContext rendererContext, RhiMessageCallback? messageCallback = null)
        : base(messageCallback)
    {
        ArgumentNullException.ThrowIfNull(rendererContext);

        this.rendererContext = rendererContext;
    }

    /// <summary>Describes an already linked program as a shader module, so a
    /// <see cref="GraphicsPipelineDesc"/> can be filled in from a <see cref="Shader"/>.</summary>
    /// <param name="program">The linked program.</param>
    /// <param name="stage">The stage the module stands for.</param>
    /// <returns>A module backed by <paramref name="program"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    /// <remarks>Does not link, query or otherwise touch OpenGL, so it costs nothing and does not force a
    /// program the driver is still linking on a worker thread to finish.</remarks>
    public static GLProgramModule ModuleFor(Shader program, ShaderStage stage)
    {
        ArgumentNullException.ThrowIfNull(program);

        return new GLProgramModule(program, stage, HashProgramIdentity(program));
    }

    /// <summary>
    /// Gets the pipeline that draws a program with a render state, on whichever backend the device is.
    /// </summary>
    /// <param name="device">The device to create through, normally <see cref="ICommandList.Device"/>.</param>
    /// <param name="program">The linked program to draw with.</param>
    /// <param name="renderState">The rasterizer, depth-stencil and blend state to draw under.</param>
    /// <param name="vertexInput">The vertex layout, or <see langword="null"/> for <see cref="VertexInputDesc.Empty"/>.</param>
    /// <param name="topology">The primitive topology.</param>
    /// <param name="colorFormats">The colour attachment formats, or <see langword="null"/> for none.</param>
    /// <param name="depthFormat">The depth attachment format, or <see cref="RhiFormat.Undefined"/> for none.</param>
    /// <param name="sampleCount">The sample count, which must match the render pass.</param>
    /// <param name="pushConstants">The push constant range, or <see langword="null"/> for none.</param>
    /// <param name="name">The debug name, or <see langword="null"/> to name the pipeline after the program.</param>
    /// <returns>The pipeline, cached on its <see cref="PipelineCacheKey"/> by whichever device made it.</returns>
    /// <exception cref="NotSupportedException">The device is not an OpenGL one and the program has no
    /// compiled modules to build a pipeline from.</exception>
    /// <remarks>
    /// <para>
    /// What a draw site should call. The instance <see cref="GetOrCreatePipeline"/> below is an OpenGL
    /// convenience, so reaching it means casting the device to this type &#8212; which is exactly what
    /// every draw site used to do, and what threw the moment a device was anything else.
    /// </para>
    /// <para>
    /// OpenGL still goes through that same method, unchanged, so its pipelines and their cache keys are
    /// what they always were. Any other backend is handed the modules <see cref="ShaderLoader"/> actually
    /// compiled: <see cref="ModuleFor"/> returns a stand-in naming a linked OpenGL program, which a
    /// device that never linked it cannot derive a layout from.
    /// </para>
    /// </remarks>
    public static IGraphicsPipeline PipelineFor(
        IDevice device,
        Shader program,
        in RenderState renderState,
        VertexInputDesc? vertexInput = null,
        PrimitiveTopology topology = PrimitiveTopology.TriangleList,
        RhiFormat[]? colorFormats = null,
        RhiFormat depthFormat = RhiFormat.Undefined,
        int sampleCount = 1,
        PushConstantRange? pushConstants = null,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(program);

        if (device is GLRendererDevice gl)
        {
            return gl.GetOrCreatePipeline(program, in renderState, vertexInput, topology, colorFormats, depthFormat, sampleCount, pushConstants, name);
        }

        var modules = program.RendererContext.ShaderLoader.GetShaderModules(program)
            ?? throw new NotSupportedException(
                $"Shader '{program.Name}' has no compiled modules, so no pipeline can be built for a {device.Backend} device. Only the OpenGL backend can draw with a linked program alone.");

        if (!modules.TryGetValue(ShaderProgramType.Vertex, out var vertexModule))
        {
            throw new NotSupportedException(
                $"Shader '{program.Name}' compiled no vertex stage, which a graphics pipeline cannot be built without.");
        }

        var desc = new GraphicsPipelineDesc
        {
            VertexShader = vertexModule,

            // Absent for a depth-only pipeline, which the contract allows and the shadow passes use.
            FragmentShader = modules.GetValueOrDefault(ShaderProgramType.Fragment),
            VertexInput = vertexInput ?? VertexInputDesc.Empty,
            RenderState = renderState,
            Topology = topology,
            ColorFormats = colorFormats ?? [],
            DepthFormat = depthFormat,
            SampleCount = sampleCount,
            PushConstants = pushConstants,
            Name = name ?? program.Name,
        };

        return device.CreateGraphicsPipeline(desc);
    }

    /// <summary>
    /// Gets the pipeline that draws a program with a render state, creating it on first request.
    /// </summary>
    /// <param name="program">The linked program to draw with, from <see cref="ShaderLoader"/>.</param>
    /// <param name="renderState">The rasterizer, depth-stencil and blend state to draw under.</param>
    /// <param name="vertexInput">The vertex layout, or <see langword="null"/> for
    /// <see cref="VertexInputDesc.Empty"/>. Build one from the renderer's existing
    /// <see cref="VertexInputLayout"/> with <see cref="GLVertexInput.ToVertexInputDesc"/>.</param>
    /// <param name="topology">The primitive topology.</param>
    /// <param name="colorFormats">The colour attachment formats the pipeline renders into, or
    /// <see langword="null"/> for none.</param>
    /// <param name="depthFormat">The depth attachment format, or <see cref="RhiFormat.Undefined"/> for none.</param>
    /// <param name="sampleCount">The sample count, which must match the render pass.</param>
    /// <param name="pushConstants">The push constant range, or <see langword="null"/> for none. Only
    /// <see cref="DrawConstants"/> is writable on this backend.</param>
    /// <param name="name">The debug name, or <see langword="null"/> to name the pipeline after the program.</param>
    /// <returns>The pipeline, cached on its <see cref="PipelineCacheKey"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The OpenGL backend's own caching path, kept because it is what makes a pipeline out of a linked
    /// program alone. Call sites should go through <see cref="PipelineFor"/>, which reaches this when the
    /// device is an OpenGL one and builds a description from real modules when it is not.
    /// </remarks>
    public GLGraphicsPipeline GetOrCreatePipeline(
        Shader program,
        in RenderState renderState,
        VertexInputDesc? vertexInput = null,
        PrimitiveTopology topology = PrimitiveTopology.TriangleList,
        RhiFormat[]? colorFormats = null,
        RhiFormat depthFormat = RhiFormat.Undefined,
        int sampleCount = 1,
        PushConstantRange? pushConstants = null,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(program);

        var identity = HashProgramIdentity(program);

        var desc = new GraphicsPipelineDesc
        {
            VertexShader = new GLProgramModule(program, ShaderStage.Vertex, identity),
            FragmentShader = new GLProgramModule(program, ShaderStage.Fragment, identity),
            VertexInput = vertexInput ?? VertexInputDesc.Empty,
            RenderState = renderState,
            Topology = topology,
            ColorFormats = colorFormats ?? [],
            DepthFormat = depthFormat,
            SampleCount = sampleCount,
            PushConstants = pushConstants,
            Name = name ?? program.Name,
        };

        return CreatePipeline(desc, program);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="desc"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The push constant range does not fit
    /// <see cref="IDeviceLimits.MaxPushConstantSize"/>.</exception>
    /// <exception cref="NotSupportedException">The description's vertex module names no program this device
    /// can link.</exception>
    /// <remarks>
    /// Prefer <see cref="GetOrCreatePipeline"/>, which takes the <see cref="Shader"/> directly. This
    /// overload exists to satisfy the contract, and resolves a program from
    /// <see cref="GraphicsPipelineDesc.VertexShader"/>: a <see cref="GLProgramModule"/> carries one, and any
    /// other module is taken to name a shader for <see cref="ShaderLoader"/> to load.
    /// </remarks>
    public override IGraphicsPipeline CreateGraphicsPipeline(GraphicsPipelineDesc desc)
    {
        ArgumentNullException.ThrowIfNull(desc);

        return CreatePipeline(desc, ResolveProgram(desc.VertexShader, desc.Name));
    }

    private GLGraphicsPipeline CreatePipeline(GraphicsPipelineDesc desc, Shader program)
    {
        ValidatePushConstants(desc.PushConstants, desc.Name);

        var key = GLGraphicsPipeline.CreateCacheKey(desc);

        if (graphicsPipelines.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var pipeline = new GLGraphicsPipeline(desc, program);
        graphicsPipelines[key] = pipeline;

        return pipeline;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">The push constant range does not fit
    /// <see cref="IDeviceLimits.MaxPushConstantSize"/>.</exception>
    /// <exception cref="NotSupportedException">The description's module names no program this device can link.</exception>
    /// <remarks>
    /// Unlike a graphics pipeline this one has to wait for the program to finish linking, because
    /// <see cref="IComputePipeline.WorkgroupSize"/> is read back from the linked program. Compute programs
    /// are a handful next to the material shaders, so this does not affect first-load time.
    /// </remarks>
    public override IComputePipeline CreateComputePipeline(in ComputePipelineDesc desc)
    {
        ValidatePushConstants(desc.PushConstants, desc.Name);

        // Compute has no render state, vertex input or render targets, so only the program identifies it.
        // Held in its own dictionary so a graphics pipeline over a default state cannot collide with one.
        var key = new PipelineCacheKey
        {
            VertexShaderHash = desc.ComputeShader.ContentHash,
        };

        if (computePipelines.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var pipeline = new GLComputePipeline(in desc, ResolveProgram(desc.ComputeShader, desc.Name));
        computePipelines[key] = pipeline;

        return pipeline;
    }

    /// <summary>Finds the linked program a module stands for.</summary>
    private Shader ResolveProgram(IShaderModule module, string pipelineName)
    {
        ArgumentNullException.ThrowIfNull(module);

        if (module is GLProgramModule linked)
        {
            return linked.Program;
        }

        // Anything else names a shader rather than carrying a program. Loaded without waiting on the link
        // status so the driver keeps linking on its own threads; ShaderLoader.LinkLoadedShaders collects
        // the failures.
        try
        {
            return ShaderLoader.LoadShader(module.Name, arguments: null, blocking: false);
        }
        catch (Exception e) when (e is System.IO.FileNotFoundException or System.IO.InvalidDataException)
        {
            throw new NotSupportedException(
                $"Pipeline '{pipelineName}' names shader module '{module.Name}', which is not a shader {nameof(ShaderLoader)} can load. On OpenGL a pipeline needs a linked program: pass a {nameof(GLProgramModule)} from {nameof(ModuleFor)}, or use {nameof(GetOrCreatePipeline)} with the {nameof(Shader)} directly.",
                e);
        }
    }

    /// <summary>Rejects a push constant range this device could not write.</summary>
    /// <remarks>Checked here rather than at the first draw, where a block silently written nowhere would
    /// render every draw with stale constants. The block itself stays lazy: resolving its uniform locations
    /// would force the program to finish linking, which is what the parallel compile path avoids.</remarks>
    private void ValidatePushConstants(PushConstantRange? pushConstants, string pipelineName)
    {
        if (pushConstants is not { } range)
        {
            return;
        }

        if (range.OffsetInBytes < 0 || range.SizeInBytes <= 0 || range.OffsetInBytes + range.SizeInBytes > Limits.MaxPushConstantSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pushConstants), range,
                $"Pipeline '{pipelineName}' declares push constants at offset {range.OffsetInBytes} for {range.SizeInBytes} bytes, which does not fit the {Limits.MaxPushConstantSize} byte block. The renderer's block is {nameof(DrawPushConstants)}, {DrawPushConstants.SizeInBytes} bytes; see {nameof(DrawConstants)}.");
        }
    }

    // FNV-1a, matching GLGraphicsPipeline and GLShaderModule. HashCode is seeded per process, so a cache
    // that outlives a run would miss every entry if the hash changed between runs.
    private const ulong FnvOffsetBasis = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    /// <summary>Hashes what makes a program distinct: the shader it was loaded as, and its static combos.</summary>
    private static ulong HashProgramIdentity(Shader program)
    {
        var hash = HashString(FnvOffsetBasis, program.Name);

        // Sorted, because a dictionary's order is not part of the identity.
        var combos = new List<string>(program.Parameters.Count);

        foreach (var combo in program.Parameters.Keys)
        {
            combos.Add(combo);
        }

        combos.Sort(StringComparer.Ordinal);

        foreach (var combo in combos)
        {
            hash = HashString(hash, combo);
            hash ^= program.Parameters[combo];
            hash *= FnvPrime;
        }

        return hash;
    }

    private static ulong HashString(ulong hash, string value)
    {
        foreach (var c in value)
        {
            hash ^= (byte)c;
            hash *= FnvPrime;
            hash ^= (byte)(c >> 8);
            hash *= FnvPrime;
        }

        // Terminator, so that concatenations of different names cannot collide.
        hash ^= 0xFF;
        hash *= FnvPrime;

        return hash;
    }

    /// <summary>Drops every cached pipeline. The programs belong to <see cref="ShaderLoader"/> and are
    /// untouched.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var pipeline in graphicsPipelines.Values)
            {
                pipeline.Dispose();
            }

            foreach (var pipeline in computePipelines.Values)
            {
                pipeline.Dispose();
            }

            graphicsPipelines.Clear();
            computePipelines.Clear();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Hashes and compares a <see cref="PipelineCacheKey"/> over its raw bytes.
    /// </summary>
    /// <remarks>
    /// The key is <c>Pack = 1</c> and holds no references precisely so this is possible: its bytes are its
    /// exact bit image, with no padding to hold indeterminate values and nothing to chase. The default
    /// equality of a <c>record struct</c> would work too, but it hashes field by field through
    /// <see cref="HashCode"/> and so is not reproducible across runs.
    /// </remarks>
    private sealed class PipelineCacheKeyComparer : IEqualityComparer<PipelineCacheKey>
    {
        public static PipelineCacheKeyComparer Instance { get; } = new();

        public bool Equals(PipelineCacheKey x, PipelineCacheKey y)
            => AsBytes(ref x).SequenceEqual(AsBytes(ref y));

        public int GetHashCode(PipelineCacheKey obj)
        {
            var hash = FnvOffsetBasis;

            foreach (var b in AsBytes(ref obj))
            {
                hash ^= b;
                hash *= FnvPrime;
            }

            // The dictionary wants 32 bits; fold rather than truncate so the whole hash contributes.
            return (int)(hash ^ (hash >> 32));
        }

        private static ReadOnlySpan<byte> AsBytes(ref PipelineCacheKey key)
            => MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<PipelineCacheKey, byte>(ref key), Unsafe.SizeOf<PipelineCacheKey>());
    }
}
