namespace ValveResourceFormat.Renderer.RHI;

/// <summary>
/// API-neutral pixel and vertex format. Replaces the OpenTK <c>SizedInternalFormat</c>,
/// <c>PixelInternalFormat</c>, <c>PixelFormat</c> and <c>PixelType</c> quadruple that leaked into the
/// renderer's public surface, and maps one-to-one onto <c>VkFormat</c>.
/// </summary>
/// <remarks>
/// The members are exactly the formats the renderer uses today, derived from
/// <see cref="Materials.MaterialLoader"/>, <see cref="Framebuffer"/> and <see cref="RenderTexture"/>.
/// Adding a member is cheap; a backend that cannot map one must throw rather than substitute, so a
/// missing format fails loudly at load time instead of rendering black.
/// </remarks>
public enum RhiFormat
{
    /// <summary>No format. Valid only where a format is explicitly optional.</summary>
    Undefined = 0,

    // Unorm colour

    /// <summary>Single channel, 8 bit normalized.</summary>
    R8_UNorm,
    /// <summary>Two channel, 8 bit normalized.</summary>
    R8G8_UNorm,
    /// <summary>Four channel, 8 bit normalized.</summary>
    R8G8B8A8_UNorm,
    /// <summary>Four channel, 8 bit normalized, sRGB encoded.</summary>
    R8G8B8A8_SRgb,
    /// <summary>Four channel, 8 bit normalized, blue first. Common swapchain format.</summary>
    B8G8R8A8_UNorm,
    /// <summary>Four channel, 8 bit normalized, blue first, sRGB encoded.</summary>
    B8G8R8A8_SRgb,
    /// <summary>Three channel, 8 bit normalized. Storage only; not renderable on all backends.</summary>
    R8G8B8_UNorm,
    /// <summary>Three channel, 8 bit normalized, sRGB encoded.</summary>
    R8G8B8_SRgb,
    /// <summary>Single channel, 16 bit normalized.</summary>
    R16_UNorm,
    /// <summary>Two channel, 16 bit normalized.</summary>
    R16G16_UNorm,
    /// <summary>Four channel, 16 bit normalized.</summary>
    R16G16B16A16_UNorm,
    /// <summary>Ten bit RGB with two bit alpha, normalized.</summary>
    R10G10B10A2_UNorm,

    // Float colour

    /// <summary>Single channel, 16 bit float.</summary>
    R16_SFloat,
    /// <summary>Two channel, 16 bit float.</summary>
    R16G16_SFloat,
    /// <summary>Four channel, 16 bit float.</summary>
    R16G16B16A16_SFloat,
    /// <summary>Single channel, 32 bit float. The depth pyramid storage format.</summary>
    R32_SFloat,
    /// <summary>Two channel, 32 bit float.</summary>
    R32G32_SFloat,
    /// <summary>Four channel, 32 bit float.</summary>
    R32G32B32A32_SFloat,
    /// <summary>Packed float HDR colour with no alpha.</summary>
    B10G11R11_UFloat,

    // Integer

    /// <summary>Single channel, 8 bit unsigned integer.</summary>
    R8_UInt,
    /// <summary>Single channel, 16 bit unsigned integer.</summary>
    R16_UInt,
    /// <summary>Single channel, 32 bit unsigned integer.</summary>
    R32_UInt,
    /// <summary>Two channel, 32 bit unsigned integer.</summary>
    R32G32_UInt,
    /// <summary>Four channel, 32 bit unsigned integer.</summary>
    R32G32B32A32_UInt,
    /// <summary>Single channel, 32 bit signed integer.</summary>
    R32_SInt,

    // Vertex-only float formats

    /// <summary>Three channel, 32 bit float. Vertex input only on most backends.</summary>
    R32G32B32_SFloat,
    /// <summary>Two channel, 16 bit signed normalized.</summary>
    R16G16_SNorm,
    /// <summary>Four channel, 8 bit signed normalized.</summary>
    R8G8B8A8_SNorm,

    // Block compressed

    /// <summary>BC1 (DXT1), RGB with one bit alpha.</summary>
    BC1_RGBA_UNorm,
    /// <summary>BC1 (DXT1), sRGB encoded.</summary>
    BC1_RGBA_SRgb,
    /// <summary>BC2 (DXT3), RGBA with explicit alpha.</summary>
    BC2_UNorm,
    /// <summary>BC2 (DXT3), sRGB encoded.</summary>
    BC2_SRgb,
    /// <summary>BC3 (DXT5), RGBA with interpolated alpha.</summary>
    BC3_UNorm,
    /// <summary>BC3 (DXT5), sRGB encoded.</summary>
    BC3_SRgb,
    /// <summary>BC4, single channel.</summary>
    BC4_UNorm,
    /// <summary>BC4, single channel signed.</summary>
    BC4_SNorm,
    /// <summary>BC5, two channel. The normal map format.</summary>
    BC5_UNorm,
    /// <summary>BC5, two channel signed.</summary>
    BC5_SNorm,
    /// <summary>BC6H, HDR RGB, unsigned.</summary>
    BC6H_UFloat,
    /// <summary>BC6H, HDR RGB, signed.</summary>
    BC6H_SFloat,
    /// <summary>BC7, high quality RGBA.</summary>
    BC7_UNorm,
    /// <summary>BC7, sRGB encoded.</summary>
    BC7_SRgb,

    // Depth and stencil

    /// <summary>16 bit normalized depth.</summary>
    D16_UNorm,
    /// <summary>32 bit float depth. The renderer's reverse-Z default.</summary>
    D32_SFloat,
    /// <summary>24 bit normalized depth with 8 bit stencil.</summary>
    D24_UNorm_S8_UInt,
    /// <summary>32 bit float depth with 8 bit stencil.</summary>
    D32_SFloat_S8_UInt,
}

/// <summary>
/// Static facts about a <see cref="RhiFormat"/> that every backend agrees on. Backend-specific
/// translation tables live with their backend; this is the shared, API-neutral part.
/// </summary>
public static class RhiFormatInfo
{
    /// <summary>Gets a value indicating whether the format is block compressed.</summary>
    /// <param name="format">The format to test.</param>
    /// <returns><see langword="true"/> for the BC family.</returns>
    public static bool IsCompressed(RhiFormat format) => format is >= RhiFormat.BC1_RGBA_UNorm and <= RhiFormat.BC7_SRgb;

    /// <summary>Gets a value indicating whether the format carries a depth channel.</summary>
    /// <param name="format">The format to test.</param>
    /// <returns><see langword="true"/> for depth and depth-stencil formats.</returns>
    public static bool IsDepth(RhiFormat format) => format is >= RhiFormat.D16_UNorm and <= RhiFormat.D32_SFloat_S8_UInt;

    /// <summary>Gets a value indicating whether the format carries a stencil channel.</summary>
    /// <param name="format">The format to test.</param>
    /// <returns><see langword="true"/> for depth-stencil formats.</returns>
    public static bool IsStencil(RhiFormat format) => format is RhiFormat.D24_UNorm_S8_UInt or RhiFormat.D32_SFloat_S8_UInt;

    /// <summary>Gets a value indicating whether sampling the format applies an sRGB to linear conversion.</summary>
    /// <param name="format">The format to test.</param>
    /// <returns><see langword="true"/> for sRGB encoded formats.</returns>
    public static bool IsSRgb(RhiFormat format) => format
        is RhiFormat.R8G8B8A8_SRgb or RhiFormat.B8G8R8A8_SRgb or RhiFormat.R8G8B8_SRgb
        or RhiFormat.BC1_RGBA_SRgb or RhiFormat.BC2_SRgb or RhiFormat.BC3_SRgb or RhiFormat.BC7_SRgb;

    /// <summary>Gets the edge length in texels of one compression block, or 1 for uncompressed formats.</summary>
    /// <param name="format">The format to measure.</param>
    /// <returns>4 for the BC family, otherwise 1.</returns>
    public static int BlockDimension(RhiFormat format) => IsCompressed(format) ? 4 : 1;
}
