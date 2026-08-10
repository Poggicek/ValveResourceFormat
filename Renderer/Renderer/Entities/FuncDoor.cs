using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>func_door</c> and <c>func_movelinear</c>. A brush that slides open along its <c>movedir</c> and back
/// again, Source's <c>CBaseDoor</c>.
/// </summary>
/// <remarks>
/// The travel is <see cref="BaseToggle"/>'s, so a door is mostly the state machine around it: which way it
/// is going, whether it comes back by itself, and whether it is locked. Not simulated: the blocking
/// behaviour that reverses a door onto whoever is standing in it, and the door groups that open together.
/// </remarks>
public class FuncDoor : BaseToggle
{
    /// <summary>What a <c>func_door</c>'s <c>spawnflags</c> mean.</summary>
    [Flags]
    public enum SpawnFlag : uint
    {
        /// <summary>Things pass straight through it.</summary>
        Passable = 8,

        /// <summary>Stays open once opened, rather than coming back by itself.</summary>
        Toggle = 32,

        /// <summary>The player may open it by pressing use. Source's <c>SF_DOOR_PUSE</c>.</summary>
        UseOpens = 256,

        /// <summary>Opens when the player walks into it.</summary>
        TouchOpens = 1024,

        /// <summary>Spawns locked, and stays that way until something unlocks it.</summary>
        StartsLocked = 2048,

        /// <summary>Refuses use entirely, whatever else is set.</summary>
        IgnoreUse = 32768,
    }

    /// <summary>
    /// Gets whether the player can open this door by pressing it. Source's <c>CBaseDoor::ObjectCaps</c>:
    /// a door is only usable when the map said so, which is why most doors ignore a press and the ones a
    /// map means as levers do not.
    /// </summary>
    public override EntityCapability ObjectCaps
        => HasSpawnFlags(SpawnFlag.UseOpens) && !HasSpawnFlags(SpawnFlag.IgnoreUse)
            ? EntityCapability.ImpulseUse
            : EntityCapability.None;

    /// <summary>Gets where the door is in its travel.</summary>
    public bool IsOpen => State is ToggleState.AtTop or ToggleState.GoingUp;

    /// <summary>Gets whether the door refuses to open.</summary>
    public bool IsLocked { get; private set; }

    /// <summary>Gets the seconds the door stays open before closing; -1 means it stays open.</summary>
    public float Wait { get; private set; }

    /// <summary>Gets where the door is in its travel.</summary>
    protected ToggleState State { get; private set; }

    /// <summary>Gets the place the door rests when closed.</summary>
    protected Vector3 PositionClosed { get; set; }

    /// <summary>Gets the place the door rests when open.</summary>
    protected Vector3 PositionOpen { get; set; }

    /// <summary>
    /// Initializes a <c>func_door</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public FuncDoor(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        // A door keeps its authored orientation: only a button spends its angles on the travel direction
        ResolveMoveDirection(consumeAngles: false);

        Speed = KeyValues.GetFloatProperty("speed", 100f);

        if (Speed <= 0f)
        {
            Speed = 100f;
        }

        Wait = KeyValues.GetFloatProperty("wait", 4f);
        Lip = KeyValues.GetFloatProperty("lip");
        // Both spellings: the flag is how the compiled maps carry it, the keyvalue how the FGD offers it
        IsLocked = HasSpawnFlags(SpawnFlag.StartsLocked) || KeyValues.GetBooleanProperty("startlocked");

        PositionClosed = Origin;
        PositionOpen = PositionClosed + (MoveDirection * GetTravelDistance());

        SetUpTravel();

        // A door that spawns open is authored at its open position, so the two ends swap
        if (KeyValues.GetBooleanProperty("spawnpos"))
        {
            SwapEnds();
            State = ToggleState.AtBottom;
        }
    }

    /// <summary>
    /// Works out where the door's two ends are. A door that turns rather than slides overrides this.
    /// </summary>
    protected virtual void SetUpTravel()
    {
    }

    /// <summary>Exchanges the two ends, for a door the map authored in its open position.</summary>
    protected virtual void SwapEnds()
    {
        (PositionClosed, PositionOpen) = (PositionOpen, PositionClosed);
        Origin = PositionClosed;
    }

    /// <summary>Sets the door travelling towards one of its two ends.</summary>
    /// <param name="opening">Whether it is heading for the open end.</param>
    protected virtual void StartMove(bool opening) => LinearMove(opening ? PositionOpen : PositionClosed);

    /// <inheritdoc/>
    public override void MoveDone()
    {
        FinishLinearMove();

        if (State == ToggleState.GoingUp)
        {
            State = ToggleState.AtTop;

            EntitySystem.TriggerOutput(this, "OnFullyOpen");

            // A toggle door waits to be told; the rest close themselves after their wait
            if (!HasSpawnFlags(SpawnFlag.Toggle) && Wait >= 0f)
            {
                SetNextThink(EntitySystem.CurrentTime + Wait);
            }

            return;
        }

        if (State == ToggleState.GoingDown)
        {
            State = ToggleState.AtBottom;

            EntitySystem.TriggerOutput(this, "OnFullyClosed");
        }
    }

    /// <summary>Closes the door once it has stood open for its wait.</summary>
    public override void Think() => Close();

    /// <inheritdoc/>
    public override void Use(BaseEntity? activator) => Toggle();

    /// <summary>Opens the door.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Open")]
    protected void InputOpen(EntityInputData data) => Open();

    /// <summary>Closes the door.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Close")]
    protected void InputClose(EntityInputData data) => Close();

    /// <summary>Opens a closed door, closes an open one.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Toggle")]
    protected void InputToggle(EntityInputData data) => Toggle();

    /// <summary>Stops the door opening until it is unlocked.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Lock")]
    protected void InputLock(EntityInputData data) => IsLocked = true;

    /// <summary>Lets the door open again.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Unlock")]
    protected void InputUnlock(EntityInputData data) => IsLocked = false;

    /// <summary>Changes how fast the door travels.</summary>
    /// <param name="data">Carries the new speed in units per second.</param>
    [EntityInput("SetSpeed")]
    protected void InputSetSpeed(EntityInputData data) => Speed = MathF.Max(data.Float(Speed), 0f);

    /// <summary>Opens the door, unless it is locked or already going that way.</summary>
    public void Open()
    {
        if (IsLocked)
        {
            EntitySystem.TriggerOutput(this, "OnLockedUse");
            return;
        }

        if (State is ToggleState.AtTop or ToggleState.GoingUp)
        {
            return;
        }

        State = ToggleState.GoingUp;
        SetNextThink(-1f);

        EntitySystem.TriggerOutput(this, "OnOpen");

        StartMove(opening: true);
    }

    /// <summary>Closes the door, unless it is already going that way.</summary>
    public void Close()
    {
        if (State is ToggleState.AtBottom or ToggleState.GoingDown)
        {
            return;
        }

        State = ToggleState.GoingDown;
        SetNextThink(-1f);

        EntitySystem.TriggerOutput(this, "OnClose");

        StartMove(opening: false);
    }

    /// <summary>Opens a closed door and closes an open one, which is what a use or a toggle means.</summary>
    public void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }
}
