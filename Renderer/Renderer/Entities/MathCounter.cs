using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>math_counter</c>. Holds a number, clamped to an authored range, and reports when it reaches the ends
/// of that range. How a map counts things: scores, lives, how many buttons are still to be pressed.
/// </summary>
/// <remarks>
/// <c>OutValue</c> fires with no value attached. Entity I/O carries a parameter, but a connection's own
/// <c>OverrideParam</c> is what the wiring in these maps relies on, and the counter's value would need a
/// parameter to travel with the output; until outputs carry one, a target reading <c>OutValue</c> sees the
/// firing but not the number.
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
        SetValue(Value, data.Activator);
    }

    /// <summary>Changes the lower bound.</summary>
    /// <param name="data">Carries the new minimum.</param>
    [EntityInput("SetHitMin")]
    private void InputSetHitMin(EntityInputData data)
    {
        Min = data.Float();
        SetValue(Value, data.Activator);
    }

    /// <summary>Fires <c>OutValue</c> with the value the counter already holds.</summary>
    /// <param name="data">The input's parameter and sender, unused.</param>
    [EntityInput("GetValue")]
    private void InputGetValue(EntityInputData data) => EntitySystem.TriggerOutput(this, "OutValue", data.Activator);

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

        Value = Clamp(value);

        EntitySystem.TriggerOutput(this, "OutValue", activator);

        // The engine reports hitting a limit every time it lands there, not only on the way in
        if (Max != 0f && Value >= Max)
        {
            EntitySystem.TriggerOutput(this, "OnHitMax", activator);
        }
        else if (Value <= Min && Min != 0f)
        {
            EntitySystem.TriggerOutput(this, "OnHitMin", activator);
        }
    }

    /// <summary>Clamps to the authored range, treating an unset maximum as no upper bound.</summary>
    private float Clamp(float value)
    {
        if (Max != 0f && value > Max)
        {
            return Max;
        }

        return value < Min ? Min : value;
    }
}
