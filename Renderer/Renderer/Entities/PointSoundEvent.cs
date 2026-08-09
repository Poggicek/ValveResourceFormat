using ValveResourceFormat.Renderer.Audio;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>point_soundevent</c>. Plays a sound event, either the moment the map starts or whenever entity I/O
/// tells it to.
/// </summary>
/// <remarks>
/// Where the sound comes from is settled when it starts: flat on the listener for a "to local player"
/// event, otherwise the source entity's attachment or origin, falling back to this entity's own.
/// </remarks>
public sealed class PointSoundEvent : BaseEntity
{
    /// <summary>Gets the sound event this plays.</summary>
    public string? SoundName { get; private set; }

    /// <summary>Gets whether the sound is playing.</summary>
    public bool IsPlaying => playing is { Playing: true };

    private SoundEvent? playing;

    /// <summary>
    /// Initializes a <c>point_soundevent</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public PointSoundEvent(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        SoundName = KeyValues.GetStringProperty("soundname");
    }

    /// <inheritdoc/>
    public override void Activate()
    {
        if (!KeyValues.GetBooleanProperty("startonspawn"))
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

        playing = Sound.Play(SoundName, GetEmitPosition());
    }

    /// <summary>
    /// Stops the sound, if it is playing.
    /// </summary>
    public void StopSound()
    {
        playing?.Stop();
        playing = null;
    }

    /// <inheritdoc/>
    public override void Delete()
    {
        StopSound();

        base.Delete();
    }

    /// <summary>Starts the sound.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("StartSound")]
    private void InputStartSound(EntityInputData data) => StartSound();

    /// <summary>Stops the sound.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("StopSound")]
    private void InputStopSound(EntityInputData data) => StopSound();

    /// <summary>
    /// Where the sound emits from, or <see langword="null"/> for an event played flat on the listener
    /// rather than placed in the world.
    /// </summary>
    private Vector3? GetEmitPosition()
    {
        if (KeyValues.GetBooleanProperty("tolocalplayer"))
        {
            return null;
        }

        var sourceEntityName = KeyValues.GetStringProperty("sourceentityname");

        if (string.IsNullOrEmpty(sourceEntityName)
            || Scene.FindNodeByTargetName(sourceEntityName) is not { } sourceNode)
        {
            return Origin;
        }

        var attachmentName = KeyValues.GetStringProperty("sourceentityattachment");

        if (!string.IsNullOrEmpty(attachmentName)
            && sourceNode is ModelSceneNode sourceModel
            && sourceModel.Attachments.ContainsKey(attachmentName))
        {
            return sourceModel.GetAttachmentTransform(attachmentName).Translation;
        }

        // The offset is authored relative to the source, and this entity was placed at the result of it
        if (KeyValues.GetBooleanProperty("uselocaloffset"))
        {
            return Origin;
        }

        return sourceNode.Transform.Translation;
    }
}
