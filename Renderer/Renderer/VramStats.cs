using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace ValveResourceFormat.Renderer;

/// <summary>Video memory the operating system has charged to this process on its graphics adapter.</summary>
/// <param name="Dedicated">Bytes resident in the adapter's own memory.</param>
/// <param name="DedicatedBudget">Bytes of adapter memory the process can use before the OS starts evicting.</param>
/// <param name="Shared">Bytes resident in system memory the adapter reads.</param>
public readonly record struct ProcessVideoMemory(long Dedicated, long DedicatedBudget, long Shared);

/// <summary>
/// Measures the video memory held by the textures and buffers of the current graphics device.
/// </summary>
public class VramStats
{
    /// <summary>Total size and object count of one row of a <see cref="Report"/>, with its largest and smallest member described.</summary>
    internal sealed record Group(string Name, long Bytes, int Count, string Detail, string SmallestDetail);

    /// <summary>One measurement of the device's objects, largest first in every list.</summary>
    internal sealed record Report(
        long TextureBytes,
        int TextureCount,
        int ViewCount,
        long BufferBytes,
        int BufferCount,
        ProcessVideoMemory? Process,
        List<Group> Categories,
        List<Group> Formats,
        List<Group> Textures,
        List<Group> Buffers)
    {
        public long TotalBytes => TextureBytes + BufferBytes;
    }

    private const int MaxTexturesShown = 18;
    private const int MaxBuffersShown = 10;
    private const int MaxLabelLength = 90;

    /// <summary>
    /// Reads the video memory the operating system has charged to this process, which includes what
    /// the tracked objects miss: other viewers, the window's own buffers, shaders, and the driver.
    /// Set by hosts that can read it; null or returning null where unavailable.
    /// </summary>
    public static Func<ProcessVideoMemory?>? ProcessMemoryQuery { get; set; }

    private readonly List<GpuObjectRegistry.Entry> liveTextures = [];
    private readonly List<GpuObjectRegistry.Entry> liveBuffers = [];

    /// <summary>
    /// Measures every live texture and buffer of the device whose context is current on the calling thread.
    /// </summary>
    /// <param name="maxTextures">Largest texture names to list.</param>
    /// <param name="maxBuffers">Largest buffer names to list.</param>
    internal Report Measure(int maxTextures = MaxTexturesShown, int maxBuffers = MaxBuffersShown)
    {
        liveTextures.Clear();
        liveBuffers.Clear();
        GraphicsDevice.Current.Objects.Measure(liveTextures, liveBuffers);

        var textureBytes = 0L;
        var gameTextures = new Accumulator();
        var rendererTextures = new Accumulator();
        var meshBuffers = new Accumulator();
        var otherBuffers = new Accumulator();
        var viewCount = 0;

        var formats = new Dictionary<string, Accumulator>();
        var textures = new Dictionary<string, Accumulator>();
        var buffers = new Dictionary<string, Accumulator>();

        foreach (var texture in liveTextures)
        {
            if (texture.IsView)
            {
                viewCount++;
                continue;
            }

            textureBytes += texture.Bytes;

            var category = IsGameTexture(texture.Label) ? gameTextures : rendererTextures;
            category.Add(texture);
            GetAccumulator(formats, texture.FormatName).Add(texture);
            GetAccumulator(textures, texture.Label).Add(texture);
        }

        var bufferBytes = 0L;

        foreach (var buffer in liveBuffers)
        {
            bufferBytes += buffer.Bytes;

            var category = IsMeshBuffer(buffer.Label) ? meshBuffers : otherBuffers;
            category.Add(buffer);
            GetAccumulator(buffers, buffer.Label).Add(buffer);
        }

        List<Group> categories =
        [
            gameTextures.ToGroup("Game textures", string.Empty),
            rendererTextures.ToGroup("Renderer textures", "render targets, atlases, LUTs"),
            meshBuffers.ToGroup("Mesh buffers", "vertex and index"),
            otherBuffers.ToGroup("Other buffers", "uniform, storage, streaming"),
        ];

        categories.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));

        return new Report(
            textureBytes,
            liveTextures.Count - viewCount,
            viewCount,
            bufferBytes,
            liveBuffers.Count,
            ProcessMemoryQuery?.Invoke(),
            categories,
            Largest(formats, int.MaxValue, static _ => string.Empty),
            Largest(textures, maxTextures, DescribeTexture),
            Largest(buffers, maxBuffers, static _ => string.Empty));

        static Accumulator GetAccumulator(Dictionary<string, Accumulator> groups, string name)
        {
            if (!groups.TryGetValue(name, out var accumulator))
            {
                accumulator = new Accumulator();
                groups[name] = accumulator;
            }

            return accumulator;
        }
    }

    private static List<Group> Largest(Dictionary<string, Accumulator> groups, int count, Func<GpuObjectRegistry.Entry, string> describe)
    {
        var sorted = groups
            .Select(group => group.Value.ToGroup(group.Key.Length > 0 ? group.Key : "(unnamed)", describe))
            .ToList();

        sorted.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));

        if (sorted.Count > count)
        {
            sorted.RemoveRange(count, sorted.Count - count);
        }

        return sorted;
    }

    private static string DescribeTexture(GpuObjectRegistry.Entry texture)
    {
        var size = texture.Depth > 1
            ? string.Create(CultureInfo.InvariantCulture, $"{texture.Width}x{texture.Height}x{texture.Depth}")
            : string.Create(CultureInfo.InvariantCulture, $"{texture.Width}x{texture.Height}");

        var samples = texture.Samples > 1
            ? string.Create(CultureInfo.InvariantCulture, $" {texture.Samples}x MSAA")
            : string.Empty;

        return string.Create(CultureInfo.InvariantCulture, $"{size} {texture.FormatName} {texture.Levels} mip{(texture.Levels == 1 ? "" : "s")}{samples}");
    }

    private static bool IsGameTexture(string label) => label.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase);

    private static bool IsMeshBuffer(string label) => label.Contains(" VB ", StringComparison.Ordinal) || label.Contains(" IB ", StringComparison.Ordinal);

    private sealed class Accumulator
    {
        public long Bytes;
        public int Count;
        private GpuObjectRegistry.Entry? largest;
        private GpuObjectRegistry.Entry? smallest;

        public void Add(GpuObjectRegistry.Entry entry)
        {
            Bytes += entry.Bytes;
            Count++;

            if (largest == null || entry.Bytes > largest.Bytes)
            {
                largest = entry;
            }

            if (smallest == null || entry.Bytes < smallest.Bytes)
            {
                smallest = entry;
            }
        }

        public Group ToGroup(string name, string detail) => new(name, Bytes, Count, detail, string.Empty);

        public Group ToGroup(string name, Func<GpuObjectRegistry.Entry, string> describe) => new(
            name,
            Bytes,
            Count,
            largest != null ? describe(largest) : string.Empty,
            Count > 1 && smallest != null ? describe(smallest) : string.Empty);
    }

    private readonly TextRenderer.TextArena displayText = new(16 * 1024);
    private readonly List<(TextRenderer.TextMemory Text, Color32 Color)> displayLines = [];
    private long lastDisplayRebuild;

    /// <summary>
    /// Renders the video memory breakdown to screen, measured again once a second.
    /// </summary>
    /// <param name="textRenderer">Text renderer to use for display.</param>
    /// <param name="camera">Camera for positioning text.</param>
    /// <param name="x">X position (0-1 as fraction of screen width).</param>
    /// <param name="y">Y position (0-1 as fraction of screen height).</param>
    /// <param name="scale">Text scale.</param>
    public void DisplayVram(TextRenderer textRenderer, Camera camera, float x = 0.02f, float y = 0.05f, float scale = 11f)
    {
        if (displayLines.Count == 0 || Stopwatch.GetElapsedTime(lastDisplayRebuild).TotalSeconds >= 1.0)
        {
            lastDisplayRebuild = Stopwatch.GetTimestamp();
            RebuildDisplayLines(Measure());
        }

        var lineHeight = scale * 1.5f / camera.WindowSize.Y;
        var offset = y;

        foreach (var (text, color) in displayLines)
        {
            if (text.Length > 0)
            {
                textRenderer.AddTextRelative(new TextRenderer.TextRenderRequest
                {
                    X = x,
                    Y = offset,
                    Scale = scale,
                    Color = color,
                    Text = text,
                }, camera);
            }

            offset += lineHeight;
        }
    }

    private void RebuildDisplayLines(Report report)
    {
        displayLines.Clear();
        displayText.Clear();

        var headerColor = new Color32(255, 200, 0);
        var valueColor = new Color32(150, 255, 150);
        var greyColor = new Color32(140, 140, 140);

        void AddLine(TextRenderer.TextMemory text, Color32 color) => displayLines.Add((text, color));

        void AddGroup(Group group, long total)
        {
            var share = total > 0 ? (double)group.Bytes / total : 0;
            var name = group.Name.AsSpan(0, Math.Min(group.Name.Length, MaxLabelLength));
            var color = share >= 0.1 ? new Color32(255, 255, 0) : valueColor;

            AddLine(displayText.Format($"{new ByteSize(group.Bytes),10} {share,6:0.0%} {group.Count,7:N0}  {name}  {group.Detail}"), color);
        }

        AddLine("Video Memory", headerColor);

        AddLine(displayText.Format($"This viewer: {new ByteSize(report.TotalBytes),10} tracked, {new ByteSize(report.TextureBytes),10} in {report.TextureCount:N0} textures, {new ByteSize(report.BufferBytes),10} in {report.BufferCount:N0} buffers"), valueColor);

        if (report.Process is { } process)
        {
            var usage = process.DedicatedBudget > 0 ? (double)process.Dedicated / process.DedicatedBudget : 0;
            var usageColor = usage switch
            {
                >= 0.9 => new Color32(255, 0, 0),
                >= 0.7 => new Color32(255, 150, 0),
                _ => valueColor,
            };

            AddLine(displayText.Format($"Process:     {new ByteSize(process.Dedicated),10} dedicated ({usage:0%} of {new ByteSize(process.DedicatedBudget)} budget), {new ByteSize(process.Shared),10} shared"), usageColor);
            AddLine(displayText.Format($"Untracked:   {new ByteSize(process.Dedicated + process.Shared - report.TotalBytes),10} other viewers, window buffers, shaders, driver"), greyColor);
        }

        AddLine(displayText.Format($"Texture views sharing storage: {report.ViewCount:N0}"), greyColor);

        AddLine(default, default);
        AddLine(displayText.Format($"{"Bytes",10} {"Share",6} {"Count",7}  Category"), Color32.White);

        foreach (var group in report.Categories)
        {
            AddGroup(group, report.TotalBytes);
        }

        AddLine(default, default);
        AddLine("Textures by format", headerColor);

        foreach (var group in report.Formats)
        {
            AddGroup(group, report.TextureBytes);
        }

        AddLine(default, default);
        AddLine("Largest textures, by name", headerColor);

        foreach (var group in report.Textures)
        {
            AddGroup(group, report.TextureBytes);
        }

        AddLine(default, default);
        AddLine("Largest buffers, by name", headerColor);

        foreach (var group in report.Buffers)
        {
            AddGroup(group, report.BufferBytes);
        }
    }
}
