using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>trigger_multiple</c> and the other volumes a map wires up for the fact that something entered them:
/// <c>trigger_once</c>, and the volumes whose own effect is not simulated here, <c>trigger_hurt</c> and
/// <c>trigger_push</c>.
/// </summary>
/// <remarks>
/// <para>
/// The shared behaviour is the whole point: a volume that reports what walks into it, and that can be
/// switched on and off. <c>OnStartTouch</c> and <c>OnEndTouch</c> come from <see cref="BaseTrigger"/>;
/// this adds <c>OnTrigger</c> and the <c>wait</c> that keeps it from firing every tick a player stands
/// inside.
/// </para>
/// <para>
/// What <c>trigger_hurt</c> and <c>trigger_push</c> do to whoever is inside is not simulated: no damage,
/// no push. They are here because maps switch them on and off and read their touches, which is entity I/O
/// this can answer, and a map whose wiring runs is worth more than one whose triggers are inert.
/// </para>
/// </remarks>
public class TriggerMultiple : BaseTrigger
{
    /// <summary>Gets whether the trigger reacts to anything entering it.</summary>
    public bool IsEnabled { get; private set; } = true;

    /// <summary>Gets how long the trigger waits before it may fire <c>OnTrigger</c> again.</summary>
    public float Wait { get; private set; }

    /// <summary>Gets whether the trigger removes itself after firing once.</summary>
    protected bool IsOnce { get; init; }

    private float nextTriggerTime;

    /// <summary>
    /// Initializes a trigger volume from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public TriggerMultiple(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        InitTrigger();

        Wait = KeyValues.GetFloatProperty("wait", 1f);

        if (KeyValues.GetBooleanProperty("startdisabled"))
        {
            SetEnabled(false);
        }
    }

    /// <inheritdoc/>
    protected override void OnStartTouch(BaseEntity other)
    {
        base.OnStartTouch(other);

        if (EntitySystem.CurrentTime < nextTriggerTime)
        {
            return;
        }

        nextTriggerTime = EntitySystem.CurrentTime + MathF.Max(Wait, 0f);

        EntitySystem.TriggerOutput(this, "OnTrigger", other);

        if (IsOnce)
        {
            EntitySystem.Remove(this);
        }
    }

    /// <summary>Lets the trigger react to what enters it again.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Enable")]
    private void InputEnable(EntityInputData data) => SetEnabled(true);

    /// <summary>Stops the trigger reacting, and closes whatever it currently holds.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Disable")]
    private void InputDisable(EntityInputData data) => SetEnabled(false);

    /// <summary>Switches the trigger between enabled and disabled.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Toggle")]
    private void InputToggle(EntityInputData data) => SetEnabled(!IsEnabled);

    /// <summary>
    /// Switching a trigger off clears <see cref="BaseEntity.IsTrigger"/>, which stops the touch pass from
    /// testing it at all, and closes what it already holds so whatever was inside hears that it left.
    /// </summary>
    private void SetEnabled(bool enabled)
    {
        IsEnabled = enabled;
        IsTrigger = enabled;

        if (!enabled)
        {
            ClearTouchLinks();
        }
    }
}

/// <summary>
/// <c>trigger_once</c>. A <see cref="TriggerMultiple"/> that removes itself the first time it fires.
/// </summary>
public sealed class TriggerOnce : TriggerMultiple
{
    /// <summary>
    /// Initializes a <c>trigger_once</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public TriggerOnce(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
        IsOnce = true;
    }
}
