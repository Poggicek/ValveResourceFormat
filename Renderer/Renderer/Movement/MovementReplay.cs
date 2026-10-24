using System.IO;
using System.Linq;
using System.Text.Json;
using ValveResourceFormat.Renderer.Entities;

namespace ValveResourceFormat.Renderer.Movement;

/// <summary>
/// One tick of a recording: the state it started from, its command, and where it left the player.
/// Recordings made on a server also have the full state the tick ended in, and mark commands that ran
/// late, with no time passing.
/// </summary>
internal sealed record RecordedTick(int Tick, IReadOnlyList<string> Markers, JsonElement State, UserCmd Command, Vector3 OriginAfter, Vector3 VelocityAfter,
    JsonElement? StateAfter, bool Late);

/// <summary>A movement recording read back from what <see cref="MovementRecorder.Export"/> wrote.</summary>
internal sealed class MovementRecording
{
    public string? Map { get; private init; }

    /// <summary>Gets the entity index of the player recorded, for recordings made on a server.</summary>
    public int? Player { get; private init; }
    public required MovementVars Vars { get; init; }
    public required IReadOnlyList<RecordedTick> Ticks { get; init; }

    public static MovementRecording Load(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);

        // The elements are kept past the document, so they are cloned out of it
        var root = document.RootElement.Clone();
        var ticks = new List<RecordedTick>();
        var player = FindMovingPlayer(root.GetProperty("ticks"));

        foreach (var tick in root.GetProperty("ticks").EnumerateArray())
        {
            if (player != null && tick.GetProperty("player").GetInt32() != player)
            {
                continue;
            }

            var markers = tick.TryGetProperty("markers", out var markerArray)
                ? markerArray.EnumerateArray().Select(static marker => marker.GetString()!).ToList()
                : [];

            var after = tick.GetProperty("after");
            var command = tick.GetProperty("command");

            ticks.Add(new RecordedTick(
                tick.GetProperty("tick").GetInt32(),
                markers,
                tick.GetProperty("state"),
                ReadCommand(command),
                MovementJson.ReadVector(after.GetProperty("origin")),
                MovementJson.ReadVector(after.GetProperty("velocity")),
                tick.TryGetProperty("stateAfter", out var stateAfter) ? stateAfter : null,
                command.TryGetProperty("late", out var late) && late.GetBoolean()));
        }

        if (ticks.Count == 0)
        {
            throw new InvalidDataException("The recording has no ticks.");
        }

        return new MovementRecording
        {
            Map = root.GetProperty("map").GetString(),
            Player = player,
            Vars = MovementVarsJson.Read(root.GetProperty("vars")),
            Ticks = ticks,
        };
    }

    /// <summary>
    /// Finds the player a server recording is of: every player on the server is recorded, and the one
    /// who moved the most is taken to be the one playing.
    /// </summary>
    /// <returns>The player's entity index, or null when the recording is of a single player.</returns>
    private static int? FindMovingPlayer(JsonElement ticks)
    {
        var movesByPlayer = new Dictionary<int, int>();

        foreach (var tick in ticks.EnumerateArray())
        {
            if (!tick.TryGetProperty("player", out var player))
            {
                return null;
            }

            var moved = MovementJson.ReadVector(tick.GetProperty("before").GetProperty("origin")) != MovementJson.ReadVector(tick.GetProperty("after").GetProperty("origin"));
            movesByPlayer[player.GetInt32()] = movesByPlayer.GetValueOrDefault(player.GetInt32()) + (moved ? 1 : 0);
        }

        return movesByPlayer.Count == 0 ? null : movesByPlayer.MaxBy(static entry => entry.Value).Key;
    }

    private static UserCmd ReadCommand(JsonElement command)
    {
        var cmd = new UserCmd
        {
            CommandNumber = command.GetProperty("number").GetInt32(),
            ViewAngles = MovementJson.ReadVector(command.GetProperty("viewAngles")),
            ForwardMove = MovementJson.ReadFloat(command.GetProperty("forwardMove")),
            LeftMove = MovementJson.ReadFloat(command.GetProperty("leftMove")),
            UpMove = MovementJson.ReadFloat(command.GetProperty("upMove")),
            Buttons = new ButtonState
            {
                Held = command.GetProperty("held").GetUInt64(),
                Changed = command.GetProperty("changed").GetUInt64(),
                ChangedTwice = command.GetProperty("changedTwice").GetUInt64(),
            },
        };

        foreach (var move in command.GetProperty("subtickMoves").EnumerateArray())
        {
            cmd.SubtickMoves.Add(new SubtickMoveStep
            {
                When = MovementJson.ReadFloat(move.GetProperty("when")),
                Button = Enum.Parse<InputButtons>(move.GetProperty("button").GetString()!),
                Pressed = move.GetProperty("pressed").GetBoolean(),
                AnalogForwardDelta = MovementJson.ReadFloat(move.GetProperty("analogForwardDelta")),
                AnalogLeftDelta = MovementJson.ReadFloat(move.GetProperty("analogLeftDelta")),
                PitchDelta = MovementJson.ReadFloat(move.GetProperty("pitchDelta")),
                YawDelta = MovementJson.ReadFloat(move.GetProperty("yawDelta")),
            });
        }

        return cmd;
    }
}

/// <summary>
/// Runs a recording's commands again in the same world, from the state of any of its ticks, and checks
/// that each ends where it did when recorded.
/// </summary>
/// <remarks>
/// The replayed ticks are recorded again by <see cref="Recorder"/>, so a run can be exported with
/// changes made to the movement and compared with the original.
/// </remarks>
internal sealed class MovementReplay
{
    private readonly MovementRecording Recording;
    private readonly Func<uint, int, float?>? WeaponMaxSpeed;

    public MovementReplay(MovementRecording recording, PhysicsWorld? world, EntitySystem? entities, SurfacePropertyTable? surfaces)
        : this(recording, new MovementServices(recording.Vars, new MovementCollision { World = world, Entities = entities })
        {
            Surfaces = surfaces ?? SurfacePropertyTable.Empty,
            Recorder = new MovementRecorder(recording.Vars),
        })
    {
    }

    /// <param name="recording">The recording to run.</param>
    /// <param name="services">The movement to run the commands through, which takes the recording's settings.</param>
    /// <param name="weaponMaxSpeed">
    /// The max speed of a weapon by subclass and firing mode, for recordings made on a server, which name
    /// the weapon held instead of its speed.
    /// </param>
    public MovementReplay(MovementRecording recording, MovementServices services, Func<uint, int, float?>? weaponMaxSpeed = null)
    {
        Recording = recording;
        Services = services;
        WeaponMaxSpeed = weaponMaxSpeed;
        MovementVarsJson.Copy(recording.Vars, services.Vars);
    }

    public MovementServices Services { get; }
    public MovementCollision Collision => Services.Collision;
    public MovementRecorder? Recorder => Services.Recorder;

    /// <summary>Gets the index of the next tick to run.</summary>
    public int Position { get; private set; }

    /// <summary>Gets whether every tick has been run.</summary>
    public bool Finished => Position >= Recording.Ticks.Count;

    /// <summary>Puts the player in the state the tick at <paramref name="index"/> started from.</summary>
    public void Seek(int index)
    {
        Position = index;
        Restore(Recording.Ticks[index].State);
    }

    /// <summary>Runs the next tick's command.</summary>
    /// <returns>Whether it ended exactly where it did when recorded.</returns>
    public bool Step()
    {
        var tick = Recording.Ticks[Position++];

        // A teleport or reset happened before this tick, outside the movement
        if (tick.Markers.Count > 0)
        {
            Restore(tick.State);
        }

        // The movement has no commands that run late, with no time passing, so the player is put where it left them
        if (tick.Late && tick.StateAfter is { } after)
        {
            Restore(after);
        }
        else
        {
            Services.RunCommand(tick.Command);
        }

        return Services.AbsOrigin == tick.OriginAfter && Services.AbsVelocity == tick.VelocityAfter;
    }

    /// <summary>Runs every tick from <paramref name="start"/> on.</summary>
    /// <returns>The index of the first tick that ended somewhere else than recorded, or -1 when all matched.</returns>
    public int Run(int start = 0)
    {
        Seek(start);

        while (Position < Recording.Ticks.Count)
        {
            if (!Step())
            {
                return Position - 1;
            }
        }

        return -1;
    }

    private void Restore(JsonElement state)
    {
        Services.VisitState(new JsonStateReader(state));

        if (WeaponMaxSpeed != null && !state.TryGetProperty(nameof(MovementServices.WeaponMaxSpeed), out _))
        {
            Services.WeaponMaxSpeed = state.TryGetProperty("WeaponSubclass", out var subclass)
                ? WeaponMaxSpeed(subclass.GetUInt32(), state.TryGetProperty("WeaponMode", out var mode) ? mode.GetInt32() : 0)
                : null;
        }

        // Only the world can be found again by name; standing on anything else replays as standing on nothing
        var entities = Services.Collision.Entities;
        var onWorld = state.TryGetProperty("GroundEntity", out var ground)
            && entities?.World is { } world && ground.GetString() == world.Classname;

        Services.GroundEntity = onWorld ? entities!.World : null;
    }
}
