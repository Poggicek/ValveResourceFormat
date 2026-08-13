using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>trigger_changelevel</c>. The volume at the end of a level: walking into it is what takes the player
/// to the next map.
/// </summary>
/// <remarks>
/// <para>
/// The engine carries a great deal across a level change - the player, their inventory, and whatever was
/// standing inside a <c>trigger_transition</c> - and lands them at the landmark that matches the one they
/// left. None of that happens here. What happens is the part a viewer can honestly do: the next map is
/// opened, from the beginning, as though it had been opened by hand.
/// </para>
/// <para>
/// The request is published rather than acted on, because the entity system draws a map, it does not decide
/// which map is open. Whoever is hosting the viewer picks the request up and does the opening.
/// </para>
/// </remarks>
public sealed class TriggerChangeLevel : BaseTrigger
{
    /// <summary>Gets the map this leads to, without a path or an extension.</summary>
    public string? NextMap { get; private set; }

    /// <summary>Gets the landmark the arrival is placed at. Recorded only; nothing here places anyone.</summary>
    public string? Landmark { get; private set; }

    /// <summary>
    /// Initializes a <c>trigger_changelevel</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public TriggerChangeLevel(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        InitTrigger();

        NextMap = KeyValues.GetStringProperty("map");
        Landmark = KeyValues.GetStringProperty("landmark");
    }

    /// <inheritdoc/>
    protected override void OnStartTouch(BaseEntity other)
    {
        base.OnStartTouch(other);

        // Only the player leaves a level; anything else standing in the volume is a passenger at most
        if (other is PlayerEntity)
        {
            ChangeLevel(other);
        }
    }

    /// <summary>Takes the player to the next map, as though they had walked into the volume.</summary>
    /// <param name="data">The input's parameter and sender; the activator is passed along.</param>
    [EntityInput("ChangeLevel")]
    private void InputChangeLevel(EntityInputData data) => ChangeLevel(data.Activator);

    private void ChangeLevel(BaseEntity? activator)
    {
        if (string.IsNullOrEmpty(NextMap))
        {
            return;
        }

        EntitySystem.TriggerOutput(this, "OnChangeLevel", activator);
        EntitySystem.RequestLevelChange(NextMap);
    }
}
