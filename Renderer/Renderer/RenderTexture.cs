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

        /// <summary>
        /// Moves every mip level and layer of this texture into <paramref name="state"/>, so it can be used
        /// for something other than being copied into.
        /// </summary>
        /// <param name="state">The state to move to, normally <see cref="ResourceState.ShaderRead"/>.</param>
        /// <param name="expectedCurrentState">What the caller believes the current state is, checked by an
        /// assertion in debug builds, or <see langword="null"/> to assert nothing.</param>
        /// <remarks>
        /// <para>
        /// <b>Every uploaded texture needs this.</b> <see cref="IDevice.UploadTexture"/> leaves its
        /// destination in <see cref="ResourceState.CopyDestination"/> and deliberately does not guess what
        /// comes next, because the guess would be wrong for every storage image and render target. A
        /// texture still in <see cref="ResourceState.CopyDestination"/> cannot be sampled.
        /// </para>
        /// <para>
        /// <b>Why the barrier goes on the upload path's command buffer.</b> A transition has to be
        /// recorded somewhere, and uploads happen at load time outside any frame. Taking a command list
        /// through <see cref="IDevice.BeginCommandList"/> and submitting it here would signal the frame
        /// timeline from outside a frame, which is what the upload context exists to avoid: it owns a pool
        /// and a fence of its own precisely so load-time work does not touch the frame ring. So this rides
        /// the same batch as the staging copy that just ran, and is flushed with it when the next frame
        /// opens. No extra submission, and the barrier cannot be separated from the copy it publishes.
        /// </para>
        /// <para>
        /// This is not the inference the upload path refuses to make. The upload does not guess; the caller
        /// states what the texture is for, which is the division the contract asks for.
        /// </para>
        /// <para>
        /// Redundant calls are free. The backend transitions from its own tracked state and returns without
        /// emitting anything when the texture is already there, so a defensive call before use costs
        /// nothing and is better than reasoning about whether one is needed.
        /// </para>
        /// </remarks>
        public void TransitionTo(ResourceState state, ResourceState? expectedCurrentState = null)
        {
            // OpenGL has no image layouts, and its command list drops the barriers that would express one,
            // so there is nothing to record. The call still belongs at the call site: it is what makes the
            // same code correct on Vulkan.
            // Unwrapped, because the device a caller holds may be a decorator forwarding to the real one:
            // the golden suite's census is exactly that, and without this the transition silently does
            // nothing under it while working everywhere else.
            if (RendererDevice.Unwrap(Device) is not RHI.Vulkan.VulkanDevice vulkanDevice
                || rhiTexture is not RHI.Vulkan.VulkanTexture vulkanTexture)
            {
                return;
            }

            vulkanTexture.TransitionTo(vulkanDevice.Uploads.BeginBatch(), state, expectedCurrentState);
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

        // OpenGL's own defaults for a freshly created texture object, so a texture nobody configures
        // samples through RhiSampler exactly as it does through sampler 0 on the OpenGL path.
        private SamplerDesc samplerDesc = new(
            MinFilter: FilterMode.Nearest,
            MagFilter: FilterMode.Linear,
            MipFilter: MipFilterMode.Linear,
            AddressU: AddressMode.Repeat,
            AddressV: AddressMode.Repeat,
            AddressW: AddressMode.Repeat);

        private ISampler? rhiSampler;

        /// <summary>
        /// Gets the sampler state this texture has been given, as the RHI models it.
        /// </summary>
        /// <remarks>Accumulated from every <see cref="SetParameter(TextureParameterName, int)"/>,
        /// <see cref="SetFiltering"/> and <see cref="SetWrapMode"/> call, on every backend. Starts at
        /// OpenGL's texture object defaults so that a texture nobody configures still describes itself
        /// correctly.</remarks>
        public SamplerDesc RhiSamplerDesc => samplerDesc;

        /// <summary>
        /// Gets a sampler carrying this texture's filtering, wrapping and comparison state, for
        /// <see cref="ICommandList.BindTexture"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is what makes a texture sample correctly on Vulkan.</b> OpenGL is the odd one out in
        /// letting a texture object carry filtering and wrap state, and the renderer leans on that: it
        /// binds sampler object 0 nearly everywhere, which tells OpenGL to defer to those parameters.
        /// Vulkan has no such fallback, so a texture bound with no sampler samples with defaults no matter
        /// what the material asked for &#8212; silently, and looking exactly like a content bug.
        /// </para>
        /// <para>
        /// Created once and cached, and thrown away whenever the state changes, so a caller that
        /// configures a texture after binding it still gets a sampler that agrees.
        /// </para>
        /// </remarks>
        public ISampler RhiSampler => rhiSampler ??= CreateSampler();

        /// <summary>
        /// Gets the sampler to pass to <see cref="ICommandList.BindTexture"/> for this texture, which is
        /// <see langword="null"/> on OpenGL and <see cref="RhiSampler"/> everywhere else.
        /// </summary>
        /// <param name="device">The device the bind is being recorded on.</param>
        /// <returns>The sampler to bind, or <see langword="null"/> to leave the unit on sampler 0.</returns>
        /// <remarks>
        /// <para>
        /// <b>Both answers produce the same filtering.</b> On OpenGL a null sampler binds sampler object 0,
        /// which is the instruction to use the parameters on the texture object &#8212; and those are kept in
        /// step with <see cref="RhiSamplerDesc"/> by <see cref="SetParameter(TextureParameterName, int)"/>.
        /// On Vulkan there is no such fallback, so the sampler has to be passed or the texture samples with
        /// device defaults.
        /// </para>
        /// <para>
        /// <b>Binding a real sampler on OpenGL is not harmless, which is why this exists.</b> A sampler
        /// binding belongs to the texture unit and persists until something overwrites it. The renderer
        /// still has passes that bind textures with raw <c>glBindTextureUnit</c> and never touch
        /// <c>glBindSampler</c> &#8212; the post-process chain among them &#8212; so a sampler left on a
        /// unit by an earlier recorded bind is still there when they sample through it, and they get the
        /// wrong filtering. That is a real regression the golden suite catches, in a scene unrelated to the
        /// bind that caused it. Binding samplers on OpenGL only becomes safe once every bind site records
        /// through a command list.
        /// </para>
        /// </remarks>
        public ISampler? SamplerFor(IDevice? device)
            => RendererDevice.IsOpenGL(device ?? explicitDevice) ? null : RhiSampler;

        private ISampler CreateSampler()
        {
            var device = Device;
            var name = $"{Name ?? "Texture"} sampler";

            // GLSampler directly when there is no device, for the same reason the rest of this type has a
            // legacy path: the renderer runs before the presentation layer brings a device up.
            return device is not null
                ? device.CreateSampler(samplerDesc)
                : new GLSampler(samplerDesc, name);
        }

        private void UpdateSampler(SamplerDesc updated)
        {
            if (updated == samplerDesc)
            {
                return;
            }

            samplerDesc = updated;

            // Dropped rather than mutated: a sampler is immutable once created on both backends, and a
            // stale one would describe the state this texture had when it was first bound.
            if (rhiSampler is not null)
            {
                Device?.DeferredDestroy(rhiSampler);
                rhiSampler = null;
            }
        }

        /// <summary>Sets a single integer texture parameter.</summary>
        /// <param name="parameter">The parameter name to set.</param>
        /// <param name="value">The integer value to assign.</param>
        /// <remarks>
        /// <para>
        /// The single funnel every filtering and wrap call in the renderer passes through, which is why
        /// the translation into <see cref="RhiSamplerDesc"/> lives here: it catches the twenty-odd call
        /// sites spread across the post-process chain, the world loader and the GUI without any of them
        /// having to change.
        /// </para>
        /// <para>
        /// The state is recorded on every backend and the OpenGL call is made only on OpenGL, where it
        /// stays load-bearing: the renderer binds sampler 0 nearly everywhere, and sampler 0 defers to
        /// these parameters. Removing them would change what every material samples.
        /// </para>
        /// </remarks>
        public void SetParameter(TextureParameterName parameter, int value)
        {
            RecordSamplerState(parameter, value);

            if (RendererDevice.IsOpenGL(explicitDevice))
            {
                GL.TextureParameter(Handle, parameter, value);
            }
        }

        /// <summary>Sets a single floating point texture parameter.</summary>
        /// <param name="parameter">The parameter name to set.</param>
        /// <param name="value">The value to assign.</param>
        /// <remarks>Anisotropy is the only one of these the renderer sets, and it is sampler state like
        /// the rest, so it is recorded into <see cref="RhiSamplerDesc"/> too.</remarks>
        public void SetParameter(TextureParameterName parameter, float value)
        {
            if (parameter == (TextureParameterName)ExtTextureFilterAnisotropic.TextureMaxAnisotropyExt)
            {
                UpdateSampler(samplerDesc with { MaxAnisotropy = value });
            }

            if (RendererDevice.IsOpenGL(explicitDevice))
            {
                GL.TextureParameter(Handle, parameter, value);
            }
        }

        private void RecordSamplerState(TextureParameterName parameter, int value)
        {
            switch (parameter)
            {
                case TextureParameterName.TextureWrapS:
                    UpdateSampler(samplerDesc with { AddressU = ToAddressMode(value) });
                    break;

                case TextureParameterName.TextureWrapT:
                    UpdateSampler(samplerDesc with { AddressV = ToAddressMode(value) });
                    break;

                case TextureParameterName.TextureWrapR:
                    UpdateSampler(samplerDesc with { AddressW = ToAddressMode(value) });
                    break;

                case TextureParameterName.TextureMinFilter:
                    var (min, mip) = ToMinFilter(value);
                    UpdateSampler(samplerDesc with { MinFilter = min, MipFilter = mip });
                    break;

                case TextureParameterName.TextureMagFilter:
                    UpdateSampler(samplerDesc with { MagFilter = (TextureMagFilter)value == TextureMagFilter.Nearest ? FilterMode.Nearest : FilterMode.Linear });
                    break;

                case TextureParameterName.TextureCompareMode:
                    // Turning comparison off clears the function; turning it on without one yet gets the
                    // OpenGL default, which is Lequal.
                    UpdateSampler(samplerDesc with
                    {
                        CompareOp = (TextureCompareMode)value == TextureCompareMode.None
                            ? null
                            : samplerDesc.CompareOp ?? Comparison.LessEqual,
                    });
                    break;

                case TextureParameterName.TextureCompareFunc:
                    UpdateSampler(samplerDesc with { CompareOp = ToComparison(value) });
                    break;

                // TextureBaseLevel and TextureMaxLevel are deliberately not sampler state. A mip range is
                // expressed as an image view in the RHI -- ITexture.CreateView(baseMip, mipCount, ...) --
                // because that is what Vulkan has; SamplerDesc carries no LOD clamp to put them in.
                default:
                    break;
            }
        }

        private static AddressMode ToAddressMode(int wrap) => (TextureWrapMode)wrap switch
        {
            TextureWrapMode.MirroredRepeat => AddressMode.MirroredRepeat,
            TextureWrapMode.ClampToEdge => AddressMode.ClampToEdge,
            TextureWrapMode.ClampToBorder => AddressMode.ClampToBorder,
            _ => AddressMode.Repeat,
        };

        private static (FilterMode Min, MipFilterMode Mip) ToMinFilter(int filter) => (TextureMinFilter)filter switch
        {
            TextureMinFilter.Nearest => (FilterMode.Nearest, MipFilterMode.None),
            TextureMinFilter.Linear => (FilterMode.Linear, MipFilterMode.None),
            TextureMinFilter.NearestMipmapNearest => (FilterMode.Nearest, MipFilterMode.Nearest),
            TextureMinFilter.LinearMipmapNearest => (FilterMode.Linear, MipFilterMode.Nearest),
            TextureMinFilter.NearestMipmapLinear => (FilterMode.Nearest, MipFilterMode.Linear),
            TextureMinFilter.LinearMipmapLinear => (FilterMode.Linear, MipFilterMode.Linear),
            _ => (FilterMode.Linear, MipFilterMode.Linear),
        };

        // The inverse of the table GLSampler applies, so a round trip through both is the identity.
        private static Comparison ToComparison(int func) => (DepthFunction)func switch
        {
            DepthFunction.Never => Comparison.Never,
            DepthFunction.Less => Comparison.Less,
            DepthFunction.Equal => Comparison.Equal,
            DepthFunction.Lequal => Comparison.LessEqual,
            DepthFunction.Greater => Comparison.Greater,
            DepthFunction.Notequal => Comparison.NotEqual,
            DepthFunction.Gequal => Comparison.GreaterEqual,
            _ => Comparison.Always,
        };

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
            if (rhiSampler is not null)
            {
                var samplerDevice = Device;

                if (samplerDevice is not null)
                {
                    samplerDevice.DeferredDestroy(rhiSampler);
                }
                else
                {
                    rhiSampler.Dispose();
                }

                rhiSampler = null;
            }

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
