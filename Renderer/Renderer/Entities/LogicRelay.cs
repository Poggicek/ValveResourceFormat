using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>logic_relay</c>. A named place to send an output so that one trigger can drive many things, and so
/// that a map can switch a whole branch of its wiring on and off from one entity.
/// </summary>
/// <remarks>
/// The plumbing of a map rather than anything in the world: it draws nothing, occupies no space, and only
/// passes an input on as an output. That makes it the cheapest thing an entity system can implement and
/// usually the most valuable, since a map's buttons and triggers tend to fire relays rather than their
/// eventual targets.
/// </remarks>
public sealed class LogicRelay : BaseEntity
{
    /// <summary>What a <c>logic_relay</c>'s <c>spawnflags</c> mean.</summary>
    [Flags]
    public enum SpawnFlag : uint
    {
        /// <summary>Fires once and then switches itself off for good.</summary>
        OnlyOnce = 1,

        /// <summary>May be triggered again while a previous trigger is still waiting on its delay.</summary>
        AllowFastRetrigger = 2,
    }

    /// <summary>Gets whether the relay passes anything on. The <c>Disable</c> input clears it.</summary>
    public bool IsEnabled { get; private set; } = true;

    private bool isWaiting;

    /// <summary>
    /// Initializes a <c>logic_relay</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public LogicRelay(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        IsEnabled = !KeyValues.GetBooleanProperty("startdisabled");
    }

    /// <summary>Passes the trigger on as <c>OnTrigger</c>.</summary>
    /// <param name="data">Carries the activator, which is passed along to whatever the relay fires.</param>
    [EntityInput("Trigger")]
    private void InputTrigger(EntityInputData data)
    {
        if (!IsEnabled || (isWaiting && !HasSpawnFlags(SpawnFlag.AllowFastRetrigger)))
        {
            return;
        }

        // Held until the outputs have been queued, so a relay wired back into itself cannot recurse
        isWaiting = true;

        EntitySystem.TriggerOutput(this, "OnTrigger", data.Activator);

        isWaiting = false;

        if (HasSpawnFlags(SpawnFlag.OnlyOnce))
        {
            IsEnabled = false;
        }
    }

    /// <summary>Lets the relay pass triggers on again.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Enable")]
    private void InputEnable(EntityInputData data) => IsEnabled = true;

    /// <summary>Stops the relay passing anything on.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Disable")]
    private void InputDisable(EntityInputData data) => IsEnabled = false;

    /// <summary>Switches the relay between enabled and disabled.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Toggle")]
    private void InputToggle(EntityInputData data) => IsEnabled = !IsEnabled;

    /// <summary>
    /// Cancels triggers this relay has fired that have not landed yet, Source's <c>CancelPending</c>.
    /// </summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("CancelPending")]
    private void InputCancelPending(EntityInputData data) => EntitySystem.CancelQueuedInputsFrom(this);
}
