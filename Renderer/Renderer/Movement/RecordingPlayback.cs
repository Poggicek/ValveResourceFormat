using System.Globalization;
using System.IO;
using System.Text.Json;
using ValveResourceFormat.Renderer.Movement.Demo;

namespace ValveResourceFormat.Renderer.Movement;

/// <summary>
/// A movement recording played through the player movement, compared with where the player was after
/// each tick when recorded. Recordings come from this movement's own export, or from a server, which
/// records the game's own movement.
/// </summary>
public sealed class RecordingPlayback : MovementPlayback
{
    private readonly MovementRecording Recording;
    private MovementReplay? Replay;

    private RecordingPlayback(MovementRecording recording)
    {
        Recording = recording;
    }

    /// <summary>Reads a movement recording.</summary>
    /// <param name="stream">The <c>.json</c> recording.</param>
    /// <exception cref="InvalidDataException">The file is not a movement recording, or has no ticks.</exception>
    public static RecordingPlayback Load(Stream stream)
    {
        try
        {
            return new(MovementRecording.Load(stream));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or IndexOutOfRangeException)
        {
            throw new InvalidDataException($"Not a movement recording: {e.Message}", e);
        }
    }

    /// <inheritdoc/>
    public override string? MapName => Recording.Map;

    /// <inheritdoc/>
    public override string PlayerName => Recording.Player is { } player
        ? string.Create(CultureInfo.InvariantCulture, $"player {player}")
        : "the recorded player";

    /// <inheritdoc/>
    public override int CommandCount => Recording.Ticks.Count;

    private protected override int? ReplayPosition => Replay?.Position;

    private protected override (int Start, Vector3 ViewAngles)? BeginReplay(MovementServices services, WeaponSpeedTable weaponSpeeds)
    {
        Replay = new MovementReplay(Recording, services, weaponSpeeds.Get);
        Replay.Seek(0);

        return (0, Recording.Ticks[0].Command.ViewAngles);
    }

    private protected override PlaybackStep StepReplay()
    {
        var tick = Recording.Ticks[Replay!.Position];
        Replay.Step();

        return new PlaybackStep(tick.Tick, tick.Command, Replay.Services.AbsOrigin, Replay.Services.AbsVelocity, tick.OriginAfter, tick.VelocityAfter);
    }
}
