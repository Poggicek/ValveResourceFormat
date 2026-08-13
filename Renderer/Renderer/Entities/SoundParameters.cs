using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>snd_event_param</c>. Carries a value into a sound event that is already playing, the way a map
/// changes an engine note or a room's size without restarting the sound.
/// </summary>
/// <remarks>
/// The value is held and reported, not applied: a playing <c>SoundEvent</c> takes its parameters when it
/// starts, so there is nothing to hand a mid-play change to. Wiring that sets a parameter therefore runs
/// to completion rather than stalling, and the sound plays on unchanged.
/// </remarks>
public sealed class SoundEventParameter : BaseEntity
{
    /// <summary>Gets the parameter's name, as the sound event knows it.</summary>
    public string? ParameterName { get; private set; }

    /// <summary>Gets the value last set.</summary>
    public float Value { get; private set; }

    /// <summary>Gets the playing sound the parameter was last pointed at.</summary>
    public string? SoundEventGuid { get; private set; }

    /// <summary>
    /// Initializes a <c>snd_event_param</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public SoundEventParameter(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        ParameterName = KeyValues.GetStringProperty("parametername");
        Value = KeyValues.GetFloatProperty("floatvalue");
    }

    /// <summary>Sets the value the parameter would carry.</summary>
    /// <param name="data">Carries the new value.</param>
    [EntityInput("SetFloatValue")]
    private void InputSetFloatValue(EntityInputData data) => Value = data.Float(Value);

    /// <summary>Names the playing sound the parameter belongs to.</summary>
    /// <param name="data">Carries the sound event's identifier.</param>
    [EntityInput("SetSoundEventGUID")]
    private void InputSetSoundEventGuid(EntityInputData data)
        // Held for the same reason the value is: nothing here can reach into a sound already playing
        => SoundEventGuid = data.Parameter;
}

/// <summary>
/// The <c>snd_opvar_set_</c> volumes. A region that sets a mixing operator variable while the player is
/// inside it, which is how a map makes a room sound like a room.
/// </summary>
/// <remarks>
/// The mixing graph these drive is not simulated, so the value is held rather than applied and the sound
/// does not change with the space. The volume still reports entering and leaving, which is wiring a map
/// can hang other things on.
/// </remarks>
public sealed class SoundOpvarSet : BaseEntity
{
    /// <summary>Gets whether the volume is setting its variable.</summary>
    public bool IsEnabled { get; private set; } = true;

    /// <summary>Gets the value the variable takes while the volume is off.</summary>
    public float DisabledValue { get; private set; }

    /// <summary>
    /// Initializes a sound operator variable volume from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public SoundOpvarSet(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        IsEnabled = !KeyValues.GetBooleanProperty("startdisabled");
    }

    /// <summary>Lets the volume set its variable again.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Enable")]
    private void InputEnable(EntityInputData data) => IsEnabled = true;

    /// <summary>Stops the volume setting its variable.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Disable")]
    private void InputDisable(EntityInputData data) => IsEnabled = false;

    /// <summary>Sets the value the variable takes while the volume is off.</summary>
    /// <param name="data">Carries the value.</param>
    [EntityInput("SetDisabledValue")]
    private void InputSetDisabledValue(EntityInputData data) => DisabledValue = data.Float(DisabledValue);
}
