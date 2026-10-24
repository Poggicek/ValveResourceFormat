using ValveResourceFormat.Renderer.Movement.Demo;

namespace ValveResourceFormat.Renderer.Movement;

/// <summary>
/// Recorded user commands played through the player movement as the game ran them, to see where the
/// movement and the game part ways.
/// </summary>
/// <remarks>
/// The player starts from the state recorded before the first command, and from there is moved by the
/// commands alone. Where the game had the player after each command is kept alongside, to compare with.
/// </remarks>
public abstract class MovementPlayback
{
    // How far apart two positions may be and still be drawn as the same path point
    private const float PathPointSpacing = 1f;

    private readonly List<Vector3> replayedPath = [];
    private readonly List<Vector3> serverPath = [];
    private int StartIndex;

    private protected MovementPlayback()
    {
    }

    /// <summary>Gets the map the commands were recorded on.</summary>
    public abstract string? MapName { get; }

    /// <summary>Gets the name of the player being replayed.</summary>
    public abstract string PlayerName { get; }

    /// <summary>Gets how many commands the player sent.</summary>
    public abstract int CommandCount { get; }

    /// <summary>Gets or sets whether the playback is held where it is.</summary>
    public bool Paused { get; set; }

    /// <summary>Gets whether every command has been played.</summary>
    public bool Finished => ReplayPosition >= CommandCount;

    /// <summary>Gets how many commands have been played.</summary>
    public int CommandsPlayed => ReplayPosition is { } position ? position - StartIndex : 0;

    /// <summary>Gets how many commands can be played, from the first there is a state to start from.</summary>
    public int CommandsPlayable => CommandCount - StartIndex;

    /// <summary>Gets the server tick of the last command played.</summary>
    public int ServerTick { get; private set; }

    /// <summary>Gets where the game had the player after the last command played, if recorded.</summary>
    public Vector3? ServerOrigin { get; private set; }

    /// <summary>Gets how far the replayed player is from where the game had them, after the last command played.</summary>
    public float OriginError { get; private set; }

    /// <summary>Gets how far the replayed velocity is from the game's, after the last command played.</summary>
    public float VelocityError { get; private set; }

    /// <summary>Gets the server tick of the first command that did not end exactly as in the game, if any.</summary>
    public int? FirstMismatchTick { get; private set; }

    /// <summary>Gets the feet positions the replay has gone through, a point per unit of distance.</summary>
    public IReadOnlyList<Vector3> ReplayedPath => replayedPath;

    /// <summary>Gets the feet positions the game had the player at over the same commands.</summary>
    public IReadOnlyList<Vector3> ServerPath => serverPath;

    /// <summary>Gets a number that changes whenever a command is played, to tell when the paths changed.</summary>
    public int Version { get; private set; }

    /// <summary>Gets the index of the next command to play, or null before the playback has begun.</summary>
    private protected abstract int? ReplayPosition { get; }

    /// <summary>Puts the player where the recording starts.</summary>
    /// <returns>The view angles to start with, or null when there is no state to start from.</returns>
    internal Vector3? Begin(MovementServices services, WeaponSpeedTable weaponSpeeds)
    {
        if (BeginReplay(services, weaponSpeeds) is not var (start, viewAngles))
        {
            return null;
        }

        StartIndex = start;
        replayedPath.Clear();
        serverPath.Clear();
        replayedPath.Add(services.AbsOrigin);
        serverPath.Add(services.AbsOrigin);
        FirstMismatchTick = null;
        Version++;

        return viewAngles;
    }

    /// <summary>Plays the next command.</summary>
    /// <returns>The command played, or null when there are none left.</returns>
    internal UserCmd? Step()
    {
        if (ReplayPosition == null || Finished)
        {
            return null;
        }

        var step = StepReplay();

        ServerTick = step.Tick;
        Version++;
        AddPathPoint(replayedPath, step.Origin);

        if (step.ExpectedOrigin is { } expectedOrigin)
        {
            ServerOrigin = expectedOrigin;
            OriginError = Vector3.Distance(step.Origin, expectedOrigin);
            VelocityError = Vector3.Distance(step.Velocity, step.ExpectedVelocity);
            AddPathPoint(serverPath, expectedOrigin);

            if (step.Origin != expectedOrigin || step.Velocity != step.ExpectedVelocity)
            {
                FirstMismatchTick ??= step.Tick;
            }
        }

        return step.Command;
    }

    /// <summary>Puts the player where the recording starts.</summary>
    /// <returns>The index of the first command to play and the view angles to start with, or null when there is no state to start from.</returns>
    private protected abstract (int Start, Vector3 ViewAngles)? BeginReplay(MovementServices services, WeaponSpeedTable weaponSpeeds);

    private protected abstract PlaybackStep StepReplay();

    /// <summary>A command played, where it left the player, and where the game had them after it if recorded.</summary>
    private protected readonly record struct PlaybackStep(int Tick, UserCmd Command, Vector3 Origin, Vector3 Velocity, Vector3? ExpectedOrigin, Vector3 ExpectedVelocity);

    private static void AddPathPoint(List<Vector3> path, Vector3 point)
    {
        if (path.Count == 0 || Vector3.DistanceSquared(path[^1], point) >= PathPointSpacing * PathPointSpacing)
        {
            path.Add(point);
        }
    }
}
