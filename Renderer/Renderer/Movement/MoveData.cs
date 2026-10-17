using ValveResourceFormat.Renderer.Entities;

namespace ValveResourceFormat.Renderer.Movement;

/// <summary>
/// One input change inside the tick being simulated, with the absolute view angles at that moment.
/// </summary>
internal record struct SubtickMove(float When, InputButtons Button, bool Pressed, float AnalogForwardDelta, float AnalogLeftDelta, float Pitch, float Yaw)
{
    public readonly bool IsAnalogInput => Button == InputButtons.None;
}

/// <summary>
/// The working state of one command's movement, copied in from the player before it runs and back out after.
/// </summary>
/// <remarks>
/// Velocity is integrated in three parts. Friction, acceleration and gravity change
/// <see cref="Velocity"/> by their whole effect over the step and record the rate in
/// <see cref="ContinuousAcceleration"/>. Before the position moves, half of that change is taken back
/// out and parked in <see cref="FrameVelocityDelta"/>, so the step moves at its mid-step velocity,
/// which is exact for constant acceleration. The parked half is added back after the move.
/// </remarks>
/// <seealso href="https://s2v.app/SchemaExplorer/cs2/server/CMoveData">CMoveData</seealso>
internal sealed class MoveData
{
    public bool HasZeroFrametime;

    public Vector3 ViewAngles;

    /// <summary>The raw -1 to 1 movement inputs of the step being simulated.</summary>
    public Vector3 LastMovementImpulses;

    public float ForwardMove;

    /// <summary>The left move, positive to the left.</summary>
    public float SideMove;

    public float UpMove;
    public Vector3 Velocity;
    public Vector3 Angles;

    public List<SubtickMove> SubtickMoves { get; } = [];

    /// <summary>Entities hit during the command, each once, for touch callbacks.</summary>
    public List<BaseEntity> TouchList { get; } = [];

    public Vector3 AbsOrigin;

    /// <summary>The tick the command starts at.</summary>
    public int TickCount;

    /// <summary>The tick the command ends at, normally the next one.</summary>
    public int TargetTick;

    public float SubtickStartFraction;
    public float SubtickEndFraction;

    public Vector3 OutWishVel;
    public Vector2 WalkWishVel;
    public Vector3 ContinuousAcceleration;
    public Vector3 FrameVelocityDelta;
    public float MaxSpeed;
    public float ClientMaxSpeed;

    /// <summary>Friction that went unused because the player had already stopped, taken off acceleration.</summary>
    public float FrictionDecel;

    public float PreAirMovePosZ;
    public float PreAirMoveVelZ;
    public float PreAirMoveAccelZ;

    public bool GameCodeMovedPlayer;
    public bool UseFrictionStashedSpeed;

    public void SetSubtickFraction(float start, float end)
    {
        SubtickStartFraction = start;
        SubtickEndFraction = end;
    }

    /// <summary>Sets one velocity axis and drops any acceleration pending on it.</summary>
    public void SetVelocityAxis(int axis, float value)
    {
        Velocity[axis] = value;
        ContinuousAcceleration[axis] = 0f;
        FrameVelocityDelta[axis] = 0f;
    }

    public void ZeroVelocity()
    {
        Velocity = Vector3.Zero;
        ContinuousAcceleration = Vector3.Zero;
        FrameVelocityDelta = Vector3.Zero;
    }

    public void ClearTouchAndNormals() => TouchList.Clear();

    /// <summary>Records a touch, unless that entity was already touched during the command.</summary>
    public bool AddToTouched(in GameTrace trace)
    {
        if (!trace.Hit)
        {
            return false;
        }

        if (trace.Entity is not { } entity || TouchList.Contains(entity))
        {
            return false;
        }

        TouchList.Add(entity);
        return true;
    }

    /// <summary>
    /// Splits the step list at <paramref name="when"/> with a step that changes nothing, so the
    /// simulation has a boundary there. Taken from the step it is inserted before, so the view angles
    /// carry over.
    /// </summary>
    public void InsertSubtickSplit(float when)
    {
        if (when == 0f || when == 1f)
        {
            return;
        }

        var index = 0;

        for (; index < SubtickMoves.Count; index++)
        {
            var move = SubtickMoves[index];

            if (!(move.When <= when))
            {
                break;
            }

            if (when == move.When)
            {
                return;
            }
        }

        if (index < SubtickMoves.Count)
        {
            var next = SubtickMoves[index];
            SubtickMoves.Insert(index, new SubtickMove(when, InputButtons.None, false, 0f, 0f, next.Pitch, next.Yaw));
        }
        else
        {
            SubtickMoves.Add(new SubtickMove(when, InputButtons.None, false, 0f, 0f, 0f, 0f));
        }
    }
}
