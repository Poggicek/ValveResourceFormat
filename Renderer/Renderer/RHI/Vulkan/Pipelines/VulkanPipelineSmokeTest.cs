using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.Buffers;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;
using ValveResourceFormat.Renderer.Shaders;
using ValveResourceFormat.Renderer.Shaders.Spirv;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// A deliberate API misuse the pipeline smoke test can commit, to prove the validation layer is watching.
/// </summary>
/// <remarks>
/// A run that has only ever passed is not evidence that it can fail. Each of these commits one violation
/// and expects <see cref="VulkanInstance.ValidationErrorCount"/> to rise by exactly one; a run in which
/// it does not move means validation is not loaded and every green result in this file is worthless.
/// </remarks>
public enum VulkanPipelineProvocation
{
    /// <summary>Commit no violation. The normal run.</summary>
    None,

    /// <summary>Draw with a pipeline whose colour attachment format is not the one the rendering info
    /// names. The mistake dynamic rendering makes possible and a render pass object would have caught at
    /// creation, which is exactly why <see cref="GraphicsPipelineDesc.ColorFormats"/> exists.</summary>
    RenderingFormatMismatch,

    /// <summary>Create a pipeline layout whose push constant range is larger than the device allows.</summary>
    OversizedPushConstantRange,
}

/// <summary>What <see cref="VulkanPipelineSmokeTest.Run"/> observed.</summary>
/// <param name="Succeeded">Whether every check passed and validation stayed silent. For a provoked run
/// it means the checks held and the provocation was seen, since such a run is expected to be dirty.</param>
/// <param name="ValidationActive">Whether <c>VK_LAYER_KHRONOS_validation</c> was actually loaded. A pass
/// with this <see langword="false"/> proves only that the code ran.</param>
/// <param name="DeviceName">The physical device that was used.</param>
/// <param name="Provocation">The violation this run deliberately committed, if any.</param>
/// <param name="Checks">Every named check and whether it held.</param>
/// <param name="ValidationErrors">Error-severity messages attributable to this code's use of the API.</param>
/// <param name="ValidationWarnings">Warning-severity messages attributable to this code.</param>
/// <param name="ForeignMessages">Error and warning messages that came from the loader's view of the
/// machine rather than from this code, counted separately so an overlay layer cannot fail the run.</param>
/// <param name="ProvokedErrors">How many validation errors the provocation raised. One is the wanted
/// answer: zero means the check has no teeth, more than one means the provocation is not isolated.</param>
/// <param name="Messages">Every message the debug messenger delivered.</param>
/// <param name="PipelineReport">The counters and the shader-variant report.</param>
public readonly record struct VulkanPipelineSmokeTestResult(
    bool Succeeded,
    bool ValidationActive,
    string DeviceName,
    VulkanPipelineProvocation Provocation,
    IReadOnlyList<(string Name, bool Passed)> Checks,
    int ValidationErrors,
    int ValidationWarnings,
    int ForeignMessages,
    int ProvokedErrors,
    IReadOnlyList<string> Messages,
    string PipelineReport)
{
    /// <summary>Renders the result as a report.</summary>
    /// <returns>The report text.</returns>
    public override string ToString()
    {
        var builder = new StringBuilder();

        builder.Append(CultureInfo.InvariantCulture, $"Vulkan pipeline smoke test [{Provocation}]: {(Succeeded ? "PASS" : "FAIL")}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"  device            {DeviceName}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture,
            $"  validation layer  {(ValidationActive ? "loaded" : "NOT LOADED - this run proves little")}");
        builder.AppendLine();

        foreach (var (name, passed) in Checks)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  {(passed ? "ok  " : "FAIL")}  {name}");
            builder.AppendLine();
        }

        builder.Append(CultureInfo.InvariantCulture,
            $"  validation        {ValidationErrors} errors, {ValidationWarnings} warnings ({ForeignMessages} foreign, not counted)");
        builder.AppendLine();

        if (Provocation != VulkanPipelineProvocation.None)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  provoked errors   {ProvokedErrors}");
            builder.AppendLine();
        }

        builder.Append(PipelineReport);

        foreach (var message in Messages)
        {
            builder.Append("  | ").Append(message);
            builder.AppendLine();
        }

        return builder.ToString();
    }
}

/// <summary>
/// Exercises <see cref="VulkanPipelineDevice"/> against a real device with the validation layer required
/// to stay completely silent: real GLSL compiled to SPIR-V, reflected, turned into layouts and
/// pipelines, drawn with through dynamic rendering, deduplicated, warmed in parallel, and persisted to
/// and invalidated from disk.
/// </summary>
/// <remarks>
/// <para>
/// The golden image suite cannot reach any of this &#8212; it is an OpenGL harness and there is no
/// Vulkan path through the renderer yet &#8212; so this is the only thing standing between the pipeline
/// layer and a first consumer. It is written accordingly: the shaders are contract-shaped rather than
/// minimal, with the real 92 byte push constant block from
/// <see cref="VulkanGlsl.PushConstantBlockSource"/> and descriptors in the sets the contract fixes, so
/// what is proven is what the renderer will actually do.
/// </para>
/// <para>
/// Only <c>Validation</c> and <c>Performance</c> typed messages are counted. A consumer machine
/// routinely carries overlay layers from capture software and storefronts whose stale manifests the
/// loader reports at error severity through the same messenger, and counting those would make the result
/// depend on what the developer happens to have installed.
/// </para>
/// </remarks>
public static unsafe class VulkanPipelineSmokeTest
{
    private const int TargetSize = 64;

    /// <summary>Runs the smoke test.</summary>
    /// <param name="provocation">A deliberate violation to commit, or
    /// <see cref="VulkanPipelineProvocation.None"/> for the normal run.</param>
    /// <param name="coreOptions">Core overrides, or <see langword="null"/> for validation-on defaults.</param>
    /// <returns>What happened.</returns>
    public static VulkanPipelineSmokeTestResult Run(
        VulkanPipelineProvocation provocation = VulkanPipelineProvocation.None,
        VulkanCoreOptions? coreOptions = null)
    {
        var messages = new List<string>();
        var checks = new List<(string, bool)>();

        var effective = (coreOptions ?? new VulkanCoreOptions()) with
        {
            EnableValidation = coreOptions?.EnableValidation ?? true,
            EnableSynchronizationValidation = coreOptions?.EnableSynchronizationValidation ?? true,
            ApplicationName = "VulkanPipelineSmokeTest",
        };

        void OnMessage(RhiMessageSeverity severity, string message)
        {
            lock (messages)
            {
                messages.Add($"{severity}: {message}");
            }
        }

        var cachePath = Path.Combine(Path.GetTempPath(), $"vrf_pipeline_cache_{Environment.ProcessId}.bin");

        var pipelineOptions = new VulkanPipelineOptions
        {
            PipelineCachePath = cachePath,
            MessageCallback = OnMessage,
            TreatInterfaceProblemsAsErrors = false,
        };

        if (!SpirvCompiler.IsAvailable)
        {
            checks.Add(("shaderc: the native compiler is available", false));

            return new VulkanPipelineSmokeTestResult(
                false, false, "not reached", provocation, checks, 0, 0, 0, 0, messages,
                "The native shader compiler could not be loaded, so no SPIR-V could be produced and nothing was tested.\n");
        }

        var device = new VulkanPipelineDevice(OnMessage, effective, pipelineOptions);

        var provokedErrors = 0;
        string report;

        try
        {
            var shaders = CompileShaders(checks);

            CheckReflection(shaders, checks);
            CheckKeys(shaders, device, checks);

            var pipelines = CheckGraphicsPipelines(device, shaders, checks);
            CheckComputePipeline(device, shaders, checks);
            CheckVariantFanOut(device, shaders, checks);
            CheckAsynchronousCompilation(device, shaders, checks);
            CheckDrawThroughDynamicRendering(device, pipelines.Trivial, checks);
            CheckDiskCache(device, cachePath, checks);

            if (provocation != VulkanPipelineProvocation.None)
            {
                device.WaitIdle();

                var before = device.Core.Instance.ValidationErrorCount;
                Provoke(device, shaders, provocation);
                device.WaitIdle();

                provokedErrors = device.Core.Instance.ValidationErrorCount - before;

                checks.Add((string.Create(CultureInfo.InvariantCulture,
                    $"provocation {provocation} raised exactly one validation error (raised {provokedErrors})"),
                    provokedErrors == 1));
            }

            report = device.DescribePipelines();

            device.WaitIdle();
        }
        catch
        {
            // Disposal waits for the device to go idle itself, so this both drains and tears down.
            device.Dispose();
            TryDelete(cachePath);
            throw;
        }

        var errors = device.Core.Instance.ValidationErrorCount;
        var warnings = device.Core.Instance.ValidationWarningCount;
        var foreign = device.Core.Instance.ErrorCount + device.Core.Instance.WarningCount - errors - warnings;

        var everyCheckPassed = true;

        foreach (var (_, passed) in checks)
        {
            everyCheckPassed &= passed;
        }

        var succeeded = provocation == VulkanPipelineProvocation.None
            ? everyCheckPassed && errors == 0 && warnings == 0
            : everyCheckPassed;

        var result = new VulkanPipelineSmokeTestResult(
            succeeded,
            device.Core.ValidationEnabled,
            device.Core.Adapter.Name,
            provocation,
            checks.ConvertAll(c => (c.Item1, c.Item2)),
            errors,
            warnings,
            foreign,
            provokedErrors,
            messages,
            report);

        // Disposed before the file is removed, because disposing the device saves the pipeline cache and
        // would otherwise write the temporary file back out after it had been deleted.
        device.Dispose();
        TryDelete(cachePath);

        return result;
    }

    /// <summary>The SPIR-V the test compiles, before any of it reaches the device.</summary>
    private sealed record CompiledShaders(
        byte[] MeshVertex,
        byte[] MeshFragment,
        byte[] Compute,
        byte[] TrivialVertex,
        byte[] TrivialFragment,
        byte[] UnregisteredVertex);

    /// <summary>The pipelines the test built, kept so later checks can reuse them.</summary>
    private sealed record BuiltPipelines(VulkanGraphicsPipeline Mesh, VulkanGraphicsPipeline Trivial);

    private static CompiledShaders CompileShaders(List<(string, bool)> checks)
    {
        var compiler = SpirvCompiler.Shared;

        byte[] Compile(string source, ShaderStage stage, string name)
        {
            var result = compiler.Compile(source, stage);

            checks.Add(($"shaderc: '{name}' compiled to SPIR-V", result.Success));

            if (!result.Success)
            {
                throw new InvalidOperationException($"'{name}' did not compile: {result.FormatDiagnostics()}");
            }

            return result.Spirv.ToArray();
        }

        return new CompiledShaders(
            Compile(MeshVertexSource, ShaderStage.Vertex, "mesh vertex"),
            Compile(MeshFragmentSource, ShaderStage.Fragment, "mesh fragment"),
            Compile(ComputeSource, ShaderStage.Compute, "compute"),
            Compile(TrivialVertexSource, ShaderStage.Vertex, "trivial vertex"),
            Compile(TrivialFragmentSource, ShaderStage.Fragment, "trivial fragment"),
            Compile(UnregisteredVertexSource, ShaderStage.Vertex, "unregistered vertex"));
    }

    private static void CheckReflection(CompiledShaders shaders, List<(string, bool)> checks)
    {
        var vertex = SpirvReflection.Reflect(shaders.MeshVertex);
        var fragment = SpirvReflection.Reflect(shaders.MeshFragment);
        var compute = SpirvReflection.Reflect(shaders.Compute);

        // The number the contract calls load bearing, read back out of a real compiled module rather
        // than out of the table that generated it.
        checks.Add((string.Create(CultureInfo.InvariantCulture,
            $"reflection: the vertex push constant block is {VulkanGlsl.PushConstantSize} bytes (reflected {vertex.PushConstantSizeInBytes})"),
            vertex.PushConstantSizeInBytes == VulkanGlsl.PushConstantSize));

        checks.Add((string.Create(CultureInfo.InvariantCulture,
            $"reflection: the fragment push constant block is {VulkanGlsl.PushConstantSize} bytes (reflected {fragment.PushConstantSizeInBytes})"),
            fragment.PushConstantSizeInBytes == VulkanGlsl.PushConstantSize));

        checks.Add(("reflection: the contract shaders conform to the descriptor set scheme",
            SpirvReflection.ValidateDescriptorSets(vertex).IsEmpty && SpirvReflection.ValidateDescriptorSets(fragment).IsEmpty));

        checks.Add(("reflection: the uniform buffer landed in set 0",
            vertex.DescriptorBindings.Any(b => b.Kind == SpirvResourceKind.UniformBuffer && b.Set == DescriptorSets.UniformBuffers)));

        checks.Add(("reflection: the storage buffer landed in set 1",
            vertex.DescriptorBindings.Any(b => b.Kind == SpirvResourceKind.StorageBuffer && b.Set == DescriptorSets.StorageBuffers)));

        checks.Add(("reflection: the global texture landed in set 2",
            vertex.DescriptorBindings.Any(b => b.Set == DescriptorSets.ReservedTextures)));

        checks.Add(("reflection: the material texture landed in set 3",
            fragment.DescriptorBindings.Any(b => b.Set == DescriptorSets.MaterialTextures)));

        checks.Add(("reflection: the vertex inputs carry their declared locations",
            vertex.VertexInputs.Any(i => i.Location == 0) && vertex.VertexInputs.Any(i => i.Location == 3)));

        checks.Add(("reflection: the compute workgroup size is read back", compute.WorkgroupSize == (64, 1, 1)));

        checks.Add(("reflection: the trivial vertex shader has no inputs and no descriptors",
            SpirvReflection.Reflect(shaders.TrivialVertex) is { VertexInputs.IsEmpty: true, DescriptorBindings.IsEmpty: true, PushConstantSizeInBytes: 0 }));
    }

    private static void CheckKeys(CompiledShaders shaders, VulkanPipelineDevice device, List<(string, bool)> checks)
    {
        // The FNV-1a 64 bit hash of "pipeline", worked out from the published offset basis and prime
        // rather than copied from a previous run of this code. It pins two things at once: that the
        // implementation is really FNV-1a, and that it answers the same on every run. HashCode is seeded
        // per process and would fail this immediately, which is the property a persisted cache depends on.
        const ulong ExpectedFnv = 0x343C11FD413AEEE3;

        checks.Add((string.Create(CultureInfo.InvariantCulture,
            $"key: FNV-1a of \"pipeline\" is a fixed value across runs (got 0x{VulkanPipelineKey.Fnv1a("pipeline"u8):X16})"),
            VulkanPipelineKey.Fnv1a("pipeline"u8) == ExpectedFnv));

        using var vertex = device.CreateReflectedShaderModule(shaders.MeshVertex, ShaderStage.Vertex, "key probe vertex");
        using var fragment = device.CreateReflectedShaderModule(shaders.MeshFragment, ShaderStage.Fragment, "key probe fragment");

        var first = DescribeMeshPipeline(vertex, fragment, RenderState.Default, "key probe");
        var second = DescribeMeshPipeline(vertex, fragment, RenderState.Default, "key probe with another name");

        var keyA = VulkanPipelineKey.ForGraphics(first);
        var keyB = VulkanPipelineKey.ForGraphics(second);

        checks.Add(("key: the debug name is not part of the identity",
            VulkanPipelineKey.Comparer.Instance.Equals(keyA, keyB)));

        var blended = first.RenderState;
        blended.Blend.BlendEnable = !blended.Blend.BlendEnable;

        var keyC = VulkanPipelineKey.ForGraphics(first with { RenderState = blended });

        checks.Add(("key: a render state difference changes the key",
            !VulkanPipelineKey.Comparer.Instance.Equals(keyA, keyC)));

        // The two backends key independently and must not drift, since the cache key is the contract's
        // and both are expected to produce the same one from the same description.
        var glKey = OpenGL.GLGraphicsPipeline.CreateCacheKey(first);

        checks.Add(("key: the OpenGL and Vulkan backends build the same key from the same description",
            VulkanPipelineKey.Comparer.Instance.Equals(keyA, glKey)));
    }

    private static BuiltPipelines CheckGraphicsPipelines(
        VulkanPipelineDevice device,
        CompiledShaders shaders,
        List<(string, bool)> checks)
    {
        using var vertex = device.CreateReflectedShaderModule(shaders.MeshVertex, ShaderStage.Vertex, "mesh vertex");
        using var fragment = device.CreateReflectedShaderModule(shaders.MeshFragment, ShaderStage.Fragment, "mesh fragment");

        var mesh = (VulkanGraphicsPipeline)device.CreateGraphicsPipeline(
            DescribeMeshPipeline(vertex, fragment, RenderState.Default, "mesh"));

        checks.Add(("graphics: a contract shaped pipeline is created", mesh.Handle.Handle != 0));
        checks.Add(("graphics: it reports no interface problems", mesh.Problems.Count == 0));

        checks.Add((string.Create(CultureInfo.InvariantCulture,
            $"layout: the push constant range is the contract's {VulkanGlsl.PushConstantSize} bytes at offset 0"),
            mesh.Layout.PushConstants is { OffsetInBytes: 0, SizeInBytes: VulkanGlsl.PushConstantSize }));

        checks.Add(("layout: the push constant range covers both graphics stages",
            mesh.Layout.PushConstants?.Stages == ShaderStage.AllGraphics));

        checks.Add((string.Create(CultureInfo.InvariantCulture, $"layout: it declares all {DescriptorSets.Count} descriptor sets"),
            mesh.Layout.SetLayouts.Count == DescriptorSets.Count));

        checks.Add(("layout: every set layout is a real handle",
            mesh.Layout.SetLayouts.All(static l => l.Handle != 0)));

        CheckBindingSeam(mesh, PipelineBindPoint.Graphics, checks);

        // Asking twice must hand back the same object, which is what IDevice.CreateGraphicsPipeline
        // promises and what stops a scene rebuilding its pipelines every frame.
        var again = device.CreateGraphicsPipeline(DescribeMeshPipeline(vertex, fragment, RenderState.Default, "mesh"));

        checks.Add(("cache: an identical description returns the same pipeline object", ReferenceEquals(mesh, again)));
        checks.Add(("cache: the hit was counted", device.Stats[VulkanPipelineCounter.GraphicsCacheHits] > 0));

        // A depth-only pipeline, which the shadow passes need: no fragment stage and no colour formats.
        var depthOnly = (VulkanGraphicsPipeline)device.CreateGraphicsPipeline(new GraphicsPipelineDesc
        {
            VertexShader = vertex,
            FragmentShader = null,
            VertexInput = MeshVertexInput(),
            RenderState = RenderState.Default,
            ColorFormats = [],
            DepthFormat = RhiFormat.D32_SFloat,
            Name = "mesh depth only",
        });

        checks.Add(("graphics: a depth only pipeline with no fragment stage is created", depthOnly.Handle.Handle != 0));

        // The fullscreen passes, which fetch no vertex data at all.
        using var trivialVertex = device.CreateReflectedShaderModule(shaders.TrivialVertex, ShaderStage.Vertex, "trivial vertex");
        using var trivialFragment = device.CreateReflectedShaderModule(shaders.TrivialFragment, ShaderStage.Fragment, "trivial fragment");

        var trivial = (VulkanGraphicsPipeline)device.CreateGraphicsPipeline(new GraphicsPipelineDesc
        {
            VertexShader = trivialVertex,
            FragmentShader = trivialFragment,
            VertexInput = VertexInputDesc.Empty,
            RenderState = NoDepth(),
            ColorFormats = [RhiFormat.R8G8B8A8_UNorm],
            Name = "fullscreen",
        });

        checks.Add(("graphics: a pipeline with an empty vertex input is created", trivial.Handle.Handle != 0));
        checks.Add(("graphics: a pipeline that declares nothing needs no push constant range",
            trivial.Layout.PushConstants is null));

        if (device.Limits.MaxSampleCount >= 4)
        {
            var multisampled = (VulkanGraphicsPipeline)device.CreateGraphicsPipeline(new GraphicsPipelineDesc
            {
                VertexShader = trivialVertex,
                FragmentShader = trivialFragment,
                VertexInput = VertexInputDesc.Empty,
                RenderState = NoDepth(),
                ColorFormats = [RhiFormat.R8G8B8A8_UNorm],
                SampleCount = 4,
                Name = "fullscreen 4x",
            });

            checks.Add(("graphics: a 4x multisampled pipeline is created", multisampled.Handle.Handle != 0));
        }
        else
        {
            checks.Add(("graphics: 4x multisampling unsupported on this device, skipped", true));
        }

        checks.Add(("graphics: a sample count past the device limit is refused before the driver sees it",
            Throws<ArgumentOutOfRangeException>(() => device.CreateGraphicsPipeline(new GraphicsPipelineDesc
            {
                VertexShader = trivialVertex,
                FragmentShader = trivialFragment,
                VertexInput = VertexInputDesc.Empty,
                RenderState = NoDepth(),
                ColorFormats = [RhiFormat.R8G8B8A8_UNorm],
                SampleCount = 128,
                Name = "fullscreen impossible",
            }))));

        // The failure mode the class remarks call out: a module whose SPIR-V was never registered has no
        // interface to derive a layout from, and guessing one would bind the wrong resource silently.
        // Deliberately a module nothing else in this run compiles: reflection is keyed on the content
        // hash, so reusing another shader's SPIR-V here would find an interface already registered and
        // the check would pass without proving anything.
        using var unregistered = device.CreateShaderModule(shaders.UnregisteredVertex, ShaderStage.Vertex, "unregistered");

        checks.Add(("graphics: a module with no registered interface is refused by name",
            Throws<InvalidOperationException>(() => device.CreateGraphicsPipeline(new GraphicsPipelineDesc
            {
                VertexShader = unregistered,
                FragmentShader = trivialFragment,
                VertexInput = VertexInputDesc.Empty,
                RenderState = NoDepth(),
                ColorFormats = [RhiFormat.R8G8B8A8_UNorm],
                Name = "unregistered",
            }))));

        return new BuiltPipelines(mesh, trivial);
    }

    private static void CheckComputePipeline(
        VulkanPipelineDevice device,
        CompiledShaders shaders,
        List<(string, bool)> checks)
    {
        using var module = device.CreateReflectedShaderModule(shaders.Compute, ShaderStage.Compute, "compute");

        var pipeline = (VulkanComputePipeline)device.CreateComputePipeline(
            new ComputePipelineDesc(module, "compute"));

        checks.Add(("compute: a pipeline is created", pipeline.Handle.Handle != 0));
        checks.Add(("compute: the workgroup size comes from the module", pipeline.WorkgroupSize == (64, 1, 1)));
        checks.Add(("compute: a thread count rounds up to whole workgroups", pipeline.DispatchGroupsFor(100) == (2, 1, 1)));
        checks.Add(("compute: it reports no interface problems", pipeline.Problems.Count == 0));

        var again = device.CreateComputePipeline(new ComputePipelineDesc(module, "compute again"));

        checks.Add(("compute: the cache returns the same object", ReferenceEquals(pipeline, again)));

        using var graphicsModule = device.CreateReflectedShaderModule(shaders.TrivialVertex, ShaderStage.Vertex, "not compute");

        checks.Add(("compute: a graphics module is refused",
            Throws<ArgumentException>(() => device.CreateComputePipeline(new ComputePipelineDesc(graphicsModule, "wrong stage")))));

        CheckBindingSeam(pipeline, PipelineBindPoint.Compute, checks);
    }

    /// <summary>
    /// Checks that a pipeline can actually be bound: that it implements <see cref="IVulkanPipeline"/>,
    /// and that every member the command list reads through it answers correctly.
    /// </summary>
    /// <param name="pipeline">The pipeline to inspect.</param>
    /// <param name="expected">The bind point it should report.</param>
    /// <param name="checks">Where to record the outcome.</param>
    /// <remarks>
    /// This is a type test, which normally is not worth a check. It is worth one here because the whole
    /// Vulkan path was blocked on exactly this: the pipelines were built, cached and validated clean, and
    /// nothing could bind a single one of them because neither type implemented the interface the command
    /// list binds through. Nothing in pipeline creation notices, since the interface has no bearing on
    /// whether a <c>VkPipeline</c> is well formed &#8212; the failure only appears at the first draw.
    /// </remarks>
    private static void CheckBindingSeam(
        IRhiResource pipeline,
        PipelineBindPoint expected,
        List<(string, bool)> checks)
    {
        var kind = pipeline.GetType().Name;

        if (pipeline is not IVulkanPipeline bindable)
        {
            checks.Add(($"binding: {kind} implements {nameof(IVulkanPipeline)}", false));
            return;
        }

        checks.Add(($"binding: {kind} implements {nameof(IVulkanPipeline)}", true));
        checks.Add(($"binding: {kind} exposes its pipeline handle", bindable.Handle.Handle != 0));
        checks.Add(($"binding: {kind} exposes its layout handle", bindable.Layout.Handle != 0));
        checks.Add(($"binding: {kind} binds to {expected}", bindable.BindPoint == expected));

        // The interface's range has to be the layout's, not a restatement of it: a command list bounds
        // every vkCmdPushConstants write against whatever this answers.
        var layoutRange = pipeline switch
        {
            VulkanGraphicsPipeline graphics => graphics.Layout.PushConstants,
            VulkanComputePipeline compute => compute.Layout.PushConstants,
            _ => null,
        };

        checks.Add(($"binding: {kind} reports the layout's own push constant range",
            bindable.PushConstants == layoutRange));
    }

    private static void CheckVariantFanOut(
        VulkanPipelineDevice device,
        CompiledShaders shaders,
        List<(string, bool)> checks)
    {
        // One shader pair drawn under a spread of render states, which is the shape the material system
        // produces and the thing this layer had to be instrumented for. The states are the ones the
        // renderer actually varies: cull mode, depth function, depth write, blending and fill mode.
        using var vertex = device.CreateReflectedShaderModule(shaders.TrivialVertex, ShaderStage.Vertex, "variant vertex");
        using var fragment = device.CreateReflectedShaderModule(shaders.TrivialFragment, ShaderStage.Fragment, "variant fragment");

        var states = new List<RenderState>();

        foreach (var cull in new[] { CullMode.None, CullMode.Back, CullMode.Front })
        {
            foreach (var blend in new[] { false, true })
            {
                foreach (var depthWrite in new[] { false, true })
                {
                    var state = RenderState.Default;
                    state.Rasterizer.CullMode = cull;
                    state.Blend.BlendEnable = blend;
                    state.DepthStencil.DepthWriteEnable = depthWrite;
                    states.Add(state);
                }
            }
        }

        var before = device.Stats[VulkanPipelineCounter.GraphicsPipelinesCreated];
        var pairsBefore = device.Stats.DistinctShaderPairs;

        foreach (var state in states)
        {
            device.CreateGraphicsPipeline(new GraphicsPipelineDesc
            {
                VertexShader = vertex,
                FragmentShader = fragment,
                VertexInput = VertexInputDesc.Empty,
                RenderState = state,
                ColorFormats = [RhiFormat.R8G8B8A8_UNorm],
                DepthFormat = RhiFormat.D32_SFloat,
                Name = "variant",
            });
        }

        var created = device.Stats[VulkanPipelineCounter.GraphicsPipelinesCreated] - before;

        checks.Add((string.Create(CultureInfo.InvariantCulture,
            $"variants: {states.Count} render states over one shader pair produced {created} pipelines"),
            created == states.Count));

        // At most one, not exactly one: this shader pair may already have produced a pipeline earlier in
        // the run, in which case the twelve add none. Either way twelve render states must not look like
        // twelve programs, which is the thing the fan-out number would be meaningless without.
        checks.Add((string.Create(CultureInfo.InvariantCulture,
            $"variants: {states.Count} pipelines added at most one shader pair (added {device.Stats.DistinctShaderPairs - pairsBefore})"),
            device.Stats.DistinctShaderPairs - pairsBefore <= 1));

        checks.Add((string.Create(CultureInfo.InvariantCulture,
            $"variants: the worst shader pair reports at least {states.Count} variants (reports {device.Stats.WorstCaseVariantsPerShaderPair})"),
            device.Stats.WorstCaseVariantsPerShaderPair >= states.Count));

        // The saving the layout cache exists for: many pipelines, one layout.
        checks.Add(("variants: they share one pipeline layout",
            device.Stats[VulkanPipelineCounter.PipelineLayoutCacheHits] >= states.Count - 1));
    }

    private static void CheckAsynchronousCompilation(
        VulkanPipelineDevice device,
        CompiledShaders shaders,
        List<(string, bool)> checks)
    {
        using var vertex = device.CreateReflectedShaderModule(shaders.TrivialVertex, ShaderStage.Vertex, "async vertex");
        using var fragment = device.CreateReflectedShaderModule(shaders.TrivialFragment, ShaderStage.Fragment, "async fragment");

        var descriptions = new List<GraphicsPipelineDesc>();

        // Distinct depth bias values, which is a cheap way to get many genuinely distinct pipelines out
        // of one shader pair without the states meaning anything.
        for (var i = 1; i <= 32; i++)
        {
            var state = RenderState.Default;
            state.Rasterizer.DepthBias = i;

            descriptions.Add(new GraphicsPipelineDesc
            {
                VertexShader = vertex,
                FragmentShader = fragment,
                VertexInput = VertexInputDesc.Empty,
                RenderState = state,
                ColorFormats = [RhiFormat.R8G8B8A8_UNorm],
                DepthFormat = RhiFormat.D32_SFloat,
                Name = string.Create(CultureInfo.InvariantCulture, $"async {i}"),
            });
        }

        var compiled = device.WarmAsync(descriptions).GetAwaiter().GetResult();

        checks.Add((string.Create(CultureInfo.InvariantCulture,
            $"async: WarmAsync compiled all {descriptions.Count} pipelines (compiled {compiled})"),
            compiled == descriptions.Count));

        checks.Add(("async: at least one compile happened off the calling thread",
            device.Stats[VulkanPipelineCounter.PipelinesCompiledAsynchronously] > 0));

        // Asking again, synchronously, must not recompile: the two paths share one cache entry.
        var before = device.Stats[VulkanPipelineCounter.GraphicsPipelinesCreated];

        foreach (var description in descriptions)
        {
            device.CreateGraphicsPipeline(description);
        }

        checks.Add(("async: the synchronous path reuses what the warm compiled",
            device.Stats[VulkanPipelineCounter.GraphicsPipelinesCreated] == before));

        var single = device.CreateGraphicsPipelineAsync(descriptions[0]).GetAwaiter().GetResult();

        checks.Add(("async: the single asynchronous request returns the cached pipeline",
            ReferenceEquals(single, device.CreateGraphicsPipeline(descriptions[0]))));
    }

    /// <summary>
    /// Renders one triangle through <c>vkCmdBeginRendering</c> with the pipeline bound, which is the only
    /// thing that proves the pipeline's attachment formats and the rendering info actually agree.
    /// </summary>
    /// <remarks>
    /// Creating a pipeline whose formats are wrong is not an error, and neither is binding it: dynamic
    /// rendering has no render pass object to check it against, and the layer only compares the two at
    /// the draw. A test that stopped at creation would therefore pass with the formats mismatched, which
    /// is why the base case draws and why <see cref="VulkanPipelineProvocation.RenderingFormatMismatch"/>
    /// is the provocation worth having.
    /// </remarks>
    private static void CheckDrawThroughDynamicRendering(
        VulkanPipelineDevice device,
        VulkanGraphicsPipeline pipeline,
        List<(string, bool)> checks)
    {
        using var target = (VulkanTexture)device.CreateTexture(new TextureDesc(
            TargetSize,
            TargetSize,
            RhiFormat.R8G8B8A8_UNorm,
            TextureUsage.ColorTarget | TextureUsage.CopySource,
            "SmokeTest colour target"));

        var command = device.Uploads.BeginBatch();

        target.TransitionTo(command, ResourceState.ColorTarget);
        RecordTriangle(device, command, target, pipeline);

        device.Uploads.Flush();

        checks.Add(("draw: a triangle was recorded and submitted through dynamic rendering", true));
        checks.Add(("draw: the target ended in the colour target state", target.StateOf(0, 0) == ResourceState.ColorTarget));
    }

    private static void RecordTriangle(
        VulkanPipelineDevice device,
        CommandBuffer command,
        VulkanTexture target,
        VulkanGraphicsPipeline pipeline)
    {
        var api = device.Core.Api;

        var attachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = target.View,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(new ClearColorValue(0f, 0f, 0f, 1f)),
        };

        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, new Extent2D(TargetSize, TargetSize)),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &attachment,
        };

        api.CmdBeginRendering(command, &rendering);

        // The negative height the contract fixes the Y-flip as. It is what makes VulkanPipelineOptions
        // default FrontFace to CounterClockwise, so setting it here is part of testing that pairing
        // rather than an incidental detail. This test does not check pixels, so it cannot catch the
        // pairing being wrong -- that took a golden scene rendering entirely empty.
        var viewport = new Viewport
        {
            X = 0,
            Y = TargetSize,
            Width = TargetSize,
            Height = -TargetSize,
            MinDepth = 0f,
            MaxDepth = 1f,
        };

        var scissor = new Rect2D(default, new Extent2D(TargetSize, TargetSize));

        api.CmdSetViewport(command, 0, 1, &viewport);
        api.CmdSetScissor(command, 0, 1, &scissor);

        // BindPoint rather than a literal, so this reads the same member VulkanCommandList binds through.
        // The seam itself is covered by CheckBindingSeam, which is what would have caught the pipelines
        // not implementing IVulkanPipeline at all.
        api.CmdBindPipeline(command, pipeline.BindPoint, pipeline.Handle);
        api.CmdDraw(command, 3, 1, 0, 0);

        api.CmdEndRendering(command);
    }

    private static void CheckDiskCache(VulkanPipelineDevice device, string path, List<(string, bool)> checks)
    {
        var blob = device.PipelineCache.GetData();

        checks.Add(("disk cache: the driver produced a cache blob", blob.Length >= VulkanPipelineCache.BlobHeaderSize));
        checks.Add(("disk cache: it was written to disk", device.PipelineCache.Save() && File.Exists(path)));

        var core = device.Core;

        VulkanPipelineCache Reload()
            => new(core.Api, core.Handle, core.Adapter, core.DebugNames, device.Stats, path);

        using (var warm = Reload())
        {
            checks.Add(("disk cache: a file this device wrote is accepted", warm.LoadedFromDisk));
        }

        var original = File.ReadAllBytes(path);

        // A byte flipped inside the payload, which the specification's own header cannot detect and the
        // container's checksum can.
        var corrupt = (byte[])original.Clone();
        corrupt[^1] ^= 0xFF;
        File.WriteAllBytes(path, corrupt);

        using (var rejected = Reload())
        {
            checks.Add(("disk cache: a corrupted payload is rejected",
                rejected.Rejection == VulkanPipelineCacheRejection.Corrupt));
        }

        // A truncated file, the shape a process killed mid-save leaves behind.
        File.WriteAllBytes(path, original.AsSpan(0, original.Length / 2).ToArray());

        using (var rejected = Reload())
        {
            checks.Add(("disk cache: a truncated file is rejected",
                rejected.Rejection == VulkanPipelineCacheRejection.Corrupt));
        }

        // Something else entirely.
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(new string('x', 512)));

        using (var rejected = Reload())
        {
            checks.Add(("disk cache: a foreign file is rejected",
                rejected.Rejection == VulkanPipelineCacheRejection.NotOurFormat));
        }

        // The driver version bumped, with the checksum still correct, which is what a driver update
        // looks like when the vendor keeps the same pipeline cache UUID.
        var otherDriver = (byte[])original.Clone();
        otherDriver[8] ^= 0xFF;
        File.WriteAllBytes(path, otherDriver);

        using (var rejected = Reload())
        {
            checks.Add(("disk cache: a file written by another driver version is rejected",
                rejected.Rejection == VulkanPipelineCacheRejection.DriverChanged));
        }

        // The vendor identifier inside the driver's own header changed, and the container checksum
        // recomputed so only the blob header can catch it.
        var otherDevice = (byte[])original.Clone();
        otherDevice[VulkanPipelineCache.ContainerHeaderSize + 8] ^= 0xFF;
        var payload = otherDevice.AsSpan(VulkanPipelineCache.ContainerHeaderSize);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
            otherDevice.AsSpan(16),
            VulkanPipelineKey.Fnv1a(payload));
        File.WriteAllBytes(path, otherDevice);

        using (var rejected = Reload())
        {
            checks.Add(("disk cache: a file written by another device is rejected",
                rejected.Rejection == VulkanPipelineCacheRejection.DeviceChanged));
        }

        File.WriteAllBytes(path, original);
    }

    /// <summary>Records one deliberately invalid operation, so the validation layer has something to report.</summary>
    private static void Provoke(
        VulkanPipelineDevice device,
        CompiledShaders shaders,
        VulkanPipelineProvocation provocation)
    {
        switch (provocation)
        {
            case VulkanPipelineProvocation.RenderingFormatMismatch:
                ProvokeFormatMismatch(device, shaders);
                break;

            case VulkanPipelineProvocation.OversizedPushConstantRange:
                ProvokeOversizedPushConstants(device);
                break;

            default:
                break;
        }
    }

    private static void ProvokeFormatMismatch(VulkanPipelineDevice device, CompiledShaders shaders)
    {
        using var vertex = device.CreateReflectedShaderModule(shaders.TrivialVertex, ShaderStage.Vertex, "provocation vertex");
        using var fragment = device.CreateReflectedShaderModule(shaders.TrivialFragment, ShaderStage.Fragment, "provocation fragment");

        // A perfectly legal pipeline; it is only wrong once it meets a rendering info that says otherwise.
        var mismatched = (VulkanGraphicsPipeline)device.CreateGraphicsPipeline(new GraphicsPipelineDesc
        {
            VertexShader = vertex,
            FragmentShader = fragment,
            VertexInput = VertexInputDesc.Empty,
            RenderState = NoDepth(),
            ColorFormats = [RhiFormat.R16G16B16A16_SFloat],
            Name = "provocation format mismatch",
        });

        using var target = (VulkanTexture)device.CreateTexture(new TextureDesc(
            TargetSize,
            TargetSize,
            RhiFormat.R8G8B8A8_UNorm,
            TextureUsage.ColorTarget,
            "Provocation colour target"));

        var command = device.Uploads.BeginBatch();

        target.TransitionTo(command, ResourceState.ColorTarget);
        RecordTriangle(device, command, target, mismatched);

        device.Uploads.Flush();
    }

    private static void ProvokeOversizedPushConstants(VulkanPipelineDevice device)
    {
        var core = device.Core;
        var layouts = new DescriptorSetLayout[DescriptorSets.Count];

        for (var set = 0; set < layouts.Length; set++)
        {
            layouts[set] = device.DescriptorSetLayouts.Empty(set).Handle;
        }

        var range = new PushConstantRange(0, device.Limits.MaxPushConstantSize + 4, ShaderStage.AllGraphics);

        using var layout = new VulkanPipelineLayout(
            core.Api,
            core.Handle,
            core.DebugNames,
            layouts,
            range,
            [],
            "Provocation oversized push constants");
    }

    private static GraphicsPipelineDesc DescribeMeshPipeline(
        IShaderModule vertex,
        IShaderModule fragment,
        RenderState renderState,
        string name)
        => new()
        {
            VertexShader = vertex,
            FragmentShader = fragment,
            VertexInput = MeshVertexInput(),
            RenderState = renderState,
            ColorFormats = [RhiFormat.R8G8B8A8_UNorm],
            DepthFormat = RhiFormat.D32_SFloat,
            Name = name,
        };

    private static VertexInputDesc MeshVertexInput()
        => new(
            [
                new VertexAttributeDesc(0, RhiFormat.R32G32B32_SFloat, 0),
                new VertexAttributeDesc(3, RhiFormat.R32G32_SFloat, 12),
            ],
            [new VertexBindingDesc(0, 20)]);

    private static RenderState NoDepth()
    {
        var state = RenderState.Default;
        state.DepthStencil.DepthTestEnable = false;
        state.DepthStencil.DepthWriteEnable = false;
        state.Rasterizer.CullMode = CullMode.None;

        return state;
    }

    private static bool Throws<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is not worth failing the run over.
        }
    }

    /// <summary>
    /// A vertex shader shaped like the renderer's mesh path: descriptors in the sets the contract fixes,
    /// the real generated push constant block, and vertex inputs at their canonical locations.
    /// </summary>
    private static string MeshVertexSource =>
        $$"""
        #version 460

        layout(set = {{DescriptorSets.UniformBuffers}}, binding = {{(int)ReservedBufferSlots.View}}, std140) uniform ViewConstants
        {
            mat4 g_matWorldToProjection;
        } view;

        layout(set = {{DescriptorSets.StorageBuffers}}, binding = {{(int)ReservedBufferSlots.Transforms}}, std430) readonly buffer TransformBuffer
        {
            vec4 rows[];
        } transforms;

        layout(set = {{DescriptorSets.ReservedTextures}}, binding = {{(int)ReservedTextureSlots.BRDFLookup}}) uniform sampler2D g_tBRDFLookup;

        {{VulkanGlsl.PushConstantBlockSource}}

        layout(location = 0) in vec3 vPOSITION;
        layout(location = 3) in vec2 vTEXCOORD;

        layout(location = 0) out vec2 vTexCoordOut;
        layout(location = 1) out vec4 vTintOut;

        void main()
        {
            vTexCoordOut = vTEXCOORD * morphCompositeTextureSize;
            vTintOut = unpackUnorm4x8(vTint) + transforms.rows[meshId] + texture(g_tBRDFLookup, vTEXCOORD);

            vec3 world = vec4(vPOSITION, 1.0) * transform;

            if (bIsInstancing)
            {
                world += vec3(uAnimationData) + vec3(float(morphVertexIdOffset));
            }

            gl_Position = view.g_matWorldToProjection * vec4(world, 1.0);
        }
        """;

    /// <summary>The fragment half of <see cref="MeshVertexSource"/>, sharing its push constant block.</summary>
    private static string MeshFragmentSource =>
        $$"""
        #version 460

        layout(set = {{DescriptorSets.ReservedTextures}}, binding = {{(int)ReservedTextureSlots.BlueNoise}}) uniform sampler2D g_tBlueNoise;
        layout(set = {{DescriptorSets.MaterialTextures}}, binding = 0) uniform sampler2D g_tColor;
        layout(set = {{DescriptorSets.MaterialTextures}}, binding = 1) uniform sampler2D g_tNormal;

        {{VulkanGlsl.PushConstantBlockSource}}

        layout(location = 0) in vec2 vTexCoordOut;
        layout(location = 1) in vec4 vTintOut;

        layout(location = 0) out vec4 outColor;

        void main()
        {
            outColor = texture(g_tColor, vTexCoordOut)
                * texture(g_tNormal, vTexCoordOut)
                * texture(g_tBlueNoise, vTexCoordOut)
                * vTintOut
                * float(shaderId + shaderProgramId);
        }
        """;

    /// <summary>A compute shader with a declared workgroup size and descriptors in the contract's sets.</summary>
    private static string ComputeSource =>
        $$"""
        #version 460

        layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

        layout(set = {{DescriptorSets.UniformBuffers}}, binding = {{(int)ReservedBufferSlots.CullParams}}, std140) uniform CullParams
        {
            vec4 g_vCullBounds;
        } cull;

        layout(set = {{DescriptorSets.StorageBuffers}}, binding = {{(int)ReservedBufferSlots.Histogram}}, std430) buffer Histogram
        {
            uint bins[];
        } histogram;

        void main()
        {
            uint index = gl_GlobalInvocationID.x;
            histogram.bins[index] = uint(cull.g_vCullBounds.x) + index;
        }
        """;

    /// <summary>A vertex shader that fetches nothing and declares nothing: the fullscreen triangle.</summary>
    private const string TrivialVertexSource =
        """
        #version 460

        void main()
        {
            vec2 corner = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
            gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// A vertex shader compiled but never registered, so a pipeline over it has no interface to derive a
    /// layout from. Its body differs from <see cref="TrivialVertexSource"/> only so that its SPIR-V, and
    /// therefore its content hash, is genuinely its own.
    /// </summary>
    private const string UnregisteredVertexSource =
        """
        #version 460

        void main()
        {
            vec2 corner = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
            gl_Position = vec4(corner * 3.0 - 1.5, 0.25, 1.0);
        }
        """;

    /// <summary>The fragment half of <see cref="TrivialVertexSource"/>.</summary>
    private const string TrivialFragmentSource =
        """
        #version 460

        layout(location = 0) out vec4 outColor;

        void main()
        {
            outColor = vec4(1.0, 0.5, 0.25, 1.0);
        }
        """;
}
