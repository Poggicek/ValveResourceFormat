using System.IO;

namespace ValveResourceFormat.Renderer.Movement.Demo;

/// <summary>
/// A demo's user commands played through the player movement as the server ran them, compared with
/// the state the server networked after each tick.
/// </summary>
public sealed class DemoPlayback : MovementPlayback
{
    private readonly DemoMovementRecording Recording;
    private DemoReplay? Replay;

    private DemoPlayback(DemoMovementRecording recording)
    {
        Recording = recording;
    }

    /// <summary>Reads the movement of the player who sent the most commands in a demo.</summary>
    /// <param name="stream">The <c>.dem</c> file.</param>
    /// <exception cref="InvalidDataException">The file is not a Source 2 demo, or has no user commands.</exception>
    public static DemoPlayback Load(Stream stream) => new(DemoMovementRecording.Load(stream));

    /// <inheritdoc/>
    public override string? MapName => Recording.MapName;

    /// <inheritdoc/>
    public override string PlayerName => Recording.Player.Name;

    /// <inheritdoc/>
    public override int CommandCount => Recording.Commands.Count;

    private protected override int? ReplayPosition => Replay?.Position;

    private protected override (int Start, Vector3 ViewAngles)? BeginReplay(MovementServices services, WeaponSpeedTable weaponSpeeds)
    {
        Replay = new DemoReplay(Recording, services, weaponSpeeds.Get);
        var start = Replay.FirstStartIndex;

        if (start < 0 || !Replay.Seek(start))
        {
            Replay = null;
            return null;
        }

        return (start, Recording.Commands[start].Command.ViewAngles);
    }

    private protected override PlaybackStep StepReplay()
    {
        var step = Replay!.Step();
        var command = Recording.Commands[step.Index].Command;

        return new PlaybackStep(step.ServerTick, command, step.Origin, step.Velocity, step.Expected?.Origin, step.Expected?.Velocity ?? default);
    }
}
