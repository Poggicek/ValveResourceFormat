using Silk.NET.Vulkan;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Present;

/// <summary>
/// One acquired swapchain image, handed to whoever draws the frame.
/// </summary>
/// <param name="Backbuffer">The acquired image. It starts the frame in
/// <see cref="ResourceState.Undefined"/> with undefined contents, and the presentation layer transitions
/// it to <see cref="ResourceState.Present"/> after the callback returns.</param>
/// <param name="Commands">The frame's command list, already begun. The presentation layer submits it;
/// the callback must not.</param>
/// <param name="CommandBuffer">The same recording as a raw <c>VkCommandBuffer</c>, for the operations
/// the backbuffer cannot yet express through <paramref name="Commands"/>. See the remarks.</param>
/// <param name="FrameIndex">The frame slot, for selecting a copy of any per-frame resource. Matches
/// <see cref="IDevice.FrameIndex"/>.</param>
/// <remarks>
/// <para>
/// <b>The backbuffer cannot currently be a render pass attachment, and that is a real limitation rather
/// than an oversight.</b> Every <see cref="ITexture"/>-taking member of <see cref="ICommandList"/>
/// resolves its argument through <c>VulkanBarrierTranslation.AsVulkanTexture</c>, which casts to the
/// sealed <see cref="VulkanTexture"/>; a swapchain image is not one, because it owns no memory and its
/// <c>VkImage</c> belongs to the swapchain. So <see cref="Backbuffer"/> is a genuine
/// <see cref="ITexture"/> for everything the contract describes &#8212; extent, format, usage, views,
/// layout tracking &#8212; but naming it in a <see cref="RenderPassDesc"/> throws.
/// </para>
/// <para>
/// Closing that gap needs one of two changes in files the presentation layer does not own: a
/// <see cref="VulkanTexture"/> constructor that adopts an image it does not own, or an interface on the
/// texture side that the command list resolves through instead of the concrete cast. Until then, a frame
/// that wants to draw renders into its own offscreen colour target and reaches the backbuffer through
/// <paramref name="CommandBuffer"/>. That is the OpenGL shape and the contract says Vulkan should not
/// need it, which is exactly why it is written down here rather than quietly worked around.
/// </para>
/// </remarks>
public readonly record struct VulkanPresentFrame(
    VulkanSwapchainTexture Backbuffer,
    ICommandList Commands,
    CommandBuffer CommandBuffer,
    int FrameIndex);

/// <summary>Draws one frame into an acquired swapchain image.</summary>
/// <param name="frame">The image, and the command list to record into.</param>
/// <remarks>Called on the shared render thread with the window's render lock held. Returning without
/// recording anything is legal and leaves the image's contents undefined, which the presentation layer
/// still presents; that is the correct behaviour for a window with nothing to show yet.</remarks>
public delegate void VulkanPresentFrameCallback(in VulkanPresentFrame frame);
