using Silk.NET.Vulkan;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// The Vulkan expansion of a <see cref="ResourceState"/> for an image: the layout it must be in, and
/// the stage and access masks a barrier reaching or leaving that state names.
/// </summary>
/// <param name="Layout">The image layout the state corresponds to.</param>
/// <param name="Stages">The pipeline stages that touch the image in this state.</param>
/// <param name="Access">The access types those stages perform.</param>
public readonly record struct VulkanImageState(ImageLayout Layout, PipelineStageFlags2 Stages, AccessFlags2 Access);

/// <summary>
/// The Vulkan expansion of a <see cref="ResourceState"/> for a buffer. Buffers have no layout, so only
/// the stage and access masks matter.
/// </summary>
/// <param name="Stages">The pipeline stages that touch the buffer in this state.</param>
/// <param name="Access">The access types those stages perform.</param>
public readonly record struct VulkanBufferState(PipelineStageFlags2 Stages, AccessFlags2 Access);

/// <summary>
/// Expands the contract's <see cref="ResourceState"/> enum into the <c>VkImageLayout</c>,
/// <c>VkPipelineStageFlags2</c> and <c>VkAccessFlags2</c> triple a synchronization2 barrier is built
/// from.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ResourceState"/>'s own remarks promise exactly this: a state-based model the caller
/// cannot get subtly wrong, which the Vulkan backend expands and the OpenGL backend largely ignores.
/// This is that expansion, and it is the single place it happens &#8212; a barrier assembled anywhere
/// else in the backend would be a second, divergent opinion about what <see cref="ResourceState"/>
/// means.
/// </para>
/// <para>
/// The tables are total over the states that are legal for each resource kind and throw for the rest.
/// Half of <see cref="ResourceState"/> is meaningless on an image (there is no such thing as an image
/// bound as an index buffer) and half is meaningless on a buffer, and silently mapping a nonsensical
/// pair onto some neutral default is how a missing barrier becomes invisible. Naming the mistake is
/// worth more than tolerating it.
/// </para>
/// </remarks>
public static class VulkanResourceStates
{
    // Shader reads can come from any stage the renderer runs. Naming all three rather than trying to
    // infer which one is deliberate: over-synchronizing a transition costs a little, while missing the
    // stage that actually reads is the race the contract's barrier section warns about.
    private const PipelineStageFlags2 ShaderStages =
        PipelineStageFlags2.VertexShaderBit
        | PipelineStageFlags2.FragmentShaderBit
        | PipelineStageFlags2.ComputeShaderBit;

    private const PipelineStageFlags2 DepthStages =
        PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;

    /// <summary>Expands a state for an image.</summary>
    /// <param name="state">The state to expand.</param>
    /// <param name="isDepth">Whether the image carries a depth aspect, which selects the depth-stencil
    /// read layout over the colour one for <see cref="ResourceState.ShaderRead"/>.</param>
    /// <returns>The layout, stages and access the state corresponds to.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The state is not one an image can be in.</exception>
    public static VulkanImageState ForImage(ResourceState state, bool isDepth = false) => state switch
    {
        // The only legal source state for a transition out of "contents undefined". No stage waits on
        // it and no access is made visible, because nothing has happened to the image yet.
        ResourceState.Undefined => new(ImageLayout.Undefined, PipelineStageFlags2.None, AccessFlags2.None),

        ResourceState.ShaderRead => new(
            isDepth ? ImageLayout.DepthStencilReadOnlyOptimal : ImageLayout.ShaderReadOnlyOptimal,
            ShaderStages,
            AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit),

        // A storage image is readable and writable only in General. There is no write-only image
        // layout, so ShaderWrite and ShaderReadWrite differ in access mask alone.
        ResourceState.ShaderWrite => new(
            ImageLayout.General,
            ShaderStages,
            AccessFlags2.ShaderStorageWriteBit),

        ResourceState.ShaderReadWrite => new(
            ImageLayout.General,
            ShaderStages,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit),

        ResourceState.ColorTarget => new(
            ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit),

        ResourceState.DepthWrite => new(
            ImageLayout.DepthStencilAttachmentOptimal,
            DepthStages,
            AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit),

        // Read-only depth is the state that permits sampling the attachment while it is still bound,
        // which is what DepthAttachmentDesc.ReadOnly is for, so it names the fragment stage too.
        ResourceState.DepthRead => new(
            ImageLayout.DepthStencilReadOnlyOptimal,
            DepthStages | PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.ShaderSampledReadBit),

        ResourceState.CopySource => new(
            ImageLayout.TransferSrcOptimal,
            PipelineStageFlags2.AllTransferBit,
            AccessFlags2.TransferReadBit),

        ResourceState.CopyDestination => new(
            ImageLayout.TransferDstOptimal,
            PipelineStageFlags2.AllTransferBit,
            AccessFlags2.TransferWriteBit),

        // Handing an image to the presentation engine is synchronized by the present semaphore, not by
        // the barrier, so the barrier names no destination stage or access. Adding one here would be a
        // validation error, not extra safety.
        ResourceState.Present => new(ImageLayout.PresentSrcKhr, PipelineStageFlags2.None, AccessFlags2.None),

        ResourceState.IndirectArgument or ResourceState.IndexBuffer or ResourceState.VertexBuffer
            => throw new ArgumentOutOfRangeException(nameof(state), state, "This is a buffer state; an image can never be in it."),

        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown resource state."),
    };

    /// <summary>Expands a state for a buffer.</summary>
    /// <param name="state">The state to expand.</param>
    /// <returns>The stages and access the state corresponds to.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The state is not one a buffer can be in.</exception>
    public static VulkanBufferState ForBuffer(ResourceState state) => state switch
    {
        ResourceState.Undefined => new(PipelineStageFlags2.None, AccessFlags2.None),

        ResourceState.ShaderRead => new(
            ShaderStages,
            AccessFlags2.UniformReadBit | AccessFlags2.ShaderStorageReadBit),

        ResourceState.ShaderWrite => new(ShaderStages, AccessFlags2.ShaderStorageWriteBit),

        ResourceState.ShaderReadWrite => new(
            ShaderStages,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit),

        ResourceState.IndirectArgument => new(
            PipelineStageFlags2.DrawIndirectBit,
            AccessFlags2.IndirectCommandReadBit),

        ResourceState.IndexBuffer => new(PipelineStageFlags2.IndexInputBit, AccessFlags2.IndexReadBit),

        ResourceState.VertexBuffer => new(
            PipelineStageFlags2.VertexAttributeInputBit,
            AccessFlags2.VertexAttributeReadBit),

        ResourceState.CopySource => new(PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit),

        ResourceState.CopyDestination => new(PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit),

        ResourceState.ColorTarget or ResourceState.DepthWrite or ResourceState.DepthRead or ResourceState.Present
            => throw new ArgumentOutOfRangeException(nameof(state), state, "This is an image state; a buffer can never be in it."),

        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown resource state."),
    };
}
