using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// Batches the staging copies behind <see cref="IDevice.UploadBuffer"/> and
/// <see cref="IDevice.UploadTexture"/> onto one command buffer, submits them together, and waits for
/// them to retire.
/// </summary>
/// <remarks>
/// <para>
/// <b>This does not use <see cref="VulkanUploadRing"/>, deliberately.</b> The ring is for data written
/// and consumed on the same frame; its memory is handed to a different caller
/// <c>FramesInFlight</c> frames later. These uploads are not frame work at all &#8212; they run at load
/// time, before any frame is open, and their staging memory must stay valid until a submission that
/// has nothing to do with the frame timeline has completed. Staging therefore comes from
/// <see cref="VulkanMemoryAllocator"/>, the persistent half of the split, and is freed only after the
/// upload's own fence has signalled. Routing these through the ring would be exactly the mistake the
/// ring's remarks are written to prevent.
/// </para>
/// <para>
/// <b>Why a separate queue submission rather than the frame's command buffer.</b> An upload can be
/// requested at any time, including outside <c>BeginFrame</c>/<c>EndFrame</c>, which is the normal case
/// while a map is loading. Appending to the frame's command buffer would make loading depend on a frame
/// being open, and would tie the lifetime of load-time staging memory to the frame ring's serials for
/// no benefit.
/// </para>
/// <para>
/// <b>Why the flush blocks.</b> <see cref="Flush"/> waits on its fence before returning, so once it
/// returns the data is on the device and the staging memory is reclaimed. That matches what the call
/// sites already assume: <c>glNamedBufferSubData</c> and <c>glTextureSubImage</c> behave as though the
/// upload has happened by the time they return. Cost is contained by batching &#8212; work accumulates
/// until <see cref="PendingBytes"/> crosses <see cref="FlushThresholdBytes"/>, or until something asks
/// for the data, so a model load is a handful of submissions rather than one per buffer.
/// </para>
/// </remarks>
public sealed unsafe class VulkanUploadContext : IDisposable
{
    /// <summary>The number of staged bytes that triggers an automatic flush.</summary>
    public const ulong FlushThresholdBytes = 32UL * 1024 * 1024;

    private readonly Vk Api;
    private readonly Device Device;
    private readonly Queue Queue;
    private readonly VulkanMemoryAllocator Allocator;
    private readonly VulkanDebugNames DebugNames;
    private readonly VulkanCommandPool Pool;
    private readonly List<Staged> Staging = [];

    private CommandBuffer Recording;
    private Fence Fence;
    private bool Open;
    private bool Disposed;

    /// <summary>Gets the number of bytes staged and not yet submitted.</summary>
    public ulong PendingBytes { get; private set; }

    /// <summary>Gets the number of flushes performed since creation.</summary>
    public int FlushCount { get; private set; }

    /// <summary>Creates an upload context.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="queue">The queue uploads are submitted to.</param>
    /// <param name="queueFamilyIndex">The family that queue belongs to.</param>
    /// <param name="allocator">Where staging memory comes from.</param>
    /// <param name="debugNames">Used to name the pool and the staging buffers.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public VulkanUploadContext(
        Vk api,
        Device device,
        Queue queue,
        uint queueFamilyIndex,
        VulkanMemoryAllocator allocator,
        VulkanDebugNames debugNames)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(debugNames);

        Api = api;
        Device = device;
        Queue = queue;
        Allocator = allocator;
        DebugNames = debugNames;

        Pool = new VulkanCommandPool(api, device, queueFamilyIndex, debugNames, "VulkanUploadContext pool");

        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Api.CreateFence(Device, &fenceInfo, null, out Fence).Check("vkCreateFence");
    }

    private CommandBuffer Begin()
    {
        if (!Open)
        {
            Recording = Pool.Acquire("VulkanUploadContext batch");
            Open = true;
        }

        return Recording;
    }

    private Silk.NET.Vulkan.Buffer CreateStaging(ulong size, ReadOnlySpan<byte> data, string name)
    {
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
        };

        Api.CreateBuffer(Device, &info, null, out var buffer).Check("vkCreateBuffer");
        DebugNames.SetName(buffer, name);

        VulkanAllocation allocation;

        try
        {
            allocation = Allocator.AllocateForBuffer(buffer, BufferMemory.HostUpload, name);
        }
        catch
        {
            Api.DestroyBuffer(Device, buffer, null);
            throw;
        }

        data.CopyTo(allocation.AsSpan());
        Allocator.Flush(allocation);

        Staging.Add(new Staged(buffer, allocation));
        PendingBytes += size;

        return buffer;
    }

    /// <summary>Stages a write into a device local buffer.</summary>
    /// <param name="destination">The buffer to write.</param>
    /// <param name="offsetInBytes">Byte offset to write at.</param>
    /// <param name="data">The bytes to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range falls outside the buffer.</exception>
    /// <exception cref="InvalidOperationException">The buffer was not created with
    /// <see cref="BufferUsage.CopyDestination"/>.</exception>
    public void StageBuffer(VulkanBuffer destination, int offsetInBytes, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(offsetInBytes);

        if (data.IsEmpty)
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(offsetInBytes + data.Length, destination.SizeInBytes);
        destination.RequireUsage(BufferUsage.CopyDestination, "Uploading into a device local buffer");

        var command = Begin();
        var staging = CreateStaging((ulong)data.Length, data, $"Upload staging for '{destination.Name}'");

        var copy = new BufferCopy
        {
            SrcOffset = 0,
            DstOffset = (ulong)offsetInBytes,
            Size = (ulong)data.Length,
        };

        Api.CmdCopyBuffer(command, staging, destination.Handle, 1, &copy);

        FlushIfLarge();
    }

    /// <summary>Stages a write of one mip level of one array layer.</summary>
    /// <param name="destination">The texture to write.</param>
    /// <param name="mipLevel">Mip level to write.</param>
    /// <param name="arrayLayer">Array layer or cube face to write.</param>
    /// <param name="data">The texel or block data, tightly packed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The mip level or layer is outside the texture, or
    /// <paramref name="data"/> is not the size the level needs.</exception>
    /// <exception cref="InvalidOperationException">The texture is multisampled, or was not created with
    /// <see cref="TextureUsage.CopyDestination"/>.</exception>
    /// <remarks>
    /// The destination is transitioned into <see cref="ResourceState.CopyDestination"/> before the copy
    /// and left there, with the tracked state updated to match. It is deliberately not moved on to
    /// <see cref="ResourceState.ShaderRead"/> afterwards: guessing what a texture is going to be used
    /// for is the inference-based layout model this backend avoids, and the guess would be wrong for
    /// every storage image and render target. Transition it explicitly at the point of use.
    /// </remarks>
    public void StageTexture(VulkanTexture destination, int mipLevel, int arrayLayer, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(mipLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(mipLevel, destination.MipLevels);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayLayer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(arrayLayer, destination.LayerCount);

        if (destination.SampleCount > 1)
        {
            throw new InvalidOperationException($"Texture '{destination.Name}' is multisampled and cannot be uploaded to.");
        }

        if (data.IsEmpty)
        {
            return;
        }

        destination.RequireUsage(TextureUsage.CopyDestination, "Uploading into a texture");

        // Checked here rather than left to validation: an undersized source makes the copy read past
        // the end of the staging allocation, which is a use of uninitialized memory the layer reports
        // only sometimes and which reads as corrupt texels the rest of the time.
        var expected = destination.MipSizeInBytes(mipLevel);

        if (data.Length != expected)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                data.Length,
                $"Texture '{destination.Name}' mip {mipLevel} needs exactly {expected} bytes of tightly packed {destination.Format} data.");
        }

        var command = Begin();

        destination.TransitionTo(command, ResourceState.CopyDestination);

        var staging = CreateStaging((ulong)data.Length, data, $"Upload staging for '{destination.Name}'");
        var (width, height, depth) = destination.MipExtent(mipLevel);

        var copy = new BufferImageCopy
        {
            BufferOffset = 0,

            // Zero means tightly packed to the image extent, which is the layout every caller here
            // supplies and the only one the size check above validates.
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = destination.Aspect,
                MipLevel = (uint)mipLevel,

                // A volume texture has one array layer and is written whole; every other layered shape
                // addresses one slice at a time.
                BaseArrayLayer = (uint)(destination.Dimension == TextureDimension.Texture3D ? 0 : arrayLayer),
                LayerCount = 1,
            },
            ImageOffset = default,
            ImageExtent = new Extent3D((uint)width, (uint)height, (uint)depth),
        };

        Api.CmdCopyBufferToImage(command, staging, destination.Handle, ImageLayout.TransferDstOptimal, 1, &copy);

        FlushIfLarge();
    }

    /// <summary>Gets a command buffer for work that must run before the next frame, opening a batch if
    /// none is open.</summary>
    /// <returns>The batch's command buffer, in the recording state.</returns>
    /// <remarks>For a caller that needs to record a transition or a copy alongside its uploads, such as
    /// the smoke test's readback. Everything recorded here is submitted and waited on by the next
    /// <see cref="Flush"/>.</remarks>
    public CommandBuffer BeginBatch() => Begin();

    private void FlushIfLarge()
    {
        if (PendingBytes >= FlushThresholdBytes)
        {
            Flush();
        }
    }

    /// <summary>Submits everything staged so far and blocks until it has completed.</summary>
    /// <remarks>Does nothing when no batch is open, so calling it defensively is free.</remarks>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        if (!Open)
        {
            return;
        }

        Api.EndCommandBuffer(Recording).Check("vkEndCommandBuffer");

        var commandInfo = new CommandBufferSubmitInfo
        {
            SType = StructureType.CommandBufferSubmitInfo,
            CommandBuffer = Recording,
        };

        var submit = new SubmitInfo2
        {
            SType = StructureType.SubmitInfo2,
            CommandBufferInfoCount = 1,
            PCommandBufferInfos = &commandInfo,
        };

        var fence = Fence;
        Api.ResetFences(Device, 1, &fence).Check("vkResetFences");
        Api.QueueSubmit2(Queue, 1, &submit, fence).Check("vkQueueSubmit2");
        Api.WaitForFences(Device, 1, &fence, true, ulong.MaxValue).Check("vkWaitForFences");

        Open = false;
        Recording = default;
        FlushCount++;

        ReleaseStaging();

        // Safe now that the fence has signalled: nothing the pool handed out is still executing.
        Pool.Reset();
    }

    private void ReleaseStaging()
    {
        foreach (var staged in Staging)
        {
            Api.DestroyBuffer(Device, staged.Buffer, null);
            Allocator.Free(staged.Allocation);
        }

        Staging.Clear();
        PendingBytes = 0;
    }

    /// <summary>Flushes anything outstanding, then destroys the pool and the fence.</summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        if (Open)
        {
            Flush();
        }

        Disposed = true;

        ReleaseStaging();
        Pool.Dispose();

        if (Fence.Handle != 0)
        {
            Api.DestroyFence(Device, Fence, null);
            Fence = default;
        }
    }

    private readonly record struct Staged(Silk.NET.Vulkan.Buffer Buffer, VulkanAllocation Allocation);
}
