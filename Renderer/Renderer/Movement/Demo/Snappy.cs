using System.Buffers.Binary;
using System.IO;

namespace ValveResourceFormat.Renderer.Movement.Demo;

/// <summary>
/// Decompresses raw, unframed Snappy blocks, which demos use for their frames, string tables and
/// string table values.
/// </summary>
internal static class Snappy
{
    public static byte[] Decompress(ReadOnlySpan<byte> input)
    {
        var position = 0;
        var length = checked((int)ProtoReader.ReadVarint(input, ref position));
        var output = new byte[length];
        var written = 0;

        while (position < input.Length)
        {
            var tag = input[position++];
            int copyLength;
            int offset;

            switch (tag & 3)
            {
                case 0:
                    var literalLength = tag >> 2;

                    // Long literals store their length in the next one to four bytes
                    if (literalLength >= 60)
                    {
                        var lengthBytes = literalLength - 59;
                        literalLength = 0;

                        for (var i = 0; i < lengthBytes; i++)
                        {
                            literalLength |= input[position++] << (8 * i);
                        }
                    }

                    literalLength++;
                    input.Slice(position, literalLength).CopyTo(output.AsSpan(written));
                    position += literalLength;
                    written += literalLength;
                    continue;

                case 1:
                    copyLength = ((tag >> 2) & 7) + 4;
                    offset = ((tag >> 5) << 8) | input[position++];
                    break;

                case 2:
                    copyLength = (tag >> 2) + 1;
                    offset = BinaryPrimitives.ReadUInt16LittleEndian(input[position..]);
                    position += 2;
                    break;

                default:
                    copyLength = (tag >> 2) + 1;
                    offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(input[position..]));
                    position += 4;
                    break;
            }

            if (offset == 0 || offset > written || written + copyLength > length)
            {
                throw new InvalidDataException("Snappy copy is out of range.");
            }

            // A copy may overlap the bytes it is producing, so it goes a byte at a time
            for (var i = 0; i < copyLength; i++, written++)
            {
                output[written] = output[written - offset];
            }
        }

        if (written != length)
        {
            throw new InvalidDataException("Snappy block is shorter than its header says.");
        }

        return output;
    }
}
