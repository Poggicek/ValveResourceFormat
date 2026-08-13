using System.Diagnostics.CodeAnalysis;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// Constructs an entity of one classname.
/// </summary>
/// <param name="system">The world the entity is being created in.</param>
/// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
/// <returns>The constructed entity, before <see cref="BaseEntity.Spawn"/> has run.</returns>
public delegate BaseEntity EntityCreator(EntitySystem system, EntitySpawnInfo spawnInfo);

/// <summary>
/// Turns a classname into a live entity. Source's entity factory dictionary: every simulated classname
/// registers here, and anything absent from the table is not one the entity system implements.
/// </summary>
/// <remarks>
/// The table is populated statically rather than by scanning types, so the renderer stays trim-safe and
/// AOT-compatible. Add a classname here to make <see cref="World.WorldLoader"/> hand it over to
/// <see cref="EntitySystem"/> instead of loading it as a static scene node.
/// </remarks>
public static class EntityFactory
{
    private static readonly Dictionary<string, EntityCreator> Creators = new(StringComparer.OrdinalIgnoreCase);

    static EntityFactory()
    {
        Register<AmbientGeneric>("ambient_generic", static (system, spawnInfo) => new AmbientGeneric(system, spawnInfo));
        Register<BaseActor>("generic_actor", static (system, spawnInfo) => new BaseActor(system, spawnInfo));
        Register<BaseActor>("npc_furniture", static (system, spawnInfo) => new BaseActor(system, spawnInfo));
        Register<BaseActor>("npc_vr_citizen_female", static (system, spawnInfo) => new BaseActor(system, spawnInfo));
        Register<BaseActor>("npc_vr_citizen_male", static (system, spawnInfo) => new BaseActor(system, spawnInfo));
        Register<EnvFade>("env_fade", static (system, spawnInfo) => new EnvFade(system, spawnInfo));
        Register<FilterActivatorClass>("filter_activator_class", static (system, spawnInfo) => new FilterActivatorClass(system, spawnInfo));
        Register<FilterActivatorModel>("filter_activator_model", static (system, spawnInfo) => new FilterActivatorModel(system, spawnInfo));
        Register<FilterActivatorName>("filter_activator_name", static (system, spawnInfo) => new FilterActivatorName(system, spawnInfo));
        Register<FilterMulti>("filter_multi", static (system, spawnInfo) => new FilterMulti(system, spawnInfo));
        Register<FuncBrush>("func_brush", static (system, spawnInfo) => new FuncBrush(system, spawnInfo));
        Register<FuncButton>("func_button", static (system, spawnInfo) => new FuncButton(system, spawnInfo));
        Register<FuncDoor>("func_door", static (system, spawnInfo) => new FuncDoor(system, spawnInfo));
        Register<FuncDoorRotating>("func_door_rotating", static (system, spawnInfo) => new FuncDoorRotating(system, spawnInfo));
        Register<FuncMoveLinear>("func_movelinear", static (system, spawnInfo) => new FuncMoveLinear(system, spawnInfo));
        Register<FuncPhysicalButton>("func_physical_button", static (system, spawnInfo) => new FuncPhysicalButton(system, spawnInfo));
        Register<FuncRotating>("func_rotating", static (system, spawnInfo) => new FuncRotating(system, spawnInfo));
        Register<InfoParticleSystem>("info_particle_system", static (system, spawnInfo) => new InfoParticleSystem(system, spawnInfo));
        Register<ItemGrenadeFrag>("item_hlvr_grenade_frag", static (system, spawnInfo) => new ItemGrenadeFrag(system, spawnInfo));
        Register<LightEntity>("light_omni", static (system, spawnInfo) => new LightEntity(system, spawnInfo));
        Register<LightEntity>("light_spot", static (system, spawnInfo) => new LightEntity(system, spawnInfo));
        Register<LogicAuto>("logic_auto", static (system, spawnInfo) => new LogicAuto(system, spawnInfo));
        Register<LogicAchievement>("logic_achievement", static (system, spawnInfo) => new LogicAchievement(system, spawnInfo));
        Register<LogicAutosave>("logic_autosave", static (system, spawnInfo) => new LogicAutosave(system, spawnInfo));
        Register<LogicBranch>("logic_branch", static (system, spawnInfo) => new LogicBranch(system, spawnInfo));
        Register<LogicCase>("logic_case", static (system, spawnInfo) => new LogicCase(system, spawnInfo));
        Register<LogicChoreographedScene>("logic_choreographed_scene", static (system, spawnInfo) => new LogicChoreographedScene(system, spawnInfo));
        Register<LogicCompare>("logic_compare", static (system, spawnInfo) => new LogicCompare(system, spawnInfo));
        Register<LogicRelay>("logic_relay", static (system, spawnInfo) => new LogicRelay(system, spawnInfo));
        Register<LogicTimer>("logic_timer", static (system, spawnInfo) => new LogicTimer(system, spawnInfo));
        Register<MathCounter>("math_counter", static (system, spawnInfo) => new MathCounter(system, spawnInfo));
        Register<PointTemplate>("point_template", static (system, spawnInfo) => new PointTemplate(system, spawnInfo));
        Register<PointTeleport>("point_teleport", static (system, spawnInfo) => new PointTeleport(system, spawnInfo));
        Register<PathCorner>("path_corner", static (system, spawnInfo) => new PathCorner(system, spawnInfo));
        Register<PointClientCommand>("point_clientcommand", static (system, spawnInfo) => new PointClientCommand(system, spawnInfo));
        Register<PointInstructorEvent>("point_instructor_event", static (system, spawnInfo) => new PointInstructorEvent(system, spawnInfo));
        Register<PointSoundEvent>("point_soundevent", static (system, spawnInfo) => new PointSoundEvent(system, spawnInfo));
        Register<PropPhysics>("prop_physics", static (system, spawnInfo) => new PropPhysics(system, spawnInfo));
        Register<PropPhysics>("prop_physics_override", static (system, spawnInfo) => new PropPhysics(system, spawnInfo));
        Register<PropPhysics>("prop_ragdoll", static (system, spawnInfo) => new PropPhysics(system, spawnInfo));
        Register<PropDynamic>("prop_dynamic", static (system, spawnInfo) => new PropDynamic(system, spawnInfo));
        Register<PropDynamic>("prop_dynamic_override", static (system, spawnInfo) => new PropDynamic(system, spawnInfo));
        Register<ScriptedSequence>("scripted_sequence", static (system, spawnInfo) => new ScriptedSequence(system, spawnInfo));
        Register<SoundEventParameter>("snd_event_param", static (system, spawnInfo) => new SoundEventParameter(system, spawnInfo));
        Register<SoundOpvarSet>("snd_opvar_set_aabb", static (system, spawnInfo) => new SoundOpvarSet(system, spawnInfo));
        Register<SoundOpvarSet>("snd_opvar_set_obb", static (system, spawnInfo) => new SoundOpvarSet(system, spawnInfo));
        Register<SoundOpvarSet>("snd_opvar_set_point", static (system, spawnInfo) => new SoundOpvarSet(system, spawnInfo));
        Register<PointSoundEvent>("snd_event_alignedbox", static (system, spawnInfo) => new PointSoundEvent(system, spawnInfo));
        Register<PointSoundEvent>("snd_event_point", static (system, spawnInfo) => new PointSoundEvent(system, spawnInfo));

        // The volumes whose own effect is not simulated, but whose touches and switches the map wires up
        Register<TriggerChangeLevel>("trigger_changelevel", static (system, spawnInfo) => new TriggerChangeLevel(system, spawnInfo));
        Register<TriggerMultiple>("trigger_hurt", static (system, spawnInfo) => new TriggerMultiple(system, spawnInfo));
        Register<TriggerLook>("trigger_look", static (system, spawnInfo) => new TriggerLook(system, spawnInfo));
        Register<TriggerMultiple>("trigger_multiple", static (system, spawnInfo) => new TriggerMultiple(system, spawnInfo));
        Register<TriggerMultiple>("trigger_push", static (system, spawnInfo) => new TriggerMultiple(system, spawnInfo));
        Register<TriggerOnce>("trigger_once", static (system, spawnInfo) => new TriggerOnce(system, spawnInfo));
        Register<TriggerTeleport>("trigger_teleport", static (system, spawnInfo) => new TriggerTeleport(system, spawnInfo));
    }

    /// <summary>
    /// Whether this classname is simulated by the entity system.
    /// </summary>
    /// <param name="classname">The classname to look up.</param>
    public static bool IsRegistered(string classname) => Creators.ContainsKey(classname);

    /// <summary>
    /// Registers a classname the entity system should simulate, and builds the entity class's table of
    /// <see cref="EntityInputAttribute"/> handlers. Not thread safe: call it during startup, before any
    /// map is loaded.
    /// </summary>
    /// <typeparam name="T">The entity class the classname spawns.</typeparam>
    /// <param name="classname">The classname to link, matched case-insensitively.</param>
    /// <param name="creator">Constructs the entity.</param>
    public static void Register<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] T>(
        string classname, EntityCreator creator)
        where T : BaseEntity
    {
        Creators[classname] = creator;
        EntityInputTable.Bind<T>();
    }

    /// <summary>
    /// Creates and spawns the entity for a classname. The entity is fully set up when this returns, but
    /// is not in the world yet; <see cref="EntitySystem.CreateEntity"/> is what puts it there.
    /// </summary>
    /// <param name="system">The world the entity is being created in.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    /// <returns>The spawned entity, or <see langword="null"/> when the classname is not implemented.</returns>
    public static BaseEntity? Create(EntitySystem system, EntitySpawnInfo spawnInfo)
    {
        var classname = spawnInfo.Data.GetStringProperty("classname");

        if (classname == null || !Creators.TryGetValue(classname, out var creator))
        {
            return null;
        }

        var entity = creator(system, spawnInfo);
        entity.Spawn();

        return entity;
    }
}
