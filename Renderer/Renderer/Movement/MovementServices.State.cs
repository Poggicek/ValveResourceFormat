namespace ValveResourceFormat.Renderer.Movement;

/// <summary>Visits every field of the player's movement state by reference, to save or restore it.</summary>
internal interface IMovementStateVisitor
{
    void Visit(string name, ref float value);
    void Visit(string name, ref int value);
    void Visit(string name, ref bool value);
    void Visit(string name, ref ulong value);
    void Visit(string name, ref Vector2 value);
    void Visit(string name, ref Vector3 value);
}

internal sealed partial class MovementServices
{
    private static readonly string[] ForceSubtickMoveWhenNames =
        [$"{nameof(ForceSubtickMoveWhen)}0", $"{nameof(ForceSubtickMoveWhen)}1", $"{nameof(ForceSubtickMoveWhen)}2", $"{nameof(ForceSubtickMoveWhen)}3"];

    /// <summary>Gets or sets where every tick is recorded in detail, or null to record nothing.</summary>
    public MovementRecorder? Recorder { get; set; }

    /// <summary>
    /// Visits all state that carries from one command to the next, which is everything a command's
    /// outcome depends on besides the command, the settings and the world. The ground entity is left
    /// out, being a reference.
    /// </summary>
    public void VisitState(IMovementStateVisitor visitor)
    {
        visitor.Visit(nameof(AbsOrigin), ref AbsOrigin);
        visitor.Visit(nameof(AbsVelocity), ref AbsVelocity);
        visitor.Visit(nameof(BaseVelocity), ref BaseVelocity);

        var flags = (ulong)Flags;
        visitor.Visit(nameof(Flags), ref flags);
        Flags = (PlayerFlags)flags;

        var moveType = (int)MoveType;
        visitor.Visit(nameof(MoveType), ref moveType);
        MoveType = (MoveType)moveType;

        var weaponMaxSpeed = WeaponMaxSpeed ?? float.NaN;
        visitor.Visit(nameof(WeaponMaxSpeed), ref weaponMaxSpeed);
        WeaponMaxSpeed = float.IsNaN(weaponMaxSpeed) ? null : weaponMaxSpeed;

        visitor.Visit(nameof(ViewAngles), ref ViewAngles);
        visitor.Visit(nameof(WaterLevelFraction), ref WaterLevelFraction);
        visitor.Visit(nameof(WaterJumpTime), ref WaterJumpTime);
        visitor.Visit(nameof(WaterJumpVelocity), ref WaterJumpVelocity);
        visitor.Visit(nameof(SwimSoundTime), ref SwimSoundTime);
        visitor.Visit(nameof(VelocityModifier), ref VelocityModifier);
        visitor.Visit(nameof(IsWalking), ref IsWalking);
        visitor.Visit(nameof(IgnoreLadderJumpTime), ref IgnoreLadderJumpTime);
        visitor.Visit(nameof(GravityScale), ref GravityScale);
        visitor.Visit(nameof(EntityFriction), ref EntityFriction);
        visitor.Visit(nameof(ViewPunchAngle), ref ViewPunchAngle);
        visitor.Visit(nameof(ViewPunchTime), ref ViewPunchTime);

        var held = Buttons.Held;
        var changed = Buttons.Changed;
        var changedTwice = Buttons.ChangedTwice;
        visitor.Visit("ButtonsHeld", ref held);
        visitor.Visit("ButtonsChanged", ref changed);
        visitor.Visit("ButtonsChangedTwice", ref changedTwice);
        Buttons = new ButtonState { Held = held, Changed = changed, ChangedTwice = changedTwice };

        visitor.Visit(nameof(QueuedButtonDownMask), ref QueuedButtonDownMask);
        visitor.Visit(nameof(QueuedButtonChangeMask), ref QueuedButtonChangeMask);
        visitor.Visit(nameof(LastCommandNumberProcessed), ref LastCommandNumberProcessed);
        visitor.Visit(nameof(CmdForwardMove), ref CmdForwardMove);
        visitor.Visit(nameof(CmdLeftMove), ref CmdLeftMove);
        visitor.Visit(nameof(CmdUpMove), ref CmdUpMove);
        visitor.Visit(nameof(MaxSpeed), ref MaxSpeed);

        for (var i = 0; i < ForceSubtickMoveWhen.Length; i++)
        {
            visitor.Visit(ForceSubtickMoveWhenNames[i], ref ForceSubtickMoveWhen[i]);
        }

        visitor.Visit(nameof(ForwardMove), ref ForwardMove);
        visitor.Visit(nameof(LeftMove), ref LeftMove);
        visitor.Visit(nameof(UpMove), ref UpMove);
        visitor.Visit(nameof(LastMovementImpulses), ref LastMovementImpulses);
        visitor.Visit(nameof(ForceReleasedButtons), ref ForceReleasedButtons);
        visitor.Visit(nameof(PendingForceReleasedButtons), ref PendingForceReleasedButtons);
        visitor.Visit(nameof(TickBase), ref TickBase);
        visitor.Visit(nameof(StepSoundTime), ref StepSoundTime);
        visitor.Visit(nameof(FallVelocity), ref FallVelocity);
        visitor.Visit(nameof(SurfaceFriction), ref SurfaceFriction);

        var surface = (ulong)SurfacePropertyHash;
        var lastGroundSurface = (ulong)LastGroundSurfacePropertyHash;
        visitor.Visit(nameof(SurfacePropertyHash), ref surface);
        visitor.Visit(nameof(LastGroundSurfacePropertyHash), ref lastGroundSurface);
        SurfacePropertyHash = (uint)surface;
        LastGroundSurfacePropertyHash = (uint)lastGroundSurface;

        visitor.Visit(nameof(LadderNormal), ref LadderNormal);
        visitor.Visit(nameof(Ducked), ref Ducked);
        visitor.Visit(nameof(DuckAmount), ref DuckAmount);
        visitor.Visit(nameof(DuckSpeed), ref DuckSpeed);
        visitor.Visit(nameof(DesiresDuck), ref DesiresDuck);
        visitor.Visit(nameof(Ducking), ref Ducking);
        visitor.Visit(nameof(DuckRootOffset), ref DuckRootOffset);
        visitor.Visit(nameof(DuckViewOffset), ref DuckViewOffset);
        visitor.Visit(nameof(LastDuckTime), ref LastDuckTime);
        visitor.Visit(nameof(LastPositionAtFullCrouchSpeed), ref LastPositionAtFullCrouchSpeed);
        visitor.Visit(nameof(DuckUntilOnGround), ref DuckUntilOnGround);
        visitor.Visit(nameof(HasWalkMovedSinceLastJump), ref HasWalkMovedSinceLastJump);
        visitor.Visit(nameof(SpeedCropped), ref SpeedCropped);
        visitor.Visit(nameof(OldWaterLevel), ref OldWaterLevel);
        visitor.Visit(nameof(Forward), ref Forward);
        visitor.Visit(nameof(Left), ref Left);
        visitor.Visit(nameof(Up), ref Up);
        visitor.Visit(nameof(StashGrenadeParameterWhen), ref StashGrenadeParameterWhen);
        visitor.Visit(nameof(UseFrictionStashedSpeed), ref UseFrictionStashedSpeed);
        visitor.Visit(nameof(UseFrictionStashedSpeedUntilFrac), ref UseFrictionStashedSpeedUntilFrac);
        visitor.Visit(nameof(FrictionStashedSpeed), ref FrictionStashedSpeed);
        visitor.Visit(nameof(Stamina), ref Stamina);
        visitor.Visit(nameof(AccumulatedJumpError), ref AccumulatedJumpError);
        visitor.Visit(nameof(OldJumpPressed), ref OldJumpPressed);
        visitor.Visit(nameof(JumpPressedTime), ref JumpPressedTime);
        visitor.Visit(nameof(LastJumpTick), ref LastJumpTick);
        visitor.Visit(nameof(LastJumpFrac), ref LastJumpFrac);
        visitor.Visit(nameof(LastJumpVelocityZ), ref LastJumpVelocityZ);
        visitor.Visit(nameof(JumpApexPending), ref JumpApexPending);
        visitor.Visit(nameof(WalkWishVel), ref WalkWishVel);
        visitor.Visit(nameof(StuckLast), ref StuckLast);
        visitor.Visit(nameof(StuckCheckTime), ref StuckCheckTime);
        visitor.Visit(nameof(LastActualJumpPressTick), ref LastActualJumpPressTick);
        visitor.Visit(nameof(LastActualJumpPressFrac), ref LastActualJumpPressFrac);
        visitor.Visit(nameof(LastUsableJumpPressTick), ref LastUsableJumpPressTick);
        visitor.Visit(nameof(LastUsableJumpPressFrac), ref LastUsableJumpPressFrac);
        visitor.Visit(nameof(LastLandedTick), ref LastLandedTick);
        visitor.Visit(nameof(LastLandedFrac), ref LastLandedFrac);
        visitor.Visit(nameof(LastLandedVelocityX), ref LastLandedVelocityX);
        visitor.Visit(nameof(LastLandedVelocityY), ref LastLandedVelocityY);
        visitor.Visit(nameof(LastLandedVelocityZ), ref LastLandedVelocityZ);
        visitor.Visit(nameof(Jumped), ref Jumped);
        visitor.Visit(nameof(CurTime), ref CurTime);
        visitor.Visit(nameof(FrameTime), ref FrameTime);
        visitor.Visit(nameof(TickCount), ref TickCount);
    }

    /// <summary>The player as the recorder keeps them at each recorded moment.</summary>
    private MovementState CaptureState(Vector3 origin, Vector3 velocity)
        => new(origin, velocity, BaseVelocity, Flags, MoveType, GroundEntity, Ducked, DuckAmount, SurfaceFriction, WaterLevel);
}
