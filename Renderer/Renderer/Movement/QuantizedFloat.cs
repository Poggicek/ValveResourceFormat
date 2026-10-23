namespace ValveResourceFormat.Renderer.Movement;

/// <summary>
/// Rounds a float to the value a networked quantized float field would carry, so movement runs on
/// exactly what a client sees.
/// </summary>
internal sealed class QuantizedFloat
{
    [Flags]
    public enum EncodeFlags
    {
        None = 0,
        RoundDown = 1 << 0,
        RoundUp = 1 << 1,
        EncodeZeroExactly = 1 << 2,
        EncodeIntegersExactly = 1 << 3,
    }

    /// <summary>Velocities, 20 bits over [-16384, 16384] with zero exact.</summary>
    public static QuantizedFloat Velocity { get; } = new(20, EncodeFlags.EncodeZeroExactly, -16384f, 16384f);

    /// <summary>How submerged an entity is, 8 bits over [0, 1].</summary>
    public static QuantizedFloat WaterLevel { get; } = new(8, EncodeFlags.EncodeIntegersExactly, 0f, 1f);

    private readonly float Low;
    private readonly float High;
    private readonly double HighLowMultiplier;
    private readonly double DecodeMultiplier;
    private readonly EncodeFlags ExactFlags;
    private readonly int Bits;
    private readonly bool NoScale;

    public QuantizedFloat(int bits, EncodeFlags flags, float low, float high)
    {
        if ((byte)(bits - 1) > 30)
        {
            Low = float.MinValue;
            High = float.MaxValue;
            NoScale = true;
            return;
        }

        Low = low;
        High = high;
        Bits = bits;

        if ((low == 0f && flags.HasFlag(EncodeFlags.RoundDown)) || (high == 0f && flags.HasFlag(EncodeFlags.RoundUp)))
        {
            flags &= ~EncodeFlags.EncodeZeroExactly;
        }

        if (low == 0f && flags.HasFlag(EncodeFlags.EncodeZeroExactly))
        {
            flags = (flags & ~(EncodeFlags.EncodeZeroExactly | EncodeFlags.RoundDown)) | EncodeFlags.RoundDown;
        }

        if (high == 0f && flags.HasFlag(EncodeFlags.EncodeZeroExactly))
        {
            flags = (flags & ~(EncodeFlags.EncodeZeroExactly | EncodeFlags.RoundUp)) | EncodeFlags.RoundUp;
        }

        if (low >= 0f || high <= 0f)
        {
            flags &= ~EncodeFlags.EncodeZeroExactly;
        }

        if (flags.HasFlag(EncodeFlags.EncodeIntegersExactly))
        {
            flags &= ~(EncodeFlags.RoundDown | EncodeFlags.RoundUp | EncodeFlags.EncodeZeroExactly);
        }

        var exactFlags = flags & (EncodeFlags.RoundDown | EncodeFlags.RoundUp | EncodeFlags.EncodeZeroExactly);
        var steps = 1 << bits;

        if (flags.HasFlag(EncodeFlags.RoundDown))
        {
            High -= (High - Low) / steps;
        }
        else if (flags.HasFlag(EncodeFlags.RoundUp))
        {
            Low += (High - Low) / steps;
        }

        if (flags.HasFlag(EncodeFlags.EncodeIntegersExactly))
        {
            var delta = (int)High - (int)Low;
            var bitsNeeded = 0;

            if (delta > 1)
            {
                for (var n = (uint)delta >> 1; n != 0; n >>= 1)
                {
                    bitsNeeded++;
                }
            }

            var range = 1 << (bitsNeeded + 1);
            var widerBits = bits;

            while ((1 << widerBits) < range)
            {
                widerBits++;
            }

            if (widerBits > bits)
            {
                bits = widerBits;
                Bits = bits;
                steps = 1 << bits;
            }

            High = (Low + range) - ((float)range / steps);
        }

        var highValue = (uint)((1L << bits) - 1);
        var multiplier = (double)(int)highValue;
        var spread = (double)(High - Low);

        if (MathF.Abs((float)spread - 0f) > 0.001f)
        {
            multiplier /= spread;
        }

        HighLowMultiplier = multiplier;
        DecodeMultiplier = 1.0 / (steps - 1);

        // Drop the special cases the plain encoding already hits exactly
        if (exactFlags.HasFlag(EncodeFlags.RoundDown) && QuantizeNoSpecial(Low) == Low)
        {
            exactFlags &= ~EncodeFlags.RoundDown;
        }

        if (exactFlags.HasFlag(EncodeFlags.RoundUp) && QuantizeNoSpecial(High) == High)
        {
            exactFlags &= ~EncodeFlags.RoundUp;
        }

        if (exactFlags.HasFlag(EncodeFlags.EncodeZeroExactly) && QuantizeNoSpecial(0f) == 0f)
        {
            exactFlags &= ~EncodeFlags.EncodeZeroExactly;
        }

        ExactFlags = exactFlags;
    }

    /// <summary>Rounds <paramref name="value"/> the way encoding and decoding it would.</summary>
    public float Quantize(float value)
    {
        if (NoScale)
        {
            return value;
        }

        if (value < Low)
        {
            return Low;
        }

        if (value > High)
        {
            return High;
        }

        if (ExactFlags.HasFlag(EncodeFlags.RoundDown) && value == Low)
        {
            return Low;
        }

        if (ExactFlags.HasFlag(EncodeFlags.RoundUp) && value == High)
        {
            return High;
        }

        if (ExactFlags.HasFlag(EncodeFlags.EncodeZeroExactly) && value == 0f)
        {
            return 0f;
        }

        return QuantizeNoSpecial(value);
    }

    /// <summary>Reads a value encoded with these settings from a network bit stream.</summary>
    public float Decode(Demo.BitReader reader)
    {
        if (NoScale)
        {
            return reader.ReadFloat();
        }

        if (ExactFlags.HasFlag(EncodeFlags.RoundDown) && reader.ReadBit())
        {
            return Low;
        }

        if (ExactFlags.HasFlag(EncodeFlags.RoundUp) && reader.ReadBit())
        {
            return High;
        }

        if (ExactFlags.HasFlag(EncodeFlags.EncodeZeroExactly) && reader.ReadBit())
        {
            return 0f;
        }

        return MathF.Min(DecodeSteps((int)reader.ReadBits(Bits)), High);
    }

    private float QuantizeNoSpecial(float value)
    {
        var quantized = (int)(((double)(value - Low) * HighLowMultiplier) + 0.5);

        return MathF.Min(DecodeSteps(quantized), High);
    }

    private float DecodeSteps(int quantized) => (float)((quantized * DecodeMultiplier * (High - Low)) + Low);
}
