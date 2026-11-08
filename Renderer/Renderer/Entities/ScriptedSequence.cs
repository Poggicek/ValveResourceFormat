using System.Linq;
using Microsoft.Extensions.Logging;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// Takes over an NPC to play authored sequences on it: an optional move to the script's mark, a pre-action
/// idle until <c>BeginSequence</c>, the entry and action sequences, then a post-action idle. Scripts with the
/// same <c>sync_group</c>, or failing that the same name, start their actions together.
/// </summary>
/// <remarks>
/// The NPC side of the script lives in <see cref="BaseNpc"/>. Scripts started by code against an interaction
/// partner, and the ground IK and collision group a script switches, have nothing to act on here.
/// </remarks>
/// <seealso href="https://s2v.app/SchemaExplorer/hlvr/server/CAI_ScriptedSequence">CAI_ScriptedSequence</seealso>
public sealed class ScriptedSequence : BaseEntity
{
    /// <summary>Spawn flags for scripted sequences.</summary>
    [Flags]
    public enum SpawnFlag : uint
    {
        /// <summary>Can play again once it has finished, instead of removing itself.</summary>
        Repeatable = 4,
        /// <summary>An NPC killed by the script stays as it is, rather than turning into a ragdoll.</summary>
        LeaveCorpse = 8,
        /// <summary>Looks for its NPC on spawn even though it is named.</summary>
        StartOnSpawn = 16,
        /// <summary>The NPC cannot be interrupted while it plays.</summary>
        NoInterruptions = 32,
        /// <summary>Takes the NPC whatever state it is in.</summary>
        OverrideAiState = 64,
        /// <summary>Leaves the NPC where it stands at the end, instead of moving it onto the model's root.</summary>
        DontTeleportAtEnd = 128,
        /// <summary>Loops the post-action idle until another script takes the NPC.</summary>
        LoopInPostIdle = 256,
        /// <summary>Cannot be pushed out of an NPC's queue by another script.</summary>
        HighPriority = 512,
        /// <summary>Looks for the next matching NPC after the last one it took, rather than the first.</summary>
        SearchCyclically = 1024,
        /// <summary>Does not complain about an NPC that cannot play it.</summary>
        NoComplaints = 2048,
        /// <summary>A cancelled script does not cancel the rest of its sync group once it has started.</summary>
        AllowNpcDeath = 4096,
    }

    /// <summary>How the NPC gets to the script's mark, the <c>m_fMoveTo</c> key.</summary>
    public enum MoveToMode
    {
        /// <summary>Plays where the NPC stands.</summary>
        None = 0,
        /// <summary>Walks to the mark.</summary>
        Walk = 1,
        /// <summary>Runs to the mark.</summary>
        Run = 2,
        /// <summary>Moves to the mark with <c>m_iszCustomMove</c>.</summary>
        CustomMove = 3,
        /// <summary>Teleports onto the mark.</summary>
        Instantaneous = 4,
        /// <summary>Turns to face the mark's direction where it stands.</summary>
        TurnToFace = 5,
    }

    /// <summary>What a script does when its NPC is already in another one.</summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/hlvr/server/ScriptedConflictResponse_t">ScriptedConflictResponse_t</seealso>
    public enum ConflictResponse
    {
        /// <summary>Waits in the NPC's queue for the other script to finish.</summary>
        Enqueue = 0,
        /// <summary>Cancels the other script.</summary>
        Interrupt = 1,
    }

    private enum PendingThink
    {
        None,
        ScriptThink,
        Remove,
    }

    // The NPC may already sit in the action (when the pre-action idle is the action) or be done with it,
    // which is what synchronizing backs it up into
    private const float MaxSynchronizeTime = 0.15f;

    /// <summary>Gets the sequence played once before the action, if any.</summary>
    public string? Entry { get; }

    /// <summary>Gets the sequence held while waiting for <c>BeginSequence</c>, if any.</summary>
    public string? PreIdle { get; }

    /// <summary>Gets the action sequence, which <c>SetActionSequence</c> replaces.</summary>
    public string? Play { get; private set; }

    /// <summary>Gets the sequence played after the action, if any.</summary>
    public string? PostIdle { get; }

    /// <summary>Gets the sequence a <see cref="MoveToMode.CustomMove"/> moves with.</summary>
    public string? CustomMove { get; }

    /// <summary>Gets how the NPC gets to the mark.</summary>
    public MoveToMode MoveTo { get; }

    /// <summary>Gets whether the action loops until the script is cancelled.</summary>
    public bool LoopActionSequence { get; private set; }

    /// <summary>Gets whether the sync group starts its post-action idles together.</summary>
    public bool SynchPostIdles { get; }

    /// <summary>Gets whether the NPC is held off the ground while it plays.</summary>
    public bool IgnoreGravity { get; }

    /// <summary>Gets whether the NPC keeps holding its last sequence after the script.</summary>
    public bool KeepAnimgraphLockedPost { get; }

    /// <summary>Gets whether the NPC keeps the direction it ended in, rather than where it faced at the start.</summary>
    public bool PreventUpdateYawOnFinish { get; }

    /// <summary>Gets whether the NPC is put back on the navigation mesh at the end.</summary>
    public bool EnsureOnNavmeshOnFinish { get; }

    /// <summary>Gets the NPC the script has taken, or is waiting in the queue of.</summary>
    public BaseNpc? TargetNpc
    {
        get => field is { IsRemoved: false } ? field : null;
        internal set;
    }

    /// <summary>Gets the script the NPC plays once this one is done, from <c>m_iszNextScript</c> or a queued one.</summary>
    public ScriptedSequence? NextCine
    {
        get => field is { IsRemoved: false } ? field : null;
        internal set;
    }

    /// <summary>Gets whether <c>m_iszNextScript</c> names the script that follows, which no other script may take the place of.</summary>
    internal bool HasNextScript => nextScriptName != null;

    internal bool IsPlayingEntry { get; private set; }
    internal bool WaitForBeginSequence { get; private set; }
    internal bool IsTeleportingDueToMoveTo { get; private set; }
    internal bool ForceSynch { get; private set; }

    /// <summary>
    /// Gets how many members of the sync group are still holding the action back. The NPC starts it at 0.
    /// </summary>
    internal int Delay { get; private set; }

    /// <summary>Gets when the action was last released, 0 before the script has started.</summary>
    internal float StartTime { get; private set; }

    private readonly string? entityName;
    private readonly string? syncGroup;
    private readonly float radius;
    private readonly ConflictResponse conflictResponse;
    private readonly bool cancelOnPlayerDeath;

    private string? nextScriptName;
    private bool sequenceStarted;
    private bool initiatedSelfDelete;
    private bool targetWasAsleep;
    private bool savedIsFlying;
    private BaseNpc? lastFoundEntity;
    private PendingThink pendingThink;

    /// <summary>Initializes a <c>scripted_sequence</c> from its keyvalues.</summary>
    public ScriptedSequence(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
        Entry = ReadName("m_iszentry");
        PreIdle = ReadName("m_iszidle");
        Play = ReadName("m_iszplay");
        PostIdle = ReadName("m_iszpostidle");
        CustomMove = ReadName("m_iszcustommove");
        nextScriptName = ReadEntityName("m_isznextscript");
        entityName = ReadEntityName("m_iszentity");
        syncGroup = ReadEntityName("sync_group");

        MoveTo = (MoveToMode)KeyValues.GetInt32Property("m_fmoveto");
        LoopActionSequence = KeyValues.GetBooleanProperty("m_bloopactionsequence");
        SynchPostIdles = KeyValues.GetBooleanProperty("m_bsynchpostidles");
        IgnoreGravity = KeyValues.GetBooleanProperty("m_bignoregravity");
        KeepAnimgraphLockedPost = KeyValues.GetBooleanProperty("m_bkeepanimgraphlockedpost");
        PreventUpdateYawOnFinish = KeyValues.GetBooleanProperty("prevent_update_yaw_on_finish");
        EnsureOnNavmeshOnFinish = KeyValues.GetBooleanProperty("ensure_on_navmesh_on_finish");

        radius = KeyValues.GetFloatProperty("m_flradius");
        conflictResponse = (ConflictResponse)KeyValues.GetInt32Property("conflict_response");
        cancelOnPlayerDeath = KeyValues.GetInt32Property("onplayerdeath") == 1;
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        base.Spawn();

        // A named script waits for an input, unless it is told to start anyway, and then still holds its
        // action back for BeginSequence
        if (TargetName == null || HasSpawnFlags(SpawnFlag.StartOnSpawn))
        {
            StartScriptThink(EntitySystem.CurrentTime + EntitySystem.TickInterval);

            if (TargetName != null)
            {
                WaitForBeginSequence = true;
            }
        }

        sequenceStarted = false;
        StartTime = 0f;
        NextCine = null;
        lastFoundEntity = null;
    }

    /// <inheritdoc/>
    public override void Activate()
    {
        base.Activate();

        if (nextScriptName == null)
        {
            return;
        }

        NextCine = EntitySystem.FindTargets(new EntityIOTarget(nextScriptName, EntityIOTargetType.EntityName), caller: this)
            .OfType<ScriptedSequence>()
            .FirstOrDefault();

        // An unresolved name no longer counts as a next script, which would hold the NPC in the queue
        if (NextCine == null)
        {
            nextScriptName = null;
        }
    }

    /// <inheritdoc/>
    public override void Think()
    {
        var think = pendingThink;
        pendingThink = PendingThink.None;

        switch (think)
        {
            case PendingThink.ScriptThink:
                ScriptThink();
                break;
            case PendingThink.Remove:
                EntitySystem.Remove(this);
                break;
        }
    }

    /// <inheritdoc/>
    protected override void OnRemove()
    {
        // A whole world going away has no one left to tell
        if (TargetNpc is { IsRemoved: false })
        {
            ScriptEntityCancel(pretendSuccess: false);
        }
    }

    [EntityInput("BeginSequence")]
    private void InputBeginSequence(EntityInputData data)
    {
        if (initiatedSelfDelete)
        {
            return;
        }

        WaitForBeginSequence = false;

        // An NPC already moving to the mark or waiting there carries on by itself, now that the wait is over
        if (TargetNpc is { } npc)
        {
            if (npc.ScriptedState is BaseNpc.ScriptState.Playing or BaseNpc.ScriptState.PostIdle
                && npc.CanPlaySequence(HasSpawnFlags(SpawnFlag.OverrideAiState), interruptLevel: 1) != BaseNpc.PlayResult.Cannot)
            {
                StartScript();
            }

            return;
        }

        StopThinking();
        ScriptThink();
    }

    [EntityInput("MoveToPosition")]
    private void InputMoveToPosition(EntityInputData data)
    {
        if (initiatedSelfDelete)
        {
            return;
        }

        if (TargetNpc is { } npc)
        {
            if (npc.ScriptedState is BaseNpc.ScriptState.Playing or BaseNpc.ScriptState.PostIdle
                && npc.CanPlaySequence(HasSpawnFlags(SpawnFlag.OverrideAiState), interruptLevel: 1) != BaseNpc.PlayResult.Cannot)
            {
                StartScript();
                WaitForBeginSequence = true;
            }

            return;
        }

        StartScriptThink(EntitySystem.CurrentTime + EntitySystem.TickInterval);
        WaitForBeginSequence = true;
    }

    [EntityInput("CancelSequence")]
    private void InputCancelSequence(EntityInputData data)
    {
        if (initiatedSelfDelete)
        {
            return;
        }

        StopThinking();
        ScriptEntityCancel(pretendSuccess: false);
    }

    [EntityInput("SetActionSequence")]
    private void InputSetActionSequence(EntityInputData data) => Play = string.IsNullOrEmpty(data.Parameter) ? null : data.Parameter;

    [EntityInput("ScriptPlayerDeath")]
    private void InputScriptPlayerDeath(EntityInputData data)
    {
        if (!cancelOnPlayerDeath || initiatedSelfDelete)
        {
            return;
        }

        StopThinking();
        ScriptEntityCancel(pretendSuccess: false);
    }

    private void StartScriptThink(float time)
    {
        sequenceStarted = false;
        pendingThink = PendingThink.ScriptThink;
        SetNextThink(time);
    }

    private void StopThinking()
    {
        if (pendingThink == PendingThink.ScriptThink)
        {
            pendingThink = PendingThink.None;
            SetNextThink(-1f);
        }
    }

    // Looks for the NPC until one can play the script, once a second
    private void ScriptThink()
    {
        if (FindEntity())
        {
            StartScript();
            return;
        }

        CancelScript();
        StartScriptThink(EntitySystem.CurrentTime + 1f);
    }

    private bool FindEntity()
    {
        var found = FindScriptEntity();

        if (HasSpawnFlags(SpawnFlag.SearchCyclically))
        {
            lastFoundEntity = found;
        }

        TargetNpc = found;
        return found != null;
    }

    // An NPC that can play now wins over one that would queue the script behind another
    private BaseNpc? FindScriptEntity()
    {
        if (entityName == null)
        {
            return null;
        }

        var overrideState = HasSpawnFlags(SpawnFlag.OverrideAiState);
        BaseNpc? enqueueTarget = null;

        foreach (var (npc, interruptLevel) in FindCandidates())
        {
            switch (npc.CanPlaySequence(overrideState, interruptLevel))
            {
                case BaseNpc.PlayResult.Now:
                    return npc;
                case BaseNpc.PlayResult.Enqueue:
                    enqueueTarget = npc;
                    break;
                default:
                    if (!HasSpawnFlags(SpawnFlag.NoComplaints))
                    {
                        EntitySystem.Logger.LogDebug("Scripted sequence {Name} found NPC {Entity}, but can't play!", TargetName, entityName);
                    }

                    break;
            }
        }

        return enqueueTarget;
    }

    // Names first, from just after the last NPC taken; the class name is a fallback that may only interrupt
    // NPCs in a lower state
    private IEnumerable<(BaseNpc Npc, int InterruptLevel)> FindCandidates()
    {
        var byName = EntitySystem.FindTargets(new EntityIOTarget(entityName!, EntityIOTargetType.EntityName), caller: this)
            .OfType<BaseNpc>()
            .Where(IsInRadius)
            .ToList();

        if (byName.Count > 0)
        {
            foreach (var npc in StartAfterLastFound(byName))
            {
                yield return (npc, 1);
            }

            yield break;
        }

        var byClass = EntitySystem.Entities
            .OfType<BaseNpc>()
            .Where(npc => !npc.IsRemoved && npc.Classname.Equals(entityName, StringComparison.OrdinalIgnoreCase) && IsInRadius(npc))
            .ToList();

        foreach (var npc in StartAfterLastFound(byClass))
        {
            yield return (npc, 0);
        }
    }

    private IEnumerable<BaseNpc> StartAfterLastFound(List<BaseNpc> candidates)
    {
        var last = lastFoundEntity == null ? -1 : candidates.IndexOf(lastFoundEntity);
        return candidates.Skip(last + 1);
    }

    // A radius of 0 is no limit
    private bool IsInRadius(BaseEntity npc)
        => radius == 0f || Vector3.DistanceSquared(npc.WorldOrigin, WorldOrigin) < radius * radius;

    /// <summary>Takes <see cref="TargetNpc"/>, or queues behind the script it is already in.</summary>
    internal void StartScript()
    {
        if (TargetNpc is not { } npc)
        {
            return;
        }

        if (npc.Cine is { } current)
        {
            if (conflictResponse != ConflictResponse.Interrupt)
            {
                // Only one script waits in the queue; a newer one pushes the waiting one out
                if (current.NextCine is { } queued && queued != current)
                {
                    queued.TargetNpc = null;
                }

                current.NextCine = this;
                return;
            }

            current.ScriptEntityCancel(pretendSuccess: false);
        }

        if (nextScriptName == null)
        {
            NextCine = null;
        }

        npc.Cine = this;

        // A sleeping NPC is woken and kept out of sight until it plays something past its pre-action idle,
        // so it is not seen standing in the wrong pose
        targetWasAsleep = npc.Wake();

        if (targetWasAsleep)
        {
            npc.IsDrawn = false;
        }

        savedIsFlying = npc.IsFlying;

        switch (MoveTo)
        {
            case MoveToMode.Walk:
                npc.ScriptedState = BaseNpc.ScriptState.Walk;
                break;
            case MoveToMode.Run:
                npc.ScriptedState = BaseNpc.ScriptState.Run;
                break;
            case MoveToMode.CustomMove:
                npc.ScriptedState = BaseNpc.ScriptState.Custom;
                break;
            default:
                if (MoveTo == MoveToMode.Instantaneous)
                {
                    IsTeleportingDueToMoveTo = true;
                    npc.Teleport(WorldOrigin, npc.WorldAngles with { Y = WorldAngles.Y });
                    IsTeleportingDueToMoveTo = false;
                }

                npc.ScriptedState = BaseNpc.ScriptState.Wait;

                if (IgnoreGravity)
                {
                    npc.IsFlying = true;
                }

                break;
        }

        DelayStart(true);
        npc.SetIdealState(BaseNpc.NpcState.Script);
    }

    /// <summary>
    /// Plays one of the script's sequences on the NPC. A missing sequence with <paramref name="completeOnEmpty"/>
    /// ends the action instead.
    /// </summary>
    internal void StartSequence(BaseNpc npc, string? sequence, bool completeOnEmpty)
    {
        sequenceStarted = true;
        IsPlayingEntry = sequence == Entry;

        if (sequence == null && completeOnEmpty)
        {
            SequenceDone(npc);
            return;
        }

        npc.PlayScriptSequence(sequence);

        if (targetWasAsleep && sequence != PreIdle)
        {
            targetWasAsleep = false;
            npc.IsDrawn = true;
        }
    }

    /// <summary>Starts every NPC of the sync group on the same sequence, from now.</summary>
    internal void SynchNewSequence(BaseNpc.ScriptState state, bool synchOthers)
    {
        if (synchOthers)
        {
            foreach (var member in CollectSyncGroup())
            {
                if (member != this)
                {
                    member.SynchNewSequence(state, synchOthers: false);
                }
            }
        }

        if (TargetNpc is not { } npc)
        {
            return;
        }

        StartTime = EntitySystem.CurrentTime;
        npc.ScriptedState = state;

        if (state == BaseNpc.ScriptState.Playing)
        {
            StartSequence(npc, Play, completeOnEmpty: false);
            EntitySystem.TriggerOutput(this, "OnActionStartOrLoop", this);
        }
        else
        {
            StartSequence(npc, state == BaseNpc.ScriptState.PostIdle ? PostIdle : null, completeOnEmpty: false);
        }

        ForceSynch = true;
    }

    /// <summary>Puts the NPC as far into the sequence as the group has run since its start, up to a limit.</summary>
    internal void SynchronizeSequence(BaseNpc npc)
    {
        npc.SetScriptSequenceTime(Math.Clamp(EntitySystem.CurrentTime - StartTime, 0f, MaxSynchronizeTime));
        ForceSynch = false;
    }

    /// <summary>
    /// Called when a sequence of the action ends. The entry goes on into the action and a looped action plays
    /// again, so only the end of the last one finishes the action.
    /// </summary>
    /// <returns><see langword="true"/> when the action is over.</returns>
    internal bool FinishedActionSequence(BaseNpc npc)
    {
        if (IsPlayingEntry)
        {
            if (CollectSyncGroup().Count > 1)
            {
                SynchNewSequence(BaseNpc.ScriptState.Playing, synchOthers: true);
            }
            else
            {
                StartSequence(npc, Play, completeOnEmpty: true);
                EntitySystem.TriggerOutput(this, "OnActionStartOrLoop", this);
            }

            return false;
        }

        if (LoopActionSequence && npc.ScriptedState != BaseNpc.ScriptState.PostIdle)
        {
            npc.RestartScriptSequence();
            EntitySystem.TriggerOutput(this, "OnActionStartOrLoop", this);
            return false;
        }

        return true;
    }

    /// <summary>Moves on from the action to the post-action idle, or ends the script without one.</summary>
    internal void SequenceDone(BaseNpc npc)
    {
        var isGrouped = CollectSyncGroup().Count > 1;

        if (SynchPostIdles && isGrouped)
        {
            if (npc.ScriptedState != BaseNpc.ScriptState.PostIdle)
            {
                if (PostIdle != null && NextCine == null)
                {
                    SynchNewSequence(BaseNpc.ScriptState.PostIdle, synchOthers: true);
                }
                else
                {
                    PostIdleDone(npc);
                }
            }
        }
        else if (PostIdle != null && NextCine == null)
        {
            npc.ScriptedState = BaseNpc.ScriptState.PostIdle;
            StartSequence(npc, PostIdle, completeOnEmpty: false);
        }
        else
        {
            PostIdleDone(npc);
        }

        EntitySystem.TriggerOutput(this, "OnEndSequence", npc);
    }

    /// <summary>Releases the NPC once the post-action idle has played, and hands it to the next script.</summary>
    internal void PostIdleDone(BaseNpc npc)
    {
        if (PostIdle != null && HasSpawnFlags(SpawnFlag.LoopInPostIdle) && NextCine == null)
        {
            npc.ScriptedState = BaseNpc.ScriptState.PostIdle;
            StartSequence(npc, PostIdle, completeOnEmpty: false);
        }
        else
        {
            if (!HasSpawnFlags(SpawnFlag.Repeatable))
            {
                StopThinking();
                pendingThink = PendingThink.Remove;
                SetNextThink(EntitySystem.CurrentTime + 0.1f);
                initiatedSelfDelete = true;
            }

            npc.CineCleanup();
            FixScriptNpcSchedule(npc, savedIsFlying);
            npc.ReleaseScriptAnimation(KeepAnimgraphLockedPost);

            if (NextCine is { } next && (next != this || HasSpawnFlags(SpawnFlag.Repeatable)))
            {
                next.TargetNpc = npc;
                next.StartScript();
            }
        }

        EntitySystem.TriggerOutput(this, "OnPostIdleEndSequence", npc);
    }

    // Gives the NPC back to its own AI, held on the ground again unless it flew before the script
    private static void FixScriptNpcSchedule(BaseNpc npc, bool wasFlying)
    {
        if (npc.IdealState is not (BaseNpc.NpcState.Dead or BaseNpc.NpcState.Idle))
        {
            npc.SetIdealState(BaseNpc.NpcState.Idle);
        }

        if (!wasFlying)
        {
            npc.IsFlying = false;
        }

        npc.ClearSchedule();
    }

    /// <summary>
    /// Undoes what the script changed on the NPC, for <see cref="BaseNpc.CineCleanup"/>. The NPC comes out
    /// drawn, as hiding it is the only change a script makes to how it is drawn.
    /// </summary>
    internal void RestoreNpc(BaseNpc npc)
    {
        TargetNpc = null;
        targetWasAsleep = false;
        npc.IsDrawn = true;
    }

    /// <summary>Gets whether the script got as far as playing its action, which the NPC is moved onto the end of.</summary>
    internal bool HasPlayedAction => Play != null && sequenceStarted;

    /// <summary>
    /// Holds back the action of the sync group, or releases this script's hold on it. Every member holds
    /// every member, so the group starts when the last one is ready.
    /// </summary>
    internal void DelayStart(bool delay)
    {
        var group = CollectSyncGroup();

        if (group.Count <= 1)
        {
            Delay = delay ? 1 : 0;
            StartTime = EntitySystem.CurrentTime;
            return;
        }

        foreach (var member in group)
        {
            if (delay)
            {
                Delay++;
            }
            else if (--member.Delay == 0)
            {
                member.StartTime = EntitySystem.CurrentTime;
            }
        }
    }

    /// <summary>Fires the outputs of the action starting.</summary>
    internal void OnBeginSequence()
    {
        EntitySystem.TriggerOutput(this, "OnBeginSequence", TargetNpc);
        EntitySystem.TriggerOutput(this, "OnActionStartOrLoop", this);
    }

    /// <summary>Fires one of the <c>OnScriptEvent01</c> to <c>OnScriptEvent08</c> outputs, from 0.</summary>
    internal void FireScriptEvent(int index)
    {
        if (index is < 0 or > 7)
        {
            return;
        }

        EntitySystem.TriggerOutput(this, $"OnScriptEvent0{index + 1}", TargetNpc);
    }

    // Cancels the whole sync group, unless this member may leave it on its own
    private void CancelScript()
    {
        var group = CollectSyncGroup();

        if (group.Count <= 1 || (HasSpawnFlags(SpawnFlag.AllowNpcDeath) && StartTime != 0f))
        {
            ScriptEntityCancel(pretendSuccess: false);
            return;
        }

        foreach (var member in group)
        {
            member.ScriptEntityCancel(pretendSuccess: false);
            member.LoopActionSequence = false;
        }
    }

    /// <summary>
    /// Lets go of the NPC before the script is over. Pretending success reports it as having ended, for a
    /// script cut short by a teleport.
    /// </summary>
    internal void ScriptEntityCancel(bool pretendSuccess)
    {
        var npc = TargetNpc;

        if (npc != null)
        {
            // An NPC already held lets go of whichever script holds it
            if (npc.State == BaseNpc.NpcState.Script)
            {
                npc.ScriptedState = BaseNpc.ScriptState.Cleanup;

                var wasFlying = npc.Cine?.savedIsFlying ?? false;
                npc.CineCleanup();
                FixScriptNpcSchedule(npc, wasFlying);
            }
            else
            {
                RestoreNpc(npc);
                npc.Cine = null;

                if (npc.IdealState != BaseNpc.NpcState.Idle)
                {
                    npc.SetIdealState(BaseNpc.NpcState.Idle);
                }
            }

            npc.ReleaseScriptAnimation(keepLocked: false);
        }

        Delay = 0;

        if (pretendSuccess)
        {
            EntitySystem.TriggerOutput(this, "OnEndSequence", npc);
            EntitySystem.TriggerOutput(this, "OnPostIdleEndSequence", npc);
            return;
        }

        EntitySystem.TriggerOutput(this, "OnCancelSequence", npc);

        if (StartTime == 0f)
        {
            EntitySystem.TriggerOutput(this, "OnCancelFailedSequence", npc);
        }
    }

    // The scripts that start their actions together: those sharing the sync group, or failing one, the name
    private List<ScriptedSequence> CollectSyncGroup()
    {
        var name = syncGroup ?? TargetName;

        if (name == null)
        {
            return [this];
        }

        var byGroup = syncGroup != null;

        return EntitySystem.Entities
            .OfType<ScriptedSequence>()
            .Where(script => !script.IsRemoved && string.Equals(byGroup ? script.syncGroup : script.TargetName, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private string? ReadName(string key)
        => KeyValues.GetStringProperty(key) is { Length: > 0 } value ? value : null;

    private string? ReadEntityName(string key)
        => ReadName(key) is { } name ? NameFixup.Apply(name) : null;
}
