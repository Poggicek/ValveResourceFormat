using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// The base of the <c>filter_</c> entities, Source's <c>CBaseFilter</c>. A named test another entity asks
/// about whoever set it off: a trigger names one in its <c>filtername</c> and only reacts to what passes.
/// </summary>
/// <remarks>
/// The <c>Negated</c> keyvalue flips the answer, which is what turns "only this" into "anything but this",
/// so every filter writes its test as the positive case and lets this class invert it.
/// </remarks>
public abstract class BaseFilter : BaseEntity
{
    /// <summary>Gets whether the filter reports the opposite of what its test says.</summary>
    public bool IsNegated { get; private set; }

    /// <summary>
    /// Initializes a filter from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    protected BaseFilter(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        IsNegated = KeyValues.GetBooleanProperty("negated");
    }

    /// <summary>
    /// Whether an entity gets through this filter, negation included. Source's <c>PassesFilter</c>.
    /// </summary>
    /// <param name="activator">The entity being tested; may be <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when it passes.</returns>
    public bool PassesFilter(BaseEntity? activator)
        => Matches(activator) != IsNegated;

    /// <summary>
    /// The filter's own test, written as the positive case. <see cref="PassesFilter"/> applies negation.
    /// </summary>
    /// <param name="activator">The entity being tested; may be <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when it matches what this filter names.</returns>
    protected abstract bool Matches(BaseEntity? activator);

    /// <summary>Tests whoever fired this and reports the answer, Source's <c>TestActivator</c>.</summary>
    /// <param name="data">Its activator is the entity being tested, and is passed on to the output.</param>
    [EntityInput("TestActivator")]
    protected void InputTestActivator(EntityInputData data)
        => EntitySystem.TriggerOutput(this, PassesFilter(data.Activator) ? "OnPass" : "OnFail", data.Activator);
}

/// <summary>
/// <c>filter_activator_name</c>. Passes whoever is called what the filter names, wildcards included.
/// </summary>
public sealed class FilterActivatorName : BaseFilter
{
    private string? filterName;

    /// <summary>
    /// Initializes a <c>filter_activator_name</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public FilterActivatorName(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        base.Spawn();

        filterName = KeyValues.GetStringProperty("filtername");
    }

    /// <inheritdoc/>
    protected override bool Matches(BaseEntity? activator)
        => filterName != null
        && activator?.TargetName != null
        && ResourceTypes.EntityLump.EntityNameMatches(filterName, activator.TargetName);
}

/// <summary>
/// <c>filter_activator_class</c>. Passes whatever is of the classname the filter names.
/// </summary>
public sealed class FilterActivatorClass : BaseFilter
{
    private string? filterClass;

    /// <summary>
    /// Initializes a <c>filter_activator_class</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public FilterActivatorClass(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        base.Spawn();

        filterClass = KeyValues.GetStringProperty("filterclass");
    }

    /// <inheritdoc/>
    protected override bool Matches(BaseEntity? activator)
        => filterClass != null
        && activator != null
        && ResourceTypes.EntityLump.EntityNameMatches(filterClass, activator.Classname);
}

/// <summary>
/// <c>filter_activator_model</c>. Passes whatever was built from the model the filter names.
/// </summary>
public sealed class FilterActivatorModel : BaseFilter
{
    private string? filterModel;

    /// <summary>
    /// Initializes a <c>filter_activator_model</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public FilterActivatorModel(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        base.Spawn();

        filterModel = KeyValues.GetStringProperty("model");
    }

    /// <inheritdoc/>
    protected override bool Matches(BaseEntity? activator)
        => filterModel != null
        && activator?.ModelName != null
        && ResourceTypes.EntityLump.EntityNameMatches(filterModel, activator.ModelName);
}

/// <summary>
/// <c>filter_multi</c>. Combines up to ten other filters with AND or OR, so a map can express a test none
/// of them can alone.
/// </summary>
/// <remarks>
/// The sub-filters are named, so they resolve once everything has spawned. One that names a filter this
/// viewer does not implement contributes nothing rather than failing the whole test, which keeps a map
/// running instead of shutting every trigger behind it.
/// </remarks>
public sealed class FilterMulti : BaseFilter
{
    /// <summary>How many sub-filters the entity supports, matching the FGD's <c>Filter01</c> to <c>Filter10</c>.</summary>
    private const int FilterCount = 10;

    private readonly List<BaseFilter> filters = [];
    private bool requiresAll = true;

    /// <summary>
    /// Initializes a <c>filter_multi</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public FilterMulti(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        base.Spawn();

        // 0 is AND, 1 is OR
        requiresAll = KeyValues.GetInt32Property("filtertype") == 0;
    }

    /// <inheritdoc/>
    public override void Activate()
    {
        for (var i = 0; i < FilterCount; i++)
        {
            var name = KeyValues.GetStringProperty($"filter{i + 1:00}");

            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            foreach (var target in EntitySystem.FindTargets(name, caller: this))
            {
                if (target is BaseFilter filter && filter != this)
                {
                    filters.Add(filter);
                    break;
                }
            }
        }
    }

    /// <inheritdoc/>
    protected override bool Matches(BaseEntity? activator)
    {
        if (filters.Count == 0)
        {
            return true;
        }

        foreach (var filter in filters)
        {
            var passes = filter.PassesFilter(activator);

            if (requiresAll && !passes)
            {
                return false;
            }

            if (!requiresAll && passes)
            {
                return true;
            }
        }

        return requiresAll;
    }
}
