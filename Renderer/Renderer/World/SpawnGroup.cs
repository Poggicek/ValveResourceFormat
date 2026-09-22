namespace ValveResourceFormat.Renderer.World;

/// <summary>Where a <see cref="SpawnGroup"/> is in its life.</summary>
public enum SpawnGroupState
{
    /// <summary>Requested, and waiting for the next frame boundary to be loaded.</summary>
    Queued,

    /// <summary>Its world is drawn and its entities are in the entity world.</summary>
    Loaded,

    /// <summary>Still loaded, and waiting for the next frame boundary to be unloaded.</summary>
    Unloading,

    /// <summary>Gone: unloaded, or its map failed to load.</summary>
    Unloaded,
}

/// <summary>
/// A map loaded into the entity world, Source's spawn group: its world geometry, baked lighting and
/// collision, and the entities it spawned. The map the viewer opened is one, its 3D sky is another, and
/// so is every map an <c>info_spawngroup_load_unload</c> streams in, which can be unloaded again without
/// touching the rest.
/// </summary>
/// <remarks>
/// Each spawn group draws into a <see cref="Renderer.Scene"/> of its own. Baked lighting - the lightmaps,
/// the cubemap array, the light probe atlas - and visibility belong to one world and are bound per scene,
/// so two worlds cannot share one. The scenes of a <see cref="World.WorldGroup"/> are drawn through the
/// same camera instead.
/// </remarks>
public sealed class SpawnGroup
{
    internal SpawnGroup(int handle, string mapName, SpawnGroup? parent, WorldGroup worldGroup, Scene scene, bool isStreamed)
    {
        Handle = handle;
        MapName = mapName;
        Parent = parent;
        WorldGroup = worldGroup;
        Scene = scene;
        IsStreamed = isStreamed;
    }

    /// <summary>Gets the handle the spawn group is known by, unique for as long as the entity world lives.</summary>
    public int Handle { get; }

    /// <summary>
    /// Gets the map the spawn group loads, as a spawn group names it: relative to <c>maps/</c> and without
    /// an extension, such as <c>de_dust2</c> or <c>stages/stage1</c>.
    /// </summary>
    public string MapName { get; }

    /// <summary>Gets the spawn group this one was loaded from, or <see langword="null"/> for the map the viewer opened.</summary>
    public SpawnGroup? Parent { get; }

    /// <summary>Gets the world group the spawn group was placed in.</summary>
    public WorldGroup WorldGroup { get; }

    /// <summary>Gets the scene the spawn group's world and entities draw into.</summary>
    public Scene Scene { get; }

    /// <summary>Gets the transform placing the map in its world group, applied to everything it loads.</summary>
    public Matrix4x4 Transform { get; internal set; } = Matrix4x4.Identity;

    /// <summary>Gets where the spawn group is in its life.</summary>
    public SpawnGroupState State { get; internal set; }

    /// <summary>Gets whether the spawn group's world is in the scene, including while it waits to be unloaded.</summary>
    public bool IsLoaded => State is SpawnGroupState.Loaded or SpawnGroupState.Unloading;

    /// <summary>Gets the loader that loaded the map, once it has.</summary>
    public WorldLoader? World { get; internal set; }

    /// <summary>
    /// Gets the landmark a streamed in spawn group is placed by: its name, and where the landmark of that
    /// name stood in the world when the load was requested. <see langword="null"/> when it was loaded where
    /// it was built.
    /// </summary>
    public (string Name, Vector3 Origin)? Landmark { get; internal init; }

    /// <summary>
    /// Gets whether the spawn group was streamed in on request, rather than loaded along with the map the
    /// viewer opened. Only these can be unloaded again.
    /// </summary>
    public bool IsStreamed { get; }

    /// <summary>
    /// Whether <see cref="Scene"/> was made for this spawn group, and goes when it does: everything but the
    /// map the viewer opened, which draws into the renderer's own scene.
    /// </summary>
    internal bool OwnsScene { get; init; } = true;

    /// <inheritdoc/>
    public override string ToString() => $"{MapName} ({Handle})";
}
