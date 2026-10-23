using System.Globalization;
using System.IO;
using System.Linq;

namespace ValveResourceFormat.Renderer.Movement.Demo;

/// <summary>A user command as the server reported running it, still protobuf encoded.</summary>
/// <param name="PlayerSlot">The slot of the player who sent it.</param>
/// <param name="CommandNumber">The command's sequence number.</param>
/// <param name="ServerTickExecuted">The server tick it ran in.</param>
/// <param name="ClientTick">The client tick it was made for.</param>
/// <param name="Data">The whole command, or null when it is sent as <paramref name="DeltaData"/>.</param>
/// <param name="DeltaData">The changes from the player's previous command.</param>
internal sealed record ServerUserCommand(int PlayerSlot, int CommandNumber, int ServerTickExecuted, int ClientTick, byte[]? Data, byte[]? DeltaData);

/// <summary>
/// Reads a Source 2 demo (<c>PBDEMS2</c>) from start to end, keeping the networked entities up to date
/// and passing on what the movement replay needs.
/// </summary>
/// <remarks>
/// Only what keeps the entities right is read: the serializers, classes, instance baselines and entity
/// updates. Everything else in the packets is skipped.
/// </remarks>
internal sealed class DemoParser
{
    private enum DemoCommand
    {
        Stop = 0,
        FileHeader = 1,
        SendTables = 4,
        ClassInfo = 5,
        Packet = 7,
        SignonPacket = 8,
        IsCompressed = 64,
    }

    private enum PacketMessage
    {
        CreateStringTable = 44,
        UpdateStringTable = 45,
        ClearAllStringTables = 51,
        PacketEntities = 55,
        UserCommands = 76,
    }

    private const string InstanceBaselineTable = "instancebaseline";

    private static readonly byte[] Magic = "PBDEMS2\0"u8.ToArray();

    private Dictionary<string, Serializer> Serializers = [];
    private ServerClass?[] Classes = [];
    private int ClassIdBits;
    private readonly List<StringTable> StringTables = [];
    private readonly Dictionary<int, Dictionary<FieldPath, object>> DecodedBaselines = [];
    private readonly List<FieldPath> PathBuffer = [];

    public string? MapName { get; private set; }
    public string? ServerName { get; private set; }

    /// <summary>Gets the entities by index.</summary>
    public Dictionary<int, DemoEntity> Entities { get; } = [];

    /// <summary>Gets the server tick of the last entity update.</summary>
    public int ServerTick { get; private set; }

    /// <summary>Called after each entity update with its server tick.</summary>
    public Action<int>? EntitiesUpdated { get; init; }

    /// <summary>Called for each user command the server reports running.</summary>
    public Action<ServerUserCommand>? UserCommand { get; init; }

    public void Parse(Stream stream)
    {
        Span<byte> header = stackalloc byte[16];
        stream.ReadExactly(header);

        if (!header[..8].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a Source 2 demo: it does not start with PBDEMS2.");
        }

        while (ReadFrame(stream) is var (command, payload) && command != DemoCommand.Stop)
        {
            switch (command)
            {
                case DemoCommand.FileHeader:
                    ReadFileHeader(payload);
                    break;
                case DemoCommand.SendTables:
                    Serializers = SendTables.Read(payload);
                    break;
                case DemoCommand.ClassInfo:
                    ReadClassInfo(payload);
                    break;
                case DemoCommand.Packet or DemoCommand.SignonPacket:
                    ReadPacket(payload);
                    break;
            }
        }
    }

    private static (DemoCommand Command, byte[] Payload)? ReadFrame(Stream stream)
    {
        if (!TryReadVarint(stream, out var command))
        {
            return null;
        }

        TryReadVarint(stream, out _);
        TryReadVarint(stream, out var size);

        var payload = new byte[size];
        stream.ReadExactly(payload);

        if ((command & (uint)DemoCommand.IsCompressed) != 0)
        {
            payload = Snappy.Decompress(payload);
        }

        return ((DemoCommand)(command & ~(uint)DemoCommand.IsCompressed), payload);
    }

    private static bool TryReadVarint(Stream stream, out uint value)
    {
        value = 0;

        for (var shift = 0; shift < 35; shift += 7)
        {
            var b = stream.ReadByte();

            if (b < 0)
            {
                return false;
            }

            value |= (uint)(b & 0x7F) << shift;

            if (b < 0x80)
            {
                return true;
            }
        }

        throw new InvalidDataException("Frame varint is longer than five bytes.");
    }

    private void ReadFileHeader(ReadOnlySpan<byte> payload)
    {
        var reader = new ProtoReader(payload);

        while (reader.Next())
        {
            switch (reader.Field)
            {
                case 3:
                    ServerName = reader.ReadString();
                    break;
                case 5:
                    MapName = reader.ReadString();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
    }

    private void ReadClassInfo(ReadOnlySpan<byte> payload)
    {
        var classes = new List<(int Id, string Name)>();
        var reader = new ProtoReader(payload);

        while (reader.Next())
        {
            if (reader.Field != 1)
            {
                reader.Skip();
                continue;
            }

            var id = 0;
            var name = string.Empty;
            var classReader = new ProtoReader(reader.ReadBytes());

            while (classReader.Next())
            {
                switch (classReader.Field)
                {
                    case 1:
                        id = classReader.ReadInt32();
                        break;
                    case 2:
                        name = classReader.ReadString();
                        break;
                    default:
                        classReader.Skip();
                        break;
                }
            }

            classes.Add((id, name));
        }

        Classes = new ServerClass?[classes.Count == 0 ? 0 : classes.Max(static c => c.Id) + 1];

        foreach (var (id, name) in classes)
        {
            if (Serializers.TryGetValue(name, out var serializer))
            {
                Classes[id] = new ServerClass(id, name, serializer);
            }
        }

        ClassIdBits = (int)MathF.Ceiling(MathF.Log2(classes.Count + 1));
    }

    private void ReadPacket(ReadOnlySpan<byte> payload)
    {
        var data = Array.Empty<byte>();
        var reader = new ProtoReader(payload);

        while (reader.Next())
        {
            if (reader.Field == 3)
            {
                data = reader.ReadBytes().ToArray();
            }
            else
            {
                reader.Skip();
            }
        }

        var messages = new BitReader(data);

        while (messages.BitsLeft > 8)
        {
            var type = (PacketMessage)messages.ReadUBitVar();
            var size = checked((int)messages.ReadVarUInt32());
            var message = messages.ReadBytes(size);

            switch (type)
            {
                case PacketMessage.CreateStringTable:
                    CreateStringTable(message);
                    break;
                case PacketMessage.UpdateStringTable:
                    UpdateStringTable(message);
                    break;
                case PacketMessage.ClearAllStringTables:
                    StringTables.Clear();
                    DecodedBaselines.Clear();
                    break;
                case PacketMessage.PacketEntities:
                    ReadPacketEntities(message);
                    break;
                case PacketMessage.UserCommands:
                    ReadUserCommands(message);
                    break;
            }
        }
    }

    private void ReadUserCommands(ReadOnlySpan<byte> message)
    {
        if (UserCommand == null)
        {
            return;
        }

        var reader = new ProtoReader(message);

        while (reader.Next())
        {
            if (reader.Field != 1)
            {
                reader.Skip();
                continue;
            }

            byte[]? data = null;
            byte[]? deltaData = null;
            var commandNumber = 0;
            var playerSlot = -1;
            var serverTick = 0;
            var clientTick = 0;
            var command = new ProtoReader(reader.ReadBytes());

            while (command.Next())
            {
                switch (command.Field)
                {
                    case 1:
                        data = command.ReadBytes().ToArray();
                        break;
                    case 2:
                        commandNumber = command.ReadInt32();
                        break;
                    case 3:
                        playerSlot = command.ReadInt32();
                        break;
                    case 4:
                        serverTick = command.ReadInt32();
                        break;
                    case 5:
                        clientTick = command.ReadInt32();
                        break;
                    case 6:
                        deltaData = command.ReadBytes().ToArray();
                        break;
                    default:
                        command.Skip();
                        break;
                }
            }

            UserCommand(new ServerUserCommand(playerSlot, commandNumber, serverTick, clientTick, data, deltaData));
        }
    }

    private void ReadPacketEntities(ReadOnlySpan<byte> message)
    {
        var updatedEntries = 0;
        var entityData = Array.Empty<byte>();
        var hasPvsVisBits = false;
        var serverTick = ServerTick;
        var reader = new ProtoReader(message);

        while (reader.Next())
        {
            switch (reader.Field)
            {
                case 2:
                    updatedEntries = reader.ReadInt32();
                    break;
                case 7:
                    entityData = reader.ReadBytes().ToArray();
                    break;
                case 12:
                    serverTick = (int)reader.ReadVarint();
                    break;
                case 16:
                    hasPvsVisBits = reader.ReadVarint() != 0;
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        var bits = new BitReader(entityData);
        var index = -1;

        for (var i = 0; i < updatedEntries; i++)
        {
            index += (int)bits.ReadUBitVar() + 1;

            // Bit 0 is leaving, bit 1 entering or, with bit 0, being deleted
            switch (bits.ReadBits(2))
            {
                case 0b00:
                    if (hasPvsVisBits && (bits.ReadBits(2) & 1) != 0)
                    {
                        continue;
                    }

                    ReadEntityUpdate(bits, Entities.GetValueOrDefault(index)
                        ?? throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Update of entity {index}, which does not exist.")));
                    break;

                case 0b10:
                    ReadEntityUpdate(bits, CreateEntity(bits, index));
                    break;

                case 0b11:
                    Entities.Remove(index);
                    break;

                // Left the view, which a demo recording everything does not do for good
                default:
                    break;
            }
        }

        ServerTick = serverTick;
        EntitiesUpdated?.Invoke(serverTick);
    }

    private DemoEntity CreateEntity(BitReader bits, int index)
    {
        var classId = (int)bits.ReadBits(ClassIdBits);
        var serial = (int)bits.ReadBits(17);
        bits.ReadVarUInt32();

        var serverClass = (uint)classId < (uint)Classes.Length ? Classes[classId] : null;

        if (serverClass == null)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Entity {index} is created with unknown class {classId}."));
        }

        var entity = new DemoEntity(index, serial, serverClass);

        foreach (var (path, value) in GetBaseline(serverClass))
        {
            entity.Values[path] = value;
        }

        Entities[index] = entity;
        return entity;
    }

    private Dictionary<FieldPath, object> GetBaseline(ServerClass serverClass)
    {
        if (DecodedBaselines.TryGetValue(serverClass.Id, out var baseline))
        {
            return baseline;
        }

        baseline = [];

        var key = serverClass.Id.ToString(CultureInfo.InvariantCulture);
        var table = StringTables.Find(static table => table.Name == InstanceBaselineTable);

        if (table != null && table.Entries.Values.FirstOrDefault(entry => entry.Key == key).Value is { } data)
        {
            ReadFields(new BitReader(data), serverClass, baseline);
        }

        DecodedBaselines[serverClass.Id] = baseline;
        return baseline;
    }

    private void ReadEntityUpdate(BitReader bits, DemoEntity entity) => ReadFields(bits, entity.Class, entity.Values);

    private void ReadFields(BitReader bits, ServerClass serverClass, Dictionary<FieldPath, object> values)
    {
        FieldPath.ReadUpdate(bits, PathBuffer);

        foreach (var path in PathBuffer)
        {
            values[path] = serverClass.Resolve(path).PathDecoder.Decode(bits);
        }
    }

    private void CreateStringTable(ReadOnlySpan<byte> message)
    {
        var table = new StringTable();
        var entryCount = 0;
        var compressed = false;
        var data = ReadOnlySpan<byte>.Empty;
        var reader = new ProtoReader(message);

        while (reader.Next())
        {
            switch (reader.Field)
            {
                case 1:
                    table.Name = reader.ReadString();
                    break;
                case 2:
                    entryCount = reader.ReadInt32();
                    break;
                case 3:
                    table.UserDataFixedSize = reader.ReadBool();
                    break;
                case 5:
                    table.UserDataSizeBits = reader.ReadInt32();
                    break;
                case 6:
                    table.Flags = reader.ReadInt32();
                    break;
                case 7:
                    data = reader.ReadBytes();
                    break;
                case 9:
                    compressed = reader.ReadBool();
                    break;
                case 10:
                    table.VarintBitCounts = reader.ReadBool();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        StringTables.Add(table);
        table.Update(compressed ? Snappy.Decompress(data) : data.ToArray(), entryCount);
        BaselinesChanged(table);
    }

    private void UpdateStringTable(ReadOnlySpan<byte> message)
    {
        var tableId = 0;
        var entryCount = 0;
        var data = Array.Empty<byte>();
        var reader = new ProtoReader(message);

        while (reader.Next())
        {
            switch (reader.Field)
            {
                case 1:
                    tableId = reader.ReadInt32();
                    break;
                case 2:
                    entryCount = reader.ReadInt32();
                    break;
                case 3:
                    data = reader.ReadBytes().ToArray();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (tableId < StringTables.Count)
        {
            StringTables[tableId].Update(data, entryCount);
            BaselinesChanged(StringTables[tableId]);
        }
    }

    private void BaselinesChanged(StringTable table)
    {
        if (table.Name == InstanceBaselineTable)
        {
            DecodedBaselines.Clear();
        }
    }

    private sealed class StringTable
    {
        private const int KeyHistorySize = 32;

        public string Name { get; set; } = string.Empty;
        public bool UserDataFixedSize { get; set; }
        public int UserDataSizeBits { get; set; }
        public int Flags { get; set; }
        public bool VarintBitCounts { get; set; }
        public Dictionary<int, KeyValuePair<string, byte[]?>> Entries { get; } = [];

        public void Update(byte[] data, int entryCount)
        {
            var bits = new BitReader(data);
            var index = -1;
            var keyHistory = new List<string>();

            for (var i = 0; i < entryCount; i++)
            {
                index = bits.ReadBit() ? index + 1 : index + (int)bits.ReadVarUInt32() + 2;

                var key = Entries.TryGetValue(index, out var existing) ? existing.Key : string.Empty;
                var value = existing.Value;

                if (bits.ReadBit())
                {
                    // A key may start with part of one of the last few
                    if (bits.ReadBit())
                    {
                        var position = (int)bits.ReadBits(5);
                        var length = (int)bits.ReadBits(5);
                        var prefix = position < keyHistory.Count ? keyHistory[position] : string.Empty;

                        key = prefix[..Math.Min(length, prefix.Length)] + bits.ReadString();
                    }
                    else
                    {
                        key = bits.ReadString();
                    }

                    if (keyHistory.Count == KeyHistorySize)
                    {
                        keyHistory.RemoveAt(0);
                    }

                    keyHistory.Add(key);
                }

                if (bits.ReadBit())
                {
                    value = ReadValue(bits);
                }

                Entries[index] = new(key, value);
            }
        }

        private byte[] ReadValue(BitReader bits)
        {
            if (UserDataFixedSize)
            {
                var fixedValue = new byte[(UserDataSizeBits + 7) / 8];

                for (var remaining = UserDataSizeBits; remaining > 0; remaining -= 8)
                {
                    fixedValue[(UserDataSizeBits - remaining) / 8] = (byte)bits.ReadBits(Math.Min(8, remaining));
                }

                return fixedValue;
            }

            var compressed = (Flags & 1) != 0 && bits.ReadBit();
            var size = VarintBitCounts ? (int)bits.ReadUBitVar() : (int)bits.ReadBits(17);
            var value = bits.ReadBytes(size);

            return compressed ? Snappy.Decompress(value) : value;
        }
    }
}
