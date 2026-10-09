namespace ValveResourceFormat.Particles.Debugging
{
    /// <summary>
    /// Particle states that are usually the visible symptom of something going wrong earlier in the
    /// pipeline. Some are legitimate at times, like a particle faded to zero alpha at the end of its life,
    /// so they point at where to look rather than proving a bug.
    /// </summary>
    [Flags]
    public enum ParticleAnomalies
    {
        /// <summary>No anomaly.</summary>
        None = 0,

        /// <summary>An attribute holds NaN or an infinity, which poisons everything computed from it.</summary>
        NonFinite = 1 << 0,

        /// <summary>The particle sits absurdly far from control point 0, typically from an exploding integration.</summary>
        FarAway = 1 << 1,

        /// <summary>The radius is zero or negative, so the particle draws nothing.</summary>
        ZeroRadius = 1 << 2,

        /// <summary>The alpha is zero or negative, so the particle draws nothing.</summary>
        Transparent = 1 << 3,

        /// <summary>The color is black, which draws nothing with additive blending.</summary>
        Black = 1 << 4,

        /// <summary>The lifetime is zero or negative, so the particle dies on its first step.</summary>
        NoLifetime = 1 << 5,

        /// <summary>The radius is so large the particle covers everything around it.</summary>
        HugeRadius = 1 << 6,
    }

    /// <summary>Detects <see cref="ParticleAnomalies"/> on a particle.</summary>
    public static class ParticleAnomalyDetector
    {
        /// <summary>Distance from control point 0 past which a particle counts as <see cref="ParticleAnomalies.FarAway"/>.</summary>
        public const float FarAwayDistance = 100_000f;

        /// <summary>Radius past which a particle counts as <see cref="ParticleAnomalies.HugeRadius"/>.</summary>
        public const float HugeRadius = 10_000f;

        /// <summary>The anomalies that are wrong whenever they appear, as opposed to the ones a fade can cause on purpose.</summary>
        public const ParticleAnomalies Severe = ParticleAnomalies.NonFinite | ParticleAnomalies.FarAway | ParticleAnomalies.HugeRadius;

        /// <summary>Finds the anomalies <paramref name="particle"/> shows.</summary>
        /// <param name="particle">The particle to test.</param>
        /// <param name="origin">The position of control point 0.</param>
        public static ParticleAnomalies Detect(in Particle particle, Vector3 origin)
        {
            var anomalies = ParticleAnomalies.None;

            foreach (var attribute in ParticleAttribute.All)
            {
                if (!attribute.IsFinite(in particle))
                {
                    anomalies |= ParticleAnomalies.NonFinite;
                    break;
                }
            }

            if (Vector3.DistanceSquared(particle.Position, origin) > FarAwayDistance * FarAwayDistance)
            {
                anomalies |= ParticleAnomalies.FarAway;
            }

            if (particle.Radius <= 0f)
            {
                anomalies |= ParticleAnomalies.ZeroRadius;
            }
            else if (particle.Radius > HugeRadius)
            {
                anomalies |= ParticleAnomalies.HugeRadius;
            }

            if (particle.Alpha <= 0f)
            {
                anomalies |= ParticleAnomalies.Transparent;
            }

            if (particle.Color is { X: <= 0f, Y: <= 0f, Z: <= 0f })
            {
                anomalies |= ParticleAnomalies.Black;
            }

            if (particle.Lifetime <= 0f)
            {
                anomalies |= ParticleAnomalies.NoLifetime;
            }

            return anomalies;
        }
    }
}
