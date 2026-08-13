namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>item_hlvr_grenade_frag</c>. A frag grenade sitting in the world, or riding on something. Nothing
/// here throws one, and going off damages nothing, but a map that arms a grenade by hand is scripting an
/// explosion rather than fighting with one, and what that explosion drives is entity I/O like anything else.
/// </summary>
/// <remarks>
/// Half-Life Alyx ends its van ride this way: a scanner carries a grenade in through the windshield, the
/// crash relay arms it, and the grenade's <c>OnExplode</c> is what crashes the van, fades the screen and
/// eventually teleports the player out of the wreck. With no class for the grenade the whole chain stops
/// at it, and the map is left riding a van that never crashes.
/// </remarks>
public sealed class ItemGrenadeFrag : BaseEntity
{
    /// <summary>Gets whether the fuse is burning.</summary>
    public bool IsArmed { get; private set; }

    /// <summary>
    /// Initializes an <c>item_hlvr_grenade_frag</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public ItemGrenadeFrag(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Think() => Explode();

    /// <summary>
    /// Starts the fuse, the grenade's one input.
    /// </summary>
    /// <remarks>
    /// The parameter is how long the fuse burns for in seconds, as the class hands maps ("ArmGrenade(float)").
    /// Maps do author it - a grenade meant to be dodged is armed with 3 or 8 - but the two scripted ones
    /// that go off the moment something sets them off, this map's van grenade and a booby trap elsewhere,
    /// pass nothing at all, which reads as a fuse of zero the way the engine parses an absent parameter.
    /// That is honoured as the soonest an explosion can happen rather than one inside this delivery, so
    /// the outputs it fires are not queued from within the queue that armed it.
    /// </remarks>
    /// <param name="data">Carries the fuse length in seconds.</param>
    [EntityInput("ArmGrenade")]
    private void InputArmGrenade(EntityInputData data)
    {
        var fuse = MathF.Max(data.Float(), EntitySystem.TickInterval);

        IsArmed = true;
        SetNextThink(EntitySystem.CurrentTime + fuse);
    }

    /// <summary>
    /// Goes off: reports the explosion, then takes the grenade out of the world. That is the whole of an
    /// explosion here - nothing is damaged, moved, lit or set alight by it.
    /// </summary>
    public void Explode()
    {
        if (!IsArmed)
        {
            return;
        }

        IsArmed = false;

        EntitySystem.TriggerOutput(this, "OnExplode", this);

        // The engine's grenade does not survive its own explosion, and a map that spawns one from a
        // template spawns another rather than reusing this one
        EntitySystem.Remove(this);
    }
}
