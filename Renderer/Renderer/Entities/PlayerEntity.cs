using ValveResourceFormat.Renderer.Input;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// The player, as an entity the rest of the world can see. Source's <c>CBasePlayer</c> is an entity like
/// any other; here the movement itself still lives behind an <see cref="IPlayerController"/>, and this
/// mirrors it into the entity world so triggers have something to touch and teleports something to move.
/// </summary>
/// <remarks>
/// Position comes from the controller rather than being simulated: the player moves off the input, per
/// rendered frame, not on the entity tick. This entity is only a view onto that state, so
/// <see cref="TryGetTouchBounds"/> reads the hull live rather than the last tick's copy.
/// </remarks>
public sealed class PlayerEntity : BaseEntity
{
    /// <summary>
    /// How far the player can reach to press something, in units. Source's <c>PLAYER_USE_RADIUS</c>.
    /// </summary>
    public const float UseRange = 80f;

    /// <summary>Gets the controller whose state this entity reflects.</summary>
    public IPlayerController Controller { get; }

    /// <summary>
    /// Gets the buttons as of the current tick: what is held, and what changed since the tick before.
    /// </summary>
    private PlayerButtonState Buttons;

    /// <summary>
    /// Creates the player entity for a movement controller.
    /// </summary>
    public PlayerEntity(EntitySystem system, IPlayerController controller) : base(system, "player")
    {
        Controller = controller;

        // Nothing traces against the player, and the player is what enters triggers rather than a volume
        // anything can enter.
        IsSolid = false;
    }

    /// <inheritdoc/>
    public override bool TryGetTouchBounds(out Vector3 center, out Vector3 halfExtents)
    {
        halfExtents = Controller.HullHalfExtents;
        center = Controller.Position + new Vector3(0, 0, halfExtents.Z);
        return true;
    }

    /// <summary>
    /// Teleports the player. <see cref="BaseEntity.Origin"/> is the feet, which is what
    /// <see cref="IPlayerController.Teleport"/> takes, so the destination passes straight through.
    /// </summary>
    public override void Teleport(Vector3 origin, Vector3? angles)
    {
        Controller.Teleport(origin, angles);
        SyncFromController();
    }

    /// <inheritdoc/>
    protected override void PhysicsSimulate(float tickInterval)
    {
        // Resets key latches inside player movement, if a key is presseed,
        // unpressed and pressed again in within 3 frames the tick will see it as one press.
        Buttons = Controller.ConsumeButtons();

        // The controller owns the position, so there is nothing to integrate; just keep up with it
        SyncFromController();

        if (Buttons.Pressed(TrackedKeys.E))
        {
            PressUse();
        }
    }

    /// <summary>
    /// Finds the usable entity a reach trace hits first, with the world blocking the way.
    /// </summary>
    /// <param name="from">Trace start, eye position.</param>
    /// <param name="to">Trace end, the far edge of the reach.</param>
    /// <returns>The nearest usable entity in reach, or <see langword="null"/> when there is none.</returns>
    private BaseEntity? FindUseTarget(Vector3 from, Vector3 to)
    {
        var nearest = Scene.PhysicsWorld?.TraceRay(from, to) ?? new Rubikon.TraceResult();
        BaseEntity? target = null;

        foreach (var entity in EntitySystem.Entities)
        {
            if (entity.IsRemoved
                || (entity.ObjectCaps & EntityCapability.UsableMask) == 0
                || entity.Collider is not { IsEmpty: false } collider)
            {
                continue;
            }

            if (nearest.MinimizeWith(collider.TraceRay(from, to)))
            {
                target = entity;
            }
        }

        return target;
    }

    /// <summary>
    /// Presses whatever the player is looking at within <see cref="UseRange"/>
    /// </summary>
    private void PressUse()
    {
        var from = Controller.EyePosition;
        var target = FindUseTarget(from, from + Controller.ViewForward * UseRange);

        target?.Use(this);
    }

    private void SyncFromController()
    {
        Origin = Controller.Position;
        Velocity = Controller.Velocity;
    }
}
