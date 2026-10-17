using System.IO;
using System.Linq;
using System.Text.Json;
using ValveResourceFormat.Renderer.Entities;

namespace ValveResourceFormat.Renderer.Movement;

/// <summary>One tick of a recording: the state it started from, its command, and where it left the player.</summary>
internal sealed record RecordedTick(int Tick, IReadOnlyList<string> Markers, JsonElement State, UserCmd Command, Vector3 OriginAfter, Vector3 VelocityAfter);

/// <summary>A movement recording read back from what <see cref="MovementRecorder.Export"/> wrote.</summary>
internal sealed class MovementRecording
{
    public string? Map { get; private init; }
    public required MovementVars Vars { get; init; }
    public required IReadOnlyList<RecordedTick> Ticks { get; init; }

    public static MovementRecording Load(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);

        // The elements are kept past the document, so they are cloned out of it
        var root = document.RootElement.Clone();
        var ticks = new List<RecordedTick>();

        foreach (var tick in root.GetProperty("ticks").EnumerateArray())
        {
            var markers = tick.TryGetProperty("markers", out var markerArray)
                ? markerArray.EnumerateArray().Select(static marker => marker.GetString()!).ToList()
                : [];

            var after = tick.GetProperty("after");

            ticks.Add(new RecordedTick(
                tick.GetProperty("tick").GetInt32(),
                markers,
                tick.GetProperty("state"),
                ReadCommand(tick.GetProperty("command")),
                MovementJson.ReadVector(after.GetProperty("origin")),
                MovementJson.ReadVector(after.GetProperty("velocity"))));
        }

        return new MovementRecording
        {
            Map = root.GetProperty("map").GetString(),
            Vars = MovementVarsJson.Read(root.GetProperty("vars")),
            Ticks = ticks,
        };
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
    private readonly EntitySystem? Entities;

    public MovementReplay(MovementRecording recording, PhysicsWorld? world, EntitySystem? entities, SurfacePropertyTable? surfaces)
    {
        Recording = recording;
        Entities = entities;
        Collision = new MovementCollision { World = world, Entities = entities };
        Recorder = new MovementRecorder(recording.Vars);
        Services = new MovementServices(recording.Vars, Collision)
        {
            Surfaces = surfaces ?? SurfacePropertyTable.Empty,
            Recorder = Recorder,
        };
    }

    public MovementCollision Collision { get; }
    public MovementServices Services { get; }
    public MovementRecorder Recorder { get; }

    /// <summary>Gets the index of the next tick to run.</summary>
    public int Position { get; private set; }

    /// <summary>Puts the player in the state the tick at <paramref name="index"/> started from.</summary>
    public void Seek(int index)
    {
        Position = index;
        Restore(Recording.Ticks[index]);
    }

    /// <summary>Runs the next tick's command.</summary>
    /// <returns>Whether it ended exactly where it did when recorded.</returns>
    public bool Step()
    {
        var tick = Recording.Ticks[Position++];

        // A teleport or reset happened before this tick, outside the movement
        if (tick.Markers.Count > 0)
        {
            Restore(tick);
        }

        Services.RunCommand(tick.Command);

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

    private void Restore(RecordedTick tick)
    {
        Services.VisitState(new JsonStateReader(tick.State));

        // Only the world can be found again by name; standing on anything else replays as standing on nothing
        var onWorld = tick.State.TryGetProperty("GroundEntity", out var ground)
            && Entities?.World is { } world && ground.GetString() == world.Classname;

        Services.GroundEntity = onWorld ? Entities!.World : null;
    }
}
