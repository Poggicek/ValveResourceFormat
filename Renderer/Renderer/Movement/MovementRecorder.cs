using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using ValveResourceFormat.Renderer.Entities;

namespace ValveResourceFormat.Renderer.Movement;

/// <summary>The player as a recorded moment found them.</summary>
internal readonly record struct MovementState(
    Vector3 Origin,
    Vector3 Velocity,
    Vector3 BaseVelocity,
    PlayerFlags Flags,
    MoveType MoveType,
    BaseEntity? GroundEntity,
    bool Ducked,
    float DuckAmount,
    float SurfaceFriction,
    int WaterLevel);

/// <summary>A part of a tick simulated with one input, between two sub-tick input changes.</summary>
internal readonly record struct SliceRecord(
    float StartFraction,
    float EndFraction,
    float FrameTime,
    float CurTime,
    float ForwardMove,
    float SideMove,
    Vector3 ViewAngles,
    ulong Buttons,
    MovementState State);

/// <summary>How a collision of the slide move left the velocity.</summary>
internal enum ClipOutcome
{
    /// <summary>The move went the whole way.</summary>
    Finished,

    /// <summary>The velocity was clipped along the planes hit since the last move.</summary>
    Clipped,

    /// <summary>The first plane hit while airborne reflected the velocity, which is how surfing works.</summary>
    Reflected,

    /// <summary>The velocity was turned along the crease of two planes.</summary>
    Crease,

    /// <summary>Too many planes were hit, which zeroes the velocity.</summary>
    StoppedOutOfPlanes,

    /// <summary>Landed on something standable with no horizontal speed left, which zeroes the velocity.</summary>
    StoppedLanded,

    /// <summary>No direction satisfies three or more planes, which zeroes the velocity.</summary>
    StoppedCorner,

    /// <summary>Clipping turned the velocity back against the original one, which zeroes it. The record keeps the turned velocity.</summary>
    StoppedReversed,
}

/// <summary>One collision of the slide move and what it did to the velocity.</summary>
internal readonly record struct ClipRecord
{
    public int Bump { get; init; }

    /// <summary>The time of the slice left to move before this bump.</summary>
    public float TimeLeft { get; init; }

    public float Fraction { get; init; }

    /// <summary>Whether a first move under 1/32 of a unit was taken as no move at all.</summary>
    public bool FractionZeroed { get; init; }

    /// <summary>Whether the origin moved to the trace end, which forgets the planes hit before.</summary>
    public bool Moved { get; init; }

    public Vector3 Origin { get; init; }
    public Vector3 Normal { get; init; }
    public Vector3 VelocityIn { get; init; }
    public Vector3 VelocityOut { get; init; }
    public int PlaneCount { get; init; }
    public ClipOutcome Outcome { get; init; }
}

/// <summary>
/// Keeps the last ticks of the player's movement in full detail, so a move that went wrong, such as a surf
/// ramp stopping the player dead, can be taken apart afterwards.
/// </summary>
/// <remarks>
/// Each tick keeps its command, the player before and after it, and in the order they happened: the
/// slices its sub-tick input split it into, every hull trace with the triangle or hull it hit, every
/// collision of the slide move with the velocity before and after, and ground changes and snaps.
/// Ticks are recorded on the thread running the movement, and <see cref="Export"/> may be called from any other.
/// </remarks>
public sealed class MovementRecorder
{
    /// <summary>The number of ticks kept by default, ten seconds.</summary>
    public const int DefaultCapacity = 10 * 64;

    // The most hull vertices kept for a hit, as some world hulls have thousands
    private const int MaxHullVertices = 128;

    private readonly MovementVars Vars;
    private readonly TickRecord[] Ring;
    private readonly Lock Sync = new();
    private readonly List<string> PendingMarkers = [];

    // The slot the next tick is written to, and how many finished ticks there are before it
    private int Head;
    private int Count;

    private TickRecord? Current;

    internal MovementRecorder(MovementVars vars, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        Vars = vars;
        Ring = new TickRecord[capacity];

        for (var i = 0; i < capacity; i++)
        {
            Ring[i] = new TickRecord();
        }
    }

    /// <summary>Gets or sets whether ticks are recorded.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets how many ticks are kept at most.</summary>
    public int Capacity => Ring.Length;

    /// <summary>Gets how many ticks are recorded.</summary>
    public int TickCount
    {
        get
        {
            lock (Sync)
            {
                return Count;
            }
        }
    }

    /// <summary>Forgets every recorded tick.</summary>
    public void Clear()
    {
        lock (Sync)
        {
            Count = 0;
        }
    }

    /// <summary>Notes a jump in the player's state, such as a teleport, on the next recorded tick.</summary>
    internal void Mark(string marker)
    {
        lock (Sync)
        {
            PendingMarkers.Add(marker);
        }
    }

    /// <summary>Starts recording a tick, before the command runs.</summary>
    internal void BeginTick(MovementServices services, UserCmd cmd, in MovementState state)
    {
        if (!Enabled)
        {
            Current = null;
            return;
        }

        TickRecord record;

        lock (Sync)
        {
            // When full, the oldest tick is the one overwritten, so it stops being part of the recording
            if (Count == Ring.Length)
            {
                Count--;
            }

            record = Ring[Head];
            record.Reset();
            record.Markers.AddRange(PendingMarkers);
            PendingMarkers.Clear();
        }

        record.Tick = services.TickBase + 1;
        record.SaveState(services);
        record.CommandNumber = cmd.CommandNumber;
        record.ViewAngles = cmd.ViewAngles;
        record.ForwardMove = cmd.ForwardMove;
        record.LeftMove = cmd.LeftMove;
        record.UpMove = cmd.UpMove;
        record.Buttons = cmd.Buttons;
        record.SubtickMoves.AddRange(cmd.SubtickMoves);
        record.Before = state;

        Current = record;
    }

    internal void EndTick(in MovementState state)
    {
        if (Current is not { } record)
        {
            return;
        }

        record.After = state;
        Current = null;

        lock (Sync)
        {
            Head = (Head + 1) % Ring.Length;
            Count++;
        }
    }

    internal void BeginSlice(in SliceRecord slice)
    {
        if (Current is { } record)
        {
            record.Events.Add(new EventRef(EventKind.SliceStart, record.Slices.Count));
            record.Slices.Add(slice);
        }
    }

    internal void EndSlice(in MovementState state)
    {
        if (Current is { } record)
        {
            record.Events.Add(new EventRef(EventKind.SliceEnd, record.States.Count));
            record.States.Add(state);
        }
    }

    internal void Trace(string caller, string collisionName, Vector3 start, Vector3 end, Vector3 mins, Vector3 maxs, in GameTrace trace,
        in TraceHitShape shape)
    {
        if (Current is not { } record)
        {
            return;
        }

        var pointStart = record.Points.Count;

        if (trace.Hit)
        {
            AddHitGeometry(record.Points, shape);
        }

        record.Events.Add(new EventRef(EventKind.Trace, record.Traces.Count));
        record.Traces.Add(new TraceRecord(caller, collisionName, start, end, mins, maxs, trace, shape.Mesh != null ? HitKind.Mesh : shape.Hull != null ? HitKind.Hull : HitKind.Plane,
            shape.TriangleIndex, pointStart, record.Points.Count - pointStart));
    }

    internal void Clip(in ClipRecord clip)
    {
        if (Current is { } record)
        {
            record.Events.Add(new EventRef(EventKind.Clip, record.Clips.Count));
            record.Clips.Add(clip);
        }
    }

    /// <summary>Records something the movement did that changes the outcome, such as landing or snapping to the ground.</summary>
    internal void Note(string text, Vector3 origin, Vector3 velocity, float value = float.NaN, [CallerMemberName] string caller = "")
    {
        if (Current is { } record)
        {
            record.Events.Add(new EventRef(EventKind.Note, record.Notes.Count));
            record.Notes.Add(new NoteRecord(caller, text, origin, velocity, value));
        }
    }

    // A mesh hit keeps its triangle and the far vertex of each neighbour, which is where seams bug; a hull keeps its vertices
    private static void AddHitGeometry(List<Vector3> points, in TraceHitShape shape)
    {
        if (shape.Mesh is { } mesh && (uint)shape.TriangleIndex < (uint)mesh.Triangles.Length)
        {
            var triangle = mesh.Triangles[shape.TriangleIndex];
            var vertices = mesh.VertexPositions;

            points.Add(Vector3.Transform(vertices[triangle.X], shape.Transform));
            points.Add(Vector3.Transform(vertices[triangle.Y], shape.Transform));
            points.Add(Vector3.Transform(vertices[triangle.Z], shape.Transform));

            if ((uint)shape.TriangleIndex < (uint)mesh.Wings.Length)
            {
                var wing = mesh.Wings[shape.TriangleIndex];

                points.Add(wing.X >= 0 ? Vector3.Transform(vertices[wing.X], shape.Transform) : new Vector3(float.NaN));
                points.Add(wing.Y >= 0 ? Vector3.Transform(vertices[wing.Y], shape.Transform) : new Vector3(float.NaN));
                points.Add(wing.Z >= 0 ? Vector3.Transform(vertices[wing.Z], shape.Transform) : new Vector3(float.NaN));
            }
        }
        else if (shape.Hull is { } hull)
        {
            var count = Math.Min(hull.VertexPositions.Length, MaxHullVertices);

            for (var i = 0; i < count; i++)
            {
                points.Add(Vector3.Transform(hull.VertexPositions[i], shape.Transform));
            }
        }
    }

    /// <summary>
    /// Writes the recorded ticks as JSON, oldest first, with the movement settings and a list of the
    /// ticks that lost a lot of speed, which is where to start looking.
    /// </summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="mapName">The map the ticks were recorded on, if known.</param>
    public void Export(Stream stream, string? mapName = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var writer = new Utf8JsonWriter(stream);

        // The movement thread waits while this writes, so no tick is overwritten under it
        lock (Sync)
        {
            var first = (Head - Count + Ring.Length) % Ring.Length;

            writer.WriteStartObject();
            writer.WriteString("format", "s2v-movement-recording");
            writer.WriteNumber("version", 1);
            writer.WriteString("map", mapName);
            writer.WriteNumber("tickInterval", MovementServices.TickInterval);
            WriteVars(writer);
            WriteHull(writer);

            writer.WriteStartArray("speedLosses");

            for (var i = 0; i < Count; i++)
            {
                WriteSpeedLoss(writer, Ring[(first + i) % Ring.Length]);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("ticks");

            var scratch = new MovementServices(Vars, new MovementCollision());

            for (var i = 0; i < Count; i++)
            {
                WriteTick(writer, Ring[(first + i) % Ring.Length], scratch);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }

    private void WriteVars(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("vars");

        foreach (var (name, get, _) in MovementVarsJson.Floats)
        {
            WriteFloat(writer, name, get(Vars));
        }

        foreach (var (name, get, _) in MovementVarsJson.Booleans)
        {
            writer.WriteBoolean(name, get(Vars));
        }

        writer.WriteEndObject();
    }

    // Everything the tick depends on besides its command, so the commands can be run again from any tick
    private static void WriteFullState(Utf8JsonWriter writer, TickRecord tick, MovementServices scratch)
    {
        tick.LoadState(scratch);

        writer.WriteStartObject("state");
        scratch.VisitState(new JsonStateWriter(writer));

        if (tick.Before.GroundEntity is { } ground)
        {
            writer.WriteString("GroundEntity", ground.Classname);
        }

        writer.WriteEndObject();
    }

    private static void WriteHull(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("hull");
        WriteVector(writer, "mins", MovementServices.HullMin);
        WriteVector(writer, "maxs", MovementServices.HullMax);
        WriteVector(writer, "duckMins", MovementServices.DuckHullMin);
        WriteVector(writer, "duckMaxs", MovementServices.DuckHullMax);
        writer.WriteEndObject();
    }

    // Gravity takes 12.5 units per second off a tick, so anything far beyond that was a collision
    private static void WriteSpeedLoss(Utf8JsonWriter writer, TickRecord tick)
    {
        var before = tick.Before.Velocity.Length();
        var after = tick.After.Velocity.Length();

        if (before - after < 100f || after > before * 0.75f)
        {
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("tick", tick.Tick);
        WriteFloat(writer, "speedBefore", before);
        WriteFloat(writer, "speedAfter", after);
        writer.WriteBoolean("onGroundAfter", (tick.After.Flags & PlayerFlags.OnGround) != 0);
        writer.WriteEndObject();
    }

    private static void WriteTick(Utf8JsonWriter writer, TickRecord tick, MovementServices scratch)
    {
        writer.WriteStartObject();
        writer.WriteNumber("tick", tick.Tick);

        if (tick.Markers.Count > 0)
        {
            writer.WriteStartArray("markers");

            foreach (var marker in tick.Markers)
            {
                writer.WriteStringValue(marker);
            }

            writer.WriteEndArray();
        }

        writer.WriteStartObject("command");
        writer.WriteNumber("number", tick.CommandNumber);
        WriteVector(writer, "viewAngles", tick.ViewAngles);
        WriteFloat(writer, "forwardMove", tick.ForwardMove);
        WriteFloat(writer, "leftMove", tick.LeftMove);
        WriteFloat(writer, "upMove", tick.UpMove);
        writer.WriteString("heldNames", ((InputButtons)tick.Buttons.Held).ToString());
        writer.WriteNumber("held", tick.Buttons.Held);
        writer.WriteNumber("changed", tick.Buttons.Changed);
        writer.WriteNumber("changedTwice", tick.Buttons.ChangedTwice);

        writer.WriteStartArray("subtickMoves");

        foreach (var move in tick.SubtickMoves)
        {
            writer.WriteStartObject();
            WriteFloat(writer, "when", move.When);
            writer.WriteString("button", move.Button.ToString());
            writer.WriteBoolean("pressed", move.Pressed);
            WriteFloat(writer, "analogForwardDelta", move.AnalogForwardDelta);
            WriteFloat(writer, "analogLeftDelta", move.AnalogLeftDelta);
            WriteFloat(writer, "pitchDelta", move.PitchDelta);
            WriteFloat(writer, "yawDelta", move.YawDelta);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();

        WriteState(writer, "before", tick.Before);
        WriteState(writer, "after", tick.After);
        WriteFullState(writer, tick, scratch);

        writer.WriteStartArray("events");

        foreach (var (kind, index) in tick.Events)
        {
            writer.WriteStartObject();

            switch (kind)
            {
                case EventKind.SliceStart:
                    WriteSlice(writer, tick.Slices[index]);
                    break;
                case EventKind.SliceEnd:
                    writer.WriteString("type", "sliceEnd");
                    WriteState(writer, "state", tick.States[index]);
                    break;
                case EventKind.Trace:
                    WriteTrace(writer, tick.Traces[index], tick.Points);
                    break;
                case EventKind.Clip:
                    WriteClip(writer, tick.Clips[index]);
                    break;
                case EventKind.Note:
                    WriteNote(writer, tick.Notes[index]);
                    break;
                default:
                    throw new UnreachableException();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteSlice(Utf8JsonWriter writer, in SliceRecord slice)
    {
        writer.WriteString("type", "slice");
        WriteFloat(writer, "start", slice.StartFraction);
        WriteFloat(writer, "end", slice.EndFraction);
        WriteFloat(writer, "frameTime", slice.FrameTime);
        WriteFloat(writer, "curTime", slice.CurTime);
        WriteFloat(writer, "forwardMove", slice.ForwardMove);
        WriteFloat(writer, "sideMove", slice.SideMove);
        WriteVector(writer, "viewAngles", slice.ViewAngles);
        writer.WriteString("buttons", ((InputButtons)slice.Buttons).ToString());
        WriteState(writer, "state", slice.State);
    }

    private static void WriteTrace(Utf8JsonWriter writer, in TraceRecord record, List<Vector3> points)
    {
        var trace = record.Trace;

        writer.WriteString("type", "trace");
        writer.WriteString("caller", record.Caller);

        if (record.CollisionName != Rubikon.Cs2PlayerCollisionFilter)
        {
            writer.WriteString("collision", record.CollisionName);
        }

        WriteVector(writer, "start", record.Start);
        WriteVector(writer, "end", record.End);
        WriteVector(writer, "mins", record.Mins);
        WriteVector(writer, "maxs", record.Maxs);
        WriteFloat(writer, "fraction", trace.Fraction);
        writer.WriteBoolean("startSolid", trace.StartSolid);

        if (!trace.Hit)
        {
            return;
        }

        WriteVector(writer, "endPos", trace.EndPos);
        WriteVector(writer, "normal", trace.Normal);
        writer.WriteNumber("surface", trace.SurfacePropertyHash);

        if (trace.HitLadder)
        {
            writer.WriteBoolean("ladder", true);
        }

        if (trace.Entity is { } entity)
        {
            writer.WriteString("entity", entity.TargetName is { Length: > 0 } name ? $"{entity.Classname} {name}" : entity.Classname);
        }

        writer.WriteStartObject("shape");

        switch (record.HitKind)
        {
            case HitKind.Mesh:
                writer.WriteString("kind", "mesh");
                writer.WriteNumber("triangle", record.TriangleIndex);
                WriteVectors(writer, "vertices", points, record.PointStart, Math.Min(record.PointCount, 3));

                // The far vertex of the neighbour across each edge: v0-v1, v1-v2, v2-v0
                if (record.PointCount > 3)
                {
                    WriteVectors(writer, "neighbours", points, record.PointStart + 3, record.PointCount - 3);
                }

                break;
            case HitKind.Hull:
                writer.WriteString("kind", "hull");
                WriteVectors(writer, "vertices", points, record.PointStart, record.PointCount);
                break;
            case HitKind.Plane:
                writer.WriteString("kind", "plane");
                break;
            default:
                throw new UnreachableException();
        }

        writer.WriteEndObject();
    }

    private static void WriteClip(Utf8JsonWriter writer, in ClipRecord clip)
    {
        writer.WriteString("type", "clip");
        writer.WriteNumber("bump", clip.Bump);
        writer.WriteString("outcome", clip.Outcome.ToString());
        WriteFloat(writer, "timeLeft", clip.TimeLeft);
        WriteFloat(writer, "fraction", clip.Fraction);
        writer.WriteBoolean("fractionZeroed", clip.FractionZeroed);
        writer.WriteBoolean("moved", clip.Moved);
        writer.WriteNumber("planeCount", clip.PlaneCount);
        WriteVector(writer, "origin", clip.Origin);
        WriteVector(writer, "normal", clip.Normal);
        WriteVector(writer, "velocityIn", clip.VelocityIn);
        WriteVector(writer, "velocityOut", clip.VelocityOut);
    }

    private static void WriteNote(Utf8JsonWriter writer, in NoteRecord note)
    {
        writer.WriteString("type", "note");
        writer.WriteString("caller", note.Caller);
        writer.WriteString("text", note.Text);
        WriteVector(writer, "origin", note.Origin);
        WriteVector(writer, "velocity", note.Velocity);

        if (!float.IsNaN(note.Value))
        {
            WriteFloat(writer, "value", note.Value);
        }
    }

    private static void WriteState(Utf8JsonWriter writer, string name, in MovementState state)
    {
        writer.WriteStartObject(name);
        WriteVector(writer, "origin", state.Origin);
        WriteVector(writer, "velocity", state.Velocity);

        if (state.BaseVelocity != Vector3.Zero)
        {
            WriteVector(writer, "baseVelocity", state.BaseVelocity);
        }

        writer.WriteBoolean("onGround", (state.Flags & PlayerFlags.OnGround) != 0);
        writer.WriteString("flags", state.Flags.ToString());
        writer.WriteString("moveType", state.MoveType.ToString());

        if (state.GroundEntity is { } ground)
        {
            writer.WriteString("groundEntity", ground.Classname);
        }

        writer.WriteBoolean("ducked", state.Ducked);
        WriteFloat(writer, "duckAmount", state.DuckAmount);
        WriteFloat(writer, "surfaceFriction", state.SurfaceFriction);
        writer.WriteNumber("waterLevel", state.WaterLevel);
        writer.WriteEndObject();
    }

    internal static void WriteVector(Utf8JsonWriter writer, string name, Vector3 value)
    {
        writer.WritePropertyName(name);
        WriteVectorValue(writer, value);
    }

    private static void WriteVectors(Utf8JsonWriter writer, string name, List<Vector3> points, int start, int count)
    {
        writer.WriteStartArray(name);

        for (var i = start; i < start + count; i++)
        {
            if (float.IsNaN(points[i].X))
            {
                writer.WriteNullValue();
                continue;
            }

            WriteVectorValue(writer, points[i]);
        }

        writer.WriteEndArray();
    }

    private static void WriteVectorValue(Utf8JsonWriter writer, Vector3 value)
    {
        writer.WriteStartArray();
        WriteFloatValue(writer, value.X);
        WriteFloatValue(writer, value.Y);
        WriteFloatValue(writer, value.Z);
        writer.WriteEndArray();
    }

    internal static void WriteFloat(Utf8JsonWriter writer, string name, float value)
    {
        writer.WritePropertyName(name);
        WriteFloatValue(writer, value);
    }

    // JSON has no NaN or infinity, which are exactly what a broken move may produce
    internal static void WriteFloatValue(Utf8JsonWriter writer, float value)
    {
        if (float.IsFinite(value))
        {
            writer.WriteNumberValue(value);
        }
        else
        {
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private enum EventKind : byte
    {
        SliceStart,
        SliceEnd,
        Trace,
        Clip,
        Note,
    }

    private enum HitKind : byte
    {
        Plane,
        Mesh,
        Hull,
    }

    private readonly record struct EventRef(EventKind Kind, int Index);

    private readonly record struct TraceRecord(
        string Caller,
        string CollisionName,
        Vector3 Start,
        Vector3 End,
        Vector3 Mins,
        Vector3 Maxs,
        GameTrace Trace,
        HitKind HitKind,
        int TriangleIndex,
        int PointStart,
        int PointCount);

    private readonly record struct NoteRecord(string Caller, string Text, Vector3 Origin, Vector3 Velocity, float Value);

    // Reused as the ring wraps, so recording allocates nothing once every slot has been filled
    private sealed class TickRecord
    {
        public int Tick;
        public int CommandNumber;
        public Vector3 ViewAngles;
        public float ForwardMove;
        public float LeftMove;
        public float UpMove;
        public ButtonState Buttons;
        public MovementState Before;
        public MovementState After;

        public readonly List<string> Markers = [];
        public readonly List<SubtickMoveStep> SubtickMoves = [];
        public readonly List<EventRef> Events = [];
        public readonly List<SliceRecord> Slices = [];
        public readonly List<MovementState> States = [];
        public readonly List<TraceRecord> Traces = [];
        public readonly List<ClipRecord> Clips = [];
        public readonly List<NoteRecord> Notes = [];
        public readonly List<Vector3> Points = [];

        // The whole movement state before the command, in the order it is visited
        private readonly ArrayBufferWriter<byte> State = new();
        private readonly BinaryStateWriter StateWriter;

        public TickRecord()
        {
            StateWriter = new BinaryStateWriter(State);
        }

        public void SaveState(MovementServices services)
        {
            State.ResetWrittenCount();
            services.VisitState(StateWriter);
        }

        public void LoadState(MovementServices services) => services.VisitState(new BinaryStateReader(State.WrittenMemory));

        public void Reset()
        {
            Markers.Clear();
            SubtickMoves.Clear();
            Events.Clear();
            Slices.Clear();
            States.Clear();
            Traces.Clear();
            Clips.Clear();
            Notes.Clear();
            Points.Clear();
        }
    }
}
