using OpenTK.Graphics.OpenGL;
using GLApi = OpenTK.Graphics.OpenGL.GL;

namespace ValveResourceFormat.Renderer.RHI.OpenGL;

/// <summary>
/// <see cref="ITexture"/> on OpenGL: one texture object with immutable storage, allocated through the
/// <c>glTextureStorage</c> family.
/// </summary>
/// <remarks>
/// <para>
/// Immutable storage is deliberate and matches what <see cref="Materials.MaterialLoader"/> and
/// <see cref="RenderTexture"/> already allocate. Its shape &#8212; extent, format, mip and layer counts
/// fixed at creation, contents filled in afterwards &#8212; is the shape a <c>VkImage</c> has, so the
/// creation path ports across rather than being rewritten.
/// </para>
/// <para>
/// Every format decision goes through <see cref="FormatTables"/>. Those tables are total and throw on a
/// format they do not know, so an unmappable format fails here at load time instead of rendering black
/// later.
/// </para>
/// </remarks>
public sealed class GLTexture : ITexture
{
    private readonly GLTexture? viewOf;
    private readonly bool ownsHandle = true;

    /// <summary>Gets the OpenGL texture object name, or 0 once disposed.</summary>
    public int Handle { get; private set; }

    /// <summary>Gets the OpenGL texture target this texture was created against.</summary>
    public TextureTarget Target { get; }

    /// <summary>Gets the sized internal format the storage was allocated with. Also the argument
    /// <c>glCompressedTextureSubImage</c> takes in place of a client pixel format.</summary>
    public SizedInternalFormat InternalFormat { get; }

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

    /// <summary>Gets a value indicating whether this texture is a view aliasing another texture's storage.</summary>
    public bool IsView => viewOf is not null;

    /// <summary>Allocates a texture and its complete mip chain.</summary>
    /// <param name="desc">Creation parameters.</param>
    /// <exception cref="ArgumentOutOfRangeException">An extent, mip count or sample count is not positive,
    /// or the format has no OpenGL equivalent.</exception>
    public GLTexture(in TextureDesc desc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.Width, nameof(desc));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.Height, nameof(desc));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.Depth, nameof(desc));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.MipLevels, nameof(desc));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.SampleCount, nameof(desc));

        Name = desc.Name ?? string.Empty;
        Width = desc.Width;
        Height = desc.Height;
        Depth = desc.Depth;
        MipLevels = desc.MipLevels;
        SampleCount = desc.SampleCount;
        Format = desc.Format;
        Dimension = desc.Dimension;
        Usage = desc.Usage;

        Target = ToGLTarget(desc.Dimension, desc.SampleCount);
        InternalFormat = FormatTables.ToGLSizedInternalFormat(desc.Format);

        GLApi.CreateTextures(Target, 1, out int handle);
        Handle = handle;

        AllocateStorage();
        SetLabel(Handle, Name);
    }

    private GLTexture(GLTexture parent, int handle, TextureTarget target, TextureDimension dimension, SizedInternalFormat internalFormat, RhiFormat format, int baseMipLevel, int mipLevelCount, int arrayLayerCount)
    {
        viewOf = parent;
        Handle = handle;
        Target = target;
        Dimension = dimension;
        InternalFormat = internalFormat;
        Format = format;
        Name = $"{parent.Name} (view)";

        Width = Math.Max(1, parent.Width >> baseMipLevel);
        Height = Math.Max(1, parent.Height >> baseMipLevel);
        Depth = dimension == TextureDimension.Texture3D ? Math.Max(1, parent.Depth >> baseMipLevel) : arrayLayerCount;
        MipLevels = mipLevelCount;
        SampleCount = parent.SampleCount;
        Usage = parent.Usage;
    }

    private GLTexture(int handle, TextureTarget target, in TextureDesc desc)
    {
        Handle = handle;
        Target = target;
        ownsHandle = false;

        Name = desc.Name ?? string.Empty;
        Width = desc.Width;
        Height = desc.Height;
        Depth = desc.Depth;
        MipLevels = desc.MipLevels;
        SampleCount = desc.SampleCount;
        Format = desc.Format;
        Dimension = desc.Dimension;
        Usage = desc.Usage;

        // Undefined is legal here and only here: a wrapped handle whose format the renderer never
        // recorded can still be bound and sampled, which is all the bridge is for. Anything that needs a
        // real format (uploads, views) asks FormatTables and fails loudly instead.
        InternalFormat = desc.Format == RhiFormat.Undefined ? 0 : FormatTables.ToGLSizedInternalFormat(desc.Format);
    }

    /// <summary>
    /// Wraps an existing OpenGL texture object as an <see cref="ITexture"/> without taking ownership of it.
    /// </summary>
    /// <param name="handle">The existing texture object name.</param>
    /// <param name="target">The target it was created against.</param>
    /// <param name="desc">Its metadata. <see cref="TextureDesc.Format"/> may be
    /// <see cref="RhiFormat.Undefined"/> when the renderer never recorded one.</param>
    /// <returns>A non-owning view. <see cref="Dispose"/> does not delete the texture.</returns>
    /// <remarks>
    /// The bridge for textures the renderer still creates itself, which is every
    /// <see cref="RenderTexture"/>. It exists so a material or a framebuffer attachment can be handed to
    /// <see cref="ICommandList.BindTexture"/> before its allocation has been ported, and it disappears as
    /// those allocations move onto <see cref="IDevice.CreateTexture"/>. Not a long-term surface: it
    /// cannot exist on Vulkan, where there is no loose handle to adopt.
    /// </remarks>
    public static GLTexture Wrap(int handle, TextureTarget target, in TextureDesc desc)
        => new(handle, target, in desc);

    /// <summary>Gets the texture shape an OpenGL target describes.</summary>
    /// <param name="target">The target to translate.</param>
    /// <returns>The matching <see cref="TextureDimension"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The target is not one the RHI models.</exception>
    public static TextureDimension ToDimension(TextureTarget target) => target switch
    {
        TextureTarget.Texture1D => TextureDimension.Texture1D,
        TextureTarget.Texture2D or TextureTarget.TextureRectangle => TextureDimension.Texture2D,
        TextureTarget.Texture2DMultisample => TextureDimension.Texture2DMultisample,
        TextureTarget.Texture2DArray or TextureTarget.Texture2DMultisampleArray or TextureTarget.Texture1DArray => TextureDimension.Texture2DArray,
        TextureTarget.Texture3D => TextureDimension.Texture3D,
        TextureTarget.TextureCubeMap => TextureDimension.TextureCube,
        TextureTarget.TextureCubeMapArray => TextureDimension.TextureCubeArray,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "No RHI texture dimension for this OpenGL target."),
    };

    /// <summary>Gets a value indicating whether this texture's storage is multisampled.</summary>
    /// <remarks>The dimension says so outright; a sample count above one says so for the shapes that have
    /// no multisample dimension of their own, which is the array case.</remarks>
    public bool IsMultisampled => Dimension == TextureDimension.Texture2DMultisample || SampleCount > 1;

    private void AllocateStorage()
    {
        var layers = GLLayerCount();

        if (IsMultisampled)
        {
            // Multisampled storage has no mip chain, and fixed sample locations are required for a
            // texture that is both rendered into and resolved.
            if (Dimension is TextureDimension.Texture2DArray)
            {
                GLApi.TextureStorage3DMultisample(Handle, SampleCount, InternalFormat, Width, Height, layers, true);
                return;
            }

            GLApi.TextureStorage2DMultisample(Handle, SampleCount, InternalFormat, Width, Height, true);
            return;
        }

        switch (Dimension)
        {
            case TextureDimension.Texture1D:
                GLApi.TextureStorage1D(Handle, MipLevels, InternalFormat, Width);
                break;

            // glTextureStorage2D allocates all six faces of a cube map, so a single cube is a 2D
            // allocation and only an array of them needs the 3D entry point.
            case TextureDimension.Texture2D:
            case TextureDimension.TextureCube:
                GLApi.TextureStorage2D(Handle, MipLevels, InternalFormat, Width, Height);
                break;

            case TextureDimension.Texture2DArray:
            case TextureDimension.Texture3D:
            case TextureDimension.TextureCubeArray:
                GLApi.TextureStorage3D(Handle, MipLevels, InternalFormat, Width, Height, layers);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(Dimension), Dimension, "Unknown texture dimension.");
        }
    }

    /// <summary>Gets the number of layers OpenGL sees, which counts each cube face separately.</summary>
    /// <returns>The layer count for the 3D storage and upload entry points.</returns>
    public int GLLayerCount() => Dimension switch
    {
        TextureDimension.TextureCube => 6,
        TextureDimension.TextureCubeArray => Depth * 6,
        TextureDimension.Texture2DArray or TextureDimension.Texture3D => Depth,
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

    /// <summary>Uploads one mip level of one array layer or cube face.</summary>
    /// <param name="mipLevel">Mip level to write.</param>
    /// <param name="arrayLayer">Array layer or cube face to write. A volume texture has only layer 0, whose
    /// upload covers the whole slab.</param>
    /// <param name="data">Texel data, or block data for a compressed format.</param>
    /// <exception cref="ArgumentOutOfRangeException">The mip level or layer is outside the texture.</exception>
    /// <exception cref="InvalidOperationException">The texture is multisampled, which cannot be uploaded to.</exception>
    /// <remarks>Backs <see cref="IDevice.UploadTexture"/>. Compressed formats take the
    /// <c>glCompressedTextureSubImage</c> path with their sized internal format, exactly as the existing
    /// <see cref="Materials.MaterialLoader"/> upload does; <see cref="FormatTables.ToGLPixelFormat"/>
    /// returning <see langword="null"/> is what selects it.</remarks>
    public unsafe void Upload(int mipLevel, int arrayLayer, ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(mipLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(mipLevel, MipLevels);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayLayer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(arrayLayer, GLLayerCount());

        if (IsMultisampled)
        {
            throw new InvalidOperationException($"Texture '{Name}' is multisampled and cannot be uploaded to.");
        }

        if (data.IsEmpty)
        {
            return;
        }

        var (width, height, depth) = MipExtent(mipLevel);
        var clientFormat = FormatTables.ToGLPixelFormat(Format);

        // A volume texture is uploaded whole; every other layered shape addresses one slice at a time.
        var zOffset = Dimension == TextureDimension.Texture3D ? 0 : arrayLayer;
        var uses3D = Dimension is TextureDimension.Texture2DArray or TextureDimension.Texture3D
            or TextureDimension.TextureCube or TextureDimension.TextureCubeArray;

        fixed (byte* source = data)
        {
            var pointer = (IntPtr)source;

            if (clientFormat is null)
            {
                if (uses3D)
                {
                    GLApi.CompressedTextureSubImage3D(Handle, mipLevel, 0, 0, zOffset, width, height, depth, (PixelFormat)InternalFormat, data.Length, pointer);
                }
                else if (Dimension == TextureDimension.Texture1D)
                {
                    GLApi.CompressedTextureSubImage1D(Handle, mipLevel, 0, width, (PixelFormat)InternalFormat, data.Length, pointer);
                }
                else
                {
                    GLApi.CompressedTextureSubImage2D(Handle, mipLevel, 0, 0, width, height, (PixelFormat)InternalFormat, data.Length, pointer);
                }

                return;
            }

            var clientType = FormatTables.ToGLPixelType(Format)!.Value;

            if (uses3D)
            {
                GLApi.TextureSubImage3D(Handle, mipLevel, 0, 0, zOffset, width, height, depth, clientFormat.Value, clientType, pointer);
            }
            else if (Dimension == TextureDimension.Texture1D)
            {
                GLApi.TextureSubImage1D(Handle, mipLevel, 0, width, clientFormat.Value, clientType, pointer);
            }
            else
            {
                GLApi.TextureSubImage2D(Handle, mipLevel, 0, 0, width, height, clientFormat.Value, clientType, pointer);
            }
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">The mip or layer range falls outside this texture.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="aspect"/> is not
    /// <see cref="TextureAspect.All"/> on a format that has only one aspect.</exception>
    /// <remarks>
    /// <para>
    /// <paramref name="baseArrayLayer"/> and <paramref name="arrayLayerCount"/> count cube faces
    /// individually, which is what <c>glTextureView</c> takes: a view of one whole cube is six layers.
    /// </para>
    /// <para>
    /// <paramref name="aspect"/> is applied as <c>GL_DEPTH_STENCIL_TEXTURE_MODE</c> on the view, which is
    /// how <see cref="Framebuffer"/> already builds a sampleable stencil view of its depth attachment. The
    /// mode belongs to the texture object on OpenGL, so it has to be a view and not a sampler setting.
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
        ArgumentOutOfRangeException.ThrowIfGreaterThan(baseArrayLayer + arrayLayerCount, GLLayerCount());

        var viewFormat = format == RhiFormat.Undefined ? Format : format;

        if (aspect != TextureAspect.All && !RhiFormatInfo.IsStencil(viewFormat))
        {
            throw new InvalidOperationException($"Texture '{Name}' is {viewFormat}, which has no {aspect} aspect to view. Only depth-stencil formats have more than one aspect.");
        }

        var viewDimension = ViewDimension(arrayLayerCount);
        var viewTarget = ToGLTarget(viewDimension, SampleCount);
        var viewInternalFormat = FormatTables.ToGLSizedInternalFormat(viewFormat);

        // GenTexture rather than CreateTextures: glTextureView requires a name that has never been bound
        // to a target, which CreateTextures would have done.
        var handle = GLApi.GenTexture();
        GLApi.TextureView(handle, viewTarget, Handle, (PixelInternalFormat)viewInternalFormat, baseMipLevel, mipLevelCount, baseArrayLayer, arrayLayerCount);

        var view = new GLTexture(this, handle, viewTarget, viewDimension, viewInternalFormat, viewFormat, baseMipLevel, mipLevelCount, arrayLayerCount);

        if (aspect != TextureAspect.All)
        {
            var mode = aspect == TextureAspect.Stencil ? DepthStencilTextureMode.StencilIndex : DepthStencilTextureMode.DepthComponent;
            GLApi.TextureParameter(handle, TextureParameterName.DepthStencilTextureMode, (int)mode);
        }

        SetLabel(view.Handle, view.Name);
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

    // Static so that the Release build, where the body compiles away, does not trip CA1822.
    private static void SetLabel(int handle, string name)
    {
#if DEBUG
        if (name.Length > 0)
        {
            GLApi.ObjectLabel(ObjectLabelIdentifier.Texture, handle, name.Length, name);
        }
#endif
    }

    private static TextureTarget ToGLTarget(TextureDimension dimension, int sampleCount) => dimension switch
    {
        TextureDimension.Texture1D => TextureTarget.Texture1D,

        // The dimension decides, not the count: a multisample target carrying one sample is a real shape
        // the renderer allocates, and it has its own target here as it does its own dimension.
        TextureDimension.Texture2DMultisample => TextureTarget.Texture2DMultisample,

        // The count still promotes a plain 2D shape, so a caller that has not moved to the dimension yet
        // keeps working. There is no multisample-array dimension, so an array only has the count.
        TextureDimension.Texture2D => sampleCount > 1 ? TextureTarget.Texture2DMultisample : TextureTarget.Texture2D,
        TextureDimension.Texture2DArray => sampleCount > 1 ? TextureTarget.Texture2DMultisampleArray : TextureTarget.Texture2DArray,
        TextureDimension.Texture3D => TextureTarget.Texture3D,
        TextureDimension.TextureCube => TextureTarget.TextureCubeMap,
        TextureDimension.TextureCubeArray => TextureTarget.TextureCubeMapArray,
        _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "Unknown texture dimension."),
    };

    /// <summary>Deletes the texture object. A view releases only its own name, never its parent's storage,
    /// and a wrapped handle is left alone.</summary>
    public void Dispose()
    {
        if (Handle == 0)
        {
            return;
        }

        if (ownsHandle)
        {
            GLApi.DeleteTexture(Handle);
        }

        Handle = 0;
    }
}
