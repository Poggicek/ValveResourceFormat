using ValveResourceFormat.Renderer.Entities;

namespace ValveResourceFormat.Renderer.Movement;

internal sealed partial class MovementServices
{
    // Offsets tried in turn to get out of geometry, a few per check
    private static readonly Vector3[] StuckTable =
    [
        new(0, 0, -0.125f), new(0, 0, 0), new(0, 0, 0.125f),
        new(0, -0.125f, 0), new(0, 0, 0), new(0, 0.125f, 0),
        new(-0.125f, 0, 0), new(0, 0, 0), new(0.125f, 0, 0),
        new(-0.125f, -0.125f, -0.125f), new(-0.125f, -0.125f, 0.125f), new(-0.125f, 0.125f, -0.125f), new(-0.125f, 0.125f, 0.125f),
        new(0.125f, -0.125f, -0.125f), new(0.125f, -0.125f, 0.125f), new(0.125f, 0.125f, -0.125f), new(0.125f, 0.125f, 0.125f),
        new(0, 0, 0), new(0, 0, 1), new(0, 0, 6),
        new(0, -2, 0), new(0, 0, 0), new(0, 2, 0),
        new(-2, 0, 0), new(0, 0, 0), new(2, 0, 0),
        new(-2, -2, 0), new(-2, 0, 0), new(-2, 2, 0), new(0, -2, 0), new(0, 0, 0), new(0, 2, 0), new(2, -2, 0), new(2, 0, 0), new(2, 2, 0),
        new(-2, -2, 1), new(-2, 0, 1), new(-2, 2, 1), new(0, -2, 1), new(0, 0, 1), new(0, 2, 1), new(2, -2, 1), new(2, 0, 1), new(2, 2, 1),
        new(-2, -2, 6), new(-2, 0, 6), new(-2, 2, 6), new(0, -2, 6), new(0, 0, 6), new(0, 2, 6), new(2, -2, 6), new(2, 0, 6), new(2, 2, 6),
        new(0, 0, 0),
    ];

    private float StuckCheckTime = float.MinValue;

    /// <summary>Decides whether the player stands on something, and snaps them down onto it.</summary>
    private void CategorizePosition(MoveData mv, bool stayOnGround)
    {
        // Reset each time, or a quick plunge into water keeps bogus friction
        SurfaceFriction = 1f;

        CheckWater(mv);

        var origin = mv.AbsOrigin;
        var point = mv.AbsOrigin;
        var velocityZ = mv.Velocity.Z;

        // Moving up fast relative to what is underfoot means leaving the ground
        var movingUpRapidly = velocityZ > NonJumpVelocity;

        if (movingUpRapidly && OnGround)
        {
            movingUpRapidly = (velocityZ - GetGroundVelocity(GroundEntity).Z) > NonJumpVelocity;
        }

        var fullyUnderwater = WaterLevelFraction == 1f;
        var probe = false;

        // Grounded players probe a full step down, which keeps them on the ground going down stairs
        if ((MoveType == MoveType.Walk && OnGround && !fullyUnderwater) || stayOnGround)
        {
            probe = true;
            point.Z -= Vars.StepSize;
        }
        else
        {
            point.Z += -2f;
        }

        var movingUpOnLadder = MoveType == MoveType.Ladder && velocityZ > 0.02f;

        if (movingUpRapidly || movingUpOnLadder)
        {
            SetGroundEntity(mv, null);
            return;
        }

        // The probe starts 4 units up with a hull 4 units shorter, so a player slightly sunk into the
        // floor still finds it
        var maxs = PlayerMaxs;
        var traceMaxs = new Vector3(maxs.X - 0f, maxs.Y - 0f, maxs.Z - 4f);
        var traceStart = new Vector3(origin.X + 0f, origin.Y + 0f, origin.Z + 4f);
        var trace = TracePlayerBBox(traceStart, point, PlayerMins, traceMaxs, Rubikon.Cs2PlayerCollisionFilter);

        LastGroundSurfacePropertyHash = trace.SurfacePropertyHash;

        if (!IsValidGroundTrace(trace))
        {
            // A smaller box in any quadrant may find shallower ground to stand on
            TryTouchGroundInQuadrants(origin, point, PlayerMins, PlayerMaxs, ref trace, Vars.StandableNormal);

            if (!IsValidGroundTrace(trace))
            {
                SetGroundEntity(mv, null);

                if (mv.Velocity.Z > 0f)
                {
                    SurfaceFriction = 0.25f;
                }

                return;
            }
        }

        SetGroundEntity(mv, trace);

        if (probe && !trace.StartSolid && trace.Fraction > 0f && trace.Fraction < 1f)
        {
            mv.AbsOrigin = trace.EndPos;
        }
    }

    private void TryTouchGroundInQuadrants(Vector3 start, Vector3 end, Vector3 minsSource, Vector3 maxsSource, ref GameTrace trace, float minNormal)
    {
        var endPos = trace.EndPos;
        var fraction = trace.Fraction;

        // -x -y quadrant
        var mins = minsSource;
        var maxs = new Vector3(MathF.Min(0f, maxsSource.X), MathF.Min(0f, maxsSource.Y), maxsSource.Z);
        trace = TracePlayerBBox(start, end, mins, maxs, Rubikon.Cs2PlayerCollisionFilter);

        if (!trace.Hit || !(trace.Normal.Z >= minNormal))
        {
            // +x +y quadrant
            mins = new Vector3(MathF.Max(0f, minsSource.X), MathF.Max(0f, minsSource.Y), minsSource.Z);
            maxs = maxsSource;
            trace = TracePlayerBBox(start, end, mins, maxs, Rubikon.Cs2PlayerCollisionFilter);

            if (!trace.Hit || !(trace.Normal.Z >= minNormal))
            {
                // -x +y quadrant
                mins = new Vector3(minsSource.X, MathF.Max(0f, minsSource.Y), minsSource.Z);
                maxs = new Vector3(MathF.Min(0f, maxsSource.X), maxsSource.Y, maxsSource.Z);
                trace = TracePlayerBBox(start, end, mins, maxs, Rubikon.Cs2PlayerCollisionFilter);

                if (!trace.Hit || !(trace.Normal.Z >= Vars.StandableNormal))
                {
                    // +x -y quadrant
                    mins = new Vector3(MathF.Max(0f, minsSource.X), minsSource.Y, minsSource.Z);
                    maxs = new Vector3(maxsSource.X, MathF.Min(0f, maxsSource.Y), maxsSource.Z);
                    trace = TracePlayerBBox(start, end, mins, maxs, Rubikon.Cs2PlayerCollisionFilter);
                }
            }
        }

        trace.EndPos = endPos;
        trace.Fraction = fraction;
    }

    private bool IsValidGroundTrace(in GameTrace trace)
        => trace.Hit && !trace.StartSolid && trace.Normal.Z >= Vars.StandableNormal;

    /// <summary>
    /// Lands on or leaves the ground. Leaving a moving ground keeps its velocity as base velocity, and
    /// landing on one takes it away again.
    /// </summary>
    private void SetGroundEntity(MoveData mv, GameTrace? trace)
    {
        var newGround = trace?.Entity;
        var hasNewGround = trace.HasValue;
        var hadGround = OnGround;

        var baseVelocity = BaseVelocity;

        if (!hadGround && hasNewGround)
        {
            // Subtract the ground velocity at the instant of landing
            var groundVelocity = GetGroundVelocity(newGround);
            baseVelocity -= groundVelocity;
            baseVelocity.Z = groundVelocity.Z;
        }
        else if (hadGround && !hasNewGround)
        {
            // Add in the ground velocity at the instant of leaving it
            var groundVelocity = GetGroundVelocity(GroundEntity);
            baseVelocity += groundVelocity;
            baseVelocity.Z = groundVelocity.Z;
        }

        BaseVelocity = baseVelocity;
        GroundEntity = newGround;

        if (!hasNewGround)
        {
            Flags &= ~PlayerFlags.OnGround;
            return;
        }

        Flags |= PlayerFlags.OnGround;

        var ground = trace!.Value;
        SurfacePropertyHash = ground.SurfacePropertyHash;
        SurfaceFriction = MathF.Min(1f, Surfaces.Get(ground.SurfacePropertyHash).Friction * 1.25f);
        WaterJumpTime = 0f;

        // Standing on an entity other than the world touches it
        if ((!(1f > ground.Fraction) && !ground.StartSolid) || (ground.Entity != null && ground.Entity != Collision.Entities?.World))
        {
            mv.AddToTouched(ground);
        }

        mv.SetVelocityAxis(2, 0f);
    }

    private static Vector3 GetGroundVelocity(BaseEntity? ground)
        => ground is null or WorldEntity ? Vector3.Zero : ground.Velocity;

    private void CheckFalling(MoveData mv)
    {
        if (!OnGround || !(FallVelocity > 0f))
        {
            return;
        }

        if (WaterLevel != 0 && FallVelocity >= 260f)
        {
            Effects?.PlayWaterSound(mv.AbsOrigin, "Land_WaterVol.StepLeft");
        }

        if (FallVelocity >= PlayerFallPunchThreshold)
        {
            var volume = 0.5f;

            if (WaterLevel == 0)
            {
                // Landing on something descending lands softer
                var groundVelocity = GetGroundVelocity(GroundEntity);

                if (groundVelocity.Z < 0f)
                {
                    FallVelocity += groundVelocity.Z;
                    FallVelocity = MathF.Max(0.1f, FallVelocity);
                }

                if (FallVelocity > PlayerMaxSafeFallSpeed)
                {
                    PlayerFallingDamage(mv);
                    volume = 1f;
                }
                else if (FallVelocity > PlayerMaxSafeFallSpeed / 2f)
                {
                    volume = 0.85f;
                }
                else if (FallVelocity < PlayerMinBounceSpeed)
                {
                    volume = 0f;
                }
            }

            PlayerRoughLandingEffects(mv, volume);
        }

        // Landing dips the view
        var fall = FallVelocity;

        if (fall > 250f && fall <= 1024f)
        {
            var punch = GetViewPunchAngle();
            punch.X = (fall * 0.001f) < 0.75f ? 0.75f : fall * 0.001f;
            SetViewPunchAngle(punch);
        }

        var landVelocity = FallVelocity;

        if (Vars.LegacyJump)
        {
            Stamina = ClampPassNaN((Vars.StaminaLandCost * landVelocity) + Stamina, 0f, Vars.StaminaMax);
        }
        else
        {
            RecordLanding(mv, landVelocity);
        }

        JumpApexPending = false;

        Effects?.OnLand(mv.AbsOrigin, landVelocity);

        FallVelocity = 0f;
    }

    private void PlayerFallingDamage(MoveData mv)
    {
        // Damage grows linearly from the safe fall speed to the fatal one, 1024
        var damage = Vars.FallDamageScale * ((FallVelocity - PlayerMaxSafeFallSpeed) * 0.23809524f);

        if (damage > 0f)
        {
            Effects?.OnFallDamage(mv.AbsOrigin, damage);
        }
    }

    private void PlayerRoughLandingEffects(MoveData mv, float volume)
    {
        if (!(volume > 0f))
        {
            return;
        }

        // Play the landing step right away
        StepSoundTime = 400f;
        Effects?.PlayStepSound(mv.AbsOrigin, LastGroundSurfacePropertyHash, MathF.Max(volume, 0.1f), onLadder: false);

        // Knock the view about a little
        var punch = GetViewPunchAngle();
        punch.Z = -QuantizedFloat.Velocity.Quantize(FallVelocity) * 0.013f;

        if (punch.X > 8f)
        {
            punch.X = 8f;
        }

        SetViewPunchAngle(punch);
    }

    /// <summary>
    /// Every 64th command, frees a player stuck in geometry by trying a few nearby offsets.
    /// </summary>
    /// <returns>Whether the player is still stuck, which skips the move.</returns>
    private bool CheckStuck(MoveData mv)
    {
        if ((LastCommandNumberProcessed & 0x3F) != 0)
        {
            return false;
        }

        if (!TestPlayerPosition(mv.AbsOrigin, out var trace))
        {
            StuckLast = 0;
            return false;
        }

        // Not again too quickly
        if (!(StuckCheckTime < CurTime - 0.05f))
        {
            return true;
        }

        StuckCheckTime = CurTime;
        mv.AddToTouched(trace);

        var start = mv.AbsOrigin;

        for (var i = 0; i < 27; i++)
        {
            var test = start + StuckTable[StuckLast++ % StuckTable.Length];

            if (!TestPlayerPosition(test, out _))
            {
                StuckLast = 0;
                mv.AbsOrigin = test;
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the hull at <paramref name="position"/> starts inside something.</summary>
    private bool TestPlayerPosition(Vector3 position, out GameTrace trace)
    {
        trace = TracePlayerBBox(position, position, PlayerMins, PlayerMaxs, Rubikon.Cs2PlayerCollisionFilter);
        return trace.StartSolid;
    }

    /// <summary>The landing punch, decayed by how long ago it was set.</summary>
    private Vector3 GetViewPunchAngle()
    {
        var decay = (CurTime - ViewPunchTime) * Vars.ViewPunchDecay;
        return decay > 0f ? ViewPunchAngle * MathF.Exp(-decay) : ViewPunchAngle;
    }

    private void SetViewPunchAngle(Vector3 punch)
    {
        ViewPunchAngle = punch;
        ViewPunchTime = CurTime;
    }

    /// <summary>Gets the landing punch now, as (pitch, yaw, roll) degrees.</summary>
    public Vector3 GetViewPunchAngle(float time)
    {
        var decay = (time - ViewPunchTime) * Vars.ViewPunchDecay;
        return decay > 0f ? ViewPunchAngle * MathF.Exp(-decay) : ViewPunchAngle;
    }

    // Some callers clamp so that a NaN passes through
    private static float ClampPassNaN(float value, float low, float high)
        => low > value ? low : value > high ? high : value;
}
