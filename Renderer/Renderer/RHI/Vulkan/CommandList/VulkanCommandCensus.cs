using System.Globalization;
using System.Linq;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// Records what <see cref="VulkanCommandList"/> actually put into a command buffer, so a frame that
/// records without faulting can still be asked whether it drew anything.
///
/// <para><b>Why this exists.</b> Every other instrument in this migration measures what the renderer
/// <em>asked for</em>: <c>GLCallTrap</c> counts the calls that never left OpenGL, and
/// <c>RhiDeviceCensus</c> counts what reached <see cref="IDevice"/>. Neither can see a
/// <c>vkCmdDraw</c>, because draws are recorded on the command list rather than on the device, and a
/// blank capture target is precisely the failure where every stage reports success. This is the third
/// side of that triangle: what reached the command buffer, in order, with the numbers that decide
/// whether it rasterises.</para>
///
/// <para><b>Off unless something turns it on.</b> The golden harness does, for the Vulkan run it
/// reports; nothing else does, so a viewer frame pays one predictably-false static read per command.</para>
/// </summary>
/// <remarks>
/// The log is an ordered transcript rather than a set of counters because the questions it answers are
/// about order and identity: which attachment a pass opened over, whether a draw fell inside it, and
/// what the viewport and scissor were at the time. Counters alone cannot distinguish "drew into the
/// wrong target" from "did not draw".
/// </remarks>
public static class VulkanCommandCensus
{
    /// <summary>How many events are kept before the transcript stops growing.</summary>
    /// <remarks>A frame of a golden scene records a few hundred; the cap is only here so that a viewer
    /// which turned this on and forgot cannot grow without bound.</remarks>
    public const int MaxEvents = 20000;

    private static readonly System.Threading.Lock Gate = new();
    private static readonly List<string> Events = [];
    private static readonly Dictionary<string, int> Counts = new(StringComparer.Ordinal);

    private static bool Truncated;

    /// <summary>Whether commands are being recorded into the transcript.</summary>
    public static bool IsEnabled { get; set; }

    /// <summary>Drops the transcript and the counters, normally between scenes.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Events.Clear();
            Counts.Clear();
            Truncated = false;
        }
    }

    /// <summary>Records one command with no detail worth printing.</summary>
    /// <param name="command">The contract method that was called.</param>
    public static void Note(string command) => Note(command, null);

    /// <summary>Records one command and the arguments that decide whether it does anything.</summary>
    /// <param name="command">The contract method that was called.</param>
    /// <param name="detail">The arguments, already formatted, or <see langword="null"/> for none.</param>
    public static void Note(string command, string? detail)
    {
        if (!IsEnabled)
        {
            return;
        }

        lock (Gate)
        {
            Counts[command] = Counts.TryGetValue(command, out var count) ? count + 1 : 1;

            if (Events.Count >= MaxEvents)
            {
                Truncated = true;
                return;
            }

            Events.Add(detail is null ? command : $"{command} {detail}");
        }
    }

    /// <summary>The transcript so far, oldest first.</summary>
    public static IReadOnlyList<string> Transcript
    {
        get
        {
            lock (Gate)
            {
                return [.. Events];
            }
        }
    }

    /// <summary>How many times one command was recorded since the last <see cref="Reset"/>.</summary>
    /// <param name="command">The contract method name.</param>
    public static int CountOf(string command)
    {
        lock (Gate)
        {
            return Counts.TryGetValue(command, out var count) ? count : 0;
        }
    }

    /// <summary>The counters, most-used first, one per line.</summary>
    public static string Summary()
    {
        lock (Gate)
        {
            if (Counts.Count == 0)
            {
                return "  nothing was recorded into a Vulkan command buffer.";
            }

            var lines = Counts
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => string.Create(CultureInfo.InvariantCulture, $"  {pair.Key,-26} {pair.Value,8:N0}"))
                .ToList();

            if (Truncated)
            {
                lines.Add($"  (the transcript stopped at {MaxEvents:N0} events; the counters above are complete)");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}
