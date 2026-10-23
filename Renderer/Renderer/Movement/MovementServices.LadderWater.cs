namespace ValveResourceFormat.Renderer.Movement;

internal sealed partial class MovementServices
{
    private const float MaxClimbSpeed = 200f;
    private const float LadderJumpSpeed = 270f;
    private const float WaterJumpHeight = 8f;

    private float ClimbSpeed => Buttons.IsHeld(InputButtons.Duck | InputButtons.Speed) ? DuckSpeedModifier * MaxClimbSpeed : MaxClimbSpeed;

    private bool IsLadderTrace(in GameTrace trace)
        => trace.Normal.Z != 1f && (trace.HitLadder || Surfaces.Get(trace.SurfacePropertyHash).IsLadder);

    /// <summary>
    /// Finds a ladder ahead, or one just walked off the top of, and turns the input into climbing it.
    /// </summary>
    /// <returns>Whether the player is on a ladder.</returns>
    private bool LadderMove(MoveData mv)
    {
        if (ActualMoveType == MoveType.NoClip)
        {
            return false;
        }

        // Already on a ladder keep pressing into it, else look for one where the player is heading
        Vector3 wishDirection;

        if (ActualMoveType == MoveType.Ladder)
        {
            wishDirection = -LadderNormal;
        }
        else
        {
            var forwardMove = mv.ForwardMove;

            if (forwardMove == 0f && mv.SideMove == 0f)
            {
                return false;
            }

            wishDirection = new Vector3(
                (mv.SideMove * Left.X) + (forwardMove * Forward.X),
                (mv.SideMove * Left.Y) + (forwardMove * Forward.Y),
                (mv.SideMove * Left.Z) + (forwardMove * Forward.Z));
            VectorNormalize(ref wishDirection);
        }

        var distance = ActualMoveType == MoveType.Ladder ? 10f : 2f;
        var end = new Vector3(
            (wishDirection.X * distance) + mv.AbsOrigin.X,
            (wishDirection.Y * distance) + mv.AbsOrigin.Y,
            (wishDirection.Z * distance) + mv.AbsOrigin.Z);

        var mins = PlayerMins;
        var maxs = PlayerMaxs;
        var trace = TracePlayerBBox(mv.AbsOrigin, end, mins, maxs, Rubikon.LadderCollisionName);

        if (trace.Fraction == 1f || !IsLadderTrace(trace))
        {
            // Not pressing into a ladder: grab one just walked off the top of, looking back along the velocity
            if (!HasWalkMovedSinceLastJump || ActualMoveType == MoveType.Ladder || OnGround)
            {
                return false;
            }

            var velocityZ = mv.Velocity.Z;

            if (!(velocityZ <= 0f) || !(velocityZ > -50f) || !(MathF.Abs(mv.Velocity.X) > 0f) || !(MathF.Abs(mv.Velocity.Y) > 0f))
            {
                return false;
            }

            var start = mv.AbsOrigin with { Z = mv.AbsOrigin.Z + -6f };

            var direction = mv.Velocity;
            VectorNormalize(ref direction);

            var back = new Vector3(
                mv.AbsOrigin.X - (direction.X * 24f),
                mv.AbsOrigin.Y - (direction.Y * 24f),
                mv.AbsOrigin.Z - (direction.Z * 24f));

            trace = TracePlayerBBox(start, back, mins, maxs, Rubikon.LadderCollisionName);

            if (trace.Fraction == 1f || !IsLadderTrace(trace) || trace.Normal.Z == 1f || trace.StartSolid)
            {
                return false;
            }

            MoveType = MoveType.Ladder;
            LadderNormal = trace.Normal;
            mv.ZeroVelocity();

            // Snap back onto the ladder if there is room
            var solid = TracePlayerBBox(start, back, mins, maxs, Rubikon.Cs2PlayerCollisionFilter);

            if (!solid.StartSolid && !TestPlayerPosition(solid.EndPos, out _))
            {
                mv.AbsOrigin = solid.EndPos;
            }
        }

        if (ActualMoveType != MoveType.Ladder)
        {
            // Jumping off is ignored for a moment after mounting
            IgnoreLadderJumpTime = CurTime + 0.2f;
        }

        MoveType = MoveType.Ladder;
        LadderNormal = trace.Normal;

        var onFloor = OnGround;

        if (!onFloor)
        {
            var floor = mv.AbsOrigin with { Z = mv.AbsOrigin.Z + (PlayerMins.Z - 1f) };
            var floorTrace = TracePlayerBBox(mv.AbsOrigin, floor, mins, maxs, Rubikon.Cs2PlayerCollisionFilter);

            if (floorTrace.DidHit && !floorTrace.StartSolid && floorTrace.Normal.Z > Vars.StandableNormal)
            {
                onFloor = true;
            }
        }

        GravityScale = 1f;

        var climbSpeed = ClimbSpeed;
        var forwardSpeed = 0f;
        var leftSpeed = 0f;

        if (Buttons.IsHeld(InputButtons.Back))
        {
            forwardSpeed = 0f - climbSpeed;
        }

        if (Buttons.IsHeld(InputButtons.Forward))
        {
            forwardSpeed += climbSpeed;
        }

        if (Buttons.IsHeld(InputButtons.MoveLeft))
        {
            leftSpeed = climbSpeed + 0f;
        }

        if (Buttons.IsHeld(InputButtons.MoveRight))
        {
            leftSpeed -= climbSpeed;
        }

        if (Buttons.IsHeld(InputButtons.Jump) || WasButtonPressed(InputButtons.Jump))
        {
            if (!(CurTime < IgnoreLadderJumpTime))
            {
                MoveType = MoveType.Walk;
                mv.Velocity = trace.Normal * LadderJumpSpeed;
            }
        }
        else if (forwardSpeed == 0f && leftSpeed == 0f)
        {
            mv.ZeroVelocity();
        }
        else
        {
            var n = trace.Normal;

            var velocity = new Vector3(
                (forwardSpeed * Forward.X) + (leftSpeed * Left.X),
                (leftSpeed * Left.Y) + (forwardSpeed * Forward.Y),
                (leftSpeed * Left.Z) + (forwardSpeed * Forward.Z));

            // Horizontal tangent of the ladder face
            var perp = new Vector3((n.Z * 0f) - n.Y, n.X - (n.Z * 0f), (n.Y * 0f) - (n.X * 0f));
            VectorNormalize(ref perp);

            // Split the input into the part pushing into the face and the part along it
            var normal = Dot(n, velocity);
            var cross = new Vector3(n.X * normal, n.Y * normal, n.Z * normal);
            var lateral = new Vector3(velocity.X - cross.X, velocity.Y - cross.Y, velocity.Z - cross.Z);

            // Up the face, in its plane
            var up = new Vector3((n.Y * perp.Z) - (n.Z * perp.Y), (perp.X * n.Z) - (n.X * perp.Z), (n.X * perp.Y) - (perp.X * n.Y));

            var upDistance = ((up.Z * lateral.Z) + (up.Y * lateral.Y)) + (up.X * lateral.X);
            var perpDistance = ((perp.Z * lateral.Z) + (perp.Y * lateral.Y)) + (perp.X * lateral.X);

            var angleVector = new Vector3((perp.X * perpDistance) + cross.X, (perp.Y * perpDistance) + cross.Y, (perp.Z * perpDistance) + cross.Z);
            VectorNormalize(ref angleVector);

            // Input mostly sideways across the face is damped
            var angleDot = ((n.Y * angleVector.Y) + (n.Z * angleVector.Z)) + (angleVector.X * n.X);

            if (Vars.LadderAngle > angleDot)
            {
                var dampen = Vars.LadderDampen;
                lateral = new Vector3(
                    ((perp.X * dampen) * perpDistance) + (upDistance * up.X),
                    ((perp.Y * dampen) * perpDistance) + (upDistance * up.Y),
                    ((perp.Z * dampen) * perpDistance) + (upDistance * up.Z));
            }

            // Pressing into the face climbs it
            mv.Velocity = new Vector3(lateral.X - (up.X * normal), lateral.Y - (up.Y * normal), lateral.Z - (up.Z * normal));

            var scale = Vars.LadderScaleSpeed;

            if (scale > 0f)
            {
                mv.Velocity *= scale;
            }

            // On the floor and moving away from the ladder
            if (onFloor && normal > 0f)
            {
                mv.Velocity = new Vector3((n.X * MaxClimbSpeed) + mv.Velocity.X, (n.Y * MaxClimbSpeed) + mv.Velocity.Y, (n.Z * MaxClimbSpeed) + mv.Velocity.Z);
            }
        }

        return true;
    }

    /// <summary>Sets how deep in water the player is, from a line down from the top of the hull to the origin.</summary>
    /// <returns>Whether the player is deeper than ankle deep.</returns>
    private bool CheckWater(MoveData mv)
    {
        var top = new Vector3(mv.AbsOrigin.X + 0f, mv.AbsOrigin.Y + 0f, PlayerMaxs.Z + mv.AbsOrigin.Z);
        var fraction = Collision.TraceWater(top, mv.AbsOrigin);

        var level = 0f;

        if (fraction < 1f)
        {
            level = 1f - fraction;
            level = 0f > level ? 0f : MathF.Min(1f, level);
        }

        WaterLevelFraction = QuantizedFloat.WaterLevel.Quantize(level);

        return WaterLevel > 1;
    }

    /// <summary>Swimming against a ledge pops the player up out of the water onto it.</summary>
    private void CheckWaterJump(MoveData mv)
    {
        if (WaterJumpTime != 0f || mv.Velocity.Z < -180f)
        {
            return;
        }

        var flatVelocity = mv.Velocity with { Z = 0f };
        var speed = VectorNormalize(ref flatVelocity);

        var flatForward = Forward with { Z = 0f };
        VectorNormalize(ref flatForward);

        // Backing into water from steps should not pop the player forward
        if (speed != 0f && Dot(flatVelocity, flatForward) < 0f)
        {
            return;
        }

        var mins = PlayerMins;
        var maxs = PlayerMaxs;

        var start = new Vector3(
            ((maxs.X + mins.X) * 0.5f) + mv.AbsOrigin.X,
            ((maxs.Y + mins.Y) * 0.5f) + mv.AbsOrigin.Y,
            ((maxs.Z + mins.Z) * 0.5f) + mv.AbsOrigin.Z);
        var end = new Vector3((flatForward.X * 24f) + start.X, (flatForward.Y * 24f) + start.Y, (flatForward.Z * 24f) + start.Z);

        var trace = TracePlayerBBox(start, end, mins, maxs, Rubikon.Cs2PlayerCollisionFilter);

        if (trace.Fraction >= 1f)
        {
            return;
        }

        start.Z = (ViewOffsetZ + mv.AbsOrigin.Z) + WaterJumpHeight;
        end = new Vector3((flatForward.X * 24f) + start.X, (flatForward.Y * 24f) + start.Y, (flatForward.Z * 24f) + start.Z);

        WaterJumpVelocity = trace.Normal * -50f;

        trace = TracePlayerBBox(start, end, mins, maxs, Rubikon.Cs2PlayerCollisionFilter);

        if (trace.Fraction != 1f)
        {
            return;
        }

        // Something to land on
        start = end;
        end.Z += -1024f;
        trace = TracePlayerBBox(start, end, mins, maxs, Rubikon.Cs2PlayerCollisionFilter);

        if (trace.Fraction < 1f && !(trace.Normal.Z < Vars.StandableNormal))
        {
            mv.SetVelocityAxis(2, 256f);
            Flags |= PlayerFlags.WaterJump;
            WaterJumpTime = 2000f;
        }
    }

    private void FullWaterMove(MoveData mv)
    {
        if (!MoveInit(mv))
        {
            return;
        }

        if (WaterLevel == 3)
        {
            CheckWaterJump(mv);
        }

        // Falling again means no longer jumping out of the water
        if (mv.Velocity.Z < 0f && WaterJumpTime != 0f)
        {
            WaterJumpTime = 0f;
        }

        Duck(mv);
        CheckJumpButton(mv);
        WaterMove(mv);
        CategorizePosition(mv, stayOnGround: false);

        if (OnGround)
        {
            mv.SetVelocityAxis(2, 0f);
        }

        if (!IsWalking && !Ducked)
        {
            var inWater = WaterLevel != 0;

            if ((OldWaterLevel > 0) != inWater)
            {
                Effects?.PlayWaterSound(mv.AbsOrigin, inWater ? "Water.PlayerEnter" : "Water.PlayerExit");
            }
        }

        PostMove(mv, retryMove: false);
    }

    /// <summary>Jump held in deep water swims straight up.</summary>
    private void SwimUp(MoveData mv)
    {
        SetGroundEntity(mv, null);
        mv.SetVelocityAxis(2, 100f);

        if (SwimSoundTime <= 0f)
        {
            SwimSoundTime = 1000f;

            if (!IsWalking && !Ducked)
            {
                Effects?.PlayWaterSound(mv.AbsOrigin, "Player.Swim");
            }
        }
    }

    private void WaterMove(MoveData mv)
    {
        AngleVectors(mv.ViewAngles, out var forward, out var left, out _);

        var forwardMove = mv.ForwardMove;
        var sideMove = mv.SideMove;

        var wishVelocity = new Vector3((forward.X * forwardMove) + (left.X * sideMove), (sideMove * left.Y) + (forwardMove * forward.Y), 0f);
        var baseZ = (sideMove * left.Z) + (forwardMove * forward.Z);

        if (Buttons.IsHeld(InputButtons.Jump))
        {
            wishVelocity.Z = baseZ + mv.ClientMaxSpeed;
        }
        else if (forwardMove == 0f && sideMove == 0f && mv.UpMove == 0f)
        {
            // Sink when doing nothing
            wishVelocity.Z = -60f;
        }
        else
        {
            // Upward movement along forward is exaggerated
            var upward = (forwardMove * forward.Z) + (forwardMove * forward.Z);
            upward = 0f > upward ? 0f : MathF.Min(mv.ClientMaxSpeed, upward);
            wishVelocity.Z = (upward + mv.UpMove) + baseZ;
        }

        var wishDirection = wishVelocity;
        var wishSpeed = VectorNormalize(ref wishDirection);

        if (wishSpeed > mv.MaxSpeed)
        {
            wishVelocity *= mv.MaxSpeed / wishSpeed;
            wishSpeed = mv.MaxSpeed;
        }

        wishSpeed *= 0.80000001f;

        // Water friction
        var velocityDirection = mv.Velocity;
        var speed = VectorNormalize(ref velocityDirection);
        float newSpeed;

        if (speed == 0f)
        {
            newSpeed = 0f;
        }
        else
        {
            newSpeed = speed - (SurfaceFriction * ((speed * FrameTime) * Vars.Friction));

            if (newSpeed < 0.1f)
            {
                newSpeed = 0f;
            }

            mv.Velocity *= newSpeed / speed;
        }

        if (!(wishSpeed < 0.1f))
        {
            var addSpeed = wishSpeed - newSpeed;

            if (addSpeed > 0f)
            {
                VectorNormalize(ref wishVelocity);
                var accelerationSpeed = FrameTime * (wishSpeed * Vars.Accelerate);
                accelerationSpeed = MathF.Min(addSpeed, accelerationSpeed * SurfaceFriction);

                mv.Velocity += wishVelocity * accelerationSpeed;
                mv.OutWishVel += wishVelocity * accelerationSpeed;
            }
        }

        PrepareVelocityForMove(mv);

        // Assume a stair or a slope, so press down from a step above
        var destination = new Vector3(
            (mv.Velocity.X * FrameTime) + mv.AbsOrigin.X,
            (FrameTime * mv.Velocity.Y) + mv.AbsOrigin.Y,
            (FrameTime * mv.Velocity.Z) + mv.AbsOrigin.Z);

        var mins = PlayerMins;
        var maxs = PlayerMaxs;
        var trace = TracePlayerBBox(mv.AbsOrigin, destination, mins, maxs, Rubikon.Cs2PlayerCollisionFilter);

        if (trace.Fraction == 1f)
        {
            var start = destination with { Z = (Vars.StepSize + 1f) + destination.Z };
            trace = TracePlayerBBox(start, destination, mins, maxs, Rubikon.Cs2PlayerCollisionFilter);

            if (!trace.StartSolid)
            {
                // Walked up the step
                mv.AbsOrigin = trace.EndPos;
                FinalizeVelocity(mv);
                return;
            }

            TryPlayerMove(mv, null);
        }
        else
        {
            if (!OnGround)
            {
                TryPlayerMove(mv, null);
                FinalizeVelocity(mv);
                return;
            }

            var startOrigin = mv.AbsOrigin;
            var startVelocity = mv.Velocity;
            TryPlayerMove(mv, (destination, trace));

            if (!StepMove(mv, startOrigin, startVelocity))
            {
                var startSpeed = Length(startVelocity);

                if (startSpeed > 0f && Vars.StepMoveVelMin > startSpeed && (mv.ForwardMove != 0f || mv.SideMove != 0f))
                {
                    StepMove(mv, startOrigin, startVelocity * (Vars.StepMoveVelMin / startSpeed));
                }
            }
        }

        FinalizeVelocity(mv);
    }

    /// <summary>Counts down to the next footstep and plays it when due.</summary>
    private void UpdateStepSound(uint surfacePropertyHash, Vector3 origin, Vector3 velocity)
    {
        // Walking pace and the walk key are silent
        if (ActualMoveType == MoveType.Walk)
        {
            var speedSquared = ((velocity.X * velocity.X) + (velocity.Y * velocity.Y)) + (velocity.Z * velocity.Z);

            if (((WalkSpeedModifier * MaxSpeedCap) * (WalkSpeedModifier * MaxSpeedCap)) > speedSquared || IsWalking)
            {
                if (speedSquared < 10f)
                {
                    SetStepSoundTime(StepSoundKind.Normal, walking: false, velocity);
                }

                return;
            }
        }

        if (StepSoundTime > 0f)
        {
            StepSoundTime = MathF.Max(0f, StepSoundTime - (FrameTime * 1000f));

            if (StepSoundTime > 0f)
            {
                return;
            }
        }

        if ((Flags & (PlayerFlags.Frozen | PlayerFlags.AtControls)) != 0)
        {
            return;
        }

        var speed = MathF.Sqrt(((velocity.Z * velocity.Z) + (velocity.Y * velocity.Y)) + (velocity.X * velocity.X));
        var groundSpeed = MathF.Sqrt((velocity.X * velocity.X) + (velocity.Y * velocity.Y));

        const float WalkVelocity = 135f;
        const float RunVelocity = 220f;

        if (IsWalking || !(speed >= WalkVelocity) || !(ActualMoveType == MoveType.Ladder || (OnGround && groundSpeed > 0.000099999997f)))
        {
            return;
        }

        var walking = RunVelocity > speed;
        float volume;
        var onLadder = false;

        if (ActualMoveType == MoveType.Ladder)
        {
            volume = 0.5f;
            onLadder = true;
            SetStepSoundTime(StepSoundKind.Ladder, walking, velocity);
        }
        else if (WaterLevel == 3)
        {
            volume = 0.65f;
            SetStepSoundTime(StepSoundKind.WaterKnee, walking, velocity);
        }
        else if (WaterLevel is 1 or 2)
        {
            volume = walking ? 0.2f : 0.5f;
            SetStepSoundTime(StepSoundKind.WaterFoot, walking, velocity);
        }
        else
        {
            SetStepSoundTime(StepSoundKind.Normal, walking, velocity);
            volume = walking ? 0.2f : 0.5f;
        }

        if (WaterLevel != 0)
        {
            Effects?.PlayWaterSound(origin, "CT_Water.StepLeft");
        }

        if ((Flags & PlayerFlags.Ducking) != 0)
        {
            volume *= 0.64999998f;
        }

        Effects?.PlayStepSound(origin, surfacePropertyHash, volume, onLadder);
    }

    private enum StepSoundKind
    {
        Normal,
        Ladder,
        WaterKnee,
        WaterFoot,
    }

    private void SetStepSoundTime(StepSoundKind kind, bool walking, Vector3 velocity)
    {
        StepSoundTime = kind switch
        {
            // Slower climbing steps further apart, 350 to 250 ms
            StepSoundKind.Ladder => MathF.Floor(35f - (MathF.Min(MathF.Max((MathF.Abs(velocity.Z) - 150f) / 100f, 0f), 1f) * 10f)) * 10f,
            StepSoundKind.WaterKnee => 600f,
            _ => walking ? 400f : 300f,
        };

        if ((Flags & PlayerFlags.Ducking) != 0)
        {
            StepSoundTime += 100f;
        }
    }
}
