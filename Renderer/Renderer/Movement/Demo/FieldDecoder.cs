using System.IO;

namespace ValveResourceFormat.Renderer.Movement.Demo;

/// <summary>How a networked field's value is encoded in the bit stream.</summary>
internal enum FieldEncoding
{
    Bool,
    String,
    Signed32,
    Signed64,
    Unsigned32,
    Unsigned64,
    Fixed64,
    NoScale,
    Coord,
    SimulationTime,
    Quantized,
    VectorNoScale,
    VectorCoord,
    VectorNormal,
    QAngleBits,
    QAngleCoord,
    QAnglePrecise,
    QAngleNoScale,
    Polymorphic,
    BinaryBlock,
}

/// <summary>Decodes one networked field value.</summary>
/// <remarks>
/// Values are boxed as <see cref="bool"/>, <see cref="string"/>, <see cref="int"/>, <see cref="long"/>,
/// <see cref="uint"/>, <see cref="ulong"/>, <see cref="float"/>, <see cref="Vector3"/> (also for
/// angles, as pitch, yaw, roll), <see cref="float"/>[] for vectors of other sizes, and
/// <see cref="byte"/>[].
/// </remarks>
internal sealed class FieldDecoder(FieldEncoding encoding)
{
    public static readonly FieldDecoder Bool = new(FieldEncoding.Bool);
    public static readonly FieldDecoder String = new(FieldEncoding.String);
    public static readonly FieldDecoder Signed32 = new(FieldEncoding.Signed32);
    public static readonly FieldDecoder Signed64 = new(FieldEncoding.Signed64);
    public static readonly FieldDecoder Unsigned32 = new(FieldEncoding.Unsigned32);
    public static readonly FieldDecoder Unsigned64 = new(FieldEncoding.Unsigned64);
    public static readonly FieldDecoder Fixed64 = new(FieldEncoding.Fixed64);
    public static readonly FieldDecoder NoScale = new(FieldEncoding.NoScale);
    public static readonly FieldDecoder Coord = new(FieldEncoding.Coord);
    public static readonly FieldDecoder SimulationTime = new(FieldEncoding.SimulationTime);
    public static readonly FieldDecoder VectorNormal = new(FieldEncoding.VectorNormal);
    public static readonly FieldDecoder QAngleCoord = new(FieldEncoding.QAngleCoord);
    public static readonly FieldDecoder QAnglePrecise = new(FieldEncoding.QAnglePrecise);
    public static readonly FieldDecoder QAngleNoScale = new(FieldEncoding.QAngleNoScale);
    public static readonly FieldDecoder Polymorphic = new(FieldEncoding.Polymorphic);
    public static readonly FieldDecoder BinaryBlock = new(FieldEncoding.BinaryBlock);

    public FieldEncoding Encoding { get; } = encoding;

    public static FieldDecoder VectorNoScaleOf(int components) => new(FieldEncoding.VectorNoScale) { Components = components };

    /// <summary>Gets the float encoding, for <see cref="FieldEncoding.Quantized"/>.</summary>
    public QuantizedFloat? Quantized { get; init; }

    /// <summary>Gets the bits per component, for <see cref="FieldEncoding.QAngleBits"/>.</summary>
    public int BitCount { get; init; }

    /// <summary>Gets the number of components, for the vector encodings.</summary>
    public int Components { get; init; } = 3;

    public object Decode(BitReader reader)
    {
        switch (Encoding)
        {
            case FieldEncoding.Bool:
                return reader.ReadBit();
            case FieldEncoding.String:
                return reader.ReadString();
            case FieldEncoding.Signed32:
                return reader.ReadVarInt32();
            case FieldEncoding.Signed64:
                return reader.ReadVarInt64();
            case FieldEncoding.Unsigned32:
                return reader.ReadVarUInt32();
            case FieldEncoding.Unsigned64:
                return reader.ReadVarUInt64();
            case FieldEncoding.Fixed64:
                return reader.ReadFixed64();
            case FieldEncoding.NoScale:
                return reader.ReadFloat();
            case FieldEncoding.Coord:
                return reader.ReadCoord();
            case FieldEncoding.SimulationTime:
                return reader.ReadVarUInt32() * (1f / 30f);
            case FieldEncoding.Quantized:
                return Quantized!.Decode(reader);
            case FieldEncoding.VectorNoScale:
                return ReadComponents(reader, static r => r.ReadFloat());
            case FieldEncoding.VectorCoord:
                return ReadComponents(reader, static r => r.ReadCoord());
            case FieldEncoding.VectorNormal:
                return ReadNormalVector(reader);
            case FieldEncoding.QAngleBits:
            {
                var scale = 360f / (1u << BitCount);
                return new Vector3(reader.ReadBits(BitCount) * scale, reader.ReadBits(BitCount) * scale, reader.ReadBits(BitCount) * scale);
            }

            case FieldEncoding.QAngleCoord:
                return ReadOptionalAngles(reader, static r => r.ReadCoord());
            case FieldEncoding.QAnglePrecise:
                return ReadOptionalAngles(reader, static r => (r.ReadBits(20) * (360f / (1 << 20))) - 180f);
            case FieldEncoding.QAngleNoScale:
                return new Vector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat());
            case FieldEncoding.Polymorphic:
            {
                // Whether the pointer is set, then which of the types it points to
                var present = reader.ReadBit();
                reader.ReadUBitVar();
                return present;
            }

            case FieldEncoding.BinaryBlock:
                return reader.ReadBytes(checked((int)reader.ReadVarUInt32()));
            default:
                throw new InvalidDataException($"Unknown field encoding {Encoding}.");
        }
    }

    private object ReadComponents(BitReader reader, Func<BitReader, float> read)
    {
        if (Components == 3)
        {
            return new Vector3(read(reader), read(reader), read(reader));
        }

        var values = new float[Components];

        for (var i = 0; i < values.Length; i++)
        {
            values[i] = read(reader);
        }

        return values;
    }

    private static Vector3 ReadNormalVector(BitReader reader)
    {
        var hasX = reader.ReadBit();
        var hasY = reader.ReadBit();
        var x = hasX ? reader.ReadNormal() : 0f;
        var y = hasY ? reader.ReadNormal() : 0f;
        var negativeZ = reader.ReadBit();
        var lengthSquared = (x * x) + (y * y);
        var z = lengthSquared < 1f ? MathF.Sqrt(1f - lengthSquared) : 0f;

        return new Vector3(x, y, negativeZ ? -z : z);
    }

    private static Vector3 ReadOptionalAngles(BitReader reader, Func<BitReader, float> read)
    {
        var hasPitch = reader.ReadBit();
        var hasYaw = reader.ReadBit();
        var hasRoll = reader.ReadBit();

        return new Vector3(hasPitch ? read(reader) : 0f, hasYaw ? read(reader) : 0f, hasRoll ? read(reader) : 0f);
    }
}
