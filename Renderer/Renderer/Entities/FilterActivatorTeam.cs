namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>filter_activator_team</c>. A test other entities ask about whoever set them off: in the engine,
/// whether that activator is on a given team.
/// </summary>
/// <remarks>
/// <para>
/// Everything passes. A viewer has one player, on no team, so a team test has no answer to give; failing
/// would make every door and trigger behind such a filter permanently shut, which is worse than letting a
/// map's logic run. The same reasoning a trigger with no recognised spawnflags uses to accept everything.
/// </para>
/// <para>
/// <c>OnFail</c> therefore never fires, and <c>Negated</c> is ignored: negating "always passes" would be
/// "always fails", which is the outcome this exists to avoid.
/// </para>
/// <para>
/// Only the <c>TestActivator</c> input is answered. A trigger's <c>filtername</c> is still not consulted
/// at all, so a trigger behind this filter passes for the same reason it passed before: it does not ask.
/// </para>
/// </remarks>
public sealed class FilterActivatorTeam : BaseEntity
{
    /// <summary>
    /// Initializes a <c>filter_activator_team</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public FilterActivatorTeam(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <summary>
    /// Tests whoever fired this and reports the result, Source's <c>TestActivator</c>. Always
    /// <c>OnPass</c>.
    /// </summary>
    /// <param name="data">Its activator is the entity being tested, and is passed along to the output.</param>
    [EntityInput("TestActivator")]
    private void InputTestActivator(EntityInputData data)
        => EntitySystem.TriggerOutput(this, "OnPass", data.Activator);
}
