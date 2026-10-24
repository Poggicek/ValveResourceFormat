using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace ValveResourceFormat.Renderer.Movement;

/// <summary>Saves the movement state as raw bytes, exact and compact.</summary>
internal sealed class BinaryStateWriter(ArrayBufferWriter<byte> buffer) : IMovementStateVisitor
{
    public void Visit(string name, ref float value) => WriteSingle(value);

    public void Visit(string name, ref int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer.GetSpan(sizeof(int)), value);
        buffer.Advance(sizeof(int));
    }

    public void Visit(string name, ref bool value)
    {
        buffer.GetSpan(1)[0] = value ? (byte)1 : (byte)0;
        buffer.Advance(1);
    }

    public void Visit(string name, ref ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.GetSpan(sizeof(ulong)), value);
        buffer.Advance(sizeof(ulong));
    }

    public void Visit(string name, ref Vector2 value)
    {
        WriteSingle(value.X);
        WriteSingle(value.Y);
    }

    public void Visit(string name, ref Vector3 value)
    {
        WriteSingle(value.X);
        WriteSingle(value.Y);
        WriteSingle(value.Z);
    }

    private void WriteSingle(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(buffer.GetSpan(sizeof(float)), value);
        buffer.Advance(sizeof(float));
    }
}

/// <summary>Restores the movement state saved by <see cref="BinaryStateWriter"/>.</summary>
internal sealed class BinaryStateReader(ReadOnlyMemory<byte> data) : IMovementStateVisitor
{
    private int Offset;

    public void Visit(string name, ref float value) => value = ReadSingle();
    public void Visit(string name, ref int value) => value = BinaryPrimitives.ReadInt32LittleEndian(Take(sizeof(int)));
    public void Visit(string name, ref bool value) => value = Take(1)[0] != 0;
    public void Visit(string name, ref ulong value) => value = BinaryPrimitives.ReadUInt64LittleEndian(Take(sizeof(ulong)));
    public void Visit(string name, ref Vector2 value) => value = new Vector2(ReadSingle(), ReadSingle());
    public void Visit(string name, ref Vector3 value) => value = new Vector3(ReadSingle(), ReadSingle(), ReadSingle());

    private float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(sizeof(float)));

    private ReadOnlySpan<byte> Take(int length)
    {
        var span = data.Span.Slice(Offset, length);
        Offset += length;
        return span;
    }
}

/// <summary>Writes the movement state as JSON properties named after its fields.</summary>
internal sealed class JsonStateWriter(Utf8JsonWriter writer) : IMovementStateVisitor
{
    public void Visit(string name, ref float value) => MovementRecorder.WriteFloat(writer, name, value);
    public void Visit(string name, ref int value) => writer.WriteNumber(name, value);
    public void Visit(string name, ref bool value) => writer.WriteBoolean(name, value);
    public void Visit(string name, ref ulong value) => writer.WriteNumber(name, value);

    public void Visit(string name, ref Vector2 value)
    {
        writer.WriteStartArray(name);
        MovementRecorder.WriteFloatValue(writer, value.X);
        MovementRecorder.WriteFloatValue(writer, value.Y);
        writer.WriteEndArray();
    }

    public void Visit(string name, ref Vector3 value) => MovementRecorder.WriteVector(writer, name, value);
}

/// <summary>Restores the movement state from what <see cref="JsonStateWriter"/> wrote, leaving fields it lacks alone.</summary>
internal sealed class JsonStateReader(JsonElement state) : IMovementStateVisitor
{
    public void Visit(string name, ref float value)
    {
        if (state.TryGetProperty(name, out var element))
        {
            value = MovementJson.ReadFloat(element);
        }
    }

    public void Visit(string name, ref int value)
    {
        if (state.TryGetProperty(name, out var element))
        {
            value = element.GetInt32();
        }
    }

    public void Visit(string name, ref bool value)
    {
        if (state.TryGetProperty(name, out var element))
        {
            value = element.GetBoolean();
        }
    }

    public void Visit(string name, ref ulong value)
    {
        if (state.TryGetProperty(name, out var element))
        {
            value = element.GetUInt64();
        }
    }

    public void Visit(string name, ref Vector2 value)
    {
        if (state.TryGetProperty(name, out var element))
        {
            value = new Vector2(MovementJson.ReadFloat(element[0]), MovementJson.ReadFloat(element[1]));
        }
    }

    public void Visit(string name, ref Vector3 value)
    {
        if (state.TryGetProperty(name, out var element))
        {
            value = MovementJson.ReadVector(element);
        }
    }
}

/// <summary>The movement settings by their console variable names, for writing and reading them back.</summary>
internal static class MovementVarsJson
{
    public static readonly (string Name, Func<MovementVars, float> Get, Action<MovementVars, float> Set)[] Floats =
    [
        ("sv_accelerate", v => v.Accelerate, (v, x) => v.Accelerate = x),
        ("sv_air_max_wishspeed", v => v.AirMaxWishSpeed, (v, x) => v.AirMaxWishSpeed = x),
        ("sv_airaccelerate", v => v.AirAccelerate, (v, x) => v.AirAccelerate = x),
        ("sv_bhop_time_window", v => v.BhopTimeWindow, (v, x) => v.BhopTimeWindow = x),
        ("sv_bounce", v => v.Bounce, (v, x) => v.Bounce = x),
        ("sv_friction", v => v.Friction, (v, x) => v.Friction = x),
        ("sv_gravity", v => v.Gravity, (v, x) => v.Gravity = x),
        ("sv_jump_impulse", v => v.JumpImpulse, (v, x) => v.JumpImpulse = x),
        ("sv_jump_spam_penalty_time", v => v.JumpSpamPenaltyTime, (v, x) => v.JumpSpamPenaltyTime = x),
        ("sv_ladder_angle", v => v.LadderAngle, (v, x) => v.LadderAngle = x),
        ("sv_ladder_dampen", v => v.LadderDampen, (v, x) => v.LadderDampen = x),
        ("sv_ladder_scale_speed", v => v.LadderScaleSpeed, (v, x) => v.LadderScaleSpeed = x),
        ("sv_maxspeed", v => v.MaxSpeed, (v, x) => v.MaxSpeed = x),
        ("sv_maxvelocity", v => v.MaxVelocity, (v, x) => v.MaxVelocity = x),
        ("sv_min_jump_landing_sound", v => v.MinJumpLandingSound, (v, x) => v.MinJumpLandingSound = x),
        ("sv_noclipaccelerate", v => v.NoclipAccelerate, (v, x) => v.NoclipAccelerate = x),
        ("sv_noclipspeed", v => v.NoclipSpeed, (v, x) => v.NoclipSpeed = x),
        ("sv_rollangle", v => v.RollAngle, (v, x) => v.RollAngle = x),
        ("sv_rollspeed", v => v.RollSpeed, (v, x) => v.RollSpeed = x),
        ("sv_staminajumpcost", v => v.StaminaJumpCost, (v, x) => v.StaminaJumpCost = x),
        ("sv_staminalandcost", v => v.StaminaLandCost, (v, x) => v.StaminaLandCost = x),
        ("sv_staminamax", v => v.StaminaMax, (v, x) => v.StaminaMax = x),
        ("sv_staminarecoveryrate", v => v.StaminaRecoveryRate, (v, x) => v.StaminaRecoveryRate = x),
        ("sv_standable_normal", v => v.StandableNormal, (v, x) => v.StandableNormal = x),
        ("sv_step_move_vel_min", v => v.StepMoveVelMin, (v, x) => v.StepMoveVelMin = x),
        ("sv_stepsize", v => v.StepSize, (v, x) => v.StepSize = x),
        ("sv_stopspeed", v => v.StopSpeed, (v, x) => v.StopSpeed = x),
        ("sv_timebetweenducks", v => v.TimeBetweenDucks, (v, x) => v.TimeBetweenDucks = x),
        ("sv_walkable_normal", v => v.WalkableNormal, (v, x) => v.WalkableNormal = x),
        ("sv_water_slow_amount", v => v.WaterSlowAmount, (v, x) => v.WaterSlowAmount = x),
        ("sv_falldamage_scale", v => v.FallDamageScale, (v, x) => v.FallDamageScale = x),
        ("view_punch_decay", v => v.ViewPunchDecay, (v, x) => v.ViewPunchDecay = x),
    ];

    public static readonly (string Name, Func<MovementVars, bool> Get, Action<MovementVars, bool> Set)[] Booleans =
    [
        ("sv_accelerate_use_weapon_speed", v => v.AccelerateUseWeaponSpeed, (v, x) => v.AccelerateUseWeaponSpeed = x),
        ("sv_autobunnyhopping", v => v.AutoBunnyHopping, (v, x) => v.AutoBunnyHopping = x),
        ("sv_enablebunnyhopping", v => v.EnableBunnyHopping, (v, x) => v.EnableBunnyHopping = x),
        ("sv_jump_precision_enable", v => v.JumpPrecision, (v, x) => v.JumpPrecision = x),
        ("sv_legacy_jump", v => v.LegacyJump, (v, x) => v.LegacyJump = x),
        ("sv_optimizedmovement", v => v.OptimizedMovement, (v, x) => v.OptimizedMovement = x),
        ("sv_quantize_movement_input", v => v.QuantizeMovementInput, (v, x) => v.QuantizeMovementInput = x),
        ("sv_subtick_movement_view_angles", v => v.SubtickMovementViewAngles, (v, x) => v.SubtickMovementViewAngles = x),
    ];

    public static void Copy(MovementVars from, MovementVars to)
    {
        foreach (var (_, get, set) in Floats)
        {
            set(to, get(from));
        }

        foreach (var (_, get, set) in Booleans)
        {
            set(to, get(from));
        }
    }

    public static MovementVars Read(JsonElement vars)
    {
        var result = new MovementVars();

        foreach (var (name, _, set) in Floats)
        {
            if (vars.TryGetProperty(name, out var element))
            {
                set(result, MovementJson.ReadFloat(element));
            }
        }

        foreach (var (name, _, set) in Booleans)
        {
            if (vars.TryGetProperty(name, out var element))
            {
                set(result, element.GetBoolean());
            }
        }

        return result;
    }
}

internal static class MovementJson
{
    // Non-finite values are written as strings, as JSON has no numbers for them
    public static float ReadFloat(JsonElement element)
        => element.ValueKind == JsonValueKind.String
            ? float.Parse(element.GetString()!, CultureInfo.InvariantCulture)
            : element.GetSingle();

    public static Vector3 ReadVector(JsonElement element)
        => new(ReadFloat(element[0]), ReadFloat(element[1]), ReadFloat(element[2]));
}
