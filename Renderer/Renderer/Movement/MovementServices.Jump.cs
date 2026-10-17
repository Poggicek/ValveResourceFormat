namespace ValveResourceFormat.Renderer.Movement;

internal sealed partial class MovementServices
{
    /// <summary>Caps the speed a jump leaves with at 1.1 times the max speed, unless bunny hopping is enabled.</summary>
    private void PreventBunnyJumping(MoveData mv)
    {
        if (Vars.EnableBunnyHopping)
        {
            return;
        }

        var maxScaledSpeed = MaxSpeed * 1.1f;

        if (maxScaledSpeed <= 0f)
        {
            return;
        }

        var speed = Length(mv.Velocity);

        if (speed <= maxScaledSpeed)
        {
            return;
        }

        var fraction = maxScaledSpeed / speed;
        mv.Velocity = new Vector3(fraction * mv.Velocity.X, fraction * mv.Velocity.Y, fraction * mv.Velocity.Z);
    }

    private void RecordJump(MoveData mv)
    {
        LastJumpTick = mv.TickCount;
        LastJumpFrac = TickFraction.RoundSubtickFraction(mv.SubtickStartFraction);
        LastJumpVelocityZ = mv.Velocity.Z;
        JumpApexPending = true;
        Jumped = true;
    }

    /// <summary>The take-off shared by both jumps.</summary>
    private void StartJumpCommon(MoveData mv)
    {
        PreventBunnyJumping(mv);
        SetGroundEntity(mv, null);

        // Grenades thrown in the next 0.1 seconds use the throw from before the jump
        StashGrenadeParameterWhen = (CurTime - FrameTime) + 0.1f;

        // Airborne heights are kept on a 1/64 grid from here on
        var height = TickFraction.RoundCoord64(mv.AbsOrigin.Z);
        AccumulatedJumpError = mv.AbsOrigin.Z - height;
    }

    private float JumpImpulseFor(bool ducked)
    {
        var groundFactor = Surfaces.Get(SurfacePropertyHash).JumpFactor;

        // A standing jump loses the first half tick of gravity
        return ducked
            ? groundFactor * Vars.JumpImpulse
            : (Vars.JumpImpulse - (((GravityScale * Vars.Gravity) * 0.5f) * 0.0078125f)) * groundFactor;
    }

    private void CheckJumpButtonLegacy(MoveData mv)
    {
        if (!Buttons.IsHeld(InputButtons.Jump) && !WasButtonPressed(InputButtons.Jump))
        {
            return;
        }

        // A press long enough after the previous one counts as a new jump
        if ((OldJumpPressed && CurTime > (Vars.JumpSpamPenaltyTime + JumpPressedTime)) || Vars.AutoBunnyHopping)
        {
            OldJumpPressed = false;
        }

        OnJumpLegacy(mv);

        OldJumpPressed = true;

        if (WasButtonPressed(InputButtons.Jump))
        {
            JumpPressedTime = CurTime - FrameTime;
        }
    }

    private void OnJumpLegacy(MoveData mv)
    {
        if (!Vars.AutoBunnyHopping && !WasButtonPressed(InputButtons.Jump))
        {
            return;
        }

        if (WaterJumpTime != 0f)
        {
            WaterJumpTime = MathF.Max(0f, WaterJumpTime - FrameTime);
            return;
        }

        if (WaterLevel >= 3)
        {
            SwimUp(mv);
            return;
        }

        HasWalkMovedSinceLastJump = false;

        if (!OnGround || OldJumpPressed)
        {
            return;
        }

        StartJumpCommon(mv);

        if (Length(mv.Velocity) > 140f)
        {
            Effects?.PlayStepSound(mv.AbsOrigin, LastGroundSurfacePropertyHash, 1f, onLadder: false);
        }

        var startZ = mv.Velocity.Z;
        mv.SetVelocityAxis(2, JumpImpulseFor(DuckUntilOnGround || Ducking || (Flags & PlayerFlags.Ducking) != 0));

        Effects?.PlayJumpSound(mv.AbsOrigin);

        var multiplier = Stamina > 0f ? Clamp(1f - (Stamina / 100f), 0f, 1f) : 1f;
        mv.Velocity.Z = multiplier * mv.Velocity.Z;

        var outWishZ = (mv.Velocity.Z - startZ) + mv.OutWishVel.Z;
        mv.OutWishVel.Z = outWishZ;

        // Jumping costs stamina
        Stamina = Clamp((Vars.StaminaJumpCost * outWishZ) + Stamina, 0f, Vars.StaminaMax);

        RecordJump(mv);
    }

    private TickFraction LastLanded => new(LastLandedTick, LastLandedFrac);
    private TickFraction LastUsableJumpPress => new(LastUsableJumpPressTick, LastUsableJumpPressFrac);
    private TickFraction BhopWindowStart => LastLanded - TickFraction.FromSecondsRounded(Vars.BhopTimeWindow * 0.5f);
    private TickFraction BhopWindowEnd => LastLanded + TickFraction.FromSecondsRounded(Vars.BhopTimeWindow * 0.5f);
    private TickFraction LandedPlusOneTick => LastLanded + TickFraction.FromSecondsRounded(TickInterval);

    /// <summary>
    /// A hard landing caps the max speed, recovering linearly with every whole tick since the landing.
    /// </summary>
    private float GetLandingSpeedScale(TickFraction now)
    {
        var penalty = Clamp((LastLandedVelocityZ * 0.00050000002f) + 1f, 0.2f, 1f);
        var since = now - LastLanded;
        var scale = ((since.Tick * TickInterval) * 1.1111894f) + (penalty * penalty);
        return MathF.Min(scale, 1f);
    }

    /// <summary>A hard landing also lowers the next jump, recovering at 0.6 per second.</summary>
    private float GetJumpVelocityMultiplier(TickFraction jumpTime)
    {
        var penalty = Clamp((LastLandedVelocityZ * 0.00050000002f) + 1f, 0.2f, 1f);
        var since = (jumpTime - LastLanded) + new TickFraction(1, 0f);
        var multiplier = MathF.Min(penalty + (since.ToSeconds() * 0.60000002f), 1f);

        if (Stamina > 0f)
        {
            multiplier *= Clamp(1f - (Stamina / 100f), 0f, 1f);
        }

        return multiplier;
    }

    /// <summary>Remembers the start of the step's vertical motion, to solve the exact landing time from.</summary>
    private void RecordPreAirMove(MoveData mv)
    {
        var accelerationZ = mv.ContinuousAcceleration.Z;
        mv.PreAirMovePosZ = mv.AbsOrigin.Z;
        mv.PreAirMoveAccelZ = accelerationZ;
        mv.PreAirMoveVelZ = mv.Velocity.Z - ((accelerationZ * FrameTime) * 0.5f);
    }

    private void RecordLanding(MoveData mv, float fallVelocity)
    {
        var landVelocityZ = -fallVelocity;
        var landFraction = mv.SubtickEndFraction;

        // Solve z0 + v0 t + a t^2 / 2 for when inside this step the player touched down
        var dz = mv.AbsOrigin.Z - mv.PreAirMovePosZ;
        var a = mv.PreAirMoveAccelZ;

        if (0f > dz)
        {
            var v0 = mv.PreAirMoveVelZ;

            if (0f > v0 && 0f > a)
            {
                var discriminant = ((a + a) * dz) + (v0 * v0);

                if (discriminant >= 0f)
                {
                    var t = (-v0 - MathF.Sqrt(discriminant)) / a;
                    t = 0f > t ? 0f : MathF.Min(FrameTime, t);

                    landVelocityZ = (t * a) + v0;
                    landFraction = (t / TickInterval) + mv.SubtickStartFraction;
                }
            }
        }

        var landed = new TickFraction(mv.TickCount, TickFraction.RoundSubtickFraction(landFraction));
        LastLandedTick = landed.Tick;
        LastLandedFrac = landed.Fraction;
        LastLandedVelocityX = QuantizedFloat.Velocity.Quantize(mv.Velocity.X);
        LastLandedVelocityY = QuantizedFloat.Velocity.Quantize(mv.Velocity.Y);
        LastLandedVelocityZ = QuantizedFloat.Velocity.Quantize(landVelocityZ);
    }

    /// <summary>Splits the tick where the jump state changes inside it.</summary>
    private void AddModernJumpSubtickSplits(MoveData mv)
    {
        var tick = mv.TickCount;

        // While the landing penalty recovers it changes every tick at the landing fraction
        if (1f > GetLandingSpeedScale(new TickFraction(tick, 0f)))
        {
            mv.InsertSubtickSplit(LastLandedFrac);
        }

        var windowEnd = BhopWindowEnd;

        if (tick == windowEnd.Tick)
        {
            mv.InsertSubtickSplit(windowEnd.Fraction);
        }

        var oneTick = LandedPlusOneTick;

        if (tick == oneTick.Tick)
        {
            mv.InsertSubtickSplit(oneTick.Fraction);
        }
    }

    private void CheckJumpButtonModern(MoveData mv)
    {
        var now = new TickFraction(mv.TickCount, mv.SubtickStartFraction);
        var pressed = WasButtonPressed(InputButtons.Jump);

        if (pressed)
        {
            var lastActual = new TickFraction(LastActualJumpPressTick, LastActualJumpPressFrac);
            var press = new TickFraction(now.Tick, TickFraction.RoundSubtickFraction(now.Fraction));

            LastActualJumpPressTick = press.Tick;
            LastActualJumpPressFrac = press.Fraction;

            // A press right after the previous one is ignored
            if (Vars.JumpSpamPenaltyTime >= (press - lastActual).ToSeconds())
            {
                pressed = false;
            }
            else
            {
                // An earlier press still inside the landing window is kept, else this one is used on landing
                var usable = LastUsableJumpPress;
                var keep = TickFraction.Compare(usable, BhopWindowStart) != -1 && TickFraction.Compare(usable, BhopWindowEnd) < 0;

                if (!keep)
                {
                    LastUsableJumpPressTick = press.Tick;
                    LastUsableJumpPressFrac = press.Fraction;
                }
            }
        }

        if ((Vars.AutoBunnyHopping && Buttons.IsHeld(InputButtons.Jump)) || pressed)
        {
            OnJumpModern(mv);
            return;
        }

        // A press buffered from just before landing still jumps
        var buffered = LastUsableJumpPress;

        if (TickFraction.Compare(buffered, BhopWindowStart) >= 0)
        {
            if (TickFraction.Compare(buffered, BhopWindowEnd) == -1
                || TickFraction.Compare(buffered, LastLanded + TickFraction.FromSecondsRounded(TickInterval)) == -1)
            {
                OnJumpModern(mv);
                return;
            }
        }

        LastUsableJumpPressTick = JumpPressTickNone;
    }

    private void OnJumpModern(MoveData mv)
    {
        // The usable press is kept while airborne: the player may land inside the window later
        if (!OnGround)
        {
            return;
        }

        var usable = LastUsableJumpPress;
        var tickNow = new TickFraction(mv.TickCount, mv.SubtickStartFraction);
        var jumpTime = tickNow;

        var inWindow = TickFraction.Compare(BhopWindowStart, usable) is -1 or 0 && TickFraction.Compare(usable, BhopWindowEnd) == -1;

        var autoCompare = TickFraction.Compare(tickNow, LastLanded + new TickFraction(1, 0f));
        bool useLandingVelocity;

        if (inWindow || (Vars.AutoBunnyHopping && autoCompare is -1 or TickFraction.Unordered or 0))
        {
            // A perfect bunny hop keeps the speed it landed with, if that was above the max speed
            useLandingVelocity = Vars.AutoBunnyHopping
                || !((mv.MaxSpeed * mv.MaxSpeed) >= ((LastLandedVelocityX * LastLandedVelocityX) + (LastLandedVelocityY * LastLandedVelocityY)));
        }
        else
        {
            useLandingVelocity = false;
        }

        if (useLandingVelocity)
        {
            jumpTime = LastLanded;
            mv.Velocity.X = LastLandedVelocityX;
            mv.Velocity.Y = LastLandedVelocityY;
        }
        else if (TickFraction.Compare(tickNow, LandedPlusOneTick) == -1)
        {
            // A plain jump cannot happen in the same tick as the landing
            return;
        }

        LastUsableJumpPressTick = JumpPressTickNone;
        HasWalkMovedSinceLastJump = false;
        StartJumpCommon(mv);

        if (LengthSquared(mv.Velocity) > 19600f)
        {
            Effects?.PlayStepSound(mv.AbsOrigin, LastGroundSurfacePropertyHash, 1f, onLadder: false);
        }

        var startZ = mv.Velocity.Z;
        mv.SetVelocityAxis(2, JumpImpulseFor(DuckUntilOnGround || Ducking || (Flags & PlayerFlags.Ducking) != 0));

        var multiplier = GetJumpVelocityMultiplier(jumpTime);
        mv.Velocity.Z = multiplier * mv.Velocity.Z;
        mv.OutWishVel.Z = (mv.Velocity.Z - startZ) + mv.OutWishVel.Z;

        Effects?.PlayJumpSound(mv.AbsOrigin);
        RecordJump(mv);
    }
}
