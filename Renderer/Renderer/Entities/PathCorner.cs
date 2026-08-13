using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>path_corner</c>. A stop on a route: a position, how fast to travel from it, how long to wait there,
/// and the corner that comes next.
/// </summary>
/// <remarks>
/// Nothing follows these yet. They are the route for trains and for walking NPCs, and the levels that use
/// them most are the ones whose NPCs are not simulated here. The chain is resolved and readable so that
/// whatever comes to follow it has the route ready, and so a map's corners answer entity I/O meanwhile.
/// </remarks>
public sealed class PathCorner : BaseEntity
{
    /// <summary>Gets the corner that follows this one, or <see langword="null"/> at the end of the route.</summary>
    public PathCorner? Next { get; private set; }

    /// <summary>Gets how long to wait at this corner, in seconds.</summary>
    public float Wait { get; private set; }

    /// <summary>Gets the speed to travel at from this corner, or zero to keep the current one.</summary>
    public float NewSpeed { get; private set; }

    /// <summary>
    /// Initializes a <c>path_corner</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public PathCorner(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        Wait = KeyValues.GetFloatProperty("wait");
        NewSpeed = KeyValues.GetFloatProperty("speed");
    }

    /// <inheritdoc/>
    public override void Activate()
    {
        var targetName = KeyValues.GetStringProperty("target");

        if (string.IsNullOrEmpty(targetName))
        {
            return;
        }

        foreach (var target in EntitySystem.FindTargets(targetName, caller: this))
        {
            if (target is PathCorner next)
            {
                Next = next;
                break;
            }
        }
    }

    /// <summary>Changes how long something waits here.</summary>
    /// <param name="data">Carries the new wait in seconds.</param>
    [EntityInput("SetWait")]
    private void InputSetWait(EntityInputData data) => Wait = data.Float(Wait);

    /// <summary>Reports something reaching this corner, for a map wired to notice.</summary>
    /// <param name="data">The input's parameter and sender; the activator is passed along.</param>
    [EntityInput("InPass")]
    private void InputInPass(EntityInputData data)
        => EntitySystem.TriggerOutput(this, "OnPass", data.Activator);
}
