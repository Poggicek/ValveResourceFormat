namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>info_spawngroup_landmark</c>. A point two maps share, by which an <c>info_spawngroup_load_unload</c>
/// lines up the map it streams in with the world it streams it into.
/// </summary>
public sealed class InfoSpawnGroupLandmark : BaseEntity
{
    /// <summary>Initializes an <c>info_spawngroup_landmark</c> from its keyvalues.</summary>
    public InfoSpawnGroupLandmark(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <summary>Gets where the landmark stands in the world, its spawn group's placement included.</summary>
    public Vector3 WorldOrigin => Vector3.Transform(Origin, ParentTransform);
}
