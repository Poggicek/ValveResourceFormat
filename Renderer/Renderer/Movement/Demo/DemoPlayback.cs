using System.IO;

namespace ValveResourceFormat.Renderer.Movement.Demo;

/// <summary>
/// A demo's user commands played through the player movement as the server ran them, to see where
/// the movement and the game part ways.
/// </summary>
/// <remarks>
/// The player starts from the state the server networked before the first command, and from there is
/// moved by the commands alone. Where the server had the player after each tick is kept alongside, to
/// compare with.
/// </remarks>
public sealed class DemoPlayback
{
    // How far apart two positions may be and still be drawn as the same path point
    private const float PathPointSpacing = 1f;

    private readonly DemoMovementRecording Recording;
    private readonly List<Vector3> replayedPath = [];
    private readonly List<Vector3> serverPath = [];
    private DemoReplay? Replay;
    private int StartIndex;

    private DemoPlayback(DemoMovementRecording recording)
    {
        Recording = recording;
    }

    /// <summary>Reads the movement of the player who sent the most commands in a demo.</summary>
    /// <param name="stream">The <c>.dem</c> file.</param>
    /// <exception cref="InvalidDataException">The file is not a Source 2 demo, or has no user commands.</exception>
    public static DemoPlayback Load(Stream stream) => new(DemoMovementRecording.Load(stream));

    /// <summary>Gets the map the demo was recorded on.</summary>
    public string? MapName => Recording.MapName;

    /// <summary>Gets the name of the player being replayed.</summary>
    public string PlayerName => Recording.Player.Name;

    /// <summary>Gets how many commands the player sent.</summary>
    public int CommandCount => Recording.Commands.Count;

    /// <summary>Gets or sets whether the playback is held where it is.</summary>
    public bool Paused { get; set; }

    /// <summary>Gets whether every command has been played.</summary>
    public bool Finished => Replay?.Finished ?? false;

    /// <summary>Gets how many commands have been played.</summary>
    public int CommandsPlayed => Replay == null ? 0 : Replay.Position - StartIndex;

    /// <summary>Gets how many commands can be played, from the first the demo can start from.</summary>
    public int CommandsPlayable => CommandCount - StartIndex;

    /// <summary>Gets the server tick of the last command played.</summary>
    public int ServerTick { get; private set; }

    /// <summary>Gets where the server had the player after the last command played, if the demo says.</summary>
    public Vector3? ServerOrigin { get; private set; }

    /// <summary>Gets how far the replayed player is from where the server had them, after the last command played.</summary>
    public float OriginError { get; private set; }

    /// <summary>Gets how far the replayed velocity is from the server's, after the last command played.</summary>
    public float VelocityError { get; private set; }

    /// <summary>Gets the server tick of the first command that did not end exactly as on the server, if any.</summary>
    public int? FirstMismatchTick { get; private set; }

    /// <summary>Gets the feet positions the replay has gone through, a point per unit of distance.</summary>
    public IReadOnlyList<Vector3> ReplayedPath => replayedPath;

    /// <summary>Gets the feet positions the server had the player at over the same commands.</summary>
    public IReadOnlyList<Vector3> ServerPath => serverPath;

    /// <summary>Gets a number that changes whenever a command is played, to tell when the paths changed.</summary>
    public int Version { get; private set; }

    /// <summary>Puts the player where the demo starts.</summary>
    /// <returns>The view angles to start with, or null when the demo has no state to start from.</returns>
    internal Vector3? Begin(MovementServices services, Func<NetworkedMovementState, float?> weaponMaxSpeed)
    {
        Replay = new DemoReplay(Recording, services, weaponMaxSpeed);
        StartIndex = Replay.FirstStartIndex;

        if (StartIndex < 0 || !Replay.Seek(StartIndex))
        {
            Replay = null;
            return null;
        }

        replayedPath.Clear();
        serverPath.Clear();
        replayedPath.Add(services.AbsOrigin);
        serverPath.Add(services.AbsOrigin);
        FirstMismatchTick = null;
        Version++;

        return Recording.Commands[StartIndex].Command.ViewAngles;
    }

    /// <summary>Plays the next command.</summary>
    /// <returns>The command played, or null when there are none left.</returns>
    internal UserCmd? Step()
    {
        if (Replay == null || Replay.Finished)
        {
            return null;
        }

        var step = Replay.Step();

        ServerTick = step.ServerTick;
        Version++;
        AddPathPoint(replayedPath, step.Origin);

        if (step.Expected is { } expected)
        {
            ServerOrigin = expected.Origin;
            OriginError = step.OriginError;
            VelocityError = step.VelocityError;
            AddPathPoint(serverPath, expected.Origin);

            if (!step.Matches)
            {
                FirstMismatchTick ??= step.ServerTick;
            }
        }

        return Recording.Commands[step.Index].Command;
    }

    private static void AddPathPoint(List<Vector3> path, Vector3 point)
    {
        if (path.Count == 0 || Vector3.DistanceSquared(path[^1], point) >= PathPointSpacing * PathPointSpacing)
        {
            path.Add(point);
        }
    }
}
