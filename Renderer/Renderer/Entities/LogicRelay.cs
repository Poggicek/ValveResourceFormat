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
    /// <summary>What a <c>logic_relay</c>'s <c>spawnflags</c> mean, in the Source 1 maps that use them.</summary>
    [Flags]
    public enum SpawnFlag : uint
    {
        /// <summary>Fires once and is then gone.</summary>
        OnlyOnce = 1,

        /// <summary>May be triggered again while a previous trigger is still waiting on its delay.</summary>
        AllowFastRetrigger = 2,
    }

    /// <summary>Gets whether the relay passes anything on. The <c>Disable</c> input clears it.</summary>
    public bool IsEnabled { get; private set; } = true;

    /// <summary>Gets whether the relay is gone once it has fired.</summary>
    public bool TriggersOnce { get; private set; }

    /// <summary>Gets whether a trigger arriving before the last one has finished is passed on anyway.</summary>
    public bool AllowsFastRetrigger { get; private set; }

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

        // Source 1 spelled both of these as spawnflags; Source 2 gives them keyvalues of their own, and a
        // map compiled from it carries no flags at all, so a relay read only from flags never triggers once
        TriggersOnce = KeyValues.GetBooleanProperty("triggeronce") || HasSpawnFlags(SpawnFlag.OnlyOnce);
        AllowsFastRetrigger = KeyValues.GetBooleanProperty("fastretrigger") || HasSpawnFlags(SpawnFlag.AllowFastRetrigger);
    }

    /// <inheritdoc/>
    public override void Activate()
    {
        // Fired once the map is up rather than as the relay itself is built, so that whatever it drives
        // exists to be driven. A relay that only triggers once is spent on this as much as on a Trigger.
        if (!HasOnSpawn)
        {
            return;
        }

        EntitySystem.TriggerOutput(this, "OnSpawn", this);

        if (TriggersOnce)
        {
            EntitySystem.Remove(this);
        }
    }

    /// <summary>Whether the map wired anything to <c>OnSpawn</c>.</summary>
    private bool HasOnSpawn
    {
        get
        {
            if (Data?.Connections == null)
            {
                return false;
            }

            foreach (var connection in Data.Connections)
            {
                if (connection.OutputName.Equals("OnSpawn", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Passes the trigger on as <c>OnTrigger</c>.</summary>
    /// <param name="data">Carries the activator, which is passed along to whatever the relay fires.</param>
    [EntityInput("Trigger")]
    private void InputTrigger(EntityInputData data)
    {
        if (!IsEnabled || (isWaiting && !AllowsFastRetrigger))
        {
            return;
        }

        // Held until the outputs have been queued, so a relay wired back into itself cannot recurse
        isWaiting = true;

        EntitySystem.TriggerOutput(this, "OnTrigger", data.Activator);

        isWaiting = false;

        if (TriggersOnce)
        {
            EntitySystem.Remove(this);
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
