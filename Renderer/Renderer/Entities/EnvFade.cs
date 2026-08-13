using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>env_fade</c>. Fades the screen to a colour and back, which a map uses to cover a cut, a teleport or
/// a death.
/// </summary>
/// <remarks>
/// <para>
/// The fade is laid over the finished image by the post-processing pass, after tonemapping and dithering,
/// because it covers the picture rather than lighting the scene. The entity publishes what it wants drawn
/// to <see cref="EntitySystem.ScreenFade"/>, so the last one triggered is the one on screen.
/// </para>
/// <para>
/// Not simulated: "Modulate", which blends the fade colour rather than covering with it, and "Triggering
/// player only", there being one viewer to fade.
/// </para>
/// </remarks>
public sealed class EnvFade : BaseEntity
{
    /// <summary>What an <c>env_fade</c>'s <c>spawnflags</c> mean.</summary>
    [Flags]
    public enum SpawnFlag : uint
    {
        /// <summary>Starts fully faded and clears, rather than fading in from nothing.</summary>
        FadeFrom = 1,

        /// <summary>Blends the colour instead of covering with it. Not simulated.</summary>
        Modulate = 2,

        /// <summary>Holds the faded state instead of returning.</summary>
        StayOut = 8,
    }

    /// <summary>Gets how long the fade takes, in seconds.</summary>
    public float Duration { get; private set; } = 2f;

    /// <summary>Gets how long the faded state is held once reached, in seconds.</summary>
    public float HoldTime { get; private set; }

    /// <summary>Gets the colour faded towards, with the alpha it reaches at its fullest.</summary>
    public Vector4 FadeColor { get; private set; } = new(0f, 0f, 0f, 1f);

    /// <summary>Gets whether a fade is running.</summary>
    public bool IsFading { get; private set; }

    private float startTime = -1f;

    /// <summary>
    /// Initializes an <c>env_fade</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public EnvFade(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        Duration = MathF.Max(KeyValues.GetFloatProperty("duration", 2f), 0f);
        HoldTime = MathF.Max(KeyValues.GetFloatProperty("holdtime"), 0f);

        var color = KeyValues.GetColor32Property("rendercolor");
        var alpha = Math.Clamp(KeyValues.GetFloatProperty("renderamt", 255f) / 255f, 0f, 1f);

        FadeColor = new Vector4(color, alpha);
    }

    /// <summary>Starts the fade.</summary>
    /// <param name="data">The input's parameter and sender; the activator is passed along.</param>
    [EntityInput("Fade")]
    private void InputFade(EntityInputData data)
    {
        IsFading = true;
        startTime = EntitySystem.CurrentTime;

        EntitySystem.TriggerOutput(this, "OnBeginFade", data.Activator);

        // Woken every tick while it runs, since the fade is a curve rather than a pair of edges
        SetNextThink(EntitySystem.CurrentTime + EntitySystem.TickInterval);
    }

    /// <summary>Advances the fade, and publishes what it should look like now.</summary>
    public override void Think()
    {
        if (!IsFading)
        {
            return;
        }

        var elapsed = EntitySystem.CurrentTime - startTime;
        var fadesFrom = HasSpawnFlags(SpawnFlag.FadeFrom);

        // Up to full over the duration, held, then back down unless the map wants it left covered
        var coverage = Duration <= 0f ? 1f : Math.Clamp(elapsed / Duration, 0f, 1f);

        if (elapsed >= Duration + HoldTime)
        {
            if (fadesFrom || !HasSpawnFlags(SpawnFlag.StayOut))
            {
                Finish();
                return;
            }

            coverage = 1f;
        }

        // "Fade From" runs the same curve backwards: full at the start, clear by the end
        var amount = fadesFrom ? 1f - coverage : coverage;

        EntitySystem.SetScreenFade(this, FadeColor with { W = FadeColor.W * amount });

        SetNextThink(EntitySystem.CurrentTime + EntitySystem.TickInterval);
    }

    /// <inheritdoc/>
    protected override void OnRemove()
    {
        Finish();

        base.OnRemove();
    }

    /// <summary>Ends the fade and hands the screen back, unless another fade has since taken it.</summary>
    private void Finish()
    {
        IsFading = false;

        SetNextThink(-1f);

        if (EntitySystem.ScreenFadeOwner == this)
        {
            EntitySystem.SetScreenFade(this, Vector4.Zero);
        }
    }
}
