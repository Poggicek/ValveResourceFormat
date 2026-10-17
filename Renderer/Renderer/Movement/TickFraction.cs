namespace ValveResourceFormat.Renderer.Movement;

/// <summary>
/// A point in time as a whole tick plus how far into it, the time base of jump and landing timing.
/// </summary>
/// <remarks>
/// Kept as a tick and a fraction rather than seconds so times compare exactly however long the game
/// has been running. The arithmetic matches the game's operation for operation.
/// </remarks>
internal readonly record struct TickFraction
{
    public const float TickInterval = 1f / 64f;
    public const float TicksPerSecond = 64f;

    public int Tick { get; }
    public float Fraction { get; }

    /// <summary>Normalizes a fraction outside [0, 1) by moving whole ticks into <see cref="Tick"/>.</summary>
    public TickFraction(int tick, float fraction)
    {
        Tick = tick;
        Fraction = fraction;

        if (fraction is >= 0f and < 1f)
        {
            return;
        }

        var whole = MathF.Truncate(fraction);
        var part = fraction - whole;

        if (part < 0f)
        {
            part += 1f;

            if (part < 1f)
            {
                whole += -1f;
            }
            else
            {
                part = 0f;
            }
        }

        var wholeTicks = whole is >= -2147483600f and < 2147483600f ? (int)whole : 0;
        var carry = new TickFraction(wholeTicks, part);

        Tick += carry.Tick;
        Fraction = carry.Fraction;
    }

    public float ToSeconds() => (Tick * TickInterval) + (Fraction * TickInterval);

    public static TickFraction FromSeconds(float seconds)
    {
        var remainder = seconds % TickInterval;
        var tick = (int)(((seconds - remainder) * TicksPerSecond) + 0.5f);

        return new TickFraction(tick, remainder * TicksPerSecond);
    }

    /// <summary>Converts seconds and snaps the fraction to 1/64 of a tick, as every jump window is.</summary>
    public static TickFraction FromSecondsRounded(float seconds)
    {
        var time = FromSeconds(seconds);
        return new TickFraction(time.Tick, RoundSubtickFraction(time.Fraction));
    }

    /// <summary>
    /// Snaps a fraction in [0, 1) to a multiple of 1/64: the float spacing at 2^17 is exactly that,
    /// so adding and removing it rounds to the nearest.
    /// </summary>
    public static float RoundSubtickFraction(float fraction) => (fraction + 131072f) - 131072f;

    /// <summary>Snaps a coordinate to a multiple of 1/64, the same trick at 3 * 2^16.</summary>
    public static float RoundCoord64(float value) => (value + 196608f) - 196608f;

    public static TickFraction operator -(TickFraction left, TickFraction right)
    {
        var fraction = left.Fraction - right.Fraction;
        var tick = left.Tick - right.Tick;

        if (fraction < 0f)
        {
            fraction += 1f;

            if (fraction < 1f)
            {
                tick--;
            }
            else
            {
                fraction = 0f;
            }
        }

        return new TickFraction(tick, fraction);
    }

    public static TickFraction operator +(TickFraction left, TickFraction right)
    {
        var sum = right.Fraction + left.Fraction;
        var tick = left.Tick + right.Tick;
        var fraction = sum;

        if (sum >= 1f)
        {
            fraction = sum + -1f;
            tick++;
        }

        return new TickFraction(tick, fraction);
    }

    /// <summary>
    /// Orders two times: -1, 0 or 1, and <see cref="Unordered"/> when a fraction is NaN. Callers that
    /// test for "less" count unordered as less, as it is negative as a signed byte.
    /// </summary>
    public static int Compare(TickFraction a, TickFraction b)
    {
        if (a.Tick != b.Tick)
        {
            return a.Tick >= b.Tick ? 1 : -1;
        }

        if (b.Fraction > a.Fraction)
        {
            return -1;
        }

        if (a.Fraction > b.Fraction)
        {
            return 1;
        }

        return a.Fraction == b.Fraction ? 0 : Unordered;
    }

    public const int Unordered = -128;
}
