namespace ValveResourceFormat.Renderer.Movement;

internal sealed partial class MovementServices
{
    private bool CanUnduck(MoveData mv)
    {
        // Noclip stands up through anything
        if (ActualMoveType == MoveType.NoClip)
        {
            return true;
        }

        var newOrigin = mv.AbsOrigin;

        if (OnGround)
        {
            newOrigin += DuckHullMin - HullMin;
        }
        else
        {
            // In the air the hull grows around its middle, so the origin drops by half the difference
            newOrigin -= ((HullMax - HullMin) - (DuckHullMax - DuckHullMin)) * 0.5f;
        }

        var trace = TracePlayerBBox(mv.AbsOrigin, newOrigin, HullMin, HullMax, Rubikon.Cs2PlayerCollisionFilter);

        return !trace.StartSolid && trace.Fraction == 1f;
    }

    /// <summary>Lifts the player out of the floor after ducking left them embedded, a unit at a time.</summary>
    private void FixPlayerCrouchStuck(MoveData mv, bool upward)
    {
        if (!TestPlayerPosition(mv.AbsOrigin, out _))
        {
            return;
        }

        var direction = upward ? 1f : 0f;
        var start = mv.AbsOrigin;

        for (var i = 0; i < 36; i++)
        {
            mv.AbsOrigin.Z += direction;

            if (!TestPlayerPosition(mv.AbsOrigin, out _))
            {
                return;
            }
        }

        mv.AbsOrigin = start;
    }

    private void FinishUnDuck(MoveData mv)
    {
        Vector3 offset;

        if (OnGround || ActualMoveType == MoveType.Ladder)
        {
            offset = DuckHullMin - HullMin;
        }
        else
        {
            // Released duck in the air: the origin drops so the hull grows around its middle
            offset = new Vector3(
                ((HullMax.X - HullMin.X) - (DuckHullMax.X - DuckHullMin.X)) * -0.5f - 0f,
                ((HullMax.Y - HullMin.Y) - (DuckHullMax.Y - DuckHullMin.Y)) * -0.5f - 0f,
                ((HullMax.Z - HullMin.Z) - (DuckHullMax.Z - DuckHullMin.Z)) * -0.5f - 0f);
        }

        mv.AbsOrigin = offset + mv.AbsOrigin;
        DuckRootOffset -= offset.Z;

        Flags &= ~PlayerFlags.Ducking;
        Ducked = false;
        Ducking = false;

        CategorizePosition(mv, stayOnGround: false);

        DuckAmount = 0f;
    }

    private void FinishDuck(MoveData mv)
    {
        Vector3 offset;

        if (OnGround || ActualMoveType == MoveType.Ladder || WaterLevel >= 3)
        {
            offset = -(DuckHullMin - HullMin);
        }
        else
        {
            offset = ((HullMax - HullMin) - (DuckHullMax - DuckHullMin)) * 0.5f;
        }

        Ducking = false;
        Ducked = true;
        LastDuckTime = CurTime;
        Flags |= PlayerFlags.Ducking;

        mv.AbsOrigin = offset + mv.AbsOrigin;
        DuckRootOffset -= offset.Z;

        FixPlayerCrouchStuck(mv, upward: true);
        CategorizePosition(mv, stayOnGround: false);

        DuckAmount = 1f;
    }

    /// <summary>
    /// Animates the duck amount towards ducked or standing. The hull only changes when that finishes,
    /// or straight away in the air.
    /// </summary>
    private void Duck(MoveData mv)
    {
        var touchingGround = OnGround;
        var inAir = !touchingGround && ActualMoveType != MoveType.Ladder;
        var duckHeld = Buttons.IsHeld(InputButtons.Duck);

        // A new duck can only start at enough duck speed and not too soon after the last one
        var canStartDuck = false;

        if (!(1.5f > DuckSpeed))
        {
            canStartDuck = (Flags & PlayerFlags.Ducking) != 0 || !((Vars.TimeBetweenDucks + LastDuckTime) > CurTime);
        }

        var wantsDuck = (DuckUntilOnGround && inAir) || (duckHeld && canStartDuck);

        if (DuckUntilOnGround)
        {
            if ((Flags & PlayerFlags.Ducking) == 0)
            {
                DuckUntilOnGround = false;
                return;
            }

            if (inAir)
            {
                // Un-duck just as the player lands, if there is room
                if (mv.Velocity.Z > 0f)
                {
                    return;
                }

                var newOrigin = mv.AbsOrigin - ((HullMax - HullMin) - (DuckHullMax - DuckHullMin));
                var groundCheck = newOrigin with { Z = newOrigin.Z - Vars.StepSize };
                var trace = TracePlayerBBox(newOrigin, groundCheck, HullMin, HullMax, Rubikon.Cs2PlayerCollisionFilter);

                if (trace.StartSolid || trace.Fraction == 1f)
                {
                    return;
                }
            }

            DuckUntilOnGround = false;

            if (CanUnduck(mv))
            {
                FinishUnDuck(mv);
            }

            return;
        }

        // The duck spam penalty wears off over time
        DuckSpeed = Approach(DuckSpeedIdeal, DuckSpeed, FrameTime * 3f);

        // Moving far enough restores duck speed faster
        if (DuckSpeed < DuckSpeedIdeal)
        {
            if (!(DuckAmount > 0f) || !(1f > DuckAmount))
            {
                var dx = LastPositionAtFullCrouchSpeed.X - AbsOrigin.X;
                var dy = LastPositionAtFullCrouchSpeed.Y - AbsOrigin.Y;

                if (((dx * dx) + (dy * dy)) > (64f * 64f))
                {
                    DuckSpeed = Approach(DuckSpeedIdeal, DuckSpeed, FrameTime * 6f);
                }
            }
        }
        else
        {
            LastPositionAtFullCrouchSpeed = new Vector2(AbsOrigin.X, AbsOrigin.Y);
        }

        if (wantsDuck)
        {
            if (DuckAmount < 1f && !Ducking)
            {
                Ducking = true;
            }

            if (Ducking)
            {
                // Ducking is a little slower than standing up
                var speed = DuckSpeed * 0.80000001f;
                DuckAmount = Approach(1f, DuckAmount, speed * FrameTime);

                if (!(1f > DuckAmount) || !touchingGround)
                {
                    FinishDuck(mv);
                }
            }
        }
        else
        {
            // Not holding duck but still ducked, as after a round restart: stand up
            if (DuckAmount > 0f && !Ducking)
            {
                Ducking = true;
            }

            if (Ducking)
            {
                if (CanUnduck(mv))
                {
                    // Standing up never goes below 1.5, so half-ducked positions give no advantage
                    var speed = MathF.Max(1.5f, DuckSpeed);
                    DuckAmount = Approach(0f, DuckAmount, speed * FrameTime);
                    Ducked = false;

                    if (!(DuckAmount > 0f) || !touchingGround)
                    {
                        FinishUnDuck(mv);
                    }

                    // The ducked accuracy goes early in the un-duck
                    if (!(DuckAmount > 0.75f))
                    {
                        Flags &= ~PlayerFlags.Ducking;
                    }
                }
                else
                {
                    // Under something: stay fully ducked and try again once clear of it
                    DuckAmount = 1f;
                    Ducked = true;
                    Ducking = false;
                    Flags |= PlayerFlags.Ducking;
                }
            }
        }

        // Ducking slows the player in proportion to how far ducked
        if (!SpeedCropped && (Buttons.IsHeld(InputButtons.Duck) || Ducking || (Flags & PlayerFlags.Ducking) != 0))
        {
            var modifier = ((DuckAmount * DuckSpeedModifier) + 1f) - DuckAmount;
            mv.ForwardMove = modifier * mv.ForwardMove;
            mv.SideMove = modifier * mv.SideMove;
            mv.UpMove = modifier * mv.UpMove;
            mv.MaxSpeed = modifier * mv.MaxSpeed;
            SpeedCropped = true;
        }
    }

    /// <summary>
    /// Moves the hull offset left by a duck back to zero over a short time, and the eye height with it,
    /// so the view does not snap when the hull changes.
    /// </summary>
    private void UpdateDuckRootOffset(float frametime)
    {
        var standHeight = HullMax.Z - HullMin.Z;
        var duckHeight = DuckHullMax.Z - DuckHullMin.Z;

        DuckRootOffset = Approach(0f, DuckRootOffset, (((standHeight - duckHeight) * 0.5f) / 0.1f) * frametime);
        DuckViewOffset = Approach(((duckHeight - standHeight) * DuckAmount) - DuckRootOffset, DuckViewOffset, ((standHeight - duckHeight) / 0.2f) * frametime);
    }

    /// <summary>Gets the eye height above the origin.</summary>
    public float ViewOffsetZ => TickFraction.RoundCoord64((Math.Clamp(DuckRootOffset, -32f, 32f) + DuckViewOffset) + ViewHeight);
}
