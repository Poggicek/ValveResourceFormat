using Silk.NET.Vulkan;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Core;

/// <summary>
/// The frames-in-flight ring: one timeline semaphore, one command pool per slot, and the bookkeeping
/// that decides when a slot's resources may be reused.
/// </summary>
/// <remarks>
/// <para>
/// A single timeline semaphore replaces the fence-per-frame plus binary-semaphore arrangement Vulkan
/// 1.0 required. Frame <c>n</c> signals the timeline to value <c>n</c> when its work completes, so
/// "has frame <c>n</c> retired" is a comparison against one monotonically increasing counter that can
/// be read from the host at any time, and any number of waiters can wait on any past or future value
/// without extra objects.
/// </para>
/// <para>
/// The serial is deliberately distinct from the slot index. The slot is <c>serial % FramesInFlight</c>
/// and repeats; the serial never does, which is what makes it usable as a deletion-queue key and as
/// the stamp on <see cref="VulkanRingAllocation"/> that detects cross-frame reuse.
/// </para>
/// </remarks>
public sealed unsafe class VulkanFrameRing : IDisposable
{
    private readonly Vk Api;
    private readonly Device Device;
    private readonly VulkanDeletionQueue DeletionQueue;
    private readonly VulkanUploadRing? UploadRing;
    private readonly VulkanCommandPool[] Pools;
    private readonly ulong[] SlotSerials;

    private Semaphore Timeline;
    private bool Disposed;
    private bool FrameOpen;

    /// <summary>Gets how many frames may be recorded before the oldest must have retired.</summary>
    public int FramesInFlight { get; }

    /// <summary>Gets the serial of the frame currently being recorded. Starts at 1 and never repeats.</summary>
    public ulong CurrentSerial { get; private set; }

    /// <summary>Gets the slot the current frame is using, which selects the copy of any per-frame
    /// resource. Backs <see cref="IDevice.FrameIndex"/>.</summary>
    public int FrameIndex { get; private set; }

    /// <summary>Gets the timeline semaphore. A submission that ends a frame must signal it to
    /// <see cref="CurrentSerial"/>.</summary>
    public Semaphore TimelineSemaphore => Timeline;

    /// <summary>Gets the highest frame serial the GPU has finished.</summary>
    public ulong CompletedSerial
    {
        get
        {
            Api.GetSemaphoreCounterValue(Device, Timeline, out var value).Check("vkGetSemaphoreCounterValue");
            return value;
        }
    }

    /// <summary>Initializes a new instance of the <see cref="VulkanFrameRing"/> class.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="queueFamilyIndex">The family the per-slot command pools record for.</param>
    /// <param name="debugNames">Used to name the semaphore and the pools.</param>
    /// <param name="deletionQueue">Swept at the start of every frame.</param>
    /// <param name="uploadRing">Reset at the start of every frame, or <see langword="null"/>.</param>
    /// <param name="framesInFlight">How many slots to create.</param>
    public VulkanFrameRing(
        Vk api,
        Device device,
        uint queueFamilyIndex,
        VulkanDebugNames debugNames,
        VulkanDeletionQueue deletionQueue,
        VulkanUploadRing? uploadRing,
        int framesInFlight)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(debugNames);
        ArgumentNullException.ThrowIfNull(deletionQueue);
        ArgumentOutOfRangeException.ThrowIfLessThan(framesInFlight, 1);

        Api = api;
        Device = device;
        DeletionQueue = deletionQueue;
        UploadRing = uploadRing;
        FramesInFlight = framesInFlight;

        var typeInfo = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = 0,
        };

        var info = new SemaphoreCreateInfo
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = &typeInfo,
        };

        Api.CreateSemaphore(Device, &info, null, out Timeline).Check("vkCreateSemaphore");
        debugNames.SetName(Timeline, "VulkanFrameRing timeline");

        Pools = new VulkanCommandPool[framesInFlight];
        SlotSerials = new ulong[framesInFlight];

        for (var i = 0; i < framesInFlight; i++)
        {
            Pools[i] = new VulkanCommandPool(api, device, queueFamilyIndex, debugNames, $"VulkanFrameRing pool {i}");
        }
    }

    /// <summary>Gets the command pool for the frame being recorded.</summary>
    /// <exception cref="InvalidOperationException">No frame is open.</exception>
    public VulkanCommandPool CurrentPool
    {
        get
        {
            if (!FrameOpen)
            {
                throw new InvalidOperationException("No frame is open. Call BeginFrame first.");
            }

            return Pools[FrameIndex];
        }
    }

    /// <summary>
    /// Opens a frame: waits until the slot it will use has retired, then recycles everything that slot
    /// owns and sweeps the deletion queue.
    /// </summary>
    /// <remarks>Blocks only when the CPU is more than <see cref="FramesInFlight"/> frames ahead of the
    /// GPU, which is the point of the ring.</remarks>
    public void BeginFrame()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        if (FrameOpen)
        {
            throw new InvalidOperationException("A frame is already open. Call EndFrame first.");
        }

        CurrentSerial++;
        FrameIndex = (int)((CurrentSerial - 1) % (ulong)FramesInFlight);

        var mustRetire = SlotSerials[FrameIndex];

        if (mustRetire != 0)
        {
            WaitForSerial(mustRetire);
        }

        DeletionQueue.Collect(CompletedSerial);
        Pools[FrameIndex].Reset();
        UploadRing?.BeginFrame(FrameIndex, CurrentSerial);

        FrameOpen = true;
    }

    /// <summary>
    /// Closes the frame. The submission that ends it must signal <see cref="TimelineSemaphore"/> to
    /// <see cref="CurrentSerial"/>, otherwise the slot never becomes reusable and the next pass through
    /// the ring deadlocks.
    /// </summary>
    public void EndFrame()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        if (!FrameOpen)
        {
            throw new InvalidOperationException("No frame is open.");
        }

        SlotSerials[FrameIndex] = CurrentSerial;
        FrameOpen = false;
    }

    /// <summary>Blocks until the GPU has finished the given frame.</summary>
    /// <param name="serial">The frame serial to wait for.</param>
    /// <param name="timeoutNanoseconds">How long to wait before giving up.</param>
    /// <exception cref="VulkanException">The wait timed out or the device was lost.</exception>
    public void WaitForSerial(ulong serial, ulong timeoutNanoseconds = 5_000_000_000)
    {
        var value = serial;
        var semaphore = Timeline;

        var info = new SemaphoreWaitInfo
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
            PSemaphores = &semaphore,
            PValues = &value,
        };

        var result = Api.WaitSemaphores(Device, &info, timeoutNanoseconds);

        if (result == Result.Timeout)
        {
            throw new VulkanException($"Timed out waiting for frame {serial} to retire.", result);
        }

        result.Check("vkWaitSemaphores");
    }

    /// <summary>Blocks until every frame submitted so far has retired.</summary>
    public void WaitForIdle()
    {
        if (CurrentSerial == 0)
        {
            return;
        }

        var highest = 0UL;

        foreach (var serial in SlotSerials)
        {
            highest = Math.Max(highest, serial);
        }

        if (highest != 0)
        {
            WaitForSerial(highest);
        }

        DeletionQueue.Collect(CompletedSerial);
    }

    /// <summary>Destroys the timeline semaphore and the per-slot command pools.</summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        foreach (var pool in Pools)
        {
            pool.Dispose();
        }

        if (Timeline.Handle != 0)
        {
            Api.DestroySemaphore(Device, Timeline, null);
            Timeline = default;
        }
    }
}
