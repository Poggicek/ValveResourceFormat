using ValveResourceFormat.Renderer.Entities;

namespace ValveResourceFormat.Renderer.Movement;

/// <summary>How the player moves.</summary>
/// <seealso href="https://s2v.app/SchemaExplorer/cs2/server/MoveType_t">MoveType_t</seealso>
public enum MoveType : byte
{
    /// <summary>Does not move.</summary>
    None = 0,

    /// <summary>Walks and falls.</summary>
    Walk = 2,

    /// <summary>Flies through everything, which a player's controller turns on with <c>noclip</c>.</summary>
    NoClip = 7,

    /// <summary>Climbs a ladder.</summary>
    Ladder = 9,
}

/// <summary>Player state flags.</summary>
[Flags]
internal enum PlayerFlags : uint
{
    None = 0,
    OnGround = 1 << 0,
    Ducking = 1 << 1,
    WaterJump = 1 << 2,
    Frozen = 1 << 5,
    AtControls = 1 << 6,
    BaseVelocity = 1 << 23,
}

/// <summary>Side effects of movement that are not movement: sounds and the landing effects.</summary>
internal interface IMovementEffects
{
    /// <summary>A footstep on a surface, or a jump or landing that sounds like one.</summary>
    void PlayStepSound(Vector3 origin, uint surfacePropertyHash, float volume, bool onLadder);

    /// <summary>The jump grunt.</summary>
    void PlayJumpSound(Vector3 origin);

    /// <summary>The player landed after falling this fast.</summary>
    void OnLand(Vector3 origin, float fallVelocity);

    /// <summary>A fall fast enough to hurt.</summary>
    void OnFallDamage(Vector3 origin, float damage);

    /// <summary>Entering, leaving or wading through water.</summary>
    void PlayWaterSound(Vector3 origin, string soundEvent);
}

/// <summary>
/// The player's movement, run one user command per tick, with the input changes inside the tick
/// simulated at the moments they happened.
/// </summary>
/// <remarks>
/// Each tick runs <see cref="RunCommand"/>: the command's sub-tick steps split the tick into slices, each
/// simulated with the input held during it, and the buttons the command reports are taken in at the end.
/// The arithmetic follows the game's float operations in order, so the trajectories match it.
/// </remarks>
/// <seealso href="https://s2v.app/SchemaExplorer/cs2/server/CCSPlayer_MovementServices">CCSPlayer_MovementServices</seealso>
internal sealed partial class MovementServices
{
    public const float TickInterval = TickFraction.TickInterval;

    // Player hulls around the origin at the feet
    public static readonly Vector3 HullMin = new(-16f, -16f, 0f);
    public static readonly Vector3 HullMax = new(16f, 16f, 72f);
    public static readonly Vector3 DuckHullMin = new(-16f, -16f, 0f);
    public static readonly Vector3 DuckHullMax = new(16f, 16f, 54f);
    public const float ViewHeight = 64f;

    private const float NonJumpVelocity = 140f;
    private const float WalkSpeedModifier = 0.51999998f;
    private const float DuckSpeedModifier = 0.34f;
    private const float DuckSpeedIdeal = 8f;
    private const float MaxSpeedCap = 260f;
    private const float PlayerFallPunchThreshold = 350f;
    private const float PlayerMaxSafeFallSpeed = 580f;
    private const float PlayerMinBounceSpeed = 200f;
    private const int JumpPressTickNone = -1024;

    public MovementVars Vars { get; }
    public MovementCollision Collision { get; }
    public IMovementEffects? Effects { get; set; }

    /// <summary>Gets or sets the surface properties friction, speed and jump factors come from.</summary>
    public SurfacePropertyTable Surfaces { get; set; } = SurfacePropertyTable.Empty;

    /// <summary>Gets or sets the held weapon's max speed, or null when holding nothing.</summary>
    public float? WeaponMaxSpeed { get; set; }

    public Vector3 AbsOrigin;
    public Vector3 AbsVelocity;
    public Vector3 BaseVelocity;
    public PlayerFlags Flags;
    public MoveType MoveType = MoveType.Walk;

    /// <summary>Whether the player's controller has noclip on, which overrides the move type.</summary>
    public bool NoClip;

    /// <summary>The entity stood on, while <see cref="PlayerFlags.OnGround"/> is set. Null for the world without an entity world.</summary>
    public BaseEntity? GroundEntity;

    /// <summary>The view angles of the last command, as (pitch, yaw, roll) degrees.</summary>
    public Vector3 ViewAngles;

    public float WaterLevelFraction;
    public float WaterJumpTime;
    public Vector3 WaterJumpVelocity;
    public float SwimSoundTime;

    public float VelocityModifier = 1f;
    public bool IsWalking;
    public float IgnoreLadderJumpTime;
    public float GravityScale = 1f;
    public float EntityFriction = 1f;

    /// <summary>The landing view punch, as (pitch, yaw, roll) degrees at <see cref="ViewPunchTime"/>.</summary>
    public Vector3 ViewPunchAngle;
    public float ViewPunchTime;

    public bool OnGround => (Flags & PlayerFlags.OnGround) != 0;

    /// <summary>Gets the move type movement runs with: noclip while the controller has it on, else <see cref="MoveType"/>.</summary>
    public MoveType ActualMoveType => NoClip ? MoveType.NoClip : MoveType;

    public ButtonState Buttons;
    private ulong QueuedButtonDownMask;
    private ulong QueuedButtonChangeMask;
    private int LastCommandNumberProcessed;
    private float CmdForwardMove;
    private float CmdLeftMove;
    private float CmdUpMove;

    /// <summary>The max speed the player had at the end of the last command.</summary>
    public float MaxSpeed = MaxSpeedCap;

    private readonly float[] ForceSubtickMoveWhen = new float[4];
    public float ForwardMove;
    public float LeftMove;
    public float UpMove;
    public Vector3 LastMovementImpulses;
    private ulong ForceReleasedButtons;
    private ulong PendingForceReleasedButtons;

    public int TickBase;

    private float StepSoundTime;
    public float FallVelocity;
    private float SurfaceFriction = 1f;
    private uint SurfacePropertyHash;
    private uint LastGroundSurfacePropertyHash;

    /// <summary>Gets the surface last found under the player.</summary>
    public uint LastGroundSurface => LastGroundSurfacePropertyHash;

    public Vector3 LadderNormal;
    public bool Ducked;
    public float DuckAmount;
    private float DuckSpeed = DuckSpeedIdeal;
    private bool DesiresDuck;
    public bool Ducking;
    private float DuckRootOffset;
    public float DuckViewOffset;
    private float LastDuckTime;
    private Vector2 LastPositionAtFullCrouchSpeed;
    private bool DuckUntilOnGround;
    private bool HasWalkMovedSinceLastJump;
    private bool SpeedCropped;
    private int OldWaterLevel;
    private Vector3 Forward;
    private Vector3 Left;
    private Vector3 Up;
    private float StashGrenadeParameterWhen;
    private bool UseFrictionStashedSpeed;
    private float UseFrictionStashedSpeedUntilFrac;
    private float FrictionStashedSpeed;
    public float Stamina;
    private float AccumulatedJumpError;
    private bool OldJumpPressed;
    private float JumpPressedTime;
    private int LastJumpTick = JumpPressTickNone;
    private float LastJumpFrac;
    private float LastJumpVelocityZ;
    private bool JumpApexPending;
    private Vector2 WalkWishVel;
    private int StuckLast;

    // Modern jump
    private int LastActualJumpPressTick = JumpPressTickNone;
    private float LastActualJumpPressFrac;
    private int LastUsableJumpPressTick = JumpPressTickNone;
    private float LastUsableJumpPressFrac;
    private int LastLandedTick;
    private float LastLandedFrac;
    private float LastLandedVelocityX;
    private float LastLandedVelocityY;
    private float LastLandedVelocityZ;

    /// <summary>Whether a jump left the ground during the last command.</summary>
    public bool Jumped;

    // The time of the slice being simulated
    private float CurTime;
    private float FrameTime;
    private int TickCount;

    private readonly MoveData Move = new();

    public MovementServices(MovementVars vars, MovementCollision collision)
    {
        Vars = vars;
        Collision = collision;
    }

    public Vector3 PlayerMins => Ducked ? DuckHullMin : HullMin;
    public Vector3 PlayerMaxs => Ducked ? DuckHullMax : HullMax;

    /// <summary>The water level as whole steps: 0 out of the water, 3 or more with the eyes under.</summary>
    public int WaterLevel => WaterLevelFraction != 0f ? (byte)(int)((WaterLevelFraction * 4f) + 1f) : 0;

    /// <summary>Puts the player somewhere, standing, at rest and with no input history.</summary>
    public void Reset(Vector3 origin, Vector3 viewAngles)
    {
        AbsOrigin = origin;
        AbsVelocity = Vector3.Zero;
        BaseVelocity = Vector3.Zero;
        Flags = PlayerFlags.None;
        GroundEntity = null;
        MoveType = MoveType.Walk;
        ViewAngles = viewAngles;
        Buttons = default;
        QueuedButtonChangeMask = 0;
        QueuedButtonDownMask = 0;
        CmdForwardMove = CmdLeftMove = CmdUpMove = 0f;
        ForwardMove = LeftMove = UpMove = 0f;
        LastMovementImpulses = Vector3.Zero;
        Ducked = false;
        Ducking = false;
        DuckAmount = 0f;
        DuckViewOffset = 0f;
        DuckRootOffset = 0f;
        FallVelocity = 0f;
        Stamina = 0f;
        VelocityModifier = 1f;
        JumpApexPending = false;
        WaterJumpTime = 0f;
        ViewPunchAngle = Vector3.Zero;
        Array.Clear(ForceSubtickMoveWhen);
    }

    /// <summary>Runs one user command: one tick of movement.</summary>
    public void RunCommand(UserCmd cmd)
    {
        Recorder?.BeginTick(this, cmd, CaptureState(AbsOrigin, AbsVelocity));

        TickBase++;

        TickCount = TickBase;
        FrameTime = TickInterval;
        CurTime = TickBase * TickInterval;

        Jumped = false;

        DoMovement(cmd);

        Recorder?.EndTick(CaptureState(AbsOrigin, AbsVelocity));
    }

    private void DoMovement(UserCmd cmd)
    {
        TickCount = (int)((CurTime * 64f) + 0.5f);
        FrameTime = TickInterval;

        // Sub-tick button edges apply while the command runs; the command's own buttons replace them after
        var savedButtons = Buttons;
        var savedQueuedDown = QueuedButtonDownMask;
        var savedQueuedChange = QueuedButtonChangeMask;
        var savedForward = CmdForwardMove;
        var savedLeft = CmdLeftMove;
        var savedUp = CmdUpMove;

        ApplyGroundVelocity(TickInterval);

        var mv = Move;
        InitMoveData(mv, TickCount, zeroFrametime: false);
        SetupMove(cmd, mv);
        ForceReleasedButtons = 0;
        DoMovementSubticks(mv);
        FinishMove(mv);

        PendingForceReleasedButtons = ForceReleasedButtons;
        Buttons = savedButtons;
        QueuedButtonDownMask = savedQueuedDown;
        QueuedButtonChangeMask = savedQueuedChange;
        CmdForwardMove = savedForward;
        CmdLeftMove = savedLeft;
        CmdUpMove = savedUp;

        UpdateInputState(cmd);

        if ((Flags & PlayerFlags.Frozen) != 0)
        {
            ClearAllButtons();
        }

        for (var button = 1ul; button != 0; button <<= 1)
        {
            if ((button & PendingForceReleasedButtons) != 0)
            {
                ForceButtonState((InputButtons)button, false);
            }
        }

        PostCommandButtons();
    }

    private static void InitMoveData(MoveData mv, int targetTick, bool zeroFrametime)
    {
        mv.TargetTick = targetTick;
        mv.SubtickStartFraction = 0f;
        mv.SubtickEndFraction = 1f;
        mv.HasZeroFrametime = zeroFrametime;
        mv.TickCount = targetTick - (zeroFrametime ? 0 : 1);
    }

    private void SetupMove(UserCmd cmd, MoveData mv)
    {
        mv.ViewAngles = cmd.ViewAngles;
        mv.AbsOrigin = AbsOrigin;

        // The velocity as networked, so a client predicting this command starts from the same one
        mv.Velocity = new Vector3(
            QuantizedFloat.Velocity.Quantize(AbsVelocity.X),
            QuantizedFloat.Velocity.Quantize(AbsVelocity.Y),
            QuantizedFloat.Velocity.Quantize(AbsVelocity.Z));

        mv.ForwardMove = 0f;
        mv.SideMove = 0f;
        mv.UpMove = 0f;
        mv.LastMovementImpulses = Vector3.Zero;

        if ((Flags & PlayerFlags.AtControls) == 0)
        {
            // The impulses carry over from the previous command, and only while their keys are held
            if (Buttons.IsHeld(InputButtons.Forward | InputButtons.Back))
            {
                var forward = SanitizeMoveInput(CmdForwardMove);
                mv.ForwardMove = forward;
                mv.LastMovementImpulses.X = forward;
            }

            if (Buttons.IsHeld(InputButtons.MoveLeft | InputButtons.MoveRight))
            {
                var left = SanitizeMoveInput(CmdLeftMove);
                mv.SideMove = left;
                mv.LastMovementImpulses.Y = left;
            }

            var up = SanitizeMoveInput(CmdUpMove);
            mv.UpMove = up;
            mv.LastMovementImpulses.Z = up;
        }

        var clearInputs = false;

        if ((Flags & PlayerFlags.Frozen) != 0)
        {
            ClearAllButtons();
            mv.ForwardMove = 0f;
            mv.SideMove = 0f;
            mv.UpMove = 0f;
            mv.LastMovementImpulses = Vector3.Zero;
            clearInputs = true;
        }

        BuildSubtickMoves(cmd, mv, clearInputs);

        // While airborne after a jump, the exact vertical velocity follows from the jump itself, if the
        // networked one still agrees with it
        if (!OnGround)
        {
            var since = new TickFraction(mv.TickCount, 0f) - new TickFraction(LastJumpTick, LastJumpFrac);
            var predicted = Clamp(LastJumpVelocityZ - ((Vars.Gravity * GravityScale) * since.ToSeconds()), -16384f, 16384f);

            if (mv.Velocity.Z == QuantizedFloat.Velocity.Quantize(predicted))
            {
                mv.Velocity.Z = predicted;
            }
        }

        mv.Angles = mv.ViewAngles;
        mv.UseFrictionStashedSpeed = false;
        mv.GameCodeMovedPlayer = false;
        mv.ClientMaxSpeed = MaxSpeed;
    }

    // Rejects input a client could not have produced
    private static float SanitizeMoveInput(float value)
        => !float.IsFinite(value) || MathF.Abs(value) >= 10f ? 0f : value;

    private void BuildSubtickMoves(UserCmd cmd, MoveData mv, bool clearInputs)
    {
        mv.SubtickMoves.Clear();

        if (!clearInputs && ValidateSubtickMoves(cmd))
        {
            var pitch = cmd.ViewAngles.X;
            var yaw = cmd.ViewAngles.Y;

            foreach (var step in cmd.SubtickMoves)
            {
                var when = TickFraction.RoundSubtickFraction(step.When);
                var button = step.Button;
                var pressed = false;
                var analogForward = 0f;
                var analogLeft = 0f;

                if (button != InputButtons.None)
                {
                    pressed = step.Pressed;

                    // Attack edges drive the weapon, not movement
                    if ((button & (InputButtons.Attack | InputButtons.Attack2)) != 0)
                    {
                        button = InputButtons.None;
                        pressed = false;
                    }
                }
                else
                {
                    analogForward = step.AnalogForwardDelta;
                    analogLeft = step.AnalogLeftDelta;
                }

                pitch += step.PitchDelta;
                yaw += step.YawDelta;

                mv.SubtickMoves.Add(new SubtickMove(when, button, pressed, analogForward, analogLeft, pitch, yaw));
            }
        }

        // The last step runs to the end of the tick with the command's own view angles
        mv.SubtickMoves.Add(new SubtickMove(1f, InputButtons.None, false, 0f, 0f, cmd.ViewAngles.X, cmd.ViewAngles.Y));

        // Steps that change nothing are dropped
        for (var i = mv.SubtickMoves.Count - 2; i >= 0; i--)
        {
            var current = mv.SubtickMoves[i];
            var next = mv.SubtickMoves[i + 1];

            if (current.Button == InputButtons.None && current.AnalogForwardDelta == 0f && current.AnalogLeftDelta == 0f
                && current.Pitch == next.Pitch && current.Yaw == next.Yaw)
            {
                mv.SubtickMoves.RemoveAt(i);
            }
        }

        AddSubtickSplits(mv);

        // Splits game code asked for ahead of time
        var length = (mv.TargetTick - mv.TickCount) * TickInterval;

        if (length != 0f)
        {
            var start = mv.TickCount * TickInterval;
            var end = mv.TargetTick * TickInterval;

            foreach (var when in ForceSubtickMoveWhen)
            {
                if (when >= start && end >= when)
                {
                    var fraction = (when - start) / ((mv.TargetTick - mv.TickCount) * TickInterval);

                    if (fraction > 0.001f && fraction < 0.99900001f)
                    {
                        mv.InsertSubtickSplit(fraction);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Sub-tick input is thrown out whole when it could not have come from a real client: too many steps,
    /// out of order, an unexpected button, or movement input past what two keys produce.
    /// </summary>
    private bool ValidateSubtickMoves(UserCmd cmd)
    {
        if (cmd.SubtickMoves.Count > 32)
        {
            return false;
        }

        const InputButtons AllowedButtons = InputButtons.Attack | InputButtons.Jump | InputButtons.Duck | InputButtons.Forward
            | InputButtons.Back | InputButtons.MoveLeft | InputButtons.MoveRight | InputButtons.Attack2;

        var previousWhen = 0f;
        var forward = LastMovementImpulses.X;
        var left = LastMovementImpulses.Y;

        foreach (var step in cmd.SubtickMoves)
        {
            if (previousWhen > step.When)
            {
                return false;
            }

            previousWhen = step.When;

            if (step.When > 1f)
            {
                return false;
            }

            var button = (ulong)step.Button;

            if (button != 0)
            {
                if (((button - 1) & button) != 0 || (step.Button & AllowedButtons) == 0)
                {
                    return false;
                }

                AccumulateMoveButtonImpulse(ref forward, ref left, step.Button, step.Pressed);
            }
            else
            {
                forward += step.AnalogForwardDelta;
                left += step.AnalogLeftDelta;
            }

            if (MathF.Abs(forward) > 2f || MathF.Abs(left) > 2f)
            {
                return false;
            }
        }

        return true;
    }

    private static void AccumulateMoveButtonImpulse(ref float forward, ref float left, InputButtons button, bool pressed)
    {
        var delta = -1f;

        switch (button)
        {
            case InputButtons.Forward:
                delta = 1f;
                break;
            case InputButtons.Back:
                break;
            case InputButtons.MoveLeft:
                left += pressed ? 1f : -1f;
                return;
            case InputButtons.MoveRight:
                left += pressed ? -1f : 1f;
                return;
            default:
                return;
        }

        forward += pressed ? delta : -delta;
    }

    private void DoMovementSubticks(MoveData mv)
    {
        var subtickAngles = Vars.SubtickMovementViewAngles;
        var savedViewAngles = mv.ViewAngles;

        var forward = mv.LastMovementImpulses.X;
        var left = mv.LastMovementImpulses.Y;
        var up = mv.LastMovementImpulses.Z;

        var previousWhen = 0f;
        var processedThisStep = 0ul;

        for (var i = 0; i < mv.SubtickMoves.Count; i++)
        {
            var move = mv.SubtickMoves[i];
            mv.SetSubtickFraction(previousWhen, move.When);

            if (previousWhen != move.When)
            {
                // The slice up to this input change runs with the input held before it
                mv.LastMovementImpulses = new Vector3(forward, left, up);
                mv.ForwardMove = forward;
                mv.SideMove = left;
                mv.UpMove = up;
                processedThisStep = 0;

                if (subtickAngles)
                {
                    mv.ViewAngles.X = move.Pitch;
                    mv.ViewAngles.Y = move.Yaw;
                }

                var tick = mv.TickCount;
                var stepFrametime = (mv.SubtickEndFraction - mv.SubtickStartFraction) * ((mv.TargetTick - tick) * 0.015625f);
                var fraction = mv.HasZeroFrametime ? 0f : move.When;

                if (fraction >= 0f)
                {
                    if (fraction >= 1f)
                    {
                        tick++;
                        fraction += -1f;
                    }
                }
                else
                {
                    tick--;
                    fraction += 1f;
                }

                // The slice is timed at its end
                var savedTime = (CurTime, FrameTime, TickCount);
                TickCount = tick;
                FrameTime = stepFrametime;
                CurTime = (tick * 0.015625f) + (fraction * 0.015625f);

                previousWhen = move.When;

                Recorder?.BeginSlice(new SliceRecord(mv.SubtickStartFraction, mv.SubtickEndFraction, FrameTime, CurTime, mv.ForwardMove, mv.SideMove,
                    mv.ViewAngles, Buttons.Held, CaptureState(mv.AbsOrigin, mv.Velocity)));

                StartSubtickMove(mv);
                ProcessMovement(mv);
                FinishSubtickMove(mv);

                Recorder?.EndSlice(CaptureState(mv.AbsOrigin, mv.Velocity));

                (CurTime, FrameTime, TickCount) = savedTime;
            }

            if (move.Button == InputButtons.None)
            {
                forward += move.AnalogForwardDelta;
                left += move.AnalogLeftDelta;
                continue;
            }

            ProcessSubtickButton(move.Button, move.Pressed, ref processedThisStep);

            switch (move.Button)
            {
                case InputButtons.Forward:
                    forward += move.Pressed ? 1f : -1f;
                    break;
                case InputButtons.Back:
                    forward += move.Pressed ? -1f : 1f;
                    break;
                case InputButtons.MoveLeft:
                    left += move.Pressed ? 1f : -1f;
                    break;
                case InputButtons.MoveRight:
                    left += move.Pressed ? -1f : 1f;
                    break;
                default:
                    break;
            }
        }

        mv.SetSubtickFraction(0f, 1f);

        if (subtickAngles)
        {
            mv.ViewAngles = savedViewAngles;
        }
    }

    private void ProcessSubtickButton(InputButtons button, bool pressed, ref ulong processedThisStep)
    {
        // A second edge in the same step is kept as history, the first replaces the state outright
        if ((processedThisStep & (ulong)button) != 0)
        {
            Buttons.AddButtonEdge(button, pressed);
            ForceReleasedButtons &= ~(ulong)button;
            return;
        }

        processedThisStep |= (ulong)button;
        ForceButtonState(button, pressed);
    }

    private void ForceButtonState(InputButtons button, bool down)
    {
        Buttons.ForceButtonState(button, down);

        if (!down)
        {
            QueuedButtonChangeMask |= (ulong)button;
            QueuedButtonDownMask &= ~(ulong)button;
        }

        ForceReleasedButtons &= ~(ulong)button;
    }

    private void ClearAllButtons()
    {
        Buttons.ForceButtons(0);
        QueuedButtonChangeMask = ulong.MaxValue;
        QueuedButtonDownMask = 0;
    }

    private bool WasButtonPressed(InputButtons button) => Buttons.WasPressed(button);

    private void UpdateInputState(UserCmd cmd)
    {
        Buttons.UpdateFromCommand(cmd.Buttons, 0, 0);
        Buttons.ForceButtonsMasked(QueuedButtonChangeMask, QueuedButtonDownMask);

        // A forced release lasts while the command still holds the button
        QueuedButtonChangeMask &= ~QueuedButtonDownMask;
        QueuedButtonChangeMask &= cmd.Buttons.Held | ~QueuedButtonChangeMask;
        QueuedButtonDownMask = 0;

        LastCommandNumberProcessed = cmd.CommandNumber;
        CmdForwardMove = cmd.ForwardMove;
        CmdLeftMove = cmd.LeftMove;
        CmdUpMove = cmd.UpMove;
    }

    /// <summary>A jump press is used up by the command it happened in, so one press jumps at most once.</summary>
    private void PostCommandButtons()
    {
        if (WasButtonPressed(InputButtons.Jump))
        {
            ForceButtonState(InputButtons.Jump, Buttons.IsHeld(InputButtons.Jump));
        }
    }

    private void FinishMove(MoveData mv)
    {
        ForwardMove = mv.ForwardMove;
        LeftMove = mv.SideMove;
        UpMove = mv.UpMove;
        LastMovementImpulses = mv.LastMovementImpulses;

        SetAbsOrigin(mv.AbsOrigin);
        AbsVelocity = mv.Velocity;
        ViewAngles = mv.ViewAngles;

        if (Collision.Entities is { } entities)
        {
            foreach (var entity in mv.TouchList)
            {
                if (entity != entities.World)
                {
                    entities.NotePlayerImpact(entity);
                }
            }
        }

        mv.TouchList.Clear();

        FallVelocity = -mv.Velocity.Z;
        MaxSpeed = mv.ClientMaxSpeed;
        UseFrictionStashedSpeed = mv.UseFrictionStashedSpeed;
    }

    private void StartSubtickMove(MoveData mv)
    {
        mv.WalkWishVel = Vector2.Zero;
        mv.ContinuousAcceleration = Vector3.Zero;
        mv.FrameVelocityDelta = Vector3.Zero;
        mv.FrictionDecel = 0f;
        mv.PreAirMovePosZ = 0f;
        mv.PreAirMoveVelZ = 0f;
        mv.PreAirMoveAccelZ = 0f;

        // Analog input is snapped to what digital input could produce
        if (Vars.QuantizeMovementInput)
        {
            mv.ForwardMove = MathF.Round(mv.ForwardMove, MidpointRounding.AwayFromZero);
            mv.SideMove = MathF.Round(mv.SideMove, MidpointRounding.AwayFromZero);
            mv.UpMove = MathF.Round(mv.UpMove, MidpointRounding.AwayFromZero);
        }
    }

    private void FinishSubtickMove(MoveData mv)
    {
        // A jump press is used up by the step it happened in
        if (WasButtonPressed(InputButtons.Jump))
        {
            ForceButtonState(InputButtons.Jump, Buttons.IsHeld(InputButtons.Jump));
        }

        SetAbsOrigin(mv.AbsOrigin);
        AbsVelocity = mv.Velocity;

        UpdateDuckRootOffset(FrameTime);

        if (0f < StashGrenadeParameterWhen)
        {
            var now = new TickFraction(mv.TickCount, mv.SubtickEndFraction);
            var stash = TickFraction.FromSeconds(StashGrenadeParameterWhen);

            if (TickFraction.Compare(now, stash) is 0 or 1)
            {
                StashGrenadeParameterWhen = 0f;
            }
        }

        WalkWishVel = mv.WalkWishVel;
    }

    /// <summary>
    /// Moves the player the way the server's scene node does: an origin that is not finite, or within
    /// 1/256 of the current one on every axis, is ignored, so a move that small is lost.
    /// </summary>
    private void SetAbsOrigin(Vector3 origin)
    {
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z))
        {
            return;
        }

        if (IsSameOriginAxis(AbsOrigin.X, origin.X) && IsSameOriginAxis(AbsOrigin.Y, origin.Y) && IsSameOriginAxis(AbsOrigin.Z, origin.Z))
        {
            return;
        }

        AbsOrigin = origin;
    }

    private static bool IsSameOriginAxis(float current, float origin)
    {
        // The relative part only exceeds 1/256 outside the world
        var tolerance = MathF.Max(1f / 256f, MathF.Max(MathF.Abs(current), MathF.Abs(origin)) * ((1f / 256f) / NetworkOrigin.MaxCoord));
        return MathF.Abs(current - origin) <= tolerance;
    }

    private void AddSubtickSplits(MoveData mv)
    {
        // The friction stash expires at this fraction of the tick
        if (UseFrictionStashedSpeed)
        {
            mv.InsertSubtickSplit(UseFrictionStashedSpeedUntilFrac);
        }

        if (!Vars.LegacyJump)
        {
            AddModernJumpSubtickSplits(mv);
        }

        var stash = TickFraction.FromSeconds(StashGrenadeParameterWhen);

        if (stash.Tick == TickCount - 1)
        {
            mv.InsertSubtickSplit(stash.Fraction);
        }

        // Split exactly where the vertical velocity crosses zero, the top of the jump
        if (JumpApexPending && !OnGround && ActualMoveType == MoveType.Walk)
        {
            var velocityZ = mv.Velocity.Z;

            if (velocityZ > 0f && !mv.HasZeroFrametime)
            {
                var gravity = Vars.Gravity * GravityScale;
                var deltaPerTick = (BaseVelocity.Z * 0.015625f) - (((gravity * 0.015625f) * 0.5f) + ((gravity * 0.015625f) * 0.5f));

                if ((deltaPerTick + velocityZ) <= 0f)
                {
                    var apex = -velocityZ / deltaPerTick;

                    if (apex != 0f && apex != 1f)
                    {
                        AddForcedSubtickMove(new TickFraction(mv.TickCount, apex), mv, force: true);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Asks for a split at <paramref name="when"/>: straight away when it falls inside the command being
    /// run, else remembered for the command it falls in.
    /// </summary>
    private void AddForcedSubtickMove(TickFraction when, MoveData mv, bool force)
    {
        if (when.Fraction == 0f)
        {
            return;
        }

        if (when.Tick < mv.TargetTick)
        {
            var stepStart = new TickFraction(mv.TickCount, mv.SubtickStartFraction);

            if (TickFraction.Compare(stepStart, when) == -1 && (force || when.Fraction > mv.SubtickEndFraction))
            {
                mv.InsertSubtickSplit(when.Fraction);
            }

            return;
        }

        var seconds = when.ToSeconds();

        for (var i = 0; i < ForceSubtickMoveWhen.Length; i++)
        {
            if (ForceSubtickMoveWhen[i] == seconds)
            {
                return;
            }

            if (CurTime > ForceSubtickMoveWhen[i])
            {
                ForceSubtickMoveWhen[i] = seconds;
                return;
            }
        }
    }

    private void ProcessMovement(MoveData mv)
    {
        SpeedCropped = false;

        var maxSpeed = 1f;

        if (ActualMoveType != MoveType.None)
        {
            var speed = Vars.MaxSpeed;

            if (MaxSpeed > 0f && speed > MaxSpeed)
            {
                speed = MaxSpeed;
            }

            maxSpeed = MathF.Min(MaxSpeedCap, speed);

            if (WeaponMaxSpeed is { } weaponSpeed)
            {
                maxSpeed = MathF.Min(weaponSpeed, maxSpeed);
            }
        }

        mv.MaxSpeed = maxSpeed;

        PlayerMove(mv);
    }

    private void PlayerMove(MoveData mv)
    {
        if (WaterJumpTime != 0f)
        {
            CheckParameters(mv);

            if (!MoveInit(mv))
            {
                return;
            }

            if (WaterJumpTime > 10000f)
            {
                WaterJumpTime = 10000f;
            }

            if (WaterJumpTime != 0f)
            {
                WaterJumpTime -= FrameTime * 1000f;

                if (WaterJumpTime <= 0f || WaterLevel == 0)
                {
                    WaterJumpTime = 0f;
                    Flags &= ~PlayerFlags.WaterJump;
                }

                mv.Velocity.X = WaterJumpVelocity.X;
                mv.ContinuousAcceleration.X = 0f;
                mv.FrameVelocityDelta.X = 0f;
                mv.Velocity.Y = WaterJumpVelocity.Y;
                mv.ContinuousAcceleration.Y = 0f;
                mv.FrameVelocityDelta.Y = 0f;
            }

            TryPlayerMove(mv, null);
            CheckWater(mv);
            PostMove(mv, retryMove: false);
            return;
        }

        if (WaterLevel >= 3 && ActualMoveType == MoveType.Walk)
        {
            CheckParameters(mv);
            FullWaterMove(mv);
            return;
        }

        switch (ActualMoveType)
        {
            case MoveType.Walk:
                CheckParameters(mv);
                FullWalkMove(mv, skipMoveInit: false);
                break;

            case MoveType.Ladder:
                CheckParameters(mv);

                if (!MoveInit(mv))
                {
                    return;
                }

                if (ActualMoveType == MoveType.Walk)
                {
                    // The ladder code just let go
                    FullWalkMove(mv, skipMoveInit: true);
                }
                else
                {
                    FullLadderMove(mv);
                }

                break;

            case MoveType.NoClip:
                CheckParameters(mv);

                if (MoveInit(mv))
                {
                    Duck(mv);
                    FullNoClipMove(mv, Vars.NoclipSpeed, Vars.NoclipAccelerate);
                    PostMove(mv, retryMove: false);
                }

                break;

            default:
                break;
        }
    }

    private void FullLadderMove(MoveData mv)
    {
        Duck(mv);
        CheckWater(mv);
        CheckJumpButton(mv);
        PrepareVelocityForMove(mv);
        TryPlayerMove(mv, null);
        FinalizeVelocity(mv);
        PostMove(mv, retryMove: false);
    }

    private void CheckJumpButton(MoveData mv)
    {
        if (Vars.LegacyJump)
        {
            CheckJumpButtonLegacy(mv);
        }
        else
        {
            CheckJumpButtonModern(mv);
        }
    }

    private void CheckParameters(MoveData mv)
    {
        var duckHeld = Buttons.IsHeld(InputButtons.Duck);

        // Toggling duck costs duck speed, so spamming it slows ducking down
        if (DesiresDuck != duckHeld)
        {
            DesiresDuck = duckHeld;
            DuckSpeed = MathF.Max(0f, DuckSpeed - 2f);
        }

        if (DuckUntilOnGround && duckHeld)
        {
            DuckUntilOnGround = false;
        }

        if (Buttons.IsHeld(InputButtons.Speed) && !Buttons.IsHeld(InputButtons.Duck) && !Ducking && (Flags & PlayerFlags.Ducking) == 0)
        {
            var walkSpeed = WalkSpeedModifier * mv.MaxSpeed;

            if ((walkSpeed + 25f) > Length(AbsVelocity))
            {
                mv.MaxSpeed = walkSpeed;
                IsWalking = true;
            }
        }
        else
        {
            IsWalking = false;
        }

        // Noclip flies at its own speed, which nothing on the ground slows
        if (ActualMoveType != MoveType.NoClip)
        {
            var speedScale = Surfaces.Get(SurfacePropertyHash).MaxSpeedFactor;

            if (OnGround)
            {
                speedScale *= VelocityModifier;
            }

            var maxSpeed = speedScale * mv.MaxSpeed;
            mv.MaxSpeed = maxSpeed;

            // Stamina slows the player down
            var stamina = Stamina;

            if (stamina > 0f)
            {
                var scale = Clamp(1f - (stamina / 100f), 0f, 1f);
                mv.MaxSpeed = (scale * scale) * maxSpeed;
            }

            // A hard landing caps the speed for a while
            if (!Vars.LegacyJump && OnGround)
            {
                var now = new TickFraction(mv.TickCount, mv.SubtickStartFraction);
                mv.MaxSpeed = GetLandingSpeedScale(now) * mv.MaxSpeed;
            }
        }

        if ((Flags & PlayerFlags.Frozen) != 0)
        {
            mv.ForwardMove = 0f;
            mv.SideMove = 0f;
            mv.UpMove = 0f;
        }

        var angles = mv.Angles + GetViewPunchAngle();
        mv.Angles = angles with { Z = ActualMoveType != MoveType.NoClip ? CalcRoll(angles, mv.Velocity, Vars.RollAngle, Vars.RollSpeed) : 0f };
        mv.Angles.Y = AngleNormalize(mv.Angles.Y);

        // Movement input is -1 to 1; scale it to speed
        var speed = mv.MaxSpeed;
        mv.ForwardMove = speed * mv.ForwardMove;
        mv.SideMove = speed * mv.SideMove;
        mv.UpMove = speed * mv.UpMove;
    }

    private static float CalcRoll(Vector3 angles, Vector3 velocity, float rollAngle, float rollSpeed)
    {
        AngleVectors(angles, out _, out var left, out _);

        var side = Dot(left, velocity);
        var sign = side > 0f ? -1f : 1f;
        side = MathF.Abs(side);

        var value = rollSpeed > side ? (side * rollAngle) / rollSpeed : rollAngle;

        return sign * value;
    }

    private bool MoveInit(MoveData mv)
    {
        mv.OutWishVel = Vector3.Zero;
        mv.ClearTouchAndNormals();

        // Stamina recovers over time
        var stamina = Stamina;

        if (stamina > 0f)
        {
            var recover = Vars.StaminaRecoveryRate * FrameTime;

            if (stamina != stamina - recover)
            {
                Stamina = stamina - recover;
                stamina -= recover;
            }

            if (stamina < 0f)
            {
                Stamina = 0f;
            }
        }

        if (SwimSoundTime > 0f)
        {
            SwimSoundTime = MathF.Max(0f, SwimSoundTime - (FrameTime * 1000f));
        }

        AngleVectors(mv.ViewAngles, out Forward, out Left, out Up);

        if (ActualMoveType is not (MoveType.None or MoveType.NoClip) && CheckStuck(mv))
        {
            Recorder?.Note("stuck, move skipped", mv.AbsOrigin, mv.Velocity);
            return false;
        }

        if (ActualMoveType == MoveType.Walk && !mv.GameCodeMovedPlayer && Vars.OptimizedMovement)
        {
            // The ground state carries over from the last move; only launching off it is checked
            if (mv.Velocity.Z > 250f)
            {
                SetGroundEntity(mv, null);
            }
        }
        else
        {
            CategorizePosition(mv, stayOnGround: false);
        }

        OldWaterLevel = WaterLevel;

        if (!OnGround)
        {
            if (ActualMoveType == MoveType.Walk)
            {
                FallVelocity = FallVelocity > 0f ? MathF.Max(FallVelocity, -mv.Velocity.Z) : -mv.Velocity.Z;
            }
            else
            {
                FallVelocity = 0f;
            }
        }

        UpdateStepSound(LastGroundSurfacePropertyHash, mv.AbsOrigin, mv.Velocity);

        if (!LadderMove(mv) && ActualMoveType == MoveType.Ladder)
        {
            // Let go of the ladder; it is grabbed again next step if still pressed into
            MoveType = MoveType.Walk;
        }

        return true;
    }

    private void FullWalkMove(MoveData mv, bool skipMoveInit)
    {
        if (!skipMoveInit && !MoveInit(mv))
        {
            return;
        }

        Duck(mv);
        CheckJumpButton(mv);

        if (OnGround)
        {
            WalkMove(mv);
        }
        else
        {
            AirMove(mv);
        }

        PostMove(mv, retryMove: false);

        if (!IsWalking && !Ducked)
        {
            var inWater = WaterLevel != 0;

            if ((OldWaterLevel > 0) != inWater)
            {
                Effects?.PlayWaterSound(mv.AbsOrigin, inWater ? "Water.PlayerEnter" : "Water.PlayerExit");
            }
        }
    }

    private void PostMove(MoveData mv, bool retryMove)
    {
        CategorizePosition(mv, OnGround);
        CheckVelocity(mv);

        if (OnGround)
        {
            mv.SetVelocityAxis(2, 0f);
        }

        CheckFalling(mv);

        if (retryMove && !IsZero(mv.Velocity, 0.01f))
        {
            TryPlayerMove(mv, null);
        }

        // Recover from being slowed by damage
        if (OnGround && VelocityModifier < 1f)
        {
            VelocityModifier = Clamp((FrameTime * 0.4f) + VelocityModifier, 0f, 1f);
        }
    }

    /// <summary>Bounds the velocity, and clears NaNs from it and the origin.</summary>
    private void CheckVelocity(MoveData mv)
    {
        for (var i = 0; i < 3; i++)
        {
            if (!float.IsFinite(mv.Velocity[i]))
            {
                mv.Velocity[i] = 0f;
            }

            if (!float.IsFinite(mv.AbsOrigin[i]))
            {
                mv.AbsOrigin[i] = 0f;
            }

            var velocity = mv.Velocity[i];

            if (!(velocity <= Vars.MaxVelocity))
            {
                mv.Velocity[i] = Vars.MaxVelocity;
            }
            else if (!(-Vars.MaxVelocity <= velocity))
            {
                mv.Velocity[i] = -Vars.MaxVelocity;
            }
        }
    }

    /// <summary>
    /// Base velocity left from leaving a moving ground is turned into real velocity, as momentum, at the
    /// start of the next command.
    /// </summary>
    private void ApplyGroundVelocity(float frametime)
    {
        if ((Flags & PlayerFlags.BaseVelocity) == 0)
        {
            var scale = (frametime * 0.5f) + 1f;
            AbsVelocity += new Vector3(scale * BaseVelocity.X, scale * BaseVelocity.Y, scale * BaseVelocity.Z);
            BaseVelocity = Vector3.Zero;
        }

        Flags &= ~PlayerFlags.BaseVelocity;
    }

    // The game's own operation order, which differs from System.Numerics
    private static float Dot(Vector3 a, Vector3 b) => ((a.Z * b.Z) + (a.Y * b.Y)) + (a.X * b.X);

    private static float Length(Vector3 v) => MathF.Sqrt(((v.Z * v.Z) + (v.Y * v.Y)) + (v.X * v.X));

    private static float Length2D(Vector3 v) => MathF.Sqrt((v.X * v.X) + (v.Y * v.Y));

    private static float LengthSquared(Vector3 v) => ((v.Z * v.Z) + (v.Y * v.Y)) + (v.X * v.X);

    private static bool IsZero(Vector3 v, float tolerance)
        => v.X > -tolerance && v.X < tolerance && v.Y > -tolerance && v.Y < tolerance && v.Z > -tolerance && v.Z < tolerance;

    /// <summary>Normalizes in place and returns the length, falling back to double precision for extreme lengths.</summary>
    private static float VectorNormalize(ref Vector3 v)
    {
        var length = Length(v);

        if (length < 9.9999998e-18f || length > 9.9999998e16f)
        {
            if (length == 0f)
            {
                v = Vector3.Zero;
                return 0f;
            }

            double x = v.X, y = v.Y, z = v.Z;
            var precise = Math.Sqrt(((y * y) + (x * x)) + (z * z));
            var inverse = 1.0 / precise;
            v = new Vector3((float)(inverse * x), (float)(inverse * y), (float)(inverse * z));
            return (float)Math.Min(precise, float.MaxValue);
        }

        var scale = 1f / length;
        v = new Vector3(v.X * scale, v.Y * scale, scale * v.Z);
        return length;
    }

    /// <summary>Forward, left and up of a QAngle. Note the second vector points left, not right.</summary>
    private static void AngleVectors(Vector3 angles, out Vector3 forward, out Vector3 left, out Vector3 up)
    {
        const float DegreesToRadians = 0.017453292f;

        var (sp, cp) = MathF.SinCos(angles.X * DegreesToRadians);
        var (sy, cy) = MathF.SinCos(angles.Y * DegreesToRadians);
        var (sr, cr) = MathF.SinCos(angles.Z * DegreesToRadians);

        forward = new Vector3(cy * cp, sy * cp, -sp);
        left = new Vector3(((sr * sp) * cy) - (cr * sy), ((sr * sp) * sy) + (cr * cy), sr * cp);
        up = new Vector3(((cr * sp) * cy) - ((-sy) * sr), ((cr * sp) * sy) - (sr * cy), cr * cp);
    }

    private static float AngleNormalize(float angle)
    {
        if (angle < -180f || angle > 180f)
        {
            return MathF.Min(MathF.Max(angle - (MathF.Floor((angle * (1f / 360f)) + 0.5f) * 360f), -180f), 180f);
        }

        return angle;
    }

    // A NaN input returns the low bound, as both compares fail
    private static float Clamp(float value, float low, float high)
    {
        if (!(low <= value))
        {
            return low;
        }

        return !(value <= high) ? high : value;
    }

    private static float Approach(float target, float value, float speed)
    {
        var delta = target - value;

        if (delta > speed)
        {
            return value + speed;
        }

        if (delta < -speed)
        {
            return value - speed;
        }

        return target;
    }
}
