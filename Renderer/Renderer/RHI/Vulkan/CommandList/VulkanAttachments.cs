using Silk.NET.Vulkan;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// Translates the contract's attachment description into the pieces <c>vkCmdBeginRendering</c> takes,
/// and hands out the single-subresource image views dynamic rendering requires.
/// </summary>
public static class VulkanAttachments
{
    /// <summary>Translates a load operation.</summary>
    /// <param name="op">The operation to translate.</param>
    /// <returns>The matching <see cref="AttachmentLoadOp"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The operation is not one of the three.</exception>
    public static AttachmentLoadOp ToVk(LoadOp op) => op switch
    {
        LoadOp.Load => AttachmentLoadOp.Load,
        LoadOp.Clear => AttachmentLoadOp.Clear,
        LoadOp.DontCare => AttachmentLoadOp.DontCare,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown load operation."),
    };

    /// <summary>Translates a store operation.</summary>
    /// <param name="op">The operation to translate.</param>
    /// <returns>The matching <see cref="AttachmentStoreOp"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The operation is not one of the two.</exception>
    public static AttachmentStoreOp ToVk(StoreOp op) => op switch
    {
        StoreOp.Store => AttachmentStoreOp.Store,
        StoreOp.DontCare => AttachmentStoreOp.DontCare,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown store operation."),
    };

    /// <summary>
    /// Builds the clear value for a colour attachment, choosing the union member its format reads.
    /// </summary>
    /// <param name="format">The attachment's format.</param>
    /// <param name="color">The clear colour the descriptor carries.</param>
    /// <returns>The clear value.</returns>
    /// <remarks>An integer attachment reads <c>int32</c> or <c>uint32</c> out of the union and a float
    /// one reads <c>float32</c>; they are the same bytes, so handing an integer target a float clear
    /// value clears the bit pattern of the float rather than the number. This mirrors the OpenGL
    /// backend, which picks between the three <c>glClearNamedFramebuffer</c> entry points for the same
    /// reason.</remarks>
    public static ClearValue ColorClear(RhiFormat format, Vector4 color) => ClearKindOf(format) switch
    {
        ClearKind.UnsignedInteger => new ClearValue
        {
            Color = new ClearColorValue
            {
                Uint32_0 = (uint)color.X,
                Uint32_1 = (uint)color.Y,
                Uint32_2 = (uint)color.Z,
                Uint32_3 = (uint)color.W,
            },
        },

        ClearKind.SignedInteger => new ClearValue
        {
            Color = new ClearColorValue
            {
                Int32_0 = (int)color.X,
                Int32_1 = (int)color.Y,
                Int32_2 = (int)color.Z,
                Int32_3 = (int)color.W,
            },
        },

        _ => new ClearValue
        {
            Color = new ClearColorValue
            {
                Float32_0 = color.X,
                Float32_1 = color.Y,
                Float32_2 = color.Z,
                Float32_3 = color.W,
            },
        },
    };

    /// <summary>Builds the clear value for a depth-stencil attachment.</summary>
    /// <param name="depth">The depth clear value. The renderer is reverse-Z, so its far plane is 0.</param>
    /// <param name="stencil">The stencil clear value.</param>
    /// <returns>The clear value.</returns>
    public static ClearValue DepthStencilClear(float depth, byte stencil) => new()
    {
        DepthStencil = new ClearDepthStencilValue(depth, stencil),
    };

    /// <summary>
    /// Builds the raw clear value <see cref="ICommandList.ClearTexture"/> takes, reinterpreted the way
    /// the texture's format reads it.
    /// </summary>
    /// <param name="format">The texture's format.</param>
    /// <param name="value">The raw 32 bit value.</param>
    /// <returns>The clear colour value.</returns>
    /// <remarks>
    /// The value is broadcast to every component rather than unpacked across them. That matches the one
    /// use this has in the renderer &#8212; the overdraw counters, which are single-channel unsigned
    /// integer images cleared to <see cref="uint.MaxValue"/> &#8212; and it is the only reading that
    /// stays meaningful for a format with a different channel count from the 32 bits supplied. A float
    /// format takes the value as a bit pattern, so <c>0</c> clears to zero and other values are
    /// deliberate reinterpretations.
    /// </remarks>
    public static ClearColorValue RawClear(RhiFormat format, uint value) => ClearKindOf(format) switch
    {
        ClearKind.UnsignedInteger => new ClearColorValue
        {
            Uint32_0 = value,
            Uint32_1 = value,
            Uint32_2 = value,
            Uint32_3 = value,
        },

        ClearKind.SignedInteger => new ClearColorValue
        {
            Int32_0 = (int)value,
            Int32_1 = (int)value,
            Int32_2 = (int)value,
            Int32_3 = (int)value,
        },

        _ => new ClearColorValue
        {
            Float32_0 = BitConverter.UInt32BitsToSingle(value),
            Float32_1 = BitConverter.UInt32BitsToSingle(value),
            Float32_2 = BitConverter.UInt32BitsToSingle(value),
            Float32_3 = BitConverter.UInt32BitsToSingle(value),
        },
    };

    /// <summary>How a format reads the bytes of a clear value.</summary>
    private enum ClearKind
    {
        Float,
        SignedInteger,
        UnsignedInteger,
    }

    // Kept in step with GLCommandList.ClearKindOf, which makes the same choice for the same reason.
    private static ClearKind ClearKindOf(RhiFormat format) => format switch
    {
        RhiFormat.R8_UInt or RhiFormat.R16_UInt or RhiFormat.R32_UInt or RhiFormat.R32G32_UInt
            or RhiFormat.R32G32B32A32_UInt or RhiFormat.R8G8B8A8_UInt or RhiFormat.R16G16B16A16_UInt
            => ClearKind.UnsignedInteger,

        RhiFormat.R32_SInt or RhiFormat.R16G16_SInt or RhiFormat.R16G16B16A16_SInt or RhiFormat.R32G32B32A32_SInt
            => ClearKind.SignedInteger,

        _ => ClearKind.Float,
    };
}

/// <summary>
/// Hands out the single-subresource image views dynamic rendering and storage image bindings need,
/// keeping one per distinct subresource for as long as the command list lives.
/// </summary>
/// <remarks>
/// <para>
/// <b>A rendering attachment view must cover exactly one mip level.</b> A texture's own view covers its
/// whole mip chain and every array layer, so it can only ever be the attachment of a texture that has
/// one of each &#8212; which the bloom chain's mip targets and the shadow atlas's cube faces are not.
/// The same holds for <see cref="ICommandList.BindStorageTexture"/>, whose <c>mipLevel</c> selects the
/// level the shader writes.
/// </para>
/// <para>
/// Cached rather than created per pass because a <c>VkImageView</c> costs a driver allocation and the
/// set of them a frame uses is small and repeats every frame. The cache is bounded and evicts oldest
/// first, exactly as <c>GLCommandList</c>'s framebuffer cache does, and for the same reason: entries
/// are keyed on resources, so a viewport resize retires every entry naming the old textures and the
/// bound keeps what that leaves behind finite.
/// </para>
/// <para>
/// A cached view aliases its parent's image, so destroying a texture whose views are cached here leaves
/// this holding handles to a dead image. That is the same hazard the OpenGL backend carries, resolved
/// the same way: textures are destroyed through <see cref="IDevice.DeferredDestroy"/> at points where
/// the renderer rebuilds its targets, and eviction clears the rest.
/// </para>
/// </remarks>
public sealed class VulkanAttachmentViewCache : IDisposable
{
    private const int CacheLimit = 128;

    private readonly List<Entry> Entries = [];

    private bool Disposed;

    /// <summary>Gets how many views are currently cached.</summary>
    public int Count => Entries.Count;

    /// <summary>
    /// Gets a texture object addressing exactly one mip level, and one or all array layers, of
    /// <paramref name="texture"/>.
    /// </summary>
    /// <param name="texture">The texture to address into.</param>
    /// <param name="mipLevel">The mip level, relative to <paramref name="texture"/>.</param>
    /// <param name="arrayLayer">The first array layer, relative to <paramref name="texture"/>.</param>
    /// <param name="arrayLayerCount">How many array layers to cover. One for an attachment, which
    /// renders into a single face or slice; the whole count for a layered storage image, which a
    /// compute shader indexes across.</param>
    /// <returns>The texture itself when it already addresses exactly that subresource, otherwise a
    /// cached view of it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="texture"/> is <see langword="null"/>.</exception>
    /// <remarks>Returning the texture itself in the common case matters for more than allocation: a
    /// transition recorded against the returned object is what keeps the tracking table honest, and the
    /// fewer distinct objects address one subresource the less there is to keep in step.</remarks>
    public VulkanTexture Subresource(VulkanTexture texture, int mipLevel, int arrayLayer, int arrayLayerCount = 1)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        ArgumentNullException.ThrowIfNull(texture);

        if (texture.MipLevels == 1 && mipLevel == 0 && arrayLayer == 0 && arrayLayerCount == texture.LayerCount)
        {
            return texture;
        }

        foreach (var entry in Entries)
        {
            if (ReferenceEquals(entry.Source, texture)
                && entry.MipLevel == mipLevel
                && entry.ArrayLayer == arrayLayer
                && entry.ArrayLayerCount == arrayLayerCount)
            {
                return entry.View;
            }
        }

        var view = (VulkanTexture)texture.CreateView(mipLevel, 1, arrayLayer, arrayLayerCount);

        if (Entries.Count >= CacheLimit)
        {
            Entries[0].View.Dispose();
            Entries.RemoveAt(0);
        }

        Entries.Add(new Entry(texture, mipLevel, arrayLayer, arrayLayerCount, view));
        return view;
    }

    /// <summary>Destroys every cached view.</summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        foreach (var entry in Entries)
        {
            entry.View.Dispose();
        }

        Entries.Clear();
    }

    private readonly record struct Entry(
        VulkanTexture Source,
        int MipLevel,
        int ArrayLayer,
        int ArrayLayerCount,
        VulkanTexture View);
}
