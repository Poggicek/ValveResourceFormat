using Microsoft.Extensions.Logging;
using ValveResourceFormat.Renderer.World;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>info_spawngroup_load_unload</c>. Streams another map into the world on entity I/O, and out again,
/// as a spawn group of its own.
/// </summary>
/// <remarks>
/// <para>
/// The map is moved so that its entity named <see cref="LandmarkName"/> lands on the
/// <c>info_spawngroup_landmark</c> of that name already in the world. Only the offset between the two is
/// applied: the engine works out the difference in their angles as well, but never uses it.
/// </para>
/// <para>
/// The engine loads in the background and polls on a think until the map is in. Here the map loads at the
/// frame boundary after the request and the next think finds it in, so the started and finished outputs
/// still come at least a tick apart and in the same order. An <c>entityfiltername</c> other than the
/// default is game code the viewer does not have, so every entity of the map spawns.
/// </para>
/// </remarks>
public sealed class InfoSpawnGroupLoadUnload : BaseEntity
{
    /// <summary>
    /// The name of the world layer every map is built on, which the engine makes sure is showing once a
    /// map it streamed in is ready.
    /// </summary>
    private const string BaseWorldLayerName = "world_layer_base";

    private enum Thinking
    {
        None,
        Loading,
        Unloading,
    }

    private Thinking thinking;
    private bool unloadingStarted;

    /// <summary>Initializes an <c>info_spawngroup_load_unload</c> from its keyvalues.</summary>
    public InfoSpawnGroupLoadUnload(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <summary>Gets the map this entity loads and unloads, as a spawn group names it. <c>SetSpawnGroup</c> changes it.</summary>
    public string? MapName { get; private set; }

    /// <summary>Gets the name of the landmark the map is lined up by.</summary>
    public string? LandmarkName { get; private set; }

    /// <summary>Gets the spawn group the last load this entity started went into.</summary>
    public SpawnGroup? LoadedSpawnGroup { get; private set; }

    /// <inheritdoc/>
    public override void Spawn()
    {
        MapName = KeyValues.GetStringProperty("mapname");
        LandmarkName = KeyValues.GetStringProperty("landmark");

        if (string.IsNullOrEmpty(MapName))
        {
            EntitySystem.Logger.LogWarning("info_spawngroup_load_unload '{Name}' has no map name, removing it", TargetName);
            EntitySystem.Remove(this);
        }
    }

    [EntityInput("StartSpawnGroupLoad")]
    private void InputStartSpawnGroupLoad(EntityInputData data)
    {
        if (string.IsNullOrEmpty(MapName))
        {
            return;
        }

        if (string.IsNullOrEmpty(LandmarkName))
        {
            EntitySystem.Logger.LogWarning("StartSpawnGroupLoad on '{Name}' without a landmark, ignoring it", TargetName);
            return;
        }

        // The first entity of that name, which has to be a landmark: the engine does not look past it
        var landmark = FindFirstByName(LandmarkName);

        if (landmark is not InfoSpawnGroupLandmark spawnGroupLandmark)
        {
            EntitySystem.Logger.LogWarning("Loading map '{MapName}', couldn't find landmark named '{Landmark}'", MapName, LandmarkName);
            return;
        }

        // Refused while that map is loaded or on its way, which fires nothing
        var spawnGroup = EntitySystem.SpawnGroups.RequestLoad(MapName, SpawnGroup, LandmarkName, spawnGroupLandmark.WorldOrigin);

        if (spawnGroup == null)
        {
            return;
        }

        LoadedSpawnGroup = spawnGroup;

        EntitySystem.TriggerOutput(this, "OnSpawnGroupLoadStarted", this);

        ThinkNextTick(Thinking.Loading);
    }

    [EntityInput("StartSpawnGroupUnload")]
    private void InputStartSpawnGroupUnload(EntityInputData data)
    {
        // By name rather than by the load this entity started, so it unloads whichever spawn group holds the map
        if (MapName == null || EntitySystem.SpawnGroups.Find(MapName) == null)
        {
            return;
        }

        ThinkNextTick(Thinking.Unloading);
    }

    [EntityInput("ActivateSpawnGroup")]
    private void InputActivateSpawnGroup(EntityInputData data)
    {
        // Still loading: showing it is what finishing the load does anyway
        if (LoadedSpawnGroup is { State: SpawnGroupState.Queued })
        {
            return;
        }

        if (MapName != null && EntitySystem.SpawnGroups.Find(MapName) is { IsLoaded: true } spawnGroup)
        {
            spawnGroup.Scene.ActivateLayer(BaseWorldLayerName);
            return;
        }

        EntitySystem.Logger.LogWarning("ActivateSpawnGroup on '{Name}' with no spawn group loaded, ignoring it", TargetName);
    }

    [EntityInput("SetSpawnGroup")]
    private void InputSetSpawnGroup(EntityInputData data) => MapName = data.Parameter;

    /// <inheritdoc/>
    public override void Think()
    {
        switch (thinking)
        {
            case Thinking.Loading:
                LoadingThink();
                break;

            case Thinking.Unloading:
                UnloadingThink();
                break;
        }
    }

    private void LoadingThink()
    {
        if (LoadedSpawnGroup is not { } spawnGroup || spawnGroup.State == SpawnGroupState.Unloaded)
        {
            // The map failed to load, which the spawn groups already reported
            thinking = Thinking.None;
            return;
        }

        if (!spawnGroup.IsLoaded)
        {
            ThinkNextTick(Thinking.Loading);
            return;
        }

        thinking = Thinking.None;

        EntitySystem.TriggerOutput(this, "OnSpawnGroupLoadFinished", this);

        spawnGroup.Scene.ActivateLayer(BaseWorldLayerName);
    }

    private void UnloadingThink()
    {
        var spawnGroup = MapName == null ? null : EntitySystem.SpawnGroups.Find(MapName);

        if (spawnGroup == null)
        {
            thinking = Thinking.None;

            if (unloadingStarted)
            {
                unloadingStarted = false;
                EntitySystem.TriggerOutput(this, "OnSpawnGroupUnloadFinished", this);
            }

            return;
        }

        if (!unloadingStarted)
        {
            // Already on its way out when another entity asked first, which this one waits on all the same
            if (spawnGroup.State != SpawnGroupState.Unloading && !EntitySystem.SpawnGroups.RequestUnload(spawnGroup))
            {
                EntitySystem.Logger.LogWarning("'{Name}' cannot unload '{MapName}', which was not streamed in", TargetName, MapName);
                thinking = Thinking.None;
                return;
            }

            unloadingStarted = true;
            EntitySystem.TriggerOutput(this, "OnSpawnGroupUnloadStarted", this);
        }

        // Finishes on the first think that no longer finds it
        ThinkNextTick(Thinking.Unloading);
    }

    private void ThinkNextTick(Thinking state)
    {
        thinking = state;
        SetNextThink(EntitySystem.CurrentTime + EntitySystem.TickInterval);
    }

    private BaseEntity? FindFirstByName(string name)
    {
        foreach (var entity in EntitySystem.FindAllByTargetName(name))
        {
            return entity;
        }

        return null;
    }
}
