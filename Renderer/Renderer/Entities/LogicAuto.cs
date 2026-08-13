namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>logic_auto</c>. Fires once when the map starts, and is what sets a level's opening state: the lights
/// that begin on, the relay that starts the first scene, the state a map assumes before anything is touched.
/// </summary>
/// <remarks>
/// The outputs go out on the first tick rather than as the map loads, so that everything they address has
/// spawned and the entity I/O queue is running to carry them. <c>OnMapTransition</c> is not fired: nothing
/// here arrives from another map, and a map that leans on it is describing a journey the viewer never made.
/// </remarks>
public sealed class LogicAuto : BaseEntity
{
    /// <summary>What a <c>logic_auto</c>'s <c>spawnflags</c> mean.</summary>
    [Flags]
    public enum SpawnFlag : uint
    {
        /// <summary>Removes itself once it has fired, which is the Hammer default.</summary>
        RemoveOnFire = 1,
    }

    /// <summary>
    /// Initializes a <c>logic_auto</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public LogicAuto(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Activate()
    {
        SetNextThink(EntitySystem.CurrentTime + EntitySystem.TickInterval);
    }

    /// <summary>Fires the map's opening outputs.</summary>
    public override void Think()
    {
        // A viewer opening a map is always starting it fresh, so both of these describe what happened
        EntitySystem.TriggerOutput(this, "OnMapSpawn");
        EntitySystem.TriggerOutput(this, "OnNewGame");

        if (HasSpawnFlags(SpawnFlag.RemoveOnFire))
        {
            EntitySystem.Remove(this);
        }
    }
}
