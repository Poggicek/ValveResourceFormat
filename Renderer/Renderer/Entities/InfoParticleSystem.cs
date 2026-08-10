using Microsoft.Extensions.Logging;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>info_particle_system</c>. An effect a map can start and stop: the fountain that runs while a game is
/// on, the burst that fires when someone wins.
/// </summary>
/// <remarks>
/// <c>Stop</c> plays the system's end cap rather than cutting it dead, which is what the engine does and
/// what makes a stopped effect fade out instead of vanishing. <c>cpoint0</c> is not resolved: an effect
/// whose first control point is another entity draws at its own origin instead of that entity's.
/// </remarks>
public sealed class InfoParticleSystem : BaseEntity
{
    /// <summary>Gets the node the effect draws in, or <see langword="null"/> when it failed to load.</summary>
    public ParticleSceneNode? ParticleNode { get; private set; }

    /// <summary>Gets whether the effect is emitting.</summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Initializes an <c>info_particle_system</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public InfoParticleSystem(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    protected override SceneNode? CreateRootNode()
    {
        var effectName = Data?.GetStringProperty("effect_name");

        if (string.IsNullOrEmpty(effectName))
        {
            return base.CreateRootNode();
        }

        if (EntitySystem.FileLoader.LoadFileCompiled(effectName)?.DataBlock is not ParticleSystem particleSystem)
        {
            EntitySystem.Logger.LogWarning("{Classname} '{TargetName}' failed to load effect \"{Effect}\"", Classname, TargetName, effectName);
            return base.CreateRootNode();
        }

        try
        {
            ParticleNode = new ParticleSceneNode(Scene, particleSystem, LoadSnapshot())
            {
                Name = effectName,
                LayerName = "Particles",
            };
        }
        catch (Exception e)
        {
            EntitySystem.Logger.LogError(e, "Failed to setup particle '{Particle}'", effectName);
            return base.CreateRootNode();
        }

        return ParticleNode;
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        IsActive = KeyValues.GetBooleanProperty("start_active");

        if (!IsActive)
        {
            // Built emitting, so one that waits to be told has to be quietened at spawn
            StopEffect();
        }
    }

    /// <summary>Starts the effect, from the beginning.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Start")]
    private void InputStart(EntityInputData data)
    {
        IsActive = true;
        ParticleNode?.Restart();
    }

    /// <summary>Stops the effect, letting it play out its end cap.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Stop")]
    private void InputStop(EntityInputData data) => StopEffect();

    /// <summary>Starts the effect if it is stopped, stops it if it is running.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Toggle")]
    private void InputToggle(EntityInputData data)
    {
        if (IsActive)
        {
            StopEffect();
        }
        else
        {
            InputStart(data);
        }
    }

    private void StopEffect()
    {
        IsActive = false;
        ParticleNode?.PlayEndCap();
    }

    private ParticleSnapshot? LoadSnapshot()
    {
        var snapshotFile = Data?.GetStringProperty("snapshot_file");

        if (string.IsNullOrEmpty(snapshotFile))
        {
            return null;
        }

        return EntitySystem.FileLoader.LoadFileCompiled(snapshotFile)?.GetBlockByType(BlockType.SNAP) as ParticleSnapshot;
    }
}
