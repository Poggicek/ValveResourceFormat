using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>func_button</c>. A brush that slides in when pressed and, unless it is told to stay in, slides back
/// out after <c>wait</c> seconds.
/// </summary>
/// <remarks>
/// </remarks>
public sealed class FuncButton : BaseModelEntity
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

        /// <summary>Shakes when used while locked. Not simulated.</summary>
        JiggleIfUseLocked = 8192,

        /// <summary>Nothing collides with it, so it can only be pressed by the player's radius search.</summary>
        NotSolid = 16384,

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

    /// <summary>
    /// Which way a press drives the button, Source's <c>BUTTON_CODE</c>. Each one refuses a different set
    /// of states, which is what separates "press it" from "press it in" and "press it out".
    /// </summary>
    private enum ButtonCode
    {
        /// <summary>Do nothing at all.</summary>
        Nothing,

        /// <summary>Drive it out, if it is not already coming out.</summary>
        Return,

        /// <summary>Drive it in, if it is not already going in.</summary>
        Activate,

        /// <summary>Drive it whichever way it is not currently at.</summary>
        Press,
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

    /// <inheritdoc/>
    public override EntityCapability ObjectCaps
        => !IsDisabled && (HasSpawnFlags(SpawnFlag.UseActivates) || !HasSpawnFlags(SpawnFlag.AnyActivation))
            ? EntityCapability.ImpulseUse | EntityCapability.UseInRadius
            : EntityCapability.None;

    /// <summary>
    /// Gets or sets the entity a press is credited to, Source's <c>m_hActivator</c>. That is an
    /// <c>EHANDLE</c> there, so one that has since been removed reads back as nobody rather than as a
    /// dead entity.
    /// </summary>
    private BaseEntity? Activator
    {
        get => field is { IsRemoved: false } ? field : null;
        set;
    }

    private Vector3 moveDirection;
    private Vector3 positionOut;
    private Vector3 positionIn;
    private Vector3 finalDestination;
    private bool staysPushed;
    private bool isLinearMoving;
    private MoveDoneFunction moveDoneFunction;

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

        IsSolid = !HasSpawnFlags(SpawnFlag.NotSolid);

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

        // Before the activator is recorded, as in the engine, so a refused press does not overwrite who
        // last really pressed it
        if (IsLocked)
        {
            EntitySystem.TriggerOutput(this, "OnUseLocked", activator);
            return;
        }

        Activator = activator;

        if (State == ButtonState.AtTop)
        {
            // Only a toggle button comes back out on a second press. Unlike the touch path, staying
            // pushed does not stop it: buttons.cpp:557 tests the toggle flag alone
            if (HasSpawnFlags(SpawnFlag.Toggle))
            {
                EntitySystem.TriggerOutput(this, "OnPressed", Activator);
                ButtonReturn();
            }

            return;
        }

        EntitySystem.TriggerOutput(this, "OnPressed", Activator);
        ButtonActivate();
    }

    /// <summary>
    /// Presses the button on a touch, Source's <c>ButtonTouch</c>. Only players press buttons by walking
    /// into them, which is the same restriction the engine's touch function opens with.
    /// </summary>
    /// <param name="other">What entered the button's volume.</param>
    protected override void OnStartTouch(BaseEntity other)
    {
        if (IsDisabled || !HasSpawnFlags(SpawnFlag.TouchActivates) || other is not PlayerEntity)
        {
            return;
        }

        // Recorded before the response is worked out, and before the lock check, as the engine does
        Activator = other;

        var code = ButtonResponseToTouch();

        if (code == ButtonCode.Nothing || IsLocked)
        {
            return;
        }

        EntitySystem.TriggerOutput(this, "OnPressed", Activator);

        if (code == ButtonCode.Return)
        {
            ButtonReturn();
        }
        else
        {
            ButtonActivate();
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
    /// Switches the button off: not drawn, not solid, and invisible to the player's reach.
    /// </summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Disable")]
    private void InputDisable(EntityInputData data)
    {
        IsDisabled = true;
        IsSolid = false;
        IsDrawn = false;
    }

    /// <summary>Switches the button back on.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Enable")]
    private void InputEnable(EntityInputData data)
    {
        IsDisabled = false;
        IsSolid = !HasSpawnFlags(SpawnFlag.NotSolid);
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

    /// <summary>Presses the button, driving it whichever way it is not already at.</summary>
    /// <param name="data">Carries the entity that fired the output, which the press is credited to.</param>
    [EntityInput("Press")]
    private void InputPress(EntityInputData data) => Press(data.Activator, ButtonCode.Press);

    /// <summary>Drives the button in.</summary>
    /// <param name="data">Carries the entity that fired the output, which the press is credited to.</param>
    [EntityInput("PressIn")]
    private void InputPressIn(EntityInputData data) => Press(data.Activator, ButtonCode.Activate);

    /// <summary>Drives the button back out.</summary>
    /// <param name="data">Carries the entity that fired the output, which the press is credited to.</param>
    [EntityInput("PressOut")]
    private void InputPressOut(EntityInputData data) => Press(data.Activator, ButtonCode.Return);

    /// <summary>
    /// The <c>Press</c> family of inputs, Source's <c>CBaseButton::Press</c>. Each code refuses the states
    /// it would be a no-op in, and a locked button refuses outright without reporting anything - only the
    /// player's press produces <c>OnUseLocked</c>.
    /// </summary>
    /// <remarks>
    /// The activator is passed to <c>OnPressed</c> but not recorded, exactly as the engine leaves
    /// <c>m_hActivator</c> alone here: a button driven in by an input still credits <c>OnIn</c> and
    /// <c>OnOut</c> to whoever last pressed it by hand.
    /// </remarks>
    /// <param name="activator">The entity the press is credited to.</param>
    /// <param name="code">Which way the press drives the button.</param>
    private void Press(BaseEntity? activator, ButtonCode code)
    {
        if (IsDisabled)
        {
            return;
        }

        var refused = code switch
        {
            ButtonCode.Press => State is ButtonState.GoingUp or ButtonState.GoingDown,
            ButtonCode.Activate => State is ButtonState.GoingUp or ButtonState.AtTop,
            ButtonCode.Return => State is ButtonState.GoingDown or ButtonState.AtBottom,
            _ => true,
        };

        if (refused || IsLocked)
        {
            return;
        }

        var returning = (code == ButtonCode.Press && State == ButtonState.AtTop)
            || (code == ButtonCode.Return && State is ButtonState.AtTop or ButtonState.GoingUp);

        var activating = !returning
            && (code == ButtonCode.Press
                || (code == ButtonCode.Activate && State is ButtonState.AtBottom or ButtonState.GoingDown));

        if (!returning && !activating)
        {
            return;
        }

        EntitySystem.TriggerOutput(this, "OnPressed", activator);

        if (returning)
        {
            ButtonReturn();
        }
        else
        {
            ButtonActivate();
        }
    }

    /// <summary>
    /// How the button answers being touched, Source's <c>ButtonResponseToTouch</c>. A button that is in
    /// and on its way back out on its own ignores touches, so walking into it cannot cut the wait short.
    /// </summary>
    /// <returns>Which way the touch drives the button, if at all.</returns>
    private ButtonCode ButtonResponseToTouch()
    {
        if (State is ButtonState.GoingUp or ButtonState.GoingDown
            || (State == ButtonState.AtTop && !staysPushed && !HasSpawnFlags(SpawnFlag.Toggle)))
        {
            return ButtonCode.Nothing;
        }

        if (State != ButtonState.AtTop)
        {
            return ButtonCode.Activate;
        }

        // A button held in by wait -1 is not toggled back out by a touch, only by a use
        return HasSpawnFlags(SpawnFlag.Toggle) && !staysPushed ? ButtonCode.Return : ButtonCode.Nothing;
    }

    /// <summary>Starts the button moving in, unless it is locked.</summary>
    private void ButtonActivate()
    {
        // Re-checked because the engine checks here too: this is reached from paths that have already
        // tested it and, through the move-done chain, from ones that have not
        if (IsLocked)
        {
            return;
        }

        State = ButtonState.GoingUp;
        moveDoneFunction = MoveDoneFunction.TriggerAndWait;

        LinearMove(positionIn);
    }

    /// <summary>The button has arrived in: fire, then either stay or schedule the return.</summary>
    /// <remarks>
    /// A button locked while it was travelling gives up here, leaving it stuck part way in with nothing
    /// scheduled, which is what <c>buttons.cpp</c> does with the same test. It reads like an oversight
    /// there, but a map that locks a button mid-press is relying on whatever the engine did with it.
    /// </remarks>
    private void TriggerAndWait()
    {
        if (IsLocked)
        {
            return;
        }

        State = ButtonState.AtTop;

        // A button that does not stay in schedules its own way back out. The output goes last either way,
        // as it does in the engine
        if (!staysPushed && !HasSpawnFlags(SpawnFlag.Toggle))
        {
            moveDoneFunction = MoveDoneFunction.ButtonReturn;
            SetMoveDoneTime(Wait);
        }

        EntitySystem.TriggerOutput(this, "OnIn", Activator);
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

        EntitySystem.TriggerOutput(this, "OnOut", Activator);
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
    /// Reads the direction the button travels.
    /// </summary>
    private void ResolveMoveDirection()
    {
        var localDirection = EntityTransformHelper.EulerAnglesToForwardDirection(KeyValues.GetVector3Property("movedir"));

        moveDirection = Vector3.TransformNormal(localDirection, EntityTransformHelper.EulerAnglesToRotationMatrix(Angles));
    }

    /// <summary>
    /// How far the button slides: its own length along the travel axis, less the lip that keeps it proud.
    /// </summary>
    private float GetTravelDistance()
    {
        if (Collider is not { IsEmpty: false } collider)
        {
            return 0f;
        }

        var size = collider.LocalBounds.Size;

        return MathF.Abs(moveDirection.X * size.X)
            + MathF.Abs(moveDirection.Y * size.Y)
            + MathF.Abs(moveDirection.Z * size.Z)
            - Lip;
    }
}
