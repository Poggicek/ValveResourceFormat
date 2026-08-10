using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>path_track</c>. One corner of the route a <see cref="FuncTrackTrain"/> drives: a position, the node
/// that comes next, and an output that fires as the train passes through.
/// </summary>
/// <remarks>
/// The nodes are a plain linked list, each naming the next through its <c>target</c>. Resolved once
/// everything has spawned, since a node may well name one that loads after it.
/// </remarks>
public sealed class PathTrack : BaseEntity
{
    /// <summary>Gets the node that follows this one, or <see langword="null"/> at the end of the route.</summary>
    public PathTrack? Next { get; private set; }

    /// <summary>Gets whether a train may stop at this node. Cleared by <c>Disable</c>.</summary>
    public bool IsEnabled { get; private set; } = true;

    /// <summary>
    /// Initializes a <c>path_track</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public PathTrack(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        IsEnabled = !KeyValues.GetBooleanProperty("startdisabled");
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
            if (target is PathTrack next)
            {
                Next = next;
                break;
            }
        }
    }

    /// <summary>Reports a train reaching this node.</summary>
    /// <param name="train">The train that arrived, passed on as the activator.</param>
    internal void Pass(BaseEntity train) => EntitySystem.TriggerOutput(this, "OnPass", train);

    /// <summary>Lets trains use this node again.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Enable")]
    private void InputEnable(EntityInputData data) => IsEnabled = true;

    /// <summary>Takes this node out of the route.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Disable")]
    private void InputDisable(EntityInputData data) => IsEnabled = false;
}
