using Microsoft.Extensions.Logging;
using ValveResourceFormat.Renderer.Audio;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>ambient_generic</c>. Plays one sound, either looping from the moment the map starts or fired by
/// entity I/O.
/// </summary>
/// <remarks>
/// Not simulated: the pitch controls, the dynamic presets, and the LFO and spin-up modulation, none of
/// which the sound player exposes. <c>message</c> is looked up as a sound event; the engine also accepts a
/// bare <c>.wav</c> path there, which resolves to nothing here.
/// </remarks>
public sealed class AmbientGeneric : BaseEntity
{
    /// <summary>What an <c>ambient_generic</c>'s <c>spawnflags</c> mean.</summary>
    [Flags]
    public enum SpawnFlag : uint
    {
        /// <summary>No flags: a looping sound, audible from its own position, playing from the start.</summary>
        None = 0,

        /// <summary>Heard everywhere at full volume, rather than from a point in the world.</summary>
        PlayEverywhere = 1,

        /// <summary>Waits to be told to play instead of starting with the map.</summary>
        StartSilent = 16,

        /// <summary>Plays once per trigger rather than looping.</summary>
        NotLooping = 32,
    }

    /// <summary>Gets the sound event this plays, the <c>message</c> keyvalue.</summary>
    public string? SoundName { get; private set; }

    /// <summary>Gets the volume, 0 to 1. Authored as <c>health</c>, which runs 0 to 10.</summary>
    public float Volume { get; private set; } = 1f;

    /// <summary>Gets whether the sound repeats until stopped.</summary>
    public bool IsLooping => !HasSpawnFlags(SpawnFlag.NotLooping);

    /// <summary>Gets whether the sound is playing.</summary>
    public bool IsPlaying => playing is { Playing: true };

    private SoundEvent? playing;
    private SceneNode? soundSource;
    private float fadeInSeconds;
    private float fadeOutSeconds;

    /// <summary>
    /// Initializes an <c>ambient_generic</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public AmbientGeneric(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <summary>Tests whether any of the given <c>spawnflags</c> bits are set.</summary>
    /// <param name="flags">The bits to test. Any one of them being set is a match.</param>
    private bool HasSpawnFlags(SpawnFlag flags) => ((SpawnFlag)SpawnFlags & flags) != 0;

    /// <inheritdoc/>
    public override void Spawn()
    {
        SoundName = KeyValues.GetStringProperty("message");

        // Authored 0 to 10, and the engine emits it as a fraction of full volume
        Volume = Math.Clamp(KeyValues.GetFloatProperty("health", 10f), 0f, 10f) / 10f;

        fadeInSeconds = KeyValues.GetFloatProperty("fadeinsecs");
        fadeOutSeconds = KeyValues.GetFloatProperty("fadeoutsecs");

        if (string.IsNullOrEmpty(SoundName))
        {
            EntitySystem.Logger.LogWarning("ambient_generic '{TargetName}' has no sound to play", TargetName);
        }
    }

    /// <inheritdoc/>
    public override void Activate()
    {
        var sourceName = KeyValues.GetStringProperty("sourceentityname");

        if (!string.IsNullOrEmpty(sourceName))
        {
            soundSource = Scene.FindNodeByTargetName(sourceName);

            if (soundSource == null)
            {
                EntitySystem.Logger.LogWarning("ambient_generic '{TargetName}' source entity '{Source}' was not found", TargetName, sourceName);
            }
        }

        if (HasSpawnFlags(SpawnFlag.StartSilent) || !IsLooping)
        {
            return;
        }

        // Left to the first tick rather than started here: entities activate while the rest of the map is
        // still loading, and the map should not be audible before it is on screen.
        SetNextThink(EntitySystem.CurrentTime + EntitySystem.TickInterval);
    }

    /// <inheritdoc/>
    public override void Think() => StartSound();

    /// <summary>
    /// Starts the sound, restarting it if it was already going.
    /// </summary>
    public void StartSound()
    {
        if (string.IsNullOrEmpty(SoundName))
        {
            return;
        }

        StopSound();

        playing = Sound.Play(SoundName, GetEmitPosition(), volume: Volume);

        if (playing != null && fadeInSeconds > 0f)
        {
            playing.FadeIn(fadeInSeconds);
        }
    }

    /// <summary>
    /// Stops the sound, fading it out first when the entity was authored to.
    /// </summary>
    public void StopSound()
    {
        if (playing == null)
        {
            return;
        }

        if (fadeOutSeconds > 0f)
        {
            playing.FadeOutAndStop(fadeOutSeconds);
        }
        else
        {
            playing.Stop();
        }

        playing = null;
    }

    /// <inheritdoc/>
    public override void Delete()
    {
        playing?.Stop();
        playing = null;

        base.Delete();
    }

    /// <summary>Starts the sound, unless a looping one is already playing.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("PlaySound")]
    private void InputPlaySound(EntityInputData data)
    {
        // A looping ambient already playing is left alone; a one-shot fires again
        if (IsLooping && IsPlaying)
        {
            return;
        }

        StartSound();
    }

    /// <summary>Stops the sound.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("StopSound")]
    private void InputStopSound(EntityInputData data) => StopSound();

    /// <summary>Starts the sound if it is stopped, stops it if it is playing.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("ToggleSound")]
    private void InputToggleSound(EntityInputData data)
    {
        if (IsPlaying)
        {
            StopSound();
        }
        else
        {
            StartSound();
        }
    }

    /// <summary>Sets the volume, on a 0 to 10 scale, taking effect on the sound already playing.</summary>
    /// <param name="data">Carries the volume.</param>
    [EntityInput("Volume")]
    private void InputVolume(EntityInputData data)
    {
        Volume = Math.Clamp(data.Float(10f), 0f, 10f) / 10f;

        if (playing != null)
        {
            playing.VolumeOverride = Volume;
        }
    }

    /// <summary>Fades the sound up over the given number of seconds, starting it if it is silent.</summary>
    /// <param name="data">Carries the fade time in seconds.</param>
    [EntityInput("FadeIn")]
    private void InputFadeIn(EntityInputData data)
    {
        if (!IsPlaying)
        {
            StartSound();
        }

        playing?.FadeIn(MathF.Max(data.Float(), 0f));
    }

    /// <summary>Fades the sound down to silence over the given number of seconds.</summary>
    /// <param name="data">Carries the fade time in seconds.</param>
    [EntityInput("FadeOut")]
    private void InputFadeOut(EntityInputData data)
    {
        playing?.FadeOutAndStop(MathF.Max(data.Float(), 0f));
        playing = null;
    }

    /// <summary>
    /// Where the sound emits from: the source entity if one was named, this entity otherwise, or nowhere
    /// in particular when it is set to play everywhere.
    /// </summary>
    private Vector3? GetEmitPosition()
    {
        if (HasSpawnFlags(SpawnFlag.PlayEverywhere))
        {
            return null;
        }

        return soundSource?.Transform.Translation ?? Origin;
    }
}
