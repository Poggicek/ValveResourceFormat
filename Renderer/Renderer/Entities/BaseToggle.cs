using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// The base of the brush entities that travel between two places, Source's <c>CBaseToggle</c>: buttons,
/// doors, and anything else that slides open and shut.
/// </summary>
/// <remarks>
/// The travel is a <c>LinearMove</c>, not an interpolation: the entity is given a constant velocity and a
/// deadline, the tick integrates it like anything else that moves, and the arrival lands on the exact
/// destination rather than wherever the last tick happened to leave it.
/// </remarks>
public abstract class BaseToggle : BaseModelEntity
{
    /// <summary>Where a moving brush is in its travel.</summary>
    protected enum ToggleState
    {
        /// <summary>At the closed end, its authored position.</summary>
        AtBottom,

        /// <summary>At the open end.</summary>
        AtTop,

        /// <summary>Travelling towards the open end.</summary>
        GoingUp,

        /// <summary>Travelling back towards the closed end.</summary>
        GoingDown,
    }

    /// <summary>Gets or sets how fast the brush travels, in units per second.</summary>
    public float Speed { get; protected set; }

    /// <summary>Gets or sets how much of the brush stays proud of its opening, the <c>lip</c> keyvalue.</summary>
    public float Lip { get; protected set; }

    /// <summary>Gets the direction the brush travels, resolved from its angles or <c>movedir</c>.</summary>
    protected Vector3 MoveDirection { get; private set; }

    /// <summary>Gets where the brush is currently heading.</summary>
    protected Vector3 FinalDestination { get; private set; }

    /// <summary>Gets whether a <see cref="LinearMove"/> or <see cref="AngularMove"/> is under way.</summary>
    protected bool IsLinearMoving { get; private set; }

    private Vector3 finalAngle;
    private bool isAngularMoving;

    /// <summary>
    /// Initializes a moving brush from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    protected BaseToggle(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <summary>
    /// Sets off towards a destination at <see cref="Speed"/>, arriving when the move-done comes due.
    /// Source's <c>CBaseToggle::LinearMove</c>: constant velocity and a deadline, not a lerp.
    /// </summary>
    /// <param name="destination">Where the entity is heading.</param>
    protected void LinearMove(Vector3 destination)
    {
        FinalDestination = destination;

        if (destination == Origin || Speed <= 0f)
        {
            // Nowhere to go, so the arrival is now
            MoveDone();
            return;
        }

        var delta = destination - Origin;
        var travelTime = delta.Length() / Speed;

        IsLinearMoving = true;
        Velocity = delta / travelTime;

        SetMoveDoneTime(travelTime);
    }

    /// <summary>
    /// Turns towards a destination angle at <paramref name="speed"/> degrees per second, arriving when the
    /// move-done comes due. Source's <c>CBaseToggle::AngularMove</c>.
    /// </summary>
    /// <param name="destinationAngle">The QAngle to end on.</param>
    /// <param name="speed">Turn rate in degrees per second.</param>
    protected void AngularMove(Vector3 destinationAngle, float speed)
    {
        finalAngle = destinationAngle;

        if (destinationAngle == Angles || speed <= 0f)
        {
            MoveDone();
            return;
        }

        var delta = destinationAngle - Angles;

        // Source's floor on the travel, so a turn cannot be so short that the tick steps over it
        var travelTime = MathF.Max(delta.Length() / speed, 0.01f);

        isAngularMoving = true;
        AngularVelocity = delta / travelTime;

        SetMoveDoneTime(travelTime);
    }

    /// <summary>
    /// Ends a travel exactly on its destination. Call from <c>MoveDone</c> before acting on the arrival.
    /// </summary>
    /// <returns><see langword="true"/> when a travel was in progress and has now landed.</returns>
    protected bool FinishLinearMove()
    {
        if (isAngularMoving)
        {
            // Land exactly on the destination rather than wherever the last tick left off
            isAngularMoving = false;
            AngularVelocity = Vector3.Zero;
            Angles = finalAngle;

            return true;
        }

        if (!IsLinearMoving)
        {
            return false;
        }

        IsLinearMoving = false;
        Velocity = Vector3.Zero;
        Origin = FinalDestination;

        return true;
    }

    /// <summary>
    /// Reads the axis a rotating brush turns about from its spawnflags. Source's
    /// <c>CBaseToggle::AxisDir</c>, whose flag names are for the QAngle component they set, so its "roll"
    /// flag is what Hammer labels X Axis.
    /// </summary>
    /// <param name="rollAxis">Whether the "X Axis" flag is set.</param>
    /// <param name="pitchAxis">Whether the "Y Axis" flag is set.</param>
    /// <returns>The axis as a QAngle direction.</returns>
    protected static Vector3 GetAxisDirection(bool rollAxis, bool pitchAxis)
    {
        if (rollAxis)
        {
            return new Vector3(0, 0, 1);
        }

        return pitchAxis ? new Vector3(1, 0, 0) : new Vector3(0, 1, 0);
    }

    /// <summary>
    /// Reads the direction the brush travels. Source encodes it in <c>angles</c>, with two magic values
    /// for straight up and down, and zeroes the angles afterwards because they were never an orientation.
    /// Source 2 authors it as its own <c>movedir</c> keyvalue on some entities, and there the brush's own
    /// angles mean what they say, so they are left alone.
    /// </summary>
    /// <param name="consumeAngles">
    /// Whether the angles are the travel direction and should be cleared once read, which is what
    /// <c>CBaseButton::Spawn</c> does through <c>SetMovedir</c>. A door does not: <c>CBaseDoor::Spawn</c>
    /// reads only the <c>movedir</c> keyvalue and leaves the brush's orientation alone, and a door that
    /// swings needs those angles kept, since they are where its travel starts from.
    /// </param>
    protected void ResolveMoveDirection(bool consumeAngles = true)
    {
        var hasMoveDir = KeyValues.ContainsKey("movedir");
        var directionAngles = hasMoveDir ? KeyValues.GetVector3Property("movedir") : Angles;

        MoveDirection = directionAngles switch
        {
            { X: 0f, Y: -1f, Z: 0f } => new Vector3(0, 0, 1),   // straight up
            { X: 0f, Y: -2f, Z: 0f } => new Vector3(0, 0, -1),  // straight down
            _ => EntityTransformHelper.QAngleToForwardDirection(directionAngles),
        };

        // Authored in the brush's own frame rather than the world's, which is how Source 2 spells the
        // direction for anything a mapper rotates as a unit. Read before the angles are given up below.
        if (KeyValues.GetBooleanProperty("movedir_islocal"))
        {
            MoveDirection = Vector3.TransformNormal(
                MoveDirection,
                EntityTransformHelper.CreateRotationMatrixFromEulerAngles(Angles));
        }

        if (!hasMoveDir && consumeAngles)
        {
            Angles = Vector3.Zero;
        }
    }

    /// <summary>
    /// How far the brush slides: its own length along the travel axis, less the lip that keeps it proud.
    /// </summary>
    /// <remarks>
    /// Source subtracts a further 2 units because the engine hands it a brush bound that is 1 unit larger
    /// in every direction. The bounds here come from the compiled collision hull and are not padded, so
    /// the same authored <c>lip</c> lands in the same place without that correction.
    /// </remarks>
    protected float GetTravelDistance()
    {
        var size = Collider?.LocalBounds.Size ?? Vector3.Zero;

        return MathF.Abs(MoveDirection.X * size.X)
            + MathF.Abs(MoveDirection.Y * size.Y)
            + MathF.Abs(MoveDirection.Z * size.Z)
            - Lip;
    }
}
