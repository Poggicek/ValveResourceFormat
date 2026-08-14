using System.Collections.Immutable;
using ValveResourceFormat.Renderer.RHI;

namespace ValveResourceFormat.Renderer.Shaders.Spirv;

/// <summary>What a descriptor binding actually holds, which is what decides its Vulkan descriptor type.</summary>
public enum SpirvResourceKind
{
    /// <summary>The kind could not be determined from the module.</summary>
    Unknown,

    /// <summary>A uniform buffer block.</summary>
    UniformBuffer,

    /// <summary>A shader storage buffer block.</summary>
    StorageBuffer,

    /// <summary>A texture and sampler declared as one, which is what a GLSL <c>sampler2D</c> becomes.</summary>
    CombinedImageSampler,

    /// <summary>A texture with no sampler attached.</summary>
    SampledImage,

    /// <summary>A read/write image.</summary>
    StorageImage,

    /// <summary>A standalone sampler.</summary>
    Sampler,

    /// <summary>A read only texel buffer.</summary>
    UniformTexelBuffer,

    /// <summary>A read/write texel buffer.</summary>
    StorageTexelBuffer,

    /// <summary>A subpass input.</summary>
    InputAttachment,
}

/// <summary>The scalar type of a shader interface variable's components.</summary>
public enum SpirvComponentType
{
    /// <summary>Not a scalar, vector or matrix of a recognised component type.</summary>
    Unknown,

    /// <summary>32 bit float.</summary>
    Float,

    /// <summary>64 bit float.</summary>
    Double,

    /// <summary>Signed integer.</summary>
    Int,

    /// <summary>Unsigned integer.</summary>
    UInt,

    /// <summary>Boolean.</summary>
    Bool,
}

/// <summary>One descriptor the shader declares.</summary>
/// <param name="Name">The declared name, or the block's type name for a buffer block.</param>
/// <param name="Set">The descriptor set. Must match the scheme in the RHI contract; see
/// <see cref="DescriptorSets"/>.</param>
/// <param name="Binding">The binding number within the set.</param>
/// <param name="Kind">What the binding holds.</param>
/// <param name="Count">Array length, 1 for a plain declaration, 0 for a runtime sized array.</param>
/// <param name="BlockSizeInBytes">Size of the backing block for a buffer, otherwise 0. A storage
/// buffer ending in a runtime array reports only its fixed prefix.</param>
public readonly record struct SpirvDescriptorBinding(
    string Name,
    int Set,
    int Binding,
    SpirvResourceKind Kind,
    int Count,
    int BlockSizeInBytes);

/// <summary>One vertex shader input.</summary>
/// <param name="Name">The declared attribute name.</param>
/// <param name="Location">The location decoration. Allocated by <see cref="VertexAttributeLocations"/>,
/// not chosen ad hoc.</param>
/// <param name="ComponentType">The scalar type the shader reads it as.</param>
/// <param name="ComponentCount">Components per location, 1 to 4.</param>
/// <param name="LocationCount">Consecutive locations consumed. Greater than one only for a matrix
/// attribute, which takes one location per column.</param>
/// <remarks>
/// This is the shader side of the interface only. It does not say how the vertex buffer stores the
/// attribute: a <c>vec4</c> input is routinely fed by an <see cref="RhiFormat.R8G8B8A8_UNorm"/> buffer.
/// The buffer format belongs to <see cref="VertexInputLayout"/>.
/// </remarks>
public readonly record struct SpirvVertexInput(
    string Name,
    int Location,
    SpirvComponentType ComponentType,
    int ComponentCount,
    int LocationCount);

/// <summary>
/// What a SPIR-V module declares: its descriptors, its push constant block and its vertex inputs.
/// </summary>
public sealed class SpirvReflectionResult
{
    /// <summary>Gets the stage the module's entry point is for.</summary>
    public ShaderStage Stage { get; init; }

    /// <summary>Gets the entry point name.</summary>
    public string EntryPoint { get; init; } = "main";

    /// <summary>Gets every descriptor the module declares, ordered by set then binding.</summary>
    public ImmutableArray<SpirvDescriptorBinding> DescriptorBindings { get; init; } = [];

    /// <summary>
    /// Gets the size of the push constant block in bytes, or 0 when the module declares none.
    /// </summary>
    /// <remarks>The renderer's per-draw block is 92 bytes. Check
    /// <see cref="IDeviceLimits.MaxPushConstantSize"/> before growing it.</remarks>
    public int PushConstantSizeInBytes { get; init; }

    /// <summary>Gets the vertex inputs, ordered by location. Empty outside a vertex module.</summary>
    public ImmutableArray<SpirvVertexInput> VertexInputs { get; init; } = [];

    /// <summary>
    /// Gets the declared workgroup size of a compute module, or <see langword="null"/> for a graphics
    /// module or when the size is specialisation constant driven.
    /// </summary>
    public (int X, int Y, int Z)? WorkgroupSize { get; init; }
}
