using Microsoft.Extensions.Logging;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>func_tracktrain</c>. A brush that drives along a chain of <see cref="PathTrack"/> nodes, Source's
/// <c>CFuncTrackTrain</c>. The lift, the platform, and the vehicle a map drives away at the end.
/// </summary>
/// <remarks>
/// <para>
/// The route is followed node by node: the train heads for its current destination at <see cref="Speed"/>,
/// and on arrival the node fires <c>OnPass</c> and hands over the one after it. A node with nothing after
/// it is the end of the line, and the train stops there.
/// </para>
/// <para>
/// It moves by <see cref="BaseEntity.Velocity"/> rather than by teleporting between nodes, which is what
/// lets anything standing on it be carried along. Not simulated: the wheel-and-length model that swings a
/// long train through a corner, the speed ramps a node can impose, and the sounds.
/// </para>
/// </remarks>
public sealed class FuncTrackTrain : BaseModelEntity
{
    /// <summary>How close to a node counts as reaching it, in units.</summary>
    private const float ArrivalDistance = 1f;

    /// <summary>Gets the top speed in units per second.</summary>
    public float MaxSpeed { get; private set; }

    /// <summary>Gets the speed the train starts moving at when told to go.</summary>
    public float StartSpeed { get; private set; }

    /// <summary>Gets the current speed in units per second; zero when stopped.</summary>
    public float Speed { get; private set; }

    /// <summary>Gets the node the train is currently heading for.</summary>
    public PathTrack? Destination { get; private set; }

    /// <summary>Gets whether the train turns to face the way it is going.</summary>
    public bool FacesTravelDirection { get; private set; }

    private Vector3 spawnAngles;
    private Vector3? referenceFacing;

    /// <summary>
    /// Initializes a <c>func_tracktrain</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public FuncTrackTrain(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        MaxSpeed = MathF.Abs(KeyValues.GetFloatProperty("speed", 100f));
        StartSpeed = MathF.Abs(KeyValues.GetFloatProperty("startspeed", MaxSpeed));

        if (MaxSpeed == 0f)
        {
            MaxSpeed = 100f;
        }

        // 0 is "keep the authored angles"; every other mode turns the train along its route, and the
        // difference between them is the interpolation this does not do
        FacesTravelDirection = KeyValues.GetInt32Property("orientationtype") != 0;

        spawnAngles = Angles;
    }

    /// <inheritdoc/>
    public override void Activate()
    {
        var targetName = KeyValues.GetStringProperty("target");

        if (string.IsNullOrEmpty(targetName))
        {
            EntitySystem.Logger.LogWarning("func_tracktrain '{TargetName}' has no path to follow", TargetName);
            return;
        }

        foreach (var target in EntitySystem.FindTargets(targetName, caller: this))
        {
            if (target is PathTrack start)
            {
                // The train is authored sitting on its first node, so that node is where it has been
                // rather than where it is going: the route starts at the one after it
                Destination = start.Next ?? start;
                break;
            }
        }
    }

    /// <summary>Drives one tick's worth of the route.</summary>
    public override void Think()
    {
        if (Speed == 0f || Destination is not { } destination)
        {
            Stop();
            return;
        }

        var delta = destination.Origin - Origin;
        var distance = delta.Length();
        var step = Speed * EntitySystem.TickInterval;

        if (distance <= MathF.Max(step, ArrivalDistance))
        {
            ArriveAt(destination);
            return;
        }

        var direction = delta / distance;

        Velocity = direction * Speed;

        if (FacesTravelDirection)
        {
            // Turned by how far the route has swung since it set off, not set outright to face along it.
            // A brush entity is compiled already pointing the way the mapper aimed it, so facing it down
            // the first leg would spin it by whatever that leg's bearing happens to be.
            var facing = DirectionToAngles(direction);

            referenceFacing ??= facing;

            Angles = spawnAngles + (facing - referenceFacing.Value);
        }

        SetNextThink(EntitySystem.CurrentTime + EntitySystem.TickInterval);
    }

    /// <summary>Sets the train moving along its route.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("StartForward")]
    private void InputStartForward(EntityInputData data) => Start(StartSpeed == 0f ? MaxSpeed : StartSpeed);

    /// <summary>Brings the train to a halt where it stands.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Stop")]
    private void InputStop(EntityInputData data) => Stop();

    /// <summary>Starts a stopped train, stops a moving one.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Toggle")]
    private void InputToggle(EntityInputData data)
    {
        if (Speed != 0f)
        {
            Stop();
        }
        else
        {
            Start(StartSpeed == 0f ? MaxSpeed : StartSpeed);
        }
    }

    /// <summary>Sets the speed as a fraction of the train's top speed.</summary>
    /// <param name="data">Carries the fraction, 0 to 1.</param>
    [EntityInput("SetSpeed")]
    private void InputSetSpeed(EntityInputData data)
        => Start(Math.Clamp(data.Float(), 0f, 1f) * MaxSpeed);

    /// <summary>Sets the speed outright, in units per second.</summary>
    /// <param name="data">Carries the speed.</param>
    [EntityInput("SetSpeedReal")]
    private void InputSetSpeedReal(EntityInputData data)
        => Start(Math.Clamp(MathF.Abs(data.Float()), 0f, MaxSpeed));

    private void Start(float speed)
    {
        Speed = speed;

        if (Speed == 0f)
        {
            Stop();
            return;
        }

        // Driven from the think rather than a move-done deadline, because the route bends: each tick has
        // to look at where the next node is, not just when the current leg runs out
        SetNextThink(EntitySystem.CurrentTime + EntitySystem.TickInterval);
    }

    private void Stop()
    {
        Speed = 0f;
        Velocity = Vector3.Zero;

        SetNextThink(-1f);
    }

    private void ArriveAt(PathTrack node)
    {
        Origin = node.Origin;
        Velocity = Vector3.Zero;

        node.Pass(this);

        // The node's own outputs may have stopped the train, and that decision outranks the route
        if (Speed == 0f)
        {
            return;
        }

        Destination = node.Next;

        if (Destination == null)
        {
            Stop();
            return;
        }

        SetNextThink(EntitySystem.CurrentTime + EntitySystem.TickInterval);
    }

    /// <summary>Turns a direction of travel into the QAngle that faces along it.</summary>
    private static Vector3 DirectionToAngles(Vector3 direction)
    {
        var yaw = MathF.Atan2(direction.Y, direction.X) * (180f / MathF.PI);
        var pitch = -MathF.Asin(Math.Clamp(direction.Z, -1f, 1f)) * (180f / MathF.PI);

        return new Vector3(pitch, yaw, 0f);
    }
}
