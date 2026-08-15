using Silk.NET.Vulkan;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// Routes the contract's <see cref="BufferBarrier"/> and <see cref="TextureBarrier"/> onto the two
/// entry points the resource layer owns, and holds the reasoning for the barriers this backend inserts
/// on the caller's behalf.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here builds a barrier structure.</b> <see cref="VulkanTexture.TransitionTo"/> and
/// <see cref="VulkanBuffer.RecordBarrier"/> do, out of <see cref="VulkanResourceStates"/>, which is the
/// single opinion about what a <see cref="ResourceState"/> means. A second expansion assembled here
/// would drift from that one silently, and the class of bug it would produce &#8212; a stage or access
/// bit missing on one path only &#8212; reproduces on one vendor's driver and not another's. This file
/// therefore chooses <i>which</i> transitions happen and leaves <i>what</i> they contain alone.
/// </para>
/// <para>
/// <b>One call per resource, not one per batch.</b>
/// <see cref="ICommandList.Barrier(ReadOnlySpan{BufferBarrier}, ReadOnlySpan{TextureBarrier})"/> asks
/// callers to batch, and the contract's reason is that separate calls cost separate pipeline stalls.
/// Going through the tracked-state entry points costs one <c>vkCmdPipelineBarrier2</c> per resource
/// instead of one for the batch, and that price is paid knowingly: the alternative is this file
/// reimplementing image layout tracking to merge the barriers, which is the duplication above. The
/// batching still pays off inside <see cref="VulkanTexture.TransitionTo"/>, which merges a
/// mixed-state image's subresources into one call, and the loss is a stall count rather than a
/// correctness risk.
/// </para>
/// <para>
/// <b>A texture barrier covers the whole texture object.</b>
/// <see cref="TextureBarrier.BaseMipLevel"/> and <see cref="TextureBarrier.MipLevelCount"/> narrow a
/// transition on a backend that tracks layout per subresource from the barrier itself; here the image
/// tracks its own, and the object being transitioned is what selects the subresources. A narrower
/// range is therefore widened to the whole object rather than dropped. Widening is safe &#8212; a
/// layout transition preserves contents unless it comes <i>from</i>
/// <see cref="ResourceState.Undefined"/>, and the tracking table follows &#8212; and being too coarse
/// shows up later as a stall, while being too narrow is the race the contract warns about. Callers
/// that need a genuinely narrow transition should barrier a <see cref="ITexture.CreateView"/> of the
/// range, which is the object the tracking is keyed on.
/// </para>
/// </remarks>
public static class VulkanBarrierTranslation
{
    /// <summary>Records one buffer transition.</summary>
    /// <param name="commandBuffer">The command buffer to record into.</param>
    /// <param name="barrier">The transition.</param>
    /// <exception cref="ArgumentException"><see cref="BufferBarrier.Buffer"/> is not a
    /// <see cref="VulkanBuffer"/>.</exception>
    /// <remarks>Both states are named by the caller. A buffer has no layout for the resource layer to
    /// track, so unlike an image there is nothing it could supply on the caller's behalf.</remarks>
    public static void Record(CommandBuffer commandBuffer, in BufferBarrier barrier)
    {
        var buffer = AsVulkanBuffer(barrier.Buffer);

        if (barrier.Before == barrier.After)
        {
            return;
        }

        buffer.RecordBarrier(commandBuffer, barrier.Before, barrier.After);
    }

    /// <summary>Records one texture transition.</summary>
    /// <param name="commandBuffer">The command buffer to record into.</param>
    /// <param name="barrier">The transition.</param>
    /// <exception cref="ArgumentException"><see cref="TextureBarrier.Texture"/> is not a
    /// <see cref="VulkanTexture"/>.</exception>
    /// <remarks>
    /// <see cref="TextureBarrier.Before"/> is handed to <see cref="VulkanTexture.TransitionTo"/> as the
    /// caller's <i>belief</i> about the current state, which a debug build asserts against the tracked
    /// one; it is never the barrier's <c>oldLayout</c>. The belief is only checked when the barrier
    /// covers every mip level of the object, because a partial barrier says nothing about the levels it
    /// leaves out and asserting the whole object against it would fail on a texture that is legitimately
    /// in two states at once.
    /// </remarks>
    public static void Record(CommandBuffer commandBuffer, in TextureBarrier barrier)
    {
        var texture = AsVulkanTexture(barrier.Texture);
        var covers = barrier.BaseMipLevel == 0
            && (barrier.MipLevelCount < 0 || barrier.MipLevelCount >= texture.MipLevels);

        texture.TransitionTo(commandBuffer, barrier.After, covers ? barrier.Before : null);
    }

    /// <summary>Records a batch of transitions.</summary>
    /// <param name="commandBuffer">The command buffer to record into.</param>
    /// <param name="bufferBarriers">Buffer transitions.</param>
    /// <param name="textureBarriers">Texture transitions.</param>
    public static void Record(
        CommandBuffer commandBuffer,
        ReadOnlySpan<BufferBarrier> bufferBarriers,
        ReadOnlySpan<TextureBarrier> textureBarriers)
    {
        foreach (ref readonly var barrier in bufferBarriers)
        {
            Record(commandBuffer, in barrier);
        }

        foreach (ref readonly var barrier in textureBarriers)
        {
            Record(commandBuffer, in barrier);
        }
    }

    /// <summary>Gets the image layout a texture's tracked state puts it in.</summary>
    /// <param name="texture">The texture to inspect.</param>
    /// <returns>The layout every subresource of <paramref name="texture"/> is currently in.</returns>
    /// <remarks>What a descriptor must name for the image it points at. Read from the tracking table
    /// rather than inferred from the binding, so a descriptor cannot disagree with the image.</remarks>
    public static ImageLayout LayoutOf(VulkanTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);

        return VulkanResourceStates.ForImage(texture.StateOf(0, 0), RhiFormatInfo.IsDepth(texture.Format)).Layout;
    }

    /// <summary>Casts a contract buffer to this backend's, naming the mistake when it is not one.</summary>
    /// <param name="buffer">The buffer to cast.</param>
    /// <returns>The backend buffer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="buffer"/> came from another backend.</exception>
    public static VulkanBuffer AsVulkanBuffer(IBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return buffer as VulkanBuffer
            ?? throw new ArgumentException($"Expected a {nameof(VulkanBuffer)}, got {buffer.GetType().Name}.", nameof(buffer));
    }

    /// <summary>Casts a contract texture to this backend's, naming the mistake when it is not one.</summary>
    /// <param name="texture">The texture to cast.</param>
    /// <returns>The backend texture.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="texture"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="texture"/> came from another backend.</exception>
    public static VulkanTexture AsVulkanTexture(ITexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);

        return texture as VulkanTexture
            ?? throw new ArgumentException($"Expected a {nameof(VulkanTexture)}, got {texture.GetType().Name}.", nameof(texture));
    }
}
