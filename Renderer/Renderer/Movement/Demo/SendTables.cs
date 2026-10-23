using System.Globalization;
using System.IO;

namespace ValveResourceFormat.Renderer.Movement.Demo;

/// <summary>How a networked field is laid out, which decides how field paths step into it.</summary>
internal enum SendFieldKind
{
    /// <summary>A single value.</summary>
    Value,

    /// <summary>A fixed number of elements; the next path index picks one.</summary>
    Array,

    /// <summary>A growable list; the field itself is its length and the next path index picks an element.</summary>
    Vector,

    /// <summary>An optional nested serializer; the field itself says whether it is set.</summary>
    Pointer,

    /// <summary>A nested serializer; the next path index picks its field.</summary>
    Table,
}

/// <summary>A field of a networked class, as the server's flattened serializers describe it.</summary>
internal sealed class SendField
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public SendFieldKind Kind { get; init; }

    /// <summary>Gets how a <see cref="SendFieldKind.Value"/> or a <see cref="SendFieldKind.Pointer"/> is read.</summary>
    public FieldDecoder? Decoder { get; init; }

    /// <summary>Gets the element of an array or a vector.</summary>
    public SendField? Element { get; init; }

    /// <summary>Gets the fields a pointer or a table holds.</summary>
    public Serializer? Serializer { get; init; }

    /// <summary>Gets the element count of an array.</summary>
    public int Length { get; init; }

    /// <summary>Gets the field one path index further in.</summary>
    public SendField Child(int index) => Kind switch
    {
        SendFieldKind.Array or SendFieldKind.Vector => Element!,
        SendFieldKind.Pointer or SendFieldKind.Table when (uint)index < (uint)Serializer!.Fields.Length => Serializer.Fields[index],
        _ => throw new InvalidDataException($"Field path steps into {Name} ({Type}), which has no field {index}."),
    };

    /// <summary>Gets how a path ending at this field reads its value.</summary>
    public FieldDecoder PathDecoder => Kind switch
    {
        SendFieldKind.Value or SendFieldKind.Pointer => Decoder!,
        SendFieldKind.Vector => FieldDecoder.Unsigned32,
        _ => throw new InvalidDataException($"Field path ends at {Name} ({Type}), which holds no value."),
    };
}

/// <summary>The networked fields of a class, in field path order.</summary>
internal sealed class Serializer(string name, SendField[] fields)
{
    public string Name { get; } = name;
    public SendField[] Fields { get; } = fields;

    /// <summary>
    /// Finds a field by its dotted name, stepping through pointers and tables. A flattened field's name
    /// has its member in front, as in <c>m_vecVelocity.m_vecX</c>.
    /// </summary>
    /// <returns>The path to the field, or null when there is no such field.</returns>
    public FieldPath? FindPath(string dottedName)
    {
        var path = FieldPath.Empty;
        var serializer = this;
        var remaining = dottedName.AsSpan();

        while (serializer != null)
        {
            var index = -1;

            for (var i = 0; i < serializer.Fields.Length; i++)
            {
                var name = serializer.Fields[i].Name;

                if (remaining.StartsWith(name, StringComparison.Ordinal) && (remaining.Length == name.Length || remaining[name.Length] == '.'))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                return null;
            }

            path = path.Push(index);

            var matched = serializer.Fields[index].Name.Length;

            if (matched == remaining.Length)
            {
                return path;
            }

            remaining = remaining[(matched + 1)..];
            serializer = serializer.Fields[index].Serializer;
        }

        return null;
    }
}

/// <summary>Builds the serializers from the flattened serializer message a demo starts with.</summary>
internal static class SendTables
{
    private static readonly HashSet<string> PointerTypes =
        ["CBodyComponent", "CLightComponent", "CPhysicsComponent", "CRenderComponent", "CPlayerLocalData"];

    private static readonly Dictionary<string, FieldDecoder> BaseTypeDecoders = new()
    {
        ["bool"] = FieldDecoder.Bool,
        ["char"] = FieldDecoder.String,
        ["int8"] = FieldDecoder.Signed32,
        ["int16"] = FieldDecoder.Signed32,
        ["int32"] = FieldDecoder.Signed32,
        ["int64"] = FieldDecoder.Signed64,
        ["GameTick_t"] = FieldDecoder.Signed32,
        ["uint8"] = FieldDecoder.Unsigned32,
        ["uint16"] = FieldDecoder.Unsigned32,
        ["uint32"] = FieldDecoder.Unsigned32,
        ["color32"] = FieldDecoder.Unsigned32,
        ["Color"] = FieldDecoder.Unsigned32,
        ["CGameSceneNodeHandle"] = FieldDecoder.Unsigned32,
        ["CUtlStringToken"] = FieldDecoder.Unsigned32,
        ["GameTime_t"] = FieldDecoder.NoScale,
        ["Quaternion"] = FieldDecoder.NoScale,
        ["CTransform"] = FieldDecoder.NoScale,
        ["CBodyComponent"] = FieldDecoder.Bool,
        ["CPhysicsComponent"] = FieldDecoder.Bool,
        ["CRenderComponent"] = FieldDecoder.Bool,
        ["CUtlString"] = FieldDecoder.String,
        ["CUtlSymbolLarge"] = FieldDecoder.String,
        ["CGlobalSymbol"] = FieldDecoder.String,
        ["CUtlBinaryBlock"] = FieldDecoder.BinaryBlock,
        ["CStrongHandle"] = FieldDecoder.Unsigned64,
    };

    /// <summary>Reads the serializers from a <c>CDemoSendTables</c> payload.</summary>
    /// <returns>The serializers by name; for a name sent in several versions, the last one.</returns>
    public static Dictionary<string, Serializer> Read(ReadOnlySpan<byte> sendTables)
    {
        var data = ReadOnlySpan<byte>.Empty;
        var outer = new ProtoReader(sendTables);

        while (outer.Next())
        {
            if (outer.Field == 1)
            {
                data = outer.ReadBytes();
            }
            else
            {
                outer.Skip();
            }
        }

        // CSVCMsg_FlattenedSerializer, after its length
        var position = 0;
        var length = checked((int)ProtoReader.ReadVarint(data, ref position));
        var message = new ProtoReader(data.Slice(position, length));

        var symbols = new List<string>();
        var fieldMessages = new List<byte[]>();
        var serializerMessages = new List<byte[]>();

        while (message.Next())
        {
            switch (message.Field)
            {
                case 1:
                    serializerMessages.Add(message.ReadBytes().ToArray());
                    break;
                case 2:
                    symbols.Add(message.ReadString());
                    break;
                case 3:
                    fieldMessages.Add(message.ReadBytes().ToArray());
                    break;
                default:
                    message.Skip();
                    break;
            }
        }

        var serializers = new Dictionary<string, Serializer>();

        // A field is shared by every serializer listing it, and built with the serializers known when first listed
        var fields = new SendField?[fieldMessages.Count];

        foreach (var serializerMessage in serializerMessages)
        {
            var name = string.Empty;
            var fieldIndices = new List<int>();
            var reader = new ProtoReader(serializerMessage);

            while (reader.Next())
            {
                switch (reader.Field)
                {
                    case 1:
                        name = symbols[reader.ReadInt32()];
                        break;
                    case 3 when reader.WireType == WireType.LengthDelimited:
                    {
                        var packed = reader.ReadBytes();
                        var packedPosition = 0;

                        while (packedPosition < packed.Length)
                        {
                            fieldIndices.Add((int)ProtoReader.ReadVarint(packed, ref packedPosition));
                        }

                        break;
                    }

                    case 3:
                        fieldIndices.Add(reader.ReadInt32());
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            var serializerFields = new SendField[fieldIndices.Count];

            for (var i = 0; i < serializerFields.Length; i++)
            {
                var index = fieldIndices[i];
                serializerFields[i] = fields[index] ??= CreateField(FieldInfo.Read(fieldMessages[index], symbols), serializers);
            }

            serializers[name] = new Serializer(name, serializerFields);
        }

        return serializers;
    }

    private static SendField CreateField(FieldInfo info, Dictionary<string, Serializer> serializers)
    {
        var type = FieldType.Parse(info.Type);
        var isPointer = type.IsPointer || PointerTypes.Contains(type.BaseType);
        var serializer = info.SerializerName != null
            ? serializers.GetValueOrDefault(info.SerializerName) ?? throw new InvalidDataException($"Serializer {info.SerializerName} is used before it is sent.")
            : null;

        SendField element;

        if (serializer != null)
        {
            element = new SendField
            {
                Name = info.FullName,
                Type = info.Type,
                Kind = isPointer ? SendFieldKind.Pointer : SendFieldKind.Table,
                Serializer = serializer,
                Decoder = serializer.Name == "CCSGameModeRules" ? FieldDecoder.Polymorphic : FieldDecoder.Bool,
            };
        }
        else
        {
            element = new SendField { Name = info.FullName, Type = info.Type, Decoder = ChooseDecoder(info, type) };
        }

        if (isPointer)
        {
            return element;
        }

        // A nested serializer that is not a pointer is sent as a list of it
        if (serializer != null || type.BaseType is "CUtlVector" or "CNetworkUtlVectorBase")
        {
            return new SendField { Name = info.FullName, Type = info.Type, Kind = SendFieldKind.Vector, Element = element };
        }

        if (type.Count is { } count && type.BaseType != "char")
        {
            return new SendField { Name = info.FullName, Type = info.Type, Kind = SendFieldKind.Array, Element = element, Length = count };
        }

        return element;
    }

    private static FieldDecoder ChooseDecoder(FieldInfo info, FieldType type)
    {
        switch (info.Name)
        {
            case "m_PredFloatVariables" or "m_OwnerOnlyPredNetFloatVariables":
                return FieldDecoder.NoScale;
            case "m_PredVectorVariables" or "m_OwnerOnlyPredNetVectorVariables":
                return FieldDecoder.VectorNoScaleOf(3);
            case "m_pGameModeRules":
                return FieldDecoder.Polymorphic;
        }

        if (info.Encoder == "qangle_precise")
        {
            return FieldDecoder.QAnglePrecise;
        }

        if (BaseTypeDecoders.TryGetValue(type.BaseType, out var decoder))
        {
            return decoder;
        }

        var name = type.GenericType?.BaseType ?? type.BaseType;

        return name switch
        {
            "float32" or "CNetworkedQuantizedFloat" => ChooseFloatDecoder(info),
            "Vector" or "VectorWS" => ChooseVectorDecoder(info, 3),
            "Vector2D" => ChooseVectorDecoder(info, 2),
            "Vector4D" => ChooseVectorDecoder(info, 4),
            "QAngle" when info.Name == "m_angEyeAngles" => FieldDecoder.QAngleNoScale,
            "QAngle" when info.BitCount != 0 => new FieldDecoder(FieldEncoding.QAngleBits) { BitCount = info.BitCount },
            "QAngle" => FieldDecoder.QAngleCoord,
            "uint64" or "CEntityHandle" => info.Encoder == "fixed64" ? FieldDecoder.Fixed64 : FieldDecoder.Unsigned64,
            "CHandle" => FieldDecoder.Unsigned32,
            _ => BaseTypeDecoders.GetValueOrDefault(name, FieldDecoder.Unsigned32),
        };
    }

    private static FieldDecoder ChooseFloatDecoder(FieldInfo info)
    {
        if (info.Name is "m_flSimulationTime" or "m_flAnimTime")
        {
            return FieldDecoder.SimulationTime;
        }

        if (info.Encoder == "coord")
        {
            return FieldDecoder.Coord;
        }

        if (info.BitCount is <= 0 or >= 32)
        {
            return FieldDecoder.NoScale;
        }

        return new FieldDecoder(FieldEncoding.Quantized)
        {
            Quantized = new QuantizedFloat(info.BitCount, (QuantizedFloat.EncodeFlags)info.EncodeFlags, info.LowValue, info.HighValue),
        };
    }

    private static FieldDecoder ChooseVectorDecoder(FieldInfo info, int components)
    {
        if (components == 3 && info.Encoder == "normal")
        {
            return FieldDecoder.VectorNormal;
        }

        return ChooseFloatDecoder(info).Encoding switch
        {
            FieldEncoding.NoScale => FieldDecoder.VectorNoScaleOf(components),
            FieldEncoding.Coord => new FieldDecoder(FieldEncoding.VectorCoord) { Components = components },
            _ => FieldDecoder.VectorNormal,
        };
    }

    /// <summary>A field as the flattened serializer message describes it.</summary>
    private sealed record FieldInfo(string Name, string Type, string? SendNode, string? SerializerName, string? Encoder, int BitCount, float LowValue, float HighValue, int EncodeFlags)
    {
        /// <summary>Gets the name with the member it was flattened out of, as in <c>m_vecVelocity.m_vecX</c>.</summary>
        public string FullName => string.IsNullOrEmpty(SendNode) ? Name : $"{SendNode}.{Name}";

        public static FieldInfo Read(ReadOnlySpan<byte> message, List<string> symbols)
        {
            string? name = null;
            string? type = null;
            string? serializerName = null;
            string? encoder = null;
            string? sendNode = null;
            var bitCount = 0;
            var lowValue = 0f;
            var highValue = 1f;
            var encodeFlags = 0;
            var reader = new ProtoReader(message);

            while (reader.Next())
            {
                switch (reader.Field)
                {
                    case 1:
                        type = symbols[reader.ReadInt32()];
                        break;
                    case 2:
                        name = symbols[reader.ReadInt32()];
                        break;
                    case 3:
                        bitCount = reader.ReadInt32();
                        break;
                    case 4:
                        lowValue = reader.ReadFloat();
                        break;
                    case 5:
                        highValue = reader.ReadFloat();
                        break;
                    case 6:
                        encodeFlags = reader.ReadInt32();
                        break;
                    case 7:
                        serializerName = symbols[reader.ReadInt32()];
                        break;
                    case 9:
                        sendNode = symbols[reader.ReadInt32()];
                        break;
                    case 10:
                        encoder = symbols[reader.ReadInt32()];
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            return new FieldInfo(
                name ?? throw new InvalidDataException("Serializer field has no name."),
                type ?? throw new InvalidDataException($"Serializer field {name} has no type."),
                sendNode, serializerName, encoder, bitCount, lowValue, highValue, encodeFlags);
        }
    }

    /// <summary>A field's type name taken apart: <c>Base&lt; Generic &gt;*[Count]</c>.</summary>
    private sealed record FieldType(string BaseType, FieldType? GenericType, bool IsPointer, int? Count)
    {
        public static FieldType Parse(string name)
        {
            var baseEnd = name.IndexOfAny(['<', '[', '*']);
            var baseType = baseEnd < 0 ? name : name[..baseEnd];
            var rest = baseEnd < 0 ? string.Empty : name[baseEnd..];

            FieldType? generic = null;

            if (rest.StartsWith("< ", StringComparison.Ordinal))
            {
                var close = rest.LastIndexOf(" >", StringComparison.Ordinal);
                generic = Parse(rest[2..close]);
                rest = rest[(close + 2)..];
            }

            var isPointer = rest.StartsWith('*');
            int? count = null;

            if (rest.IndexOf('[', StringComparison.Ordinal) is var open and >= 0)
            {
                var close = open + rest.AsSpan(open).IndexOf(']');
                count = int.TryParse(rest.AsSpan(open + 1, close - open - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            }

            return new FieldType(baseType, generic, isPointer, count);
        }
    }
}
