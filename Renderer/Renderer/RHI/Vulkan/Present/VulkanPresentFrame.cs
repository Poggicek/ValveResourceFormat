using Silk.NET.Vulkan;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Present;

/// <summary>
/// One acquired swapchain image, handed to whoever draws the frame.
/// </summary>
/// <param name="Backbuffer">The acquired image. It starts the frame in
/// <see cref="ResourceState.Undefined"/> with undefined contents, and the presentation layer transitions
/// it to <see cref="ResourceState.Present"/> after the callback returns.</param>
/// <param name="Device">The device the frame is open on. The callback records through command lists it
/// begins and submits on this itself; see the remarks.</param>
/// <param name="FrameIndex">The frame slot, for selecting a copy of any per-frame resource. Matches
/// <see cref="IDevice.FrameIndex"/>.</param>
/// <remarks>
/// <para>
/// <b>The callback owns its command lists; the presentation layer owns the frame.</b> This used to hand
/// over a command list the presentation layer had already begun, which cannot work for a callback that
/// draws a scene: <see cref="VulkanRecordingDevice.BeginCommandList"/> reuses one list and refuses to
/// begin a second while the first is unsubmitted, and a real frame is several lists &#8212; the scene,
/// the post-process chain, the overlay &#8212; because a transfer or a dispatch is not valid inside a
/// render pass. So the callback begins and submits as many as it needs, and the presentation layer
/// begins its own only afterwards, for the transition into <see cref="ResourceState.Present"/>. Every
/// one of them lands in the frame's single batch, in the order it was submitted.
/// </para>
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
/// <see cref="PresentTexture"/>, which is the one operation here that has to talk to Vulkan directly.
/// That is the OpenGL shape and the contract says Vulkan should not need it, which is exactly why it is
/// written down here rather than quietly worked around.
/// </para>
/// </remarks>
public readonly record struct VulkanPresentFrame(
    VulkanSwapchainTexture Backbuffer,
    VulkanPresentDevice Device,
    int FrameIndex)
{
    /// <summary>
    /// Copies a finished image onto the backbuffer, scaling and converting format as needed.
    /// </summary>
    /// <param name="source">The colour target the frame drew into.</param>
    /// <param name="filter">How to filter when the extents differ.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> came from another backend.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="source"/> is multisampled, or was not
    /// created with <see cref="TextureUsage.CopySource"/>.</exception>
    /// <remarks>
    /// <para>
    /// <c>vkCmdBlitImage</c> rather than <see cref="ICommandList.BlitTexture"/> for the reason the class
    /// remarks give: the destination is a swapchain image and every <see cref="ITexture"/> argument in
    /// the contract is resolved by a cast to <see cref="VulkanTexture"/> that it cannot satisfy. This is
    /// the same call <see cref="ICommandList.BlitTexture"/> makes, with the same barriers, against an
    /// image the cast cannot name.
    /// </para>
    /// <para>
    /// <b>A blit and not a copy, because the formats differ.</b> The scene tonemaps into an
    /// <see cref="RhiFormat.R8G8B8A8_UNorm"/> target and a surface almost always reports
    /// <see cref="RhiFormat.B8G8R8A8_UNorm"/>. <c>vkCmdCopyImage</c> between those would reinterpret the
    /// bytes and swap red with blue; a blit converts by component, which is what makes the colours right.
    /// It also absorbs an extent mismatch, which is what a frame recorded against a swapchain the window
    /// resized underneath is.
    /// </para>
    /// <para>
    /// The list is begun and submitted here, so the callback does not have to know that the presentation
    /// layer begins one of its own after it returns.
    /// </para>
    /// </remarks>
    public unsafe void PresentTexture(ITexture source, FilterMode filter = FilterMode.Linear)
    {
        ArgumentNullException.ThrowIfNull(source);

        var from = VulkanBarrierTranslation.AsVulkanTexture(source);

        if (from.SampleCount > 1)
        {
            throw new InvalidOperationException(
                $"Texture '{from.Name}' is multisampled and cannot be presented directly. Resolve it first; a blit would resolve it on OpenGL and fail here.");
        }

        from.RequireUsage(TextureUsage.CopySource, "Presenting a texture onto the backbuffer");

        var commands = Device.BeginCommandList("Present blit");

        try
        {
            var handle = ((VulkanCommandList)commands).Handle;

            from.TransitionTo(handle, ResourceState.CopySource);
            Backbuffer.TransitionTo(handle, ResourceState.CopyDestination);

            var region = new ImageBlit
            {
                SrcSubresource = ColorLayer,
                DstSubresource = ColorLayer,
            };

            region.SrcOffsets[1] = new Offset3D(from.Width, from.Height, 1);
            region.DstOffsets[1] = new Offset3D(Backbuffer.Width, Backbuffer.Height, 1);

            Device.Core.Api.CmdBlitImage(
                handle,
                from.Handle,
                ImageLayout.TransferSrcOptimal,
                Backbuffer.Handle,
                ImageLayout.TransferDstOptimal,
                1,
                &region,
                filter == FilterMode.Nearest ? Filter.Nearest : Filter.Linear);
        }
        finally
        {
            // Unconditional. The device reuses one command list, so a list left recording refuses every
            // later acquire and the next frame reports the leak rather than whatever threw here.
            Device.Submit(commands);
        }
    }

    /// <summary>
    /// Clears the backbuffer, for a frame that produced no image to show.
    /// </summary>
    /// <param name="red">Red, in the backbuffer's own encoding.</param>
    /// <param name="green">Green.</param>
    /// <param name="blue">Blue.</param>
    /// <param name="alpha">Alpha.</param>
    /// <remarks>The alternative is presenting an acquired image without writing it, whose contents are
    /// whatever the presentation engine last left there.</remarks>
    public unsafe void ClearBackbuffer(float red, float green, float blue, float alpha = 1f)
    {
        var color = new ClearColorValue(red, green, blue, alpha);
        var commands = Device.BeginCommandList("Present clear");

        try
        {
            var handle = ((VulkanCommandList)commands).Handle;

            Backbuffer.TransitionTo(handle, ResourceState.CopyDestination);

            var range = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };

            Device.Core.Api.CmdClearColorImage(handle, Backbuffer.Handle, ImageLayout.TransferDstOptimal, in color, 1, in range);
        }
        finally
        {
            Device.Submit(commands);
        }
    }

    private static ImageSubresourceLayers ColorLayer => new()
    {
        AspectMask = ImageAspectFlags.ColorBit,
        MipLevel = 0,
        BaseArrayLayer = 0,
        LayerCount = 1,
    };
}

/// <summary>Draws one frame into an acquired swapchain image.</summary>
/// <param name="frame">The image, and the device to record through.</param>
/// <remarks>Called on the shared render thread with the window's render lock held. Returning without
/// recording anything is legal and leaves the image's contents undefined, which the presentation layer
/// still presents; that is the correct behaviour for a window with nothing to show yet.</remarks>
public delegate void VulkanPresentFrameCallback(in VulkanPresentFrame frame);
