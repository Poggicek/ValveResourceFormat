using System.Diagnostics;
using System.Globalization;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Present;

/// <summary>
/// One image of a swapchain, surfaced as an <see cref="ITexture"/> the renderer can draw into.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the case the contract's present rule is written around.</b> On OpenGL, framebuffer 0 has
/// no texture handle, so the presented surface can never appear in a render pass and the presentation
/// layer blits an offscreen texture into it once per frame. On Vulkan a swapchain image <i>is</i> a
/// real <c>VkImage</c>: it can be a colour attachment, so the frame renders straight into it and the
/// copy does not exist. That asymmetry is the API's, not the renderer's, and this type is where it is
/// paid off.
/// </para>
/// <para>
/// <b>The image is borrowed, the view is owned.</b> <c>vkGetSwapchainImagesKHR</c> hands out images
/// belonging to the swapchain; destroying one is invalid and destroying the swapchain destroys them.
/// So <see cref="Dispose"/> releases only the <c>VkImageView</c> this object created, which is also why
/// this is a separate type from <see cref="VulkanTexture"/> rather than a constructor on it &#8212;
/// <see cref="VulkanTexture"/> allocates memory it then owns, and a swapchain image has neither.
/// </para>
/// <para>
/// <b>Layout tracking follows <see cref="VulkanTexture"/>'s rule exactly:</b> the tracked state is the
/// only source of a barrier's <c>oldLayout</c>, so it cannot disagree with the image's real layout.
/// The one difference is <see cref="OverrideTrackedState"/> at the top of every frame: acquiring an
/// image says nothing about what layout it is in and guarantees nothing about its contents, so the
/// frame starts from <see cref="ResourceState.Undefined"/> and the first barrier discards whatever was
/// there. Anything else would be a lie about a layout the presentation engine chose.
/// </para>
/// </remarks>
public sealed unsafe class VulkanSwapchainTexture : ITexture
{
    private readonly Vk Api;
    private readonly Device Device;
    private readonly VulkanSwapchainTexture? Parent;

    private ImageView ViewHandle;
    private ResourceState TrackedState;
    private bool Disposed;

    /// <summary>Gets the swapchain image this object addresses. Owned by the swapchain, never by this.</summary>
    public Image Handle { get; }

    /// <summary>Gets the view covering the image.</summary>
    public ImageView View => ViewHandle;

    /// <summary>Gets the Vulkan format the swapchain was created with.</summary>
    public Format VkFormat { get; }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public int Depth => 1;

    /// <inheritdoc/>
    public int MipLevels => 1;

    /// <inheritdoc/>
    /// <remarks>Always 1. A multisampled swapchain does not exist; multisampling resolves into the
    /// swapchain image through <see cref="ColorAttachmentDesc.ResolveTexture"/>.</remarks>
    public int SampleCount => 1;

    /// <inheritdoc/>
    public RhiFormat Format { get; }

    /// <inheritdoc/>
    public TextureDimension Dimension => TextureDimension.Texture2D;

    /// <inheritdoc/>
    public TextureUsage Usage { get; }

    /// <summary>Gets the state this image is currently tracked as being in.</summary>
    public ResourceState State => Parent is null ? TrackedState : Parent.State;

    /// <summary>Wraps a swapchain image and builds a view of it.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device the swapchain belongs to.</param>
    /// <param name="debugNames">Used to name the image and its view.</param>
    /// <param name="image">The image <c>vkGetSwapchainImagesKHR</c> returned.</param>
    /// <param name="format">The format the swapchain was created with.</param>
    /// <param name="width">Swapchain width in pixels.</param>
    /// <param name="height">Swapchain height in pixels.</param>
    /// <param name="usage">The subset of the swapchain's <c>VkImageUsageFlags</c> expressed in contract
    /// terms. Must match what the swapchain was created with; Vulkan bakes usage into the object.</param>
    /// <param name="name">Debug name.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An extent is not positive, or the format has no
    /// Vulkan equivalent.</exception>
    /// <exception cref="VulkanException">The view could not be created.</exception>
    public VulkanSwapchainTexture(
        Vk api,
        Device device,
        VulkanDebugNames debugNames,
        Image image,
        RhiFormat format,
        int width,
        int height,
        TextureUsage usage,
        string name)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(debugNames);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Api = api;
        Device = device;
        Handle = image;
        Format = format;
        Width = width;
        Height = height;
        Usage = usage;
        Name = name;

        VkFormat = FormatTables.ToVkFormat(format);
        TrackedState = ResourceState.Undefined;

        ViewHandle = CreateVkView(VkFormat);

        debugNames.SetName(Handle, name);
        debugNames.SetName(ViewHandle, name);
    }

    private VulkanSwapchainTexture(VulkanSwapchainTexture parent, ImageView view, RhiFormat format, Format vkFormat)
    {
        Api = parent.Api;
        Device = parent.Device;
        Parent = parent;
        Handle = parent.Handle;
        ViewHandle = view;
        Format = format;
        VkFormat = vkFormat;
        Width = parent.Width;
        Height = parent.Height;
        Usage = parent.Usage;
        Name = string.Create(CultureInfo.InvariantCulture, $"{parent.Name} (view {format})");
    }

    private ImageView CreateVkView(Format format)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Handle,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        Api.CreateImageView(Device, &info, null, out var view).Check("vkCreateImageView");
        return view;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">Anything other than the whole image is requested.
    /// A swapchain image has exactly one mip level and one array layer.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="aspect"/> is not
    /// <see cref="TextureAspect.All"/>. A colour image has one aspect.</exception>
    /// <remarks>Useful for the one thing it can express: reinterpreting the image between its UNorm and
    /// sRGB views, so a frame can choose whether the hardware applies the transfer function on write.
    /// The view shares the parent's tracked state, because layout belongs to the image subresource and
    /// not to a view of it.</remarks>
    public ITexture CreateView(
        int baseMipLevel,
        int mipLevelCount,
        int baseArrayLayer,
        int arrayLayerCount,
        RhiFormat format = RhiFormat.Undefined,
        TextureAspect aspect = TextureAspect.All)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(baseMipLevel, 0);
        ArgumentOutOfRangeException.ThrowIfNotEqual(mipLevelCount, 1);
        ArgumentOutOfRangeException.ThrowIfNotEqual(baseArrayLayer, 0);
        ArgumentOutOfRangeException.ThrowIfNotEqual(arrayLayerCount, 1);

        if (aspect != TextureAspect.All)
        {
            throw new InvalidOperationException($"Swapchain image '{Name}' is a colour image, which has no {aspect} aspect to view.");
        }

        var viewFormat = format == RhiFormat.Undefined ? Format : format;
        var viewVkFormat = FormatTables.ToVkFormat(viewFormat);

        return new VulkanSwapchainTexture(Parent ?? this, CreateVkView(viewVkFormat), viewFormat, viewVkFormat);
    }

    /// <summary>
    /// Records a layout transition of the image and updates the tracked state.
    /// </summary>
    /// <param name="commandBuffer">The command buffer to record the barrier into.</param>
    /// <param name="newState">The state to move to.</param>
    /// <remarks>The barrier's <c>oldLayout</c> comes from the tracked state, never from the caller.
    /// Transitioning to the state the image is already in records nothing.</remarks>
    public void TransitionTo(CommandBuffer commandBuffer, ResourceState newState)
    {
        var root = Parent ?? this;
        var current = root.TrackedState;

        if (current == newState)
        {
            return;
        }

        var source = VulkanResourceStates.ForImage(current);
        var target = VulkanResourceStates.ForImage(newState);

        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = source.Stages,
            SrcAccessMask = source.Access,
            DstStageMask = target.Stages,
            DstAccessMask = target.Access,
            OldLayout = source.Layout,
            NewLayout = target.Layout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = Handle,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };

        Api.CmdPipelineBarrier2(commandBuffer, &dependency);

        root.TrackedState = newState;
    }

    /// <summary>Overwrites the tracked state without recording a barrier.</summary>
    /// <param name="state">The state the image is actually in.</param>
    /// <remarks>Called with <see cref="ResourceState.Undefined"/> once per frame after the acquire, for
    /// the reason given on the class. Using it anywhere else papers over a transition that should have
    /// been recorded.</remarks>
    public void OverrideTrackedState(ResourceState state)
    {
        var root = Parent ?? this;
        root.TrackedState = state;
    }

    /// <summary>Asserts, in debug builds, that the image is in the expected state.</summary>
    /// <param name="expected">The state the caller believes the image is in.</param>
    /// <param name="operation">What was about to happen, used in the assertion message.</param>
    [Conditional("DEBUG")]
    public void AssertState(ResourceState expected, string operation)
        => Debug.Assert(
            State == expected,
            string.Create(CultureInfo.InvariantCulture, $"Swapchain image '{Name}' is in {State}, but {operation} expected {expected}."));

    /// <summary>Translates a Vulkan surface format into the contract's format enumeration.</summary>
    /// <param name="format">The Vulkan format a surface reported.</param>
    /// <param name="result">The matching <see cref="RhiFormat"/>.</param>
    /// <returns><see langword="true"/> when the format has a contract equivalent.</returns>
    /// <remarks>Deliberately covers only the formats a surface can plausibly report rather than
    /// inverting the whole table. A surface format the contract cannot name is one the renderer could
    /// not describe a render pass against, so the presentation layer must reject it at swapchain
    /// creation rather than discover it at the first draw.</remarks>
    public static bool TryToRhiFormat(Format format, out RhiFormat result)
    {
        switch (format)
        {
            case Silk.NET.Vulkan.Format.B8G8R8A8Unorm: result = RhiFormat.B8G8R8A8_UNorm; return true;
            case Silk.NET.Vulkan.Format.B8G8R8A8Srgb: result = RhiFormat.B8G8R8A8_SRgb; return true;
            case Silk.NET.Vulkan.Format.R8G8B8A8Unorm: result = RhiFormat.R8G8B8A8_UNorm; return true;
            case Silk.NET.Vulkan.Format.R8G8B8A8Srgb: result = RhiFormat.R8G8B8A8_SRgb; return true;
            case Silk.NET.Vulkan.Format.A2B10G10R10UnormPack32: result = RhiFormat.R10G10B10A2_UNorm; return true;
            case Silk.NET.Vulkan.Format.R16G16B16A16Sfloat: result = RhiFormat.R16G16B16A16_SFloat; return true;
            default: result = RhiFormat.Undefined; return false;
        }
    }

    /// <summary>Destroys the view. The image belongs to the swapchain and is left alone.</summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        if (ViewHandle.Handle != 0)
        {
            Api.DestroyImageView(Device, ViewHandle, null);
            ViewHandle = default;
        }
    }
}
