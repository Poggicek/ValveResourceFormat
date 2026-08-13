using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>logic_branch</c>. Holds a true/false value and reports which it is when asked. The if-statement of
/// entity I/O: a map stores a fact here and tests it later, from somewhere else entirely.
/// </summary>
public sealed class LogicBranch : BaseEntity
{
    /// <summary>Gets the value currently held.</summary>
    public bool Value { get; private set; }

    /// <summary>
    /// Initializes a <c>logic_branch</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public LogicBranch(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        Value = KeyValues.GetInt32Property("initialvalue") != 0;
    }

    /// <summary>Stores a value without testing it, for a test that comes later.</summary>
    /// <param name="data">Carries the new value.</param>
    [EntityInput("SetValue")]
    private void InputSetValue(EntityInputData data) => Value = data.Bool();

    /// <summary>Stores a value and reports it.</summary>
    /// <param name="data">Carries the new value.</param>
    [EntityInput("SetValueTest")]
    private void InputSetValueTest(EntityInputData data)
    {
        Value = data.Bool();

        Test(data.Activator);
    }

    /// <summary>Flips the value without testing it.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Toggle")]
    private void InputToggle(EntityInputData data) => Value = !Value;

    /// <summary>Flips the value and reports it.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("ToggleTest")]
    private void InputToggleTest(EntityInputData data)
    {
        Value = !Value;

        Test(data.Activator);
    }

    /// <summary>Reports the value as it stands.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Test")]
    private void InputTest(EntityInputData data) => Test(data.Activator);

    /// <summary>Fires whichever of the two outputs the value calls for, carrying it as the value.</summary>
    private void Test(BaseEntity? activator)
        => EntitySystem.TriggerOutput(this, Value ? "OnTrue" : "OnFalse", activator, Value ? "1" : "0");
}
