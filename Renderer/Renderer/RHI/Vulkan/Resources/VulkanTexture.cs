using System.Diagnostics;
using System.Globalization;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// <see cref="ITexture"/> on Vulkan: a <c>VkImage</c>, the memory bound to it, and a <c>VkImageView</c>
/// covering the subresources this object addresses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout tracking is explicit, per subresource, and owned by the image.</b> Every mip and layer
/// carries its current <see cref="ResourceState"/> in <see cref="StateOf"/>, and
/// <see cref="TransitionTo"/> reads the recorded state to fill the barrier's <c>oldLayout</c> rather
/// than asking the caller to supply one. That is the whole point of the design: a barrier whose
/// <c>oldLayout</c> disagrees with the image's actual layout is the single most common way this class
/// of port goes wrong, it produces a validation error in one place and undefined contents in another,
/// and making the tracked state the only source of <c>oldLayout</c> puts it structurally out of reach.
/// The caller may additionally state what it believes the current state to be, which is checked with
/// <see cref="Debug.Assert(bool, string)"/> so a wrong belief fails at the call that holds it instead
/// of surfacing as a warning in a log nobody reads.
/// </para>
/// <para>
/// <b>A view never tracks its own state.</b> Layout belongs to an image subresource, not to a view of
/// it, so two views of one image cannot disagree about what layout that image is in. Views created by
/// <see cref="CreateView"/> therefore forward every read and write of the tracking table to their
/// parent, offset by the view's base mip and layer. A view that kept its own table would let a
/// transition through one view leave the other holding a stale opinion, which is the same bug wearing
/// a different hat.
/// </para>
/// <para>
/// The image is created with <c>VK_IMAGE_LAYOUT_UNDEFINED</c>, matching the contract's promise that a
/// new texture starts in <see cref="ResourceState.Undefined"/>.
/// </para>
/// </remarks>
public sealed unsafe class VulkanTexture : ITexture
{
    private readonly Vk Api;
    private readonly Device Device;
    private readonly VulkanMemoryAllocator? Allocator;
    private readonly VulkanTexture? Parent;
    private readonly ResourceState[]? States;
    private readonly int BaseMipLevel;
    private readonly int BaseArrayLayer;
    private readonly bool OwnsImage;

    private Image ImageHandle;
    private ImageView ViewHandle;
    private VulkanAllocation Allocation;
    private bool Disposed;

    /// <summary>Gets the image handle. Shared with the parent when this is a view.</summary>
    public Image Handle => ImageHandle;

    /// <summary>Gets the view covering the subresources this object addresses.</summary>
    public ImageView View => ViewHandle;

    /// <summary>Gets the Vulkan format the image was created with.</summary>
    public Format VkFormat { get; }

    /// <summary>Gets the aspect mask this object's views and copies address.</summary>
    public ImageAspectFlags Aspect { get; }

    /// <summary>Gets the Vulkan usage flags the image was created with.</summary>
    public ImageUsageFlags UsageFlags { get; }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public int Depth { get; }

    /// <inheritdoc/>
    public int MipLevels { get; }

    /// <inheritdoc/>
    public int SampleCount { get; }

    /// <inheritdoc/>
    public RhiFormat Format { get; }

    /// <inheritdoc/>
    public TextureDimension Dimension { get; }

    /// <inheritdoc/>
    public TextureUsage Usage { get; }

    /// <summary>Gets a value indicating whether this object is a view aliasing another texture's image.</summary>
    public bool IsView => Parent is not null;

    /// <summary>Gets the texture that owns the image, which is this object when it is not a view.</summary>
    public VulkanTexture Root => Parent ?? this;

    /// <summary>Gets the number of array layers the image has, counting each cube face separately.</summary>
    public int LayerCount { get; }

    /// <summary>Creates an image, binds memory to it and builds a view covering all of it.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="allocator">Where the backing memory comes from.</param>
    /// <param name="debugNames">Used to name the image and its view.</param>
    /// <param name="desc">Creation parameters.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An extent, mip count or sample count is not
    /// positive, or the format has no Vulkan equivalent.</exception>
    /// <exception cref="ArgumentException"><see cref="TextureDesc.Usage"/> is
    /// <see cref="TextureUsage.None"/>.</exception>
    public VulkanTexture(
        Vk api,
        Device device,
        VulkanMemoryAllocator allocator,
        VulkanDebugNames debugNames,
        in TextureDesc desc)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(debugNames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.Width, nameof(desc));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.Height, nameof(desc));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.Depth, nameof(desc));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.MipLevels, nameof(desc));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.SampleCount, nameof(desc));

        if (desc.Usage == TextureUsage.None)
        {
            throw new ArgumentException($"Texture '{desc.Name}' declares no usage. Vulkan bakes usage into the object, so it must be complete at creation.", nameof(desc));
        }

        Api = api;
        Device = device;
        Allocator = allocator;
        OwnsImage = true;

        Name = desc.Name ?? string.Empty;
        Width = desc.Width;
        Height = desc.Height;
        Depth = desc.Depth;
        MipLevels = desc.MipLevels;
        SampleCount = desc.SampleCount;
        Format = desc.Format;
        Dimension = desc.Dimension;
        Usage = desc.Usage;

        VkFormat = FormatTables.ToVkFormat(desc.Format);
        Aspect = FormatTables.ToVkAspect(desc.Format);
        UsageFlags = ToVkUsage(desc.Usage);
        LayerCount = LayerCountFor(desc.Dimension, desc.Depth);

        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            Flags = desc.Dimension is TextureDimension.TextureCube or TextureDimension.TextureCubeArray
                ? ImageCreateFlags.CreateCubeCompatibleBit
                : ImageCreateFlags.None,
            ImageType = ToVkImageType(desc.Dimension),
            Format = VkFormat,
            Extent = new Extent3D(
                (uint)desc.Width,
                (uint)desc.Height,
                (uint)(desc.Dimension == TextureDimension.Texture3D ? desc.Depth : 1)),
            MipLevels = (uint)desc.MipLevels,
            ArrayLayers = (uint)LayerCount,
            Samples = ToVkSampleCount(desc.SampleCount),
            Tiling = ImageTiling.Optimal,
            Usage = UsageFlags,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        Api.CreateImage(Device, &info, null, out ImageHandle).Check("vkCreateImage");
        debugNames.SetName(ImageHandle, Name);

        States = new ResourceState[desc.MipLevels * LayerCount];

        try
        {
            Allocation = allocator.AllocateForImage(ImageHandle, Name);
            ViewHandle = CreateVkView(ToVkViewType(desc.Dimension, LayerCount), VkFormat, Aspect, 0, desc.MipLevels, 0, LayerCount);
            debugNames.SetName(ViewHandle, Name);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private VulkanTexture(
        VulkanTexture parent,
        ImageView view,
        TextureDimension dimension,
        RhiFormat format,
        Format vkFormat,
        ImageAspectFlags aspect,
        int baseMipLevel,
        int mipLevelCount,
        int baseArrayLayer,
        int arrayLayerCount)
    {
        Api = parent.Api;
        Device = parent.Device;
        Allocator = null;
        Parent = parent.Root;
        OwnsImage = false;

        ImageHandle = parent.ImageHandle;
        ViewHandle = view;
        BaseMipLevel = parent.BaseMipLevel + baseMipLevel;
        BaseArrayLayer = parent.BaseArrayLayer + baseArrayLayer;

        Name = string.Create(CultureInfo.InvariantCulture, $"{parent.Name} (view mip {baseMipLevel}+{mipLevelCount} layer {baseArrayLayer}+{arrayLayerCount})");
        Width = Math.Max(1, parent.Width >> baseMipLevel);
        Height = Math.Max(1, parent.Height >> baseMipLevel);
        Depth = dimension == TextureDimension.Texture3D ? Math.Max(1, parent.Depth >> baseMipLevel) : arrayLayerCount;
        MipLevels = mipLevelCount;
        SampleCount = parent.SampleCount;
        Format = format;
        Dimension = dimension;
        Usage = parent.Usage;
        VkFormat = vkFormat;
        Aspect = aspect;
        UsageFlags = parent.UsageFlags;
        LayerCount = arrayLayerCount;
    }

    /// <summary>Translates the contract's usage flags to Vulkan's.</summary>
    /// <param name="usage">The usage to translate.</param>
    /// <returns>The matching <see cref="ImageUsageFlags"/>.</returns>
    public static ImageUsageFlags ToVkUsage(TextureUsage usage)
    {
        var flags = ImageUsageFlags.None;

        if (usage.HasFlag(TextureUsage.Sampled))
        {
            flags |= ImageUsageFlags.SampledBit;
        }

        if (usage.HasFlag(TextureUsage.Storage))
        {
            flags |= ImageUsageFlags.StorageBit;
        }

        if (usage.HasFlag(TextureUsage.ColorTarget))
        {
            flags |= ImageUsageFlags.ColorAttachmentBit;
        }

        if (usage.HasFlag(TextureUsage.DepthStencilTarget))
        {
            flags |= ImageUsageFlags.DepthStencilAttachmentBit;
        }

        if (usage.HasFlag(TextureUsage.CopySource))
        {
            flags |= ImageUsageFlags.TransferSrcBit;
        }

        if (usage.HasFlag(TextureUsage.CopyDestination))
        {
            flags |= ImageUsageFlags.TransferDstBit;
        }

        return flags;
    }

    /// <summary>Throws when the image was not created with a usage a caller now depends on.</summary>
    /// <param name="required">The usage the operation needs.</param>
    /// <param name="operation">What was being attempted, used in the message.</param>
    /// <exception cref="InvalidOperationException">The usage was not declared at creation.</exception>
    public void RequireUsage(TextureUsage required, string operation)
    {
        if (!Usage.HasFlag(required))
        {
            throw new InvalidOperationException($"Texture '{Name}' was created with usage {Usage}, which does not include {required}. {operation} needs it; add the flag to the {nameof(TextureDesc)}.");
        }
    }

    /// <summary>Gets the number of array layers a shape has, counting each cube face separately.</summary>
    /// <param name="dimension">The texture shape.</param>
    /// <param name="depth">The declared depth or layer count.</param>
    /// <returns>The Vulkan array layer count.</returns>
    public static int LayerCountFor(TextureDimension dimension, int depth) => dimension switch
    {
        TextureDimension.TextureCube => 6,
        TextureDimension.TextureCubeArray => depth * 6,
        TextureDimension.Texture2DArray => depth,

        // A volume texture has one array layer; its slices are the extent's depth, not layers.
        _ => 1,
    };

    /// <summary>Gets the extent of one mip level.</summary>
    /// <param name="mipLevel">The mip level to measure.</param>
    /// <returns>The width, height and depth in texels. Depth is 1 for everything but a volume texture.</returns>
    public (int Width, int Height, int Depth) MipExtent(int mipLevel)
    {
        var width = Math.Max(1, Width >> mipLevel);
        var height = Math.Max(1, Height >> mipLevel);
        var depth = Dimension == TextureDimension.Texture3D ? Math.Max(1, Depth >> mipLevel) : 1;

        return (width, height, depth);
    }

    /// <summary>Gets the number of bytes a tightly packed upload of one mip level supplies.</summary>
    /// <param name="mipLevel">The mip level to measure.</param>
    /// <returns>The expected byte count, counting whole blocks for a compressed format.</returns>
    /// <remarks>Compressed mips round up to whole blocks, so the last two levels of a BC chain are one
    /// block each rather than a fraction of one. Sizing a copy without that rounding is the classic
    /// way to hand <c>vkCmdCopyBufferToImage</c> a source region that runs off the end of the staging
    /// buffer.</remarks>
    public int MipSizeInBytes(int mipLevel)
    {
        var (width, height, depth) = MipExtent(mipLevel);

        var blockWidth = FormatTables.BlockWidth(Format);
        var blockHeight = FormatTables.BlockHeight(Format);

        var blocksX = (width + blockWidth - 1) / blockWidth;
        var blocksY = (height + blockHeight - 1) / blockHeight;

        return blocksX * blocksY * depth * FormatTables.BytesPerBlock(Format);
    }

    /// <summary>Gets the state a subresource is currently tracked as being in.</summary>
    /// <param name="mipLevel">Mip level, relative to this object.</param>
    /// <param name="arrayLayer">Array layer, relative to this object.</param>
    /// <returns>The tracked state.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The subresource is outside this object.</exception>
    public ResourceState StateOf(int mipLevel, int arrayLayer)
    {
        var root = Root;
        return root.StatesArray[TrackingIndex(mipLevel, arrayLayer)];
    }

    private ResourceState[] StatesArray => States ?? throw new InvalidOperationException($"Texture '{Name}' owns no state table; a view must forward to its parent.");

    private int TrackingIndex(int mipLevel, int arrayLayer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(mipLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(mipLevel, MipLevels);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayLayer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(arrayLayer, LayerCount);

        var root = Root;
        return ((BaseMipLevel + mipLevel) * root.LayerCount) + BaseArrayLayer + arrayLayer;
    }

    /// <summary>
    /// Asserts, in debug builds, that every subresource of this object is in the expected state.
    /// </summary>
    /// <param name="expected">The state the caller believes the texture is in.</param>
    /// <param name="operation">What was about to happen, used in the assertion message.</param>
    /// <remarks>
    /// Compiled out of release builds, like every <see cref="Debug.Assert(bool, string)"/>. It exists so
    /// that a call site which has reasoned about layout and reasoned wrongly stops at the point of the
    /// wrong belief, with the operation named, rather than producing a validation message attributed to
    /// a copy several commands later.
    /// </remarks>
    [Conditional("DEBUG")]
    public void AssertState(ResourceState expected, string operation)
    {
        for (var mip = 0; mip < MipLevels; mip++)
        {
            for (var layer = 0; layer < LayerCount; layer++)
            {
                var actual = StateOf(mip, layer);

                Debug.Assert(
                    actual == expected,
                    string.Create(CultureInfo.InvariantCulture, $"Texture '{Name}' mip {mip} layer {layer} is in {actual}, but {operation} expected {expected}."));
            }
        }
    }

    /// <summary>
    /// Records a layout transition of every subresource this object addresses, and updates the tracked
    /// state.
    /// </summary>
    /// <param name="commandBuffer">The command buffer to record the barrier into.</param>
    /// <param name="newState">The state to move to.</param>
    /// <param name="expectedCurrentState">What the caller believes the current state is, checked with an
    /// assertion in debug builds, or <see langword="null"/> to assert nothing.</param>
    /// <remarks>
    /// <para>
    /// The barrier's <c>oldLayout</c> comes from the tracked state, never from the caller, so it cannot
    /// disagree with the image's real layout. <paramref name="expectedCurrentState"/> is a check on the
    /// caller's understanding, not an input to the barrier.
    /// </para>
    /// <para>
    /// Subresources already in <paramref name="newState"/> are skipped, so calling this defensively
    /// before an operation costs nothing when the texture is already where it needs to be. Subresources
    /// in different states are batched into one <c>vkCmdPipelineBarrier2</c> call, as the contract asks:
    /// separate calls cost separate pipeline stalls.
    /// </para>
    /// </remarks>
    public void TransitionTo(CommandBuffer commandBuffer, ResourceState newState, ResourceState? expectedCurrentState = null)
    {
        if (expectedCurrentState is { } expected)
        {
            AssertState(expected, string.Create(CultureInfo.InvariantCulture, $"a transition to {newState}"));
        }

        var isDepth = RhiFormatInfo.IsDepth(Format);
        var target = VulkanResourceStates.ForImage(newState, isDepth);

        // Fast path. Nearly every texture is in one state across all of its subresources, and this is
        // the barrier call the whole backend sits on, so the common case allocates nothing and emits a
        // single barrier covering the whole range.
        if (TryGetUniformState(out var uniform))
        {
            if (uniform == newState)
            {
                return;
            }

            var source = VulkanResourceStates.ForImage(uniform, isDepth);

            var barrier = BuildBarrier(in source, in target, BaseMipLevel, MipLevels, BaseArrayLayer, LayerCount);
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = 1,
                PImageMemoryBarriers = &barrier,
            };

            Api.CmdPipelineBarrier2(commandBuffer, &dependency);
            SetTrackedState(newState);
            return;
        }

        // Mixed states: one barrier per subresource, batched into a single call because separate calls
        // cost separate pipeline stalls.
        var barriers = new List<ImageMemoryBarrier2>();

        for (var mip = 0; mip < MipLevels; mip++)
        {
            for (var layer = 0; layer < LayerCount; layer++)
            {
                var current = StateOf(mip, layer);

                if (current == newState)
                {
                    continue;
                }

                var source = VulkanResourceStates.ForImage(current, isDepth);

                barriers.Add(BuildBarrier(in source, in target, BaseMipLevel + mip, 1, BaseArrayLayer + layer, 1));
                Root.StatesArray[TrackingIndex(mip, layer)] = newState;
            }
        }

        if (barriers.Count == 0)
        {
            return;
        }

        var array = barriers.ToArray();

        fixed (ImageMemoryBarrier2* p = array)
        {
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = (uint)array.Length,
                PImageMemoryBarriers = p,
            };

            Api.CmdPipelineBarrier2(commandBuffer, &dependency);
        }
    }

    private ImageMemoryBarrier2 BuildBarrier(
        in VulkanImageState source,
        in VulkanImageState target,
        int baseMipLevel,
        int mipLevelCount,
        int baseArrayLayer,
        int arrayLayerCount) => new()
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
            Image = ImageHandle,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = Aspect,
                BaseMipLevel = (uint)baseMipLevel,
                LevelCount = (uint)mipLevelCount,
                BaseArrayLayer = (uint)baseArrayLayer,
                LayerCount = (uint)arrayLayerCount,
            },
        };

    private bool TryGetUniformState(out ResourceState state)
    {
        state = StateOf(0, 0);

        for (var mip = 0; mip < MipLevels; mip++)
        {
            for (var layer = 0; layer < LayerCount; layer++)
            {
                if (StateOf(mip, layer) != state)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void SetTrackedState(ResourceState state)
    {
        var states = Root.StatesArray;

        for (var mip = 0; mip < MipLevels; mip++)
        {
            for (var layer = 0; layer < LayerCount; layer++)
            {
                states[TrackingIndex(mip, layer)] = state;
            }
        }
    }

    /// <summary>
    /// Overwrites the tracked state of every subresource without recording a barrier.
    /// </summary>
    /// <param name="state">The state the subresources are actually in.</param>
    /// <remarks>
    /// For images whose layout was changed by something outside this object's knowledge: a swapchain
    /// image the presentation layer acquired, or a render pass whose store operation left an attachment
    /// in its final layout. Using it to paper over a transition this class could have recorded defeats
    /// the tracking entirely, which is why it is a separate, deliberately awkward call rather than an
    /// optional argument on <see cref="TransitionTo"/>.
    /// </remarks>
    public void OverrideTrackedState(ResourceState state) => SetTrackedState(state);

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">The mip or layer range falls outside this texture.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="aspect"/> names an aspect the format
    /// does not have.</exception>
    /// <remarks>
    /// <para>
    /// <paramref name="baseArrayLayer"/> and <paramref name="arrayLayerCount"/> count cube faces
    /// individually, matching the OpenGL backend and <c>VkImageSubresourceRange</c>: a view of one whole
    /// cube is six layers.
    /// </para>
    /// <para>
    /// <see cref="TextureAspect.Stencil"/> produces a real stencil-aspect view, which is what
    /// <see cref="Framebuffer"/>'s sampleable stencil view of the depth attachment needs. On OpenGL that
    /// is a texture parameter; here it is the view's aspect mask, which is the more direct expression of
    /// the same thing.
    /// </para>
    /// </remarks>
    public ITexture CreateView(
        int baseMipLevel,
        int mipLevelCount,
        int baseArrayLayer,
        int arrayLayerCount,
        RhiFormat format = RhiFormat.Undefined,
        TextureAspect aspect = TextureAspect.All)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baseMipLevel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mipLevelCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(baseMipLevel + mipLevelCount, MipLevels);
        ArgumentOutOfRangeException.ThrowIfNegative(baseArrayLayer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(arrayLayerCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(baseArrayLayer + arrayLayerCount, LayerCount);

        var viewFormat = format == RhiFormat.Undefined ? Format : format;

        if (aspect != TextureAspect.All && !RhiFormatInfo.IsDepth(viewFormat))
        {
            throw new InvalidOperationException($"Texture '{Name}' is {viewFormat}, which has no {aspect} aspect to view. Only depth-stencil formats have more than one aspect.");
        }

        // ToVkAspect throws for a stencil view of a depth-only format, which is the other half of the
        // same mistake and is worth the same loud failure.
        var viewAspect = FormatTables.ToVkAspect(viewFormat, aspect);
        var viewVkFormat = FormatTables.ToVkFormat(viewFormat);
        var viewDimension = ViewDimension(arrayLayerCount);
        var viewType = ToVkViewType(viewDimension, arrayLayerCount);

        var handle = CreateVkView(viewType, viewVkFormat, viewAspect, baseMipLevel, mipLevelCount, baseArrayLayer, arrayLayerCount);

        return new VulkanTexture(
            this,
            handle,
            viewDimension,
            viewFormat,
            viewVkFormat,
            viewAspect,
            baseMipLevel,
            mipLevelCount,
            baseArrayLayer,
            arrayLayerCount);
    }

    private ImageView CreateVkView(
        ImageViewType viewType,
        Format format,
        ImageAspectFlags aspect,
        int baseMipLevel,
        int mipLevelCount,
        int baseArrayLayer,
        int arrayLayerCount)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = ImageHandle,
            ViewType = viewType,
            Format = format,
            Components = default,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspect,
                BaseMipLevel = (uint)(BaseMipLevel + baseMipLevel),
                LevelCount = (uint)mipLevelCount,
                BaseArrayLayer = (uint)(BaseArrayLayer + baseArrayLayer),
                LayerCount = (uint)arrayLayerCount,
            },
        };

        Api.CreateImageView(Device, &info, null, out var view).Check("vkCreateImageView");
        return view;
    }

    private TextureDimension ViewDimension(int arrayLayerCount) => Dimension switch
    {
        TextureDimension.TextureCube or TextureDimension.TextureCubeArray => arrayLayerCount switch
        {
            1 => TextureDimension.Texture2D,
            6 => TextureDimension.TextureCube,
            _ => TextureDimension.TextureCubeArray,
        },
        TextureDimension.Texture2DArray when arrayLayerCount == 1 => TextureDimension.Texture2D,
        _ => Dimension,
    };

    private static ImageType ToVkImageType(TextureDimension dimension) => dimension switch
    {
        TextureDimension.Texture1D => ImageType.Type1D,
        TextureDimension.Texture3D => ImageType.Type3D,
        // A multisampled image is a 2D image whose sample count is carried separately. Vulkan keeps the
        // two facts apart, so the dimension only decides the type here.
        TextureDimension.Texture2D or TextureDimension.Texture2DMultisample or TextureDimension.Texture2DArray
            or TextureDimension.TextureCube or TextureDimension.TextureCubeArray => ImageType.Type2D,
        _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "Unknown texture dimension."),
    };

    private static ImageViewType ToVkViewType(TextureDimension dimension, int arrayLayerCount) => dimension switch
    {
        TextureDimension.Texture1D => ImageViewType.Type1D,

        // Vulkan has no multisample view type: a view of a multisampled image is a plain 2D view, and the
        // image's own sample count is what makes it multisampled.
        TextureDimension.Texture2D or TextureDimension.Texture2DMultisample => ImageViewType.Type2D,
        TextureDimension.Texture2DArray => ImageViewType.Type2DArray,
        TextureDimension.Texture3D => ImageViewType.Type3D,
        TextureDimension.TextureCube => arrayLayerCount == 6 ? ImageViewType.TypeCube : ImageViewType.Type2DArray,
        TextureDimension.TextureCubeArray => ImageViewType.TypeCubeArray,
        _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "Unknown texture dimension."),
    };

    private static SampleCountFlags ToVkSampleCount(int sampleCount) => sampleCount switch
    {
        1 => SampleCountFlags.Count1Bit,
        2 => SampleCountFlags.Count2Bit,
        4 => SampleCountFlags.Count4Bit,
        8 => SampleCountFlags.Count8Bit,
        16 => SampleCountFlags.Count16Bit,
        32 => SampleCountFlags.Count32Bit,
        64 => SampleCountFlags.Count64Bit,
        _ => throw new ArgumentOutOfRangeException(nameof(sampleCount), sampleCount, "Sample count must be a power of two between 1 and 64."),
    };

    /// <summary>Queues this texture's view, and its image and memory when it owns them, on a deletion queue.</summary>
    /// <param name="deletionQueue">The queue to enqueue on.</param>
    /// <param name="frameSerial">The serial of the frame during which destruction was requested.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deletionQueue"/> is <see langword="null"/>.</exception>
    /// <remarks>Backs <see cref="IDevice.DeferredDestroy"/>. A view releases only its own handle; the
    /// image belongs to its parent, exactly as on the OpenGL backend.</remarks>
    public void EnqueueDestroy(VulkanDeletionQueue deletionQueue, ulong frameSerial)
    {
        ArgumentNullException.ThrowIfNull(deletionQueue);

        if (Disposed)
        {
            return;
        }

        Disposed = true;

        if (ViewHandle.Handle != 0)
        {
            deletionQueue.Enqueue(frameSerial, ViewHandle);
            ViewHandle = default;
        }

        if (OwnsImage && ImageHandle.Handle != 0 && Allocator is not null)
        {
            deletionQueue.Enqueue(frameSerial, ImageHandle, Allocation, Allocator);
            Allocation = default;
        }

        ImageHandle = default;
    }

    /// <summary>Destroys the view, and the image and its memory when this object owns them.</summary>
    /// <remarks>Immediate, so it is only safe once no in-flight frame can reference the texture. Prefer
    /// <see cref="IDevice.DeferredDestroy"/>.</remarks>
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

        if (OwnsImage)
        {
            if (ImageHandle.Handle != 0)
            {
                Api.DestroyImage(Device, ImageHandle, null);
            }

            Allocator?.Free(Allocation);
            Allocation = default;
        }

        ImageHandle = default;
    }
}
