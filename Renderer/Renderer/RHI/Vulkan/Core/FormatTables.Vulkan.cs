using Silk.NET.Vulkan;

namespace ValveResourceFormat.Renderer.RHI;

/// <content>
/// The Silk.NET-typed half of the format tables.
/// </content>
/// <remarks>
/// <para>
/// <see cref="FormatTables.ToVulkanFormat"/> returns the raw <c>VkFormat</c> enumerant as a number so
/// that <c>RHI/FormatTables.cs</c> need not reference Silk.NET. Its own remarks ask for exactly this
/// partial once the package landed, so the numeric table stays the single source of truth and this
/// file adds only the cast.
/// </para>
/// <para>
/// It lives under <c>RHI/Vulkan/Core/</c> rather than next to the rest of the class because that is
/// the directory this agent owns. A partial class does not care which directory its parts sit in, and
/// putting it here avoids editing a file owned by another agent. Move it beside
/// <c>FormatTables.cs</c> whenever ownership allows.
/// </para>
/// </remarks>
public static partial class FormatTables
{
    /// <summary>Gets the Vulkan format a <see cref="RhiFormat"/> maps to.</summary>
    /// <param name="format">The format to translate.</param>
    /// <returns>The matching <see cref="Format"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format has no Vulkan equivalent.</exception>
    public static Format ToVkFormat(RhiFormat format) => (Format)ToVulkanFormat(format);

    /// <summary>Gets the image aspect mask a format's views and copies address.</summary>
    /// <param name="format">The format to inspect.</param>
    /// <param name="aspect">Which aspect of a depth-stencil format is wanted.</param>
    /// <returns>The matching <see cref="ImageAspectFlags"/>.</returns>
    /// <remarks>Colour formats ignore <paramref name="aspect"/> entirely: naming anything other than
    /// the colour aspect on a colour image is a validation error, not a no-op.</remarks>
    public static ImageAspectFlags ToVkAspect(RhiFormat format, TextureAspect aspect = TextureAspect.All)
    {
        if (!RhiFormatInfo.IsDepth(format))
        {
            return ImageAspectFlags.ColorBit;
        }

        var hasStencil = format is RhiFormat.D24_UNorm_S8_UInt or RhiFormat.D32_SFloat_S8_UInt;

        return aspect switch
        {
            TextureAspect.Depth => ImageAspectFlags.DepthBit,
            TextureAspect.Stencil => hasStencil
                ? ImageAspectFlags.StencilBit
                : throw new ArgumentOutOfRangeException(nameof(aspect), aspect, "This format has no stencil aspect."),
            _ => hasStencil
                ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
                : ImageAspectFlags.DepthBit,
        };
    }
}
