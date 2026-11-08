using System.Globalization;
using System.Threading;
using OpenTK.Graphics.OpenGL;

namespace ValveResourceFormat.Renderer;

/// <summary>
/// Every texture and buffer a <see cref="GraphicsDevice"/> created, with the size of its storage
/// read back from the driver. Storage is allocated at many call sites, so sizes are queried from the
/// objects rather than recorded where they are allocated.
/// </summary>
internal sealed class GpuObjectRegistry
{
    /// <summary>One texture or buffer, measured by the last <see cref="Measure"/>.</summary>
    internal sealed class Entry(string label, TextureTarget? target, bool isView)
    {
        public string Label { get; } = label;

        /// <summary>Null for a buffer.</summary>
        public TextureTarget? Target { get; } = target;

        /// <summary>A texture view, which owns no storage of its own.</summary>
        public bool IsView { get; } = isView;

        public long Bytes { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Depth { get; set; }
        public int Levels { get; set; }
        public int Samples { get; set; }
        public string FormatName { get; set; } = string.Empty;

        /// <summary>Immutable texture storage can not change once measured.</summary>
        public bool StorageFixed { get; set; }
    }

    private readonly Lock sync = new();
    private readonly Dictionary<int, Entry> textures = [];
    private readonly Dictionary<int, Entry> buffers = [];
    private readonly List<int> deleted = [];

    // A deleted name is handed out again by the next create, which replaces the stale entry
    public void AddTexture(int handle, TextureTarget target, string label, bool isView)
    {
        using var _ = sync.EnterScope();
        textures[handle] = new Entry(label, target, isView);
    }

    public void AddBuffer(int handle, string label)
    {
        using var _ = sync.EnterScope();
        buffers[handle] = new Entry(label, null, isView: false);
    }

    /// <summary>
    /// Forgets deleted objects and measures the live ones. Needs a context of this device current.
    /// </summary>
    public void Measure(List<Entry> liveTextures, List<Entry> liveBuffers)
    {
        using var _ = sync.EnterScope();

        deleted.Clear();

        foreach (var (handle, entry) in textures)
        {
            if (!GL.IsTexture(handle))
            {
                deleted.Add(handle);
                continue;
            }

            if (!entry.StorageFixed && !entry.IsView)
            {
                MeasureTexture(handle, entry);
            }

            liveTextures.Add(entry);
        }

        foreach (var handle in deleted)
        {
            textures.Remove(handle);
        }

        deleted.Clear();

        foreach (var (handle, entry) in buffers)
        {
            if (!GL.IsBuffer(handle))
            {
                deleted.Add(handle);
                continue;
            }

            // Mutable buffers are reallocated in place, so these are read every time
            GL.GetNamedBufferParameter(handle, BufferParameterName.BufferSize, out long size);
            entry.Bytes = size;
            liveBuffers.Add(entry);
        }

        foreach (var handle in deleted)
        {
            buffers.Remove(handle);
        }
    }

    private static void MeasureTexture(int handle, Entry entry)
    {
        var target = entry.Target!.Value;

        // Storage is always immutable here, so a texture without it has not been allocated yet
        GL.GetTextureParameter(handle, (GetTextureParameter)All.TextureImmutableFormat, out int immutable);

        if (immutable == 0)
        {
            return;
        }

        GL.GetTextureParameter(handle, (GetTextureParameter)All.TextureImmutableLevels, out int levels);
        GL.GetTextureLevelParameter(handle, 0, GetTextureParameter.TextureWidth, out int width);
        GL.GetTextureLevelParameter(handle, 0, GetTextureParameter.TextureHeight, out int height);
        GL.GetTextureLevelParameter(handle, 0, GetTextureParameter.TextureDepth, out int depth);
        GL.GetTextureLevelParameter(handle, 0, GetTextureParameter.TextureInternalFormat, out int internalFormat);
        GL.GetTextureLevelParameter(handle, 0, (GetTextureParameter)All.TextureSamples, out int samples);

        var (formatName, blockSize, blockBytes) = DescribeFormat((All)internalFormat);

        if (blockBytes == 0)
        {
            blockBytes = QueryTexelBytes(handle);
        }

        var bytes = 0L;

        for (var level = 0; level < levels; level++)
        {
            var (levelWidth, levelHeight, levelDepth) = MaterialLoader.GetChainLevelSize(target, width, height, depth, level);
            var blocksWide = (levelWidth + blockSize - 1) / blockSize;
            var blocksHigh = (levelHeight + blockSize - 1) / blockSize;

            bytes += (long)blocksWide * blocksHigh * levelDepth * blockBytes;
        }

        if (target == TextureTarget.TextureCubeMap)
        {
            bytes *= 6;
        }

        entry.Bytes = bytes * Math.Max(1, samples);
        entry.Width = width;
        entry.Height = height;
        entry.Depth = target == TextureTarget.TextureCubeMap ? 6 : depth;
        entry.Levels = levels;
        entry.Samples = samples;
        entry.FormatName = formatName;
        entry.StorageFixed = true;
    }

    /// <summary>Bytes per texel of an uncompressed format missing from <see cref="DescribeFormat"/>, from its component sizes.</summary>
    private static int QueryTexelBytes(int handle)
    {
        ReadOnlySpan<All> components = [All.TextureRedSize, All.TextureGreenSize, All.TextureBlueSize, All.TextureAlphaSize, All.TextureDepthSize, All.TextureStencilSize];
        var bits = 0;

        foreach (var component in components)
        {
            GL.GetTextureLevelParameter(handle, 0, (GetTextureParameter)component, out int size);
            bits += size;
        }

        return Math.Max(1, (bits + 7) / 8);
    }

    /// <summary>
    /// Display name, block edge in texels, and bytes per block (per texel for uncompressed formats).
    /// Zero bytes means the format is not listed. Three component formats count the padding drivers
    /// store them with.
    /// </summary>
    private static (string Name, int BlockSize, int BlockBytes) DescribeFormat(All format) => format switch
    {
        All.CompressedRgbS3tcDxt1Ext or All.CompressedRgbaS3tcDxt1Ext => ("BC1", 4, 8),
        All.CompressedSrgbS3tcDxt1Ext or All.CompressedSrgbAlphaS3tcDxt1Ext => ("BC1 sRGB", 4, 8),
        All.CompressedRgbaS3tcDxt3Ext => ("BC2", 4, 16),
        All.CompressedSrgbAlphaS3tcDxt3Ext => ("BC2 sRGB", 4, 16),
        All.CompressedRgbaS3tcDxt5Ext => ("BC3", 4, 16),
        All.CompressedSrgbAlphaS3tcDxt5Ext => ("BC3 sRGB", 4, 16),
        All.CompressedRedRgtc1 or All.CompressedSignedRedRgtc1 => ("BC4", 4, 8),
        All.CompressedRgRgtc2 or All.CompressedSignedRgRgtc2 => ("BC5", 4, 16),
        All.CompressedRgbBptcUnsignedFloat or All.CompressedRgbBptcSignedFloat => ("BC6H", 4, 16),
        All.CompressedRgbaBptcUnorm => ("BC7", 4, 16),
        All.CompressedSrgbAlphaBptcUnorm => ("BC7 sRGB", 4, 16),
        All.CompressedRgb8Etc2 or All.CompressedSrgb8Etc2 => ("ETC2", 4, 8),
        All.CompressedRgba8Etc2Eac or All.CompressedSrgb8Alpha8Etc2Eac => ("ETC2 EAC", 4, 16),
        All.CompressedR11Eac or All.CompressedSignedR11Eac => ("EAC R11", 4, 8),
        All.CompressedRg11Eac or All.CompressedSignedRg11Eac => ("EAC RG11", 4, 16),
        All.R8 => ("R8", 1, 1),
        All.Rg8 => ("RG8", 1, 2),
        All.Rgb8 => ("RGB8", 1, 4),
        All.Rgba8 => ("RGBA8", 1, 4),
        All.Srgb8Alpha8 => ("RGBA8 sRGB", 1, 4),
        All.Rgb10A2 => ("RGB10A2", 1, 4),
        All.R11fG11fB10f => ("R11G11B10F", 1, 4),
        All.R16 => ("R16", 1, 2),
        All.R16f => ("R16F", 1, 2),
        All.Rg16 => ("RG16", 1, 4),
        All.Rg16f => ("RG16F", 1, 4),
        All.Rgba16 => ("RGBA16", 1, 8),
        All.Rgba16f => ("RGBA16F", 1, 8),
        All.R32f => ("R32F", 1, 4),
        All.R32ui => ("R32UI", 1, 4),
        All.Rg32f => ("RG32F", 1, 8),
        All.Rgba32f => ("RGBA32F", 1, 16),
        All.Rgba32ui => ("RGBA32UI", 1, 16),
        All.DepthComponent16 => ("D16", 1, 2),
        All.DepthComponent24 => ("D24", 1, 4),
        All.DepthComponent32f => ("D32F", 1, 4),
        All.Depth24Stencil8 => ("D24S8", 1, 4),
        All.Depth32fStencil8 => ("D32FS8", 1, 8),
        _ => (string.Create(CultureInfo.InvariantCulture, $"0x{(int)format:X4}"), 1, 0),
    };
}
