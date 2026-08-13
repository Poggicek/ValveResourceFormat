using System.Globalization;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>logic_compare</c>. Holds a number and reports how it stands against another. Where
/// <see cref="LogicBranch"/> answers yes or no, this one answers less, equal or more.
/// </summary>
public sealed class LogicCompare : BaseEntity
{
    /// <summary>Gets the value being compared.</summary>
    public float Value { get; private set; }

    /// <summary>Gets the value it is compared against.</summary>
    public float CompareValue { get; private set; }

    /// <summary>
    /// Initializes a <c>logic_compare</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public LogicCompare(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        Value = KeyValues.GetFloatProperty("initialvalue");
        CompareValue = KeyValues.GetFloatProperty("comparevalue");
    }

    /// <summary>Sets the value without comparing it.</summary>
    /// <param name="data">Carries the new value.</param>
    [EntityInput("SetValue")]
    private void InputSetValue(EntityInputData data) => Value = data.Float(Value);

    /// <summary>Sets the value and compares it.</summary>
    /// <param name="data">Carries the new value.</param>
    [EntityInput("SetValueCompare")]
    private void InputSetValueCompare(EntityInputData data)
    {
        Value = data.Float(Value);

        Compare(data.Activator);
    }

    /// <summary>Changes what the value is compared against.</summary>
    /// <param name="data">Carries the new comparison value.</param>
    [EntityInput("SetCompareValue")]
    private void InputSetCompareValue(EntityInputData data) => CompareValue = data.Float(CompareValue);

    /// <summary>Compares the two values as they stand.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Compare")]
    private void InputCompare(EntityInputData data) => Compare(data.Activator);

    /// <summary>
    /// Reports where the value sits, carrying it as the outputs' value. Equality also reports as not
    /// greater and not less, so a map may wire either sense.
    /// </summary>
    private void Compare(BaseEntity? activator)
    {
        var value = Value.ToString(CultureInfo.InvariantCulture);

        if (Value == CompareValue)
        {
            EntitySystem.TriggerOutput(this, "OnEqualTo", activator, value);
            return;
        }

        EntitySystem.TriggerOutput(this, "OnNotEqualTo", activator, value);
        EntitySystem.TriggerOutput(this, Value > CompareValue ? "OnGreaterThan" : "OnLessThan", activator, value);
    }
}
