using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// <see cref="IBuffer"/> on Vulkan: one <c>VkBuffer</c> bound to a range of memory sub-allocated from
/// <see cref="VulkanMemoryAllocator"/>, persistently mapped when the memory is host visible.
/// </summary>
/// <remarks>
/// <para>
/// The memory comes from the <b>persistent</b> allocator, never from <see cref="VulkanUploadRing"/>,
/// and that is a correctness rule rather than a preference. A buffer handed out through
/// <see cref="IDevice.CreateBuffer"/> has an unbounded lifetime: the caller may write it once and draw
/// from it for thousands of frames, which is exactly what <c>RenderCables</c> does when its rope
/// geometry has not changed. Ring memory is reused by a different caller
/// <c>FramesInFlight</c> frames later, so backing a persistent buffer with it would corrupt that path
/// silently. See the remarks on <see cref="VulkanUploadRing"/> for the two other renderer paths with
/// the same shape.
/// </para>
/// <para>
/// <see cref="BufferUsage"/> is honoured exactly as given and never widened. OpenGL decides what a
/// buffer is from the target it is bound to and can afford to ignore the flags; Vulkan bakes them into
/// the object, and quietly adding <c>TRANSFER_DST</c> so that an upload happens to work would hide the
/// fact that the call site never declared it. A missing flag is reported here, by name, instead.
/// </para>
/// </remarks>
public sealed unsafe class VulkanBuffer : IBuffer
{
    private readonly Vk Api;
    private readonly Device Device;
    private readonly VulkanMemoryAllocator Allocator;

    private Silk.NET.Vulkan.Buffer BufferHandle;
    private VulkanAllocation Allocation;
    private bool Disposed;

    /// <summary>Gets the buffer handle, or a null handle once disposed.</summary>
    public Silk.NET.Vulkan.Buffer Handle => BufferHandle;

    /// <summary>Gets the memory range backing the buffer.</summary>
    public VulkanAllocation MemoryRange => Allocation;

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public int SizeInBytes { get; }

    /// <inheritdoc/>
    public BufferUsage Usage { get; }

    /// <inheritdoc/>
    public BufferMemory Memory { get; }

    /// <summary>Gets the Vulkan usage flags the buffer was created with.</summary>
    public BufferUsageFlags UsageFlags { get; }

    /// <summary>Gets a value indicating whether the buffer's memory is host visible and mapped.</summary>
    public bool IsMapped => Allocation.IsMapped;

    /// <inheritdoc/>
    public Span<byte> MappedData => Allocation.IsMapped
        ? new Span<byte>((void*)Allocation.MappedPointer, SizeInBytes)
        : throw new InvalidOperationException($"Buffer '{Name}' is {Memory} and has no mapping. Only {nameof(BufferMemory.HostUpload)} and {nameof(BufferMemory.HostReadback)} buffers can be reached through {nameof(MappedData)}.");

    /// <summary>Creates a buffer and binds memory to it.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="allocator">Where the backing memory comes from.</param>
    /// <param name="debugNames">Used to name the buffer.</param>
    /// <param name="desc">Creation parameters.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="BufferDesc.SizeInBytes"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><see cref="BufferDesc.Usage"/> is <see cref="BufferUsage.None"/>.</exception>
    public VulkanBuffer(
        Vk api,
        Device device,
        VulkanMemoryAllocator allocator,
        VulkanDebugNames debugNames,
        in BufferDesc desc)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(debugNames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.SizeInBytes, nameof(desc));

        if (desc.Usage == BufferUsage.None)
        {
            throw new ArgumentException($"Buffer '{desc.Name}' declares no usage. Vulkan bakes usage into the object, so it must be complete at creation.", nameof(desc));
        }

        Api = api;
        Device = device;
        Allocator = allocator;

        Name = desc.Name ?? string.Empty;
        SizeInBytes = desc.SizeInBytes;
        Usage = desc.Usage;
        Memory = desc.Memory;
        UsageFlags = ToVkUsage(desc.Usage);

        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)desc.SizeInBytes,
            Usage = UsageFlags,
            SharingMode = SharingMode.Exclusive,
        };

        Api.CreateBuffer(Device, &info, null, out BufferHandle).Check("vkCreateBuffer");
        debugNames.SetName(BufferHandle, Name);

        try
        {
            Allocation = Allocator.AllocateForBuffer(BufferHandle, desc.Memory, Name);
        }
        catch
        {
            Api.DestroyBuffer(Device, BufferHandle, null);
            BufferHandle = default;
            throw;
        }
    }

    /// <summary>Translates the contract's usage flags to Vulkan's.</summary>
    /// <param name="usage">The usage to translate.</param>
    /// <returns>The matching <see cref="BufferUsageFlags"/>.</returns>
    public static BufferUsageFlags ToVkUsage(BufferUsage usage)
    {
        var flags = BufferUsageFlags.None;

        if (usage.HasFlag(BufferUsage.Vertex))
        {
            flags |= BufferUsageFlags.VertexBufferBit;
        }

        if (usage.HasFlag(BufferUsage.Index))
        {
            flags |= BufferUsageFlags.IndexBufferBit;
        }

        if (usage.HasFlag(BufferUsage.Uniform))
        {
            flags |= BufferUsageFlags.UniformBufferBit;
        }

        if (usage.HasFlag(BufferUsage.Storage))
        {
            flags |= BufferUsageFlags.StorageBufferBit;
        }

        if (usage.HasFlag(BufferUsage.Indirect))
        {
            flags |= BufferUsageFlags.IndirectBufferBit;
        }

        if (usage.HasFlag(BufferUsage.CopySource))
        {
            flags |= BufferUsageFlags.TransferSrcBit;
        }

        if (usage.HasFlag(BufferUsage.CopyDestination))
        {
            flags |= BufferUsageFlags.TransferDstBit;
        }

        return flags;
    }

    /// <summary>Throws when the buffer was not created with a usage a caller now depends on.</summary>
    /// <param name="required">The usage the operation needs.</param>
    /// <param name="operation">What was being attempted, used in the message.</param>
    /// <exception cref="InvalidOperationException">The usage was not declared at creation.</exception>
    /// <remarks>Checked here rather than left to the validation layer, because the layer reports the
    /// missing flag against an anonymous handle at the copy, while this names the buffer and the
    /// declaration that should have carried it.</remarks>
    public void RequireUsage(BufferUsage required, string operation)
    {
        if (!Usage.HasFlag(required))
        {
            throw new InvalidOperationException($"Buffer '{Name}' was created with usage {Usage}, which does not include {required}. {operation} needs it; add the flag to the {nameof(BufferDesc)}.");
        }
    }

    /// <inheritdoc/>
    /// <remarks>Real work here, unlike the OpenGL backend where the mapping is always coherent. The
    /// allocator picks host coherent memory when it can, in which case this costs a comparison, but a
    /// device that only offers non-coherent host memory needs the flush and skipping it loses the
    /// write.</remarks>
    public void FlushRange(int offsetInBytes, int sizeInBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offsetInBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeInBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offsetInBytes + sizeInBytes, SizeInBytes);

        if (!Allocation.IsMapped)
        {
            throw new InvalidOperationException($"Buffer '{Name}' is {Memory} and has no mapping to flush.");
        }

        Allocator.Flush(Allocation, (ulong)offsetInBytes, (ulong)sizeInBytes);
    }

    /// <summary>Makes device writes visible to the host before a readback is read.</summary>
    /// <param name="offsetInBytes">Start of the range about to be read.</param>
    /// <param name="sizeInBytes">Length of the range about to be read.</param>
    /// <exception cref="InvalidOperationException">The buffer has no mapping.</exception>
    /// <remarks>The counterpart of <see cref="FlushRange"/> for a
    /// <see cref="BufferMemory.HostReadback"/> buffer, which the contract has no member for because
    /// OpenGL's persistent mappings are coherent by construction. A no-op on coherent memory, which is
    /// what the allocator selects for readback whenever the device offers it.</remarks>
    public void InvalidateRange(int offsetInBytes = 0, int sizeInBytes = -1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offsetInBytes);

        var length = sizeInBytes < 0 ? SizeInBytes - offsetInBytes : sizeInBytes;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offsetInBytes + length, SizeInBytes);

        if (!Allocation.IsMapped)
        {
            throw new InvalidOperationException($"Buffer '{Name}' is {Memory} and has no mapping to invalidate.");
        }

        Allocator.Invalidate(Allocation, (ulong)offsetInBytes, (ulong)length);
    }

    /// <summary>Writes bytes straight into the mapping, for a host visible buffer.</summary>
    /// <param name="offsetInBytes">Byte offset to write at.</param>
    /// <param name="data">The bytes to write.</param>
    /// <exception cref="InvalidOperationException">The buffer is device local.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range falls outside the buffer.</exception>
    /// <remarks>The device local path is not here: it needs a staging buffer and a queue submission,
    /// which is <see cref="VulkanUploadContext"/>'s job. <see cref="IDevice.UploadBuffer"/> picks
    /// between the two.</remarks>
    public void WriteMapped(int offsetInBytes, ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offsetInBytes);

        if (data.IsEmpty)
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(offsetInBytes + data.Length, SizeInBytes);

        data.CopyTo(MappedData[offsetInBytes..]);
        FlushRange(offsetInBytes, data.Length);
    }

    /// <summary>Records a buffer memory barrier moving this buffer between two states.</summary>
    /// <param name="commandBuffer">The command buffer to record into.</param>
    /// <param name="from">The state the buffer is being used in now.</param>
    /// <param name="to">The state it is about to be used in.</param>
    /// <param name="offsetInBytes">Start of the range the barrier covers.</param>
    /// <param name="sizeInBytes">Length of the range, or -1 for the rest of the buffer.</param>
    /// <remarks>
    /// A buffer has no layout, so unlike <see cref="VulkanTexture.TransitionTo"/> there is nothing to
    /// track and nothing this class can supply on the caller's behalf: both states have to be named.
    /// That asymmetry is real rather than an oversight &#8212; getting a buffer barrier wrong loses a
    /// write ordering, while getting an image barrier wrong also corrupts the contents, which is why
    /// only the latter is tracked.
    /// </remarks>
    public void RecordBarrier(
        CommandBuffer commandBuffer,
        ResourceState from,
        ResourceState to,
        int offsetInBytes = 0,
        int sizeInBytes = -1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offsetInBytes);

        var length = sizeInBytes < 0 ? SizeInBytes - offsetInBytes : sizeInBytes;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offsetInBytes + length, SizeInBytes);

        var source = VulkanResourceStates.ForBuffer(from);
        var target = VulkanResourceStates.ForBuffer(to);

        var barrier = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = source.Stages,
            SrcAccessMask = source.Access,
            DstStageMask = target.Stages,
            DstAccessMask = target.Access,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = BufferHandle,
            Offset = (ulong)offsetInBytes,
            Size = (ulong)length,
        };

        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1,
            PBufferMemoryBarriers = &barrier,
        };

        Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    /// <summary>Queues this buffer and its memory on a deletion queue.</summary>
    /// <param name="deletionQueue">The queue to enqueue on.</param>
    /// <param name="frameSerial">The serial of the frame during which destruction was requested.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deletionQueue"/> is <see langword="null"/>.</exception>
    /// <remarks>Backs <see cref="IDevice.DeferredDestroy"/>. The handles are released here so that
    /// <see cref="Dispose"/> becomes a no-op and the object cannot destroy them a second time.</remarks>
    public void EnqueueDestroy(VulkanDeletionQueue deletionQueue, ulong frameSerial)
    {
        ArgumentNullException.ThrowIfNull(deletionQueue);

        if (Disposed || BufferHandle.Handle == 0)
        {
            return;
        }

        Disposed = true;
        deletionQueue.Enqueue(frameSerial, BufferHandle, Allocation, Allocator);

        BufferHandle = default;
        Allocation = default;
    }

    /// <summary>Destroys the buffer and frees its memory immediately.</summary>
    /// <remarks>Immediate, so it is only safe once no in-flight frame can reference the buffer. Prefer
    /// <see cref="IDevice.DeferredDestroy"/>, which waits for that to become true.</remarks>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        if (BufferHandle.Handle != 0)
        {
            Api.DestroyBuffer(Device, BufferHandle, null);
            BufferHandle = default;
        }

        Allocator.Free(Allocation);
        Allocation = default;
    }
}
