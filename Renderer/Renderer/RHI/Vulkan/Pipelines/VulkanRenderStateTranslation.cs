using Silk.NET.Vulkan;
using VkBlendFactor = Silk.NET.Vulkan.BlendFactor;
using VkPrimitiveTopology = Silk.NET.Vulkan.PrimitiveTopology;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// Translates the renderer's <see cref="RenderState"/> and topology into the Vulkan pipeline state
/// structures, one function per <c>VkGraphicsPipelineCreateInfo</c> sub-state.
/// </summary>
/// <remarks>
/// <para>
/// The OpenGL backend applies the same descriptors through <c>RenderStateTracker</c>, which diffs them
/// and emits only the calls that changed. Vulkan cannot do that: the state is baked into the pipeline
/// object, which is where the equivalent saving comes from and also where the combinatorial cost comes
/// from. Every field of <see cref="RenderState"/> that differs between two draws is a second pipeline.
/// </para>
/// <para>
/// <b>Winding is inverted relative to OpenGL, deliberately.</b> The contract fixes the Y-flip as a
/// negative-height viewport. That flips one axis of framebuffer space, which reverses the sign of the
/// signed area Vulkan classifies facing by, so the OpenGL default of counter-clockwise-front becomes
/// <see cref="FrontFace.Clockwise"/> here. Getting this backwards does not error: it culls exactly the
/// triangles that should have been drawn and draws the ones that should have been culled, which reads
/// as inside-out geometry rather than as a bug in the pipeline layer. See
/// <see cref="VulkanPipelineOptions.FrontFace"/> if the presentation layer ever chooses a positive
/// height instead.
/// </para>
/// </remarks>
public static class VulkanRenderStateTranslation
{
    /// <summary>Translates the primitive topology.</summary>
    /// <param name="topology">The contract topology.</param>
    /// <returns>The Vulkan topology.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="topology"/> is not a known topology.</exception>
    public static VkPrimitiveTopology ToVk(PrimitiveTopology topology) => topology switch
    {
        PrimitiveTopology.PointList => VkPrimitiveTopology.PointList,
        PrimitiveTopology.LineList => VkPrimitiveTopology.LineList,
        PrimitiveTopology.LineStrip => VkPrimitiveTopology.LineStrip,
        PrimitiveTopology.TriangleList => VkPrimitiveTopology.TriangleList,
        PrimitiveTopology.TriangleStrip => VkPrimitiveTopology.TriangleStrip,
        _ => throw new ArgumentOutOfRangeException(nameof(topology), topology, "Unknown primitive topology."),
    };

    /// <summary>Translates a comparison function.</summary>
    /// <param name="comparison">The comparison to translate.</param>
    /// <returns>The Vulkan compare operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="comparison"/> is not a known comparison.</exception>
    /// <remarks>The renderer is reverse-Z, so <see cref="Comparison.Closer"/> is greater-than, matching
    /// <c>RenderStateTracker.ToGL</c> exactly.</remarks>
    public static CompareOp ToVk(Comparison comparison) => comparison switch
    {
        Comparison.Never => CompareOp.Never,
        Comparison.Less => CompareOp.Less,
        Comparison.Equal => CompareOp.Equal,
        Comparison.LessEqual => CompareOp.LessOrEqual,
        Comparison.Greater => CompareOp.Greater,
        Comparison.NotEqual => CompareOp.NotEqual,
        Comparison.GreaterEqual => CompareOp.GreaterOrEqual,
        Comparison.Always => CompareOp.Always,
        Comparison.Closer => CompareOp.Greater,
        Comparison.CloserEqual => CompareOp.GreaterOrEqual,
        Comparison.Farther => CompareOp.Less,
        Comparison.FartherEqual => CompareOp.LessOrEqual,
        _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison, "Unknown comparison."),
    };

    /// <summary>Translates a stencil operation.</summary>
    /// <param name="operation">The operation to translate.</param>
    /// <returns>The Vulkan stencil operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is not a known operation.</exception>
    public static StencilOp ToVk(StencilOperation operation) => operation switch
    {
        StencilOperation.Keep => StencilOp.Keep,
        StencilOperation.Zero => StencilOp.Zero,
        StencilOperation.Replace => StencilOp.Replace,
        StencilOperation.IncrementSaturate => StencilOp.IncrementAndClamp,
        StencilOperation.DecrementSaturate => StencilOp.DecrementAndClamp,
        StencilOperation.Invert => StencilOp.Invert,
        StencilOperation.Increment => StencilOp.IncrementAndWrap,
        StencilOperation.Decrement => StencilOp.DecrementAndWrap,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown stencil operation."),
    };

    /// <summary>Translates a blend factor.</summary>
    /// <param name="factor">The factor to translate.</param>
    /// <returns>The Vulkan blend factor.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is not a known factor.</exception>
    /// <remarks>The same mapping serves the colour and the alpha factor, as <c>glBlendFunc</c> does:
    /// Vulkan's alpha blending reads the alpha component of whatever the factor names, which is the same
    /// rule OpenGL applies.</remarks>
    public static VkBlendFactor ToVk(BlendFactor factor) => factor switch
    {
        BlendFactor.Zero => VkBlendFactor.Zero,
        BlendFactor.One => VkBlendFactor.One,
        BlendFactor.SrcColor => VkBlendFactor.SrcColor,
        BlendFactor.OneMinusSrcColor => VkBlendFactor.OneMinusSrcColor,
        BlendFactor.SrcAlpha => VkBlendFactor.SrcAlpha,
        BlendFactor.OneMinusSrcAlpha => VkBlendFactor.OneMinusSrcAlpha,
        BlendFactor.DstColor => VkBlendFactor.DstColor,
        BlendFactor.OneMinusDstColor => VkBlendFactor.OneMinusDstColor,
        BlendFactor.DstAlpha => VkBlendFactor.DstAlpha,
        BlendFactor.OneMinusDstAlpha => VkBlendFactor.OneMinusDstAlpha,
        _ => throw new ArgumentOutOfRangeException(nameof(factor), factor, "Unknown blend factor."),
    };

    /// <summary>Translates a cull mode.</summary>
    /// <param name="mode">The mode to translate.</param>
    /// <returns>The Vulkan cull mode flags.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a known mode.</exception>
    public static CullModeFlags ToVk(CullMode mode) => mode switch
    {
        CullMode.None => CullModeFlags.None,
        CullMode.Back => CullModeFlags.BackBit,
        CullMode.Front => CullModeFlags.FrontBit,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown cull mode."),
    };

    /// <summary>Translates a sample count.</summary>
    /// <param name="sampleCount">The number of samples, which must be a power of two from 1 to 64.</param>
    /// <returns>The matching flag.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sampleCount"/> is not a legal sample count.</exception>
    public static SampleCountFlags ToSampleCount(int sampleCount) => sampleCount switch
    {
        1 => SampleCountFlags.Count1Bit,
        2 => SampleCountFlags.Count2Bit,
        4 => SampleCountFlags.Count4Bit,
        8 => SampleCountFlags.Count8Bit,
        16 => SampleCountFlags.Count16Bit,
        32 => SampleCountFlags.Count32Bit,
        64 => SampleCountFlags.Count64Bit,
        _ => throw new ArgumentOutOfRangeException(nameof(sampleCount), sampleCount,
            "A sample count must be a power of two between 1 and 64."),
    };

    /// <summary>Builds the rasterization state.</summary>
    /// <param name="rasterizer">The rasterizer descriptor.</param>
    /// <param name="allowNonSolidFill">Whether the device enabled <c>fillModeNonSolid</c>. Wireframe is
    /// silently downgraded to solid when it did not, rather than failing pipeline creation.</param>
    /// <param name="frontFace">Which winding is front facing. See the class remarks before changing it
    /// from <see cref="FrontFace.Clockwise"/>.</param>
    /// <returns>The state.</returns>
    public static PipelineRasterizationStateCreateInfo ToRasterizationState(
        in RasterizerStateDesc rasterizer,
        bool allowNonSolidFill,
        FrontFace frontFace = FrontFace.Clockwise)
    {
        // GL's PolygonOffsetClamp(factor, units, clamp) is (slope scaled, constant, clamp), which is the
        // same triple Vulkan takes; only the field names differ.
        var biased = rasterizer.DepthBias != 0f || rasterizer.SlopeScaledDepthBias != 0f;

        return new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            DepthClampEnable = false,
            RasterizerDiscardEnable = false,
            PolygonMode = rasterizer.FillMode == FillMode.Wireframe && allowNonSolidFill
                ? PolygonMode.Line
                : PolygonMode.Fill,
            CullMode = ToVk(rasterizer.CullMode),
            FrontFace = frontFace,
            DepthBiasEnable = biased,
            DepthBiasConstantFactor = rasterizer.DepthBias,
            DepthBiasClamp = rasterizer.DepthBiasClamp,
            DepthBiasSlopeFactor = rasterizer.SlopeScaledDepthBias,
            LineWidth = 1f,
        };
    }

    /// <summary>Builds the depth and stencil state.</summary>
    /// <param name="depthStencil">The depth-stencil descriptor.</param>
    /// <param name="hasDepthAttachment">Whether the pipeline renders into a depth attachment at all.
    /// Depth testing and writing are forced off when it does not, since there is nothing to test against.</param>
    /// <returns>The state.</returns>
    public static PipelineDepthStencilStateCreateInfo ToDepthStencilState(
        in DepthStencilStateDesc depthStencil,
        bool hasDepthAttachment)
    {
        var stencil = depthStencil.Stencil;

        // One set of operations serves both faces, matching glStencilOp/glStencilFunc, which are the
        // two-sided calls' front-and-back form.
        var face = new StencilOpState
        {
            FailOp = ToVk(stencil.FailOp),
            PassOp = ToVk(stencil.PassOp),
            DepthFailOp = ToVk(stencil.DepthFailOp),
            CompareOp = ToVk(stencil.Func),
            CompareMask = stencil.ReadMask,
            WriteMask = stencil.WriteMask,
            Reference = depthStencil.StencilRef,
        };

        return new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = hasDepthAttachment && depthStencil.DepthTestEnable,
            DepthWriteEnable = hasDepthAttachment && depthStencil.DepthWriteEnable,
            DepthCompareOp = ToVk(depthStencil.DepthFunc),
            DepthBoundsTestEnable = false,
            StencilTestEnable = hasDepthAttachment && stencil.StencilEnable,
            Front = face,
            Back = face,
            MinDepthBounds = 0f,
            MaxDepthBounds = 1f,
        };
    }

    /// <summary>Builds the blend state of one colour attachment.</summary>
    /// <param name="blend">The blend descriptor.</param>
    /// <returns>The attachment state.</returns>
    /// <remarks><see cref="BlendStateDesc.AlphaToCoverageEnable"/> is deliberately not read here: Vulkan
    /// carries alpha-to-coverage in the multisample state, not the blend state. See
    /// <see cref="ToMultisampleState"/>.</remarks>
    public static PipelineColorBlendAttachmentState ToBlendAttachment(in BlendStateDesc blend)
    {
        var mask = blend.RenderTargetWriteMask;
        var writeMask = ColorComponentFlags.None;

        if ((mask & 1) != 0)
        {
            writeMask |= ColorComponentFlags.RBit;
        }

        if ((mask & 2) != 0)
        {
            writeMask |= ColorComponentFlags.GBit;
        }

        if ((mask & 4) != 0)
        {
            writeMask |= ColorComponentFlags.BBit;
        }

        if ((mask & 8) != 0)
        {
            writeMask |= ColorComponentFlags.ABit;
        }

        var source = ToVk(blend.SrcBlend);
        var destination = ToVk(blend.DstBlend);

        return new PipelineColorBlendAttachmentState
        {
            BlendEnable = blend.BlendEnable,
            SrcColorBlendFactor = source,
            DstColorBlendFactor = destination,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = source,
            DstAlphaBlendFactor = destination,
            AlphaBlendOp = BlendOp.Add,
            ColorWriteMask = writeMask,
        };
    }

    /// <summary>Builds the multisample state.</summary>
    /// <param name="blend">The blend descriptor, read only for its alpha-to-coverage flag.</param>
    /// <param name="sampleCount">The sample count the render pass uses.</param>
    /// <returns>The state.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sampleCount"/> is not a legal sample count.</exception>
    public static PipelineMultisampleStateCreateInfo ToMultisampleState(in BlendStateDesc blend, int sampleCount)
        => new()
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = ToSampleCount(sampleCount),
            SampleShadingEnable = false,
            MinSampleShading = 1f,
            AlphaToCoverageEnable = blend.AlphaToCoverageEnable,
            AlphaToOneEnable = false,
        };
}
