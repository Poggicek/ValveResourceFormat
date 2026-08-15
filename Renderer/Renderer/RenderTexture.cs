using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.OpenGL;
using ValveResourceFormat.ResourceTypes;

namespace ValveResourceFormat.Renderer
{
    /// <summary>
    /// OpenGL texture object with metadata for dimensions and filtering configuration.
    /// </summary>
    [DebuggerDisplay("{Width}x{Height}x{Depth} mip:{NumMipLevels} ({Target})")]
    public class RenderTexture
    {
        /// <summary>Gets the OpenGL texture target (e.g. Texture2D, TextureCubeMap).</summary>
        public TextureTarget Target { get; }

        /// <summary>Gets the OpenGL texture object handle, or 0 once <see cref="Delete"/> has been called
        /// or when the device is not an OpenGL one.</summary>
        /// <remarks>Derived from <see cref="RhiTexture"/> when the storage was allocated through a device.
        /// A texture's identity is the <see cref="ITexture"/>; this is the OpenGL name inside it, kept
        /// because the renderer still has direct GL call sites that need one.</remarks>
        public int Handle => rhiTexture is GLTexture gl ? gl.Handle : legacyHandle;

        private int legacyHandle;

        /// <summary>Gets optional spritesheet layout data when the texture is a sprite atlas.</summary>
        public Texture.SpritesheetData? SpriteSheetData { get; }

        /// <summary>Gets the width of the texture in texels.</summary>
        public int Width { get; }

        /// <summary>Gets the height of the texture in texels.</summary>
        public int Height { get; }

        /// <summary>Gets the depth of the texture (number of slices for 3D or array textures).</summary>
        public int Depth { get; }

        /// <summary>Gets the number of mip levels.</summary>
        public int NumMipLevels { get; private set; }

        /// <summary>Gets the average color reflectivity used for environment lighting calculations.</summary>
        public Vector4 Reflectivity { get; internal set; }

        /// <summary>
        /// Gets the baked radiance of each cube map in this array as an L2 spherical harmonic,
        /// 9 coefficients per channel stored planar, 27 per cube map. Null unless the source
        /// texture carried them.
        /// </summary>
        public float[]? RadianceCoefficients { get; }

        /// <summary>
        /// Gets or sets the RHI format of this texture's storage, or <see cref="RhiFormat.Undefined"/>
        /// when it was allocated through a path that never recorded one.
        /// </summary>
        /// <remarks>Set by the allocating call site, which is the only place that knows it.
        /// <see cref="RhiTexture"/> carries it through; nothing else reads it, so leaving it undefined
        /// only costs the ability to upload to or create a view of this texture through the RHI.</remarks>
        public RhiFormat RhiFormat { get; set; }

        private ITexture? rhiTexture;
        private readonly IDevice? explicitDevice;
        private bool ownsLegacyHandle;

        /// <summary>Gets the device this texture was allocated through, or <see langword="null"/> when it
        /// came from the legacy direct-OpenGL path.</summary>
        private IDevice? Device => RendererDevice.Resolve(explicitDevice);

        /// <summary>
        /// Gets this texture as an <see cref="ITexture"/>, so it can be passed to
        /// <see cref="ICommandList.BindTexture"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// When the texture was created with a known <see cref="RhiFormat"/> this <i>is</i> the texture:
        /// the device allocated it and <see cref="Delete"/> destroys it through the device.
        /// </para>
        /// <para>
        /// The remaining constructors take a target and an extent but no format, and cannot allocate:
        /// <see cref="IDevice.CreateTexture"/> needs the format up front, whereas OpenGL is happy to hand
        /// out a name and be told the format later by whoever calls <c>glTextureStorage</c>. Those
        /// textures keep a bare OpenGL name and this wraps it, exactly as before. Every such call site has
        /// to move to a format-carrying constructor before it can run on Vulkan.
        /// </para>
        /// </remarks>
        public ITexture RhiTexture
        {
            get
            {
                if (rhiTexture is null || (rhiTexture is GLTexture gl && gl.Handle != Handle))
                {
                    // Permissive usage on purpose: OpenGL ignores it at creation, and the only thing that
                    // reads it is barrier translation, where a superset is the conservative answer.
                    rhiTexture = GLTexture.Wrap(legacyHandle, Target, Describe(RhiFormat, LegacyUsage));
                }

                return rhiTexture;
            }
        }

        private const TextureUsage LegacyUsage =
            TextureUsage.Sampled | TextureUsage.Storage | TextureUsage.CopySource | TextureUsage.CopyDestination;

        private TextureDesc Describe(RhiFormat format, TextureUsage usage) => new(
            Math.Max(Width, 1),
            Math.Max(Height, 1),
            format,
            usage,
            Name ?? string.Empty,
            Math.Max(Depth, 1),
            Math.Max(NumMipLevels, 1),
            1,
            GLTexture.ToDimension(Target));

        /// <summary>Gets or sets the debug label last assigned through <see cref="SetLabel"/>.</summary>
        public string? Name { get; private set; }

        RenderTexture(TextureTarget target)
        {
            Target = target;
            GL.CreateTextures(target, 1, out legacyHandle);
            ownsLegacyHandle = true;
        }

        /// <summary>
        /// Creates a texture and allocates its storage through the device.
        /// </summary>
        /// <param name="target">OpenGL texture target, which fixes the shape.</param>
        /// <param name="format">Pixel format. Storage is allocated for it immediately.</param>
        /// <param name="width">Width in texels.</param>
        /// <param name="height">Height in texels.</param>
        /// <param name="depth">Volume depth, or array layer count, or 1.</param>
        /// <param name="mipCount">Number of mip levels.</param>
        /// <param name="name">Debug name, surfaced to graphics debuggers.</param>
        /// <param name="usage">Every use the texture will be put to.</param>
        /// <param name="device">The device to allocate through, or <see langword="null"/> to resolve one
        /// from <see cref="RendererDevice"/>.</param>
        /// <remarks>This is the constructor that works on both backends. Allocation is one step here
        /// because that is the only shape a <c>VkImage</c> has; the older two-step form, where a name is
        /// created and <c>glTextureStorage</c> gives it a format later, has no Vulkan equivalent.</remarks>
        public RenderTexture(
            TextureTarget target,
            RhiFormat format,
            int width,
            int height,
            int depth,
            int mipCount,
            string name,
            TextureUsage usage = LegacyUsage,
            IDevice? device = null)
        {
            Target = target;
            Width = width;
            Height = height;
            Depth = depth;
            NumMipLevels = mipCount;
            RhiFormat = format;
            Name = name;
            explicitDevice = device;

            var resolved = Device;

            if (resolved is null)
            {
                // No device yet: the legacy OpenGL path, which is what the renderer ran on before the RHI
                // existed and what tooling without a presentation layer still runs on.
                GL.CreateTextures(target, 1, out legacyHandle);
                ownsLegacyHandle = true;
                AllocateLegacyStorage();
                return;
            }

            rhiTexture = resolved.CreateTexture(Describe(format, usage));
        }

        /// <summary>
        /// Creates a texture from a source resource, allocating storage that may be smaller than the
        /// source.
        /// </summary>
        /// <param name="target">OpenGL texture target.</param>
        /// <param name="storage">The storage to allocate: the format, extent and mip count actually
        /// created.</param>
        /// <param name="source">The source resource, supplying spritesheet, reflectivity and radiance
        /// metadata and the dimensions this texture reports.</param>
        /// <param name="device">The device to allocate through, or <see langword="null"/> to resolve one
        /// from <see cref="RendererDevice"/>.</param>
        /// <remarks>
        /// <see cref="Width"/>, <see cref="Height"/> and <see cref="NumMipLevels"/> report the
        /// <paramref name="source"/> dimensions while the storage is <paramref name="storage"/>, and the
        /// two differ whenever the top mip levels were dropped to respect
        /// <see cref="RendererContext.MaxTextureSize"/>. That split is long-standing behaviour, kept
        /// deliberately: callers read these to reason about the asset, while the device has to be told
        /// what was really allocated.
        /// </remarks>
        public RenderTexture(TextureTarget target, in TextureDesc storage, Texture source, IDevice? device = null)
        {
            ArgumentNullException.ThrowIfNull(source);

            Target = target;
            RhiFormat = storage.Format;
            Name = storage.Name;
            explicitDevice = device;

            Width = source.Width;
            Height = source.Height;
            Depth = source.Depth;
            NumMipLevels = source.NumMipLevels;
            SpriteSheetData = source.GetSpriteSheetData();
            Reflectivity = source.Reflectivity;
            RadianceCoefficients = source.RadianceCoefficients;

            var resolved = Device;

            if (resolved is null)
            {
                GL.CreateTextures(target, 1, out legacyHandle);
                ownsLegacyHandle = true;
                AllocateLegacyStorage(in storage);
                return;
            }

            rhiTexture = resolved.CreateTexture(in storage);
        }

        private void AllocateLegacyStorage() => AllocateLegacyStorage(Describe(RhiFormat, LegacyUsage));

        private void AllocateLegacyStorage(in TextureDesc storage)
        {
            var internalFormat = FormatTables.ToGLSizedInternalFormat(storage.Format);

            switch (Target)
            {
                // glTextureStorage2D allocates all six faces of a cube map, so only an array of them needs
                // the 3D entry point.
                case TextureTarget.Texture3D:
                case TextureTarget.Texture2DArray:
                case TextureTarget.TextureCubeMapArray:
                    GL.TextureStorage3D(legacyHandle, storage.MipLevels, internalFormat, storage.Width, storage.Height, storage.Depth);
                    break;

                case TextureTarget.Texture1D:
                    GL.TextureStorage1D(legacyHandle, storage.MipLevels, internalFormat, storage.Width);
                    break;

                default:
                    GL.TextureStorage2D(legacyHandle, storage.MipLevels, internalFormat, storage.Width, storage.Height);
                    break;
            }
        }

        /// <summary>Creates a render texture and populates metadata from the given source texture resource.</summary>
        /// <param name="target">OpenGL texture target.</param>
        /// <param name="data">Source texture resource providing dimensions, mip count, spritesheet data and radiance harmonics.</param>
        public RenderTexture(TextureTarget target, Texture data) : this(target)
        {
            Width = data.Width;
            Height = data.Height;
            Depth = data.Depth;
            NumMipLevels = data.NumMipLevels;
            SpriteSheetData = data.GetSpriteSheetData();
            Reflectivity = data.Reflectivity;
            RadianceCoefficients = data.RadianceCoefficients;
        }

        /// <summary>Creates a render texture with explicit dimension and mip level metadata.</summary>
        /// <param name="target">OpenGL texture target.</param>
        /// <param name="width">Width in texels.</param>
        /// <param name="height">Height in texels.</param>
        /// <param name="depth">Depth or array layer count.</param>
        /// <param name="mipcount">Number of mip levels.</param>
        public RenderTexture(TextureTarget target, int width, int height, int depth, int mipcount)
            : this(target)
        {
            Width = width;
            Height = height;
            Depth = depth;
            NumMipLevels = mipcount;
        }

        /// <summary>Wraps an existing OpenGL texture handle without taking ownership of its storage.</summary>
        /// <param name="handle">Existing OpenGL texture handle.</param>
        /// <param name="target">OpenGL texture target.</param>
        public RenderTexture(int handle, TextureTarget target)
        {
            legacyHandle = handle;
            Target = target;
        }

        /// <summary>Wraps a texture the device already created.</summary>
        /// <param name="texture">The texture to wrap. This instance does not take ownership of it.</param>
        /// <param name="target">The OpenGL target it corresponds to, for the direct GL call sites.</param>
        /// <remarks>The route for anything allocated through <see cref="IDevice.CreateTexture"/> elsewhere,
        /// such as a render target, that still has to be handed to renderer code expecting a
        /// <see cref="RenderTexture"/>.</remarks>
        public RenderTexture(ITexture texture, TextureTarget target)
        {
            ArgumentNullException.ThrowIfNull(texture);

            rhiTexture = texture;
            Target = target;
            Width = texture.Width;
            Height = texture.Height;
            Depth = texture.Depth;
            NumMipLevels = texture.MipLevels;
            RhiFormat = texture.Format;
            Name = texture.Name;
        }

        /// <summary>Creates a 2D texture with immutable storage, optionally allocating a reduced mip chain sized by <see cref="MaxMipCount"/>.</summary>
        /// <param name="width">Texture width in texels.</param>
        /// <param name="height">Texture height in texels.</param>
        /// <param name="format">Internal pixel format.</param>
        /// <param name="mips">When <see langword="true"/>, allocates a reduced mip chain (see <see cref="MaxMipCount"/>) rather than a single level.</param>
        /// <returns>The newly created render texture.</returns>
        public static RenderTexture Create(int width, int height, SizedInternalFormat format = SizedInternalFormat.Rgba8, bool mips = false)
        {
            var mipCount = mips
                ? MaxMipCount(width, height)
                : 1;

            var texture = new RenderTexture(TextureTarget.Texture2D, width, height, 1, mipCount);
            GL.TextureStorage2D(texture.Handle, mipCount, format, width, height);
            return texture;
        }

        /// <summary>Creates a 2D texture with immutable storage and an explicit mip count.</summary>
        /// <param name="width">Texture width in texels.</param>
        /// <param name="height">Texture height in texels.</param>
        /// <param name="format">Internal pixel format.</param>
        /// <param name="mipCount">Number of mip levels to allocate.</param>
        /// <returns>The newly created render texture.</returns>
        public static RenderTexture Create(int width, int height, SizedInternalFormat format, int mipCount)
        {
            var texture = new RenderTexture(TextureTarget.Texture2D, width, height, 1, mipCount);
            GL.TextureStorage2D(texture.Handle, mipCount, format, width, height);
            return texture;
        }

        /// <summary>Creates a 2D texture with immutable storage, in an RHI format.</summary>
        /// <param name="width">Texture width in texels.</param>
        /// <param name="height">Texture height in texels.</param>
        /// <param name="format">Pixel format, translated through <see cref="FormatTables"/>.</param>
        /// <param name="mips">When <see langword="true"/>, allocates a reduced mip chain (see <see cref="MaxMipCount"/>) rather than a single level.</param>
        /// <returns>The newly created render texture, with <see cref="RhiFormat"/> recorded.</returns>
        /// <remarks>Prefer this over the <see cref="SizedInternalFormat"/> overloads: it records the
        /// format, which is what lets <see cref="RhiTexture"/> describe the texture completely.</remarks>
        public static RenderTexture Create(int width, int height, RhiFormat format, bool mips = false)
            => Create(width, height, format, mips ? MaxMipCount(width, height) : 1);

        /// <summary>Creates a 2D texture with immutable storage and an explicit mip count, in an RHI format.</summary>
        /// <param name="width">Texture width in texels.</param>
        /// <param name="height">Texture height in texels.</param>
        /// <param name="format">Pixel format, translated through <see cref="FormatTables"/>.</param>
        /// <param name="mipCount">Number of mip levels to allocate.</param>
        /// <returns>The newly created render texture, with <see cref="RhiFormat"/> recorded.</returns>
        public static RenderTexture Create(int width, int height, RhiFormat format, int mipCount)
            => new(TextureTarget.Texture2D, format, width, height, 1, mipCount, $"{format} {width}x{height}");

        /// <summary>Creates a texture view that reinterprets a subrange of this texture's storage.</summary>
        /// <param name="internalFormat">The reinterpreted pixel format for the view.</param>
        /// <param name="minLevel">First mip level visible through the view.</param>
        /// <param name="numLevels">Number of mip levels visible through the view.</param>
        /// <param name="minLayer">First array layer visible through the view.</param>
        /// <param name="numLayers">Number of array layers visible through the view.</param>
        /// <returns>A new <see cref="RenderTexture"/> wrapping the view.</returns>
        public RenderTexture CreateView(PixelInternalFormat internalFormat, int minLevel = 0, int numLevels = 1, int minLayer = 0, int numLayers = 1)
        {
            var view = new RenderTexture(GL.GenTexture(), Target);
            GL.TextureView(view.Handle, Target, Handle, internalFormat, minLevel, numLevels, minLayer, numLayers);
            return view;
        }

        /// <summary>Creates a view onto a subset of this texture's mips and layers, through the device.</summary>
        /// <param name="baseMipLevel">First mip level in the view.</param>
        /// <param name="mipLevelCount">Number of mip levels in the view.</param>
        /// <param name="baseArrayLayer">First array layer in the view.</param>
        /// <param name="arrayLayerCount">Number of array layers in the view.</param>
        /// <param name="format">Format to reinterpret as, or <see cref="RhiFormat.Undefined"/> to keep this one's.</param>
        /// <param name="aspect">Which aspect to address. Only meaningful for depth-stencil formats.</param>
        /// <returns>A new <see cref="RenderTexture"/> over the view, which must be deleted before this one.</returns>
        /// <remarks>The replacement for the <see cref="PixelInternalFormat"/> overload. It carries the
        /// aspect, which is what a sampleable stencil view needs and what <c>glTextureView</c> alone cannot
        /// express.</remarks>
        public RenderTexture CreateView(
            int baseMipLevel,
            int mipLevelCount,
            int baseArrayLayer,
            int arrayLayerCount,
            RhiFormat format = RhiFormat.Undefined,
            TextureAspect aspect = TextureAspect.All)
        {
            var view = RhiTexture.CreateView(baseMipLevel, mipLevelCount, baseArrayLayer, arrayLayerCount, format, aspect);

            return new RenderTexture(view, Target)
            {
                ownsLegacyHandle = false,
            };
        }

        /// <summary>Uploads one mip level of one array layer or cube face.</summary>
        /// <param name="mipLevel">Mip level to write.</param>
        /// <param name="arrayLayer">Array layer or cube face to write.</param>
        /// <param name="data">Texel data, or block data for a compressed format.</param>
        /// <remarks>Goes through <see cref="IDevice.UploadTexture"/>, which stages the copy. On Vulkan the
        /// texture is left in <see cref="ResourceState.CopyDestination"/> and needs an explicit transition
        /// before it is sampled; that is deliberate, because guessing would be wrong for every storage
        /// image and render target.</remarks>
        public void Upload(int mipLevel, int arrayLayer, ReadOnlySpan<byte> data)
        {
            var device = Device;

            if (device is not null && rhiTexture is not null)
            {
                device.UploadTexture(rhiTexture, mipLevel, arrayLayer, data);
                return;
            }

            ((GLTexture)RhiTexture).Upload(mipLevel, arrayLayer, data);
        }

        /// <summary>Sets the wrap mode for all relevant texture dimensions.</summary>
        /// <param name="wrap">The wrap mode to apply.</param>
        public void SetWrapMode(TextureWrapMode wrap)
        {
            SetParameter(TextureParameterName.TextureWrapS, (int)wrap);

            if (Height > 1)
            {
                SetParameter(TextureParameterName.TextureWrapT, (int)wrap);
            }

            if (Depth > 1)
            {
                SetParameter(TextureParameterName.TextureWrapR, (int)wrap);
            }
        }

        /// <summary>Sets the minification and magnification filters.</summary>
        /// <param name="min">Minification filter.</param>
        /// <param name="mag">Magnification filter.</param>
        public void SetFiltering(TextureMinFilter min, TextureMagFilter mag)
        {
            SetParameter(TextureParameterName.TextureMinFilter, (int)min);
            SetParameter(TextureParameterName.TextureMagFilter, (int)mag);
        }

        /// <summary>Sets the base and maximum mip level accessible through this texture.</summary>
        /// <param name="baseLevel">Lowest mip level index.</param>
        /// <param name="maxLevel">Highest mip level index.</param>
        public void SetBaseMaxLevel(int baseLevel, int maxLevel)
        {
            SetParameter(TextureParameterName.TextureBaseLevel, baseLevel);
            SetParameter(TextureParameterName.TextureMaxLevel, maxLevel);
        }

        /// <summary>Sets a single integer texture parameter.</summary>
        /// <param name="parameter">The parameter name to set.</param>
        /// <param name="value">The integer value to assign.</param>
        /// <remarks>
        /// <b>Not ported, and does nothing on a non-OpenGL device.</b> Filtering, wrapping and mip
        /// clamping are properties of a sampler in the RHI, not of a texture: OpenGL is the odd one out in
        /// letting a texture object carry them. The replacement is a <see cref="SamplerDesc"/> passed to
        /// <see cref="IDevice.CreateSampler"/> and bound alongside the texture.
        /// <para>
        /// The OpenGL path must keep setting these, because the renderer binds sampler 0 for most textures
        /// and sampler 0 means "use the texture object's own parameters". Removing them would change what
        /// every material samples, so they stay until the call sites carry samplers.
        /// </para>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetParameter(TextureParameterName parameter, int value)
        {
            if (!RendererDevice.IsOpenGL(explicitDevice))
            {
                return;
            }

            GL.TextureParameter(Handle, parameter, value);
        }

        /// <summary>Assigns a debug label to the OpenGL texture object.</summary>
        /// <param name="label">Label string visible in graphics debuggers.</param>
        /// <remarks>On a device-allocated texture the name was already given to
        /// <see cref="IDevice.CreateTexture"/> and both backends applied it; this relabels the OpenGL
        /// object for the call sites that name a texture only after creating it.</remarks>
        public void SetLabel(string label)
        {
            Name = label;

            if (!RendererDevice.IsOpenGL(explicitDevice))
            {
                return;
            }

            GL.ObjectLabel(ObjectLabelIdentifier.Texture, Handle, label.Length, label);
        }

        /// <summary>Destroys this texture through the device that created it.</summary>
        /// <remarks>Device-allocated storage goes through <see cref="IDevice.DeferredDestroy"/>, because a
        /// frame in flight may still be sampling it. A bare OpenGL name from the legacy path is deleted
        /// directly, which is what it always was.</remarks>
        public void Delete()
        {
            if (rhiTexture is not null && !ownsLegacyHandle)
            {
                var device = Device;

                if (device is not null)
                {
                    device.DeferredDestroy(rhiTexture);
                }
                else
                {
                    rhiTexture.Dispose();
                }

                rhiTexture = null;
                return;
            }

            if (legacyHandle != 0)
            {
                GL.DeleteTexture(legacyHandle);
                legacyHandle = 0;
            }

            rhiTexture = null;
        }

        /// <summary>Calculates a reasonable mip count for a texture of the given dimensions.</summary>
        /// <param name="width">Texture width in texels.</param>
        /// <param name="height">Texture height in texels.</param>
        /// <returns>Number of mip levels to use.</returns>
        public static int MaxMipCount(int width, int height)
        {
            return Math.Max((int)MathF.Log(MathF.Max(width, height), 2) - 2, 1);
        }

        /// <summary>Attaches the specified mip level of this texture to a framebuffer attachment point.</summary>
        /// <param name="framebuffer">Target framebuffer.</param>
        /// <param name="attachment">Attachment point (e.g. color attachment 0, depth).</param>
        /// <param name="mipLevel">Mip level to attach.</param>
        public void AttachToFramebuffer(Framebuffer framebuffer, FramebufferAttachment attachment, int mipLevel)
        {
            if (mipLevel < 0 || mipLevel >= NumMipLevels)
            {
                throw new ArgumentOutOfRangeException(nameof(mipLevel), $"Mip level {mipLevel} is out of range for attachment with {NumMipLevels} mips.");
            }

            if (!RendererDevice.IsOpenGL(explicitDevice))
            {
                // There is no framebuffer object to attach to on a Vulkan device. Attachments are named
                // directly in a RenderPassDesc under dynamic rendering, which is Framebuffer's port to make.
                return;
            }

            GL.NamedFramebufferTexture(framebuffer.FboHandle, attachment, Handle, mipLevel);
        }
    }
}
