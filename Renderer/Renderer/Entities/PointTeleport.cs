using Microsoft.Extensions.Logging;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>point_teleport</c>. Moves the entity it names to this entity's own place, when told to. The teleport
/// a map fires from its wiring rather than one a player walks into.
/// </summary>
public sealed class PointTeleport : BaseEntity
{
    /// <summary>What a <c>point_teleport</c>'s <c>spawnflags</c> mean.</summary>
    [Flags]
    public enum SpawnFlag : uint
    {
        /// <summary>Keeps whatever the target was facing instead of adopting this entity's angles.</summary>
        IntoDuck = 1,

        /// <summary>Teleports the target home rather than here. Not simulated: nothing records a home.</summary>
        TeleportHome = 2,
    }

    /// <summary>
    /// Initializes a <c>point_teleport</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public PointTeleport(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <summary>Moves the named target here.</summary>
    /// <param name="data">Its activator is what a <c>!activator</c> target resolves to.</param>
    [EntityInput("Teleport")]
    private void InputTeleport(EntityInputData data)
    {
        var targetName = KeyValues.GetStringProperty("target");

        if (string.IsNullOrEmpty(targetName))
        {
            EntitySystem.Logger.LogWarning("point_teleport '{TargetName}' has nothing to teleport", TargetName);
            return;
        }

        foreach (var target in EntitySystem.FindTargets(targetName, data.Activator, this))
        {
            target.Teleport(Origin, Angles);
        }
    }
}
