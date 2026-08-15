using System.Globalization;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;
using ValveResourceFormat.Renderer.Shaders.Spirv;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// <see cref="IGraphicsPipeline"/> on Vulkan: one <c>VkPipeline</c> with every piece of fixed-function
/// state baked in, built for dynamic rendering.
/// </summary>
/// <remarks>
/// <para>
/// There is no <c>VkRenderPass</c> anywhere in this type. Vulkan 1.3 dynamic rendering replaces it with
/// a <c>VkPipelineRenderingCreateInfo</c> chained onto pipeline creation, which is why
/// <see cref="GraphicsPipelineDesc"/> carries <see cref="GraphicsPipelineDesc.ColorFormats"/>,
/// <see cref="GraphicsPipelineDesc.DepthFormat"/> and <see cref="GraphicsPipelineDesc.SampleCount"/>:
/// with no render pass object there is nothing to infer them from, and they must match the
/// <c>vkCmdBeginRendering</c> the pipeline is bound inside.
/// </para>
/// <para>
/// <b>Viewport and scissor are dynamic and the caller must set them.</b> Baking a viewport would make
/// every pipeline depend on the resolution and multiply the cache by every render target size, so
/// <see cref="DynamicStates"/> declares both and a command list that binds this pipeline without calling
/// <c>vkCmdSetViewport</c> and <c>vkCmdSetScissor</c> first will fail validation. Nothing else is
/// dynamic: the rest of <see cref="RenderState"/> is baked, which is what makes two draws differing only
/// in blend mode two pipelines. That is the cost being measured by <see cref="VulkanPipelineStats"/>.
/// </para>
/// <para>
/// A pipeline does not own its layout or its shader modules. The layout is shared through
/// <see cref="VulkanPipelineLayoutCache"/>, and a module may back many pipelines, so
/// <see cref="Dispose"/> destroys only the <c>VkPipeline</c>.
/// </para>
/// </remarks>
public sealed unsafe class VulkanGraphicsPipeline : IGraphicsPipeline
{
    private readonly Vk Api;
    private readonly Device Device;

    private Pipeline PipelineHandle;
    private bool Disposed;

    /// <inheritdoc/>
    public GraphicsPipelineDesc Description { get; }

    /// <inheritdoc/>
    public string Name => Description.Name;

    /// <summary>Gets the pipeline handle, or a null handle once disposed.</summary>
    public Pipeline Handle => PipelineHandle;

    /// <summary>Gets the layout this pipeline was created with, which is what descriptor sets and push
    /// constants are bound through. Owned by the layout cache, not by this pipeline.</summary>
    public VulkanPipelineLayout Layout { get; }

    /// <summary>Gets the key this pipeline is cached under.</summary>
    public PipelineCacheKey CacheKey { get; }

    /// <summary>Gets the states left dynamic, which a command list must set before drawing.</summary>
    public static IReadOnlyList<DynamicState> DynamicStates { get; } = [DynamicState.Viewport, DynamicState.Scissor];

    /// <summary>Gets one message per interface problem found while building this pipeline: a descriptor
    /// that breaks the contract's set scheme, or a vertex input the shader reads and the layout does not
    /// supply. Empty when the pipeline is well formed.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Creates a graphics pipeline.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="debugNames">Used to name the pipeline.</param>
    /// <param name="cache">The pipeline cache to compile against.</param>
    /// <param name="layout">The pipeline layout, from the layout cache.</param>
    /// <param name="description">What to build.</param>
    /// <param name="cacheKey">The key it is cached under.</param>
    /// <param name="reflections">The reflected stages, used to cross-check the vertex interface.</param>
    /// <param name="allowNonSolidFill">Whether the device enabled <c>fillModeNonSolid</c>.</param>
    /// <param name="frontFace">Which winding is front facing. See
    /// <see cref="VulkanRenderStateTranslation"/> before changing it from
    /// <see cref="FrontFace.Clockwise"/>.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The description names a shader module this backend did not create.</exception>
    /// <exception cref="VulkanException">The driver rejected the pipeline.</exception>
    public VulkanGraphicsPipeline(
        Vk api,
        Device device,
        VulkanDebugNames debugNames,
        PipelineCache cache,
        VulkanPipelineLayout layout,
        GraphicsPipelineDesc description,
        in PipelineCacheKey cacheKey,
        IReadOnlyList<SpirvReflectionResult> reflections,
        bool allowNonSolidFill,
        FrontFace frontFace = FrontFace.Clockwise)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(debugNames);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(reflections);

        Api = api;
        Device = device;
        Description = description;
        Layout = layout;
        CacheKey = cacheKey;

        var problems = new List<string>(layout.Problems);
        problems.AddRange(CheckVertexInterface(description, reflections));
        Problems = problems;

        var vertex = Module(description.VertexShader, description.Name, nameof(GraphicsPipelineDesc.VertexShader));
        var fragment = description.FragmentShader is null
            ? null
            : Module(description.FragmentShader, description.Name, nameof(GraphicsPipelineDesc.FragmentShader));

        var entryPoints = new nint[2];
        entryPoints[0] = SilkMarshal.StringToPtr(EntryPointOf(reflections, ShaderStage.Vertex));
        entryPoints[1] = fragment is null ? 0 : SilkMarshal.StringToPtr(EntryPointOf(reflections, ShaderStage.Fragment));

        try
        {
            Create(description, vertex, fragment, entryPoints, cache, allowNonSolidFill, frontFace);
        }
        finally
        {
            foreach (var pointer in entryPoints)
            {
                if (pointer != 0)
                {
                    SilkMarshal.Free(pointer);
                }
            }
        }

        debugNames.SetName(ObjectType.Pipeline, PipelineHandle.Handle, description.Name);
    }

    private void Create(
        GraphicsPipelineDesc description,
        VulkanShaderModule vertex,
        VulkanShaderModule? fragment,
        nint[] entryPoints,
        PipelineCache cache,
        bool allowNonSolidFill,
        FrontFace frontFace)
    {
        var stageCount = fragment is null ? 1 : 2;
        var stages = stackalloc PipelineShaderStageCreateInfo[2];

        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertex.Handle,
            PName = (byte*)entryPoints[0],
        };

        if (fragment is not null)
        {
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragment.Handle,
                PName = (byte*)entryPoints[1],
            };
        }

        var attributes = description.VertexInput.Attributes ?? [];
        var bindings = description.VertexInput.Bindings ?? [];

        var vkAttributes = new VertexInputAttributeDescription[attributes.Length];
        var vkBindings = new VertexInputBindingDescription[bindings.Length];

        for (var i = 0; i < attributes.Length; i++)
        {
            var attribute = attributes[i];

            vkAttributes[i] = new VertexInputAttributeDescription
            {
                Location = (uint)attribute.Location,
                Binding = (uint)attribute.Binding,
                Format = FormatTables.ToVkFormat(attribute.Format),
                Offset = (uint)attribute.OffsetInBytes,
            };
        }

        for (var i = 0; i < bindings.Length; i++)
        {
            var binding = bindings[i];

            vkBindings[i] = new VertexInputBindingDescription
            {
                Binding = (uint)binding.Binding,
                Stride = (uint)binding.StrideInBytes,
                InputRate = binding.PerInstance ? VertexInputRate.Instance : VertexInputRate.Vertex,
            };
        }

        var colorFormats = description.ColorFormats ?? [];
        var vkColorFormats = new Format[colorFormats.Length];

        for (var i = 0; i < colorFormats.Length; i++)
        {
            vkColorFormats[i] = FormatTables.ToVkFormat(colorFormats[i]);
        }

        var hasDepth = description.DepthFormat != RhiFormat.Undefined;
        var depthFormat = hasDepth ? FormatTables.ToVkFormat(description.DepthFormat) : Format.Undefined;

        // Only a combined format carries a stencil aspect; naming a stencil format the attachment does
        // not have is a validation error rather than a harmless extra.
        var stencilFormat = description.DepthFormat is RhiFormat.D24_UNorm_S8_UInt or RhiFormat.D32_SFloat_S8_UInt
            ? depthFormat
            : Format.Undefined;

        // A local copy: RenderState is a property of a record class, so its sub-descriptors cannot be
        // passed by reference straight out of it.
        var renderState = description.RenderState;

        // One attachment state replicated across every colour target. RenderState models a single target,
        // as the rendersystemdx11 descriptors it mirrors do, so per-attachment blending has nothing to
        // vary from even though the device enables independentBlend.
        var blendAttachment = VulkanRenderStateTranslation.ToBlendAttachment(in renderState.Blend);
        var blendAttachments = new PipelineColorBlendAttachmentState[colorFormats.Length];
        Array.Fill(blendAttachments, blendAttachment);

        var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };

        var rasterization = VulkanRenderStateTranslation.ToRasterizationState(in renderState.Rasterizer, allowNonSolidFill, frontFace);
        var depthStencil = VulkanRenderStateTranslation.ToDepthStencilState(in renderState.DepthStencil, hasDepth);
        var multisample = VulkanRenderStateTranslation.ToMultisampleState(in renderState.Blend, description.SampleCount);

        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = VulkanRenderStateTranslation.ToVk(description.Topology),
            PrimitiveRestartEnable = false,
        };

        // Counts only: the values themselves are dynamic, so a pipeline is not tied to a resolution.
        var viewport = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1,
        };

        fixed (VertexInputAttributeDescription* attributePointer = vkAttributes)
        fixed (VertexInputBindingDescription* bindingPointer = vkBindings)
        fixed (Format* colorFormatPointer = vkColorFormats)
        fixed (PipelineColorBlendAttachmentState* blendPointer = blendAttachments)
        {
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexAttributeDescriptionCount = (uint)vkAttributes.Length,
                PVertexAttributeDescriptions = vkAttributes.Length == 0 ? null : attributePointer,
                VertexBindingDescriptionCount = (uint)vkBindings.Length,
                PVertexBindingDescriptions = vkBindings.Length == 0 ? null : bindingPointer,
            };

            var colorBlend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = false,
                LogicOp = LogicOp.Copy,
                AttachmentCount = (uint)blendAttachments.Length,
                PAttachments = blendAttachments.Length == 0 ? null : blendPointer,
            };

            var dynamic = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates,
            };

            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ViewMask = 0,
                ColorAttachmentCount = (uint)vkColorFormats.Length,
                PColorAttachmentFormats = vkColorFormats.Length == 0 ? null : colorFormatPointer,
                DepthAttachmentFormat = depthFormat,
                StencilAttachmentFormat = stencilFormat,
            };

            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = (uint)stageCount,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewport,
                PRasterizationState = &rasterization,
                PMultisampleState = &multisample,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &colorBlend,
                PDynamicState = &dynamic,
                Layout = Layout.Handle,

                // Dynamic rendering: no render pass object, and a null handle is how that is stated.
                RenderPass = default,
                Subpass = 0,
            };

            Api.CreateGraphicsPipelines(Device, cache, 1, &info, null, out PipelineHandle)
                .Check($"vkCreateGraphicsPipelines for '{description.Name}'");
        }
    }

    /// <summary>
    /// Reports every vertex input the shader reads that the pipeline's vertex layout does not supply.
    /// </summary>
    /// <param name="description">The pipeline description.</param>
    /// <param name="reflections">The reflected stages.</param>
    /// <returns>One message per missing location, empty when the interface matches.</returns>
    /// <remarks>
    /// Not fatal, and deliberately so: Vulkan permits it and reads undefined values, and there is at
    /// least one legitimate case &#8212; a shader compiled once and drawn with several vertex layouts.
    /// It is however the exact shape of a bug that renders as garbled geometry with nothing in the log,
    /// so it is recorded rather than ignored.
    /// </remarks>
    public static IReadOnlyList<string> CheckVertexInterface(
        GraphicsPipelineDesc description,
        IReadOnlyList<SpirvReflectionResult> reflections)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(reflections);

        var supplied = new HashSet<int>();

        foreach (var attribute in description.VertexInput.Attributes ?? [])
        {
            supplied.Add(attribute.Location);
        }

        var problems = new List<string>();

        foreach (var reflection in reflections)
        {
            if (reflection is null || reflection.Stage != ShaderStage.Vertex)
            {
                continue;
            }

            foreach (var input in reflection.VertexInputs)
            {
                for (var i = 0; i < Math.Max(1, input.LocationCount); i++)
                {
                    if (!supplied.Contains(input.Location + i))
                    {
                        problems.Add(string.Create(CultureInfo.InvariantCulture,
                            $"'{description.Name}': the vertex shader reads '{input.Name}' at location {input.Location + i}, which the vertex input layout does not supply."));
                    }
                }
            }
        }

        return problems;
    }

    private static string EntryPointOf(IReadOnlyList<SpirvReflectionResult> reflections, ShaderStage stage)
    {
        foreach (var reflection in reflections)
        {
            if (reflection is not null && reflection.Stage == stage)
            {
                return reflection.EntryPoint;
            }
        }

        return "main";
    }

    private static VulkanShaderModule Module(IShaderModule module, string pipelineName, string role)
        => module as VulkanShaderModule
        ?? throw new ArgumentException(
            $"Pipeline '{pipelineName}' names a {module.GetType().Name} as its {role}. The Vulkan backend needs a {nameof(VulkanShaderModule)}, which {nameof(IDevice)}.{nameof(IDevice.CreateShaderModule)} on a Vulkan device returns.",
            nameof(module));

    /// <summary>Queues this pipeline on a deletion queue.</summary>
    /// <param name="deletionQueue">The queue to enqueue on.</param>
    /// <param name="frameSerial">The serial of the frame during which destruction was requested.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deletionQueue"/> is <see langword="null"/>.</exception>
    public void EnqueueDestroy(VulkanDeletionQueue deletionQueue, ulong frameSerial)
    {
        ArgumentNullException.ThrowIfNull(deletionQueue);

        if (Disposed || PipelineHandle.Handle == 0)
        {
            return;
        }

        Disposed = true;

        var api = Api;
        var device = Device;
        var handle = PipelineHandle;

        deletionQueue.Enqueue(frameSerial, () => api.DestroyPipeline(device, handle, null));
        PipelineHandle = default;
    }

    /// <summary>Destroys the pipeline. Its layout and shader modules outlive it.</summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        if (PipelineHandle.Handle != 0)
        {
            Api.DestroyPipeline(Device, PipelineHandle, null);
            PipelineHandle = default;
        }
    }
}
