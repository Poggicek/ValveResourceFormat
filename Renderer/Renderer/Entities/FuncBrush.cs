using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>func_brush</c>. A piece of world geometry a map can show, hide, and make solid or not: the wall that
/// appears when a game starts, the barrier that drops when it ends.
/// </summary>
/// <remarks>
/// Being drawn and being solid are one switch for <c>Enable</c> and <c>Disable</c>, as they are in the
/// engine, and separate ones for <c>SetSolid</c> and <c>SetNonsolid</c>, which leave what is drawn alone.
/// The authored <c>solidity</c> decides what "solid" means for a given brush: some are never solid, some
/// always, and the rest follow whether they are drawn.
/// </remarks>
public sealed class FuncBrush : BaseModelEntity
{
    /// <summary>What a <c>func_brush</c>'s <c>solidity</c> keyvalue means.</summary>
    public enum SolidityMode
    {
        /// <summary>Solid whenever it is drawn.</summary>
        ToggleSolid = 0,

        /// <summary>Never solid, whatever else happens to it.</summary>
        NeverSolid = 1,

        /// <summary>Always solid, even while hidden.</summary>
        AlwaysSolid = 2,
    }

    /// <summary>Gets how this brush decides whether it is solid.</summary>
    public SolidityMode Solidity { get; private set; }

    /// <summary>Gets whether the brush is switched on: drawn, and solid if its solidity allows.</summary>
    public bool IsEnabled { get; private set; } = true;

    /// <summary>
    /// Initializes a <c>func_brush</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public FuncBrush(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        Solidity = (SolidityMode)KeyValues.GetInt32Property("solidity");

        SetEnabled(!KeyValues.GetBooleanProperty("startdisabled"));
    }

    /// <summary>Shows the brush, and makes it solid unless its solidity forbids.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Enable")]
    private void InputEnable(EntityInputData data) => SetEnabled(true);

    /// <summary>Hides the brush and takes it out of collision.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Disable")]
    private void InputDisable(EntityInputData data) => SetEnabled(false);

    /// <summary>Switches the brush between shown and hidden.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Toggle")]
    private void InputToggle(EntityInputData data) => SetEnabled(!IsEnabled);

    /// <summary>Makes the brush solid without changing whether it is drawn.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("SetSolid")]
    private void InputSetSolid(EntityInputData data) => IsSolid = true;

    /// <summary>Lets things pass through the brush without changing whether it is drawn.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("SetNonsolid")]
    private void InputSetNonsolid(EntityInputData data) => IsSolid = false;

    private void SetEnabled(bool enabled)
    {
        IsEnabled = enabled;
        IsDrawn = enabled;

        IsSolid = Solidity switch
        {
            SolidityMode.NeverSolid => false,
            SolidityMode.AlwaysSolid => true,
            _ => enabled,
        };
    }
}
