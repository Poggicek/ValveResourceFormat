using Microsoft.Extensions.Logging;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// An NPC of Half-Life: Alyx. Its own AI is not simulated: it holds the animation it spawns with until a
/// <see cref="ScriptedSequence"/> takes it over, then runs the schedule the script picks, and goes back
/// to idling once the script lets go of it.
/// </summary>
/// <remarks>
/// There is no navigation, so a script's walk or run to its mark goes in a straight line, at the pace
/// of the movement the walk or run sequence carries. Animation graphs are not played, so an NPC built on
/// one plays the script's sequences directly, as the game does while a script holds the graph.
/// </remarks>
/// <seealso href="https://s2v.app/SchemaExplorer/hlvr/server/CAI_BaseNPC">CAI_BaseNPC</seealso>
public class BaseNpc : BaseModelEntity
{
    /// <summary>What an NPC is doing, as far as its AI is concerned.</summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/hlvr/client/NPC_STATE">NPC_STATE</seealso>
    public enum NpcState
    {
        /// <summary>Not set.</summary>
        Invalid = -1,
        /// <summary>Has not picked a state yet.</summary>
        None = 0,
        /// <summary>Idling.</summary>
        Idle = 1,
        /// <summary>Alert, without an enemy in sight.</summary>
        Alert = 2,
        /// <summary>Fighting.</summary>
        Combat = 3,
        /// <summary>Held by a script.</summary>
        Script = 4,
        /// <summary>Playing dead.</summary>
        PlayDead = 5,
        /// <summary>Lying down.</summary>
        Prone = 6,
        /// <summary>Dead.</summary>
        Dead = 7,
    }

    /// <summary>Where an NPC is in the script holding it.</summary>
    public enum ScriptState
    {
        /// <summary>Playing the entry or the action, and what an NPC without a script is left in.</summary>
        Playing = 0,
        /// <summary>Waiting on the mark for the action to start.</summary>
        Wait = 1,
        /// <summary>Playing the post-action idle.</summary>
        PostIdle = 2,
        /// <summary>Being let go of by a cancelled script.</summary>
        Cleanup = 3,
        /// <summary>Walking to the mark.</summary>
        Walk = 4,
        /// <summary>Running to the mark.</summary>
        Run = 5,
        /// <summary>Moving to the mark with the script's custom move.</summary>
        Custom = 6,
    }

    /// <summary>Whether an NPC can take a script.</summary>
    public enum PlayResult
    {
        /// <summary>It cannot.</summary>
        Cannot = 0,
        /// <summary>It can, now.</summary>
        Now = 1,
        /// <summary>It can, once the script it is in is done.</summary>
        Enqueue = 2,
    }

    private enum ScriptTask
    {
        FaceScript,
        MoveToScript,
        PlantOnScript,
        EnableScript,
        WaitForScript,
        PlayScript,
        PlayScriptPostIdle,
    }

    private static readonly ScriptTask[] WaitSchedule =
        [ScriptTask.EnableScript, ScriptTask.WaitForScript, ScriptTask.PlayScript, ScriptTask.PlayScriptPostIdle];

    private static readonly ScriptTask[] FaceSchedule =
        [ScriptTask.FaceScript, ScriptTask.EnableScript, ScriptTask.WaitForScript, ScriptTask.PlayScript, ScriptTask.PlayScriptPostIdle];

    private static readonly ScriptTask[] MoveSchedule =
        [ScriptTask.MoveToScript, ScriptTask.PlantOnScript, ScriptTask.FaceScript, ScriptTask.EnableScript, ScriptTask.WaitForScript,
            ScriptTask.PlayScript, ScriptTask.PlayScriptPostIdle];

    // How close a move to the mark has to get before the NPC is planted on it
    private const float MoveTolerance = 2f;

    // At the end of a script the NPC is put where its model's root ended up, when that is this far off
    private const float RepositionDistance = 8f;

    // How far below the end of a script the floor is looked for
    private const float DropToFloorDistance = 256f;

    /// <summary>Gets the script holding the NPC.</summary>
    public ScriptedSequence? Cine
    {
        get => field is { IsRemoved: false } ? field : null;
        internal set;
    }

    /// <summary>Gets the state the NPC is in.</summary>
    public NpcState State { get; private set; } = NpcState.Idle;

    /// <summary>Gets the state the NPC moves to on its next think.</summary>
    public NpcState IdealState { get; private set; } = NpcState.Idle;

    /// <summary>Gets where the NPC is in its script.</summary>
    public ScriptState ScriptedState { get; internal set; }

    /// <summary>Gets whether a script's animation has killed the NPC, which it dies of once the script lets go.</summary>
    public bool IsDying { get; private set; }

    /// <summary>Gets or sets whether the NPC is held off the ground, so the end of a script does not drop it onto the floor.</summary>
    internal bool IsFlying { get; set; }

    private int sleepState;
    private ScriptTask[]? schedule;
    private int taskIndex;
    private bool isTaskStarted;
    private AnimationBase? scriptAnimation;
    private SequenceAnimation? moveAnimation;

    /// <summary>Initializes an NPC from its keyvalues.</summary>
    public BaseNpc(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    // Collides as a hull, not with the ragdoll its model carries
    /// <inheritdoc/>
    protected override bool BuildsCollider => false;

    /// <inheritdoc/>
    public override void Spawn()
    {
        base.Spawn();

        sleepState = KeyValues.GetInt32Property("sleepstate");

        if (ModelNode is { } node)
        {
            node.AnimationController.SequenceEventFired += HandleAnimEvent;
        }
    }

    /// <inheritdoc/>
    protected override void OnRemove()
    {
        if (ModelNode is { } node)
        {
            node.AnimationController.SequenceEventFired -= HandleAnimEvent;
        }
    }

    /// <inheritdoc/>
    public override void Think()
    {
        // Root motion piles up between ticks, and only the action of a script moves the NPC with it
        var rootMotion = ModelNode?.AnimationController.ConsumeRootMotionDelta() ?? Matrix4x4.Identity;

        if (State != IdealState)
        {
            State = IdealState;
            schedule = null;
        }

        if (schedule == null && State == NpcState.Script)
        {
            SelectScriptSchedule();
        }

        if (schedule != null)
        {
            RunSchedule(rootMotion);
        }

        if (schedule != null || State != IdealState)
        {
            SetNextThink(EntitySystem.CurrentTime + EntitySystem.TickInterval);
        }
    }

    /// <summary>
    /// Moves the NPC outright. A script holding it is reported as having ended, unless the script is the
    /// one moving it.
    /// </summary>
    public override void Teleport(Vector3 origin, Vector3? angles)
    {
        if (Cine is { IsTeleportingDueToMoveTo: false } cine && ScriptedState is not (ScriptState.Walk or ScriptState.Run or ScriptState.Custom))
        {
            cine.ScriptEntityCancel(pretendSuccess: true);
        }

        base.Teleport(origin, angles);
    }

    /// <summary>Tells whether the NPC can take a script now, later, or not at all.</summary>
    /// <param name="disregardState">Whether the NPC is taken whatever state it is in.</param>
    /// <param name="interruptLevel">1 when the script found the NPC by name, which may also take an alert one.</param>
    public PlayResult CanPlaySequence(bool disregardState, int interruptLevel)
    {
        if (IsRemoved || IsDying || State == NpcState.Dead)
        {
            return PlayResult.Cannot;
        }

        var result = PlayResult.Now;

        if (Cine is { } current)
        {
            // The one script waiting in the queue can be pushed out, unless it has to play
            if (current.NextCine is { } next && (current.HasNextScript || next.HasSpawnFlags(ScriptedSequence.SpawnFlag.HighPriority)))
            {
                return PlayResult.Cannot;
            }

            result = PlayResult.Enqueue;
        }

        if (!disregardState
            && State is not (NpcState.None or NpcState.Idle or NpcState.Script)
            && IdealState != NpcState.Idle
            && (State != NpcState.Alert || interruptLevel < 1))
        {
            return PlayResult.Cannot;
        }

        return result;
    }

    /// <summary>Changes the state the NPC moves to on its next think.</summary>
    internal void SetIdealState(NpcState state)
    {
        IdealState = state;
        SetNextThink(EntitySystem.CurrentTime + EntitySystem.TickInterval);
    }

    /// <summary>Wakes a sleeping NPC.</summary>
    /// <returns>Whether it was asleep.</returns>
    internal bool Wake()
    {
        var wasAsleep = sleepState != 0;
        sleepState = 0;
        return wasAsleep;
    }

    /// <summary>Drops the schedule the NPC is running.</summary>
    internal void ClearSchedule()
    {
        schedule = null;
        moveAnimation = null;
    }

    /// <summary>
    /// Lets go of the script, undoing what it changed, and moves the NPC onto where its model ended up.
    /// </summary>
    internal void CineCleanup()
    {
        if (Cine is not { } cine)
        {
            return;
        }

        cine.RestoreNpc(this);
        Cine = null;

        // Dies where the animation left it, which it holds rather than turning into a ragdoll
        if (IsDying)
        {
            SetIdealState(NpcState.Dead);
            return;
        }

        if (cine.HasPlayedAction)
        {
            Reposition(cine);
        }

        SetIdealState(NpcState.Idle);
    }

    /// <summary>Plays one of a script's sequences. A missing one counts as having already ended.</summary>
    internal void PlayScriptSequence(string? name)
    {
        scriptAnimation = null;

        if (name == null || ModelNode is not { HasMeshes: true } node)
        {
            return;
        }

        if (!node.Animations.TryGetValue(name, out var animation))
        {
            EntitySystem.Logger.LogWarning("{Classname} '{TargetName}' has no sequence \"{Sequence}\" for scripted sequence '{Script}'",
                Classname, TargetName, name, Cine?.TargetName);
            return;
        }

        PlayAnimation(node, animation);
        scriptAnimation = animation;
    }

    /// <summary>Plays the script's sequence again from its start.</summary>
    internal void RestartScriptSequence()
    {
        if (scriptAnimation != null && ModelNode is { } node)
        {
            PlayAnimation(node, scriptAnimation);
        }
    }

    /// <summary>Puts the script's sequence at a time from its start, in seconds.</summary>
    internal void SetScriptSequenceTime(float time)
    {
        if (scriptAnimation != null && ModelNode is { } node && node.AnimationController.ActiveAnimation == scriptAnimation)
        {
            node.AnimationController.Time = time;
        }
    }

    /// <summary>
    /// Hands the NPC's animation back from the script to its idle. An NPC built on an animation graph can
    /// be told to keep holding the script's last sequence instead.
    /// </summary>
    internal void ReleaseScriptAnimation(bool keepLocked)
    {
        scriptAnimation = null;

        if (IsDying || State == NpcState.Dead || ModelNode is not { HasMeshes: true } node)
        {
            return;
        }

        if (keepLocked && node.AnimationGraphReferences.Count > 0)
        {
            return;
        }

        node.AnimationController.FrametimeMultiplier = 1f;

        // Without an idle the last sequence stays as it ended
        if (node.FindSequenceByActivity("ACT_IDLE") is { } idle)
        {
            PlayAnimation(node, idle);
        }
    }

    private void SelectScriptSchedule()
    {
        if (Cine is not { } cine)
        {
            EntitySystem.Logger.LogDebug("Script failed for {Classname} '{TargetName}'", Classname, TargetName);
            SetIdealState(NpcState.Idle);
            return;
        }

        schedule = cine.MoveTo switch
        {
            ScriptedSequence.MoveToMode.Walk or ScriptedSequence.MoveToMode.Run or ScriptedSequence.MoveToMode.CustomMove => MoveSchedule,
            ScriptedSequence.MoveToMode.TurnToFace => FaceSchedule,
            _ => WaitSchedule,
        };

        taskIndex = 0;
        isTaskStarted = false;
    }

    // A task runs right after it starts, and one that finishes hands over to the next in the same tick
    private void RunSchedule(Matrix4x4 rootMotion)
    {
        while (schedule is { } current && taskIndex < current.Length && Cine is { } cine)
        {
            var index = taskIndex;
            var task = current[index];
            var isComplete = false;

            if (!isTaskStarted)
            {
                isTaskStarted = true;
                isComplete = StartTask(task, cine);

                // The motion piled up so far belongs to what played before the task
                rootMotion = Matrix4x4.Identity;
            }

            // Either may end the script, or start another one in its place
            if (schedule == current && taskIndex == index && !isComplete)
            {
                isComplete = RunTask(task, cine, rootMotion);
            }

            if (schedule != current || taskIndex != index || !isComplete)
            {
                return;
            }

            taskIndex++;
            isTaskStarted = false;
        }

        if (schedule != null && taskIndex >= schedule.Length)
        {
            schedule = null;
        }
    }

    private bool StartTask(ScriptTask task, ScriptedSequence cine)
    {
        switch (task)
        {
            case ScriptTask.FaceScript:
                SetWorldOriginAndAngles(WorldOrigin, WorldAngles with { Y = cine.WorldAngles.Y });
                return true;

            case ScriptTask.MoveToScript:
                return !StartMoveToScript(cine);

            case ScriptTask.PlantOnScript:
                SetWorldOriginAndAngles(cine.WorldOrigin, WorldAngles);
                return true;

            case ScriptTask.EnableScript:
                if (cine.PreIdle != null)
                {
                    cine.StartSequence(this, cine.PreIdle, completeOnEmpty: false);

                    // The pre-action idle that is the action holds its first frame until the action starts
                    if (cine.PreIdle == cine.Play && ModelNode is { } node)
                    {
                        node.AnimationController.FrametimeMultiplier = 0f;
                    }
                }
                else if (ModelNode is { HasMeshes: true } idleNode && idleNode.FindSequenceByActivity("ACT_IDLE") is { } idle)
                {
                    PlayAnimation(idleNode, idle);
                }

                return false;

            case ScriptTask.PlayScript:
                if (cine.IgnoreGravity || scriptAnimation?.HasMovementData() == true)
                {
                    IsFlying = true;
                }

                cine.SynchronizeSequence(this);
                ScriptedState = ScriptState.Playing;
                return false;

            case ScriptTask.PlayScriptPostIdle:
                ScriptedState = ScriptState.PostIdle;
                return false;

            default:
                return false;
        }
    }

    private bool RunTask(ScriptTask task, ScriptedSequence cine, Matrix4x4 rootMotion)
    {
        switch (task)
        {
            case ScriptTask.MoveToScript:
                return MoveToScript(cine, rootMotion);

            case ScriptTask.EnableScript:
                if (cine.WaitForBeginSequence)
                {
                    return false;
                }

                cine.DelayStart(false);
                return true;

            case ScriptTask.WaitForScript:
                // Until every member of the sync group is ready
                if (cine.Delay != 0)
                {
                    return false;
                }

                cine.OnBeginSequence();
                cine.StartSequence(this, cine.Entry ?? cine.Play, completeOnEmpty: true);
                ModelNode?.AnimationController.FrametimeMultiplier = 1f;
                return true;

            case ScriptTask.PlayScript:
                ApplyRootMotion(rootMotion);

                if (IsScriptSequenceFinished())
                {
                    if (cine.FinishedActionSequence(this))
                    {
                        cine.SequenceDone(this);
                        return true;
                    }
                }
                else if (cine.ForceSynch)
                {
                    cine.SynchronizeSequence(this);
                }

                return false;

            case ScriptTask.PlayScriptPostIdle:
                // A script waiting in the queue cuts the post-action idle short
                if (IsScriptSequenceFinished() || cine.NextCine != null)
                {
                    cine.PostIdleDone(this);
                }

                return false;

            default:
                return false;
        }
    }

    // Returns false when there is no moving there, and the NPC is planted on the mark instead
    private bool StartMoveToScript(ScriptedSequence cine)
    {
        moveAnimation = null;

        if (ModelNode is not { HasMeshes: true } node)
        {
            return false;
        }

        var animation = cine.MoveTo switch
        {
            ScriptedSequence.MoveToMode.Walk => node.FindSequenceByActivity("ACT_WALK"),
            ScriptedSequence.MoveToMode.Run => node.FindSequenceByActivity("ACT_RUN"),
            _ => cine.CustomMove != null && node.Animations.TryGetValue(cine.CustomMove, out var custom) ? custom as SequenceAnimation : null,
        };

        if (animation == null || !animation.HasNonZeroMovementData())
        {
            return false;
        }

        moveAnimation = animation;
        PlayAnimation(node, animation);
        node.AnimationController.Looping = true;

        return true;
    }

    // Heads straight for the mark, as far each tick as the move sequence carries the model
    private bool MoveToScript(ScriptedSequence cine, Matrix4x4 rootMotion)
    {
        if (moveAnimation == null)
        {
            return true;
        }

        var goal = cine.WorldOrigin;
        var origin = WorldOrigin;
        var toGoal = new Vector2(goal.X - origin.X, goal.Y - origin.Y);
        var remaining = toGoal.Length();
        var step = new Vector2(rootMotion.Translation.X, rootMotion.Translation.Y).Length();

        if (remaining <= MoveTolerance + step)
        {
            moveAnimation = null;
            return true;
        }

        var direction = toGoal / remaining;
        var yaw = float.RadiansToDegrees(MathF.Atan2(direction.Y, direction.X));
        var moved = origin + new Vector3(direction * step, 0f);

        SetWorldOriginAndAngles(moved, WorldAngles with { Y = yaw });
        return false;
    }

    private void ApplyRootMotion(Matrix4x4 rootMotion)
    {
        if (rootMotion.IsIdentity)
        {
            return;
        }

        var angles = WorldAngles;
        var moved = rootMotion * EntityTransformHelper.ToRigidTransformationMatrix(angles, WorldOrigin);
        var turn = float.RadiansToDegrees(EntityTransformHelper.GetTwistAngleAroundZ(Quaternion.CreateFromRotationMatrix(rootMotion)));

        SetWorldOriginAndAngles(moved.Translation, angles with { Y = angles.Y + turn });
    }

    // A sequence ends when it first reaches its end, whether or not it goes on to loop
    private bool IsScriptSequenceFinished()
    {
        if (scriptAnimation == null || ModelNode is not { } node)
        {
            return true;
        }

        var controller = node.AnimationController;

        if (controller.ActiveAnimation != scriptAnimation || controller.ActiveClipFinished || scriptAnimation.FrameCount <= 1)
        {
            return true;
        }

        return controller.Looping && controller.Time >= scriptAnimation.Duration;
    }

    // Puts the NPC where the root of its model ended up, on the floor below
    private void Reposition(ScriptedSequence cine)
    {
        if (cine.HasSpawnFlags(ScriptedSequence.SpawnFlag.DontTeleportAtEnd) || ModelNode is not { HasMeshes: true } node)
        {
            return;
        }

        var origin = WorldOrigin;
        var root = Vector3.Transform(node.AnimationController.Pose[0].Translation, RigidTransform);
        var target = Vector2.Distance(new Vector2(origin.X, origin.Y), new Vector2(root.X, root.Y)) >= RepositionDistance ? root : origin;

        var position = target;

        if (!IsFlying)
        {
            var start = target with { Z = origin.Z + 1f };
            position = DropToFloor(start) is { } floor ? start with { Z = floor } : target;
        }

        if (Vector3.Distance(position, origin) > RepositionDistance)
        {
            Teleport(position, null);
        }
        else
        {
            SetWorldOriginAndAngles(position, WorldAngles);
        }
    }

    private float? DropToFloor(Vector3 from)
    {
        var to = from - new Vector3(0f, 0f, DropToFloorDistance);
        var trace = EntitySystem.PhysicsWorld.TraceRay(from, to, Rubikon.DefaultGeometry);

        EntitySystem.TraceRay(from, to, Rubikon.DefaultGeometry, ref trace);

        return trace.Hit ? trace.HitPosition.Z : null;
    }

    private static void PlayAnimation(SceneNodes.ModelSceneNode node, AnimationBase animation)
    {
        var controller = node.AnimationController;
        var blendTime = controller.ActiveAnimation != null && animation is SequenceAnimation sequence ? sequence.SequenceParams.FadeInTime : 0f;

        controller.Looping = animation is not SequenceAnimation { IsLooping: false };
        node.SetAnimation(animation, blendTime);

        // A non-looping animation that ran out paused the whole player, which a new one does not undo
        controller.IsPaused = false;
    }

    // Raised while the scene advances the model's animation, so anything that acts on other entities is queued
    private void HandleAnimEvent(AnimationEvent animationEvent)
    {
        switch (animationEvent.Name)
        {
            case "AE_SCRIPT_EVENT_FIREEVENT":
                Cine?.FireScriptEvent(animationEvent.EventData.GetInt32Property("eventindex") - 1);
                break;

            case "AE_SCRIPT_EVENT_FIRE_INPUT":
                if (animationEvent.EventData.GetStringProperty("input") is { Length: > 0 } input)
                {
                    EntitySystem.QueueInput(this, input, activator: this, caller: this);
                }

                break;

            case "AE_SCRIPT_EVENT_DEAD":
                if (State == NpcState.Script)
                {
                    IsDying = true;
                }

                break;

            case "AE_SCRIPT_EVENT_NOT_DEAD":
                if (State == NpcState.Script)
                {
                    IsDying = false;
                }

                break;
        }
    }
}
