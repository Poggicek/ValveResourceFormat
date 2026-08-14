using System.Runtime.InteropServices;

namespace ValveResourceFormat.Renderer.RHI;

/// <summary>
/// The descriptor set partitioning every backend and every shader agrees on.
/// </summary>
/// <remarks>
/// <para>
/// The binding numbers inside each set are the existing
/// <see cref="Buffers.ReservedBufferSlots"/> and <see cref="Materials.ReservedTextureSlots"/> values,
/// unchanged. This scheme was chosen to preserve them exactly rather than renumber 120 shaders.
/// </para>
/// <para>
/// It also resolves a real ambiguity in the current model. <c>ReservedBufferSlots</c> deliberately
/// overlaps its uniform and storage index spaces &#8212; both start at zero, which is why the file
/// suppresses CA1069. OpenGL keeps those namespaces separate per target; Vulkan does not. Putting
/// uniform buffers in set 0 and storage buffers in set 1 keeps both numbering schemes intact and
/// makes the collision impossible.
/// </para>
/// </remarks>
public static class DescriptorSets
{
    /// <summary>Uniform buffers, bound at their <see cref="Buffers.ReservedBufferSlots"/> value.</summary>
    public const int UniformBuffers = 0;

    /// <summary>Storage buffers, bound at their <see cref="Buffers.ReservedBufferSlots"/> value.</summary>
    public const int StorageBuffers = 1;

    /// <summary>Global textures, bound at their <see cref="Materials.ReservedTextureSlots"/> value.</summary>
    public const int ReservedTextures = 2;

    /// <summary>Per-material textures, bound at the slot the material assigns.</summary>
    public const int MaterialTextures = 3;

    /// <summary>The number of sets a pipeline layout declares.</summary>
    public const int Count = 4;
}

/// <summary>Creation parameters for a buffer.</summary>
/// <param name="SizeInBytes">Size in bytes. Must be greater than zero.</param>
/// <param name="Usage">Every use the buffer will be put to.</param>
/// <param name="Memory">Where the memory lives.</param>
/// <param name="Name">Debug name, surfaced to graphics debuggers.</param>
public readonly record struct BufferDesc(int SizeInBytes, BufferUsage Usage, BufferMemory Memory, string Name);

/// <summary>Creation parameters for a texture.</summary>
/// <param name="Width">Width in texels of mip level zero.</param>
/// <param name="Height">Height in texels of mip level zero.</param>
/// <param name="Format">Pixel format.</param>
/// <param name="Usage">Every use the texture will be put to.</param>
/// <param name="Name">Debug name, surfaced to graphics debuggers.</param>
/// <param name="Depth">Volume depth or array layer count. Defaults to 1.</param>
/// <param name="MipLevels">Mip level count. Defaults to 1.</param>
/// <param name="SampleCount">Sample count. 1 means not multisampled.</param>
/// <param name="Dimension">Texture shape.</param>
public readonly record struct TextureDesc(
    int Width,
    int Height,
    RhiFormat Format,
    TextureUsage Usage,
    string Name,
    int Depth = 1,
    int MipLevels = 1,
    int SampleCount = 1,
    TextureDimension Dimension = TextureDimension.Texture2D);

/// <summary>Creation parameters for a sampler.</summary>
/// <param name="MinFilter">Minification filter.</param>
/// <param name="MagFilter">Magnification filter.</param>
/// <param name="MipFilter">Filtering between mip levels.</param>
/// <param name="AddressU">Wrapping on the U axis.</param>
/// <param name="AddressV">Wrapping on the V axis.</param>
/// <param name="AddressW">Wrapping on the W axis.</param>
/// <param name="MaxAnisotropy">Maximum anisotropy. 1 disables anisotropic filtering.</param>
/// <param name="CompareOp">Comparison for a shadow sampler, or <see langword="null"/> for a normal sampler.</param>
public readonly record struct SamplerDesc(
    FilterMode MinFilter = FilterMode.Linear,
    FilterMode MagFilter = FilterMode.Linear,
    MipFilterMode MipFilter = MipFilterMode.Linear,
    AddressMode AddressU = AddressMode.Repeat,
    AddressMode AddressV = AddressMode.Repeat,
    AddressMode AddressW = AddressMode.Repeat,
    float MaxAnisotropy = 1f,
    Comparison? CompareOp = null);

/// <summary>One vertex shader input.</summary>
/// <param name="Location">The shader input location. Allocated by
/// <see cref="VertexAttributeLocations"/>, not chosen ad hoc.</param>
/// <param name="Format">Element format in the buffer.</param>
/// <param name="OffsetInBytes">Byte offset within the vertex.</param>
/// <param name="Binding">Which bound vertex buffer supplies it.</param>
public readonly record struct VertexAttributeDesc(int Location, RhiFormat Format, int OffsetInBytes, int Binding = 0);

/// <summary>One bound vertex buffer's stride and step rate.</summary>
/// <param name="Binding">The binding index this describes.</param>
/// <param name="StrideInBytes">Bytes between consecutive elements.</param>
/// <param name="PerInstance">Whether the attribute advances per instance rather than per vertex.</param>
public readonly record struct VertexBindingDesc(int Binding, int StrideInBytes, bool PerInstance = false);

/// <summary>The complete vertex input state of a pipeline. Built from the existing
/// <see cref="VertexInputLayout"/>, which already carries formats and canonical locations.</summary>
/// <param name="Attributes">The vertex inputs.</param>
/// <param name="Bindings">The buffer bindings the attributes draw from.</param>
public readonly record struct VertexInputDesc(VertexAttributeDesc[] Attributes, VertexBindingDesc[] Bindings);

/// <summary>A range of push constants visible to a set of stages.</summary>
/// <param name="OffsetInBytes">Byte offset within the push constant block.</param>
/// <param name="SizeInBytes">Size of the range in bytes.</param>
/// <param name="Stages">Stages that can read it.</param>
/// <remarks>The renderer's per-draw block is 92 bytes, inside the 128 byte floor every Vulkan
/// implementation guarantees. See <see cref="IDeviceLimits.MaxPushConstantSize"/> before growing it.</remarks>
public readonly record struct PushConstantRange(int OffsetInBytes, int SizeInBytes, ShaderStage Stages);

/// <summary>One colour attachment of a render pass.</summary>
/// <param name="Texture">The target texture or view.</param>
/// <param name="LoadOp">What happens to its contents when the pass begins.</param>
/// <param name="StoreOp">What happens to its contents when the pass ends.</param>
/// <param name="ClearColor">The clear value, used when <paramref name="LoadOp"/> is <see cref="RHI.LoadOp.Clear"/>.</param>
/// <param name="MipLevel">Which mip level to render into.</param>
/// <param name="ArrayLayer">Which array layer or cube face to render into.</param>
public readonly record struct ColorAttachmentDesc(
    ITexture Texture,
    LoadOp LoadOp,
    StoreOp StoreOp,
    Vector4 ClearColor = default,
    int MipLevel = 0,
    int ArrayLayer = 0);

/// <summary>The depth-stencil attachment of a render pass.</summary>
/// <param name="Texture">The target texture or view.</param>
/// <param name="DepthLoadOp">What happens to depth when the pass begins.</param>
/// <param name="DepthStoreOp">What happens to depth when the pass ends.</param>
/// <param name="ClearDepth">The depth clear value. The renderer is reverse-Z, so the far plane is 0.</param>
/// <param name="StencilLoadOp">What happens to stencil when the pass begins.</param>
/// <param name="StencilStoreOp">What happens to stencil when the pass ends.</param>
/// <param name="ClearStencil">The stencil clear value.</param>
/// <param name="ReadOnly">Whether the attachment is bound read-only, permitting it to be sampled in the same pass.</param>
/// <param name="MipLevel">Which mip level to render into.</param>
/// <param name="ArrayLayer">Which array layer to render into.</param>
public readonly record struct DepthAttachmentDesc(
    ITexture Texture,
    LoadOp DepthLoadOp,
    StoreOp DepthStoreOp,
    float ClearDepth = 0f,
    LoadOp StencilLoadOp = LoadOp.DontCare,
    StoreOp StencilStoreOp = StoreOp.DontCare,
    byte ClearStencil = 0,
    bool ReadOnly = false,
    int MipLevel = 0,
    int ArrayLayer = 0);

/// <summary>
/// Everything a render pass needs. Shaped for dynamic rendering: there is no render pass or
/// framebuffer object to create and cache, only attachments and their load and store operations.
/// </summary>
/// <param name="ColorAttachments">Colour attachments in attachment order. May be empty for a depth-only pass.</param>
/// <param name="DepthAttachment">The depth-stencil attachment, or <see langword="null"/> for none.</param>
/// <param name="Name">Debug label, opening a scope in graphics debuggers.</param>
public readonly record struct RenderPassDesc(
    ColorAttachmentDesc[] ColorAttachments,
    DepthAttachmentDesc? DepthAttachment,
    string Name);

/// <summary>
/// Creation parameters for a graphics pipeline. Holds the existing <see cref="RenderState"/> whole,
/// so the fixed-function state the renderer already models does not need restating here.
/// </summary>
public sealed record GraphicsPipelineDesc
{
    /// <summary>Gets the vertex stage module.</summary>
    public required IShaderModule VertexShader { get; init; }

    /// <summary>Gets the fragment stage module, or <see langword="null"/> for a depth-only pipeline.</summary>
    public IShaderModule? FragmentShader { get; init; }

    /// <summary>Gets the vertex input state.</summary>
    public required VertexInputDesc VertexInput { get; init; }

    /// <summary>Gets the rasterizer, depth-stencil and blend state.</summary>
    public required RenderState RenderState { get; init; }

    /// <summary>Gets the primitive topology.</summary>
    public PrimitiveTopology Topology { get; init; } = PrimitiveTopology.TriangleList;

    /// <summary>Gets the colour attachment formats this pipeline renders into. Required by dynamic
    /// rendering, which has no render pass object to infer them from; must match the
    /// <see cref="RenderPassDesc"/> the pipeline is used inside.</summary>
    public required RhiFormat[] ColorFormats { get; init; }

    /// <summary>Gets the depth attachment format, or <see cref="RhiFormat.Undefined"/> for none.</summary>
    public RhiFormat DepthFormat { get; init; } = RhiFormat.Undefined;

    /// <summary>Gets the sample count. Must match the render pass.</summary>
    public int SampleCount { get; init; } = 1;

    /// <summary>Gets the push constant range, or <see langword="null"/> when the pipeline uses none.</summary>
    public PushConstantRange? PushConstants { get; init; }

    /// <summary>Gets the debug name.</summary>
    public required string Name { get; init; }
}

/// <summary>Creation parameters for a compute pipeline.</summary>
/// <param name="ComputeShader">The compute stage module.</param>
/// <param name="Name">Debug name.</param>
/// <param name="PushConstants">The push constant range, or <see langword="null"/> for none.</param>
public readonly record struct ComputePipelineDesc(IShaderModule ComputeShader, string Name, PushConstantRange? PushConstants = null);

/// <summary>
/// The cache key for a graphics pipeline: packed, padding-free, and containing no references, so its
/// raw bytes are its exact bit image and can be hashed or compared with a memory compare.
/// </summary>
/// <remarks>
/// This is deliberately separate from <see cref="GraphicsPipelineDesc"/>, which holds shader module
/// references and arrays and therefore cannot be POD. <see cref="RenderState"/> is already
/// <c>Pack = 1</c> POD for exactly this reason; its own remarks anticipate this use.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public record struct PipelineCacheKey
{
    /// <summary>The rasterizer, depth-stencil and blend state.</summary>
    public RenderState RenderState;

    /// <summary>Content hash of the vertex stage module.</summary>
    public ulong VertexShaderHash;

    /// <summary>Content hash of the fragment stage module, or zero when there is none.</summary>
    public ulong FragmentShaderHash;

    /// <summary>Hash of the vertex input layout.</summary>
    public ulong VertexInputHash;

    /// <summary>Hash of the colour and depth attachment formats.</summary>
    public ulong RenderTargetHash;

    /// <summary>The primitive topology.</summary>
    public PrimitiveTopology Topology;

    /// <summary>The sample count.</summary>
    public byte SampleCount;
}
