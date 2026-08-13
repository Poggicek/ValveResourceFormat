using System.Globalization;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>math_counter</c>. Holds a number, clamped to an authored range, and reports when it reaches the ends
/// of that range. How a map counts things: scores, lives, how many buttons are still to be pressed.
/// </summary>
/// <remarks>
/// <c>OutValue</c> carries the number it now holds, which is the whole point of the entity: a counter wired
/// into a <see cref="LogicCase"/> is how a map does "on the third press", and the case has nothing to match
/// unless the value travels with the output. A connection authored with its own parameter still overrides
/// it, as in the engine.
/// </remarks>
public sealed class MathCounter : BaseEntity
{
    /// <summary>Gets the current value.</summary>
    public float Value { get; private set; }

    /// <summary>Gets the lowest value the counter may hold.</summary>
    public float Min { get; private set; }

    /// <summary>Gets the highest value the counter may hold. Zero means unbounded, as in the engine.</summary>
    public float Max { get; private set; }

    /// <summary>Gets whether the counter accepts changes. The <c>Disable</c> input clears it.</summary>
    public bool IsEnabled { get; private set; } = true;

    /// <summary>
    /// Whether the map authored a range to hold the value in. Both ends left at zero means none, which is
    /// how the engine tells "count between these" from "just count".
    /// </summary>
    private bool HasRange => Min != 0f || Max != 0f;

    private bool hitMin;
    private bool hitMax;

    /// <summary>
    /// Initializes a <c>math_counter</c> from its keyvalues.
    /// </summary>
    /// <param name="system">The world this entity belongs to.</param>
    /// <param name="spawnInfo">The entity's keyvalues and spawn context.</param>
    public MathCounter(EntitySystem system, EntitySpawnInfo spawnInfo) : base(system, spawnInfo)
    {
    }

    /// <inheritdoc/>
    public override void Spawn()
    {
        Min = KeyValues.GetFloatProperty("min");
        Max = KeyValues.GetFloatProperty("max");

        // Authored the wrong way round the range would clamp everything to nothing, so the engine swaps
        // them rather than honouring what the map said
        if (Min > Max)
        {
            (Min, Max) = (Max, Min);
        }

        Value = Clamp(KeyValues.GetFloatProperty("startvalue"));
        IsEnabled = !KeyValues.GetBooleanProperty("startdisabled");
    }

    /// <summary>Adds to the counter.</summary>
    /// <param name="data">Carries the amount to add.</param>
    [EntityInput("Add")]
    private void InputAdd(EntityInputData data) => SetValue(Value + data.Float(), data.Activator);

    /// <summary>Subtracts from the counter.</summary>
    /// <param name="data">Carries the amount to subtract.</param>
    [EntityInput("Subtract")]
    private void InputSubtract(EntityInputData data) => SetValue(Value - data.Float(), data.Activator);

    /// <summary>Sets the counter, firing the outputs the new value calls for.</summary>
    /// <param name="data">Carries the new value.</param>
    [EntityInput("SetValue")]
    private void InputSetValue(EntityInputData data) => SetValue(data.Float(), data.Activator);

    /// <summary>Sets the counter without firing anything.</summary>
    /// <param name="data">Carries the new value.</param>
    [EntityInput("SetValueNoFire")]
    private void InputSetValueNoFire(EntityInputData data)
    {
        if (IsEnabled)
        {
            Value = Clamp(data.Float());
        }
    }

    /// <summary>Changes the upper bound.</summary>
    /// <param name="data">Carries the new maximum.</param>
    [EntityInput("SetHitMax")]
    private void InputSetHitMax(EntityInputData data)
    {
        Max = data.Float();

        // A range cannot be inside out, so the other end gives way
        if (Max < Min)
        {
            Min = Max;
        }

        SetValue(Value, data.Activator);
    }

    /// <summary>Changes the lower bound.</summary>
    /// <param name="data">Carries the new minimum.</param>
    [EntityInput("SetHitMin")]
    private void InputSetHitMin(EntityInputData data)
    {
        Min = data.Float();

        if (Max < Min)
        {
            Max = Min;
        }

        SetValue(Value, data.Activator);
    }

    /// <summary>
    /// Reports the value the counter already holds, without changing it. Its own output rather than
    /// <c>OutValue</c>, so that polling a counter is not mistaken for it having counted.
    /// </summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("GetValue")]
    private void InputGetValue(EntityInputData data)
        => EntitySystem.TriggerOutput(this, "OnGetValue", data.Activator, FormatValue(Value));

    /// <summary>Lets the counter accept changes again.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Enable")]
    private void InputEnable(EntityInputData data) => IsEnabled = true;

    /// <summary>Holds the counter at its current value.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("Disable")]
    private void InputDisable(EntityInputData data) => IsEnabled = false;

    private void SetValue(float value, BaseEntity? activator)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (HasRange)
        {
            // Reaching a limit is an edge, not a state: a counter held at its maximum reports arriving
            // there once, and reports leaving it once, however many times it is added to in between
            UpdateLimit(value >= Max, ref hitMax, Max, "OnHitMax", "OnChangedFromMax", activator);
            UpdateLimit(value <= Min, ref hitMin, Min, "OnHitMin", "OnChangedFromMin", activator);
        }

        Value = Clamp(value);

        EntitySystem.TriggerOutput(this, "OutValue", activator, FormatValue(Value));
    }

    /// <summary>
    /// Reports one end of the range being reached or left. <paramref name="limit"/> is compared against the
    /// value the counter still holds, since leaving an end is only news when it was sitting on it.
    /// </summary>
    private void UpdateLimit(bool reached, ref bool wasReached, float limit, string onHit, string onChanged, BaseEntity? activator)
    {
        if (reached)
        {
            if (!wasReached)
            {
                wasReached = true;

                EntitySystem.TriggerOutput(this, onHit, activator);
            }

            return;
        }

        if (Value == limit)
        {
            EntitySystem.TriggerOutput(this, onChanged, activator);
        }

        wasReached = false;
    }

    /// <summary>
    /// Clamps to the authored range. A counter with neither end authored has no range at all rather than
    /// one that runs from zero to zero, so it counts as far in either direction as the map drives it.
    /// </summary>
    private float Clamp(float value) => HasRange ? Math.Clamp(value, Min, Max) : value;

    /// <summary>
    /// The value as entity I/O carries it. Whole numbers are written without a fractional part, so that a
    /// counter feeding a <see cref="LogicCase"/> matches a case authored as <c>1</c>.
    /// </summary>
    private static string FormatValue(float value) => value.ToString(CultureInfo.InvariantCulture);
}
