using System.Runtime.CompilerServices;

namespace ValveResourceFormat.Renderer.Movement;

internal sealed partial class MovementServices
{
    private const int MaxClipPlanes = 5;

    /// <summary>
    /// Takes half of this step's continuous acceleration back out of the velocity and defers it, so the
    /// move uses the mid-step velocity, then adds the base velocity for the move.
    /// </summary>
    private void PrepareVelocityForMove(MoveData mv)
    {
        var dt = FrameTime;
        var hx = (dt * mv.ContinuousAcceleration.X) * 0.5f;
        var hy = (dt * mv.ContinuousAcceleration.Y) * 0.5f;
        var hz = (dt * mv.ContinuousAcceleration.Z) * 0.5f;

        mv.Velocity = new Vector3(mv.Velocity.X - hx, mv.Velocity.Y - hy, mv.Velocity.Z - hz);
        mv.FrameVelocityDelta = new Vector3(hx + mv.FrameVelocityDelta.X, hy + mv.FrameVelocityDelta.Y, hz + mv.FrameVelocityDelta.Z);
        mv.Velocity = new Vector3(BaseVelocity.X + mv.Velocity.X, BaseVelocity.Y + mv.Velocity.Y, BaseVelocity.Z + mv.Velocity.Z);
    }

    /// <summary>Removes the base velocity again and applies the deferred half of the step's velocity change.</summary>
    private void FinalizeVelocity(MoveData mv)
    {
        var x = mv.Velocity.X - BaseVelocity.X;
        var y = mv.Velocity.Y - BaseVelocity.Y;
        var z = mv.Velocity.Z - BaseVelocity.Z;

        mv.Velocity = new Vector3(x + mv.FrameVelocityDelta.X, mv.FrameVelocityDelta.Y + y, mv.FrameVelocityDelta.Z + z);
    }

    private void Friction(MoveData mv)
    {
        if (WaterJumpTime != 0f)
        {
            return;
        }

        var speed = Length2D(mv.Velocity);

        // Friction works off the networked speed, or the speed stashed when the wish velocity last
        // changed inside a tick
        var control = UseFrictionStashedSpeed ? FrictionStashedSpeed : QuantizedFloat.Velocity.Quantize(speed);

        if (0.1f > control)
        {
            return;
        }

        var drop = 0f;

        if (OnGround)
        {
            var friction = (Vars.Friction * SurfaceFriction) * EntityFriction;
            var frametime = FrameTime;

            // Below the stop speed, friction takes the stop speed's worth
            var decel = MathF.Max(control, Vars.StopSpeed) * friction;
            drop = (frametime * decel) + 0f;

            if (speed > 0f && frametime > 0f)
            {
                if (drop > speed)
                {
                    decel = speed / frametime;
                }

                var direction = mv.Velocity;
                VectorNormalize(ref direction);

                mv.ContinuousAcceleration = new Vector3(
                    mv.ContinuousAcceleration.X - (direction.X * decel),
                    mv.ContinuousAcceleration.Y - (direction.Y * decel),
                    mv.ContinuousAcceleration.Z - (direction.Z * decel));
            }
        }

        // What could not be dropped because the player already stopped is taken off acceleration
        var newSpeed = speed - drop;
        mv.FrictionDecel = MathF.Max(-newSpeed, 0f);
        mv.OutWishVel = mv.Velocity;

        if (speed > 0f)
        {
            newSpeed = MathF.Max(newSpeed, 0f) / speed;
            mv.Velocity = new Vector3(newSpeed * mv.Velocity.X, newSpeed * mv.Velocity.Y, newSpeed * mv.Velocity.Z);
        }
    }

    private void Accelerate(MoveData mv, Vector3 wishDirection, float wishSpeed, float acceleration)
    {
        if (WaterJumpTime == 0f)
        {
            AccelerateInternal(mv, FrameTime, wishDirection, wishSpeed, acceleration);
        }
    }

    private void AccelerateInternal(MoveData mv, float frametime, Vector3 wishDirection, float wishSpeed, float acceleration)
    {
        var currentSpeed = Dot(mv.Velocity, wishDirection);
        var addSpeed = wishSpeed - currentSpeed;

        if (addSpeed <= 0f)
        {
            return;
        }

        currentSpeed = MathF.Max(0f, currentSpeed);

        var isDucking = Buttons.IsHeld(InputButtons.Duck) || Ducking || (Flags & PlayerFlags.Ducking) != 0;
        var isWalking = Buttons.IsHeld(InputButtons.Speed) && !isDucking;

        var accelerationScale = MathF.Max(250f, wishSpeed);
        var goalSpeed = accelerationScale;
        var accelerationScaleMultiplier = 1f;

        if (Vars.AccelerateUseWeaponSpeed && WeaponMaxSpeed is { } weaponMaxSpeed)
        {
            var weaponSpeedMultiplier = MathF.Min(1f, weaponMaxSpeed / 250f);

            if (!isDucking && !isWalking)
            {
                accelerationScaleMultiplier = weaponSpeedMultiplier;
            }

            goalSpeed = weaponSpeedMultiplier * accelerationScale;
        }

        float acceleratedSpeed;
        var storedAcceleration = acceleration;

        if (WaterLevel >= 2)
        {
            var waterSlow = Vars.WaterSlowAmount;
            goalSpeed *= waterSlow;

            if (!isDucking && !isWalking)
            {
                // In water the slow also applies to the acceleration
                acceleratedSpeed = (accelerationScale * waterSlow) * accelerationScaleMultiplier;
                ApplyAcceleration(mv, frametime, wishDirection, addSpeed, acceleratedSpeed, storedAcceleration);
                return;
            }
        }

        if (isDucking)
        {
            goalSpeed *= DuckSpeedModifier;
            accelerationScaleMultiplier = MathF.Min(DuckSpeedModifier, accelerationScaleMultiplier);
        }

        acceleratedSpeed = accelerationScale * accelerationScaleMultiplier;

        if (isWalking)
        {
            acceleratedSpeed *= WalkSpeedModifier;
            goalSpeed *= WalkSpeedModifier;

            // Near the walk speed acceleration fades, so the player settles on it instead of overshooting
            if (currentSpeed > (goalSpeed - 5f))
            {
                var fraction = 1f - (MathF.Max(0f, currentSpeed - (goalSpeed - 5f)) / MathF.Max(0f, goalSpeed - (goalSpeed - 5f)));
                storedAcceleration = 0f > fraction ? 0f * acceleration : MathF.Min(1f, fraction) * acceleration;
            }
        }

        ApplyAcceleration(mv, frametime, wishDirection, addSpeed, acceleratedSpeed, storedAcceleration);
    }

    private void ApplyAcceleration(MoveData mv, float frametime, Vector3 wishDirection, float addSpeed, float accelerationScale, float acceleration)
    {
        var accelerationSpeed = 0f;

        if (frametime > 0f)
        {
            // Friction that could not be applied because the player already stopped slows acceleration
            var rate = ((accelerationScale * acceleration) * SurfaceFriction) - (mv.FrictionDecel / frametime);

            if (rate > 0f)
            {
                accelerationSpeed = rate * frametime;

                if (accelerationSpeed > addSpeed)
                {
                    accelerationSpeed = addSpeed;
                    rate = addSpeed / frametime;
                }

                mv.ContinuousAcceleration = new Vector3(
                    (wishDirection.X * rate) + mv.ContinuousAcceleration.X,
                    (wishDirection.Y * rate) + mv.ContinuousAcceleration.Y,
                    (wishDirection.Z * rate) + mv.ContinuousAcceleration.Z);
            }
        }

        mv.Velocity = new Vector3(
            (wishDirection.X * accelerationSpeed) + mv.Velocity.X,
            (wishDirection.Y * accelerationSpeed) + mv.Velocity.Y,
            (wishDirection.Z * accelerationSpeed) + mv.Velocity.Z);
    }

    private void WalkMove(MoveData mv)
    {
        HasWalkMovedSinceLastJump = true;

        AngleVectors(mv.ViewAngles, out var forward, out var left, out _);

        var hadGround = OnGround;
        var forwardMove = mv.ForwardMove;
        var sideMove = mv.SideMove;

        if (forward.Z != 0f)
        {
            forward.Z = 0f;
            VectorNormalize(ref forward);
        }

        if (left.Z != 0f)
        {
            left.Z = 0f;
            VectorNormalize(ref left);
        }

        var wishVelocity = new Vector3((forward.X * forwardMove) + (left.X * sideMove), (forward.Y * forwardMove) + (left.Y * sideMove), 0f);
        mv.WalkWishVel = new Vector2(wishVelocity.X, wishVelocity.Y);

        var wishDirection = wishVelocity;
        var wishSpeed = VectorNormalize(ref wishDirection);

        UpdateFrictionStash(mv);

        mv.SetVelocityAxis(2, 0f);
        FallVelocity = 0f;

        Friction(mv);
        CheckVelocity(mv);

        if (wishSpeed != 0f)
        {
            wishSpeed = MathF.Min(mv.MaxSpeed, wishSpeed);
        }

        mv.SetVelocityAxis(2, 0f);
        Accelerate(mv, wishDirection, wishSpeed, Vars.Accelerate);
        mv.SetVelocityAxis(2, 0f);

        // Clamp to the max speed, recording the change as acceleration
        var maxSpeed = mv.MaxSpeed;
        var (vx, vy, vz) = (mv.Velocity.X, mv.Velocity.Y, mv.Velocity.Z);
        var speedSquared = ((vx * vx) + (vy * vy)) + (vz * vz);

        if (speedSquared > (maxSpeed * maxSpeed))
        {
            var ratio = maxSpeed / MathF.Sqrt(speedSquared);
            mv.Velocity = new Vector3(vx * ratio, vy * ratio, vz * ratio);

            if (FrameTime > 0f)
            {
                var inverse = 1f / FrameTime;
                mv.ContinuousAcceleration = new Vector3(
                    (((vx * ratio) - vx) * inverse) + mv.ContinuousAcceleration.X,
                    (((vy * ratio) - vy) * inverse) + mv.ContinuousAcceleration.Y,
                    (((vz * ratio) - vz) * inverse) + mv.ContinuousAcceleration.Z);
            }
        }

        PrepareVelocityForMove(mv);

        // Stop when the velocity at the end of the tick would be tiny
        var lookahead = TickInterval - (FrameTime * 0.5f);
        var ex = (lookahead * mv.ContinuousAcceleration.X) + mv.Velocity.X;
        var ey = (lookahead * mv.ContinuousAcceleration.Y) + mv.Velocity.Y;
        var ez = (lookahead * mv.ContinuousAcceleration.Z) + mv.Velocity.Z;

        if (MathF.Sqrt(((ez * ez) + (ey * ey)) + (ex * ex)) < 1f)
        {
            Recorder?.Note("stopped, too slow", mv.AbsOrigin, mv.Velocity);
            mv.ZeroVelocity();
            FinalizeVelocity(mv);
            return;
        }

        // First try moving straight to the destination
        var destination = new Vector3(
            (mv.Velocity.X * FrameTime) + mv.AbsOrigin.X,
            (mv.Velocity.Y * FrameTime) + mv.AbsOrigin.Y,
            mv.AbsOrigin.Z);

        var trace = TracePlayerBBox(mv.AbsOrigin, destination, PlayerMins, PlayerMaxs, Rubikon.Cs2PlayerCollisionFilter);

        mv.OutWishVel = new Vector3(
            (wishDirection.X * wishSpeed) + mv.OutWishVel.X,
            (wishDirection.Y * wishSpeed) + mv.OutWishVel.Y,
            (wishDirection.Z * wishSpeed) + mv.OutWishVel.Z);

        if (trace.Fraction == 1f)
        {
            mv.AbsOrigin = trace.EndPos;
        }
        else
        {
            // Don't walk up stairs if not on ground, nor while jumping out of water
            if ((!hadGround && WaterLevel == 0) || WaterJumpTime != 0f)
            {
                FinalizeVelocity(mv);
                return;
            }

            var startOrigin = mv.AbsOrigin;
            var startVelocity = mv.Velocity;

            TryPlayerMove(mv, (destination, trace));

            var stepped = StepMove(mv, startOrigin, startVelocity);
            Recorder?.Note(stepped ? "step move used" : "step move not used", mv.AbsOrigin, mv.Velocity);

            if (!stepped)
            {
                // Very slow players get a minimum velocity to test step ups with
                var startSpeed = Length(startVelocity);

                if (startSpeed > 0f && Vars.StepMoveVelMin > startSpeed && (mv.ForwardMove != 0f || mv.SideMove != 0f))
                {
                    var scale = Vars.StepMoveVelMin / startSpeed;
                    StepMove(mv, startOrigin, startVelocity * scale);
                }
            }
        }

        FinalizeVelocity(mv);
        StayOnGround(mv);
    }

    /// <summary>
    /// When the wish velocity changes inside a tick, friction keeps using the speed from that moment
    /// until the same fraction of the next tick, so where in the tick the input changed does not change
    /// how much friction there is.
    /// </summary>
    private void UpdateFrictionStash(MoveData mv)
    {
        var expired = false;

        if (UseFrictionStashedSpeed)
        {
            if (UseFrictionStashedSpeedUntilFrac != mv.SubtickStartFraction)
            {
                return;
            }

            expired = true;
            UseFrictionStashedSpeed = false;
        }

        var quantizedSpeed = QuantizedFloat.Velocity.Quantize(MathF.Sqrt((mv.Velocity.X * mv.Velocity.X) + (mv.Velocity.Y * mv.Velocity.Y)));
        var wishChanged = WalkWishVel.X != mv.WalkWishVel.X || WalkWishVel.Y != mv.WalkWishVel.Y;

        if ((expired && FrictionStashedSpeed != quantizedSpeed) || wishChanged)
        {
            UseFrictionStashedSpeed = true;
            UseFrictionStashedSpeedUntilFrac = mv.SubtickStartFraction;
            FrictionStashedSpeed = quantizedSpeed;
            mv.UseFrictionStashedSpeed = true;
        }
    }

    private void AirAccelerate(MoveData mv, Vector3 wishDirection, float wishSpeed, float acceleration)
    {
        if (WaterJumpTime != 0f)
        {
            return;
        }

        var cappedWishSpeed = MathF.Min(wishSpeed, Vars.AirMaxWishSpeed);
        var addSpeed = cappedWishSpeed - Dot(mv.Velocity, wishDirection);

        if (addSpeed <= 0f)
        {
            return;
        }

        var accelerationSpeed = 0f;
        var frametime = FrameTime;

        if (frametime > 0f)
        {
            // Half of it now and half after the move, as for continuous acceleration
            var fullAcceleration = ((wishSpeed * acceleration) * SurfaceFriction) * frametime;
            var halfAcceleration = fullAcceleration * 0.5f;

            if (!(halfAcceleration > addSpeed))
            {
                accelerationSpeed = halfAcceleration;

                var deferred = fullAcceleration > addSpeed ? addSpeed - halfAcceleration : halfAcceleration;
                mv.FrameVelocityDelta = new Vector3(
                    (wishDirection.X * deferred) + mv.FrameVelocityDelta.X,
                    (wishDirection.Y * deferred) + mv.FrameVelocityDelta.Y,
                    (wishDirection.Z * deferred) + mv.FrameVelocityDelta.Z);
            }
            else
            {
                accelerationSpeed = addSpeed;
            }
        }

        mv.OutWishVel = new Vector3(
            (wishDirection.X * accelerationSpeed) + mv.OutWishVel.X,
            (wishDirection.Y * accelerationSpeed) + mv.OutWishVel.Y,
            (wishDirection.Z * accelerationSpeed) + mv.OutWishVel.Z);

        mv.Velocity = new Vector3(
            (wishDirection.X * accelerationSpeed) + mv.Velocity.X,
            (wishDirection.Y * accelerationSpeed) + mv.Velocity.Y,
            (wishDirection.Z * accelerationSpeed) + mv.Velocity.Z);
    }

    private void AddGravity(MoveData mv)
    {
        if (WaterJumpTime != 0f)
        {
            return;
        }

        var gravity = Vars.Gravity * GravityScale;
        mv.Velocity.Z -= gravity * FrameTime;
        mv.ContinuousAcceleration.Z -= gravity;

        // Vertical base velocity acts as an acceleration
        var baseZ = BaseVelocity.Z;
        mv.Velocity.Z = (baseZ * FrameTime) + mv.Velocity.Z;
        mv.ContinuousAcceleration.Z = baseZ + mv.ContinuousAcceleration.Z;
        BaseVelocity.Z = 0f;

        CheckVelocity(mv);
    }

    /// <summary>Flies without touching anything: accelerates, then moves the whole way.</summary>
    private void FullNoClipMove(MoveData mv, float maxSpeed, float acceleration)
    {
        NoClipAccelerate(mv, maxSpeed, acceleration);

        var frametime = FrameTime;
        mv.AbsOrigin.X = (frametime * mv.Velocity.X) + mv.AbsOrigin.X;
        mv.AbsOrigin.Y = (frametime * mv.Velocity.Y) + mv.AbsOrigin.Y;
        mv.AbsOrigin.Z = (frametime * mv.Velocity.Z) + mv.AbsOrigin.Z;

        if (acceleration < 0f)
        {
            mv.ZeroVelocity();
        }
    }

    /// <summary>
    /// Accelerates towards the input in the full 3D view direction, and bleeds speed as friction does on
    /// the ground. Without acceleration the velocity is the input itself.
    /// </summary>
    private void NoClipAccelerate(MoveData mv, float maxSpeed, float acceleration)
    {
        if (Buttons.IsHeld(InputButtons.Speed))
        {
            maxSpeed *= 0.5f;
        }

        var move = new Vector3(mv.ForwardMove, mv.SideMove, mv.UpMove);
        VectorNormalize(ref move);
        move = new Vector3(move.X * maxSpeed, move.Y * maxSpeed, move.Z * maxSpeed);

        AngleVectors(mv.ViewAngles, out var forward, out var left, out _);
        VectorNormalize(ref forward);
        VectorNormalize(ref left);

        var wishVelocity = new Vector3(
            (left.X * move.Y) + (forward.X * move.X),
            (left.Y * move.Y) + (forward.Y * move.X),
            ((left.Z * move.Y) + (forward.Z * move.X)) + move.Z);

        var wishDirection = wishVelocity;
        var wishSpeed = VectorNormalize(ref wishDirection);

        if (!(acceleration > 0f))
        {
            mv.Velocity = wishVelocity;
            return;
        }

        Accelerate(mv, wishDirection, wishSpeed, acceleration);

        var speed = Length(mv.Velocity);

        if (!(speed >= 1f))
        {
            mv.ZeroVelocity();
            return;
        }

        var control = MathF.Max(Vars.MaxSpeed * 0.25f, speed);
        var newSpeed = MathF.Max(0f, speed - (((Vars.Friction * SurfaceFriction) * control) * FrameTime)) / speed;
        mv.Velocity = new Vector3(newSpeed * mv.Velocity.X, newSpeed * mv.Velocity.Y, newSpeed * mv.Velocity.Z);
    }

    private void AirMove(MoveData mv)
    {
        CheckVelocity(mv);

        AngleVectors(mv.ViewAngles, out var forward, out var left, out _);

        // The top of the jump arc: the vertical velocity crosses zero during this step
        var atApex = false;

        if (JumpApexPending && !OnGround && mv.Velocity.Z <= 0f)
        {
            atApex = mv.Velocity.Z > -(TickInterval * (Vars.Gravity * 0.5f));
            JumpApexPending = false;
        }

        var forwardMove = mv.ForwardMove;
        var sideMove = mv.SideMove;

        forward.Z = 0f;
        left.Z = 0f;
        VectorNormalize(ref forward);
        VectorNormalize(ref left);

        var wishDirection = new Vector3((forward.X * forwardMove) + (left.X * sideMove), (left.Y * sideMove) + (forward.Y * forwardMove), 0f);
        var wishSpeed = VectorNormalize(ref wishDirection);

        if (wishSpeed != 0f)
        {
            wishSpeed = MathF.Min(mv.MaxSpeed, wishSpeed);
        }

        AirAccelerate(mv, wishDirection, wishSpeed, Vars.AirAccelerate);
        AddGravity(mv);
        PrepareVelocityForMove(mv);

        if (!Vars.LegacyJump)
        {
            RecordPreAirMove(mv);
        }

        var speed2D = MathF.Sqrt((mv.Velocity.X * mv.Velocity.X) + (mv.Velocity.Y * mv.Velocity.Y));

        if (atApex && speed2D > 0f)
        {
            // At the apex the move goes horizontally then vertically, so an edge the player is exactly
            // level with cannot catch them
            var savedVelocity = mv.Velocity;
            var savedAcceleration = mv.ContinuousAcceleration;
            var savedDelta = mv.FrameVelocityDelta;
            var savedZ = mv.Velocity.Z;

            Recorder?.Note("apex: horizontal then vertical move", mv.AbsOrigin, mv.Velocity);
            mv.Velocity.Z = 0f;

            if ((speed2D * FrameTime) < 0.09375f)
            {
                var scale = (1f / FrameTime) * 0.09375f;
                mv.Velocity = new Vector3(wishDirection.X * scale, wishDirection.Y * scale, wishDirection.Z * scale);
            }

            TryPlayerMove(mv, null);

            mv.Velocity = new Vector3(0f, 0f, savedZ);
            TryPlayerMove(mv, null);

            mv.Velocity = savedVelocity;
            mv.ContinuousAcceleration = savedAcceleration;
            mv.FrameVelocityDelta = savedDelta;
        }
        else
        {
            TryPlayerMove(mv, null);
        }

        FinalizeVelocity(mv);
    }

    /// <summary>Slides the player along whatever they hit, up to four times.</summary>
    /// <param name="mv">The move.</param>
    /// <param name="firstMove">A first trace already made by the caller, reused if the first sweep matches it.</param>
    private void TryPlayerMove(MoveData mv, (Vector3 Destination, GameTrace Trace)? firstMove)
    {
        var planeCount = 0;
        var planesSinceMove = 0;
        Span<Vector3> planes = stackalloc Vector3[MaxClipPlanes];

        var primalVelocity = mv.Velocity;
        var originalVelocity = mv.Velocity;
        var initialVelocity = mv.Velocity;

        var airborne = !OnGround;
        var jumpPrecision = Vars.JumpPrecision;

        var allFraction = 0f;
        var timeLeft = FrameTime;

        // Moves shorter than this are treated as no move, scaled by the starting speed
        var minMoveSpeed = MathF.Max(1f, Length(mv.Velocity));

        var mins = PlayerMins;
        var maxs = PlayerMaxs;

        for (var bump = 0; bump < 4; bump++)
        {
            var speed = Length(mv.Velocity);

            if (speed == 0f)
            {
                break;
            }

            Vector3 end;

            if (jumpPrecision && airborne && timeLeft != 0f)
            {
                // Airborne heights stay on a 1/64 grid, the rounding carried into the next move, so jump
                // heights do not depend on the sub-tick timing
                end.X = ((mv.Velocity.X * timeLeft) + 0f) + mv.AbsOrigin.X;
                end.Y = ((mv.Velocity.Y * timeLeft) + 0f) + mv.AbsOrigin.Y;

                var desired = (mv.Velocity.Z * timeLeft) + AccumulatedJumpError;
                var snapped = TickFraction.RoundCoord64(mv.Velocity.Z * timeLeft);
                var big = snapped + 196608f;
                end.Z = (((desired - snapped) + big) + mv.AbsOrigin.Z) - 196608f;

                AccumulatedJumpError = desired - (end.Z - mv.AbsOrigin.Z);
            }
            else
            {
                end = new Vector3(
                    (mv.Velocity.X * timeLeft) + mv.AbsOrigin.X,
                    (mv.Velocity.Y * timeLeft) + mv.AbsOrigin.Y,
                    (mv.Velocity.Z * timeLeft) + mv.AbsOrigin.Z);
            }

            // Sliding along a single plane: aim 1/32 off it
            if (planeCount == 1)
            {
                end = new Vector3((planes[0].X * 0.03125f) + end.X, (planes[0].Y * 0.03125f) + end.Y, (planes[0].Z * 0.03125f) + end.Z);
            }

            var trace = firstMove is { } first && end == first.Destination
                ? first.Trace
                : TracePlayerBBox(mv.AbsOrigin, end, mins, maxs, Rubikon.Cs2PlayerCollisionFilter);

            var moveDistance = speed * timeLeft;
            var fraction = trace.Fraction;
            var fractionZeroed = false;

            // A first move that would get less than 1/32 of a unit is no move at all
            if (allFraction == 0f && fraction < 1f && moveDistance >= 0.03125f && (fraction * moveDistance) < 0.03125f)
            {
                trace.Fraction = 0f;
                fraction = 0f;
                fractionZeroed = true;
            }

            var moved = (fraction * minMoveSpeed) > 0.03125f;

            if (moved)
            {
                planesSinceMove = 0;
                planeCount = 0;
                originalVelocity = mv.Velocity;
                mv.AbsOrigin = trace.EndPos;
            }

            allFraction += fraction;

            var clip = new ClipRecord
            {
                Bump = bump,
                TimeLeft = timeLeft,
                Fraction = fraction,
                FractionZeroed = fractionZeroed,
                Moved = moved,
                Origin = mv.AbsOrigin,
                Normal = trace.Normal,
                VelocityIn = mv.Velocity,
            };

            if (fraction == 1f)
            {
                RecordClip(clip, ClipOutcome.Finished, mv.Velocity, planeCount);
                break;
            }

            mv.AddToTouched(trace);

            timeLeft -= trace.Fraction * timeLeft;

            // Out of planes, or landed on something standable with no horizontal speed left: stop
            if (planeCount >= MaxClipPlanes
                || (airborne && mv.Velocity.Z < 0f
                    && trace.Normal.Z >= Vars.StandableNormal
                    && IsValidGroundTrace(trace)
                    && MathF.Sqrt((mv.Velocity.X * mv.Velocity.X) + (mv.Velocity.Y * mv.Velocity.Y)) < 1f))
            {
                mv.ZeroVelocity();
                RecordClip(clip, planeCount >= MaxClipPlanes ? ClipOutcome.StoppedOutOfPlanes : ClipOutcome.StoppedLanded, mv.Velocity, planeCount);
                break;
            }

            planes[planeCount] = trace.Normal;
            planesSinceMove++;
            planeCount++;

            // Only the first impact while airborne reflects: anything more gets stuck in acute corners
            if (planeCount == 1 && ActualMoveType == MoveType.Walk && !OnGround)
            {
                float backoff;

                if (!(planes[0].Z > Vars.WalkableNormal))
                {
                    // A steep surface while airborne: surfing, which may bounce
                    var overbounce = ((1f - SurfaceFriction) * Vars.Bounce) + 1f;
                    backoff = -Dot(planes[0], originalVelocity) * overbounce;
                }
                else
                {
                    backoff = -Dot(planes[0], originalVelocity);
                }

                backoff = MathF.Max(backoff, 0f) + 0.03125f;
                mv.Velocity = new Vector3(
                    (planes[0].X * backoff) + originalVelocity.X,
                    (planes[0].Y * backoff) + originalVelocity.Y,
                    (planes[0].Z * backoff) + originalVelocity.Z);
                originalVelocity = mv.Velocity;

                RecordClip(clip, ClipOutcome.Reflected, mv.Velocity, planeCount);
            }
            else
            {
                var outcome = ClipOutcome.Clipped;
                int i;

                for (i = 0; i < planeCount; i++)
                {
                    var backoff = MathF.Max(-Dot(planes[i], originalVelocity), 0f) + 0.03125f;
                    mv.Velocity = new Vector3(
                        (backoff * planes[i].X) + originalVelocity.X,
                        (backoff * planes[i].Y) + originalVelocity.Y,
                        (backoff * planes[i].Z) + originalVelocity.Z);

                    int j;

                    for (j = 0; j < planeCount; j++)
                    {
                        if (j != i && Dot(mv.Velocity, planes[j]) < 0f)
                        {
                            break;
                        }
                    }

                    if (j == planesSinceMove)
                    {
                        break;
                    }
                }

                if (i == planesSinceMove)
                {
                    // Go along the crease
                    if (planeCount != 2)
                    {
                        mv.ZeroVelocity();
                        RecordClip(clip, ClipOutcome.StoppedCorner, mv.Velocity, planeCount);
                        break;
                    }

                    outcome = ClipOutcome.Crease;

                    var direction = new Vector3(
                        (planes[0].Y * planes[1].Z) - (planes[0].Z * planes[1].Y),
                        (planes[0].Z * planes[1].X) - (planes[0].X * planes[1].Z),
                        (planes[0].X * planes[1].Y) - (planes[0].Y * planes[1].X));
                    VectorNormalize(ref direction);

                    var along = Dot(mv.Velocity, direction);
                    mv.Velocity = new Vector3(
                        ((planes[1].X * 0f) + (planes[0].X * 0f)) + (direction.X * along),
                        ((planes[1].Y * 0f) + (planes[0].Y * 0f)) + (direction.Y * along),
                        ((planes[1].Z * 0f) + (planes[0].Z * 0f)) + (direction.Z * along));
                }

                // Turned back against the original velocity: stop dead to avoid oscillating in corners
                if (Dot(mv.Velocity, primalVelocity) <= 0f)
                {
                    RecordClip(clip, ClipOutcome.StoppedReversed, mv.Velocity, planeCount);
                    mv.ZeroVelocity();
                    break;
                }

                RecordClip(clip, outcome, mv.Velocity, planeCount);
            }
        }

        if (allFraction == 0f && planesSinceMove > 0)
        {
            Recorder?.Note("no progress, velocity zeroed", mv.AbsOrigin, mv.Velocity);
            mv.ZeroVelocity();
        }

        // Slammed into a wall
        var lateralStop = Length2D(initialVelocity) - Length2D(mv.Velocity);

        if (lateralStop > PlayerMaxSafeFallSpeed * 2f)
        {
            PlayerRoughLandingEffects(mv, 1f);
        }
        else if (lateralStop > PlayerMaxSafeFallSpeed)
        {
            PlayerRoughLandingEffects(mv, 0.85f);
        }
    }

    /// <summary>
    /// After a plain slide, tries the same move lifted by a step and set back down, and keeps whichever
    /// went further along the original velocity.
    /// </summary>
    /// <returns>Whether the stepped move was used.</returns>
    private bool StepMove(MoveData mv, Vector3 startOrigin, Vector3 startVelocity)
    {
        var downPosition = mv.AbsOrigin;
        var downVelocity = mv.Velocity;

        mv.AbsOrigin = startOrigin;
        mv.Velocity = startVelocity;

        var stepSize = Vars.StepSize;
        var mins = PlayerMins;
        var maxs = PlayerMaxs;

        // Move up a stair height, plus 1/32
        var end = mv.AbsOrigin with { Z = mv.AbsOrigin.Z + (stepSize + 0.03125f) };
        var trace = TracePlayerBBox(mv.AbsOrigin, end, mins, maxs, Rubikon.Cs2PlayerCollisionFilter);

        if (trace.Fraction > 0f)
        {
            mv.AbsOrigin = trace.EndPos;
        }

        TryPlayerMove(mv, null);

        // Move down a stair
        end = mv.AbsOrigin with { Z = mv.AbsOrigin.Z - (stepSize + 0.0625f) };
        trace = TracePlayerBBox(mv.AbsOrigin, end, mins, maxs, Rubikon.Cs2PlayerCollisionFilter);

        var validStep = false;

        if (trace.Fraction < 1f || trace.StartSolid)
        {
            validStep = true;
            var walkable = Vars.WalkableNormal;

            if (walkable > trace.Normal.Z)
            {
                // A steep surface below. Slow players get a second chance: a slightly narrower hull a
                // bit further down, then a unit along the velocity and back down.
                validStep = false;

                if (64f > Length(startVelocity))
                {
                    var narrowMins = new Vector3(mins.X + 2f, mins.Y + 2f, mins.Z);
                    var narrowMaxs = new Vector3(maxs.X - 2f, maxs.Y - 2f, maxs.Z);
                    end.Z -= stepSize * 0.5f;
                    var narrow = TracePlayerBBox(mv.AbsOrigin, end, narrowMins, narrowMaxs, Rubikon.Cs2PlayerCollisionFilter);

                    if ((narrow.Fraction < 1f || narrow.StartSolid) && narrow.Normal.Z >= walkable)
                    {
                        var direction = mv.Velocity;
                        VectorNormalize(ref direction);

                        var forwardStart = mv.AbsOrigin;
                        var forwardEnd = new Vector3(forwardStart.X + direction.X, direction.Y + forwardStart.Y, direction.Z + forwardStart.Z);
                        var ahead = TracePlayerBBox(forwardStart, forwardEnd, mins, maxs, Rubikon.Cs2PlayerCollisionFilter);

                        if (ahead.Fraction > 0f)
                        {
                            var downEnd = new Vector3(ahead.EndPos.X - 0f, ahead.EndPos.Y - 0f, ahead.EndPos.Z - (stepSize + 0.0625f));
                            var below = TracePlayerBBox(ahead.EndPos, downEnd, mins, maxs, Rubikon.Cs2PlayerCollisionFilter);

                            if (below.DidHit)
                            {
                                validStep = below.Normal.Z >= walkable;
                            }
                        }
                    }
                }
            }
        }

        bool usedStep;

        if ((trace.Fraction < 1f || trace.StartSolid) && validStep && trace.Fraction > 0f)
        {
            mv.AbsOrigin = trace.EndPos;

            // Use whichever went further along the original velocity
            var downDistance = ((downPosition.X - startOrigin.X) * startVelocity.X) + ((downPosition.Y - startOrigin.Y) * startVelocity.Y);
            var upDistance = ((trace.EndPos.X - startOrigin.X) * startVelocity.X) + ((trace.EndPos.Y - startOrigin.Y) * startVelocity.Y);

            if (downDistance > upDistance)
            {
                mv.AbsOrigin = downPosition;
                mv.Velocity = mv.Velocity with { X = downVelocity.X, Y = downVelocity.Y };
            }

            usedStep = true;
        }
        else
        {
            mv.AbsOrigin = downPosition;
            mv.Velocity = mv.Velocity with { X = downVelocity.X, Y = downVelocity.Y };
            usedStep = false;
        }

        // Z comes from the plain slide
        mv.Velocity.Z = downVelocity.Z;
        return usedStep;
    }

    private void RecordClip(in ClipRecord clip, ClipOutcome outcome, Vector3 velocityOut, int planeCount)
        => Recorder?.Clip(clip with { Outcome = outcome, VelocityOut = velocityOut, PlaneCount = planeCount });

    /// <summary>Keeps the player on the ground going down slopes and stairs.</summary>
    private void StayOnGround(MoveData mv)
    {
        var start = mv.AbsOrigin with { Z = mv.AbsOrigin.Z + 2f };
        var end = mv.AbsOrigin with { Z = mv.AbsOrigin.Z - Vars.StepSize };
        var mins = PlayerMins;
        var maxs = PlayerMaxs;

        // See how far up the player can go without getting stuck, then trace down from there
        var trace = TracePlayerBBox(mv.AbsOrigin, start, mins, maxs, Rubikon.Cs2PlayerCollisionFilter);
        start = trace.EndPos;

        trace = TracePlayerBBox(start, end, mins, maxs, Rubikon.Cs2PlayerCollisionFilter);

        if (trace.Fraction > 0f && trace.Fraction < 1f && !trace.StartSolid && trace.Normal.Z >= Vars.WalkableNormal)
        {
            // Smaller differences than a network unit are left alone
            if (MathF.Abs(mv.AbsOrigin.Z - trace.EndPos.Z) > 0.015625f)
            {
                Recorder?.Note("snapped down", mv.AbsOrigin, mv.Velocity, trace.EndPos.Z - mv.AbsOrigin.Z);
                mv.AbsOrigin = trace.EndPos;
            }
        }
    }

    private GameTrace TracePlayerBBox(Vector3 start, Vector3 end, Vector3 mins, Vector3 maxs, string collisionName,
        [CallerMemberName] string caller = "")
    {
        var trace = Collision.TraceHull(start, end, mins, maxs, collisionName);
        Recorder?.Trace(caller, collisionName, start, end, mins, maxs, trace, Collision.LastHitShape);
        return trace;
    }
}
