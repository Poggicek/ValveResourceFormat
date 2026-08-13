namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>func_physical_button</c>. A button pressed by pushing it, rather than by looking at it and pressing
/// use. Half-Life Alyx puts these everywhere a hand would reach.
/// </summary>
/// <remarks>
/// There are no hands here, so it is pressed the only way a viewer can press anything: by looking at it
/// and using it. Everything after that is <see cref="FuncButton"/>'s - it travels along its
/// <c>movedir</c>, reports <c>OnIn</c> at the bottom and <c>OnOut</c> on the way back, and a map wired to
/// those cannot tell the difference. What it does not have is the push itself: no partial travel under a
/// hand, and no pressing it by throwing something at it.
/// </remarks>
public sealed class FuncPhysicalButton : FuncButton
{
    /// <summary>
    /// A physical button is pressed by touch in the engine and names no activation flags, so it would
    /// otherwise read as a button with no way of being pressed at all.
    /// </summary>
    public override EntityCapability ObjectCaps
        => IsDisabled ? EntityCapability.None : EntityCapability.ImpulseUse;

    /// <summary>
    /// Initializes a <c>func_physical_button</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public FuncPhysicalButton(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }
}
