namespace ValveResourceFormat.Renderer.World;

/// <summary>
/// Spawn groups that share one space, Source's world group. The map the viewer opened is one, together
/// with every map it streams in; its 3D sky is another.
/// </summary>
/// <remarks>
/// The scenes of one world group are drawn through the same camera, and the entities in them touch, push
/// and trace against each other. Entities of different world groups never meet, even where their
/// coordinates overlap, which is what keeps a 3D sky out of reach.
/// </remarks>
public sealed class WorldGroup
{
    // Replaced rather than mutated, so a walk over it survives a spawn group loading or unloading
    private SpawnGroup[] spawnGroups = [];

    /// <summary>Initializes an empty world group.</summary>
    /// <param name="name">The name it is known by.</param>
    public WorldGroup(string name)
    {
        Name = name;
    }

    /// <summary>Gets the name the world group is known by.</summary>
    public string Name { get; }

    /// <summary>Gets the spawn groups placed in this world group, in the order they were created.</summary>
    public IReadOnlyList<SpawnGroup> SpawnGroups => spawnGroups;

    internal void Add(SpawnGroup spawnGroup) => spawnGroups = [.. spawnGroups, spawnGroup];

    internal void Remove(SpawnGroup spawnGroup) => spawnGroups = Array.FindAll(spawnGroups, group => group != spawnGroup);
}
