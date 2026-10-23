using System.IO;
using System.Text;

namespace ValveResourceFormat.Renderer.Movement.Demo;

/// <summary>Reads a network bit stream, least significant bit first.</summary>
internal sealed class BitReader(byte[] data)
{
    private readonly byte[] Data = data;
    private int Position;

    public int BitsLeft => (Data.Length * 8) - Position;

    public uint ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        if (count > BitsLeft)
        {
            throw new EndOfStreamException("Read past the end of the bit stream.");
        }

        var byteIndex = Position >> 3;
        var shift = Position & 7;
        var bytesNeeded = (shift + count + 7) >> 3;
        var window = 0ul;

        for (var i = 0; i < bytesNeeded; i++)
        {
            window |= (ulong)Data[byteIndex + i] << (8 * i);
        }

        Position += count;
        return (uint)((window >> shift) & ((1ul << count) - 1));
    }

    public bool ReadBit() => ReadBits(1) != 0;

    public byte[] ReadBytes(int count)
    {
        var bytes = new byte[count];

        if ((Position & 7) == 0)
        {
            Data.AsSpan(Position >> 3, count).CopyTo(bytes);
            Position += count * 8;
            return bytes;
        }

        for (var i = 0; i < count; i++)
        {
            bytes[i] = (byte)ReadBits(8);
        }

        return bytes;
    }

    /// <summary>Reads a variable length integer: four bits, then two more saying how many follow.</summary>
    public uint ReadUBitVar()
    {
        var value = ReadBits(6);

        return (value & 0x30) switch
        {
            0x10 => (value & 15) | (ReadBits(4) << 4),
            0x20 => (value & 15) | (ReadBits(8) << 4),
            0x30 => (value & 15) | (ReadBits(28) << 4),
            _ => value,
        };
    }

    /// <summary>Reads the variable length integer field paths use, which favors small values more.</summary>
    public uint ReadUBitVarFieldPath()
    {
        if (ReadBit())
        {
            return ReadBits(2);
        }

        if (ReadBit())
        {
            return ReadBits(4);
        }

        if (ReadBit())
        {
            return ReadBits(10);
        }

        if (ReadBit())
        {
            return ReadBits(17);
        }

        return ReadBits(31);
    }

    public uint ReadVarUInt32()
    {
        var value = 0u;

        for (var i = 0; i < 5; i++)
        {
            var b = ReadBits(8);
            value |= (b & 0x7F) << (7 * i);

            if (b < 0x80)
            {
                break;
            }
        }

        return value;
    }

    public ulong ReadVarUInt64()
    {
        var value = 0ul;

        for (var i = 0; i < 10; i++)
        {
            var b = ReadBits(8);
            value |= (ulong)(b & 0x7F) << (7 * i);

            if (b < 0x80)
            {
                break;
            }
        }

        return value;
    }

    public int ReadVarInt32()
    {
        var value = ReadVarUInt32();
        return (int)(value >> 1) ^ -(int)(value & 1);
    }

    public long ReadVarInt64()
    {
        var value = ReadVarUInt64();
        return (long)(value >> 1) ^ -(long)(value & 1);
    }

    public float ReadFloat() => BitConverter.UInt32BitsToSingle(ReadBits(32));

    public ulong ReadFixed64() => ReadBits(32) | ((ulong)ReadBits(32) << 32);

    /// <summary>Reads a world coordinate: an optional 14 bit integer part and 5 bit fraction, and a sign.</summary>
    public float ReadCoord()
    {
        var hasInteger = ReadBit();
        var hasFraction = ReadBit();

        if (!hasInteger && !hasFraction)
        {
            return 0f;
        }

        var negative = ReadBit();
        var integer = hasInteger ? ReadBits(14) + 1 : 0;
        var fraction = hasFraction ? ReadBits(5) : 0;
        var value = (float)(integer + (fraction * (1.0 / 32.0)));

        return negative ? -value : value;
    }

    /// <summary>Reads one component of a unit vector: a sign and an 11 bit fraction.</summary>
    public float ReadNormal()
    {
        var negative = ReadBit();
        var value = ReadBits(11) * (1f / ((1 << 11) - 1));

        return negative ? -value : value;
    }

    public string ReadString()
    {
        var bytes = new List<byte>();

        for (var b = (byte)ReadBits(8); b != 0; b = (byte)ReadBits(8))
        {
            bytes.Add(b);
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }
}
