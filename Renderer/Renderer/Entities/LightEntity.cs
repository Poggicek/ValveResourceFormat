using ValveResourceFormat.Renderer.SceneEnvironment;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>light_omni</c> and <c>light_spot</c>. A light a map can switch on and off: the lamp that comes on
/// with the power, the one that dies as something goes wrong.
/// </summary>
/// <remarks>
/// <para>
/// The entity builds the same <see cref="SceneLight"/> the loader would, through the same factory, and owns
/// it: registering a classname takes it out of the loader's hands entirely, so anything the loader did for
/// a light has to be done here instead or the map loses it.
/// </para>
/// <para>
/// Switching one off scales its brightness to zero rather than removing it, which is how the particle
/// light renderers already fade lights, and what <see cref="SceneLight.IsVisible"/> tests each frame. The
/// authored brightness is kept so switching back on restores exactly what the map authored.
/// </para>
/// </remarks>
public sealed class LightEntity : BaseEntity
{
    /// <summary>Gets the light this entity is, or <see langword="null"/> when the classname was not one.</summary>
    public SceneLight? Light { get; private set; }

    /// <summary>Gets whether the light is currently lit.</summary>
    public bool IsLit { get; private set; } = true;

    private float authoredBrightnessScale = 1f;

    /// <summary>
    /// Initializes a light from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public LightEntity(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    protected override SceneNode? CreateRootNode()
    {
        if (Data == null)
        {
            return base.CreateRootNode();
        }

        var accepted = SceneLight.IsAccepted(Classname);

        if (!accepted.Accepted)
        {
            return base.CreateRootNode();
        }

        Light = SceneLight.FromEntityProperties(Scene, accepted.Type, Data);

        // The loader marks lights this way, and a light casting shadows onto itself is not the point
        Light.Flags |= ObjectTypeFlags.NoShadows;

        authoredBrightnessScale = Light.BrightnessScale;

        return Light;
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        // A light the map authored off starts off, and remembers what it was to come back to
        SetLit(KeyValues.GetBooleanProperty("enabled", true));
    }

    /// <summary>Lights it.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("TurnOn")]
    private void InputTurnOn(EntityInputData data) => SetLit(true);

    /// <summary>Puts it out.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("TurnOff")]
    private void InputTurnOff(EntityInputData data) => SetLit(false);

    /// <summary>Lights it if it is out, puts it out if it is lit.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Toggle")]
    private void InputToggle(EntityInputData data) => SetLit(!IsLit);

    /// <summary>Sets whether it is lit from the parameter.</summary>
    /// <param name="data">Carries whether the light should be on.</param>
    [EntityInput("SetLightEnabled")]
    private void InputSetLightEnabled(EntityInputData data) => SetLit(data.Bool());

    private void SetLit(bool lit)
    {
        IsLit = lit;

        if (Light is not { } light)
        {
            return;
        }

        light.BrightnessScale = lit ? authoredBrightnessScale : 0f;

        // The barn faces are rebuilt from the new state on the next frame that considers this light
        light.IsDirty = true;
    }
}
