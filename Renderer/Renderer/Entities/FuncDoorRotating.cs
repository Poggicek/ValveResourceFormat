using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>func_door_rotating</c>. A door that swings about one axis instead of sliding, Source's
/// <c>CRotDoor</c>. Maps use it as a lever as often as a door: something that swings when pressed and
/// swings back.
/// </summary>
/// <remarks>
/// Everything but the travel is <see cref="FuncDoor"/>'s: the state machine, the lock, the wait, and the
/// outputs are the same, because in the engine they are the same class. Only where the two ends are, and
/// how it gets between them, changes.
/// </remarks>
public sealed class FuncDoorRotating : FuncDoor
{
    /// <summary>What a <c>func_door_rotating</c>'s <c>spawnflags</c> mean beyond the door's own.</summary>
    [Flags]
    public enum RotatingSpawnFlag : uint
    {
        /// <summary>Swings the other way.</summary>
        Backwards = 2,

        /// <summary>Swings about the world X axis (roll). Hammer "X Axis".</summary>
        RollAxis = 64,

        /// <summary>Swings about the world Y axis (pitch). Hammer "Y Axis".</summary>
        PitchAxis = 128,
    }

    /// <summary>Gets the angle the door rests at when closed.</summary>
    public Vector3 AngleClosed { get; private set; }

    /// <summary>Gets the angle the door rests at when open.</summary>
    public Vector3 AngleOpen { get; private set; }

    /// <summary>Gets how far the door swings, in degrees.</summary>
    public float Distance { get; private set; }

    private Vector3 moveAngles;

    /// <summary>
    /// Initializes a <c>func_door_rotating</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public FuncDoorRotating(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    protected override void SetUpTravel()
    {
        moveAngles = GetAxisDirection(
            HasSpawnFlags(RotatingSpawnFlag.RollAxis),
            HasSpawnFlags(RotatingSpawnFlag.PitchAxis));

        if (HasSpawnFlags(RotatingSpawnFlag.Backwards))
        {
            moveAngles = -moveAngles;
        }

        Distance = KeyValues.GetFloatProperty("distance", 90f);

        if (Distance == 0f)
        {
            Distance = 90f;
        }

        AngleClosed = Angles;
        AngleOpen = AngleClosed + (moveAngles * Distance);

        // A swinging door does not slide, so the positions the base class worked out mean nothing to it
        PositionOpen = Origin;
        PositionClosed = Origin;
    }

    /// <inheritdoc/>
    protected override void SwapEnds()
    {
        (AngleClosed, AngleOpen) = (AngleOpen, AngleClosed);

        moveAngles = -moveAngles;
        Angles = AngleClosed;

        SnapInterpolation();
    }

    /// <inheritdoc/>
    protected override void StartMove(bool opening) => AngularMove(opening ? AngleOpen : AngleClosed, Speed);
}
