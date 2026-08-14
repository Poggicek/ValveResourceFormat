using System;
using System.Collections.Generic;
using NUnit.Framework;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Renderer.RHI;

namespace Tests.Renderer
{
    public class FormatTablesTest
    {
        private const RhiFormat UnknownFormat = (RhiFormat)1000;

        private static readonly RhiFormat[] AllFormats = Enum.GetValues<RhiFormat>();

        /// <summary>Every format the RHI carries other than <see cref="RhiFormat.Undefined"/>, which no
        /// texture can be allocated with.</summary>
        private static IEnumerable<RhiFormat> RealFormats()
        {
            foreach (var format in AllFormats)
            {
                if (format != RhiFormat.Undefined)
                {
                    yield return format;
                }
            }
        }

        /// <summary>The vertex attribute formats <see cref="VBIB.GetFormatInfo(DXGI_FORMAT, string)"/>
        /// decodes, which is exactly what the renderer can be handed.</summary>
        private static readonly DXGI_FORMAT[] SupportedVertexFormats =
        [
            DXGI_FORMAT.R8G8B8A8_UINT,
            DXGI_FORMAT.R8G8B8A8_UNORM,
            DXGI_FORMAT.R16G16_FLOAT,
            DXGI_FORMAT.R16G16_SINT,
            DXGI_FORMAT.R16G16_SNORM,
            DXGI_FORMAT.R16G16_UNORM,
            DXGI_FORMAT.R16G16B16A16_FLOAT,
            DXGI_FORMAT.R16G16B16A16_SINT,
            DXGI_FORMAT.R16G16B16A16_UINT,
            DXGI_FORMAT.R16G16B16A16_UNORM,
            DXGI_FORMAT.R32_FLOAT,
            DXGI_FORMAT.R32_UINT,
            DXGI_FORMAT.R32G32_FLOAT,
            DXGI_FORMAT.R32G32B32_FLOAT,
            DXGI_FORMAT.R32G32B32A32_FLOAT,
            DXGI_FORMAT.R32G32B32A32_SINT,
        ];

        /// <summary>The texture formats MaterialLoader uploads without decoding them first.</summary>
        private static readonly VTexFormat[] UploadableTextureFormats =
        [
            VTexFormat.DXT1,
            VTexFormat.DXT5,
            VTexFormat.ATI1N,
            VTexFormat.ATI2N,
            VTexFormat.BC6H,
            VTexFormat.BC7,
            VTexFormat.ETC2,
            VTexFormat.ETC2_EAC,
            VTexFormat.I8,
            VTexFormat.IA88,
            VTexFormat.RGBA8888,
            VTexFormat.BGRA8888,
            VTexFormat.R16,
            VTexFormat.RG1616,
            VTexFormat.RGBA16161616,
            VTexFormat.R16F,
            VTexFormat.RG1616F,
            VTexFormat.RGBA16161616F,
            VTexFormat.R32F,
            VTexFormat.RG3232F,
            VTexFormat.RGB323232F,
            VTexFormat.RGBA32323232F,
        ];

        [Test]
        public void EveryFormatMapsToOpenGL()
        {
            foreach (var format in RealFormats())
            {
                // Throwing here is the failure: an unmapped format must never reach a backend
                FormatTables.ToGLSizedInternalFormat(format);
                FormatTables.ToGLPixelInternalFormat(format);

                var pixelFormat = FormatTables.ToGLPixelFormat(format);
                var pixelType = FormatTables.ToGLPixelType(format);

                if (RhiFormatInfo.IsCompressed(format))
                {
                    Assert.That(pixelFormat, Is.Null, $"{format} is compressed and uploads through glCompressedTextureSubImage");
                    Assert.That(pixelType, Is.Null, $"{format} is compressed and has no client pixel type");
                }
                else
                {
                    Assert.That(pixelFormat, Is.Not.Null, $"{format} has no client pixel format");
                    Assert.That(pixelType, Is.Not.Null, $"{format} has no client pixel type");
                }
            }
        }

        [Test]
        public void UndefinedHasNoOpenGLFormat()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FormatTables.ToGLSizedInternalFormat(RhiFormat.Undefined));
            Assert.Throws<ArgumentOutOfRangeException>(() => FormatTables.ToGLPixelInternalFormat(RhiFormat.Undefined));
            Assert.Throws<ArgumentOutOfRangeException>(() => FormatTables.ToGLPixelFormat(RhiFormat.Undefined));
            Assert.Throws<ArgumentOutOfRangeException>(() => FormatTables.ToGLPixelType(RhiFormat.Undefined));
        }

        [Test]
        public void EveryFormatMapsToAUniqueVulkanFormat()
        {
            var seen = new Dictionary<uint, RhiFormat>(AllFormats.Length);

            foreach (var format in AllFormats)
            {
                var vkFormat = FormatTables.ToVulkanFormat(format);

                if (format == RhiFormat.Undefined)
                {
                    Assert.That(vkFormat, Is.Zero, "Undefined must map to VK_FORMAT_UNDEFINED");
                }
                else
                {
                    Assert.That(vkFormat, Is.Not.Zero, $"{format} maps to VK_FORMAT_UNDEFINED");
                }

                Assert.That(seen.TryAdd(vkFormat, format), Is.True, $"{format} and {seen.GetValueOrDefault(vkFormat)} both map to VkFormat {vkFormat}");
            }
        }

        [Test]
        public void EveryFormatHasSizeAndChannelInformation()
        {
            foreach (var format in RealFormats())
            {
                var expectedBlock = RhiFormatInfo.IsCompressed(format) ? 4 : 1;

                Assert.That(FormatTables.BytesPerBlock(format), Is.GreaterThan(0), $"{format} has no block size");
                Assert.That(FormatTables.BlockWidth(format), Is.EqualTo(expectedBlock), $"{format} has the wrong block width");
                Assert.That(FormatTables.BlockHeight(format), Is.EqualTo(expectedBlock), $"{format} has the wrong block height");
                Assert.That(FormatTables.ChannelCount(format), Is.InRange(1, 4), $"{format} has an implausible channel count");
            }
        }

        [Test]
        public void UndefinedHasNoSizeOrChannels()
        {
            Assert.That(FormatTables.BytesPerBlock(RhiFormat.Undefined), Is.Zero);
            Assert.That(FormatTables.ChannelCount(RhiFormat.Undefined), Is.Zero);
            Assert.That(FormatTables.BlockWidth(RhiFormat.Undefined), Is.EqualTo(1));
            Assert.That(FormatTables.BlockHeight(RhiFormat.Undefined), Is.EqualTo(1));
        }

        [Test]
        public void UnknownFormatThrowsInEveryTable()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FormatTables.ToGLSizedInternalFormat(UnknownFormat));
            Assert.Throws<ArgumentOutOfRangeException>(() => FormatTables.ToGLPixelInternalFormat(UnknownFormat));
            Assert.Throws<ArgumentOutOfRangeException>(() => FormatTables.ToGLPixelFormat(UnknownFormat));
            Assert.Throws<ArgumentOutOfRangeException>(() => FormatTables.ToGLPixelType(UnknownFormat));
            Assert.Throws<ArgumentOutOfRangeException>(() => FormatTables.ToVulkanFormat(UnknownFormat));
            Assert.Throws<ArgumentOutOfRangeException>(() => FormatTables.BytesPerBlock(UnknownFormat));
            Assert.Throws<ArgumentOutOfRangeException>(() => FormatTables.BlockWidth(UnknownFormat));
            Assert.Throws<ArgumentOutOfRangeException>(() => FormatTables.BlockHeight(UnknownFormat));
            Assert.Throws<ArgumentOutOfRangeException>(() => FormatTables.ChannelCount(UnknownFormat));
        }

        [Test]
        public void EveryVertexFormatMapsFromDxgiWithTheSameStride()
        {
            foreach (var dxgi in SupportedVertexFormats)
            {
                // Throwing here is the failure: every format VBIB decodes must have an RhiFormat
                var format = FormatTables.FromDxgiFormat(dxgi);
                var (elementSize, elementCount) = VBIB.GetFormatInfo(dxgi);

                Assert.That(FormatTables.BytesPerBlock(format), Is.EqualTo(elementSize * elementCount),
                    $"{dxgi} maps to {format}, which is a different size");
                Assert.That(FormatTables.ChannelCount(format), Is.EqualTo(elementCount),
                    $"{dxgi} maps to {format}, which has a different channel count");
            }
        }

        [Test]
        public void UnmappedDxgiFormatThrows()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FormatTables.FromDxgiFormat(DXGI_FORMAT.UNKNOWN));
            Assert.Throws<ArgumentOutOfRangeException>(() => FormatTables.FromDxgiFormat(DXGI_FORMAT.NV12));
            Assert.Throws<ArgumentOutOfRangeException>(() => FormatTables.FromDxgiFormat(DXGI_FORMAT.R32G32B32A32_TYPELESS));
        }

        [Test]
        public void EveryUploadableTextureFormatMaps()
        {
            foreach (var vtexFormat in UploadableTextureFormats)
            {
                var linear = FormatTables.FromVTexFormat(vtexFormat);
                var srgb = FormatTables.FromVTexFormat(vtexFormat, srgb: true);

                Assert.That(RhiFormatInfo.IsSRgb(linear), Is.False, $"{vtexFormat} reads back sRGB when it was not asked to");

                // Formats with no sRGB pair fall back to the linear one, matching the existing upload path
                Assert.That(srgb, Is.EqualTo(linear).Or.Matches<RhiFormat>(RhiFormatInfo.IsSRgb),
                    $"{vtexFormat} maps to {srgb} when read as sRGB");
            }
        }

        [Test]
        public void UnsupportedTextureFormatsThrow()
        {
            Assert.Throws<NotSupportedException>(() => FormatTables.FromVTexFormat(VTexFormat.PNG_RGBA8888));
            Assert.Throws<NotSupportedException>(() => FormatTables.FromVTexFormat(VTexFormat.R11_EAC));
            Assert.Throws<NotSupportedException>(() => FormatTables.FromVTexFormat(VTexFormat.RG11_EAC));
            Assert.Throws<ArgumentOutOfRangeException>(() => FormatTables.FromVTexFormat(VTexFormat.UNKNOWN));
        }
    }
}
