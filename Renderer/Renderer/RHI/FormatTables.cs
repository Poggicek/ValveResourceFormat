using OpenTK.Graphics.OpenGL;

namespace ValveResourceFormat.Renderer.RHI;

/// <summary>
/// Total translation tables between <see cref="RhiFormat"/>, the two backends' format enums, and the
/// formats the resource files carry.
/// </summary>
/// <remarks>
/// Every table here is exhaustive and throws on a value it does not know: there is no fallback arm
/// anywhere in this file. A format that reaches a backend unmapped must fail at load time, loudly,
/// rather than silently substitute another format and be discovered as a black texture later.
/// </remarks>
public static partial class FormatTables
{
    /// <summary>
    /// Gets the OpenGL sized internal format a texture of this format is allocated with, the argument to
    /// <c>glTextureStorage</c>. Compressed formats map to their <c>GL_COMPRESSED_*</c> enum, which is also
    /// what <c>glCompressedTextureSubImage</c> expects.
    /// </summary>
    /// <param name="format">The format to translate.</param>
    /// <returns>The matching OpenTK <see cref="SizedInternalFormat"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format has no OpenGL equivalent, including
    /// <see cref="RhiFormat.Undefined"/>, which no texture can be allocated with.</exception>
    public static SizedInternalFormat ToGLSizedInternalFormat(RhiFormat format) => format switch
    {
        RhiFormat.R8_UNorm => SizedInternalFormat.R8,
        RhiFormat.R8G8_UNorm => SizedInternalFormat.Rg8,
        RhiFormat.R8G8B8A8_UNorm => SizedInternalFormat.Rgba8,
        RhiFormat.R8G8B8A8_SRgb => SizedInternalFormat.Srgb8Alpha8,

        // OpenGL has no BGRA internal format, the swizzle happens on upload through PixelFormat.Bgra
        RhiFormat.B8G8R8A8_UNorm => SizedInternalFormat.Rgba8,
        RhiFormat.B8G8R8A8_SRgb => SizedInternalFormat.Srgb8Alpha8,

        RhiFormat.R8G8B8_UNorm => SizedInternalFormat.Rgb8,
        RhiFormat.R8G8B8_SRgb => SizedInternalFormat.Srgb8,
        RhiFormat.R16_UNorm => SizedInternalFormat.R16,
        RhiFormat.R16G16_UNorm => SizedInternalFormat.Rg16,
        RhiFormat.R16G16B16A16_UNorm => SizedInternalFormat.Rgba16,
        RhiFormat.R10G10B10A2_UNorm => SizedInternalFormat.Rgb10A2,

        RhiFormat.R16_SFloat => SizedInternalFormat.R16f,
        RhiFormat.R16G16_SFloat => SizedInternalFormat.Rg16f,
        RhiFormat.R16G16B16A16_SFloat => SizedInternalFormat.Rgba16f,
        RhiFormat.R32_SFloat => SizedInternalFormat.R32f,
        RhiFormat.R32G32_SFloat => SizedInternalFormat.Rg32f,
        RhiFormat.R32G32B32A32_SFloat => SizedInternalFormat.Rgba32f,
        RhiFormat.B10G11R11_UFloat => SizedInternalFormat.R11fG11fB10f,

        RhiFormat.R8_UInt => SizedInternalFormat.R8ui,
        RhiFormat.R16_UInt => SizedInternalFormat.R16ui,
        RhiFormat.R32_UInt => SizedInternalFormat.R32ui,
        RhiFormat.R32G32_UInt => SizedInternalFormat.Rg32ui,
        RhiFormat.R32G32B32A32_UInt => SizedInternalFormat.Rgba32ui,
        RhiFormat.R32_SInt => SizedInternalFormat.R32i,

        RhiFormat.R8G8B8A8_UInt => SizedInternalFormat.Rgba8ui,
        RhiFormat.R16G16_SInt => SizedInternalFormat.Rg16i,
        RhiFormat.R16G16B16A16_UInt => SizedInternalFormat.Rgba16ui,
        RhiFormat.R16G16B16A16_SInt => SizedInternalFormat.Rgba16i,
        RhiFormat.R32G32B32A32_SInt => SizedInternalFormat.Rgba32i,

        RhiFormat.R32G32B32_SFloat => SizedInternalFormat.Rgb32f,
        RhiFormat.R16G16_SNorm => SizedInternalFormat.Rg16Snorm,
        RhiFormat.R8G8B8A8_SNorm => SizedInternalFormat.Rgba8Snorm,

        RhiFormat.BC1_RGBA_UNorm => SizedInternalFormat.CompressedRgbaS3tcDxt1Ext,
        RhiFormat.BC1_RGBA_SRgb => SizedInternalFormat.CompressedSrgbAlphaS3tcDxt1Ext,
        RhiFormat.BC2_UNorm => SizedInternalFormat.CompressedRgbaS3tcDxt3Ext,
        RhiFormat.BC2_SRgb => SizedInternalFormat.CompressedSrgbAlphaS3tcDxt3Ext,
        RhiFormat.BC3_UNorm => SizedInternalFormat.CompressedRgbaS3tcDxt5Ext,
        RhiFormat.BC3_SRgb => SizedInternalFormat.CompressedSrgbAlphaS3tcDxt5Ext,
        RhiFormat.BC4_UNorm => SizedInternalFormat.CompressedRedRgtc1,
        RhiFormat.BC4_SNorm => SizedInternalFormat.CompressedSignedRedRgtc1,
        RhiFormat.BC5_UNorm => SizedInternalFormat.CompressedRgRgtc2,
        RhiFormat.BC5_SNorm => SizedInternalFormat.CompressedSignedRgRgtc2,
        RhiFormat.BC6H_UFloat => SizedInternalFormat.CompressedRgbBptcUnsignedFloat,
        RhiFormat.BC6H_SFloat => SizedInternalFormat.CompressedRgbBptcSignedFloat,
        RhiFormat.BC7_UNorm => SizedInternalFormat.CompressedRgbaBptcUnorm,
        RhiFormat.BC7_SRgb => SizedInternalFormat.CompressedSrgbAlphaBptcUnorm,
        RhiFormat.ETC2_R8G8B8_UNorm => SizedInternalFormat.CompressedRgb8Etc2,
        RhiFormat.ETC2_R8G8B8_SRgb => SizedInternalFormat.CompressedSrgb8Etc2,
        RhiFormat.ETC2_R8G8B8A8_UNorm => SizedInternalFormat.CompressedRgba8Etc2Eac,
        RhiFormat.ETC2_R8G8B8A8_SRgb => SizedInternalFormat.CompressedSrgb8Alpha8Etc2Eac,

        RhiFormat.D16_UNorm => SizedInternalFormat.DepthComponent16,
        RhiFormat.D32_SFloat => SizedInternalFormat.DepthComponent32f,
        RhiFormat.D24_UNorm_S8_UInt => SizedInternalFormat.Depth24Stencil8,
        RhiFormat.D32_SFloat_S8_UInt => SizedInternalFormat.Depth32fStencil8,

        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "No OpenGL sized internal format for this RhiFormat."),
    };

    /// <summary>
    /// Gets the OpenGL internal format as the wider <see cref="PixelInternalFormat"/> enum, which is what
    /// the framebuffer attachment paths take. Same underlying GL enum value as
    /// <see cref="ToGLSizedInternalFormat"/>.
    /// </summary>
    /// <param name="format">The format to translate.</param>
    /// <returns>The matching OpenTK <see cref="PixelInternalFormat"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format has no OpenGL equivalent.</exception>
    public static PixelInternalFormat ToGLPixelInternalFormat(RhiFormat format)
        => (PixelInternalFormat)ToGLSizedInternalFormat(format);

    /// <summary>
    /// Gets the OpenGL client pixel format an upload of this format supplies, the <c>format</c> argument to
    /// <c>glTextureSubImage</c>.
    /// </summary>
    /// <param name="format">The format to translate.</param>
    /// <returns>The matching OpenTK <see cref="PixelFormat"/>, or <see langword="null"/> for block
    /// compressed formats, which upload through <c>glCompressedTextureSubImage</c> and pass their sized
    /// internal format instead.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format has no OpenGL equivalent.</exception>
    public static PixelFormat? ToGLPixelFormat(RhiFormat format) => format switch
    {
        RhiFormat.R8_UNorm or RhiFormat.R16_UNorm or RhiFormat.R16_SFloat or RhiFormat.R32_SFloat => PixelFormat.Red,
        RhiFormat.R8G8_UNorm or RhiFormat.R16G16_UNorm or RhiFormat.R16G16_SFloat or RhiFormat.R32G32_SFloat
            or RhiFormat.R16G16_SNorm => PixelFormat.Rg,
        RhiFormat.R8G8B8_UNorm or RhiFormat.R8G8B8_SRgb or RhiFormat.B10G11R11_UFloat
            or RhiFormat.R32G32B32_SFloat => PixelFormat.Rgb,
        RhiFormat.R8G8B8A8_UNorm or RhiFormat.R8G8B8A8_SRgb or RhiFormat.R8G8B8A8_SNorm
            or RhiFormat.R16G16B16A16_UNorm or RhiFormat.R16G16B16A16_SFloat or RhiFormat.R32G32B32A32_SFloat
            or RhiFormat.R10G10B10A2_UNorm => PixelFormat.Rgba,

        RhiFormat.B8G8R8A8_UNorm or RhiFormat.B8G8R8A8_SRgb => PixelFormat.Bgra,

        // Integer formats take the *Integer client formats, the plain ones normalize
        RhiFormat.R8_UInt or RhiFormat.R16_UInt or RhiFormat.R32_UInt or RhiFormat.R32_SInt => PixelFormat.RedInteger,
        RhiFormat.R32G32_UInt or RhiFormat.R16G16_SInt => PixelFormat.RgInteger,
        RhiFormat.R32G32B32A32_UInt or RhiFormat.R8G8B8A8_UInt or RhiFormat.R16G16B16A16_UInt
            or RhiFormat.R16G16B16A16_SInt or RhiFormat.R32G32B32A32_SInt => PixelFormat.RgbaInteger,

        RhiFormat.D16_UNorm or RhiFormat.D32_SFloat => PixelFormat.DepthComponent,
        RhiFormat.D24_UNorm_S8_UInt or RhiFormat.D32_SFloat_S8_UInt => PixelFormat.DepthStencil,

        _ when RhiFormatInfo.IsCompressed(format) => null,

        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "No OpenGL pixel format for this RhiFormat."),
    };

    /// <summary>
    /// Gets the OpenGL client data type an upload of this format supplies, the <c>type</c> argument to
    /// <c>glTextureSubImage</c>.
    /// </summary>
    /// <param name="format">The format to translate.</param>
    /// <returns>The matching OpenTK <see cref="PixelType"/>, or <see langword="null"/> for block compressed
    /// formats, which have no client type.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format has no OpenGL equivalent.</exception>
    public static PixelType? ToGLPixelType(RhiFormat format) => format switch
    {
        RhiFormat.R8_UNorm or RhiFormat.R8G8_UNorm or RhiFormat.R8G8B8_UNorm or RhiFormat.R8G8B8_SRgb
            or RhiFormat.R8G8B8A8_UNorm or RhiFormat.R8G8B8A8_SRgb or RhiFormat.B8G8R8A8_UNorm
            or RhiFormat.B8G8R8A8_SRgb or RhiFormat.R8_UInt or RhiFormat.R8G8B8A8_UInt => PixelType.UnsignedByte,
        RhiFormat.R8G8B8A8_SNorm => PixelType.Byte,

        RhiFormat.R16_UNorm or RhiFormat.R16G16_UNorm or RhiFormat.R16G16B16A16_UNorm
            or RhiFormat.R16_UInt or RhiFormat.R16G16B16A16_UInt or RhiFormat.D16_UNorm => PixelType.UnsignedShort,
        RhiFormat.R16G16_SNorm or RhiFormat.R16G16_SInt or RhiFormat.R16G16B16A16_SInt => PixelType.Short,

        RhiFormat.R16_SFloat or RhiFormat.R16G16_SFloat or RhiFormat.R16G16B16A16_SFloat => PixelType.HalfFloat,

        RhiFormat.R32_SFloat or RhiFormat.R32G32_SFloat or RhiFormat.R32G32B32_SFloat
            or RhiFormat.R32G32B32A32_SFloat or RhiFormat.D32_SFloat => PixelType.Float,

        RhiFormat.R32_UInt or RhiFormat.R32G32_UInt or RhiFormat.R32G32B32A32_UInt => PixelType.UnsignedInt,
        RhiFormat.R32_SInt or RhiFormat.R32G32B32A32_SInt => PixelType.Int,

        RhiFormat.R10G10B10A2_UNorm => PixelType.UnsignedInt2101010Rev,
        RhiFormat.B10G11R11_UFloat => PixelType.UnsignedInt10F11F11FRev,

        RhiFormat.D24_UNorm_S8_UInt => PixelType.UnsignedInt248,
        RhiFormat.D32_SFloat_S8_UInt => PixelType.Float32UnsignedInt248Rev,

        _ when RhiFormatInfo.IsCompressed(format) => null,

        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "No OpenGL pixel type for this RhiFormat."),
    };

    /// <summary>
    /// Gets the Vulkan <c>VkFormat</c> as its raw numeric value.
    /// </summary>
    /// <param name="format">The format to translate.</param>
    /// <returns>The <c>VkFormat</c> enumerant, 0 (<c>VK_FORMAT_UNDEFINED</c>) for
    /// <see cref="RhiFormat.Undefined"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format has no Vulkan equivalent.</exception>
    /// <remarks>
    /// Returned as a number rather than <c>Silk.NET.Vulkan.Format</c> because Silk.NET is not a reference of
    /// this project yet. The values are the ones fixed by the Vulkan core specification and can never
    /// change; when the package lands, add a partial of this class that casts:
    /// <c>(Format)ToVulkanFormat(format)</c>. Each arm names its symbolic constant.
    /// </remarks>
    public static uint ToVulkanFormat(RhiFormat format) => format switch
    {
        RhiFormat.Undefined => 0, // VK_FORMAT_UNDEFINED

        RhiFormat.R8_UNorm => 9, // VK_FORMAT_R8_UNORM
        RhiFormat.R8G8_UNorm => 16, // VK_FORMAT_R8G8_UNORM
        RhiFormat.R8G8B8_UNorm => 23, // VK_FORMAT_R8G8B8_UNORM
        RhiFormat.R8G8B8_SRgb => 29, // VK_FORMAT_R8G8B8_SRGB
        RhiFormat.R8G8B8A8_UNorm => 37, // VK_FORMAT_R8G8B8A8_UNORM
        RhiFormat.R8G8B8A8_SNorm => 38, // VK_FORMAT_R8G8B8A8_SNORM
        RhiFormat.R8G8B8A8_UInt => 41, // VK_FORMAT_R8G8B8A8_UINT
        RhiFormat.R8G8B8A8_SRgb => 43, // VK_FORMAT_R8G8B8A8_SRGB
        RhiFormat.B8G8R8A8_UNorm => 44, // VK_FORMAT_B8G8R8A8_UNORM
        RhiFormat.B8G8R8A8_SRgb => 50, // VK_FORMAT_B8G8R8A8_SRGB
        RhiFormat.R8_UInt => 13, // VK_FORMAT_R8_UINT

        // Vulkan names packed formats most significant bit first, so DXGI's R10G10B10A2 is A2B10G10R10
        RhiFormat.R10G10B10A2_UNorm => 64, // VK_FORMAT_A2B10G10R10_UNORM_PACK32

        RhiFormat.R16_UNorm => 70, // VK_FORMAT_R16_UNORM
        RhiFormat.R16_UInt => 74, // VK_FORMAT_R16_UINT
        RhiFormat.R16_SFloat => 76, // VK_FORMAT_R16_SFLOAT
        RhiFormat.R16G16_UNorm => 77, // VK_FORMAT_R16G16_UNORM
        RhiFormat.R16G16_SNorm => 78, // VK_FORMAT_R16G16_SNORM
        RhiFormat.R16G16_SInt => 82, // VK_FORMAT_R16G16_SINT
        RhiFormat.R16G16_SFloat => 83, // VK_FORMAT_R16G16_SFLOAT
        RhiFormat.R16G16B16A16_UNorm => 91, // VK_FORMAT_R16G16B16A16_UNORM
        RhiFormat.R16G16B16A16_UInt => 95, // VK_FORMAT_R16G16B16A16_UINT
        RhiFormat.R16G16B16A16_SInt => 96, // VK_FORMAT_R16G16B16A16_SINT
        RhiFormat.R16G16B16A16_SFloat => 97, // VK_FORMAT_R16G16B16A16_SFLOAT

        RhiFormat.R32_UInt => 98, // VK_FORMAT_R32_UINT
        RhiFormat.R32_SInt => 99, // VK_FORMAT_R32_SINT
        RhiFormat.R32_SFloat => 100, // VK_FORMAT_R32_SFLOAT
        RhiFormat.R32G32_UInt => 101, // VK_FORMAT_R32G32_UINT
        RhiFormat.R32G32_SFloat => 103, // VK_FORMAT_R32G32_SFLOAT
        RhiFormat.R32G32B32_SFloat => 106, // VK_FORMAT_R32G32B32_SFLOAT
        RhiFormat.R32G32B32A32_UInt => 107, // VK_FORMAT_R32G32B32A32_UINT
        RhiFormat.R32G32B32A32_SInt => 108, // VK_FORMAT_R32G32B32A32_SINT
        RhiFormat.R32G32B32A32_SFloat => 109, // VK_FORMAT_R32G32B32A32_SFLOAT
        RhiFormat.B10G11R11_UFloat => 122, // VK_FORMAT_B10G11R11_UFLOAT_PACK32

        RhiFormat.D16_UNorm => 124, // VK_FORMAT_D16_UNORM
        RhiFormat.D32_SFloat => 126, // VK_FORMAT_D32_SFLOAT
        RhiFormat.D24_UNorm_S8_UInt => 129, // VK_FORMAT_D24_UNORM_S8_UINT
        RhiFormat.D32_SFloat_S8_UInt => 130, // VK_FORMAT_D32_SFLOAT_S8_UINT

        RhiFormat.BC1_RGBA_UNorm => 133, // VK_FORMAT_BC1_RGBA_UNORM_BLOCK
        RhiFormat.BC1_RGBA_SRgb => 134, // VK_FORMAT_BC1_RGBA_SRGB_BLOCK
        RhiFormat.BC2_UNorm => 135, // VK_FORMAT_BC2_UNORM_BLOCK
        RhiFormat.BC2_SRgb => 136, // VK_FORMAT_BC2_SRGB_BLOCK
        RhiFormat.BC3_UNorm => 137, // VK_FORMAT_BC3_UNORM_BLOCK
        RhiFormat.BC3_SRgb => 138, // VK_FORMAT_BC3_SRGB_BLOCK
        RhiFormat.BC4_UNorm => 139, // VK_FORMAT_BC4_UNORM_BLOCK
        RhiFormat.BC4_SNorm => 140, // VK_FORMAT_BC4_SNORM_BLOCK
        RhiFormat.BC5_UNorm => 141, // VK_FORMAT_BC5_UNORM_BLOCK
        RhiFormat.BC5_SNorm => 142, // VK_FORMAT_BC5_SNORM_BLOCK
        RhiFormat.BC6H_UFloat => 143, // VK_FORMAT_BC6H_UFLOAT_BLOCK
        RhiFormat.BC6H_SFloat => 144, // VK_FORMAT_BC6H_SFLOAT_BLOCK
        RhiFormat.BC7_UNorm => 145, // VK_FORMAT_BC7_UNORM_BLOCK
        RhiFormat.BC7_SRgb => 146, // VK_FORMAT_BC7_SRGB_BLOCK
        RhiFormat.ETC2_R8G8B8_UNorm => 147, // VK_FORMAT_ETC2_R8G8B8_UNORM_BLOCK
        RhiFormat.ETC2_R8G8B8_SRgb => 148, // VK_FORMAT_ETC2_R8G8B8_SRGB_BLOCK
        RhiFormat.ETC2_R8G8B8A8_UNorm => 151, // VK_FORMAT_ETC2_R8G8B8A8_UNORM_BLOCK
        RhiFormat.ETC2_R8G8B8A8_SRgb => 152, // VK_FORMAT_ETC2_R8G8B8A8_SRGB_BLOCK

        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "No VkFormat for this RhiFormat."),
    };

    /// <summary>
    /// Translates a DXGI format, the vocabulary the vertex input layouts and texture headers already speak,
    /// to its <see cref="RhiFormat"/>.
    /// </summary>
    /// <param name="format">The DXGI format to translate.</param>
    /// <returns>The exactly equivalent <see cref="RhiFormat"/>. Never a near miss.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format is outside what the renderer maps, such as
    /// the typeless, video and palettized DXGI formats.</exception>
    public static RhiFormat FromDxgiFormat(DXGI_FORMAT format) => format switch
    {
        DXGI_FORMAT.R8_UNORM => RhiFormat.R8_UNorm,
        DXGI_FORMAT.R8_UINT => RhiFormat.R8_UInt,
        DXGI_FORMAT.R8G8_UNORM => RhiFormat.R8G8_UNorm,
        DXGI_FORMAT.R8G8B8A8_UNORM => RhiFormat.R8G8B8A8_UNorm,
        DXGI_FORMAT.R8G8B8A8_UNORM_SRGB => RhiFormat.R8G8B8A8_SRgb,
        DXGI_FORMAT.R8G8B8A8_SNORM => RhiFormat.R8G8B8A8_SNorm,
        DXGI_FORMAT.R8G8B8A8_UINT => RhiFormat.R8G8B8A8_UInt,
        DXGI_FORMAT.B8G8R8A8_UNORM => RhiFormat.B8G8R8A8_UNorm,
        DXGI_FORMAT.B8G8R8A8_UNORM_SRGB => RhiFormat.B8G8R8A8_SRgb,
        DXGI_FORMAT.R10G10B10A2_UNORM => RhiFormat.R10G10B10A2_UNorm,

        DXGI_FORMAT.R16_UNORM => RhiFormat.R16_UNorm,
        DXGI_FORMAT.R16_UINT => RhiFormat.R16_UInt,
        DXGI_FORMAT.R16_FLOAT => RhiFormat.R16_SFloat,
        DXGI_FORMAT.R16G16_UNORM => RhiFormat.R16G16_UNorm,
        DXGI_FORMAT.R16G16_SNORM => RhiFormat.R16G16_SNorm,
        DXGI_FORMAT.R16G16_SINT => RhiFormat.R16G16_SInt,
        DXGI_FORMAT.R16G16_FLOAT => RhiFormat.R16G16_SFloat,
        DXGI_FORMAT.R16G16B16A16_UNORM => RhiFormat.R16G16B16A16_UNorm,
        DXGI_FORMAT.R16G16B16A16_UINT => RhiFormat.R16G16B16A16_UInt,
        DXGI_FORMAT.R16G16B16A16_SINT => RhiFormat.R16G16B16A16_SInt,
        DXGI_FORMAT.R16G16B16A16_FLOAT => RhiFormat.R16G16B16A16_SFloat,

        DXGI_FORMAT.R32_UINT => RhiFormat.R32_UInt,
        DXGI_FORMAT.R32_SINT => RhiFormat.R32_SInt,
        DXGI_FORMAT.R32_FLOAT => RhiFormat.R32_SFloat,
        DXGI_FORMAT.R32G32_UINT => RhiFormat.R32G32_UInt,
        DXGI_FORMAT.R32G32_FLOAT => RhiFormat.R32G32_SFloat,
        DXGI_FORMAT.R32G32B32_FLOAT => RhiFormat.R32G32B32_SFloat,
        DXGI_FORMAT.R32G32B32A32_UINT => RhiFormat.R32G32B32A32_UInt,
        DXGI_FORMAT.R32G32B32A32_SINT => RhiFormat.R32G32B32A32_SInt,
        DXGI_FORMAT.R32G32B32A32_FLOAT => RhiFormat.R32G32B32A32_SFloat,
        DXGI_FORMAT.R11G11B10_FLOAT => RhiFormat.B10G11R11_UFloat,

        DXGI_FORMAT.BC1_UNORM => RhiFormat.BC1_RGBA_UNorm,
        DXGI_FORMAT.BC1_UNORM_SRGB => RhiFormat.BC1_RGBA_SRgb,
        DXGI_FORMAT.BC2_UNORM => RhiFormat.BC2_UNorm,
        DXGI_FORMAT.BC2_UNORM_SRGB => RhiFormat.BC2_SRgb,
        DXGI_FORMAT.BC3_UNORM => RhiFormat.BC3_UNorm,
        DXGI_FORMAT.BC3_UNORM_SRGB => RhiFormat.BC3_SRgb,
        DXGI_FORMAT.BC4_UNORM => RhiFormat.BC4_UNorm,
        DXGI_FORMAT.BC4_SNORM => RhiFormat.BC4_SNorm,
        DXGI_FORMAT.BC5_UNORM => RhiFormat.BC5_UNorm,
        DXGI_FORMAT.BC5_SNORM => RhiFormat.BC5_SNorm,
        DXGI_FORMAT.BC6H_UF16 => RhiFormat.BC6H_UFloat,
        DXGI_FORMAT.BC6H_SF16 => RhiFormat.BC6H_SFloat,
        DXGI_FORMAT.BC7_UNORM => RhiFormat.BC7_UNorm,
        DXGI_FORMAT.BC7_UNORM_SRGB => RhiFormat.BC7_SRgb,

        DXGI_FORMAT.D16_UNORM => RhiFormat.D16_UNorm,
        DXGI_FORMAT.D32_FLOAT => RhiFormat.D32_SFloat,
        DXGI_FORMAT.D24_UNORM_S8_UINT => RhiFormat.D24_UNorm_S8_UInt,
        DXGI_FORMAT.D32_FLOAT_S8X24_UINT => RhiFormat.D32_SFloat_S8_UInt,

        // :VertexAttributeFormat - every format VBIB.GetFormatInfo decodes is mapped above. A new one
        // added there must be added here too, never mapped to a near neighbour: a vertex format that
        // disagrees with the buffer reinterprets the mesh data rather than failing.
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "No RhiFormat for this DXGI format."),
    };

    /// <summary>
    /// Translates the format a <c>vtex</c> stores its texture data in to the <see cref="RhiFormat"/> it is
    /// uploaded as.
    /// </summary>
    /// <param name="format">The texture format to translate.</param>
    /// <param name="srgb">Whether the texture is read as sRGB. Formats with no sRGB pair ignore it.</param>
    /// <returns>The <see cref="RhiFormat"/> the texture data uploads as, unconverted.</returns>
    /// <exception cref="NotSupportedException">The format is not uploadable as it stands: the image
    /// container formats decode to a bitmap first, and the EAC pair has no <see cref="RhiFormat"/>
    /// member.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The format is unknown or unsupported.</exception>
    public static RhiFormat FromVTexFormat(VTexFormat format, bool srgb = false) => format switch
    {
        VTexFormat.DXT1 => srgb ? RhiFormat.BC1_RGBA_SRgb : RhiFormat.BC1_RGBA_UNorm,
        VTexFormat.DXT5 => srgb ? RhiFormat.BC3_SRgb : RhiFormat.BC3_UNorm,
        VTexFormat.ATI1N => RhiFormat.BC4_UNorm,
        VTexFormat.ATI2N => RhiFormat.BC5_UNorm,
        VTexFormat.BC6H => RhiFormat.BC6H_UFloat,
        VTexFormat.BC7 => srgb ? RhiFormat.BC7_SRgb : RhiFormat.BC7_UNorm,
        VTexFormat.ETC2 => srgb ? RhiFormat.ETC2_R8G8B8_SRgb : RhiFormat.ETC2_R8G8B8_UNorm,
        VTexFormat.ETC2_EAC => srgb ? RhiFormat.ETC2_R8G8B8A8_SRgb : RhiFormat.ETC2_R8G8B8A8_UNorm,

        VTexFormat.I8 => RhiFormat.R8_UNorm,
        VTexFormat.IA88 => RhiFormat.R8G8_UNorm,
        VTexFormat.RGBA8888 => srgb ? RhiFormat.R8G8B8A8_SRgb : RhiFormat.R8G8B8A8_UNorm,
        VTexFormat.BGRA8888 => srgb ? RhiFormat.B8G8R8A8_SRgb : RhiFormat.B8G8R8A8_UNorm,

        VTexFormat.R16 => RhiFormat.R16_UNorm,
        VTexFormat.RG1616 => RhiFormat.R16G16_UNorm,
        VTexFormat.RGBA16161616 => RhiFormat.R16G16B16A16_UNorm,

        VTexFormat.R16F => RhiFormat.R16_SFloat,
        VTexFormat.RG1616F => RhiFormat.R16G16_SFloat,
        VTexFormat.RGBA16161616F => RhiFormat.R16G16B16A16_SFloat,

        VTexFormat.R32F => RhiFormat.R32_SFloat,
        VTexFormat.RG3232F => RhiFormat.R32G32_SFloat,
        VTexFormat.RGB323232F => RhiFormat.R32G32B32_SFloat,
        VTexFormat.RGBA32323232F => RhiFormat.R32G32B32A32_SFloat,

        VTexFormat.JPEG_RGBA8888 or VTexFormat.PNG_RGBA8888 or VTexFormat.WEBP_RGBA8888
            or VTexFormat.JPEG_DXT5 or VTexFormat.PNG_DXT5 or VTexFormat.WEBP_DXT5
            => throw new NotSupportedException($"{format} is an encoded image, decode it to a bitmap before choosing an upload format."),

        VTexFormat.R11_EAC or VTexFormat.RG11_EAC
            => throw new NotSupportedException($"RhiFormat has no single or dual channel EAC member yet, needed by VTexFormat.{format}."),

        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "No RhiFormat for this texture format."),
    };

    /// <summary>
    /// Gets the size in bytes of one compression block, or of one texel for uncompressed formats.
    /// </summary>
    /// <param name="format">The format to measure.</param>
    /// <returns>The block size in bytes, 0 for <see cref="RhiFormat.Undefined"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format is unknown.</exception>
    /// <remarks>
    /// <see cref="RhiFormat.D32_SFloat_S8_UInt"/> reports 5, its depth plus stencil payload. Its actual
    /// layout is implementation defined and OpenGL transfers it as an 8 byte
    /// <see cref="PixelType.Float32UnsignedInt248Rev"/> texel, so do not size a transfer with this.
    /// </remarks>
    public static int BytesPerBlock(RhiFormat format) => format switch
    {
        RhiFormat.Undefined => 0,

        RhiFormat.R8_UNorm or RhiFormat.R8_UInt => 1,
        RhiFormat.R8G8_UNorm or RhiFormat.R16_UNorm or RhiFormat.R16_SFloat or RhiFormat.R16_UInt
            or RhiFormat.D16_UNorm => 2,
        RhiFormat.R8G8B8_UNorm or RhiFormat.R8G8B8_SRgb => 3,
        RhiFormat.R8G8B8A8_UNorm or RhiFormat.R8G8B8A8_SRgb or RhiFormat.R8G8B8A8_SNorm
            or RhiFormat.B8G8R8A8_UNorm or RhiFormat.B8G8R8A8_SRgb or RhiFormat.R10G10B10A2_UNorm
            or RhiFormat.R16G16_UNorm or RhiFormat.R16G16_SNorm or RhiFormat.R16G16_SFloat
            or RhiFormat.R32_SFloat or RhiFormat.R32_UInt or RhiFormat.R32_SInt
            or RhiFormat.B10G11R11_UFloat or RhiFormat.D32_SFloat or RhiFormat.D24_UNorm_S8_UInt
            or RhiFormat.R8G8B8A8_UInt or RhiFormat.R16G16_SInt => 4,
        RhiFormat.D32_SFloat_S8_UInt => 5,
        RhiFormat.R16G16B16A16_UNorm or RhiFormat.R16G16B16A16_SFloat or RhiFormat.R32G32_SFloat
            or RhiFormat.R32G32_UInt or RhiFormat.R16G16B16A16_UInt or RhiFormat.R16G16B16A16_SInt => 8,
        RhiFormat.R32G32B32_SFloat => 12,
        RhiFormat.R32G32B32A32_SFloat or RhiFormat.R32G32B32A32_UInt or RhiFormat.R32G32B32A32_SInt => 16,

        // 4x4 blocks: 64 bits for the one endpoint pair formats, 128 bits for the rest
        RhiFormat.BC1_RGBA_UNorm or RhiFormat.BC1_RGBA_SRgb or RhiFormat.BC4_UNorm or RhiFormat.BC4_SNorm
            or RhiFormat.ETC2_R8G8B8_UNorm or RhiFormat.ETC2_R8G8B8_SRgb => 8,
        RhiFormat.BC2_UNorm or RhiFormat.BC2_SRgb or RhiFormat.BC3_UNorm or RhiFormat.BC3_SRgb
            or RhiFormat.BC5_UNorm or RhiFormat.BC5_SNorm or RhiFormat.BC6H_UFloat or RhiFormat.BC6H_SFloat
            or RhiFormat.BC7_UNorm or RhiFormat.BC7_SRgb
            or RhiFormat.ETC2_R8G8B8A8_UNorm or RhiFormat.ETC2_R8G8B8A8_SRgb => 16,

        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown RhiFormat has no block size."),
    };

    /// <summary>Gets the width in texels of one compression block, 1 for uncompressed formats.</summary>
    /// <param name="format">The format to measure.</param>
    /// <returns>4 for the BC family, otherwise 1.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format is unknown.</exception>
    public static int BlockWidth(RhiFormat format) => IsKnown(format)
        ? RhiFormatInfo.BlockDimension(format)
        : throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown RhiFormat has no block width.");

    /// <summary>Gets the height in texels of one compression block, 1 for uncompressed formats.</summary>
    /// <param name="format">The format to measure.</param>
    /// <returns>4 for the BC family, otherwise 1. Every format the RHI carries has square blocks.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format is unknown.</exception>
    public static int BlockHeight(RhiFormat format) => IsKnown(format)
        ? RhiFormatInfo.BlockDimension(format)
        : throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown RhiFormat has no block height.");

    /// <summary>
    /// Gets the number of channels the format stores, counting only what a shader can read back.
    /// </summary>
    /// <param name="format">The format to measure.</param>
    /// <returns>The channel count, 0 for <see cref="RhiFormat.Undefined"/>. Depth-stencil formats count 2.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format is unknown.</exception>
    public static int ChannelCount(RhiFormat format) => format switch
    {
        RhiFormat.Undefined => 0,

        RhiFormat.R8_UNorm or RhiFormat.R16_UNorm or RhiFormat.R16_SFloat or RhiFormat.R32_SFloat
            or RhiFormat.R8_UInt or RhiFormat.R16_UInt or RhiFormat.R32_UInt or RhiFormat.R32_SInt
            or RhiFormat.BC4_UNorm or RhiFormat.BC4_SNorm
            or RhiFormat.D16_UNorm or RhiFormat.D32_SFloat => 1,

        RhiFormat.R8G8_UNorm or RhiFormat.R16G16_UNorm or RhiFormat.R16G16_SNorm or RhiFormat.R16G16_SFloat
            or RhiFormat.R32G32_SFloat or RhiFormat.R32G32_UInt or RhiFormat.R16G16_SInt
            or RhiFormat.BC5_UNorm or RhiFormat.BC5_SNorm
            or RhiFormat.D24_UNorm_S8_UInt or RhiFormat.D32_SFloat_S8_UInt => 2,

        RhiFormat.R8G8B8_UNorm or RhiFormat.R8G8B8_SRgb or RhiFormat.R32G32B32_SFloat
            or RhiFormat.B10G11R11_UFloat or RhiFormat.BC6H_UFloat or RhiFormat.BC6H_SFloat
            or RhiFormat.ETC2_R8G8B8_UNorm or RhiFormat.ETC2_R8G8B8_SRgb => 3,

        RhiFormat.R8G8B8A8_UNorm or RhiFormat.R8G8B8A8_SRgb or RhiFormat.R8G8B8A8_SNorm
            or RhiFormat.B8G8R8A8_UNorm or RhiFormat.B8G8R8A8_SRgb or RhiFormat.R10G10B10A2_UNorm
            or RhiFormat.R16G16B16A16_UNorm or RhiFormat.R16G16B16A16_SFloat
            or RhiFormat.R32G32B32A32_SFloat or RhiFormat.R32G32B32A32_UInt
            or RhiFormat.R8G8B8A8_UInt or RhiFormat.R16G16B16A16_UInt or RhiFormat.R16G16B16A16_SInt
            or RhiFormat.R32G32B32A32_SInt
            or RhiFormat.BC1_RGBA_UNorm or RhiFormat.BC1_RGBA_SRgb or RhiFormat.BC2_UNorm or RhiFormat.BC2_SRgb
            or RhiFormat.BC3_UNorm or RhiFormat.BC3_SRgb or RhiFormat.BC7_UNorm or RhiFormat.BC7_SRgb
            or RhiFormat.ETC2_R8G8B8A8_UNorm or RhiFormat.ETC2_R8G8B8A8_SRgb => 4,

        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown RhiFormat has no channel count."),
    };

    private static bool IsKnown(RhiFormat format)
        => format is >= RhiFormat.Undefined and <= RhiFormat.D32_SFloat_S8_UInt;
}
