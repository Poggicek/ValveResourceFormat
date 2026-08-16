using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using ValveResourceFormat.Renderer.Buffers;
using ValveResourceFormat.Renderer.RHI;

namespace ValveResourceFormat.Renderer.Shaders.Spirv;

/// <summary>
/// Reads a SPIR-V module's interface: descriptor bindings, push constant block size and vertex inputs.
/// </summary>
/// <remarks>
/// <para>
/// A hand written parser rather than SPIRV-Reflect or SPIRV-Cross, because everything needed here is
/// in the decoration and type sections and reading them directly costs one linear scan and no second
/// native dependency. That keeps reflection working under PublishAot and PublishSingleFile without
/// anything to deploy, and lets the descriptor set rules from the RHI contract be checked in the same
/// pass.
/// </para>
/// <para>
/// Only the subset of SPIR-V glslang emits from GLSL 460 is handled. Modules from other producers may
/// use constructs this ignores.
/// </para>
/// </remarks>
public static class SpirvReflection
{
    private const uint MagicNumber = 0x07230203;
    private const int HeaderWordCount = 5;

    // Opcodes
    private const int OpName = 5;
    private const int OpEntryPoint = 15;
    private const int OpExecutionMode = 16;
    private const int OpTypeVoid = 19;
    private const int OpTypeBool = 20;
    private const int OpTypeInt = 21;
    private const int OpTypeFloat = 22;
    private const int OpTypeVector = 23;
    private const int OpTypeMatrix = 24;
    private const int OpTypeImage = 25;
    private const int OpTypeSampler = 26;
    private const int OpTypeSampledImage = 27;
    private const int OpTypeArray = 28;
    private const int OpTypeRuntimeArray = 29;
    private const int OpTypeStruct = 30;
    private const int OpTypePointer = 32;
    private const int OpConstant = 43;
    private const int OpVariable = 59;
    private const int OpDecorate = 71;
    private const int OpMemberDecorate = 72;

    // Decorations
    private const int DecorationBlock = 2;
    private const int DecorationBufferBlock = 3;
    private const int DecorationArrayStride = 6;
    private const int DecorationMatrixStride = 7;
    private const int DecorationBuiltIn = 11;
    private const int DecorationLocation = 30;
    private const int DecorationBinding = 33;
    private const int DecorationDescriptorSet = 34;
    private const int DecorationOffset = 35;

    // Storage classes
    private const int StorageClassUniformConstant = 0;
    private const int StorageClassInput = 1;
    private const int StorageClassUniform = 2;
    private const int StorageClassPushConstant = 9;
    private const int StorageClassStorageBuffer = 12;

    // Execution models
    private const int ExecutionModelVertex = 0;
    private const int ExecutionModelFragment = 4;
    private const int ExecutionModelGlCompute = 5;

    private const int ExecutionModeLocalSize = 17;

    // Image dimensionality
    private const int Dim1D = 0;
    private const int Dim2D = 1;
    private const int Dim3D = 2;
    private const int DimCube = 3;
    private const int DimBuffer = 5;
    private const int DimSubpassData = 6;

    /// <summary>Reflects a SPIR-V module.</summary>
    /// <param name="spirv">The module, as produced by <see cref="SpirvCompiler"/>.</param>
    /// <returns>What the module declares.</returns>
    /// <exception cref="InvalidSpirvException">The module is not well formed SPIR-V this parser can read.</exception>
    public static SpirvReflectionResult Reflect(ReadOnlySpan<byte> spirv)
    {
        var words = ToWords(spirv);
        var module = ParseModule(words);

        return new SpirvReflectionResult
        {
            Stage = module.Stage,
            EntryPoint = module.EntryPoint,
            DescriptorBindings = module.BuildDescriptorBindings(),
            PushConstantSizeInBytes = module.ComputePushConstantSize(),
            VertexInputs = module.Stage == ShaderStage.Vertex ? module.BuildVertexInputs() : [],
            WorkgroupSize = module.WorkgroupSize,
        };
    }

    private static uint[] ToWords(ReadOnlySpan<byte> spirv)
    {
        if (spirv.Length < HeaderWordCount * sizeof(uint))
        {
            throw new InvalidSpirvException("The module is shorter than a SPIR-V header.");
        }

        if (spirv.Length % sizeof(uint) != 0)
        {
            throw new InvalidSpirvException("The module length is not a whole number of 32 bit words.");
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(spirv);

        if (magic != MagicNumber)
        {
            throw new InvalidSpirvException(
                magic == BinaryPrimitives.ReverseEndianness(MagicNumber)
                    ? "The module is byte swapped SPIR-V, which this parser does not read."
                    : string.Create(CultureInfo.InvariantCulture, $"Not a SPIR-V module: magic number is 0x{magic:X8}."));
        }

        var words = new uint[spirv.Length / sizeof(uint)];

        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(spirv[(i * sizeof(uint))..]);
        }

        return words;
    }

    private static Module ParseModule(uint[] words)
    {
        var module = new Module();

        var offset = HeaderWordCount;

        while (offset < words.Length)
        {
            var instruction = words[offset];
            var wordCount = (int)(instruction >> 16);
            var opcode = (int)(instruction & 0xFFFF);

            if (wordCount < 1 || offset + wordCount > words.Length)
            {
                throw new InvalidSpirvException(
                    string.Create(CultureInfo.InvariantCulture, $"Malformed instruction at word {offset}."));
            }

            var operands = words.AsSpan(offset + 1, wordCount - 1);

            module.Consume(opcode, operands);

            offset += wordCount;
        }

        return module;
    }

    private sealed class Module
    {
        private readonly Dictionary<uint, string> names = [];
        private readonly Dictionary<(uint Id, int Decoration), uint> decorations = [];
        private readonly Dictionary<(uint Id, int Member, int Decoration), uint> memberDecorations = [];
        private readonly Dictionary<uint, Instruction> types = [];
        private readonly Dictionary<uint, uint> constants = [];
        private readonly List<Variable> variables = [];

        internal ShaderStage Stage { get; private set; }
        internal string EntryPoint { get; private set; } = "main";
        internal (int X, int Y, int Z)? WorkgroupSize { get; private set; }

        private readonly record struct Instruction(int Opcode, uint[] Operands);
        private readonly record struct Variable(uint Id, uint TypeId, int StorageClass);

        internal void Consume(int opcode, ReadOnlySpan<uint> operands)
        {
            switch (opcode)
            {
                case OpName when operands.Length >= 2:
                    names[operands[0]] = DecodeString(operands[1..]);
                    break;

                case OpEntryPoint when operands.Length >= 3:
                    Stage = operands[0] switch
                    {
                        ExecutionModelVertex => ShaderStage.Vertex,
                        ExecutionModelFragment => ShaderStage.Fragment,
                        ExecutionModelGlCompute => ShaderStage.Compute,
                        _ => ShaderStage.None,
                    };
                    EntryPoint = DecodeString(operands[2..]);
                    break;

                case OpExecutionMode when operands.Length >= 5 && operands[1] == ExecutionModeLocalSize:
                    WorkgroupSize = ((int)operands[2], (int)operands[3], (int)operands[4]);
                    break;

                case OpDecorate when operands.Length >= 2:
                    decorations[(operands[0], (int)operands[1])] = operands.Length >= 3 ? operands[2] : 1;
                    break;

                case OpMemberDecorate when operands.Length >= 3:
                    memberDecorations[(operands[0], (int)operands[1], (int)operands[2])] = operands.Length >= 4 ? operands[3] : 1;
                    break;

                case OpConstant when operands.Length >= 3:
                    constants[operands[1]] = operands[2];
                    break;

                case OpVariable when operands.Length >= 3:
                    variables.Add(new Variable(operands[1], operands[0], (int)operands[2]));
                    break;

                case OpTypeVoid:
                case OpTypeBool:
                case OpTypeInt:
                case OpTypeFloat:
                case OpTypeVector:
                case OpTypeMatrix:
                case OpTypeImage:
                case OpTypeSampler:
                case OpTypeSampledImage:
                case OpTypeArray:
                case OpTypeRuntimeArray:
                case OpTypeStruct:
                case OpTypePointer:
                    if (operands.Length >= 1)
                    {
                        types[operands[0]] = new Instruction(opcode, operands.ToArray());
                    }

                    break;

                default:
                    break;
            }
        }

        internal ImmutableArray<SpirvDescriptorBinding> BuildDescriptorBindings()
        {
            var bindings = new List<SpirvDescriptorBinding>();

            foreach (var variable in variables)
            {
                if (variable.StorageClass is not (StorageClassUniformConstant or StorageClassUniform or StorageClassStorageBuffer))
                {
                    continue;
                }

                if (!decorations.TryGetValue((variable.Id, DecorationBinding), out var binding))
                {
                    // No binding decoration means it is not a descriptor: a GL style default block
                    // uniform, or a declaration A7 has not decorated yet.
                    continue;
                }

                var set = decorations.GetValueOrDefault((variable.Id, DecorationDescriptorSet), 0u);

                var pointee = Pointee(variable.TypeId);
                var (elementType, count) = UnwrapArray(pointee);
                var kind = ClassifyKind(variable.StorageClass, elementType);

                var blockSize = kind is SpirvResourceKind.UniformBuffer or SpirvResourceKind.StorageBuffer
                    ? SizeOf(elementType, null, 0)
                    : 0;

                bindings.Add(new SpirvDescriptorBinding(
                    ResolveName(variable.Id, elementType),
                    (int)set,
                    (int)binding,
                    kind,
                    count,
                    blockSize,
                    ImageShape(elementType)));
            }

            return [.. bindings.OrderBy(static b => b.Set).ThenBy(static b => b.Binding)];
        }

        internal int ComputePushConstantSize()
        {
            var size = 0;

            foreach (var variable in variables)
            {
                if (variable.StorageClass != StorageClassPushConstant)
                {
                    continue;
                }

                size = Math.Max(size, SizeOf(Pointee(variable.TypeId), null, 0));
            }

            return size;
        }

        internal ImmutableArray<SpirvVertexInput> BuildVertexInputs()
        {
            var inputs = new List<SpirvVertexInput>();

            foreach (var variable in variables)
            {
                if (variable.StorageClass != StorageClassInput)
                {
                    continue;
                }

                if (decorations.ContainsKey((variable.Id, DecorationBuiltIn)))
                {
                    continue;
                }

                if (!decorations.TryGetValue((variable.Id, DecorationLocation), out var location))
                {
                    continue;
                }

                var type = Pointee(variable.TypeId);
                var (componentType, componentCount, locationCount) = DescribeInterfaceType(type);

                inputs.Add(new SpirvVertexInput(
                    names.GetValueOrDefault(variable.Id, string.Empty),
                    (int)location,
                    componentType,
                    componentCount,
                    locationCount));
            }

            return [.. inputs.OrderBy(static i => i.Location)];
        }

        private string ResolveName(uint variableId, uint typeId)
        {
            // A buffer block's useful name is the block's type name; glslang names the variable itself
            // with the instance name, which is often absent.
            if (types.TryGetValue(typeId, out var type) && type.Opcode == OpTypeStruct
            && names.TryGetValue(typeId, out var blockName) && blockName.Length > 0)
            {
                return blockName;
            }

            return names.GetValueOrDefault(variableId, string.Empty);
        }

        private uint Pointee(uint pointerTypeId)
            => types.TryGetValue(pointerTypeId, out var type) && type.Opcode == OpTypePointer && type.Operands.Length >= 3
                ? type.Operands[2]
                : pointerTypeId;

        private (uint ElementType, int Count) UnwrapArray(uint typeId)
        {
            if (!types.TryGetValue(typeId, out var type))
            {
                return (typeId, 1);
            }

            return type.Opcode switch
            {
                OpTypeArray when type.Operands.Length >= 3 => (type.Operands[1], (int)constants.GetValueOrDefault(type.Operands[2], 1u)),
                OpTypeRuntimeArray when type.Operands.Length >= 2 => (type.Operands[1], 0),
                _ => (typeId, 1),
            };
        }

        private SpirvResourceKind ClassifyKind(int storageClass, uint typeId)
        {
            if (storageClass == StorageClassStorageBuffer)
            {
                return SpirvResourceKind.StorageBuffer;
            }

            if (storageClass == StorageClassUniform)
            {
                return decorations.ContainsKey((typeId, DecorationBufferBlock))
                    ? SpirvResourceKind.StorageBuffer
                    : SpirvResourceKind.UniformBuffer;
            }

            if (!types.TryGetValue(typeId, out var type))
            {
                return SpirvResourceKind.Unknown;
            }

            switch (type.Opcode)
            {
                case OpTypeSampler:
                    return SpirvResourceKind.Sampler;

                case OpTypeSampledImage when type.Operands.Length >= 2:
                    return ImageKind(type.Operands[1], combined: true);

                case OpTypeImage:
                    return ImageKind(type.Operands[0], combined: false);

                case OpTypeStruct:
                    return decorations.ContainsKey((typeId, DecorationBlock))
                        ? SpirvResourceKind.UniformBuffer
                        : SpirvResourceKind.Unknown;

                default:
                    return SpirvResourceKind.Unknown;
            }
        }

        /// <summary>
        /// Reads the shape a sampler or image declares, following the same one or two hops to the
        /// <c>OpTypeImage</c> that <see cref="ClassifyKind"/> takes.
        /// </summary>
        /// <param name="typeId">The descriptor's element type.</param>
        /// <returns>The declared shape, or <see cref="SpirvImageShape.Unknown"/> when the descriptor is
        /// not an image.</returns>
        private SpirvImageShape ImageShape(uint typeId)
        {
            if (!types.TryGetValue(typeId, out var type))
            {
                return SpirvImageShape.Unknown;
            }

            var imageTypeId = type.Opcode switch
            {
                OpTypeSampledImage when type.Operands.Length >= 2 => type.Operands[1],
                OpTypeImage => typeId,
                _ => 0u,
            };

            if (imageTypeId == 0
                || !types.TryGetValue(imageTypeId, out var image)
                || image.Opcode != OpTypeImage
                || image.Operands.Length < 7)
            {
                return SpirvImageShape.Unknown;
            }

            var dim = (int)image.Operands[2];
            var arrayed = image.Operands[4] != 0;

            return dim switch
            {
                Dim1D => SpirvImageShape.Texture1D,
                Dim2D => arrayed ? SpirvImageShape.Texture2DArray : SpirvImageShape.Texture2D,
                Dim3D => SpirvImageShape.Texture3D,
                DimCube => arrayed ? SpirvImageShape.TextureCubeArray : SpirvImageShape.TextureCube,
                _ => SpirvImageShape.Unknown,
            };
        }

        private SpirvResourceKind ImageKind(uint imageTypeId, bool combined)
        {
            if (!types.TryGetValue(imageTypeId, out var image) || image.Opcode != OpTypeImage || image.Operands.Length < 7)
            {
                return SpirvResourceKind.Unknown;
            }

            var dim = (int)image.Operands[2];
            var sampled = (int)image.Operands[6];

            if (dim == DimSubpassData)
            {
                return SpirvResourceKind.InputAttachment;
            }

            if (dim == DimBuffer)
            {
                return sampled == 2 ? SpirvResourceKind.StorageTexelBuffer : SpirvResourceKind.UniformTexelBuffer;
            }

            if (sampled == 2)
            {
                return SpirvResourceKind.StorageImage;
            }

            return combined ? SpirvResourceKind.CombinedImageSampler : SpirvResourceKind.SampledImage;
        }

        private (SpirvComponentType Type, int ComponentCount, int LocationCount) DescribeInterfaceType(uint typeId)
        {
            if (!types.TryGetValue(typeId, out var type))
            {
                return (SpirvComponentType.Unknown, 0, 1);
            }

            switch (type.Opcode)
            {
                case OpTypeMatrix when type.Operands.Length >= 3:
                    {
                        var (componentType, componentCount, _) = DescribeInterfaceType(type.Operands[1]);
                        return (componentType, componentCount, (int)type.Operands[2]);
                    }

                case OpTypeVector when type.Operands.Length >= 3:
                    {
                        var (componentType, _, _) = DescribeInterfaceType(type.Operands[1]);
                        return (componentType, (int)type.Operands[2], 1);
                    }

                case OpTypeFloat when type.Operands.Length >= 2:
                    return (type.Operands[1] == 64 ? SpirvComponentType.Double : SpirvComponentType.Float, 1, 1);

                case OpTypeInt when type.Operands.Length >= 3:
                    return (type.Operands[2] == 0 ? SpirvComponentType.UInt : SpirvComponentType.Int, 1, 1);

                case OpTypeBool:
                    return (SpirvComponentType.Bool, 1, 1);

                default:
                    return (SpirvComponentType.Unknown, 0, 1);
            }
        }

        /// <summary>
        /// Byte size of a type as laid out in a block. <paramref name="declaringStruct"/> and
        /// <paramref name="memberIndex"/> locate the member decorations that carry the strides, which
        /// live on the containing struct rather than on the type itself.
        /// </summary>
        private int SizeOf(uint typeId, uint? declaringStruct, int memberIndex)
        {
            if (!types.TryGetValue(typeId, out var type))
            {
                return 0;
            }

            switch (type.Opcode)
            {
                case OpTypeStruct:
                    {
                        var size = 0;

                        for (var member = 0; member < type.Operands.Length - 1; member++)
                        {
                            var memberType = type.Operands[member + 1];
                            var offset = (int)memberDecorations.GetValueOrDefault((typeId, member, DecorationOffset), 0u);
                            size = Math.Max(size, offset + SizeOf(memberType, typeId, member));
                        }

                        return size;
                    }

                case OpTypeArray when type.Operands.Length >= 3:
                    {
                        var length = (int)constants.GetValueOrDefault(type.Operands[2], 0u);
                        var stride = (int)decorations.GetValueOrDefault((typeId, DecorationArrayStride), 0u);

                        return stride > 0
                            ? length * stride
                            : length * SizeOf(type.Operands[1], declaringStruct, memberIndex);
                    }

                // A runtime array has no size of its own; the block's fixed prefix is all that is known.
                case OpTypeRuntimeArray:
                    return 0;

                case OpTypeMatrix when type.Operands.Length >= 3:
                    {
                        var columns = (int)type.Operands[2];
                        var stride = declaringStruct is { } owner
                            ? (int)memberDecorations.GetValueOrDefault((owner, memberIndex, DecorationMatrixStride), 0u)
                            : 0;

                        return stride > 0
                            ? columns * stride
                            : columns * SizeOf(type.Operands[1], declaringStruct, memberIndex);
                    }

                case OpTypeVector when type.Operands.Length >= 3:
                    return (int)type.Operands[2] * SizeOf(type.Operands[1], declaringStruct, memberIndex);

                case OpTypeFloat when type.Operands.Length >= 2:
                case OpTypeInt when type.Operands.Length >= 2:
                    return (int)type.Operands[1] / 8;

                case OpTypeBool:
                    return 4;

                default:
                    return 0;
            }
        }

        private static string DecodeString(ReadOnlySpan<uint> operands)
        {
            var capacity = operands.Length * 4;
            Span<char> buffer = capacity <= 512 ? stackalloc char[512] : new char[capacity];
            buffer = buffer[..capacity];
            var length = 0;

            foreach (var word in operands)
            {
                for (var shift = 0; shift < 32; shift += 8)
                {
                    var b = (byte)(word >> shift);

                    if (b == 0)
                    {
                        return new string(buffer[..length]);
                    }

                    buffer[length++] = (char)b;
                }
            }

            return new string(buffer[..length]);
        }
    }

    /// <summary>
    /// Checks a module's descriptors against the set scheme in the RHI contract.
    /// </summary>
    /// <param name="reflection">The reflected module.</param>
    /// <returns>One message per violation, empty when the module conforms.</returns>
    /// <remarks>
    /// A mismatch between what a shader decorates and what the pipeline layout declares binds the
    /// wrong resource without erroring, so this is worth running over every shader in CI rather than
    /// waiting for a frame to look wrong.
    /// </remarks>
    public static ImmutableArray<string> ValidateDescriptorSets(SpirvReflectionResult reflection)
    {
        ArgumentNullException.ThrowIfNull(reflection);

        var problems = ImmutableArray.CreateBuilder<string>();

        foreach (var binding in reflection.DescriptorBindings)
        {
            var where = string.Create(CultureInfo.InvariantCulture, $"'{binding.Name}' (set={binding.Set}, binding={binding.Binding})");

            if (binding.Set >= DescriptorSets.Count)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{where} uses set {binding.Set}, but the pipeline layout declares only {DescriptorSets.Count} sets."));
                continue;
            }

            var expected = binding.Kind switch
            {
                SpirvResourceKind.UniformBuffer => DescriptorSets.UniformBuffers,
                SpirvResourceKind.StorageBuffer => DescriptorSets.StorageBuffers,
                _ => -1,
            };

            if (expected >= 0 && binding.Set != expected)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{where} is a {binding.Kind} and belongs in set {expected}."));
                continue;
            }

            // The same check the texture branch below makes, on the other index space. A buffer's binding
            // number exists twice -- as a literal in the shader and as a ReservedBufferSlots value the
            // renderer records at -- and neither side can notice the other moving. A block the table does
            // not know is a block no buffer is ever bound for, which is the silent case worth naming.
            if (expected >= 0)
            {
                var storage = binding.Kind == SpirvResourceKind.StorageBuffer;

                if (!ReservedBufferBlocks.TryGetSlot(binding.Name, storage, out var slot))
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{where} is a {binding.Kind} the renderer has no buffer for, so nothing would ever bind it."));
                }
                else if (binding.Binding != (int)slot)
                {
                    // The number, not the enum member: ReservedBufferSlots overlaps its two index spaces,
                    // so ToString on a storage slot below 8 prints whichever uniform slot shares its value.
                    problems.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{where} is a {binding.Kind} the renderer binds at binding {(int)slot} of set {expected}."));
                }

                continue;
            }

            // A storage image is a third index space, not a texture: binding it addresses image units,
            // which OpenGL keeps separate from texture units and Vulkan does not. Set 4 is where the
            // contract puts them. Set 2 is still accepted because shader emission has not moved yet,
            // and rejecting it would fail every compute pipeline before the emission change lands.
            if (binding.Kind == SpirvResourceKind.StorageImage)
            {
                if (binding.Set is not (DescriptorSets.StorageImages or DescriptorSets.ReservedTextures))
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{where} is a storage image and belongs in set {DescriptorSets.StorageImages}."));
                }

                continue;
            }

            if (expected < 0 && binding.Set is not (DescriptorSets.ReservedTextures or DescriptorSets.MaterialTextures))
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{where} is a {binding.Kind} and belongs in set {DescriptorSets.ReservedTextures} or {DescriptorSets.MaterialTextures}."));
                continue;
            }

            // Which of the two texture sets is decided by the sampler's name, and accepting either was a
            // hole wide enough to drive the whole scheme through: a probe that moved all 128 reserved
            // texture bindings into set 3 drew zero violations from this. A reserved sampler is bound
            // scene-wide at its ReservedTextureSlots number and a material sampler is numbered per
            // shader, so the two are not interchangeable and swapping them binds the wrong texture in
            // silence.
            if (expected < 0)
            {
                var reserved = MaterialLoader.ReservedTextureSlotByName.TryGetValue(binding.Name, out var slot);
                var wanted = reserved ? DescriptorSets.ReservedTextures : DescriptorSets.MaterialTextures;

                if (binding.Set != wanted && reserved)
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{where} is the reserved texture {slot} and belongs in set {DescriptorSets.ReservedTextures} at binding {(int)slot}."));
                }
                else if (binding.Set != wanted)
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{where} is not a reserved texture, so it belongs in set {DescriptorSets.MaterialTextures} at the number its shader assigned it."));
                }
                else if (reserved && binding.Binding != (int)slot)
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{where} is the reserved texture {slot}, which is bound scene-wide at binding {(int)slot}."));
                }
            }

            if (binding.Kind == SpirvResourceKind.UniformBuffer && binding.Binding >= (int)ReservedBufferSlots.Max)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{where} is past the last reserved uniform buffer slot ({(int)ReservedBufferSlots.Max - 1})."));
            }
        }

        return problems.ToImmutable();
    }
}

/// <summary>Thrown when a byte sequence is not SPIR-V this parser can read.</summary>
public class InvalidSpirvException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="InvalidSpirvException"/> class.</summary>
    public InvalidSpirvException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="InvalidSpirvException"/> class with a message.</summary>
    /// <param name="message">A description of what was wrong with the module.</param>
    public InvalidSpirvException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="InvalidSpirvException"/> class with a message and an inner exception.</summary>
    /// <param name="message">A description of what was wrong with the module.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public InvalidSpirvException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
