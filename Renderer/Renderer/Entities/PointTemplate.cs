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
/// built once, up front. <c>ForceSpawn</c> and <c>DeleteCreatedSpawnGroups</c> therefore show and hide
/// that one copy rather than creating and destroying it.
/// </para>
/// <para>
/// What that costs: a template spawned twice over gives one set of entities rather than two, and a map
/// that never spawns its templates still shows their contents, which is what the viewer did before any of
/// this existed. What it buys is that the inputs work at all for every classname a template can hold,
/// including the ones the entity system cannot build.
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

    /// <summary>Shows the template's entities.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("ForceSpawn")]
    private void InputForceSpawn(EntityInputData data) => SetSpawned(true);

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
            // toggled afterwards cannot bring back something the map deleted
            if (node.EntityInstance is { } entity)
            {
                entity.IsDrawn = spawned;
            }
            else
            {
                node.LayerEnabled = spawned;
            }
        }
    }
}
