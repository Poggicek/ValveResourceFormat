using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using Entity = ValveResourceFormat.ResourceTypes.EntityLump.Entity;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// The entity context for a <see cref="Scene"/>: the world every simulated entity lives in. It owns the
/// list of living entities, runs them on a fixed tick, and carries the entity I/O queue between them.
/// </summary>
/// <remarks>
/// Entities tick at <see cref="TickInterval"/> rather than once per rendered frame. A frame runs as many ticks as it has time for,
/// up to <see cref="MaxTicksPerFrame"/>, so a hitch cannot make the world take longer to simulate than to draw.
/// </remarks>
public sealed class EntitySystem
{
    /// <summary>The fixed simulation tick, matching the engine's default 64 tick.</summary>
    public const float TickInterval = 1f / 64f;

    /// <summary>The most ticks one frame may run before the leftover time is dropped.</summary>
    public const int MaxTicksPerFrame = 8;

    /// <summary>Gets the scene the entities render into.</summary>
    public Scene Scene { get; }

    /// <summary>Gets the loader entities use to pull their models and physics.</summary>
    public IFileLoader FileLoader => Scene.RendererContext.FileLoader;

    /// <summary>Gets the logger for entity problems.</summary>
    public ILogger Logger => Scene.RendererContext.Logger;

    /// <summary>Gets every living entity, in spawn order.</summary>
    public IReadOnlyList<BaseEntity> Entities => entities;

    /// <summary>
    /// Gets or sets whether the world is simulated. Switching it off holds every entity where it stands:
    /// the ticks stop, so nothing thinks, moves, touches, or fires entity I/O. What is already spawned
    /// stays in the scene and stays drawn.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Gets the player, once one has been spawned into this world.</summary>
    public PlayerEntity? Player { get; private set; }

    /// <summary>Gets the current simulation time in seconds, the engine's <c>curtime</c>.</summary>
    public float CurrentTime { get; private set; }

    /// <summary>Gets the number of ticks simulated so far.</summary>
    public int TickCount { get; private set; }

    /// <summary>
    /// Gets how far the current frame sits between the last two simulated ticks, in [0, 1). Entities
    /// interpolate their render transform across that span so movement is smooth at any framerate rather
    /// than stepping at the tick rate.
    /// </summary>
    public float InterpolationFraction => tickAccumulator / TickInterval;

    /// <summary>
    /// Gets what the last <see cref="Update"/> call cost, covering the ticks it ran and the entity updates
    /// after them.
    /// </summary>
    /// <remarks>
    /// Most frames run no tick at all, the world being simulated at <see cref="TickInterval"/> and drawn as
    /// fast as the machine can, so this reads near zero on all of them and jumps on the ones that do tick.
    /// A reader after the cost of simulating should take the largest value it sees over a span rather than
    /// whichever frame it happened to sample.
    /// </remarks>
    public TimeSpan LastUpdateTime { get; private set; }

    private readonly List<BaseEntity> entities = [];
    private readonly List<QueuedInput> inputQueue = [];
    private readonly List<QueuedInput> dueInputs = [];
    private float tickAccumulator;
    private bool hasRemovedEntities;

    /// <summary>
    /// Initializes an entity system for a scene. Prefer <see cref="Scene.EntitySystem"/> over constructing
    /// one directly; a scene has exactly one world.
    /// </summary>
    /// <param name="scene">The scene the entities render into.</param>
    public EntitySystem(Scene scene)
    {
        Scene = scene;
    }

    /// <summary>
    /// Creates the entity for a map entity's keyvalues and puts it in the world. Returns
    /// <see langword="null"/> when the classname is not one the entity system implements, in which case
    /// the caller keeps ownership of it.
    /// </summary>
    /// <param name="data">The entity's keyvalues, as authored in the map.</param>
    /// <param name="parentTransform">Transform of the spawner, or identity for plain map entities.</param>
    /// <param name="layerName">Visibility layer for the entity and every node it creates.</param>
    /// <returns>The spawned entity, or <see langword="null"/> if the classname is not implemented.</returns>
    public BaseEntity? CreateEntity(Entity data, Matrix4x4 parentTransform, string? layerName)
    {
        var entity = EntityFactory.Create(this, new EntitySpawnInfo(data, parentTransform, layerName));

        if (entity == null)
        {
            return null;
        }

        Add(entity);

        return entity;
    }

    /// <summary>
    /// Puts the player into the world, so triggers have something to touch. Replaces any player already
    /// spawned.
    /// </summary>
    /// <param name="controller">The player state the entity mirrors.</param>
    /// <returns>The player entity.</returns>
    public PlayerEntity SpawnPlayer(IPlayerController controller)
    {
        if (Player != null)
        {
            Remove(Player);
        }

        // Registration is what usually binds a class's inputs; the player never goes through the factory
        EntityInputTable.Bind<PlayerEntity>();

        Player = new PlayerEntity(this, controller);
        Player.Spawn();
        Add(Player);

        return Player;
    }

    private void Add(BaseEntity entity)
    {
        entities.Add(entity);
    }

    /// <summary>
    /// Runs <see cref="BaseEntity.Activate"/> on every entity, once the whole map has spawned.
    /// </summary>
    public void Activate()
    {
        foreach (var entity in entities)
        {
            entity.Activate();
        }
    }

    /// <summary>
    /// Removes an entity from the world and takes its nodes out of the scene.
    /// </summary>
    /// <param name="entity">The entity to remove.</param>
    public void Remove(BaseEntity entity)
    {
        if (entity.IsRemoved)
        {
            return;
        }

        // Anything it was standing in should hear that it left before it stops existing
        foreach (var other in entities)
        {
            other.UpdateTouchLink(entity, isOverlapping: false);
        }

        entity.RemoveFromScene();
        hasRemovedEntities = true;

        if (Player == entity)
        {
            Player = null;
        }
    }

    /// <summary>
    /// Tests every trigger volume against the player, opening and closing touch links as they change. Both
    /// sides of a touch hear about it, the way the engine marks a pair of entities as touching.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the player is tested. Every pair of entities would be the general answer, but that is one test
    /// per trigger per entity per tick for a map full of entities that never move, and the player is the
    /// only thing here that walks into a volume. An entity moved by the simulation into a trigger it was not
    /// already in therefore does not fire the trigger; the touch machinery itself stays general, so this is
    /// the one place to widen if something other than the player ever needs to touch.
    /// </para>
    /// <para>
    /// Driven by the tick and nothing else, because a touch handler is entity logic: it teleports things,
    /// queues inputs against <see cref="CurrentTime"/>, spawns and removes entities. Sampling it per
    /// rendered frame instead would make all of that depend on framerate. The player still moves per frame,
    /// so a touch resolves up to one tick after the frame that caused it, and reads the player's live
    /// position when it does.
    /// </para>
    /// </remarks>
    private void UpdateTouchLinks()
    {
        // Read once: the player is the same for every trigger, and its bounds come off the live controller
        if (Player is not { IsRemoved: false } player
            || !player.TryGetTouchBounds(out var center, out var halfExtents))
        {
            return;
        }

        foreach (var entity in entities)
        {
            if (!entity.IsTrigger || entity.IsRemoved || entity.Collider is not { IsEmpty: false } volume)
            {
                continue;
            }

            // Overlaps rejects on world bounds first, so a trigger nowhere near costs one box test
            var isOverlapping = volume.Overlaps(center, halfExtents);

            entity.UpdateTouchLink(player, isOverlapping);
            player.UpdateTouchLink(entity, isOverlapping);
        }
    }

    /// <summary>
    /// Drops every entity and resets the clock. The scene nodes themselves are the scene's to clean up;
    /// this is what <see cref="Scene.Clear"/> calls once it has done that.
    /// </summary>
    public void Clear()
    {
        entities.Clear();
        Player = null;
        inputQueue.Clear();
        dueInputs.Clear();
        hasRemovedEntities = false;
        tickAccumulator = 0f;
        CurrentTime = 0f;
        TickCount = 0;
    }

    /// <summary>
    /// Advances the world by a rendered frame's worth of time, running whole ticks.
    /// </summary>
    /// <param name="frameTime">Elapsed time in seconds since the last frame.</param>
    public void Update(float frameTime)
    {
        if (entities.Count == 0)
        {
            LastUpdateTime = TimeSpan.Zero;
            return;
        }

        var updateStart = Stopwatch.GetTimestamp();

        if (Enabled)
        {
            tickAccumulator += frameTime;

            for (var i = 0; tickAccumulator >= TickInterval; i++)
            {
                if (i == MaxTicksPerFrame)
                {
                    // Fell too far behind to catch up; drop the backlog rather than spiral
                    tickAccumulator = 0f;
                    break;
                }

                tickAccumulator -= TickInterval;
                Tick();
            }
        }
        else
        {
            // Time spent paused is not owed back: switching it on again resumes rather than catching up,
            // and the entities rest on their last tick state rather than part way to the next one
            tickAccumulator = 0f;
        }

        // Entities are not scene nodes, so nothing else would place what they own
        foreach (var entity in entities)
        {
            entity.Update();
        }

        LastUpdateTime = Stopwatch.GetElapsedTime(updateStart);
    }

    private void Tick()
    {
        TickCount++;
        CurrentTime = TickCount * TickInterval;

        DispatchDueInputs();

        // Indexed, because an input or a think can spawn or remove entities mid-tick
        for (var i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];

            if (!entity.IsRemoved)
            {
                entity.Simulate(TickInterval);
            }
        }

        if (hasRemovedEntities)
        {
            entities.RemoveAll(static entity => entity.IsRemoved);
            hasRemovedEntities = false;
        }

        UpdateTouchLinks();
    }

    /// <summary>
    /// Sweeps an axis-aligned box against every solid entity, keeping the nearest hit.
    /// </summary>
    /// <param name="from">Sweep start, the box centre in world space.</param>
    /// <param name="to">Sweep end in world space.</param>
    /// <param name="halfExtents">Half-extents of the swept box.</param>
    /// <param name="detectStartSolid">Whether an overlap at <paramref name="from"/> reports as start-solid.</param>
    /// <param name="result">The trace to narrow; a nearer entity hit replaces it.</param>
    /// <param name="hitEntity">The entity that produced the nearest hit, if one did.</param>
    /// <returns><see langword="true"/> when an entity produced the nearest hit.</returns>
    public bool TraceAABB(Vector3 from, Vector3 to, Vector3 halfExtents, bool detectStartSolid,
        ref Rubikon.TraceResult result, out BaseEntity? hitEntity)
    {
        hitEntity = null;

        foreach (var entity in entities)
        {
            if (!entity.IsCollidable || !entity.Collider!.MightHit(from, to, halfExtents))
            {
                continue;
            }

            if (result.MinimizeWith(entity.Collider.TraceAABB(from, to, halfExtents, detectStartSolid)))
            {
                hitEntity = entity;
            }
        }

        return hitEntity != null;
    }

    /// <summary>
    /// Finds the usable entity a reach trace hits first, with the world blocking the way.
    /// </summary>
    /// <param name="from">Trace start, normally an eye position.</param>
    /// <param name="to">Trace end, the far edge of the reach.</param>
    /// <returns>The nearest usable entity in reach, or <see langword="null"/> when there is none.</returns>
    public BaseEntity? FindUseTarget(Vector3 from, Vector3 to)
    {
        // Seeded with the world, so a wall between the player and a button wins the trace
        var nearest = Scene.PhysicsWorld?.TraceRay(from, to) ?? new Rubikon.TraceResult();
        BaseEntity? target = null;

        foreach (var entity in entities)
        {
            if (entity.IsRemoved
                || (entity.ObjectCaps & EntityCapability.UsableMask) == 0
                || entity.Collider is not { IsEmpty: false } collider)
            {
                continue;
            }

            if (nearest.MinimizeWith(collider.TraceRay(from, to)))
            {
                target = entity;
            }
        }

        return target;
    }

    /// <summary>
    /// Fires an entity I/O input at one entity, after its delay has elapsed.
    /// </summary>
    /// <param name="target">The entity receiving the input.</param>
    /// <param name="inputName">The input's name.</param>
    /// <param name="parameter">The parameter passed with the input, if any.</param>
    /// <param name="activator">The entity that started the I/O chain.</param>
    /// <param name="caller">The entity that fired the output.</param>
    /// <param name="delay">Seconds to wait before the input is delivered.</param>
    public void QueueInput(BaseEntity target, string inputName, string? parameter = null,
        BaseEntity? activator = null, BaseEntity? caller = null, float delay = 0f)
    {
        inputQueue.Add(new QueuedInput(target, inputName, parameter, activator, caller, CurrentTime + MathF.Max(delay, 0f)));
    }

    /// <summary>
    /// Drops the inputs an entity queued that have not fired yet, Source's <c>CancelPending</c>. Only the
    /// ones it queued itself: an input another entity aimed at it is that entity's to cancel.
    /// </summary>
    /// <param name="caller">The entity whose queued inputs should be dropped.</param>
    public void CancelQueuedInputsFrom(BaseEntity caller)
        => inputQueue.RemoveAll(input => input.Caller == caller);

    /// <summary>
    /// Fires an entity I/O input at every entity whose targetname matches, wildcards and the <c>!</c>
    /// procedural names included.
    /// </summary>
    /// <param name="targetName">Targetname to match, may contain <c>*</c> and <c>?</c>, or be a <c>!</c> name.</param>
    /// <param name="inputName">The input's name.</param>
    /// <param name="parameter">The parameter passed with the input, if any.</param>
    /// <param name="activator">The entity that started the I/O chain.</param>
    /// <param name="caller">The entity that fired the output.</param>
    /// <param name="delay">Seconds to wait before the input is delivered.</param>
    public void QueueInputByTargetName(string targetName, string inputName, string? parameter = null,
        BaseEntity? activator = null, BaseEntity? caller = null, float delay = 0f)
    {
        foreach (var target in FindTargets(targetName, activator, caller))
        {
            QueueInput(target, inputName, parameter, activator, caller, delay);
        }
    }

    /// <summary>
    /// Resolves a target name the way entity I/O does: the <c>!</c> procedural names first, then every
    /// entity whose targetname matches, wildcards included.
    /// </summary>
    /// <param name="targetName">The name to resolve.</param>
    /// <param name="activator">The entity that set the chain off, for <c>!activator</c>.</param>
    /// <param name="caller">The entity doing the lookup, for <c>!self</c>.</param>
    /// <returns>The entities the name stands for, which may be none.</returns>
    public IEnumerable<BaseEntity> FindTargets(string targetName, BaseEntity? activator = null, BaseEntity? caller = null)
    {
        if (IsProceduralName(targetName))
        {
            // A name the map means literally, so it never falls through to a search
            if (ResolveProceduralName(targetName, activator, caller) is { } resolved)
            {
                yield return resolved;
            }

            yield break;
        }

        foreach (var target in FindAllByTargetName(targetName))
        {
            yield return target;
        }
    }

    /// <summary>Whether a targetname is one of the <c>!</c> names that stand for an entity in the chain.</summary>
    private static bool IsProceduralName(string targetName) => targetName.StartsWith('!');

    /// <summary>
    /// Resolves one of Source's <c>!</c> target names, the ones that name an entity by its part in the I/O
    /// chain rather than by what it is called. Source's <c>FindEntityProcedural</c>.
    /// </summary>
    /// <remarks>
    /// <c>!self</c> is the entity doing the lookup, which for an output is the one that fired it, so it
    /// resolves to <paramref name="caller"/>.
    /// </remarks>
    /// <param name="targetName">The <c>!</c> name.</param>
    /// <param name="activator">The entity that started the I/O chain.</param>
    /// <param name="caller">The entity that fired the output.</param>
    /// <returns>The entity named, or <see langword="null"/> when there is none or the name is unknown.</returns>
    private BaseEntity? ResolveProceduralName(string targetName, BaseEntity? activator, BaseEntity? caller)
    {
        if (targetName.Equals("!self", StringComparison.OrdinalIgnoreCase)
            || targetName.Equals("!caller", StringComparison.OrdinalIgnoreCase))
        {
            return caller;
        }

        if (targetName.Equals("!activator", StringComparison.OrdinalIgnoreCase))
        {
            return activator;
        }

        if (targetName.Equals("!player", StringComparison.OrdinalIgnoreCase))
        {
            return Player;
        }

        Logger.LogDebug("Entity I/O target '{TargetName}' is not a name the entity system knows", targetName);

        return null;
    }

    /// <summary>
    /// Fires one of an entity's authored outputs, delivering it to every connection with that name.
    /// Source's <c>FireOutput</c>.
    /// </summary>
    /// <param name="source">The entity firing the output.</param>
    /// <param name="outputName">The output's name, as authored in the map.</param>
    /// <param name="activator">The entity that started the I/O chain.</param>
    public void TriggerOutput(BaseEntity source, string outputName, BaseEntity? activator = null)
    {
        if (source.Data?.Connections == null)
        {
            return;
        }

        foreach (var connection in source.Data.Connections)
        {
            if (!connection.OutputName.Equals(outputName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parameter = string.IsNullOrEmpty(connection.OverrideParam) || connection.OverrideParam == "(null)"
                ? null
                : connection.OverrideParam;

            QueueInputByTargetName(connection.TargetName, connection.InputName, parameter, activator, source, connection.Delay);
        }
    }

    /// <summary>
    /// Finds every entity whose targetname matches.
    /// </summary>
    /// <param name="pattern">Targetname to match, may contain <c>*</c> and <c>?</c>.</param>
    public IEnumerable<BaseEntity> FindAllByTargetName(string pattern)
    {
        foreach (var entity in entities)
        {
            if (Matches(entity, pattern))
            {
                yield return entity;
            }
        }
    }

    private static bool Matches(BaseEntity entity, string pattern)
        => !entity.IsRemoved
        && entity.TargetName != null
        && EntityLump.EntityNameMatches(pattern, entity.TargetName);

    private void DispatchDueInputs()
    {
        if (inputQueue.Count == 0)
        {
            return;
        }

        // Copied out first, in fire order, because handling an input can queue more of them
        var remaining = 0;

        for (var i = 0; i < inputQueue.Count; i++)
        {
            var input = inputQueue[i];

            if (input.FireTime <= CurrentTime)
            {
                dueInputs.Add(input);
            }
            else
            {
                inputQueue[remaining++] = input;
            }
        }

        inputQueue.RemoveRange(remaining, inputQueue.Count - remaining);

        foreach (var input in dueInputs)
        {
            if (!input.Target.IsRemoved)
            {
                input.Target.AcceptInput(input.InputName, new EntityInputData
                {
                    Parameter = input.Parameter,
                    Activator = input.Activator,
                    Caller = input.Caller,
                });
            }
        }

        dueInputs.Clear();
    }

    private readonly record struct QueuedInput(
        BaseEntity Target,
        string InputName,
        string? Parameter,
        BaseEntity? Activator,
        BaseEntity? Caller,
        float FireTime);
}
