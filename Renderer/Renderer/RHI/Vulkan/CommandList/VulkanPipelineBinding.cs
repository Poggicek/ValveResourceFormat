using Silk.NET.Vulkan;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// What <see cref="VulkanCommandList"/> needs from a pipeline object in order to bind it, and the
/// seam the pipeline layer implements on its <see cref="IGraphicsPipeline"/> and
/// <see cref="IComputePipeline"/> types.
/// </summary>
/// <remarks>
/// <para>
/// The contract's pipeline interfaces carry no Vulkan handles, deliberately, so a command list cannot
/// reach a <c>VkPipeline</c> through them. This interface is that reach, kept as small as recording
/// actually requires: the pipeline object, the layout push constants and descriptor sets are written
/// against, and which bind point the two belong to.
/// </para>
/// <para>
/// <b>Viewport and scissor must be dynamic state on every graphics pipeline.</b>
/// <see cref="ICommandList.BeginRenderPass"/> is specified to set both to the attachment extent, and
/// <see cref="ICommandList.SetViewport"/> may move them afterwards &#8212; the barn light atlas does
/// exactly that, once per shadow caster, inside a single pass. A pipeline that bakes them instead
/// cannot honour either call, so <c>VK_DYNAMIC_STATE_VIEWPORT</c> and <c>VK_DYNAMIC_STATE_SCISSOR</c>
/// belong in every <c>VkPipelineDynamicStateCreateInfo</c> this backend creates.
/// </para>
/// </remarks>
public interface IVulkanPipeline
{
    /// <summary>Gets the pipeline handle to bind.</summary>
    Pipeline Handle { get; }

    /// <summary>Gets the layout push constants and descriptor sets are written against.</summary>
    PipelineLayout Layout { get; }

    /// <summary>Gets which bind point the pipeline and its descriptor sets belong to.</summary>
    PipelineBindPoint BindPoint { get; }

    /// <summary>Gets the push constant range the pipeline declares, or <see langword="null"/> when it
    /// uses none. Its <see cref="PushConstantRange.Stages"/> is what
    /// <see cref="ICommandList.SetPushConstants{T}"/> names in <c>vkCmdPushConstants</c>.</summary>
    PushConstantRange? PushConstants { get; }

    /// <summary>
    /// Gets the descriptor sets this pipeline's shaders declare, as one bit per set index.
    /// </summary>
    /// <remarks>
    /// Precomputed by <see cref="Descriptors.VulkanDescriptorSetUsage.MaskFor(IReadOnlyList{Shaders.Spirv.SpirvReflectionResult})"/>
    /// when the pipeline is built, so recording a draw costs an <c>and</c> rather than a walk over
    /// reflection. It lives on the pipeline and not on <see cref="VulkanPipelineLayout"/> because layouts
    /// are shared: every layout declares all <see cref="DescriptorSets.Count"/> sets, so two shaders
    /// touching different subsets land on one layout object and the layout cannot tell them apart.
    /// </remarks>
    int UsedDescriptorSets { get; }

    /// <summary>
    /// Gets the vertex buffer bindings this pipeline fetches from, as one bit per binding index, or zero
    /// for a compute pipeline and for a graphics pipeline that generates its vertices.
    /// </summary>
    /// <remarks>
    /// The neighbouring hazard to <see cref="UsedDescriptorSets"/>, checked at the same seam: a draw whose
    /// pipeline declares a vertex binding nothing filled fetches from a null buffer, which is undefined the
    /// same way and fails the same way. Bindings at or past 32 are left out; the Vulkan floor for
    /// <c>maxVertexInputBindings</c> is 16 and nothing here comes close.
    /// </remarks>
    uint UsedVertexBindings { get; }
}

/// <summary>
/// Accumulates the descriptor bindings a draw or dispatch needs and writes them to the command buffer,
/// so that <see cref="VulkanCommandList"/> holds no descriptor pool, layout or set of its own.
/// </summary>
/// <remarks>
/// <para>
/// The contract's binding calls are immediate-mode &#8212; bind a texture, bind a buffer, draw &#8212;
/// which is OpenGL's model, not Vulkan's. Something has to turn a sequence of those into descriptor
/// sets, and it needs the set layouts the pipeline layer built from the contract's descriptor set table.
/// That belongs to the descriptor layer, so the command list calls out to this interface and the
/// strategy behind it (write-time sets, push descriptors, a cache keyed on the binding state) stays a
/// decision that layer makes.
/// </para>
/// <para>
/// Handles rather than the backend's wrapper types are passed deliberately: the command list has
/// already resolved the layout an image must be described in and the range a buffer binding covers,
/// and repeating that resolution on the other side of the seam would be a second opinion about it.
/// </para>
/// <para>
/// <see cref="Flush"/> is called immediately before every draw and dispatch, with the bound pipeline's
/// bind point and layout. A binder that has seen no change since the last flush should do nothing.
/// </para>
/// </remarks>
public interface IVulkanDescriptorBinder
{
    /// <summary>Drops every accumulated binding, at the start of a command list.</summary>
    /// <param name="commandBuffer">The command buffer about to be recorded into.</param>
    void Reset(CommandBuffer commandBuffer);

    /// <summary>Binds a range of a buffer into <see cref="DescriptorSets.UniformBuffers"/>.</summary>
    /// <param name="binding">The slot within the set.</param>
    /// <param name="buffer">The buffer.</param>
    /// <param name="offsetInBytes">Start of the bound range.</param>
    /// <param name="sizeInBytes">Length of the bound range.</param>
    void BindUniformBuffer(int binding, Silk.NET.Vulkan.Buffer buffer, ulong offsetInBytes, ulong sizeInBytes);

    /// <summary>Binds a range of a buffer into <see cref="DescriptorSets.StorageBuffers"/>.</summary>
    /// <param name="binding">The slot within the set.</param>
    /// <param name="buffer">The buffer.</param>
    /// <param name="offsetInBytes">Start of the bound range.</param>
    /// <param name="sizeInBytes">Length of the bound range.</param>
    void BindStorageBuffer(int binding, Silk.NET.Vulkan.Buffer buffer, ulong offsetInBytes, ulong sizeInBytes);

    /// <summary>Binds an image and its sampler into a texture set.</summary>
    /// <param name="descriptorSet">Either <see cref="DescriptorSets.ReservedTextures"/> or
    /// <see cref="DescriptorSets.MaterialTextures"/>.</param>
    /// <param name="binding">The slot within that set.</param>
    /// <param name="view">The image view to sample.</param>
    /// <param name="sampler">The sampler.</param>
    /// <param name="layout">The layout the image is in, already resolved from its tracked state.</param>
    void BindSampledImage(int descriptorSet, int binding, ImageView view, Sampler sampler, ImageLayout layout);

    /// <summary>Binds an image as a storage image into <see cref="DescriptorSets.ReservedTextures"/>.</summary>
    /// <param name="binding">The slot within the set.</param>
    /// <param name="view">The image view covering the mip level being written.</param>
    /// <param name="layout">The layout the image is in, which for a storage image is
    /// <see cref="ImageLayout.General"/>.</param>
    void BindStorageImage(int binding, ImageView view, ImageLayout layout);

    /// <summary>Writes everything accumulated so far to the command buffer.</summary>
    /// <param name="commandBuffer">The command buffer being recorded.</param>
    /// <param name="bindPoint">The bound pipeline's bind point.</param>
    /// <param name="layout">The bound pipeline's layout.</param>
    void Flush(CommandBuffer commandBuffer, PipelineBindPoint bindPoint, PipelineLayout layout);

    /// <summary>
    /// Gets the descriptor sets that are bound on the command buffer for the layout and bind point of the
    /// last <see cref="Flush"/>, as one bit per set index.
    /// </summary>
    /// <remarks>
    /// The other half of the guard <see cref="IVulkanPipeline.UsedDescriptorSets"/> is the first half of.
    /// Read immediately after a flush, when it is exactly the set of indices <c>vkCmdBindDescriptorSets</c>
    /// has covered: a flush binds every set it holds bindings for, and a layout or bind point change
    /// clears the lot before rebinding. Zero on a binder that has just been reset.
    /// </remarks>
    int BoundDescriptorSets { get; }
}
