using System.Globalization;

namespace ValveResourceFormat.Renderer;

/// <summary>Human-readable byte count that formats into a span, so interpolating it does not allocate.</summary>
internal readonly struct ByteSize(long bytes) : ISpanFormattable
{
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) => bytes switch
    {
        < 0 => destination.TryWrite($"0 B", out charsWritten), // a GC can shrink GetTotalMemory between snapshots
        < 1024 => destination.TryWrite($"{bytes} B", out charsWritten),
        < 1024 * 1024 => destination.TryWrite(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} KB", out charsWritten),
        < 1024 * 1024 * 1024 => destination.TryWrite(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.0} MB", out charsWritten),
        _ => destination.TryWrite(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB", out charsWritten),
    };

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    public override string ToString() => bytes switch
    {
        < 0 => "0 B",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} KB"),
        < 1024 * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.0} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB"),
    };
}
