using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace ValveResourceFormat.Renderer.Movement.Demo;

/// <summary>How a protobuf field's value is encoded.</summary>
internal enum WireType
{
    Varint = 0,
    Fixed64 = 1,
    LengthDelimited = 2,
    Fixed32 = 5,

    /// <summary>
    /// Not protobuf: delta encoded messages use it to reset a field to its default, or on a repeated
    /// field's list to give its new length.
    /// </summary>
    Reset = 7,
}

/// <summary>Reads protobuf wire format one field at a time, without a schema.</summary>
internal ref struct ProtoReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> Data = data;
    private int Position;

    public int Field { get; private set; }
    public WireType WireType { get; private set; }

    /// <summary>Moves to the next field.</summary>
    /// <returns>False at the end of the message.</returns>
    public bool Next()
    {
        if (Position >= Data.Length)
        {
            return false;
        }

        var key = ReadVarint();
        Field = (int)(key >> 3);
        WireType = (WireType)(key & 7);
        return true;
    }

    public ulong ReadVarint() => ReadVarint(Data, ref Position);

    public int ReadInt32() => (int)ReadVarint();

    public bool ReadBool() => ReadVarint() != 0;

    public uint ReadFixed32()
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(Data[Position..]);
        Position += 4;
        return value;
    }

    public float ReadFloat() => BitConverter.UInt32BitsToSingle(ReadFixed32());

    public ReadOnlySpan<byte> ReadBytes()
    {
        var length = checked((int)ReadVarint());
        var bytes = Data.Slice(Position, length);
        Position += length;
        return bytes;
    }

    public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

    /// <summary>Skips the value of the current field.</summary>
    public void Skip()
    {
        switch (WireType)
        {
            case WireType.Varint:
                ReadVarint();
                break;
            case WireType.Fixed64:
                Position += 8;
                break;
            case WireType.LengthDelimited:
                ReadBytes();
                break;
            case WireType.Fixed32:
                Position += 4;
                break;
            case WireType.Reset:
                break;
            default:
                throw new InvalidDataException($"Unknown protobuf wire type {(int)WireType}.");
        }
    }

    public static ulong ReadVarint(ReadOnlySpan<byte> data, ref int position)
    {
        var value = 0ul;

        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = data[position++];
            value |= (ulong)(b & 0x7F) << shift;

            if (b < 0x80)
            {
                return value;
            }
        }

        throw new InvalidDataException("Varint is longer than ten bytes.");
    }
}
