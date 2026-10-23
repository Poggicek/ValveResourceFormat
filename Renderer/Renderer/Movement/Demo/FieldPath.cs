using System.IO;
using System.Runtime.CompilerServices;

namespace ValveResourceFormat.Renderer.Movement.Demo;

/// <summary>
/// Where a value sits in an entity: an index into the class's fields, then into each nested
/// array, vector or serializer, up to seven deep.
/// </summary>
internal struct FieldPath : IEquatable<FieldPath>
{
    private const int MaxDepth = 7;

    private Indices Path;

    /// <summary>Gets the index of the last element in use.</summary>
    public int Last { get; private set; }

    /// <summary>Gets a path with no elements, which every update starts from.</summary>
    public static FieldPath Empty
    {
        get
        {
            var path = new FieldPath { Last = -1 };
            return path;
        }
    }

    public readonly int this[int index] => Path[index];

    public readonly int Length => Last + 1;

    /// <summary>Returns this path with another index on the end.</summary>
    public readonly FieldPath Push(int index)
    {
        var path = this;
        path.Last++;
        path.Path[path.Last] = index;
        return path;
    }

    /// <summary>
    /// Reads the paths of an entity update: Huffman coded operations, each moving the path on from
    /// the previous one, until the finish operation.
    /// </summary>
    public static void ReadUpdate(BitReader reader, List<FieldPath> paths)
    {
        paths.Clear();

        // The first operation steps from just before the first field
        var path = new FieldPath();
        path.Path[0] = -1;

        while (true)
        {
            var node = 0;

            while (HuffmanTree[node].Operation < 0)
            {
                node = reader.ReadBit() ? HuffmanTree[node].Right : HuffmanTree[node].Left;
            }

            var operation = (Operation)HuffmanTree[node].Operation;

            if (operation == Operation.FieldPathEncodeFinish)
            {
                return;
            }

            path.Apply(operation, reader);
            paths.Add(path);
        }
    }

    private void Apply(Operation operation, BitReader reader)
    {
        switch (operation)
        {
            case Operation.PlusOne:
                Path[Last] += 1;
                break;
            case Operation.PlusTwo:
                Path[Last] += 2;
                break;
            case Operation.PlusThree:
                Path[Last] += 3;
                break;
            case Operation.PlusFour:
                Path[Last] += 4;
                break;
            case Operation.PlusN:
                Path[Last] += (int)reader.ReadUBitVarFieldPath() + 5;
                break;
            case Operation.PushOneLeftDeltaZeroRightZero:
                PushIndex(0);
                break;
            case Operation.PushOneLeftDeltaZeroRightNonZero:
                PushIndex((int)reader.ReadUBitVarFieldPath());
                break;
            case Operation.PushOneLeftDeltaOneRightZero:
                Path[Last] += 1;
                PushIndex(0);
                break;
            case Operation.PushOneLeftDeltaOneRightNonZero:
                Path[Last] += 1;
                PushIndex((int)reader.ReadUBitVarFieldPath());
                break;
            case Operation.PushOneLeftDeltaNRightZero:
                Path[Last] += (int)reader.ReadUBitVarFieldPath();
                PushIndex(0);
                break;
            case Operation.PushOneLeftDeltaNRightNonZero:
                Path[Last] += (int)reader.ReadUBitVarFieldPath() + 2;
                PushIndex((int)reader.ReadUBitVarFieldPath() + 1);
                break;
            case Operation.PushOneLeftDeltaNRightNonZeroPack6Bits:
                Path[Last] += (int)reader.ReadBits(3) + 2;
                PushIndex((int)reader.ReadBits(3) + 1);
                break;
            case Operation.PushOneLeftDeltaNRightNonZeroPack8Bits:
                Path[Last] += (int)reader.ReadBits(4) + 2;
                PushIndex((int)reader.ReadBits(4) + 1);
                break;
            case Operation.PushTwoLeftDeltaZero:
                PushFieldPathVars(reader, 2);
                break;
            case Operation.PushTwoPack5LeftDeltaZero:
                PushPacked5(reader, 2);
                break;
            case Operation.PushThreeLeftDeltaZero:
                PushFieldPathVars(reader, 3);
                break;
            case Operation.PushThreePack5LeftDeltaZero:
                PushPacked5(reader, 3);
                break;
            case Operation.PushTwoLeftDeltaOne:
                Path[Last] += 1;
                PushFieldPathVars(reader, 2);
                break;
            case Operation.PushTwoPack5LeftDeltaOne:
                Path[Last] += 1;
                PushPacked5(reader, 2);
                break;
            case Operation.PushThreeLeftDeltaOne:
                Path[Last] += 1;
                PushFieldPathVars(reader, 3);
                break;
            case Operation.PushThreePack5LeftDeltaOne:
                Path[Last] += 1;
                PushPacked5(reader, 3);
                break;
            case Operation.PushTwoLeftDeltaN:
                Path[Last] += (int)reader.ReadUBitVar() + 2;
                PushFieldPathVars(reader, 2);
                break;
            case Operation.PushTwoPack5LeftDeltaN:
                Path[Last] += (int)reader.ReadUBitVar() + 2;
                PushPacked5(reader, 2);
                break;
            case Operation.PushThreeLeftDeltaN:
                Path[Last] += (int)reader.ReadUBitVar() + 2;
                PushFieldPathVars(reader, 3);
                break;
            case Operation.PushThreePack5LeftDeltaN:
                Path[Last] += (int)reader.ReadUBitVar() + 2;
                PushPacked5(reader, 3);
                break;
            case Operation.PushN:
            {
                var count = (int)reader.ReadUBitVar();
                Path[Last] += (int)reader.ReadUBitVar();
                PushFieldPathVars(reader, count);
                break;
            }

            case Operation.PushNAndNonTopological:
                AddToEach(reader, static r => r.ReadVarInt32() + 1);
                PushFieldPathVars(reader, (int)reader.ReadUBitVar());
                break;
            case Operation.PopOnePlusOne:
                Pop(1);
                Path[Last] += 1;
                break;
            case Operation.PopOnePlusN:
                Pop(1);
                Path[Last] += (int)reader.ReadUBitVarFieldPath() + 1;
                break;
            case Operation.PopAllButOnePlusOne:
                Pop(Last);
                Path[0] += 1;
                break;
            case Operation.PopAllButOnePlusN:
                Pop(Last);
                Path[0] += (int)reader.ReadUBitVarFieldPath() + 1;
                break;
            case Operation.PopAllButOnePlusNPack3Bits:
                Pop(Last);
                Path[0] += (int)reader.ReadBits(3) + 1;
                break;
            case Operation.PopAllButOnePlusNPack6Bits:
                Pop(Last);
                Path[0] += (int)reader.ReadBits(6) + 1;
                break;
            case Operation.PopNPlusOne:
                Pop((int)reader.ReadUBitVarFieldPath());
                Path[Last] += 1;
                break;
            case Operation.PopNPlusN:
                Pop((int)reader.ReadUBitVarFieldPath());
                Path[Last] += reader.ReadVarInt32();
                break;
            case Operation.PopNAndNonTopographical:
                Pop((int)reader.ReadUBitVarFieldPath());
                AddToEach(reader, static r => r.ReadVarInt32());
                break;
            case Operation.NonTopoComplex:
                AddToEach(reader, static r => r.ReadVarInt32());
                break;
            case Operation.NonTopoPenultimatePlusOne:
                Path[Last - 1] += 1;
                break;
            case Operation.NonTopoComplexPack4Bits:
                AddToEach(reader, static r => (int)r.ReadBits(4) - 7);
                break;
            default:
                throw new InvalidDataException($"Unknown field path operation {operation}.");
        }
    }

    private void PushIndex(int index)
    {
        if (Last + 1 >= MaxDepth)
        {
            throw new InvalidDataException("Field path is deeper than seven.");
        }

        Last++;
        Path[Last] = index;
    }

    private void PushFieldPathVars(BitReader reader, int count)
    {
        for (var i = 0; i < count; i++)
        {
            PushIndex((int)reader.ReadUBitVarFieldPath());
        }
    }

    private void PushPacked5(BitReader reader, int count)
    {
        for (var i = 0; i < count; i++)
        {
            PushIndex((int)reader.ReadBits(5));
        }
    }

    private void Pop(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Path[Last] = 0;
            Last--;
        }
    }

    // Each index of the path may move, each flagged by a bit of its own
    private void AddToEach(BitReader reader, Func<BitReader, int> readDelta)
    {
        for (var i = 0; i <= Last; i++)
        {
            if (reader.ReadBit())
            {
                Path[i] += readDelta(reader);
            }
        }
    }

    public readonly bool Equals(FieldPath other)
    {
        if (Last != other.Last)
        {
            return false;
        }

        for (var i = 0; i <= Last; i++)
        {
            if (Path[i] != other.Path[i])
            {
                return false;
            }
        }

        return true;
    }

    public override readonly bool Equals(object? obj) => obj is FieldPath other && Equals(other);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();

        for (var i = 0; i <= Last; i++)
        {
            hash.Add(Path[i]);
        }

        return hash.ToHashCode();
    }

    public override readonly string ToString()
    {
        var parts = new string[Length];

        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = Path[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return $"[{string.Join(", ", parts)}]";
    }

    public static bool operator ==(FieldPath left, FieldPath right) => left.Equals(right);

    public static bool operator !=(FieldPath left, FieldPath right) => !left.Equals(right);

    [InlineArray(MaxDepth)]
    private struct Indices
    {
        private int Element;
    }

    private enum Operation
    {
        PlusOne,
        PlusTwo,
        PlusThree,
        PlusFour,
        PlusN,
        PushOneLeftDeltaZeroRightZero,
        PushOneLeftDeltaZeroRightNonZero,
        PushOneLeftDeltaOneRightZero,
        PushOneLeftDeltaOneRightNonZero,
        PushOneLeftDeltaNRightZero,
        PushOneLeftDeltaNRightNonZero,
        PushOneLeftDeltaNRightNonZeroPack6Bits,
        PushOneLeftDeltaNRightNonZeroPack8Bits,
        PushTwoLeftDeltaZero,
        PushTwoPack5LeftDeltaZero,
        PushThreeLeftDeltaZero,
        PushThreePack5LeftDeltaZero,
        PushTwoLeftDeltaOne,
        PushTwoPack5LeftDeltaOne,
        PushThreeLeftDeltaOne,
        PushThreePack5LeftDeltaOne,
        PushTwoLeftDeltaN,
        PushTwoPack5LeftDeltaN,
        PushThreeLeftDeltaN,
        PushThreePack5LeftDeltaN,
        PushN,
        PushNAndNonTopological,
        PopOnePlusOne,
        PopOnePlusN,
        PopAllButOnePlusOne,
        PopAllButOnePlusN,
        PopAllButOnePlusNPack3Bits,
        PopAllButOnePlusNPack6Bits,
        PopNPlusOne,
        PopNPlusN,
        PopNAndNonTopographical,
        NonTopoComplex,
        NonTopoPenultimatePlusOne,
        NonTopoComplexPack4Bits,
        FieldPathEncodeFinish,
    }

    // How often each operation is used, in operation order, which the Huffman codes are built from
    private static readonly int[] OperationWeights =
    [
        36271, 10334, 1375, 646, 4128, 35, 3, 521, 2942, 560, 471, 10530, 251, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 310, 2, 0, 1837, 149, 300, 634, 0, 0, 1, 76, 271, 99, 25474,
    ];

    private readonly record struct HuffmanNode(int Weight, int Value, int Left, int Right, int Operation);

    private static readonly HuffmanNode[] HuffmanTree = BuildHuffmanTree();

    // The root ends up at index 0. Unused operations count as used once; among equal weights the
    // higher value comes out first, leaves being valued by operation and inner nodes after them
    private static HuffmanNode[] BuildHuffmanTree()
    {
        var nodes = new List<HuffmanNode>();
        var queue = new PriorityQueue<int, (int Weight, int Value)>(Comparer<(int Weight, int Value)>.Create(
            static (a, b) => a.Weight != b.Weight ? a.Weight.CompareTo(b.Weight) : b.Value.CompareTo(a.Value)));

        for (var operation = 0; operation < OperationWeights.Length; operation++)
        {
            var weight = Math.Max(OperationWeights[operation], 1);
            nodes.Add(new HuffmanNode(weight, operation, -1, -1, operation));
            queue.Enqueue(operation, (weight, operation));
        }

        while (queue.Count > 1)
        {
            var left = queue.Dequeue();
            var right = queue.Dequeue();
            var weight = nodes[left].Weight + nodes[right].Weight;
            var value = nodes.Count;

            nodes.Add(new HuffmanNode(weight, value, left, right, -1));
            queue.Enqueue(value, (weight, value));
        }

        // Move the root to the front so walks start at 0
        var root = queue.Dequeue();
        var tree = new HuffmanNode[nodes.Count];
        var remap = new int[nodes.Count];
        var order = new List<int> { root };

        for (var i = 0; i < order.Count; i++)
        {
            remap[order[i]] = i;

            if (nodes[order[i]].Operation < 0)
            {
                order.Add(nodes[order[i]].Left);
                order.Add(nodes[order[i]].Right);
            }
        }

        for (var i = 0; i < order.Count; i++)
        {
            var node = nodes[order[i]];
            tree[i] = node.Operation < 0 ? node with { Left = remap[node.Left], Right = remap[node.Right] } : node;
        }

        return tree;
    }
}
