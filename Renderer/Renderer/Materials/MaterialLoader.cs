using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics;
using System.IO.Hashing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using SkiaSharp;
using ValveResourceFormat.Renderer.RHI.OpenGL;
using ValveResourceFormat.ResourceTypes;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;

namespace ValveResourceFormat.Renderer.Materials
{
    /// <summary>
    /// Loads and caches materials and textures from Source 2 resources.
    /// </summary>
    public class MaterialLoader
    {
        private readonly Dictionary<ulong, RenderMaterial> Materials = [];
        private readonly List<RenderMaterial> OwnedMaterials = [];

        private readonly Dictionary<string, RenderTexture> Textures = [];
        private readonly Dictionary<string, RenderTexture> TexturesSrgb = [];
        private readonly Dictionary<(int AddressU, int AddressV, bool AnisotropicFiltering), int> Samplers = [];
        private readonly Dictionary<(int AddressU, int AddressV, bool AnisotropicFiltering), RHI.ISampler> RhiSamplers = [];
        private readonly RendererContext RendererContext;
        private RenderTexture? ErrorTexture;
        private RenderTexture? DefaultNormal;
        private RenderTexture? DefaultMask;
        private RenderTexture? DefaultColor;
        private RenderTexture? DefaultVolume;
        /// <summary>Gets or sets the maximum anisotropy level applied to newly loaded textures when anisotropic filtering is enabled.</summary>
        public static float MaxTextureMaxAnisotropy { get; set; }

        /// <summary>Gets the number of materials currently held in the cache.</summary>
        public int MaterialCount => Materials.Count;

        /// <summary>
        /// Maps a material texture parameter name to the shader uniforms it can feed, in preference order.
        /// The first candidate the shader declares and that is not already bound wins.
        /// </summary>
        private static readonly Dictionary<string, string[]> TextureAliases = new(StringComparer.Ordinal)
        {
            ["g_tColor1"] = ["g_tColor"],
            ["g_tColor2"] = ["g_tColor", "g_tLayer2Color"],
            ["g_tColorA"] = ["g_tColor"],
            ["g_tColorB"] = ["g_tLayer2Color", "g_tColor"],
            ["g_tColorC"] = ["g_tColor"],
            ["g_tGlassDust"] = ["g_tColor"],
            ["g_tNormalA"] = ["g_tNormal"],
            ["g_tNormalB"] = ["g_tLayer2NormalRoughness"],
            ["g_tNormalRoughness"] = ["g_tNormal"],
            ["g_tNormalRoughness1"] = ["g_tNormal"],
            ["g_tNormalRoughness2"] = ["g_tLayer2NormalRoughness"],
            ["g_tLayer1NormalRoughness"] = ["g_tNormal"],
            ["g_tLayer1AmbientOcclusion"] = ["g_tAmbientOcclusion"],
        };

        /// <summary>Initializes a new instance of the <see cref="MaterialLoader"/> class.</summary>
        /// <param name="rendererContext">The renderer context used for file loading and shader access.</param>
        public MaterialLoader(RendererContext rendererContext)
        {
            RendererContext = rendererContext;

            // Publishes the context as the fallback every renderer-owned allocation resolves its device
            // through. Done here because this runs inside the RendererContext constructor, which is the
            // earliest point a context exists; its Device is assigned later by the presentation layer and
            // is read at allocation time, not now. See RendererDevice for why the fallback exists at all.
            RendererDevice.Publish(rendererContext);
        }

        private static readonly byte[] NewLineArray = "\n"u8.ToArray();

        /// <summary>
        /// Clears the material cache and disposes any cached textures and samplers.
        /// </summary>
        public void Clear()
        {
            foreach (var material in OwnedMaterials)
            {
                material.Delete();
            }

            OwnedMaterials.Clear();
            Materials.Clear();

            foreach (var item in Textures)
            {
                item.Value.Delete();
            }

            Textures.Clear();

            foreach (var item in TexturesSrgb)
            {
                item.Value.Delete();
            }

            TexturesSrgb.Clear();

            // Every handle in Samplers belongs to a GLSampler in RhiSamplers: both accessors build through
            // CreateSampler, which registers there. Disposing these frees both caches' objects exactly once.
            foreach (var sampler in RhiSamplers.Values)
            {
                sampler.Dispose();
            }

            RhiSamplers.Clear();
            Samplers.Clear();
        }

        /// <summary>Returns a cached <see cref="RenderMaterial"/> for the given resource path and shader arguments, loading and caching it on first access.</summary>
        /// <param name="name">The compiled material resource path, or <see langword="null"/> to return the error material.</param>
        /// <param name="shaderArguments">Optional static combo overrides to pass to the shader.</param>
        public RenderMaterial GetMaterial(string? name, Dictionary<string, byte>? shaderArguments)
        {
            // HL:VR has a world node that has a draw call with no material
            if (name == null)
            {
                return GetErrorMaterial();
            }

            Span<byte> valueSpan = stackalloc byte[1];
            var hash = new XxHash3(StringToken.MURMUR2SEED);
            hash.Append(MemoryMarshal.AsBytes(name.AsSpan()));

            if (shaderArguments != null)
            {
                foreach (var (key, value) in shaderArguments)
                {
                    hash.Append(NewLineArray);
                    hash.Append(MemoryMarshal.AsBytes(key.AsSpan()));
                    hash.Append(NewLineArray);

                    valueSpan[0] = value;
                    hash.Append(valueSpan);
                }
            }

            var cacheKey = hash.GetCurrentHashAsUInt64();

            if (Materials.TryGetValue(cacheKey, out var mat))
            {
                return mat;
            }

            var resource = RendererContext.FileLoader.LoadFileCompiled(name);
            mat = LoadMaterial(resource, shaderArguments);

            Materials.Add(cacheKey, mat);

            return mat;
        }

        /// <summary>Creates a <see cref="RenderMaterial"/> from an already-loaded resource, binding textures and resolving aliases.</summary>
        /// <param name="resource">The material resource, or <see langword="null"/> to return the error material.</param>
        /// <param name="shaderArguments">Optional static combo overrides to pass to the shader.</param>
        public RenderMaterial LoadMaterial(Resource? resource, Dictionary<string, byte>? shaderArguments = null)
        {
            if (resource == null)
            {
                return GetErrorMaterial();
            }

            var vrfMaterial = (VrfMaterial?)resource.DataBlock;
            Debug.Assert(vrfMaterial != null);
            var mat = new RenderMaterial(
                vrfMaterial,
                RendererContext,
                shaderArguments
            );

            OwnedMaterials.Add(mat);

            foreach (var (textureName, texturePath) in mat.Material.TextureParams)
            {
                TryBindTexture(mat, textureName, texturePath);
            }

            foreach (var (textureName, texturePath) in mat.Material.TextureParams)
            {
                if (mat.Textures.ContainsKey(textureName)
                || !TextureAliases.TryGetValue(textureName, out var aliases))
                {
                    continue;
                }

                foreach (var alias in aliases)
                {
                    if (mat.Textures.ContainsKey(alias))
                    {
                        continue;
                    }

                    if (TryBindTexture(mat, alias, texturePath))
                    {
                        break;
                    }
                }
            }

            bool TryBindTexture(RenderMaterial mat, string name, string path)
            {
                if (mat.Shader.UniformNames.Contains(name))
                {
                    var srgbRead = mat.Shader.SrgbUniforms.Contains(name);
                    mat.Textures[name] = GetTexture(path, srgbRead, anisotropicFiltering: true);
                    return true;
                }

                return false;
            }

            return mat;
        }


        /// <summary>Returns a cached <see cref="RenderTexture"/> for the given path, loading it on first access.</summary>
        /// <param name="name">The compiled texture resource path.</param>
        /// <param name="srgbRead">Whether to interpret the texture data in sRGB color space.</param>
        /// <param name="anisotropicFiltering">Whether to apply anisotropic filtering when <see cref="MaxTextureMaxAnisotropy"/> is sufficient.</param>
        public RenderTexture GetTexture(string name, bool srgbRead = false, bool anisotropicFiltering = false)
        {
            // TODO: Create texture view for srgb textures
            var cache = srgbRead ? TexturesSrgb : Textures;

            if (cache.TryGetValue(name, out var tex))
            {
                return tex;
            }

            tex = LoadTexture(name, srgbRead);
            cache.Add(name, tex);

            if (anisotropicFiltering && MaxTextureMaxAnisotropy >= 4)
            {
                // Through the texture rather than straight at OpenGL, so the anisotropy lands in
                // RhiSamplerDesc as well and the sampler a Vulkan bind uses asks for it too.
                tex.SetParameter((TextureParameterName)ExtTextureFilterAnisotropic.TextureMaxAnisotropyExt, MaxTextureMaxAnisotropy);
            }

            return tex;
        }

        /// <summary>
        /// Gets a sampler object for the supplied texture address modes, creating and caching one per <see cref="MaterialLoader" />.
        /// </summary>
        public int GetOrCreateSampler(int addressModeU, int addressModeV, bool mipmaps = true, bool anisotropicFiltering = true)
        {
            var key = (addressModeU, addressModeV, anisotropicFiltering);

            if (key == (0, 0, true))
            {
                return 0; // default sampler state with repeat wrap mode
            }

            if (Samplers.TryGetValue(key, out var sampler))
            {
                return sampler;
            }

            sampler = HandleOf(CreateSampler(addressModeU, addressModeV, mipmaps, anisotropicFiltering));

            Samplers[key] = sampler;
            return sampler;
        }

        /// <summary>
        /// Gets the same sampler <see cref="GetOrCreateSampler"/> returns, as an RHI
        /// <see cref="RHI.ISampler"/> for <see cref="RHI.ICommandList.BindTexture"/>.
        /// </summary>
        /// <param name="addressModeU">Raw Source 2 <c>g_nTextureAddressModeU</c> value.</param>
        /// <param name="addressModeV">Raw Source 2 <c>g_nTextureAddressModeV</c> value.</param>
        /// <param name="mipmaps">Whether to filter between mip levels.</param>
        /// <param name="anisotropicFiltering">Whether to apply anisotropic filtering when
        /// <see cref="MaxTextureMaxAnisotropy"/> is sufficient.</param>
        /// <returns>The sampler, or <see langword="null"/> for the default sampler state, which on OpenGL
        /// means the parameters set on the texture object itself.</returns>
        /// <remarks>The same cache and the same objects as the OpenGL path: this is what that path is now
        /// built from, so the two cannot drift apart.</remarks>
        public RHI.ISampler? GetOrCreateRhiSampler(int addressModeU, int addressModeV, bool mipmaps = true, bool anisotropicFiltering = true)
        {
            var key = (addressModeU, addressModeV, anisotropicFiltering);

            if (key == (0, 0, true))
            {
                return null; // default sampler state with repeat wrap mode
            }

            if (RhiSamplers.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var created = CreateSampler(addressModeU, addressModeV, mipmaps, anisotropicFiltering);

            RhiSamplers[key] = created;
            Samplers[key] = HandleOf(created);
            return created;
        }

        /// <summary>Gets the OpenGL name of a sampler, or 0 when it is not an OpenGL one.</summary>
        /// <remarks>0 is also what <see cref="GetOrCreateSampler"/> returns for the default sampler state,
        /// so the two are indistinguishable on a non-OpenGL device. That is harmless because the integer
        /// handle only ever reaches <c>glBindSampler</c>, which does not run on one.</remarks>
        private static int HandleOf(RHI.ISampler sampler) => (sampler as GLSampler)?.Handle ?? 0;

        private RHI.ISampler CreateSampler(int addressModeU, int addressModeV, bool mipmaps, bool anisotropicFiltering)
        {
            var key = (addressModeU, addressModeV, anisotropicFiltering);

            if (RhiSamplers.TryGetValue(key, out var existing))
            {
                return existing;
            }

            // Anisotropy is requested only when the device reports at least 4x, exactly as before;
            // both backends apply the parameter only when the value exceeds 1, so the two agree.
            var desc = new RHI.SamplerDesc(
                MinFilter: RHI.FilterMode.Linear,
                MagFilter: RHI.FilterMode.Linear,
                MipFilter: mipmaps ? RHI.MipFilterMode.Linear : RHI.MipFilterMode.None,
                AddressU: MapAddressMode(addressModeU),
                AddressV: MapAddressMode(addressModeV),
                AddressW: RHI.AddressMode.Repeat,
                MaxAnisotropy: anisotropicFiltering && MaxTextureMaxAnisotropy >= 4 ? MaxTextureMaxAnisotropy : 1f);

            // Through the device when there is one, so a Vulkan run gets a VkSampler rather than a GL name
            // it cannot use. The OpenGL path still reads GLSampler.Handle off the result, which is the same
            // object, so the two descriptions cannot drift apart.
            var device = RendererContext.Device;

            var sampler = device is not null
                ? device.CreateSampler(desc)
                : new GLSampler(in desc, $"Material sampler {addressModeU},{addressModeV}");

            RhiSamplers[key] = sampler;
            return sampler;
        }

        private static RHI.AddressMode MapAddressMode(int mode) => mode switch
        {
            0 => RHI.AddressMode.Repeat,
            1 => RHI.AddressMode.MirroredRepeat,
            2 => RHI.AddressMode.ClampToEdge,
            3 => RHI.AddressMode.ClampToBorder,
            _ => RHI.AddressMode.Repeat,
        };

        private RenderTexture LoadTexture(string name, bool srgbRead = false)
        {
            var textureResource = RendererContext.FileLoader.LoadFileCompiled(name);

            if (textureResource == null)
            {
                return GetErrorTexture();
            }

            return LoadTexture(textureResource, srgbRead);
        }

#pragma warning disable CA1822 // Mark members as static
        /// <summary>Uploads a texture resource to the GPU and returns the resulting <see cref="RenderTexture"/>.</summary>
        /// <param name="textureResource">The loaded texture resource.</param>
        /// <param name="srgbRead">Whether to use the sRGB internal format when available.</param>
        /// <param name="isViewerRequest">When <see langword="true"/>, skips mip-level capping and keeps the resource alive after upload.</param>
        public RenderTexture LoadTexture(Resource textureResource, bool srgbRead = false, bool isViewerRequest = false)
#pragma warning restore CA1822 // Mark members as static
        {
            var data = (Texture?)textureResource.DataBlock;
            Debug.Assert(data != null);

            if (data.IsRawAnyImage)
            {
                using var bitmap = data.GenerateBitmap();
                return LoadBitmapTexture(bitmap, RendererContext.Device);
            }

            var target = TextureTarget.Texture2D;
            var is3d = false;
            var clampModeS = (data.Flags & VTexFlags.SUGGEST_CLAMPS) != 0 ? TextureWrapMode.ClampToBorder : TextureWrapMode.Repeat;
            var clampModeT = (data.Flags & VTexFlags.SUGGEST_CLAMPT) != 0 ? TextureWrapMode.ClampToBorder : TextureWrapMode.Repeat;
            var clampModeU = (data.Flags & VTexFlags.SUGGEST_CLAMPU) != 0 ? TextureWrapMode.ClampToBorder : TextureWrapMode.Repeat;

            if ((data.Flags & VTexFlags.CUBE_TEXTURE) != 0)
            {
                is3d = true;
                target = (data.Flags & VTexFlags.TEXTURE_ARRAY) != 0 ? TextureTarget.TextureCubeMapArray : TextureTarget.TextureCubeMap;
                clampModeS = TextureWrapMode.ClampToEdge;
                clampModeT = TextureWrapMode.ClampToEdge;
                clampModeU = TextureWrapMode.ClampToEdge;
            }
            else if ((data.Flags & (VTexFlags.TEXTURE_ARRAY | VTexFlags.VOLUME_TEXTURE)) != 0)
            {
                is3d = true;
                target = (data.Flags & VTexFlags.VOLUME_TEXTURE) != 0 ? TextureTarget.Texture3D : TextureTarget.Texture2DArray;
            }

            var format = GetTextureFormat(data.Format);

            // todo: BC7 and BC6H are also problematic on pre-RDNA AMD GPUs, when using immutable storage
            // see https://github.com/ValveResourceFormat/ValveResourceFormat/issues/721
            var rgba8UncompressedFallback = target == TextureTarget.Texture3D && IsOpenGLUnsupportedTexture3DFormat(data.Format);

            if (rgba8UncompressedFallback)
            {
                format = new TextureFormatMapping(
                    SizedInternalFormat.Rgba8,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    format.InternalSrgbFormat is not null ? SizedInternalFormat.Srgb8Alpha8 : null
                );
            }

            var uploadsAsSrgb = srgbRead && format.InternalSrgbFormat is not null;

            // GetTextureFormat has already thrown for anything FormatTables cannot map, so this is total.
            var rhiFormat = rgba8UncompressedFallback
                ? (uploadsAsSrgb ? RHI.RhiFormat.R8G8B8A8_SRgb : RHI.RhiFormat.R8G8B8A8_UNorm)
                : RHI.FormatTables.FromVTexFormat(data.Format, uploadsAsSrgb);

            var minMipLevelAllowed = 0;
            var texWidth = data.Width;
            var texHeight = data.Height;

            if (!isViewerRequest && !is3d && data.NumMipLevels > 1)
            {
                var maxUserTextureSize = RendererContext.MaxTextureSize;

                while (minMipLevelAllowed + 1 < data.NumMipLevels && (texWidth > maxUserTextureSize || texHeight > maxUserTextureSize))
                {
                    minMipLevelAllowed++;

                    texWidth >>= 1;
                    texHeight >>= 1;
                }
            }

            var textureName = System.IO.Path.GetFileName(textureResource.FileName) ?? string.Empty;

            // Depth is the layer count in RHI terms, and a cube map's six faces are implied by its shape
            // rather than multiplied in here: GLTexture and the Vulkan backend both expand it themselves.
            var storage = new RHI.TextureDesc(
                texWidth,
                texHeight,
                rhiFormat,
                RHI.TextureUsage.Sampled | RHI.TextureUsage.CopySource | RHI.TextureUsage.CopyDestination,
                textureName,
                data.Depth,
                data.NumMipLevels - minMipLevelAllowed,
                1,
                RHI.OpenGL.GLTexture.ToDimension(target));

            // The device comes from this loader's own context, never from the ambient fallback: a process
            // can hold several contexts on different devices, and a texture must be allocated on the one
            // whose renderer is going to sample it.
            var tex = new RenderTexture(target, in storage, data, RendererContext.Device);

            var buffer = ArrayPool<byte>.Shared.Rent(data.GetBiggestBufferSize());
            byte[]? decodedBuffer = null;

            if (rgba8UncompressedFallback)
            {
                decodedBuffer = ArrayPool<byte>.Shared.Rent(data.Width * data.Height * data.Depth * 4);
            }

            try
            {
                foreach (var (level, width, height, depth, bufferSize) in data.GetEveryMipLevelTexture(buffer, minMipLevelAllowed))
                {
                    var realLevel = (int)level - minMipLevelAllowed;
                    var uploadBuffer = buffer;

                    if (decodedBuffer != null)
                    {
                        data.DecodeTexture(buffer.AsSpan(0, bufferSize), decodedBuffer, width, height, depth);
                        uploadBuffer = decodedBuffer;
                    }

                    // The decoded fallback buffer is sized from the source extent, not the mip's, so the
                    // amount actually written is the mip's own size rather than the whole rental.
                    var uploadSize = decodedBuffer != null ? width * height * depth * 4 : bufferSize;
                    var mipData = uploadBuffer.AsSpan(0, uploadSize);

                    if (target is TextureTarget.Texture2DArray or TextureTarget.TextureCubeMap or TextureTarget.TextureCubeMapArray)
                    {
                        // A layered texture arrives as every slice of this mip end to end, but the RHI
                        // uploads one layer at a time, because that is the granularity a Vulkan buffer to
                        // image copy addresses. Volume textures are the exception: their depth is part of
                        // the extent, so they go up whole.
                        var sliceSize = uploadSize / depth;

                        for (var slice = 0; slice < depth; slice++)
                        {
                            tex.Upload(realLevel, slice, mipData.Slice(slice * sliceSize, sliceSize));
                        }
                    }
                    else
                    {
                        tex.Upload(realLevel, 0, mipData);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);

                if (decodedBuffer != null)
                {
                    ArrayPool<byte>.Shared.Return(decodedBuffer);
                }
            }

            if (!isViewerRequest)
            {
                // Dispose texture otherwise we run out of memory
                // TODO: This might conflict when opening multiple files due to shit caching
                textureResource.Dispose();
            }

            tex.SetFiltering(TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear);

            tex.SetParameter(TextureParameterName.TextureWrapS, (int)clampModeS);
            tex.SetParameter(TextureParameterName.TextureWrapT, (int)clampModeT);
            tex.SetParameter(TextureParameterName.TextureWrapR, (int)clampModeU);

            // Published for sampling now that every mip and layer is uploaded. One call covers the whole
            // texture and batches into a single barrier, which is why it is here rather than in the loop.
            tex.TransitionTo(RHI.ResourceState.ShaderRead, RHI.ResourceState.CopyDestination);

            return tex;
        }

        /// <param name="InternalFormat">Specifies the sized internal format to be used to store texture image data.</param>
        /// <param name="InternalSrgbFormat">Same as <see cref="InternalFormat"/>, but for sRGB textures. Null if no sRGB format.</param>
        /// <param name="PixelFormat">Specifies the format of the pixel data. Must be null if the format is compressed.</param>
        /// <param name="PixelType">Specifies the data type of the pixel data. Must be null if the format is compressed.</param>
        /// <see href="https://registry.khronos.org/OpenGL-Refpages/gl4/html/glTexStorage2D.xhtml"/>
        /// <see href="https://registry.khronos.org/OpenGL-Refpages/gl4/html/glTexSubImage2D.xhtml"/>
        record struct TextureFormatMapping(SizedInternalFormat InternalFormat, PixelFormat? PixelFormat = null, PixelType? PixelType = null, SizedInternalFormat? InternalSrgbFormat = null);

        /// <summary>
        /// Whether a format has to be decompressed before it can be uploaded to a <see cref="TextureTarget.Texture3D"/>.
        /// Of the block compressed formats only BPTC is specified to work with 3D textures, as a stack of
        /// independently compressed 2D slices. S3TC and RGTC are two-dimensional only:
        /// NVIDIA accepts them through NV_texture_compression_vtc, which reuses the very same format enums but expects
        /// 4x4x4 VTC tiling, so the slices get read back scrambled, and other drivers reject the upload outright.
        /// </summary>
        private static bool IsOpenGLUnsupportedTexture3DFormat(VTexFormat vformat) => vformat
            is VTexFormat.DXT1
            or VTexFormat.DXT5
            or VTexFormat.ATI1N
            or VTexFormat.ATI2N;

        private static TextureFormatMapping GetTextureFormat(VTexFormat vformat) => vformat switch
        {
#pragma warning disable format
            VTexFormat.ATI1N           => new((SizedInternalFormat)InternalFormat.CompressedRedRgtc1),
            VTexFormat.ATI2N           => new((SizedInternalFormat)InternalFormat.CompressedRgRgtc2),
            VTexFormat.BC6H            => new((SizedInternalFormat)InternalFormat.CompressedRgbBptcUnsignedFloat),
            VTexFormat.BC7             => new((SizedInternalFormat)InternalFormat.CompressedRgbaBptcUnorm,        InternalSrgbFormat: (SizedInternalFormat)InternalFormat.CompressedSrgbAlphaBptcUnorm),
            VTexFormat.DXT1            => new((SizedInternalFormat)InternalFormat.CompressedRgbaS3tcDxt1Ext,      InternalSrgbFormat: (SizedInternalFormat)InternalFormat.CompressedSrgbAlphaS3tcDxt1Ext),
            VTexFormat.DXT5            => new((SizedInternalFormat)InternalFormat.CompressedRgbaS3tcDxt5Ext,      InternalSrgbFormat: (SizedInternalFormat)InternalFormat.CompressedSrgbAlphaS3tcDxt5Ext),
            VTexFormat.ETC2            => new((SizedInternalFormat)InternalFormat.CompressedRgb8Etc2,             InternalSrgbFormat: (SizedInternalFormat)InternalFormat.CompressedSrgb8Etc2),
            VTexFormat.ETC2_EAC        => new((SizedInternalFormat)InternalFormat.CompressedRgba8Etc2Eac,         InternalSrgbFormat: (SizedInternalFormat)InternalFormat.CompressedSrgb8Alpha8Etc2Eac),

            VTexFormat.R16             => new(SizedInternalFormat.R16,        PixelFormat.Red,    PixelType.UnsignedShort),
            VTexFormat.RG1616          => new(SizedInternalFormat.Rg16,       PixelFormat.Rg,     PixelType.UnsignedShort),
            VTexFormat.RGBA16161616    => new(SizedInternalFormat.Rgba16,     PixelFormat.Rgba,   PixelType.UnsignedShort),

            VTexFormat.R16F            => new(SizedInternalFormat.R16f,       PixelFormat.Red,    PixelType.HalfFloat),
            VTexFormat.RG1616F         => new(SizedInternalFormat.Rg16f,      PixelFormat.Rg,     PixelType.HalfFloat),
            VTexFormat.RGBA16161616F   => new(SizedInternalFormat.Rgba16f,    PixelFormat.Rgba,   PixelType.HalfFloat),

            VTexFormat.R32F            => new(SizedInternalFormat.R32f,       PixelFormat.Red,    PixelType.Float),
            VTexFormat.RG3232F         => new(SizedInternalFormat.Rg32f,      PixelFormat.Rg,     PixelType.Float),
            VTexFormat.RGBA32323232F   => new(SizedInternalFormat.Rgba32f,    PixelFormat.Rgba,   PixelType.Float),

            VTexFormat.RGBA8888        => new(SizedInternalFormat.Rgba8,      PixelFormat.Rgba,   PixelType.UnsignedByte,     SizedInternalFormat.Srgb8Alpha8),
            VTexFormat.BGRA8888        => new(SizedInternalFormat.Rgba8,      PixelFormat.Bgra,   PixelType.UnsignedByte,     SizedInternalFormat.Srgb8Alpha8),
            VTexFormat.I8              => new(SizedInternalFormat.R8,         PixelFormat.Red,    PixelType.UnsignedByte),

            //VTexFormat.IA88
            //VTexFormat.R11_EAC
            //VTexFormat.RG11_EAC
            //VTexFormat.RGB323232F
#pragma warning restore format

            _ => throw new NotImplementedException($"Unsupported texture format {vformat}")
        };

        /// <summary>Gets the texture unit each reserved sampler uniform is bound to.</summary>
        public static readonly FrozenDictionary<string, ReservedTextureSlots> ReservedTextureSlotByName = BuildReservedTextureSlotByName();

        private static FrozenDictionary<string, ReservedTextureSlots> BuildReservedTextureSlotByName()
        {
            var slotByName = new Dictionary<string, ReservedTextureSlots>(StringComparer.Ordinal);

            foreach (var field in typeof(ReservedTextureSlots).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var attribute = field.GetCustomAttribute<SamplerNameAttribute>();

                if (attribute == null)
                {
                    continue; // Aliases such as Last carry no names of their own.
                }

                var slot = (ReservedTextureSlots)field.GetRawConstantValue()!;

                foreach (var name in attribute.Names)
                {
                    // Add, not assign: two slots claiming one sampler name is a mistake worth failing on.
                    slotByName.Add(name, slot);
                }
            }

            return slotByName.ToFrozenDictionary(StringComparer.Ordinal);
        }

        /// <summary>Returns whether a uniform name is bound to one of the <see cref="ReservedTextureSlots"/>.</summary>
        public static bool IsReservedTexture(string uniformName) => ReservedTextureSlotByName.ContainsKey(uniformName);

        /// <summary>
        /// Material invariant textures, requested by shaders. They become scene-wide textures.
        /// </summary>
        public static readonly List<(ReservedTextureSlots Slot, string Name, string Path)> ShaderTextures =
        [
            (ReservedTextureSlots.WetnessWaves, "g_tWetnessWaves", "materials/dev/water_waves.vtex"),
        ];

        private RenderMaterial GetErrorMaterial()
        {
            var errorMat = new RenderMaterial(RendererContext.ShaderLoader.LoadShader("error"));
            OwnedMaterials.Add(errorMat);
            return errorMat;
        }

        /// <summary>Returns a lazily created 4×4 checkerboard error texture used as a fallback for missing textures.</summary>
        public RenderTexture GetErrorTexture()
        {
            if (ErrorTexture == null)
            {
                ReadOnlySpan<byte> color1 = [100, 25, 75];
                ReadOnlySpan<byte> color2 = [0, 127, 0];

                var color = new byte[16 * 3];

                for (var i = 0; i < 16; i++)
                {
                    var checkerboardX = i / 4 % 2;
                    var colorToUse = i % 2 == checkerboardX ? color1 : color2;
                    var pixel = color.AsSpan(i * 3, 3);
                    colorToUse.CopyTo(pixel);
                }

                ErrorTexture = GenerateColorTexture(4, 4, color, RendererContext.Device);
            }

            return ErrorTexture;
        }

        private RenderTexture CreateSolidTexture(byte r, byte g, byte b) => GenerateColorTexture(1, 1, [r, g, b], RendererContext.Device);
        /// <summary>Returns a lazily created 1×1 flat normal map texture (127, 127, 255).</summary>
        public RenderTexture GetDefaultNormal() => DefaultNormal ??= CreateSolidTexture(127, 127, 255);

        /// <summary>Returns a lazily created 1×1 solid white mask texture.</summary>
        public RenderTexture GetDefaultMask() => DefaultMask ??= CreateSolidTexture(255, 255, 255);

        /// <summary>Returns a lazily created 1×1 solid white colour texture, a neutral fallback albedo.</summary>
        public RenderTexture GetDefaultColor() => DefaultColor ??= CreateSolidTexture(255, 255, 255);

        /// <summary>
        /// Returns a lazily created 1×1×1 white volume texture.
        /// </summary>
        public RenderTexture GetDefaultVolume()
        {
            if (DefaultVolume == null)
            {
                // R8G8B8A8 rather than R8G8B8: 24 bit RGB is not in Vulkan's required-format table at all,
                // so an implementation may support it for nothing, and lavapipe reports exactly that --
                // zero format features, which makes the image, its view and the upload into it all
                // illegal. The fourth channel is the fix that works everywhere; a white texel sampled from
                // either format returns the same (1, 1, 1, 1).
                DefaultVolume = new RenderTexture(TextureTarget.Texture3D, RHI.RhiFormat.R8G8B8A8_UNorm, 1, 1, 1, 1, "DefaultVolume", device: RendererContext.Device);
                DefaultVolume.SetFiltering(TextureMinFilter.Nearest, TextureMagFilter.Nearest);
                DefaultVolume.SetWrapMode(TextureWrapMode.ClampToEdge);

                DefaultVolume.Upload(0, 0, WhiteTexel);
                DefaultVolume.TransitionTo(RHI.ResourceState.ShaderRead, RHI.ResourceState.CopyDestination);
            }

            return DefaultVolume;
        }

        private static readonly byte[] WhiteTexel = [255, 255, 255, 255];

        /// <summary>Returns the OpenGL format triple appropriate for exporting a rendered image, choosing between 8-bit BGRA and 32-bit float RGBA.</summary>
        /// <param name="hdr">Whether to use the HDR (32-bit float) format.</param>
        public static (SizedInternalFormat SizedInternalFormat, PixelFormat PixelFormat, PixelType PixelType) GetImageExportFormat(bool hdr) => hdr switch
        {
            false => (SizedInternalFormat.Rgba8, PixelFormat.Bgra, PixelType.UnsignedByte),
            true => (SizedInternalFormat.Rgba32f, PixelFormat.Rgba, PixelType.Float),
        };

        /// <summary>Uploads an <see cref="SKBitmap"/> as a 2D texture and returns the resulting <see cref="RenderTexture"/>.</summary>
        /// <param name="bitmap">The bitmap whose pixels are uploaded to the GPU.</param>
        /// <param name="device">The device to allocate through, or <see langword="null"/> to resolve one from <see cref="RendererDevice"/>.</param>
        public static RenderTexture LoadBitmapTexture(SKBitmap bitmap, RHI.IDevice? device = null)
        {
            // Bgra8888 keeps its byte order rather than being swizzled here: the RHI carries a BGRA format
            // and both backends know how to sample it, which is what the OpenGL PixelFormat.Bgra upload did.
            var format = bitmap.ColorType switch
            {
                SKColorType.Rgba8888 => RHI.RhiFormat.R8G8B8A8_UNorm,
                SKColorType.Bgra8888 => RHI.RhiFormat.B8G8R8A8_UNorm,
                SKColorType.Rgb888x => RHI.RhiFormat.R8G8B8A8_UNorm,
                SKColorType.Gray8 => RHI.RhiFormat.R8_UNorm,
                SKColorType.RgbaF16 => RHI.RhiFormat.R16G16B16A16_SFloat,
                SKColorType.RgbaF32 => RHI.RhiFormat.R32G32B32A32_SFloat,
                _ => throw new NotSupportedException($"Unsupported bitmap color type for GPU upload {bitmap.ColorType}"),
            };

            var texture = new RenderTexture(TextureTarget.Texture2D, format, bitmap.Width, bitmap.Height, 1, 1, nameof(LoadBitmapTexture), device: device);

            unsafe
            {
                var pixels = new ReadOnlySpan<byte>((void*)bitmap.GetPixels(), bitmap.ByteCount);
                texture.Upload(0, 0, pixels);
                texture.TransitionTo(RHI.ResourceState.ShaderRead, RHI.ResourceState.CopyDestination);
            }

            return texture;
        }

        /// <summary>
        /// Builds a one-dimensional colour ramp from a list of gradient stops.
        /// </summary>
        /// <param name="stops">Gradient stops, each a position in 0-1 and its colour. Need not be sorted.</param>
        /// <param name="device">The device to allocate through, or <see langword="null"/> to resolve one from <see cref="RendererDevice"/>.</param>
        public static RenderTexture GenerateGradientTexture(ReadOnlySpan<(float Position, Color32 Color)> stops, RHI.IDevice? device = null)
        {
            const int Width = 256;

            var texels = new byte[Width * 4];

            for (var x = 0; x < Width; x++)
            {
                var position = x / (Width - 1f);
                var color = SampleGradient(stops, position);

                texels[(x * 4) + 0] = color.R;
                texels[(x * 4) + 1] = color.G;
                texels[(x * 4) + 2] = color.B;
                texels[(x * 4) + 3] = color.A;
            }

            // sRGB storage, so a sample lands in linear space like every other layer's texture.
            var texture = new RenderTexture(TextureTarget.Texture2D, RHI.RhiFormat.R8G8B8A8_SRgb, Width, 1, 1, 1, "GeneratedGradient", device: device);

            // Clamped and filtered: the ramp is addressed by a luminance, so the ends have to hold rather
            // than wrap, and the steps between stops should not be visible.
            texture.SetFiltering(TextureMinFilter.Linear, TextureMagFilter.Linear);
            texture.SetWrapMode(TextureWrapMode.ClampToEdge);

            texture.Upload(0, 0, texels);
            texture.TransitionTo(RHI.ResourceState.ShaderRead, RHI.ResourceState.CopyDestination);

            return texture;
        }

        private static Color32 SampleGradient(ReadOnlySpan<(float Position, Color32 Color)> stops, float position)
        {
            if (stops.Length == 0)
            {
                return new Color32(255, 255, 255);
            }

            // Stops are authored in order, but nothing guarantees it, so pick the bracketing pair by value
            // rather than by index.
            var lower = stops[0];
            var upper = stops[0];
            var hasLower = false;
            var hasUpper = false;

            foreach (var stop in stops)
            {
                if (stop.Position <= position && (!hasLower || stop.Position >= lower.Position))
                {
                    lower = stop;
                    hasLower = true;
                }

                if (stop.Position >= position && (!hasUpper || stop.Position <= upper.Position))
                {
                    upper = stop;
                    hasUpper = true;
                }
            }

            if (!hasLower)
            {
                return upper.Color;
            }

            if (!hasUpper)
            {
                return lower.Color;
            }

            var span = upper.Position - lower.Position;
            var t = span > 0f ? (position - lower.Position) / span : 0f;

            return new Color32(
                (byte)float.Round(float.Lerp(lower.Color.R, upper.Color.R, t)),
                (byte)float.Round(float.Lerp(lower.Color.G, upper.Color.G, t)),
                (byte)float.Round(float.Lerp(lower.Color.B, upper.Color.B, t)),
                (byte)float.Round(float.Lerp(lower.Color.A, upper.Color.A, t)));
        }

        /// <summary>Creates a small texture filled with the given tightly packed 24 bit RGB texels.</summary>
        /// <param name="width">Width in texels.</param>
        /// <param name="height">Height in texels.</param>
        /// <param name="color">Three bytes per texel, row major.</param>
        /// <param name="device">The device to allocate through.</param>
        /// <remarks>The storage is <see cref="RHI.RhiFormat.R8G8B8A8_UNorm"/> and the texels are widened
        /// on the way in. 24 bit RGB is absent from Vulkan's required-format table, so an implementation
        /// is free to support it for nothing at all &#8212; and one does: lavapipe reports zero format
        /// features for <c>VK_FORMAT_R8G8B8_UNORM</c>, which makes the image, its view and the upload into
        /// it all illegal. Widening costs one byte per texel on a handful of 1x1 and 4x4 textures and is
        /// invisible to the sampler, which returned an opaque alpha for the three channel format too.</remarks>
        private static RenderTexture GenerateColorTexture(int width, int height, byte[] color, RHI.IDevice? device = null)
        {
            ArgumentNullException.ThrowIfNull(color);

            var texture = new RenderTexture(
                TextureTarget.Texture2D,
                RHI.RhiFormat.R8G8B8A8_UNorm,
                width,
                height,
                1,
                1,
                width > 1 ? "ErrorTexture" : "ColorTexture",
                device: device);

            texture.SetFiltering(TextureMinFilter.Nearest, TextureMagFilter.Nearest);
            texture.SetWrapMode(TextureWrapMode.Repeat);

            var color32 = new Color32(color[0], color[1], color[2]);
            texture.Reflectivity = color32.ToLinearColor();

            texture.Upload(0, 0, WidenToOpaqueRgba(color));
            texture.TransitionTo(RHI.ResourceState.ShaderRead, RHI.ResourceState.CopyDestination);

            return texture;
        }

        /// <summary>Widens tightly packed 24 bit RGB texels to 32 bit RGBA with an opaque alpha.</summary>
        /// <param name="rgb">Three bytes per texel.</param>
        /// <returns>Four bytes per texel.</returns>
        private static byte[] WidenToOpaqueRgba(ReadOnlySpan<byte> rgb)
        {
            var texels = rgb.Length / 3;
            var rgba = new byte[texels * 4];

            for (var i = 0; i < texels; i++)
            {
                rgb.Slice(i * 3, 3).CopyTo(rgba.AsSpan(i * 4, 3));
                rgba[(i * 4) + 3] = byte.MaxValue;
            }

            return rgba;
        }
    }
}
