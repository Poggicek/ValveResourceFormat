using Microsoft.Extensions.Logging;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer.Entities;

namespace ValveResourceFormat.Renderer.World;

/// <summary>
/// Loads a queued spawn group's map into its scene: its world, and the entities it spawns into the
/// entity world.
/// </summary>
/// <param name="spawnGroup">The spawn group to load, with its scene and landmark already set up.</param>
/// <returns>Whether the map loaded.</returns>
public delegate bool SpawnGroupLoader(SpawnGroup spawnGroup);

/// <summary>
/// Every spawn group of an entity world, Source's spawn group manager. The maps loaded while the viewer
/// opens a map are added as they load; the ones entity I/O asks for are queued, and loaded or unloaded
/// at the next frame boundary, outside any walk over the entities.
/// </summary>
public sealed class SpawnGroupManager
{
    private readonly EntitySystem entitySystem;
    private readonly List<SpawnGroup> queue = [];

    // Replaced rather than mutated, so a walk over it survives a spawn group loading or unloading
    private SpawnGroup[] spawnGroups = [];
    private int nextHandle = 1;

    /// <summary>Initializes the spawn groups of an entity world.</summary>
    /// <param name="entitySystem">The entity world the spawn groups spawn their entities into.</param>
    public SpawnGroupManager(EntitySystem entitySystem)
    {
        ArgumentNullException.ThrowIfNull(entitySystem);

        this.entitySystem = entitySystem;
        Loader = spawnGroup => WorldLoader.LoadSpawnGroup(spawnGroup, entitySystem);
    }

    /// <summary>Gets the world group of the map the viewer opened, and of every map it streams in.</summary>
    public WorldGroup MainWorldGroup { get; private set; } = new("main");

    /// <summary>Gets every spawn group that has not been unloaded, in the order they were created.</summary>
    public IReadOnlyList<SpawnGroup> SpawnGroups => spawnGroups;

    /// <summary>Gets the spawn group of the map the viewer opened, once it has been added.</summary>
    public SpawnGroup? Root { get; private set; }

    /// <summary>
    /// Gets or sets what loads a queued spawn group. The default loads the map with <see cref="WorldLoader"/>.
    /// </summary>
    public SpawnGroupLoader Loader { get; set; }

    /// <summary>
    /// Reduces a map name to the form spawn groups are named and found by: relative to <c>maps/</c>, with
    /// forward slashes, and without an extension.
    /// </summary>
    /// <param name="mapName">A map name in any of the forms maps refer to one by.</param>
    /// <returns>The spawn group name.</returns>
    public static string NormalizeMapName(string mapName)
    {
        ArgumentNullException.ThrowIfNull(mapName);

        var name = mapName.Trim().Replace('\\', '/');

        if (name.EndsWith(GameFileLoader.CompiledFileSuffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^GameFileLoader.CompiledFileSuffix.Length];
        }

        if (name.EndsWith(".vmap", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..name.LastIndexOf('.')];
        }

        if (name.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
        {
            name = name["maps/".Length..];
        }

        return name;
    }

    /// <summary>
    /// Adds a spawn group whose map is loaded right away rather than queued: the map the viewer opened,
    /// which has no parent and draws into the scene it is given, or a 3D sky loaded as part of it.
    /// </summary>
    internal SpawnGroup Add(string mapName, SpawnGroup? parent, WorldGroup worldGroup, Scene scene, Matrix4x4 transform)
    {
        var spawnGroup = new SpawnGroup(nextHandle++, NormalizeMapName(mapName), parent, worldGroup, scene, isStreamed: false)
        {
            Transform = transform,
            State = SpawnGroupState.Loaded,
            OwnsScene = parent != null,
        };

        Register(spawnGroup);

        if (parent == null)
        {
            Root ??= spawnGroup;
        }

        return spawnGroup;
    }

    private void Register(SpawnGroup spawnGroup)
    {
        spawnGroup.Scene.WorldGroup = spawnGroup.WorldGroup;
        spawnGroup.WorldGroup.Add(spawnGroup);
        spawnGroups = [.. spawnGroups, spawnGroup];
    }

    /// <summary>Finds the spawn group of a map, the way the engine finds one by name.</summary>
    /// <param name="mapName">The map, in any form <see cref="NormalizeMapName"/> accepts.</param>
    /// <returns>The spawn group, whether it is loaded yet or not, or <see langword="null"/> when there is none.</returns>
    public SpawnGroup? Find(string mapName)
    {
        var name = NormalizeMapName(mapName);

        foreach (var spawnGroup in spawnGroups)
        {
            if (spawnGroup.MapName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return spawnGroup;
            }
        }

        return null;
    }

    /// <summary>
    /// Queues a map to be streamed into the world group of <paramref name="parent"/> at the next frame
    /// boundary. When a landmark is given, the map is moved so that the entity of that name in it lands on
    /// <paramref name="landmarkOrigin"/>.
    /// </summary>
    /// <param name="mapName">The map to load.</param>
    /// <param name="parent">The spawn group asking for it; the map the viewer opened when <see langword="null"/>.</param>
    /// <param name="landmarkName">The landmark both maps share, or <see langword="null"/> to load the map where it was built.</param>
    /// <param name="landmarkOrigin">Where the landmark stands in the world now.</param>
    /// <returns>
    /// The queued spawn group, or <see langword="null"/> when that map is already loaded or on its way,
    /// which the engine refuses too.
    /// </returns>
    public SpawnGroup? RequestLoad(string mapName, SpawnGroup? parent, string? landmarkName, Vector3 landmarkOrigin)
    {
        if (Find(mapName) != null)
        {
            return null;
        }

        parent ??= Root;

        var worldGroup = parent?.WorldGroup ?? MainWorldGroup;
        var scene = new Scene(entitySystem.RendererContext);

        if (parent != null)
        {
            scene.CopyRenderSettings(parent.Scene);
        }

        var spawnGroup = new SpawnGroup(nextHandle++, NormalizeMapName(mapName), parent, worldGroup, scene, isStreamed: true)
        {
            Landmark = string.IsNullOrEmpty(landmarkName) ? null : (landmarkName, landmarkOrigin),
            State = SpawnGroupState.Queued,
        };

        Register(spawnGroup);
        queue.Add(spawnGroup);

        return spawnGroup;
    }

    /// <summary>
    /// Queues a streamed in spawn group to be unloaded at the next frame boundary, together with every spawn
    /// group it streamed in. What was loaded with the map the viewer opened stays.
    /// </summary>
    /// <param name="spawnGroup">The spawn group to unload.</param>
    /// <returns>Whether the unload was queued.</returns>
    public bool RequestUnload(SpawnGroup spawnGroup)
    {
        ArgumentNullException.ThrowIfNull(spawnGroup);

        if (!spawnGroup.IsStreamed || spawnGroup.State is SpawnGroupState.Unloading or SpawnGroupState.Unloaded)
        {
            return false;
        }

        // One that never got to load has nothing to wait for
        if (spawnGroup.State == SpawnGroupState.Queued)
        {
            queue.Remove(spawnGroup);
            Unload(spawnGroup);
            return true;
        }

        spawnGroup.State = SpawnGroupState.Unloading;
        queue.Add(spawnGroup);

        return true;
    }

    /// <summary>
    /// Loads and unloads what was queued since the last frame boundary. Called by
    /// <see cref="EntitySystem.Update"/> at the start of every frame, before its ticks.
    /// </summary>
    internal void ServiceQueue()
    {
        if (queue.Count == 0)
        {
            return;
        }

        var serviced = queue.ToArray();
        queue.Clear();

        foreach (var spawnGroup in serviced)
        {
            switch (spawnGroup.State)
            {
                case SpawnGroupState.Queued:
                    Load(spawnGroup);
                    break;

                case SpawnGroupState.Unloading:
                    Unload(spawnGroup);
                    break;
            }
        }
    }

    private void Load(SpawnGroup spawnGroup)
    {
        bool loaded;

        try
        {
            loaded = Loader(spawnGroup);
        }
        catch (Exception e)
        {
            // Loaded mid-frame, where nothing above expects a map to throw: a broken or cancelled load
            // leaves the rest of the world running without it
            if (e is not OperationCanceledException)
            {
                entitySystem.Logger.LogError(e, "Failed to load spawn group {SpawnGroup}", spawnGroup.MapName);
            }

            loaded = false;
        }

        if (!loaded)
        {
            // Whatever it got to spawn before it failed goes again
            Unload(spawnGroup);
            return;
        }

        spawnGroup.State = SpawnGroupState.Loaded;
    }

    private void Unload(SpawnGroup spawnGroup)
    {
        foreach (var child in spawnGroups)
        {
            if (child.Parent == spawnGroup && child.State != SpawnGroupState.Unloaded)
            {
                queue.Remove(child);
                Unload(child);
            }
        }

        entitySystem.RemoveEntities(spawnGroup);

        if (spawnGroup.OwnsScene)
        {
            spawnGroup.Scene.DeleteAllNodes();
            spawnGroup.Scene.Dispose();
        }

        spawnGroup.State = SpawnGroupState.Unloaded;
        spawnGroup.WorldGroup.Remove(spawnGroup);
        spawnGroups = Array.FindAll(spawnGroups, group => group != spawnGroup);
    }

    /// <summary>
    /// Forgets every spawn group, releasing the scenes that were made for them. The entities are the entity
    /// world's to drop, and the nodes the renderer's.
    /// </summary>
    internal void Clear()
    {
        foreach (var spawnGroup in spawnGroups)
        {
            spawnGroup.State = SpawnGroupState.Unloaded;

            if (spawnGroup.OwnsScene)
            {
                spawnGroup.Scene.Dispose();
            }
        }

        spawnGroups = [];
        queue.Clear();
        nextHandle = 1;
        Root = null;
        MainWorldGroup = new("main");
    }
}
