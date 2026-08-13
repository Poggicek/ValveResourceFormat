using System.Globalization;
using Microsoft.Extensions.Logging;
using ValveResourceFormat.Renderer.Audio;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.Choreo;
using ValveResourceFormat.ResourceTypes.Choreo.Enums;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>logic_choreographed_scene</c>. Plays a <c>.vcd</c>: the timed script behind a conversation, with the
/// lines its actors speak and the cues it hands back to the map.
/// </summary>
/// <remarks>
/// <para>
/// A choreographed scene is a timeline. Much of what is on it belongs to a face - expressions, flex
/// tracks, where an actor is looking - and none of that has anywhere to go here. Three kinds of event do:
/// the lines, which are sound events with a time and a speaker; the sequences, which name the animation an
/// actor performs and are what makes a scene something you can watch rather than only hear; and the
/// triggers, which are the map's own wiring hung off the timeline. A map uses those triggers to keep in
/// step with a scene it cannot otherwise see into, so a scene that fires them is a scene the rest of the
/// map can follow.
/// </para>
/// <para>
/// Scenes are not stored one file per scene: the compiler packs them into a <c>vcdlist</c> per directory,
/// so <c>scenes/act1/hallway/eli_hallway.vcd</c> is a scene named <c>act1/hallway/eli_hallway.vcd</c>
/// inside <c>scenes/act1_hallway.vcdlist_c</c>, and one at the top of <c>scenes/</c> lives in
/// <c>scenes/_root.vcdlist_c</c>.
/// </para>
/// <para>
/// Not simulated: everything an actor's face does, the gesture overlays that play on top of a sequence,
/// waiting on an actor who is already speaking (<c>busyactor</c>), pitch shifting and interjected
/// responses.
/// </para>
/// </remarks>
public sealed class LogicChoreographedScene : BaseEntity
{
    /// <summary>How many triggers a scene can hand back, matching the FGD's <c>OnTrigger1</c> to 16.</summary>
    private const int TriggerCount = 16;

    /// <summary>One thing the scene does, and when.</summary>
    /// <param name="Time">Seconds from the start of the scene.</param>
    /// <param name="Event">The authored event.</param>
    /// <param name="ActorName">The actor it belongs to, or <see langword="null"/> for the scene itself.</param>
    private readonly record struct TimelineEntry(float Time, ChoreoEvent Event, string? ActorName);

    /// <summary>Gets the scene file this plays, as the map spells it.</summary>
    public string? SceneFile { get; private set; }

    /// <summary>Gets how long the scene runs, in seconds.</summary>
    public float Duration { get; private set; }

    /// <summary>Gets whether the scene is running, paused included.</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>Gets whether a running scene is held where it is.</summary>
    public bool IsPaused { get; private set; }

    private readonly List<TimelineEntry> timeline = [];
    private readonly List<SoundEvent> speaking = [];
    private ChoreoScene? scene;
    private float startTime;
    private float pausedAt;
    private int nextEntry;
    private bool cancelAtInterrupt;
    private bool pauseAtInterrupt;

    /// <summary>
    /// Initializes a <c>logic_choreographed_scene</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public LogicChoreographedScene(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        SceneFile = KeyValues.GetStringProperty("scenefile");
    }

    /// <inheritdoc/>
    public override void Activate()
    {
        LoadScene();
    }

    /// <inheritdoc/>
    public override void Think()
    {
        if (!IsPlaying || IsPaused)
        {
            return;
        }

        var elapsed = EntitySystem.CurrentTime - startTime;

        while (nextEntry < timeline.Count && timeline[nextEntry].Time <= elapsed)
        {
            var entry = timeline[nextEntry];

            // Stepped past before it runs, so a cancel reached through it cannot see it again
            nextEntry++;

            Perform(entry);

            // An interrupt event may have stopped or held the scene, and the rest of this instant's
            // timeline belongs to a scene that is no longer running
            if (!IsPlaying || IsPaused)
            {
                return;
            }
        }

        if (elapsed >= Duration && nextEntry >= timeline.Count)
        {
            IsPlaying = false;

            EntitySystem.TriggerOutput(this, "OnCompletion");
            return;
        }

        ScheduleNext(elapsed);
    }

    /// <summary>Plays the scene from the beginning.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Start")]
    private void InputStart(EntityInputData data)
    {
        if (scene == null)
        {
            return;
        }

        StopSpeaking();

        IsPlaying = true;
        IsPaused = false;
        cancelAtInterrupt = false;
        pauseAtInterrupt = false;
        startTime = EntitySystem.CurrentTime;
        nextEntry = 0;

        EntitySystem.TriggerOutput(this, "OnStart");

        // Woken on the first tick rather than run now, so an event at time zero lands after the outputs
        // this start has already queued
        SetNextThink(EntitySystem.CurrentTime + EntitySystem.TickInterval);
    }

    /// <summary>Holds the scene where it is.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Pause")]
    private void InputPause(EntityInputData data) => Pause();

    /// <summary>Holds the scene when it next reaches a point it may be broken off at.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("PauseAtNextInterrupt")]
    private void InputPauseAtNextInterrupt(EntityInputData data)
    {
        if (IsPlaying && !IsPaused)
        {
            pauseAtInterrupt = true;
        }
    }

    /// <summary>Carries on from where a paused scene left off.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Resume")]
    private void InputResume(EntityInputData data)
    {
        if (!IsPlaying || !IsPaused)
        {
            return;
        }

        IsPaused = false;

        // The clock moves rather than the scene: the elapsed time it was holding at is restored by
        // pretending the scene started that long ago
        startTime = EntitySystem.CurrentTime - pausedAt;

        EntitySystem.TriggerOutput(this, "OnResumed");

        SetNextThink(EntitySystem.CurrentTime + EntitySystem.TickInterval);
    }

    /// <summary>Stops the scene, and whatever it had speaking.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Cancel")]
    private void InputCancel(EntityInputData data) => Cancel();

    /// <summary>
    /// Stops the scene when it next reaches a point it may be broken off at. A scene with none authored
    /// runs to its end, as it does in the engine.
    /// </summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("CancelAtNextInterrupt")]
    private void InputCancelAtNextInterrupt(EntityInputData data)
    {
        if (IsPlaying)
        {
            cancelAtInterrupt = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnRemove()
    {
        StopSpeaking();

        base.OnRemove();
    }

    /// <summary>Does whatever one timeline entry says to do.</summary>
    private void Perform(TimelineEntry entry)
    {
        switch (entry.Event.Type)
        {
            case ChoreoEventType.FireTrigger:
                FireTrigger(entry.Event);
                break;

            case ChoreoEventType.Speak:
                Speak(entry.Event, entry.ActorName);
                break;

            case ChoreoEventType.Sequence:
                PlaySequence(entry.Event, entry.ActorName);
                break;

            case ChoreoEventType.Interrupt:
                ReachedInterrupt();
                break;
        }
    }

    /// <summary>
    /// Plays the animation an actor performs for this scene. The scene names it outright, which is what
    /// makes a choreographed scene able to animate anyone: the actor need only own the animation.
    /// </summary>
    private void PlaySequence(ChoreoEvent choreoEvent, string? actorName)
    {
        var animationName = choreoEvent.Param1;

        if (string.IsNullOrEmpty(animationName) || FindActor(actorName) is not PropDynamic actor)
        {
            return;
        }

        // The event spans exactly as long as the animation runs, so it is played through once rather than
        // left repeating underneath the rest of the scene
        if (actor.PlayAnimationByName(animationName, looping: false) < 0f)
        {
            EntitySystem.Logger.LogWarning(
                "logic_choreographed_scene '{TargetName}' actor '{Actor}' has no animation \"{Animation}\"",
                TargetName, actorName, animationName);
        }
    }

    /// <summary>
    /// A point the scene may be broken off at. A cancel or pause that arrived mid-line waits for one of
    /// these, so a scene stops where it was authored to be interruptible rather than mid-word.
    /// </summary>
    private void ReachedInterrupt()
    {
        if (cancelAtInterrupt)
        {
            Cancel();
            return;
        }

        if (pauseAtInterrupt)
        {
            pauseAtInterrupt = false;

            Pause();
        }
    }

    /// <summary>Hands a numbered cue back to the map, which is what the scene is wired into it for.</summary>
    private void FireTrigger(ChoreoEvent choreoEvent)
    {
        // The number is the event's first parameter, as the engine's CChoreoEvent::GetParameters reads it
        if (!int.TryParse(choreoEvent.Param1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            || number < 1 || number > TriggerCount)
        {
            EntitySystem.Logger.LogWarning(
                "logic_choreographed_scene '{TargetName}' has a trigger event with no usable number: '{Param}'",
                TargetName, choreoEvent.Param1);
            return;
        }

        EntitySystem.TriggerOutput(this, $"OnTrigger{number}", this);
    }

    /// <summary>Plays a line, from wherever the actor saying it is standing.</summary>
    private void Speak(ChoreoEvent choreoEvent, string? actorName)
    {
        var soundName = choreoEvent.Param1;

        if (string.IsNullOrEmpty(soundName))
        {
            return;
        }

        // The player's own lines are their own voice, so they play flat rather than from a point in the
        // world. Anyone else speaks from where they stand, or from the scene entity when the actor is one
        // this map never spawned.
        var position = IsPlayerActor(actorName)
            ? null
            : FindActor(actorName)?.WorldOrigin ?? (Vector3?)Transform.Translation;

        if (Sound.Play(soundName, position) is { } playing)
        {
            speaking.Add(playing);
            return;
        }

        EntitySystem.Logger.LogWarning(
            "logic_choreographed_scene '{TargetName}' could not play \"{SoundName}\" for actor '{Actor}'",
            TargetName, soundName, actorName);
    }

    /// <summary>Wakes the scene when the next thing on it is due, or when it runs out.</summary>
    private void ScheduleNext(float elapsed)
    {
        var next = nextEntry < timeline.Count ? MathF.Min(timeline[nextEntry].Time, Duration) : Duration;

        // Never sooner than the next tick, so a run of events sharing a time cannot spin
        SetNextThink(EntitySystem.CurrentTime + MathF.Max(next - elapsed, EntitySystem.TickInterval));
    }

    private void Pause()
    {
        if (!IsPlaying || IsPaused)
        {
            return;
        }

        IsPaused = true;
        pausedAt = EntitySystem.CurrentTime - startTime;

        SetNextThink(-1f);

        EntitySystem.TriggerOutput(this, "OnPaused");
    }

    private void Cancel()
    {
        if (!IsPlaying)
        {
            return;
        }

        IsPlaying = false;
        IsPaused = false;
        cancelAtInterrupt = false;
        pauseAtInterrupt = false;

        SetNextThink(-1f);
        StopSpeaking();

        EntitySystem.TriggerOutput(this, "OnCanceled");
    }

    /// <summary>Silences the lines this scene started, for a scene cut off part way through.</summary>
    private void StopSpeaking()
    {
        foreach (var playing in speaking)
        {
            playing.Stop();
        }

        speaking.Clear();
    }

    /// <summary>
    /// Finds the entity playing one of the scene's actors.
    /// </summary>
    /// <remarks>
    /// A scene names its actors as they were authored - "eli" - while the map's entities carry whatever
    /// prefix the prefab they came from was given, so the same actor is <c>[PR#]eli</c> in the entity lump.
    /// The scene entity sits in that same prefab, so its own name is where the prefix comes from.
    /// </remarks>
    private BaseEntity? FindActor(string? actorName)
    {
        if (string.IsNullOrEmpty(actorName))
        {
            return null;
        }

        if (IsPlayerActor(actorName))
        {
            return EntitySystem.Player;
        }

        foreach (var found in EntitySystem.FindTargets(actorName, caller: this))
        {
            return found;
        }

        var prefix = NamePrefix();

        if (prefix.Length == 0)
        {
            return null;
        }

        foreach (var found in EntitySystem.FindTargets(prefix + actorName, caller: this))
        {
            return found;
        }

        return null;
    }

    /// <summary>The prefab prefix this entity's own name carries, up to and including its closing bracket.</summary>
    private string NamePrefix()
    {
        var end = TargetName?.IndexOf(']', StringComparison.Ordinal) ?? -1;

        return end < 0 ? string.Empty : TargetName![..(end + 1)];
    }

    private static bool IsPlayerActor(string? actorName)
        => string.Equals(actorName, "!player", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Loads the scene and flattens it into one list of what happens when. The scene's own events and every
    /// actor's channels are the same timeline authored in separate tracks, and playback only cares about
    /// the order.
    /// </summary>
    private void LoadScene()
    {
        if (string.IsNullOrEmpty(SceneFile))
        {
            return;
        }

        var listName = SceneListFor(SceneFile);

        if (listName == null)
        {
            EntitySystem.Logger.LogWarning(
                "logic_choreographed_scene '{TargetName}' scene file '{SceneFile}' is not under scenes/", TargetName, SceneFile);
            return;
        }

        // Not disposed: the file loader owns and caches what it hands out, and the scenes of one directory
        // are shared by every scene entity that plays one of them
        if (EntitySystem.FileLoader.LoadFileCompiled(listName)?.DataBlock is not ChoreoSceneFileData scenes)
        {
            EntitySystem.Logger.LogWarning(
                "logic_choreographed_scene '{TargetName}' could not load scene list \"{List}\"", TargetName, listName);
            return;
        }

        var sceneName = SceneFile["scenes/".Length..];

        foreach (var candidate in scenes.Scenes)
        {
            if (string.Equals(candidate.Name, sceneName, StringComparison.OrdinalIgnoreCase))
            {
                scene = candidate;
                break;
            }
        }

        if (scene == null)
        {
            EntitySystem.Logger.LogWarning(
                "logic_choreographed_scene '{TargetName}' found no scene '{Scene}' in \"{List}\"", TargetName, sceneName, listName);
            return;
        }

        // Authored in milliseconds, which is also the only place the scene's length is recorded: the
        // events themselves are in seconds
        Duration = scene.Duration / 1000f;

        foreach (var choreoEvent in scene.Events)
        {
            timeline.Add(new TimelineEntry(choreoEvent.StartTime, choreoEvent, null));
        }

        foreach (var actor in scene.Actors)
        {
            foreach (var channel in actor.Channels)
            {
                foreach (var choreoEvent in channel.Events)
                {
                    timeline.Add(new TimelineEntry(choreoEvent.StartTime, choreoEvent, actor.Name));
                }
            }
        }

        timeline.Sort(static (left, right) => left.Time.CompareTo(right.Time));
    }

    /// <summary>
    /// Works out which <c>vcdlist</c> holds a scene. The compiler packs one per directory, named after the
    /// path with its separators flattened, and the scenes sitting directly under <c>scenes/</c> go into
    /// <c>_root</c>.
    /// </summary>
    /// <param name="sceneFile">The scene path as the map authored it.</param>
    /// <returns>The list to load, or <see langword="null"/> when the path is not a scene path at all.</returns>
    private static string? SceneListFor(string sceneFile)
    {
        const string ScenesFolder = "scenes/";

        var path = sceneFile.Replace('\\', '/');

        if (!path.StartsWith(ScenesFolder, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = path[ScenesFolder.Length..];
        var lastSlash = relative.LastIndexOf('/');

        return lastSlash < 0
            ? ScenesFolder + "_root.vcdlist"
            : ScenesFolder + relative[..lastSlash].Replace('/', '_') + ".vcdlist";
    }
}
