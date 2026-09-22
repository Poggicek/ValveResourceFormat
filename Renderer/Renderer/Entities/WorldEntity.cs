using ValveResourceFormat.ResourceTypes;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>worldspawn</c>, Source's <c>CWorld</c>: the root of the entity hierarchy. Every world has exactly
/// one, from the keyvalues of the map that supplies the entity world's root.
/// </summary>
/// <remarks>
/// A map streamed in next to that one brings a <c>worldspawn</c> of its own, which stays an ordinary
/// entity. It is what carries that map's static collision, so the collision leaves with the map.
/// </remarks>
public sealed class WorldEntity : BaseEntity
{
    /// <summary>Initializes the world from the map's authored worldspawn keyvalues.</summary>
    public WorldEntity(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <summary>Draws nothing; the world geometry already draws itself.</summary>
    protected override SceneNode? CreateRootNode() => null;

    /// <summary>
    /// Makes this world solid with the static collision of the map it came with, placed where the map's
    /// spawn group put it. The map the viewer opened keeps its own in <see cref="EntitySystem.PhysicsWorld"/>.
    /// </summary>
    /// <param name="physics">The map's world physics.</param>
    internal void SetStaticCollision(PhysAggregateData physics)
    {
        Collider = new EntityCollider(physics);
        UpdateColliderTransform();
    }
}
