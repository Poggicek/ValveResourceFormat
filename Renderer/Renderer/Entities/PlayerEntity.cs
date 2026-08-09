namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// The player, as an entity the rest of the world can see. Source's <c>CBasePlayer</c> is an entity like
/// any other; here the movement itself still lives behind an <see cref="IPlayerController"/>, and this
/// mirrors it into the entity world so triggers have something to touch and teleports something to move.
/// </summary>
/// <remarks>
/// Position is read from the controller rather than simulated: the player moves per rendered frame, off
/// the input, not on the entity tick. That makes this entity a view onto the controller's state, which is
/// why <see cref="TryGetTouchBounds"/> reads the hull live instead of trusting the last tick's copy.
/// </remarks>
public sealed class PlayerEntity : BaseEntity
{
    /// <summary>
    /// How far the player can reach to press something, in units. Source's <c>PLAYER_USE_RADIUS</c>.
    /// </summary>
    public const float UseRange = 80f;

    /// <summary>Gets the controller whose state this entity reflects.</summary>
    public IPlayerController Controller { get; }

    /// <summary>Gets the buttons held as of this tick. Source's <c>m_nButtons</c>.</summary>
    public PlayerButton Buttons { get; private set; }

    /// <summary>Gets the buttons that went down this tick. Source's <c>m_afButtonPressed</c>.</summary>
    public PlayerButton ButtonsPressed { get; private set; }

    /// <summary>Gets the buttons that came up this tick. Source's <c>m_afButtonReleased</c>.</summary>
    public PlayerButton ButtonsReleased { get; private set; }

    /// <summary>
    /// Creates the player entity for a movement controller.
    /// </summary>
    /// <param name="system">The world the player belongs to.</param>
    /// <param name="controller">The player state to mirror.</param>
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
    /// <param name="origin">Where the feet arrive.</param>
    /// <param name="angles">View angles to adopt, or <see langword="null"/> to keep the current ones.</param>
    public override void Teleport(Vector3 origin, Vector3? angles)
    {
        Controller.Teleport(origin, angles);
        SyncFromController();
    }

    /// <summary>
    /// Presses whatever the player is looking at within <see cref="UseRange"/>, the <c>+use</c> command.
    /// </summary>
    /// <returns>The entity that was pressed, or <see langword="null"/> if nothing was in reach.</returns>
    public BaseEntity? PressUse()
    {
        var from = Controller.EyePosition;
        var target = EntitySystem.FindUseTarget(from, from + Controller.ViewForward * UseRange);

        target?.Use(this);

        return target;
    }

    /// <summary>Whether a button is currently held.</summary>
    /// <param name="button">The button, or buttons, to test for.</param>
    public bool IsButtonDown(PlayerButton button) => (Buttons & button) != 0;

    /// <summary>Whether a button went down this tick.</summary>
    /// <param name="button">The button, or buttons, to test for.</param>
    public bool WasButtonPressed(PlayerButton button) => (ButtonsPressed & button) != 0;

    /// <summary>Whether a button came up this tick.</summary>
    /// <param name="button">The button, or buttons, to test for.</param>
    public bool WasButtonReleased(PlayerButton button) => (ButtonsReleased & button) != 0;

    /// <inheritdoc/>
    protected override void PhysicsSimulate(float tickInterval)
    {
        // The controller owns the position, so there is nothing to integrate; just keep up with it
        SyncFromController();

        // Collected here rather than when the key was hit, so what a button sets off runs on the tick
        var buttons = Controller.ConsumeButtons();

        Buttons = buttons.Held;
        ButtonsPressed = buttons.Pressed;
        ButtonsReleased = buttons.Released;

        if (WasButtonPressed(PlayerButton.Use))
        {
            PressUse();
        }
    }

    private void SyncFromController()
    {
        Origin = Controller.Position;
        Velocity = Controller.Velocity;
    }
}
