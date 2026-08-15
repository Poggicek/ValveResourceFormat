using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.Shaders.Spirv;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Descriptors;

/// <summary>
/// One binding inside a descriptor set layout, reduced to the four things that decide whether two
/// layouts are the same object.
/// </summary>
/// <param name="Binding">The binding number within the set. A <see cref="Buffers.ReservedBufferSlots"/>
/// value in sets 0 and 1, a <see cref="Materials.ReservedTextureSlots"/> value in set 2, and the
/// material's own numbering in set 3.</param>
/// <param name="Type">What the binding holds.</param>
/// <param name="Count">Array length. 1 for a plain declaration.</param>
/// <param name="Stages">The stages that may read it.</param>
/// <remarks>
/// Deliberately carries no name. <see cref="VulkanDescriptorLayoutCache"/> hands out one
/// <c>VkDescriptorSetLayout</c> per distinct binding table, and two shaders that declare the same table
/// under different names must share it: pipeline layout compatibility is decided by the layout object,
/// so letting a name split the cache would silently force a redundant rebind of every following set.
/// </remarks>
public readonly record struct VulkanDescriptorBinding(
    int Binding,
    DescriptorType Type,
    int Count,
    ShaderStageFlags Stages)
{
    /// <summary>Converts this binding to the Vulkan structure.</summary>
    /// <returns>The equivalent <see cref="DescriptorSetLayoutBinding"/>.</returns>
    public DescriptorSetLayoutBinding ToVulkan() => new()
    {
        Binding = (uint)Binding,
        DescriptorType = Type,
        DescriptorCount = (uint)Count,
        StageFlags = Stages,
        PImmutableSamplers = null,
    };
}

/// <summary>
/// Translations between the contract's vocabulary, SPIR-V reflection's vocabulary and Vulkan's.
/// </summary>
public static class VulkanDescriptorTypes
{
    /// <summary>
    /// Gets the number of uniform buffer bindings the canonical set 0 layout declares.
    /// </summary>
    /// <remarks><see cref="Buffers.ReservedBufferSlots.Max"/> is the OpenGL 4.6 guaranteed minimum and
    /// the ceiling <see cref="SpirvReflection.ValidateDescriptorSets"/> already enforces, so the set is
    /// exactly that wide.</remarks>
    public static int UniformBufferSlotCount => (int)Buffers.ReservedBufferSlots.Max;

    /// <summary>
    /// Gets the number of storage buffer bindings the canonical set 1 layout declares.
    /// </summary>
    /// <remarks>Derived from the last member of the SSBO range rather than written out, so adding a
    /// slot to the enum widens the layout without anyone remembering to.</remarks>
    public static int StorageBufferSlotCount => (int)Buffers.ReservedBufferSlots.CullPlanes + 1;

    /// <summary>
    /// Gets the number of global texture bindings the canonical set 2 layout declares.
    /// </summary>
    public static int ReservedTextureSlotCount => (int)Materials.ReservedTextureSlots.Last + 1;

    /// <summary>Maps a reflected resource kind to its Vulkan descriptor type.</summary>
    /// <param name="kind">What the shader declared.</param>
    /// <returns>The descriptor type to put in the layout.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The kind is <see cref="SpirvResourceKind.Unknown"/>,
    /// which means the reflector could not classify the declaration and guessing would bind the wrong
    /// resource silently.</exception>
    public static DescriptorType ToDescriptorType(SpirvResourceKind kind) => kind switch
    {
        SpirvResourceKind.UniformBuffer => DescriptorType.UniformBuffer,
        SpirvResourceKind.StorageBuffer => DescriptorType.StorageBuffer,
        SpirvResourceKind.CombinedImageSampler => DescriptorType.CombinedImageSampler,
        SpirvResourceKind.SampledImage => DescriptorType.SampledImage,
        SpirvResourceKind.StorageImage => DescriptorType.StorageImage,
        SpirvResourceKind.Sampler => DescriptorType.Sampler,
        SpirvResourceKind.UniformTexelBuffer => DescriptorType.UniformTexelBuffer,
        SpirvResourceKind.StorageTexelBuffer => DescriptorType.StorageTexelBuffer,
        SpirvResourceKind.InputAttachment => DescriptorType.InputAttachment,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
            "The reflector could not classify this declaration, so no descriptor type can be chosen for it."),
    };

    /// <summary>Maps contract stages to Vulkan stage flags.</summary>
    /// <param name="stages">The stages.</param>
    /// <returns>The equivalent flags.</returns>
    public static ShaderStageFlags ToStageFlags(ShaderStage stages)
    {
        var flags = ShaderStageFlags.None;

        if (stages.HasFlag(ShaderStage.Vertex))
        {
            flags |= ShaderStageFlags.VertexBit;
        }

        if (stages.HasFlag(ShaderStage.Fragment))
        {
            flags |= ShaderStageFlags.FragmentBit;
        }

        if (stages.HasFlag(ShaderStage.Compute))
        {
            flags |= ShaderStageFlags.ComputeBit;
        }

        return flags;
    }

    /// <summary>
    /// Gets the stage mask the shared layouts declare: every stage the renderer compiles for.
    /// </summary>
    /// <remarks>
    /// A shared layout cannot narrow its stages to the shader that happened to create it, because the
    /// next shader to use the same binding table may read the same slot from a different stage. Widening
    /// costs nothing at runtime; narrowing wrongly is a validation error at draw time.
    /// </remarks>
    public static ShaderStageFlags AllStages =>
        ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit;

    /// <summary>Whether a descriptor of this type is written through a <see cref="DescriptorImageInfo"/>.</summary>
    /// <param name="type">The descriptor type.</param>
    /// <returns><see langword="true"/> when the write carries image info.</returns>
    public static bool IsImage(DescriptorType type) => type
        is DescriptorType.Sampler
        or DescriptorType.CombinedImageSampler
        or DescriptorType.SampledImage
        or DescriptorType.StorageImage
        or DescriptorType.InputAttachment;

    /// <summary>Whether a descriptor of this type is written through a <see cref="DescriptorBufferInfo"/>.</summary>
    /// <param name="type">The descriptor type.</param>
    /// <returns><see langword="true"/> when the write carries buffer info.</returns>
    public static bool IsBuffer(DescriptorType type) => type
        is DescriptorType.UniformBuffer
        or DescriptorType.StorageBuffer
        or DescriptorType.UniformBufferDynamic
        or DescriptorType.StorageBufferDynamic;

    /// <summary>Whether a descriptor of this type is written through a buffer view.</summary>
    /// <param name="type">The descriptor type.</param>
    /// <returns><see langword="true"/> when the write carries a texel buffer view.</returns>
    /// <remarks>No renderer shader declares one. <see cref="VulkanDescriptorWriter"/> refuses them by
    /// name rather than writing a null view that validation would report far from the cause.</remarks>
    public static bool IsTexelBuffer(DescriptorType type) => type
        is DescriptorType.UniformTexelBuffer
        or DescriptorType.StorageTexelBuffer;
}
