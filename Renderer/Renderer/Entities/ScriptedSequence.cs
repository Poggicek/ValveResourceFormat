using System.Globalization;
using Microsoft.Extensions.Logging;
using ValveResourceFormat.ResourceTypes.ModelAnimation;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>scripted_sequence</c>. Takes an actor and plays a set of animations on it: how a map stages a scene.
/// </summary>
/// <remarks>
/// <para>
/// The engine's version seizes an NPC from its AI, walks it to the script's position and hands it back
/// afterwards. There is no AI here to seize it from, which leaves the part that matters to a viewer: the
/// animations, in order, and the outputs that report where the scene has got to. A map's scenes are wired
/// end to end through those outputs - <c>OnEndSequence</c> is the most connected output in a Half-Life
/// Alyx level - so a script that runs to completion is what lets the next one start.
/// </para>
/// <para>
/// The flow is the engine's, minus the walking: pre-action idle while waiting, then the entry animation,
/// then the action animation, then the post-action idle. Several scripts sharing one name is how a scene
/// starts its actors together, and that falls out of entity I/O delivering the input to all of them.
/// </para>
/// <para>
/// The script events are the third thing a scene hangs on this: <c>OnScriptEvent01</c> to <c>08</c> are
/// fired by the action animation itself, from the <c>AE_SCRIPT_EVENT_FIREEVENT</c> events an animator put
/// on it. A map uses them for the beats that have to land on a frame rather than a second - a level ending
/// when a hand comes down, rather than a fixed time after the scene began.
/// </para>
/// <para>
/// Not simulated: interruption and the enqueueing of one script behind another, both of which describe what
/// an NPC does between scripts; and the actor's facing, since nothing turns to look at anything.
/// </para>
/// </remarks>
public sealed class ScriptedSequence : BaseEntity
{
    /// <summary>What a <c>scripted_sequence</c>'s <c>spawnflags</c> mean.</summary>
    [Flags]
    public enum SpawnFlag : uint
    {
        /// <summary>May be run more than once.</summary>
        Repeatable = 4,

        /// <summary>Leaves a corpse rather than fading. Nothing dies here.</summary>
        LeaveCorpse = 8,

        /// <summary>Takes its actor as the map opens and leaves it in the pre-action idle.</summary>
        StartOnSpawn = 16,

        /// <summary>Cannot be interrupted by the actor's AI. There is none.</summary>
        NoInterruptions = 32,

        /// <summary>Overrides the actor's AI state.</summary>
        OverrideAi = 64,

        /// <summary>Leaves the actor where the animation ended rather than where it started.</summary>
        DontTeleportAtEnd = 128,

        /// <summary>Keeps playing the post-action idle until the script is cancelled.</summary>
        LoopInPostIdle = 256,

        /// <summary>Takes precedence over another script wanting the same actor.</summary>
        PriorityScript = 512,
    }

    /// <summary>How the actor gets to the script's position, the <c>m_fMoveTo</c> keyvalue.</summary>
    public enum MoveToMode
    {
        /// <summary>Plays where it already stands.</summary>
        None,

        /// <summary>Walks there.</summary>
        Walk,

        /// <summary>Runs there.</summary>
        Run,

        /// <summary>Moves there playing a named animation.</summary>
        CustomMovement,

        /// <summary>Arrives outright.</summary>
        Instantaneous,

        /// <summary>Plays where it stands, but turns to face the script.</summary>
        TurnToFace,
    }

    /// <summary>How far a script has got.</summary>
    public enum ScriptState
    {
        /// <summary>Has not taken its actor yet.</summary>
        Waiting,

        /// <summary>Holding its actor in the pre-action idle, waiting to be told to begin.</summary>
        PreIdle,

        /// <summary>Playing the entry animation.</summary>
        Entry,

        /// <summary>Playing the action animation.</summary>
        Action,

        /// <summary>Playing the post-action idle.</summary>
        PostIdle,

        /// <summary>Finished or cancelled.</summary>
        Done,
    }

    /// <summary>Gets the name or classname of the actor this script animates.</summary>
    public string? ActorName { get; private set; }

    /// <summary>Gets the animation played while waiting to begin.</summary>
    public string? IdleAnimation { get; private set; }

    /// <summary>Gets the animation played into the action, if any.</summary>
    public string? EntryAnimation { get; private set; }

    /// <summary>Gets the animation the script exists to play.</summary>
    public string? ActionAnimation { get; private set; }

    /// <summary>Gets the animation played once the action is over.</summary>
    public string? PostIdleAnimation { get; private set; }

    /// <summary>Gets the script run straight after this one, on the same actor.</summary>
    public string? NextScript { get; private set; }

    /// <summary>Gets how the actor reaches the script's position.</summary>
    public MoveToMode MoveTo { get; private set; }

    /// <summary>Gets whether the action animation repeats until the script is cancelled.</summary>
    public bool LoopsAction { get; private set; }

    /// <summary>Gets how far the script has got.</summary>
    public ScriptState State { get; private set; }

    /// <summary>Gets the actor this script animates, once it has been found.</summary>
    public PropDynamic? Actor { get; private set; }

    /// <summary>How many script events a scene may hang on an animation, the FGD's <c>OnScriptEvent01</c> to 08.</summary>
    private const int ScriptEventCount = 8;

    private float actionDuration;
    private bool hasSearchedForActor;
    private PropDynamic? listeningTo;

    /// <summary>
    /// Initializes a <c>scripted_sequence</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public ScriptedSequence(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        ActorName = KeyValues.GetStringProperty("m_iszentity");
        IdleAnimation = KeyValues.GetStringProperty("m_iszidle");
        EntryAnimation = KeyValues.GetStringProperty("m_iszentry");
        ActionAnimation = KeyValues.GetStringProperty("m_iszplay");
        PostIdleAnimation = KeyValues.GetStringProperty("m_iszpostidle");
        NextScript = KeyValues.GetStringProperty("m_isznextscript");
        MoveTo = (MoveToMode)KeyValues.GetInt32Property("m_fmoveto");
        LoopsAction = KeyValues.GetBooleanProperty("m_bloopactionsequence");
    }

    /// <inheritdoc/>
    public override void Activate()
    {
        // A script with no name has nothing that could ever tell it to begin, and one flagged to start on
        // spawn is the map saying its actor should already be in place when the level opens. Either way it
        // takes its actor now and holds it in the pre-action idle, as the engine's Spawn does.
        if (HasSpawnFlags(SpawnFlag.StartOnSpawn) || string.IsNullOrEmpty(TargetName))
        {
            TakeActor();
        }
    }

    /// <inheritdoc/>
    public override void Think()
    {
        switch (State)
        {
            case ScriptState.Entry:
                StartAction();
                break;

            case ScriptState.Action when LoopsAction:
                // A looping action goes round until something cancels it, reporting each pass
                EntitySystem.TriggerOutput(this, "OnActionStartOrLoop", this);
                ScheduleAfter(actionDuration);
                break;

            case ScriptState.Action:
                ActionDone();
                break;

            case ScriptState.PostIdle:
                PostIdleDone();
                break;
        }
    }

    /// <summary>
    /// Puts the actor in place and plays the pre-action idle, leaving it there until the script is told to
    /// begin.
    /// </summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("MoveToPosition")]
    private void InputMoveToPosition(EntityInputData data) => TakeActor();

    /// <summary>Plays the scene.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("BeginSequence")]
    private void InputBeginSequence(EntityInputData data)
    {
        if (State is ScriptState.Entry or ScriptState.Action)
        {
            return;
        }

        if (State is ScriptState.Done or ScriptState.PostIdle && !HasSpawnFlags(SpawnFlag.Repeatable))
        {
            return;
        }

        if (!TakeActor())
        {
            return;
        }

        var entry = Actor!.PlayAnimationByName(EntryAnimation, looping: false);

        if (entry < 0f)
        {
            StartAction();
            return;
        }

        State = ScriptState.Entry;

        ScheduleAfter(entry);
    }

    /// <summary>
    /// Stops the script. The actor is left as it stands, since there is no AI here for it to return to.
    /// </summary>
    /// <param name="data">The input's parameter and sender; the activator is passed along.</param>
    [EntityInput("CancelSequence")]
    private void InputCancelSequence(EntityInputData data)
    {
        // A script cancelled before its action ever ran is worth reporting separately, which is what a map
        // waiting on a scene that never happened listens for
        var neverPlayed = State is ScriptState.Waiting or ScriptState.PreIdle;

        if (State == ScriptState.Done)
        {
            return;
        }

        State = ScriptState.Done;

        SetNextThink(-1f);
        StopListeningForScriptEvents();

        EntitySystem.TriggerOutput(this, "OnCancelSequence", data.Activator);

        if (neverPlayed)
        {
            EntitySystem.TriggerOutput(this, "OnCancelFailedSequence", data.Activator);
        }
    }

    /// <summary>Changes which animation the action plays, for a scene that branches.</summary>
    /// <param name="data">Carries the animation name.</param>
    [EntityInput("SetActionSequence")]
    private void InputSetActionSequence(EntityInputData data)
    {
        if (!string.IsNullOrEmpty(data.Parameter))
        {
            ActionAnimation = data.Parameter;
        }
    }

    /// <summary>
    /// Finds the actor, places it and puts it in the pre-action idle. Repeated calls only redo the pose.
    /// </summary>
    /// <returns><see langword="true"/> when there is an actor to animate.</returns>
    private bool TakeActor()
    {
        FindActor();

        if (Actor == null)
        {
            return false;
        }

        PlaceActor();

        if (State == ScriptState.Waiting)
        {
            State = ScriptState.PreIdle;

            // Held rather than played: this is the pose the actor waits in
            Actor.PlayAnimationByName(IdleAnimation, looping: true);
        }

        return true;
    }

    /// <summary>Resolves <see cref="ActorName"/>, which the map spells as a name or as a classname.</summary>
    private void FindActor()
    {
        if (Actor is { IsRemoved: false } || hasSearchedForActor)
        {
            return;
        }

        hasSearchedForActor = true;

        if (string.IsNullOrEmpty(ActorName))
        {
            EntitySystem.Logger.LogWarning("scripted_sequence '{TargetName}' names no actor to animate", TargetName);
            return;
        }

        foreach (var found in EntitySystem.FindTargets(ActorName, caller: this))
        {
            // Anything the entity system draws as a model can be animated; an actor is only the usual one
            if (found is PropDynamic prop)
            {
                Actor = prop;
                return;
            }
        }

        EntitySystem.Logger.LogWarning("scripted_sequence '{TargetName}' could not find actor '{Actor}'", TargetName, ActorName);
    }

    /// <summary>
    /// Moves the actor to where the script wants it, for the scripts that ask. Most do not: a mapper
    /// staging a scene usually places the actors where they already need to stand.
    /// </summary>
    /// <remarks>
    /// The script's own origin and angles are handed over as they are, which is right while the two share
    /// a frame - both plain map entities, or both children of the same <c>point_template</c>. A pairing
    /// across two different spawners would need converting out to the world and back, so it is refused
    /// rather than moving the actor somewhere it was never meant to be.
    /// </remarks>
    private void PlaceActor()
    {
        if (Actor == null || MoveTo is MoveToMode.None or MoveToMode.TurnToFace)
        {
            return;
        }

        if (Actor.ParentTransform != ParentTransform)
        {
            EntitySystem.Logger.LogWarning(
                "scripted_sequence '{TargetName}' and its actor '{Actor}' were spawned by different entities, leaving the actor where it is",
                TargetName, ActorName);
            return;
        }

        Actor.Teleport(Origin, Angles);
    }

    /// <summary>Starts the animation the script exists to play.</summary>
    private void StartAction()
    {
        State = ScriptState.Action;

        // Listened to only while the action runs: the script events belong to that animation, and an actor
        // going about anything else afterwards is not performing this script
        ListenForScriptEvents();

        EntitySystem.TriggerOutput(this, "OnBeginSequence", this);

        actionDuration = Actor?.PlayAnimationByName(ActionAnimation, LoopsAction) ?? -1f;

        EntitySystem.TriggerOutput(this, "OnActionStartOrLoop", this);

        // Nothing to play, so the action is over as soon as it began. The engine does the same rather than
        // leaving the script holding an actor for good.
        if (actionDuration < 0f)
        {
            ActionDone();
            return;
        }

        if (!LoopsAction || actionDuration > 0f)
        {
            ScheduleAfter(actionDuration);
        }
    }

    /// <summary>
    /// Watches the actor's animation for the script events the scene hangs outputs on.
    /// </summary>
    private void ListenForScriptEvents()
    {
        if (listeningTo == Actor || Actor?.ModelNode is not { } model)
        {
            return;
        }

        StopListeningForScriptEvents();

        model.AnimationController.SequenceEventFired += OnAnimationEvent;
        listeningTo = Actor;
    }

    /// <summary>Stops watching, so an actor handed on to something else is no longer this script's to hear.</summary>
    private void StopListeningForScriptEvents()
    {
        if (listeningTo?.ModelNode is { } model)
        {
            model.AnimationController.SequenceEventFired -= OnAnimationEvent;
        }

        listeningTo = null;
    }

    /// <summary>
    /// Turns an animator's script event into the output the map wired to it. The event carries the number
    /// as <c>eventindex</c> in its data, which is what picks out one of the eight.
    /// </summary>
    private void OnAnimationEvent(AnimationEvent animationEvent)
    {
        if (!animationEvent.Name.Equals("AE_SCRIPT_EVENT_FIREEVENT", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var index = animationEvent.EventData?.GetInt32Property("eventindex") ?? 0;

        if (index < 1 || index > ScriptEventCount)
        {
            EntitySystem.Logger.LogWarning(
                "scripted_sequence '{TargetName}' animation fired script event {Index}, which is not one of the eight",
                TargetName, index);
            return;
        }

        EntitySystem.TriggerOutput(this, string.Create(CultureInfo.InvariantCulture, $"OnScriptEvent{index:00}"), this);
    }

    /// <inheritdoc/>
    protected override void OnRemove()
    {
        StopListeningForScriptEvents();

        base.OnRemove();
    }

    /// <summary>The action animation has finished: start the post idle and report the scene done.</summary>
    private void ActionDone()
    {
        StopListeningForScriptEvents();

        State = ScriptState.PostIdle;

        var loops = HasSpawnFlags(SpawnFlag.LoopInPostIdle);
        var duration = Actor?.PlayAnimationByName(PostIdleAnimation, loops) ?? -1f;

        // Fired as the post idle starts rather than once it ends, as the engine's SequenceDone does: the
        // action is what the rest of the map was waiting on. No activator, as there: a scene ending is not
        // something anybody did, so a downstream !activator has nothing to stand for.
        EntitySystem.TriggerOutput(this, "OnEndSequence");

        if (duration < 0f)
        {
            PostIdleDone();
            return;
        }

        // A post idle told to loop holds the actor until something cancels the script
        if (!loops)
        {
            ScheduleAfter(duration);
        }
    }

    /// <summary>The script is spent: hand the actor to the next one, if the map named one.</summary>
    private void PostIdleDone()
    {
        State = ScriptState.Done;

        EntitySystem.TriggerOutput(this, "OnPostIdleEndSequence");

        if (!string.IsNullOrEmpty(NextScript))
        {
            EntitySystem.QueueInputByTargetName(NextScript, "BeginSequence", caller: this);
        }
    }

    /// <summary>
    /// Wakes the script when an animation is due to end. Never sooner than the next tick, so a pose with
    /// no length to it still hands on rather than stalling the script.
    /// </summary>
    private void ScheduleAfter(float seconds)
        => SetNextThink(EntitySystem.CurrentTime + MathF.Max(seconds, EntitySystem.TickInterval));
}
