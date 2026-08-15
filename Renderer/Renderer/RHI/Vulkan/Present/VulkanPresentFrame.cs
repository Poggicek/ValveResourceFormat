using Silk.NET.Vulkan;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Present;

/// <summary>
/// One acquired swapchain image, handed to whoever draws the frame.
/// </summary>
/// <param name="Backbuffer">The acquired image, as an <see cref="ITexture"/>. It starts the frame in
/// <see cref="ResourceState.Undefined"/> with undefined contents, and the presentation layer transitions
/// it to <see cref="ResourceState.Present"/> after the callback returns.</param>
/// <param name="CommandBuffer">The command buffer the frame's work is recorded into. Already begun; the
/// presentation layer ends and submits it.</param>
/// <param name="FrameIndex">The frame slot, for selecting a copy of any per-frame resource. Matches
/// <see cref="IDevice.FrameIndex"/>.</param>
/// <remarks>
/// <para>
/// The raw <c>VkCommandBuffer</c> is here because <see cref="IDevice.BeginCommandList"/> does not exist
/// on the Vulkan backend yet. It is the one part of this type expected to change: once the command list
/// layer lands, this carries an <see cref="ICommandList"/> and the presentation layer submits that
/// instead. Nothing else about the shape moves, because nothing else about it is provisional &#8212; the
/// backbuffer really is an <see cref="ITexture"/>, and the frame really does render straight into it.
/// </para>
/// </remarks>
public readonly record struct VulkanPresentFrame(
    VulkanSwapchainTexture Backbuffer,
    CommandBuffer CommandBuffer,
    int FrameIndex);

/// <summary>Draws one frame into an acquired swapchain image.</summary>
/// <param name="frame">The image, and the command buffer to record into.</param>
/// <remarks>Called on the shared render thread with the window's render lock held. Returning without
/// recording anything is legal and leaves the image's contents undefined, which the presentation layer
/// still presents; that is the correct behaviour for a window with nothing to show yet.</remarks>
public delegate void VulkanPresentFrameCallback(in VulkanPresentFrame frame);
