using System.Globalization;

namespace ValveResourceFormat.Particles.Debugging
{
    /// <summary>
    /// One attribute of a <see cref="Particle"/> as the debugger shows it: a name, a way to read it, and
    /// value comparison that treats two NaNs as the same value so a NaN that persists is not a change.
    /// </summary>
    public sealed class ParticleAttribute
    {
        private delegate Vector3 VectorReader(in Particle particle);
        private delegate float ScalarReader(in Particle particle);
        private delegate int IntegerReader(in Particle particle);

        private readonly VectorReader? readVector;
        private readonly ScalarReader? readScalar;
        private readonly IntegerReader? readInteger;

        /// <summary>The attribute's name.</summary>
        public string Name { get; }

        /// <summary>Position of the attribute in <see cref="All"/>, which is also its bit in a changed-attribute mask.</summary>
        public int Index { get; }

        // Only All constructs attributes, so this numbers them in display order.
        private static int nextIndex;

        private ParticleAttribute(string name, VectorReader? vector = null, ScalarReader? scalar = null, IntegerReader? integer = null)
        {
            Name = name;
            Index = nextIndex++;
            readVector = vector;
            readScalar = scalar;
            readInteger = integer;
        }

        /// <summary>Every attribute the debugger tracks, in display order. At most 64, so a mask fits a <see cref="ulong"/>.</summary>
        public static IReadOnlyList<ParticleAttribute> All { get; } =
        [
            new("Position", vector: static (in Particle p) => p.Position),
            new("PositionPrevious", vector: static (in Particle p) => p.PositionPrevious),
            new("Velocity", vector: static (in Particle p) => p.Velocity),
            new("Age", scalar: static (in Particle p) => p.Age),
            new("Lifetime", scalar: static (in Particle p) => p.Lifetime),
            new("CreationTime", scalar: static (in Particle p) => p.CreationTime),
            new("Radius", scalar: static (in Particle p) => p.Radius),
            new("Alpha", scalar: static (in Particle p) => p.Alpha),
            new("AlphaAlternate", scalar: static (in Particle p) => p.AlphaAlternate),
            new("Color", vector: static (in Particle p) => p.Color),
            new("Rotation", vector: static (in Particle p) => p.Rotation),
            new("RotationSpeed", vector: static (in Particle p) => p.RotationSpeed),
            new("Normal", vector: static (in Particle p) => p.Normal),
            new("TrailLength", scalar: static (in Particle p) => p.TrailLength),
            new("ForceScale", scalar: static (in Particle p) => p.ForceScale),
            new("ForceAccumulator", vector: static (in Particle p) => p.ForceAccumulator),
            new("SequenceNumber", integer: static (in Particle p) => p.SequenceNumber),
            new("SecondSequenceNumber", integer: static (in Particle p) => p.SecondSequenceNumber),
            new("ManualAnimationFrame", scalar: static (in Particle p) => p.ManualAnimationFrame),
            new("UniqueParticleId", integer: static (in Particle p) => p.UniqueParticleId),
            new("ParticleId", integer: static (in Particle p) => p.ParticleId),
            new("ParentParticleIndex", integer: static (in Particle p) => p.ParentParticleIndex),
            new("ParentParticleId", integer: static (in Particle p) => p.ParentParticleId),
            new("RopeSegmentId", integer: static (in Particle p) => p.RopeSegmentId),
            new("RopeSegmentData", vector: static (in Particle p) => p.RopeSegmentData),
            new("UserEventStates", integer: static (in Particle p) => p.UserEventStates),
            new("HitboxOffsetPosition", vector: static (in Particle p) => p.HitboxOffsetPosition),
            new("ScratchFloat0", scalar: static (in Particle p) => p.ScratchFloat0),
            new("ScratchFloat1", scalar: static (in Particle p) => p.ScratchFloat1),
            new("ScratchFloat2", scalar: static (in Particle p) => p.ScratchFloat2),
            new("ScratchVector", vector: static (in Particle p) => p.ScratchVector),
            new("ScratchVector2", vector: static (in Particle p) => p.ScratchVector2),
            new("AlphaWindowThreshold", scalar: static (in Particle p) => p.AlphaWindowThreshold),
            new("BoxMins", vector: static (in Particle p) => p.BoxMins),
            new("BoxMaxs", vector: static (in Particle p) => p.BoxMaxs),
            new("BoxAngles", vector: static (in Particle p) => p.BoxAngles),
            new("BoxFlags", scalar: static (in Particle p) => p.BoxFlags),
            new("MarkedAsKilled", integer: static (in Particle p) => p.MarkedAsKilled ? 1 : 0),
        ];

        /// <summary>Formats the attribute's value on <paramref name="particle"/> for display.</summary>
        public string Format(in Particle particle)
        {
            if (readInteger != null)
            {
                return readInteger(in particle).ToString(CultureInfo.InvariantCulture);
            }

            if (readScalar != null)
            {
                return FormatFloat(readScalar(in particle));
            }

            var vector = readVector!(in particle);
            return string.Create(CultureInfo.InvariantCulture, $"{FormatFloat(vector.X)}, {FormatFloat(vector.Y)}, {FormatFloat(vector.Z)}");
        }

        /// <summary>Whether the attribute holds the same value on both particles.</summary>
        public bool ValuesEqual(in Particle a, in Particle b)
        {
            if (readInteger != null)
            {
                return readInteger(in a) == readInteger(in b);
            }

            // Equals rather than == so that NaN matches NaN.
            if (readScalar != null)
            {
                return readScalar(in a).Equals(readScalar(in b));
            }

            return readVector!(in a).Equals(readVector(in b));
        }

        /// <summary>Whether every component of the attribute's value is a finite number.</summary>
        public bool IsFinite(in Particle particle)
        {
            if (readInteger != null)
            {
                return true;
            }

            if (readScalar != null)
            {
                return float.IsFinite(readScalar(in particle));
            }

            var vector = readVector!(in particle);
            return float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
        }

        /// <summary>Names the attributes set in <paramref name="mask"/>, joined by commas.</summary>
        public static string DescribeMask(ulong mask)
        {
            var names = new List<string>();

            foreach (var attribute in All)
            {
                if ((mask & (1UL << attribute.Index)) != 0)
                {
                    names.Add(attribute.Name);
                }
            }

            return string.Join(", ", names);
        }

        /// <summary>Formats a float compactly, keeping NaN and infinities readable.</summary>
        public static string FormatFloat(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
