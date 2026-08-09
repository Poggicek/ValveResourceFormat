using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>func_button</c>. A brush that slides in when pressed and, unless it is told to stay in, slides back
/// out after <c>wait</c> seconds.
/// </summary>
/// <remarks>
/// <para>
/// Ported from Source's <c>CBaseButton</c>, movement included: the travel is a <c>LinearMove</c>, which is
/// a velocity plus a move-done time rather than a per-tick interpolation, and each leg hands over to the
/// next state through the move-done callback. That is what <see cref="BaseEntity.MoveDone"/> exists for.
/// </para>
/// <para>
/// Not simulated: the button sounds, <c>health</c>-driven damage activation, and the sparks. Touch
/// activation is wired up, but a solid button is only touched by something that actually overlaps it,
/// which movement collision normally prevents, so use activation is the path that matters here.
/// </para>
/// </remarks>
public sealed class FuncButton : BaseEntity
{
    /// <summary>What a <c>func_button</c>'s <c>spawnflags</c> mean.</summary>
    [Flags]
    public enum SpawnFlag : uint
    {
        /// <summary>No flags.</summary>
        None = 0,

        /// <summary>Fires without sliding anywhere.</summary>
        DontMove = 1,

        /// <summary>Stays in until pressed again, rather than returning after <c>wait</c>.</summary>
        Toggle = 32,

        /// <summary>Presses when something touches it.</summary>
        TouchActivates = 256,

        /// <summary>Presses when damaged. Nothing here deals damage.</summary>
        DamageActivates = 512,

        /// <summary>Presses when a player looks at it and presses use.</summary>
        UseActivates = 1024,

        /// <summary>Starts locked, refusing to press until unlocked.</summary>
        StartsLocked = 2048,

        /// <summary>Sparks while out. Not simulated.</summary>
        SparkIfOff = 4096,

        /// <summary>Every flag that names a way to press the button.</summary>
        AnyActivation = TouchActivates | DamageActivates | UseActivates,
    }

    /// <summary>Where a button is in its travel. Source's <c>m_toggle_state</c>.</summary>
    public enum ButtonState
    {
        /// <summary>Out, at rest.</summary>
        AtBottom,

        /// <summary>Sliding in.</summary>
        GoingUp,

        /// <summary>In, at rest.</summary>
        AtTop,

        /// <summary>Sliding back out.</summary>
        GoingDown,
    }

    /// <summary>Which state function the pending move-done runs, Source's <c>m_pfnCallWhenMoveDone</c>.</summary>
    private enum MoveDoneFunction
    {
        None,
        TriggerAndWait,
        ButtonReturn,
        ButtonBackHome,
    }

    /// <summary>Gets where the button is in its travel.</summary>
    public ButtonState State { get; private set; }

    /// <summary>Gets the travel speed in units per second.</summary>
    public float Speed { get; private set; }

    /// <summary>Gets the seconds the button stays in before returning; -1 means it stays in for good.</summary>
    public float Wait { get; private set; }

    /// <summary>Gets the distance held back from a full brush-length travel, so the button stays proud.</summary>
    public float Lip { get; private set; }

    /// <summary>Gets whether the button refuses to press.</summary>
    public bool IsLocked { get; private set; }

    /// <summary>
    /// Gets whether the button has been switched off. A disabled button is not drawn, not solid, and not
    /// something a trace can find, so a pair of them can share a spot and take turns.
    /// </summary>
    public bool IsDisabled { get; private set; }

    /// <summary>
    /// Gets whether a player can press this button. A button that names no way of being activated at all
    /// is treated as use-activated, for the same reason a trigger with no filters accepts everything: the
    /// flag values are Source 1's and the Source 2 games this renders are not confirmed to match, so the
    /// permissive reading keeps the button working rather than leaving it inert. A locked button is still
    /// usable, because refusing the press is what produces <c>OnUseLocked</c>.
    /// </summary>
    public override EntityCapability ObjectCaps
        => !IsDisabled && (HasSpawnFlags(SpawnFlag.UseActivates) || !HasSpawnFlags(SpawnFlag.AnyActivation))
            ? EntityCapability.ImpulseUse
            : EntityCapability.None;

    private Vector3 moveDirection;
    private Vector3 positionOut;
    private Vector3 positionIn;
    private Vector3 finalDestination;
    private bool staysPushed;
    private bool isLinearMoving;
    private MoveDoneFunction moveDoneFunction;
    private BaseEntity? lastActivator;

    /// <summary>
    /// Initializes a <c>func_button</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public FuncButton(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        ResolveMoveDirection();

        SetModel();

        Speed = KeyValues.GetFloatProperty("speed", 40f);

        if (Speed == 0f)
        {
            Speed = 40f;
        }

        Wait = KeyValues.GetFloatProperty("wait", 1f);

        if (Wait == 0f)
        {
            Wait = 1f;
        }

        Lip = KeyValues.GetFloatProperty("lip");

        if (Lip == 0f)
        {
            Lip = 4f;
        }

        IsLocked = HasSpawnFlags(SpawnFlag.StartsLocked);
        staysPushed = Wait == -1f;

        // Touch activation needs the button to report what is inside it, without giving up being solid
        IsTrigger = HasSpawnFlags(SpawnFlag.TouchActivates);

        positionOut = Origin;
        positionIn = positionOut + moveDirection * GetTravelDistance();

        // A button with nowhere to go fires in place
        if (HasSpawnFlags(SpawnFlag.DontMove) || (positionIn - positionOut).Length() < 1f)
        {
            positionIn = positionOut;
        }

        State = ButtonState.AtBottom;
    }

    /// <inheritdoc/>
    public override void Use(BaseEntity? activator)
    {
        // A switched-off button is not there to press, however the press arrived
        if (IsDisabled)
        {
            return;
        }

        // Mid-travel presses are ignored, so a button cannot be interrupted
        if (State is ButtonState.GoingUp or ButtonState.GoingDown)
        {
            return;
        }

        lastActivator = activator;

        if (State == ButtonState.AtTop)
        {
            // Only a toggle button comes back out on a second press
            if (HasSpawnFlags(SpawnFlag.Toggle) && !staysPushed)
            {
                ButtonReturn();
            }

            return;
        }

        ButtonActivate();
    }

    /// <inheritdoc/>
    protected override void OnStartTouch(BaseEntity other)
    {
        if (HasSpawnFlags(SpawnFlag.TouchActivates))
        {
            Use(other);
        }
    }

    /// <inheritdoc/>
    public override void MoveDone()
    {
        if (isLinearMoving)
        {
            // Land exactly on the destination rather than wherever the last tick left off
            isLinearMoving = false;
            Velocity = Vector3.Zero;
            Origin = finalDestination;
        }

        var next = moveDoneFunction;
        moveDoneFunction = MoveDoneFunction.None;

        switch (next)
        {
            case MoveDoneFunction.TriggerAndWait:
                TriggerAndWait();
                break;

            case MoveDoneFunction.ButtonReturn:
                ButtonReturn();
                break;

            case MoveDoneFunction.ButtonBackHome:
                ButtonBackHome();
                break;
        }
    }

    /// <summary>
    /// Switches the button off: not drawn, not solid, and invisible to the player's reach. Source's
    /// <c>Disable</c>, which clears the same three things.
    /// </summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Disable")]
    private void InputDisable(EntityInputData data)
    {
        IsDisabled = true;
        IsSolid = false;
        IsDrawn = false;
    }

    /// <summary>Switches the button back on. Source's <c>Enable</c>.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Enable")]
    private void InputEnable(EntityInputData data)
    {
        IsDisabled = false;
        IsSolid = true;
        IsDrawn = true;
    }

    /// <summary>Locks the button, so pressing it does nothing but report that it is locked.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Lock")]
    private void InputLock(EntityInputData data) => IsLocked = true;

    /// <summary>Unlocks the button.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Unlock")]
    private void InputUnlock(EntityInputData data) => IsLocked = false;

    /// <summary>Presses the button as though something had used it.</summary>
    /// <param name="data">Carries the entity that fired the output, which becomes the activator.</param>
    [EntityInput("Press")]
    private void InputPress(EntityInputData data) => Use(data.Activator);

    /// <summary>Drives the button in and leaves it there.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("PressIn")]
    private void InputPressIn(EntityInputData data)
    {
        if (State != ButtonState.AtBottom)
        {
            return;
        }

        staysPushed = true;
        ButtonActivate();
    }

    /// <summary>Drives the button back out.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("PressOut")]
    private void InputPressOut(EntityInputData data)
    {
        if (State == ButtonState.AtTop)
        {
            ButtonReturn();
        }
    }

    /// <summary>Starts the button moving in, unless it is locked.</summary>
    private void ButtonActivate()
    {
        if (IsLocked)
        {
            EntitySystem.TriggerOutput(this, "OnUseLocked", lastActivator);
            return;
        }

        State = ButtonState.GoingUp;
        moveDoneFunction = MoveDoneFunction.TriggerAndWait;

        LinearMove(positionIn);
    }

    /// <summary>The button has arrived in: fire, then either stay or schedule the return.</summary>
    private void TriggerAndWait()
    {
        State = ButtonState.AtTop;

        EntitySystem.TriggerOutput(this, "OnPressed", lastActivator);
        EntitySystem.TriggerOutput(this, "OnIn", lastActivator);

        if (staysPushed || HasSpawnFlags(SpawnFlag.Toggle))
        {
            return;
        }

        moveDoneFunction = MoveDoneFunction.ButtonReturn;
        SetMoveDoneTime(Wait);
    }

    /// <summary>Starts the button moving back out.</summary>
    private void ButtonReturn()
    {
        State = ButtonState.GoingDown;
        moveDoneFunction = MoveDoneFunction.ButtonBackHome;

        LinearMove(positionOut);
    }

    /// <summary>The button has arrived back out.</summary>
    private void ButtonBackHome()
    {
        State = ButtonState.AtBottom;

        EntitySystem.TriggerOutput(this, "OnOut", lastActivator);
    }

    /// <summary>
    /// Sets off towards a destination at <see cref="Speed"/>, arriving when the move-done comes due.
    /// Source's <c>CBaseToggle::LinearMove</c>: constant velocity and a deadline, not a lerp.
    /// </summary>
    /// <param name="destination">Where the entity is heading.</param>
    private void LinearMove(Vector3 destination)
    {
        finalDestination = destination;

        if (destination == Origin)
        {
            // Nowhere to go, so the arrival is now
            MoveDone();
            return;
        }

        var delta = destination - Origin;
        var travelTime = delta.Length() / Speed;

        isLinearMoving = true;
        Velocity = delta / travelTime;

        SetMoveDoneTime(travelTime);
    }

    /// <summary>
    /// Reads the direction the button travels. Source encodes it in <c>angles</c>, with two magic values
    /// for straight up and down, and zeroes the angles afterwards because they were never an orientation.
    /// Source 2 authors it as its own <c>movedir</c> keyvalue on some entities, and there the brush's own
    /// angles mean what they say, so they are left alone.
    /// </summary>
    private void ResolveMoveDirection()
    {
        var hasMoveDir = KeyValues.ContainsKey("movedir");
        var directionAngles = hasMoveDir ? KeyValues.GetVector3Property("movedir") : Angles;

        moveDirection = directionAngles switch
        {
            { X: 0f, Y: -1f, Z: 0f } => new Vector3(0, 0, 1),   // straight up
            { X: 0f, Y: -2f, Z: 0f } => new Vector3(0, 0, -1),  // straight down
            _ => EntityTransformHelper.QAngleToForwardDirection(directionAngles),
        };

        if (!hasMoveDir)
        {
            Angles = Vector3.Zero;
        }
    }

    /// <summary>
    /// How far the button slides: its own length along the travel axis, less the lip that keeps it proud.
    /// </summary>
    /// <remarks>
    /// Source subtracts a further 2 units because the engine hands it a brush bound that is 1 unit larger
    /// in every direction. The bounds here come from the compiled collision hull and are not padded, so
    /// the same authored <c>lip</c> lands in the same place without that correction.
    /// </remarks>
    private float GetTravelDistance()
    {
        var size = Collider?.LocalBounds.Size ?? Vector3.Zero;

        return MathF.Abs(moveDirection.X * size.X)
            + MathF.Abs(moveDirection.Y * size.Y)
            + MathF.Abs(moveDirection.Z * size.Z)
            - Lip;
    }
}
