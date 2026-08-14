using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Silk.NET.Vulkan;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Core;

/// <summary>
/// Whether a resource's memory may share a block with resources of the other kind. Buffers and
/// linearly tiled images are one class, optimally tiled images the other.
/// </summary>
/// <remarks>
/// This is how <c>bufferImageGranularity</c> is satisfied. Rather than pad every allocation up to the
/// granularity and reason about which neighbour pairs are legal, the allocator never places the two
/// classes in the same block, which makes the constraint unreachable. The cost is at most one extra
/// block per memory type; the benefit is that a whole family of "corruption on Intel only" bugs
/// cannot occur.
/// </remarks>
public enum VulkanMemoryClass
{
    /// <summary>Buffers and linearly tiled images.</summary>
    Linear,
    /// <summary>Optimally tiled images.</summary>
    Optimal,
}

/// <summary>
/// A range of device memory owned by <see cref="VulkanMemoryAllocator"/>. Bind it to a buffer or an
/// image with its <see cref="Memory"/> and <see cref="Offset"/>.
/// </summary>
public readonly struct VulkanAllocation : IEquatable<VulkanAllocation>
{
    /// <summary>Gets the device memory object the range lives in. Shared with other allocations.</summary>
    public DeviceMemory Memory { get; }

    /// <summary>Gets the byte offset of the range within <see cref="Memory"/>.</summary>
    public ulong Offset { get; }

    /// <summary>Gets the usable length of the range in bytes.</summary>
    public ulong Size { get; }

    /// <summary>Gets a pointer to the first byte, or zero when the memory is not host visible.
    /// Host visible blocks are mapped once for their whole lifetime, never per allocation.</summary>
    public nint MappedPointer { get; }

    /// <summary>Gets the memory type index the range was allocated from.</summary>
    public int MemoryTypeIndex { get; }

    /// <summary>Gets a value indicating whether the memory is coherent, making
    /// <see cref="VulkanMemoryAllocator.Flush"/> a no-op.</summary>
    public bool IsCoherent { get; }

    internal object? Block { get; }

    /// <summary>Gets a value indicating whether this refers to real memory.</summary>
    public bool IsValid => Memory.Handle != 0;

    /// <summary>Gets a value indicating whether the range can be written or read directly.</summary>
    public bool IsMapped => MappedPointer != 0;

    internal VulkanAllocation(
        DeviceMemory memory,
        ulong offset,
        ulong size,
        nint mappedPointer,
        int memoryTypeIndex,
        bool isCoherent,
        object? block)
    {
        Memory = memory;
        Offset = offset;
        Size = size;
        MappedPointer = mappedPointer;
        MemoryTypeIndex = memoryTypeIndex;
        IsCoherent = isCoherent;
        Block = block;
    }

    /// <summary>Gets the mapped range as a span.</summary>
    /// <returns>The writable bytes.</returns>
    /// <exception cref="InvalidOperationException">The memory is not host visible.</exception>
    public unsafe Span<byte> AsSpan()
    {
        if (MappedPointer == 0)
        {
            throw new InvalidOperationException("This allocation is device local and cannot be mapped.");
        }

        return new Span<byte>((void*)MappedPointer, (int)Math.Min(Size, int.MaxValue));
    }

    /// <inheritdoc/>
    public bool Equals(VulkanAllocation other)
        => Memory.Handle == other.Memory.Handle && Offset == other.Offset && Size == other.Size;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is VulkanAllocation other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Memory.Handle, Offset, Size);

    /// <summary>Compares two allocations.</summary>
    /// <param name="left">The first allocation.</param>
    /// <param name="right">The second allocation.</param>
    /// <returns><see langword="true"/> when they name the same range.</returns>
    public static bool operator ==(VulkanAllocation left, VulkanAllocation right) => left.Equals(right);

    /// <summary>Compares two allocations.</summary>
    /// <param name="left">The first allocation.</param>
    /// <param name="right">The second allocation.</param>
    /// <returns><see langword="true"/> when they name different ranges.</returns>
    public static bool operator !=(VulkanAllocation left, VulkanAllocation right) => !left.Equals(right);
}

/// <summary>What the allocator currently holds. For the memory readout and for leak checks at teardown.</summary>
/// <param name="BlockCount">Number of device memory objects held.</param>
/// <param name="ReservedBytes">Total size of those objects.</param>
/// <param name="UsedBytes">Bytes handed out to live allocations.</param>
/// <param name="AllocationCount">Number of live allocations.</param>
/// <param name="DedicatedCount">How many of those got their own device memory object.</param>
public readonly record struct VulkanMemoryStatistics(
    int BlockCount,
    ulong ReservedBytes,
    ulong UsedBytes,
    int AllocationCount,
    int DedicatedCount)
{
    /// <summary>Gets the fraction of reserved memory that is actually in use, 0 to 1.</summary>
    public double Occupancy => ReservedBytes == 0 ? 0 : (double)UsedBytes / ReservedBytes;
}

/// <summary>
/// Sub-allocates device memory out of a small number of large blocks.
/// </summary>
/// <remarks>
/// <para>
/// This exists instead of a binding to the native VulkanMemoryAllocator library. VMA is a header-only
/// C++ library with no C API, so using it would mean adding a hand-written native shim, a C++
/// toolchain, and per-platform binaries to a repository that today builds no native code at all. Both
/// publish modes were measured to work with such a shim, so the choice was made on build and
/// maintenance cost rather than on capability. See the remarks on <see cref="VulkanUploadRing"/> for
/// the streaming half of the design.
/// </para>
/// <para>
/// The renderer's allocation patterns are few, which is what makes a purpose-built allocator
/// reasonable: long-lived device-local mesh buffers, long-lived images, per-frame host-visible
/// streaming, and occasional readback. Only the first two go through this class; streaming goes
/// through <see cref="VulkanUploadRing"/>.
/// </para>
/// <para>
/// The algorithm is a first-fit free list per block with neighbour coalescing on release. Requests at
/// or above half a block get their own device memory object, so one large mesh cannot strand the tail
/// of a shared block.
/// </para>
/// </remarks>
public sealed unsafe class VulkanMemoryAllocator : IDisposable
{
    private const ulong DeviceBlockSize = 64UL * 1024 * 1024;
    private const ulong HostBlockSize = 8UL * 1024 * 1024;

    private readonly Vk Api;
    private readonly Device Device;
    private readonly VulkanAdapter Adapter;
    private readonly VulkanDebugNames DebugNames;
    private readonly Dictionary<PoolKey, List<Block>> Pools = [];
    private readonly Lock Gate = new();

    private int DedicatedCountValue;
    private int NextBlockId;
    private bool Disposed;

    /// <summary>Gets the alignment a flush or invalidate range must respect on non-coherent memory.</summary>
    public ulong NonCoherentAtomSize { get; }

    /// <summary>Initializes a new instance of the <see cref="VulkanMemoryAllocator"/> class.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="adapter">The physical device the memory types come from.</param>
    /// <param name="debugNames">Used to name each block, so a capture attributes memory to a purpose.</param>
    public VulkanMemoryAllocator(Vk api, Device device, VulkanAdapter adapter, VulkanDebugNames debugNames)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(debugNames);

        Api = api;
        Device = device;
        Adapter = adapter;
        DebugNames = debugNames;
        NonCoherentAtomSize = Math.Max(1, adapter.Properties.Limits.NonCoherentAtomSize);
    }

    /// <summary>Allocates memory for a buffer and binds it.</summary>
    /// <param name="buffer">The buffer to back.</param>
    /// <param name="memory">Where the memory should live.</param>
    /// <param name="name">Debug name, applied to the block when it is created.</param>
    /// <returns>The allocation.</returns>
    public VulkanAllocation AllocateForBuffer(Silk.NET.Vulkan.Buffer buffer, BufferMemory memory, string name)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        var requirements = Api.GetBufferMemoryRequirements(Device, buffer);
        var allocation = Allocate(requirements, memory, VulkanMemoryClass.Linear, name);

        Api.BindBufferMemory(Device, buffer, allocation.Memory, allocation.Offset).Check("vkBindBufferMemory");
        return allocation;
    }

    /// <summary>Allocates memory for an image and binds it.</summary>
    /// <param name="image">The image to back.</param>
    /// <param name="name">Debug name, applied to the block when it is created.</param>
    /// <param name="tiling">The tiling the image was created with, which decides its memory class.</param>
    /// <returns>The allocation.</returns>
    public VulkanAllocation AllocateForImage(Image image, string name, ImageTiling tiling = ImageTiling.Optimal)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        var requirements = Api.GetImageMemoryRequirements(Device, image);
        var memoryClass = tiling == ImageTiling.Linear ? VulkanMemoryClass.Linear : VulkanMemoryClass.Optimal;
        var allocation = Allocate(requirements, BufferMemory.DeviceLocal, memoryClass, name);

        Api.BindImageMemory(Device, image, allocation.Memory, allocation.Offset).Check("vkBindImageMemory");
        return allocation;
    }

    /// <summary>Allocates a range of memory without binding it to anything.</summary>
    /// <param name="requirements">Size, alignment and permitted memory types.</param>
    /// <param name="memory">Where the memory should live.</param>
    /// <param name="memoryClass">Whether the resource is linear or optimally tiled.</param>
    /// <param name="name">Debug name, applied to the block when it is created.</param>
    /// <returns>The allocation.</returns>
    /// <exception cref="VulkanException">No memory type satisfies the request, or the driver refused.</exception>
    public VulkanAllocation Allocate(
        MemoryRequirements requirements,
        BufferMemory memory,
        VulkanMemoryClass memoryClass,
        string name)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        var typeIndex = FindMemoryType(requirements.MemoryTypeBits, memory);
        var properties = Adapter.MemoryProperties.MemoryTypes[typeIndex].PropertyFlags;
        var hostVisible = (properties & MemoryPropertyFlags.HostVisibleBit) != 0;
        var coherent = (properties & MemoryPropertyFlags.HostCoherentBit) != 0;
        var blockSize = memory == BufferMemory.DeviceLocal ? DeviceBlockSize : HostBlockSize;
        var alignment = Math.Max(requirements.Alignment, 1);

        // Non-coherent memory is flushed in whole atoms, so a range that shares an atom with its
        // neighbour would flush the neighbour too. Aligning the allocation prevents that overlap.
        if (hostVisible && !coherent)
        {
            alignment = Lcm(alignment, NonCoherentAtomSize);
        }

        lock (Gate)
        {
            if (requirements.Size * 2 >= blockSize)
            {
                return AllocateDedicated(requirements.Size, typeIndex, hostVisible, coherent, name);
            }

            var key = new PoolKey(typeIndex, memoryClass);

            if (!Pools.TryGetValue(key, out var blocks))
            {
                blocks = [];
                Pools[key] = blocks;
            }

            foreach (var block in blocks)
            {
                if (block.TryAllocate(requirements.Size, alignment, out var offset))
                {
                    return new VulkanAllocation(
                        block.Memory,
                        offset,
                        requirements.Size,
                        block.MappedPointer == 0 ? 0 : block.MappedPointer + (nint)offset,
                        typeIndex,
                        coherent,
                        block);
                }
            }

            var fresh = CreateBlock(blockSize, typeIndex, hostVisible, memoryClass, name);
            blocks.Add(fresh);

            if (!fresh.TryAllocate(requirements.Size, alignment, out var freshOffset))
            {
                throw new VulkanException($"A fresh {blockSize} byte block could not satisfy a {requirements.Size} byte request.");
            }

            return new VulkanAllocation(
                fresh.Memory,
                freshOffset,
                requirements.Size,
                fresh.MappedPointer == 0 ? 0 : fresh.MappedPointer + (nint)freshOffset,
                typeIndex,
                coherent,
                fresh);
        }
    }

    private VulkanAllocation AllocateDedicated(ulong size, int typeIndex, bool hostVisible, bool coherent, string name)
    {
        var memory = AllocateDeviceMemory(size, typeIndex, out var mapped, hostVisible);
        DebugNames.SetName(memory, $"{name} (dedicated)");
        DedicatedCountValue++;

        return new VulkanAllocation(memory, 0, size, mapped, typeIndex, coherent, null);
    }

    private Block CreateBlock(ulong size, int typeIndex, bool hostVisible, VulkanMemoryClass memoryClass, string name)
    {
        var memory = AllocateDeviceMemory(size, typeIndex, out var mapped, hostVisible);
        var id = NextBlockId++;

        DebugNames.SetName(memory, string.Create(
            CultureInfo.InvariantCulture,
            $"VulkanMemoryBlock {id} type {typeIndex} {memoryClass} ({name})"));

        return new Block(id, memory, size, mapped);
    }

    private DeviceMemory AllocateDeviceMemory(ulong size, int typeIndex, out nint mapped, bool hostVisible)
    {
        // No VkMemoryAllocateFlagsInfo here on purpose. Requesting the device-address bit without the
        // bufferDeviceAddress feature enabled is a validation error, and nothing in the renderer uses
        // buffer device addresses; add it alongside the feature if a GPU-driven path ever needs it.
        var info = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = size,
            MemoryTypeIndex = (uint)typeIndex,
        };

        var result = Api.AllocateMemory(Device, &info, null, out var memory);

        if (result != Result.Success)
        {
            throw new VulkanException(
                $"vkAllocateMemory failed for {size} bytes of memory type {typeIndex}",
                result);
        }

        mapped = 0;

        if (hostVisible)
        {
            void* pointer = null;
            Api.MapMemory(Device, memory, 0, Vk.WholeSize, 0, &pointer).Check("vkMapMemory");
            mapped = (nint)pointer;
        }

        return memory;
    }

    /// <summary>Returns an allocation to its block, or frees it when it was dedicated.</summary>
    /// <param name="allocation">The allocation to release.</param>
    /// <remarks>Releasing memory a frame in flight might still read is undefined. Route resource
    /// destruction through the deletion queue rather than calling this directly.</remarks>
    public void Free(in VulkanAllocation allocation)
    {
        if (!allocation.IsValid || Disposed)
        {
            return;
        }

        lock (Gate)
        {
            if (allocation.Block is Block block)
            {
                block.Free(allocation.Offset, allocation.Size);
                return;
            }

            if (allocation.IsMapped)
            {
                Api.UnmapMemory(Device, allocation.Memory);
            }

            Api.FreeMemory(Device, allocation.Memory, null);
            DedicatedCountValue--;
        }
    }

    /// <summary>Makes host writes visible to the device. A no-op on coherent memory.</summary>
    /// <param name="allocation">The allocation that was written.</param>
    /// <param name="offset">Byte offset within the allocation.</param>
    /// <param name="size">Length in bytes, or <see cref="Vk.WholeSize"/> for the rest of it.</param>
    public void Flush(in VulkanAllocation allocation, ulong offset = 0, ulong size = Vk.WholeSize)
    {
        if (allocation.IsCoherent || !allocation.IsValid)
        {
            return;
        }

        var range = BuildRange(allocation, offset, size);
        Api.FlushMappedMemoryRanges(Device, 1, &range).Check("vkFlushMappedMemoryRanges");
    }

    /// <summary>Makes device writes visible to the host. A no-op on coherent memory.</summary>
    /// <param name="allocation">The allocation about to be read.</param>
    /// <param name="offset">Byte offset within the allocation.</param>
    /// <param name="size">Length in bytes, or <see cref="Vk.WholeSize"/> for the rest of it.</param>
    public void Invalidate(in VulkanAllocation allocation, ulong offset = 0, ulong size = Vk.WholeSize)
    {
        if (allocation.IsCoherent || !allocation.IsValid)
        {
            return;
        }

        var range = BuildRange(allocation, offset, size);
        Api.InvalidateMappedMemoryRanges(Device, 1, &range).Check("vkInvalidateMappedMemoryRanges");
    }

    private MappedMemoryRange BuildRange(in VulkanAllocation allocation, ulong offset, ulong size)
    {
        var length = size == Vk.WholeSize ? allocation.Size - offset : size;
        var start = allocation.Offset + offset;

        // Round outwards to whole atoms: the specification requires both ends aligned, and rounding
        // inwards would silently drop the edges of the range the caller actually wrote.
        var alignedStart = start / NonCoherentAtomSize * NonCoherentAtomSize;
        var alignedEnd = (start + length + NonCoherentAtomSize - 1) / NonCoherentAtomSize * NonCoherentAtomSize;

        return new MappedMemoryRange
        {
            SType = StructureType.MappedMemoryRange,
            Memory = allocation.Memory,
            Offset = alignedStart,
            Size = alignedEnd - alignedStart,
        };
    }

    private int FindMemoryType(uint typeBits, BufferMemory memory)
    {
        var (required, preferred, banned) = memory switch
        {
            BufferMemory.DeviceLocal => (
                MemoryPropertyFlags.DeviceLocalBit,
                MemoryPropertyFlags.None,
                MemoryPropertyFlags.None),
            BufferMemory.HostUpload => (
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                MemoryPropertyFlags.DeviceLocalBit,
                MemoryPropertyFlags.HostCachedBit),
            BufferMemory.HostReadback => (
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                MemoryPropertyFlags.HostCachedBit,
                MemoryPropertyFlags.None),
            _ => throw new ArgumentOutOfRangeException(nameof(memory), memory, "Unknown memory domain."),
        };

        var best = -1;
        var bestScore = -1;

        for (var i = 0; i < Adapter.MemoryProperties.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << i)) == 0)
            {
                continue;
            }

            var flags = Adapter.MemoryProperties.MemoryTypes[i].PropertyFlags;

            if ((flags & required) != required)
            {
                continue;
            }

            // AMD exposes duplicate memory types that are additionally device-coherent and
            // device-uncached. They exist for debugging cache coherency and are dramatically slower
            // for normal use, so they are never an acceptable automatic choice.
            if ((flags & (MemoryPropertyFlags.DeviceCoherentBitAmd | MemoryPropertyFlags.DeviceUncachedBitAmd)) != 0)
            {
                continue;
            }

            var score = 0;

            if ((flags & preferred) == preferred && preferred != MemoryPropertyFlags.None)
            {
                score += 2;
            }

            if (banned != MemoryPropertyFlags.None && (flags & banned) == 0)
            {
                score += 1;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        if (best < 0)
        {
            throw new VulkanException(
                $"No memory type supports {memory} within the allowed type mask 0x{typeBits:X}.");
        }

        return best;
    }

    private static ulong Lcm(ulong a, ulong b)
    {
        var gcd = Gcd(a, b);
        return gcd == 0 ? 0 : a / gcd * b;
    }

    private static ulong Gcd(ulong a, ulong b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    /// <summary>Gets a snapshot of what is currently held.</summary>
    public VulkanMemoryStatistics Statistics
    {
        get
        {
            lock (Gate)
            {
                var blockCount = 0;
                var reserved = 0UL;
                var used = 0UL;
                var allocations = 0;

                foreach (var blocks in Pools.Values)
                {
                    foreach (var block in blocks)
                    {
                        blockCount++;
                        reserved += block.Size;
                        used += block.UsedBytes;
                        allocations += block.AllocationCount;
                    }
                }

                return new VulkanMemoryStatistics(
                    blockCount,
                    reserved,
                    used,
                    allocations + DedicatedCountValue,
                    DedicatedCountValue);
            }
        }
    }

    /// <summary>Frees every block. Anything still allocated from them becomes invalid, so this must
    /// run after the device is idle.</summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        lock (Gate)
        {
            foreach (var blocks in Pools.Values)
            {
                foreach (var block in blocks)
                {
                    if (block.MappedPointer != 0)
                    {
                        Api.UnmapMemory(Device, block.Memory);
                    }

                    Api.FreeMemory(Device, block.Memory, null);
                }
            }

            Pools.Clear();
        }
    }

    private readonly record struct PoolKey(int MemoryTypeIndex, VulkanMemoryClass MemoryClass);

    private sealed class Block
    {
        private readonly List<FreeRange> FreeList;

        public int Id { get; }
        public DeviceMemory Memory { get; }
        public ulong Size { get; }
        public nint MappedPointer { get; }
        public ulong UsedBytes { get; private set; }
        public int AllocationCount { get; private set; }

        public Block(int id, DeviceMemory memory, ulong size, nint mappedPointer)
        {
            Id = id;
            Memory = memory;
            Size = size;
            MappedPointer = mappedPointer;
            FreeList = [new FreeRange(0, size)];
        }

        public bool TryAllocate(ulong size, ulong alignment, out ulong offset)
        {
            for (var i = 0; i < FreeList.Count; i++)
            {
                var range = FreeList[i];
                var aligned = (range.Offset + alignment - 1) / alignment * alignment;
                var padding = aligned - range.Offset;

                if (range.Size < padding || range.Size - padding < size)
                {
                    continue;
                }

                var tailOffset = aligned + size;
                var tailSize = range.Offset + range.Size - tailOffset;

                FreeList.RemoveAt(i);

                if (tailSize > 0)
                {
                    FreeList.Insert(i, new FreeRange(tailOffset, tailSize));
                }

                if (padding > 0)
                {
                    FreeList.Insert(i, new FreeRange(range.Offset, padding));
                }

                UsedBytes += size;
                AllocationCount++;
                offset = aligned;
                return true;
            }

            offset = 0;
            return false;
        }

        public void Free(ulong offset, ulong size)
        {
            Debug.Assert(size > 0, "Freeing an empty range.");

            var index = FreeList.Count;

            for (var i = 0; i < FreeList.Count; i++)
            {
                if (FreeList[i].Offset > offset)
                {
                    index = i;
                    break;
                }
            }

            FreeList.Insert(index, new FreeRange(offset, size));

            // Coalesce with the next range, then the previous one, so a block that is fully released
            // returns to a single free range and does not fragment over a session.
            if (index + 1 < FreeList.Count && FreeList[index].Offset + FreeList[index].Size == FreeList[index + 1].Offset)
            {
                FreeList[index] = new FreeRange(FreeList[index].Offset, FreeList[index].Size + FreeList[index + 1].Size);
                FreeList.RemoveAt(index + 1);
            }

            if (index > 0 && FreeList[index - 1].Offset + FreeList[index - 1].Size == FreeList[index].Offset)
            {
                FreeList[index - 1] = new FreeRange(
                    FreeList[index - 1].Offset,
                    FreeList[index - 1].Size + FreeList[index].Size);
                FreeList.RemoveAt(index);
            }

            UsedBytes -= size;
            AllocationCount--;
        }

        private readonly record struct FreeRange(ulong Offset, ulong Size);
    }
}
