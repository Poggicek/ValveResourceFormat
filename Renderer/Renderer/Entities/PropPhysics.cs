namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>prop_physics</c> and its relatives. A model the engine would hand to the physics solver: a crate to
/// be knocked over, a bottle to be thrown. The most numerous entity in a Half-Life Alyx level.
/// </summary>
/// <remarks>
/// <para>
/// Nothing simulates it. There is no rigid-body solver here, so a physics prop stands exactly where the
/// map left it: it is drawn, it is solid, it answers the inputs a map fires at it, and it never falls,
/// tips or is pushed. <c>EnableMotion</c>, <c>DisableMotion</c>, <c>Wake</c> and <c>Sleep</c> are
/// therefore recorded and no more - a map that switches motion on is describing something the viewer
/// cannot do either way.
/// </para>
/// <para>
/// What it does answer is everything that is not motion, which is most of what maps actually fire at
/// props: showing and hiding, skins and body groups, colour, animation, and being killed. Those come from
/// <see cref="PropDynamic"/>, because a physics prop is a model entity first and a simulated body second.
/// </para>
/// </remarks>
public sealed class PropPhysics : PropDynamic
{
    /// <summary>Gets whether the map has left this prop free to move, had it anything to move it.</summary>
    public bool IsMotionEnabled { get; private set; } = true;

    /// <summary>
    /// Initializes a <c>prop_physics</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public PropPhysics(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <summary>Lets the prop move, if anything could move it.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("EnableMotion")]
    private void InputEnableMotion(EntityInputData data) => IsMotionEnabled = true;

    /// <summary>Pins the prop in place.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("DisableMotion")]
    private void InputDisableMotion(EntityInputData data) => IsMotionEnabled = false;

    /// <summary>Wakes the prop's body. Nothing sleeps here, so this only records the intent.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Wake")]
    private void InputWake(EntityInputData data) => IsMotionEnabled = true;

    /// <summary>Puts the prop's body to sleep.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Sleep")]
    private void InputSleep(EntityInputData data) => IsMotionEnabled = false;

    /// <summary>
    /// Breaks the prop, which here means it stops being drawn and stops being collided with, and reports
    /// the break so the map's wiring carries on. What it would break into is not spawned.
    /// </summary>
    /// <param name="data">The input's parameter and sender; the activator is passed along.</param>
    [EntityInput("Break")]
    private void InputBreak(EntityInputData data)
    {
        IsDrawn = false;
        IsSolid = false;

        EntitySystem.TriggerOutput(this, "OnBreak", data.Activator);
    }
}
