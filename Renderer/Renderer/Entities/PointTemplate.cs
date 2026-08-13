using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>point_template</c>. Holds a set of entities the map spawns and deletes as a group: the props a
/// minigame needs while it is running, and gets rid of when it is over.
/// </summary>
/// <remarks>
/// <para>
/// The engine builds nothing until <c>ForceSpawn</c>, then makes a fresh copy each time. Here the copy
/// already exists: the loader walks a template's child lump when it loads the map, so the entities are
/// built once, up front. <c>ForceSpawn</c> and <c>DeleteCreatedSpawnGroups</c> therefore wake and put back
/// that one copy rather than creating and destroying it.
/// </para>
/// <para>
/// Until it is spawned, that copy is held dormant: not drawn, not solid, not simulated, and not something
/// entity I/O can find. A scene staged inside a template is then as still and as silent before its cue as
/// it would be if the entities were not there at all, which is what a map counts on when it holds a whole
/// cast ready behind a trigger.
/// </para>
/// <para>
/// The exception is a template nothing in the map ever spawns. There the engine's answer is an empty room,
/// and a viewer showing an empty room tells whoever opened the map nothing at all - so those are left
/// awake, which is what this did for every template before any of it was simulated.
/// </para>
/// <para>
/// What the single copy costs: a template spawned twice over gives one set of entities rather than two.
/// What it buys is that the inputs work at all for every classname a template can hold, including the ones
/// the entity system cannot build.
/// </para>
/// </remarks>
public sealed class PointTemplate : BaseEntity
{
    /// <summary>Gets whether the template's entities are currently shown.</summary>
    public bool IsSpawned { get; private set; } = true;

    private readonly List<SceneNode> children = [];

    /// <summary>
    /// Initializes a <c>point_template</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public PointTemplate(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <summary>
    /// Hands the template the scene nodes the loader built from its child lump.
    /// </summary>
    /// <param name="nodes">The nodes belonging to this template's entities.</param>
    internal void AdoptTemplateChildren(IEnumerable<SceneNode> nodes) => children.AddRange(nodes);

    /// <inheritdoc/>
    public override void Activate()
    {
        // Held back only if the map has some way of asking for it. Checked here rather than at spawn
        // because it is a question about the whole map, and the whole map exists by now.
        if (HasSpawner())
        {
            SetSpawned(false);
        }
    }

    /// <summary>Whether anything in the map fires <c>ForceSpawn</c> at this template.</summary>
    private bool HasSpawner()
    {
        if (string.IsNullOrEmpty(TargetName))
        {
            return false;
        }

        foreach (var entity in EntitySystem.Entities)
        {
            foreach (var connection in entity.Data?.Connections ?? [])
            {
                if (connection.InputName.Equals("ForceSpawn", StringComparison.OrdinalIgnoreCase)
                    && EntityLump.EntityNameMatches(connection.TargetName, TargetName))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Shows the template's entities.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("ForceSpawn")]
    private void InputForceSpawn(EntityInputData data)
    {
        SetSpawned(true);

        // Fired even when the copy was already showing: a map sequences its scenes off this, and the
        // engine fires it for every spawn, not only the first. Without it every chain hanging off a
        // template spawn stops dead, which is most of what a template is wired up to do.
        EntitySystem.TriggerOutput(this, "OnEntitySpawned", data.Activator);
    }

    /// <summary>Hides the template's entities, the map's way of clearing a game away.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("DeleteCreatedSpawnGroups")]
    private void InputDeleteCreatedSpawnGroups(EntityInputData data) => SetSpawned(false);

    private void SetSpawned(bool spawned)
    {
        if (IsSpawned == spawned)
        {
            return;
        }

        IsSpawned = spawned;

        foreach (var node in children)
        {
            // An entity of its own decides this through its own state, so that a visibility layer being
            // toggled afterwards cannot bring back something the map deleted. A classname the entity
            // system does not build has only its node to hide.
            if (node.EntityInstance is { } entity)
            {
                entity.SetDormant(!spawned);
            }
            else
            {
                node.LayerEnabled = spawned;
            }
        }
    }
}
