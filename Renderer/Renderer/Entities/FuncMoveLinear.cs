using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>func_movelinear</c>. A brush that slides a set distance along its <c>movedir</c>, Source's
/// <c>CFuncMoveLinear</c>. A door is told how far to go by its own size; this one is told outright, which
/// is what a map uses to move something a long way, or a precise way.
/// </summary>
/// <remarks>
/// Almost a door, so it is one: the state machine, the inputs and the outputs are
/// <see cref="FuncDoor"/>'s. Only how far it travels differs, plus <c>startposition</c> and the
/// <c>SetPosition</c> input, which place it anywhere along that travel rather than at one end.
/// </remarks>
public sealed class FuncMoveLinear : FuncDoor
{
    /// <summary>Gets how far the brush travels along its move direction, in units.</summary>
    public float MoveDistance { get; private set; }

    /// <summary>
    /// Stays wherever it was sent. Source's <c>CFuncMoveLinear</c> is a <c>CBaseToggle</c> rather than a
    /// <c>CBaseDoor</c>, and its <c>Open</c> travels to the open position and stops there: the closing is
    /// the map's to ask for. Left to the door's own rule it would shut itself after four seconds, which an
    /// elevator door held open by a scene would do in the middle of it.
    /// </summary>
    protected override bool ReturnsAfterWait => false;

    /// <summary>
    /// Initializes a <c>func_movelinear</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public FuncMoveLinear(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    protected override void SetUpTravel()
    {
        MoveDistance = KeyValues.GetFloatProperty("movedistance");

        // Authored outright rather than measured off the brush, so the door's lip has nothing to say here
        PositionOpen = PositionClosed + (MoveDirection * MoveDistance);

        var startPosition = KeyValues.GetFloatProperty("startposition");

        if (startPosition > 0f)
        {
            Origin = Vector3.Lerp(PositionClosed, PositionOpen, Math.Clamp(startPosition, 0f, 1f));
            SnapInterpolation();
        }
    }

    /// <summary>
    /// Sends the brush to a point along its travel, where 0 is closed and 1 is fully open.
    /// </summary>
    /// <param name="data">Carries the position as a fraction.</param>
    [EntityInput("SetPosition")]
    private void InputSetPosition(EntityInputData data)
    {
        var fraction = Math.Clamp(data.Float(), 0f, 1f);

        MoveTo(Vector3.Lerp(PositionClosed, PositionOpen, fraction));
    }
}
