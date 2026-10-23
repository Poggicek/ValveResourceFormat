using System.IO;

namespace ValveResourceFormat.Renderer.Movement.Demo;

/// <summary>
/// Turns the user commands a demo records back into <see cref="UserCmd"/>s, undoing the delta
/// encoding each player's commands are sent with.
/// </summary>
/// <remarks>
/// A command is either whole, or the changes from the same player's previous command. In the changes
/// a field may be reset to its default with wire type 7, and a repeated field is a list of edits: an
/// optional new length as a wire type 7 key whose field number is the length, then the changed
/// elements, each keyed by its index and itself delta encoded from the element it replaces.
/// </remarks>
/// <seealso href="https://s2v.app/SchemaExplorer/cs2/client/CUserCmd">CUserCmd</seealso>
internal sealed class UserCommandDecoder
{
    private readonly Dictionary<int, ProtoMessage> PreviousBySlot = [];

    /// <summary>Decodes a command and remembers it as the base of the player's next one.</summary>
    public UserCmd Decode(ServerUserCommand command)
    {
        ProtoMessage message;

        if (command.Data != null)
        {
            message = ProtoMessage.Parse(command.Data, ProtoSchema.CsgoUserCmd);
        }
        else
        {
            if (!PreviousBySlot.TryGetValue(command.PlayerSlot, out var previous))
            {
                throw new InvalidDataException($"Command {command.CommandNumber} is a delta from a command that was not recorded.");
            }

            message = previous.Clone();
            message.MergeDelta(command.DeltaData ?? [], ProtoSchema.CsgoUserCmd);
        }

        PreviousBySlot[command.PlayerSlot] = message;
        return ToUserCmd(message, command.CommandNumber);
    }

    private static UserCmd ToUserCmd(ProtoMessage message, int commandNumber)
    {
        var baseCmd = message.GetMessage(1) ?? new ProtoMessage();
        var buttons = baseCmd.GetMessage(3) ?? new ProtoMessage();
        var angles = baseCmd.GetMessage(4) ?? new ProtoMessage();

        var cmd = new UserCmd
        {
            CommandNumber = commandNumber,
            ViewAngles = new Vector3(angles.GetFloat(1), angles.GetFloat(2), angles.GetFloat(3)),
            ForwardMove = baseCmd.GetFloat(5),
            LeftMove = baseCmd.GetFloat(6),
            UpMove = baseCmd.GetFloat(7),
            Impulse = (int)baseCmd.GetVarint(8),
            Buttons = new ButtonState
            {
                Held = buttons.GetVarint(1),
                Changed = buttons.GetVarint(2),
                ChangedTwice = buttons.GetVarint(3),
            },
        };

        foreach (var step in baseCmd.GetList(18))
        {
            cmd.SubtickMoves.Add(new SubtickMoveStep
            {
                Button = (InputButtons)step.GetVarint(1),
                Pressed = step.GetVarint(2) != 0,
                When = step.GetFloat(3),
                AnalogForwardDelta = step.GetFloat(4),
                AnalogLeftDelta = step.GetFloat(5),
                PitchDelta = step.GetFloat(8),
                YawDelta = step.GetFloat(9),
            });
        }

        return cmd;
    }

    /// <summary>Which fields of a message are messages themselves, and which of those repeat.</summary>
    private sealed class ProtoSchema
    {
        public static readonly ProtoSchema CsgoUserCmd = Build();

        public Dictionary<int, ProtoSchema> Messages { get; } = [];
        public Dictionary<int, ProtoSchema> Lists { get; } = [];

        // CSGOUserCmdPB, CBaseUserCmdPB and their nested messages; unlisted fields are scalars or bytes
        private static ProtoSchema Build()
        {
            var qAngle = new ProtoSchema();
            var vector = new ProtoSchema();
            var interpolation = new ProtoSchema();

            var buttons = new ProtoSchema();
            var executionNotes = new ProtoSchema();
            var subtickMove = new ProtoSchema();

            var baseCmd = new ProtoSchema();
            baseCmd.Messages[3] = buttons;
            baseCmd.Messages[4] = qAngle;
            baseCmd.Lists[18] = subtickMove;
            baseCmd.Messages[22] = executionNotes;

            var inputHistory = new ProtoSchema();
            inputHistory.Messages[2] = qAngle;
            inputHistory.Messages[12] = interpolation;
            inputHistory.Messages[13] = interpolation;
            inputHistory.Messages[14] = interpolation;
            inputHistory.Messages[15] = interpolation;
            inputHistory.Messages[66] = vector;
            inputHistory.Messages[67] = vector;
            inputHistory.Messages[68] = vector;
            inputHistory.Messages[69] = qAngle;

            var csgoUserCmd = new ProtoSchema();
            csgoUserCmd.Messages[1] = baseCmd;
            csgoUserCmd.Lists[2] = inputHistory;

            return csgoUserCmd;
        }
    }

    /// <summary>A decoded protobuf message, with scalars kept as their raw wire value.</summary>
    private sealed class ProtoMessage
    {
        // ulong for varints and fixed values, byte[] for other length delimited data,
        // ProtoMessage for messages and List<ProtoMessage> for repeated messages
        private readonly Dictionary<int, object> Fields = [];

        public static ProtoMessage Parse(ReadOnlySpan<byte> data, ProtoSchema schema)
        {
            var message = new ProtoMessage();
            var reader = new ProtoReader(data);

            while (reader.Next())
            {
                if (schema.Lists.TryGetValue(reader.Field, out var elementSchema))
                {
                    message.GetOrAddList(reader.Field).Add(Parse(reader.ReadBytes(), elementSchema));
                }
                else if (schema.Messages.TryGetValue(reader.Field, out var childSchema))
                {
                    message.Fields[reader.Field] = Parse(reader.ReadBytes(), childSchema);
                }
                else
                {
                    message.Fields[reader.Field] = ReadScalar(ref reader);
                }
            }

            return message;
        }

        public void MergeDelta(ReadOnlySpan<byte> delta, ProtoSchema schema)
        {
            var reader = new ProtoReader(delta);

            while (reader.Next())
            {
                if (reader.WireType == WireType.Reset)
                {
                    Fields.Remove(reader.Field);
                }
                else if (schema.Lists.TryGetValue(reader.Field, out var elementSchema))
                {
                    MergeListDelta(GetOrAddList(reader.Field), reader.ReadBytes(), elementSchema);
                }
                else if (schema.Messages.TryGetValue(reader.Field, out var childSchema))
                {
                    var child = GetMessage(reader.Field) ?? new ProtoMessage();
                    child.MergeDelta(reader.ReadBytes(), childSchema);
                    Fields[reader.Field] = child;
                }
                else
                {
                    Fields[reader.Field] = ReadScalar(ref reader);
                }
            }
        }

        private static void MergeListDelta(List<ProtoMessage> list, ReadOnlySpan<byte> delta, ProtoSchema elementSchema)
        {
            var reader = new ProtoReader(delta);

            while (reader.Next())
            {
                // The field number of a reset is the list's new length
                if (reader.WireType == WireType.Reset)
                {
                    if (reader.Field < list.Count)
                    {
                        list.RemoveRange(reader.Field, list.Count - reader.Field);
                    }

                    while (list.Count < reader.Field)
                    {
                        list.Add(new ProtoMessage());
                    }

                    continue;
                }

                if (reader.WireType != WireType.LengthDelimited || reader.Field >= list.Count)
                {
                    throw new InvalidDataException($"Repeated field edit {reader.Field} ({reader.WireType}) is outside the list of {list.Count}.");
                }

                list[reader.Field].MergeDelta(reader.ReadBytes(), elementSchema);
            }
        }

        private static object ReadScalar(ref ProtoReader reader) => reader.WireType switch
        {
            WireType.Varint => reader.ReadVarint(),
            WireType.Fixed32 => reader.ReadFixed32(),
            WireType.Fixed64 => (ulong)reader.ReadFixed32() | ((ulong)reader.ReadFixed32() << 32),
            WireType.LengthDelimited => reader.ReadBytes().ToArray(),
            _ => throw new InvalidDataException($"Unexpected wire type {reader.WireType}."),
        };

        public ProtoMessage Clone()
        {
            var clone = new ProtoMessage();

            foreach (var (field, value) in Fields)
            {
                clone.Fields[field] = value switch
                {
                    ProtoMessage message => message.Clone(),
                    List<ProtoMessage> list => list.ConvertAll(static element => element.Clone()),
                    _ => value,
                };
            }

            return clone;
        }

        public ProtoMessage? GetMessage(int field) => Fields.GetValueOrDefault(field) as ProtoMessage;

        public IReadOnlyList<ProtoMessage> GetList(int field) => Fields.GetValueOrDefault(field) as List<ProtoMessage> ?? [];

        public ulong GetVarint(int field) => Fields.GetValueOrDefault(field) is ulong value ? value : 0;

        public float GetFloat(int field) => Fields.GetValueOrDefault(field) is uint bits ? BitConverter.UInt32BitsToSingle(bits) : 0f;

        private List<ProtoMessage> GetOrAddList(int field)
        {
            if (Fields.GetValueOrDefault(field) is not List<ProtoMessage> list)
            {
                list = [];
                Fields[field] = list;
            }

            return list;
        }
    }
}
