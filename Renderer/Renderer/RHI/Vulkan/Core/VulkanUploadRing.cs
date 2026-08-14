using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Core;

/// <summary>
/// A range inside the upload ring, valid only for the frame it was taken on.
/// </summary>
/// <remarks>
/// <see cref="FrameSerial"/> exists so that reading a ring allocation on a later frame is a detected
/// error rather than silent corruption. That mistake is the single most likely way to break this
/// renderer while porting it, because several of its draw paths already read buffers on frames they
/// did not write. See <see cref="VulkanUploadRing"/>.
/// </remarks>
public readonly record struct VulkanRingAllocation(
    Silk.NET.Vulkan.Buffer Buffer,
    ulong Offset,
    ulong Size,
    nint MappedPointer,
    ulong FrameSerial)
{
    /// <summary>Gets a value indicating whether this refers to real memory.</summary>
    public bool IsValid => Buffer.Handle != 0;

    /// <summary>Gets the mapped range as a span.</summary>
    /// <returns>The writable bytes.</returns>
    public unsafe Span<byte> AsSpan() => new((void*)MappedPointer, (int)Math.Min(Size, int.MaxValue));
}

/// <summary>
/// The per-frame streaming allocator: one persistently mapped host-visible buffer per frame slot,
/// handed out with a bump pointer and reset when the slot's previous work has retired.
/// </summary>
/// <remarks>
/// <para>
/// <b>Use this only for data written and consumed on the same frame.</b> A ring allocation's memory is
/// reused by a different caller <c>FramesInFlight</c> frames later, so anything read on a frame it was
/// not written on must instead own a persistent allocation from
/// <see cref="VulkanMemoryAllocator"/>. This is not a stylistic preference; three existing renderer
/// paths would corrupt if ported naively onto a ring:
/// </para>
/// <list type="bullet">
/// <item><description><c>RenderCables</c> skips its upload entirely when the rope geometry has not
/// changed, then draws from a buffer last written an arbitrary number of frames ago. A settled rope
/// can go thousands of frames without an upload, by which point the ring slot holds a different
/// cable. It needs a persistent buffer plus a dirty flag, not a ring.</description></item>
/// <item><description><c>SelectedNodeRenderer</c> uploads through <c>LineBuffer</c> during
/// <c>Update()</c> but draws during <c>Render()</c>, and <c>VertexCount</c> persists when
/// <c>Update()</c> is skipped, so the draw re-reads an older frame's buffer.</description></item>
/// <item><description><c>QuadOverdraw</c>'s lock and count storage images are single-buffered and
/// cleared at the top of the next frame while the previous one may still be sampling them. Those need
/// one copy per frame in flight, and its resize path must release the old textures through the
/// deletion queue rather than deleting them immediately.</description></item>
/// </list>
/// <para>
/// <b>Sizing.</b> The ring is deliberately not sized for the renderer's worst-case single allocation.
/// That worst case is <c>RenderCables</c> rebuilding its index buffer at
/// <c>(8192 - 1) * MaxSides * 6</c> indices, which is multiple megabytes, and reserving that per frame
/// slot would cost tens of megabytes permanently to serve a path that is usually idle. Instead the
/// ring is sized for the common case and any request it cannot serve spills to a dedicated buffer that
/// is destroyed when the frame retires. The spill costs one allocation on the frames that need it and
/// nothing on the frames that do not; <see cref="SpillCount"/> reports whether that assumption holds
/// in practice.
/// </para>
/// <para>
/// <b>Known exception.</b> <c>OcclusionDebugRenderer</c>'s buffer carries a 32-byte header whose layout
/// is hardcoded in <c>frustum_cull.comp.slang</c> and <c>occlusion_debug.vert.slang</c>, and both
/// shaders assume it starts at offset zero. Suballocating that buffer out of the ring shifts its base
/// and breaks both shaders silently. It must either stay a persistent allocation or gain a base-offset
/// push constant first; this class cannot make that safe on its own.
/// </para>
/// </remarks>
public sealed unsafe class VulkanUploadRing : IDisposable
{
    private readonly Vk Api;
    private readonly Device Device;
    private readonly VulkanMemoryAllocator Allocator;
    private readonly VulkanDebugNames DebugNames;
    private readonly BufferUsageFlags Usage;
    private readonly Slot[] Slots;
    private readonly ulong MinAlignment;

    private int CurrentSlot;
    private ulong CurrentSerial;
    private bool Disposed;

    /// <summary>Gets the capacity of one frame slot in bytes.</summary>
    public ulong SlotSize { get; }

    /// <summary>Gets how many requests have spilled to a dedicated buffer since creation. A number
    /// that climbs every frame means <see cref="SlotSize"/> is too small for this scene.</summary>
    public int SpillCount { get; private set; }

    /// <summary>Gets the high water mark of bytes used in a single frame, for tuning
    /// <see cref="SlotSize"/>.</summary>
    public ulong PeakUsedBytes { get; private set; }

    /// <summary>Initializes a new instance of the <see cref="VulkanUploadRing"/> class.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="adapter">The physical device, for its offset alignment limits.</param>
    /// <param name="allocator">Where the backing memory comes from.</param>
    /// <param name="debugNames">Used to name the per-slot buffers.</param>
    /// <param name="framesInFlight">How many slots to create.</param>
    /// <param name="slotSize">Capacity of one slot in bytes.</param>
    /// <param name="usage">What the ring's buffers may be bound as.</param>
    public VulkanUploadRing(
        Vk api,
        Device device,
        VulkanAdapter adapter,
        VulkanMemoryAllocator allocator,
        VulkanDebugNames debugNames,
        int framesInFlight,
        ulong slotSize = 4UL * 1024 * 1024,
        BufferUsageFlags usage =
            BufferUsageFlags.UniformBufferBit
            | BufferUsageFlags.StorageBufferBit
            | BufferUsageFlags.VertexBufferBit
            | BufferUsageFlags.IndexBufferBit
            | BufferUsageFlags.IndirectBufferBit
            | BufferUsageFlags.TransferSrcBit)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(debugNames);
        ArgumentOutOfRangeException.ThrowIfLessThan(framesInFlight, 1);

        Api = api;
        Device = device;
        Allocator = allocator;
        DebugNames = debugNames;
        Usage = usage;
        SlotSize = slotSize;

        var limits = adapter.Properties.Limits;
        MinAlignment = Math.Max(
            Math.Max(limits.MinUniformBufferOffsetAlignment, limits.MinStorageBufferOffsetAlignment),
            Math.Max(limits.MinMemoryMapAlignment, 16));

        Slots = new Slot[framesInFlight];

        for (var i = 0; i < framesInFlight; i++)
        {
            Slots[i] = CreateSlot(slotSize, $"VulkanUploadRing slot {i}");
        }
    }

    private Slot CreateSlot(ulong size, string name)
    {
        var buffer = CreateBuffer(size, Usage, name);
        var allocation = Allocator.AllocateForBuffer(buffer, BufferMemory.HostUpload, name);

        return new Slot(buffer, allocation);
    }

    private Silk.NET.Vulkan.Buffer CreateBuffer(ulong size, BufferUsageFlags usage, string name)
    {
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };

        Api.CreateBuffer(Device, &info, null, out var buffer).Check("vkCreateBuffer");
        DebugNames.SetName(buffer, name);
        return buffer;
    }

    /// <summary>Moves to the next frame slot and releases everything it held.</summary>
    /// <param name="frameSlot">Which slot the new frame uses.</param>
    /// <param name="frameSerial">A value that never repeats, used to stamp allocations.</param>
    /// <remarks>The caller must already have waited until the work using that slot has retired. The
    /// frame ring does this before calling here.</remarks>
    public void BeginFrame(int frameSlot, ulong frameSerial)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(frameSlot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frameSlot, Slots.Length);

        CurrentSlot = frameSlot;
        CurrentSerial = frameSerial;

        ref var slot = ref Slots[frameSlot];

        PeakUsedBytes = Math.Max(PeakUsedBytes, slot.Used);
        slot.Used = 0;

        if (slot.Spills is { Count: > 0 })
        {
            foreach (var spill in slot.Spills)
            {
                Api.DestroyBuffer(Device, spill.Buffer, null);
                Allocator.Free(spill.Allocation);
            }

            slot.Spills.Clear();
        }
    }

    /// <summary>Reserves a range in the current frame's slot.</summary>
    /// <param name="size">Bytes required.</param>
    /// <param name="alignment">Required alignment, raised to the device minimum.</param>
    /// <returns>The reserved range.</returns>
    public VulkanRingAllocation Allocate(ulong size, ulong alignment = 0)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        ArgumentOutOfRangeException.ThrowIfZero(size);

        var effective = Math.Max(alignment, MinAlignment);

        ref var slot = ref Slots[CurrentSlot];

        var offset = (slot.Used + effective - 1) / effective * effective;

        if (offset + size <= SlotSize)
        {
            slot.Used = offset + size;

            return new VulkanRingAllocation(
                slot.Buffer,
                offset,
                size,
                slot.Allocation.MappedPointer + (nint)offset,
                CurrentSerial);
        }

        return Spill(ref slot, size);
    }

    private VulkanRingAllocation Spill(ref Slot slot, ulong size)
    {
        SpillCount++;

        var name = $"VulkanUploadRing spill {SpillCount}";
        var buffer = CreateBuffer(size, Usage, name);
        var allocation = Allocator.AllocateForBuffer(buffer, BufferMemory.HostUpload, name);

        slot.Spills ??= [];
        slot.Spills.Add(new Spilled(buffer, allocation));

        return new VulkanRingAllocation(buffer, 0, size, allocation.MappedPointer, CurrentSerial);
    }

    /// <summary>Reserves a range and copies data into it.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="data">The data to stage.</param>
    /// <param name="alignment">Required alignment, raised to the device minimum.</param>
    /// <returns>The range the data was written to.</returns>
    public VulkanRingAllocation Write<T>(ReadOnlySpan<T> data, ulong alignment = 0)
        where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(data);
        var allocation = Allocate((ulong)bytes.Length, alignment);

        bytes.CopyTo(allocation.AsSpan());
        return allocation;
    }

    /// <summary>Reserves a range and copies a single value into it.</summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="value">The value to stage.</param>
    /// <param name="alignment">Required alignment, raised to the device minimum.</param>
    /// <returns>The range the value was written to.</returns>
    public VulkanRingAllocation Write<T>(in T value, ulong alignment = 0)
        where T : unmanaged
    {
        var allocation = Allocate((ulong)Unsafe.SizeOf<T>(), alignment);

        MemoryMarshal.Write(allocation.AsSpan(), value);
        return allocation;
    }

    /// <summary>Throws when an allocation from an earlier frame is about to be used.</summary>
    /// <param name="allocation">The allocation to validate.</param>
    /// <exception cref="InvalidOperationException">The allocation belongs to a previous frame and its
    /// memory has since been handed to someone else.</exception>
    /// <remarks>Call this at the point of use in debug builds. It converts the whole class of
    /// "buffer read on a frame it was not written on" bugs from intermittent visual corruption into a
    /// deterministic exception naming the frame it came from.</remarks>
    public void ValidateCurrentFrame(in VulkanRingAllocation allocation)
    {
        if (allocation.FrameSerial != CurrentSerial)
        {
            throw new InvalidOperationException(
                $"Upload ring allocation was made on frame {allocation.FrameSerial} but used on frame " +
                $"{CurrentSerial}. Its memory has been reused. Data read on a frame it was not written " +
                "on needs a persistent allocation, not the ring.");
        }
    }

    /// <summary>Destroys every slot buffer and any spills still outstanding.</summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        for (var i = 0; i < Slots.Length; i++)
        {
            ref var slot = ref Slots[i];

            if (slot.Spills is not null)
            {
                foreach (var spill in slot.Spills)
                {
                    Api.DestroyBuffer(Device, spill.Buffer, null);
                    Allocator.Free(spill.Allocation);
                }

                slot.Spills.Clear();
            }

            Api.DestroyBuffer(Device, slot.Buffer, null);
            Allocator.Free(slot.Allocation);
        }
    }

    private struct Slot
    {
        public Silk.NET.Vulkan.Buffer Buffer;
        public VulkanAllocation Allocation;
        public ulong Used;
        public List<Spilled>? Spills;

        public Slot(Silk.NET.Vulkan.Buffer buffer, VulkanAllocation allocation)
        {
            Buffer = buffer;
            Allocation = allocation;
            Used = 0;
            Spills = null;
        }
    }

    private readonly record struct Spilled(Silk.NET.Vulkan.Buffer Buffer, VulkanAllocation Allocation);
}
