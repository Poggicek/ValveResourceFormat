using System.Threading;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Present;

/// <summary>
/// The device a windowed viewer renders and presents through: everything
/// <see cref="VulkanPipelineDevice"/> can do, plus a surface, a swapchain and a present.
/// </summary>
/// <remarks>
/// <para>
/// <b>It sits at the end of the device chain rather than beside it.</b> It derived from
/// <see cref="VulkanDevice"/> while the recording and pipeline layers were still being written, which
/// left the windowed path able to present but unable to record a command list or create a pipeline
/// &#8212; the mirror image of the hole the offscreen path had, and equally fatal. Deriving from
/// <see cref="VulkanPipelineDevice"/> means a viewer window gets the one device in the backend with no
/// member left throwing.
/// </para>
/// <para>
/// <b>The instance and device are process-lifetime.</b> <see cref="VulkanInstance.Dispose"/> calls
/// <c>Vk.Dispose</c>, which unloads the shared <c>vulkan-1</c> module; a second device created after
/// that access-violates inside the loader. So this type is reference counted and only
/// <see cref="Shutdown"/> destroys anything, which is how a real device behaves anyway &#8212; nothing
/// in a viewer wants the GPU device torn down because the last tab closed.
/// </para>
/// <para>
/// <b>The frame is still one submission.</b> <see cref="VulkanRecordingDevice"/> batches a frame's
/// command lists into a single <c>vkQueueSubmit2</c> that signals the timeline once, for reasons its own
/// remarks set out. A presented frame needs that same batch to additionally wait on the acquire
/// semaphore and signal the semaphore the present waits on, because a swapchain image may not be
/// written before the presentation engine has released it and the present has nothing else to wait on.
/// So this overrides the batching rather than adding a second submission beside it: two submissions
/// would reintroduce exactly the ordering hazard the batching exists to remove, since submissions to one
/// queue are ordered when they start and not when they finish.
/// </para>
/// <para>
/// Not thread safe beyond <see cref="SubmissionLock"/>, like the rest of the backend.
/// </para>
/// </remarks>
public sealed class VulkanPresentDevice : VulkanPipelineDevice
{
    /// <summary><c>VK_KHR_surface</c>, the instance extension every platform surface builds on.</summary>
    public const string SurfaceExtensionName = "VK_KHR_surface";

    /// <summary><c>VK_KHR_win32_surface</c>, the instance extension that turns an <c>HWND</c> into a surface.</summary>
    public const string Win32SurfaceExtensionName = "VK_KHR_win32_surface";

    /// <summary><c>VK_KHR_swapchain</c>, the device extension the swapchain itself lives in.</summary>
    public const string SwapchainExtensionName = "VK_KHR_swapchain";

    private static readonly Lock SharedLock = new();
    private static VulkanPresentDevice? Shared;
    private static int ReferenceCount;

    private readonly List<CommandBuffer> FrameCommandBuffers = [];

    private VkSemaphore FrameWaitSemaphore;
    private VkSemaphore FramePresentSemaphore;

    /// <summary>
    /// Gets the lock serialising queue submission, presentation and the frame ring.
    /// </summary>
    /// <remarks>
    /// Vulkan queues are not internally synchronized and every window shares this one queue, so the
    /// frame lifecycle and both kinds of submission are guarded together. It is reentrant, so a caller
    /// may hold it across a whole frame and still call into methods that take it themselves.
    /// </remarks>
    public Lock SubmissionLock { get; } = new();

    /// <summary>Gets the frame serial currently being recorded. Never repeats.</summary>
    public ulong CurrentFrameSerial => Core.FrameRing.CurrentSerial;

    /// <summary>Gets a value indicating whether <c>VK_LAYER_KHRONOS_validation</c> is actually loaded.
    /// A clean run with this <see langword="false"/> proves only that the code ran.</summary>
    public bool ValidationEnabled => Core.ValidationEnabled;

    /// <summary>Gets the number of error-severity messages caused by this application's use of the API.
    /// Loader complaints about third-party overlay layers are excluded; see
    /// <see cref="VulkanInstance.ValidationErrorCount"/>.</summary>
    public int ValidationErrorCount => Core.Instance.ValidationErrorCount;

    /// <summary>Gets the number of warning-severity messages caused by this application's use of the API.</summary>
    public int ValidationWarningCount => Core.Instance.ValidationWarningCount;

    /// <summary>Gets the number of command lists queued for the frame being recorded.</summary>
    public int PendingCommandListCount => FrameCommandBuffers.Count;

    // The core is built in the base call rather than in the body so that its ownership transfer is
    // visible to analysis, which is the same shape VulkanPipelineDevice's own public constructor uses.
    private VulkanPresentDevice(VulkanCoreOptions coreOptions, VulkanPipelineOptions? pipelineOptions)
        : base(new VulkanCoreDevice(coreOptions), ownsCore: true, pipelineOptions)
    {
    }

    /// <summary>
    /// Returns the shared presentation device, creating it on first use, and takes a reference to it.
    /// </summary>
    /// <param name="messageCallback">Where to route validation and driver diagnostics on the very first
    /// call, or <see langword="null"/> to drop them. Ignored afterwards: <c>VK_EXT_debug_utils</c>
    /// installs its messenger during <c>vkCreateInstance</c> and the instance already exists.</param>
    /// <param name="options">Extra core creation parameters, or <see langword="null"/> for the defaults.
    /// Its extension lists are extended with the surface and swapchain extensions rather than replaced.</param>
    /// <param name="pipelineOptions">Pipeline layer parameters, or <see langword="null"/> for the
    /// defaults. Its <see cref="VulkanPipelineOptions.MessageCallback"/> is filled in from
    /// <paramref name="messageCallback"/> when it has none.</param>
    /// <returns>The shared device.</returns>
    /// <exception cref="VulkanException">No suitable device exists, or creation failed.</exception>
    /// <remarks>
    /// No <see cref="VulkanCoreOptions.DeviceFilter"/> is imposed. Filtering on "can present" would need
    /// <c>vkGetPhysicalDeviceWin32PresentationSupportKHR</c>, which is an instance-level entry point that
    /// does not exist yet at the point the filter would have to be supplied. The authoritative check is
    /// <c>vkGetPhysicalDeviceSurfaceSupportKHR</c> against the real surface, which the presentation layer
    /// performs once the surface exists; a device that fails it reports so and the viewer falls back to
    /// OpenGL. On Win32 every graphics queue family supports presentation in practice.
    /// </remarks>
    public static VulkanPresentDevice Acquire(
        RhiMessageCallback? messageCallback = null,
        VulkanCoreOptions? options = null,
        VulkanPipelineOptions? pipelineOptions = null)
    {
        using var _ = SharedLock.EnterScope();

        if (Shared is null)
        {
            var baseOptions = options ?? new VulkanCoreOptions();

            var effective = baseOptions with
            {
                MessageCallback = messageCallback ?? baseOptions.MessageCallback,
                InstanceExtensions = Combine(baseOptions.InstanceExtensions, SurfaceExtensionName, Win32SurfaceExtensionName),
                DeviceExtensions = Combine(baseOptions.DeviceExtensions, SwapchainExtensionName),
            };

            var pipelines = pipelineOptions ?? new VulkanPipelineOptions();

            if (pipelines.MessageCallback is null)
            {
                pipelines = pipelines with { MessageCallback = messageCallback };
            }

            Shared = new VulkanPresentDevice(effective, pipelines);
        }

        ReferenceCount++;

        return Shared;
    }

    private static List<string> Combine(IReadOnlyList<string> existing, params string[] extra)
    {
        var result = new List<string>(existing);

        foreach (var name in extra)
        {
            if (!result.Contains(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <summary>Gets the shared device if one has been created, without creating one.</summary>
    public static VulkanPresentDevice? Current
    {
        get
        {
            using var _ = SharedLock.EnterScope();
            return Shared;
        }
    }

    /// <summary>Gets how many windows currently hold a reference.</summary>
    public static int LiveReferences
    {
        get
        {
            using var _ = SharedLock.EnterScope();
            return ReferenceCount;
        }
    }

    /// <summary>Drops a window's reference. Destroys nothing; see the remarks on the class.</summary>
    public static void Release()
    {
        using var _ = SharedLock.EnterScope();

        if (ReferenceCount > 0)
        {
            ReferenceCount--;
        }
    }

    /// <summary>
    /// Destroys the shared instance and device. Call once while shutting the application down, after
    /// every window that presented is gone.
    /// </summary>
    /// <returns>The number of references still outstanding, which should be zero. Anything else means a
    /// window outlived the shutdown and the validation layer will report its objects as leaks.</returns>
    public static int Shutdown()
    {
        using var _ = SharedLock.EnterScope();

        var outstanding = ReferenceCount;

        Shared?.Dispose();
        Shared = null;
        ReferenceCount = 0;

        return outstanding;
    }

    /// <summary>
    /// Names the semaphores the frame being recorded must synchronise its present against.
    /// </summary>
    /// <param name="waitOnAcquire">The binary semaphore <c>vkAcquireNextImageKHR</c> signals, or a
    /// default handle when this frame presents nothing.</param>
    /// <param name="signalForPresent">The binary semaphore <c>vkQueuePresentKHR</c> will wait on, or a
    /// default handle when this frame presents nothing.</param>
    /// <exception cref="InvalidOperationException">The frame's work has already been submitted.</exception>
    /// <remarks>Call after acquiring and before <see cref="EndFrame"/>. Both are folded into the frame's
    /// single batch, so nothing here costs an extra submission.</remarks>
    public void SetPresentSemaphores(VkSemaphore waitOnAcquire, VkSemaphore signalForPresent)
    {
        FrameWaitSemaphore = waitOnAcquire;
        FramePresentSemaphore = signalForPresent;
    }

    /// <inheritdoc/>
    public override void BeginFrame()
    {
        using var _ = SubmissionLock.EnterScope();

        base.BeginFrame();

        FrameCommandBuffers.Clear();
        FrameWaitSemaphore = default;
        FramePresentSemaphore = default;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="commandList"/> is not a Vulkan command list.</exception>
    /// <exception cref="InvalidOperationException">A render pass is still open on it.</exception>
    /// <remarks>
    /// Ends the command buffer and queues it for the frame's batch, exactly as
    /// <see cref="VulkanRecordingDevice.Submit"/> does, but into this class's list so that
    /// <see cref="EndFrame"/> can add the acquire wait and the present signal to the batch.
    /// <b>This duplicates three lines of the base class</b> and would not need to if
    /// <see cref="VulkanRecordingDevice"/> exposed its batch as a protected virtual taking the command
    /// buffers and the semaphores; that seam, and the span overload of
    /// <see cref="VulkanCoreDevice.SubmitAndSignal"/> underneath it, are the right home for this.
    /// </remarks>
    public override void Submit(ICommandList commandList)
    {
        ArgumentNullException.ThrowIfNull(commandList);

        if (commandList is not VulkanCommandList list)
        {
            throw new ArgumentException(
                $"Expected a {nameof(VulkanCommandList)} from {nameof(BeginCommandList)}, got {commandList.GetType().Name}.",
                nameof(commandList));
        }

        list.End();
        Core.Api.EndCommandBuffer(list.Handle).Check("vkEndCommandBuffer");

        FrameCommandBuffers.Add(list.Handle);
    }

    /// <inheritdoc/>
    /// <remarks>Hands the frame's command buffers to the queue as one batch that waits on the acquire
    /// semaphore, signals the present semaphore and signals the frame timeline, then reports the signal
    /// so no empty signalling submission is added. A frame that submitted nothing &#8212; an acquire that
    /// reported the swapchain out of date, say &#8212; falls through to the base class, whose empty
    /// submission keeps the frame ring honest.</remarks>
    public override void EndFrame()
    {
        using var _ = SubmissionLock.EnterScope();

        if (FrameCommandBuffers.Count > 0)
        {
            SubmitFrameBatch();
            FrameCommandBuffers.Clear();

            NotifyFrameSignalled();
        }

        FrameWaitSemaphore = default;
        FramePresentSemaphore = default;

        base.EndFrame();
    }

    private unsafe void SubmitFrameBatch()
    {
        var commands = new CommandBufferSubmitInfo[FrameCommandBuffers.Count];

        for (var i = 0; i < commands.Length; i++)
        {
            commands[i] = new CommandBufferSubmitInfo
            {
                SType = StructureType.CommandBufferSubmitInfo,
                CommandBuffer = FrameCommandBuffers[i],
            };
        }

        // The acquire semaphore is waited at ALL_COMMANDS rather than at the one stage that happens to
        // touch the image today. The frame renders into the acquired image directly, so the first thing
        // to read or write it is whatever the renderer recorded, and naming a narrower stage here would
        // silently become wrong the moment that changes.
        var wait = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = FrameWaitSemaphore,
            StageMask = PipelineStageFlags2.AllCommandsBit,
        };

        // Two signals: the binary semaphore the present waits on, and the frame timeline. A binary
        // semaphore cannot carry a value and a timeline semaphore cannot be waited on by
        // vkQueuePresentKHR, so both are needed and neither substitutes for the other.
        var signals = stackalloc SemaphoreSubmitInfo[2];
        var signalCount = 0u;

        if (FramePresentSemaphore.Handle != 0)
        {
            signals[signalCount++] = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = FramePresentSemaphore,
                StageMask = PipelineStageFlags2.AllCommandsBit,
            };
        }

        signals[signalCount++] = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = Core.FrameRing.TimelineSemaphore,
            Value = Core.FrameRing.CurrentSerial,
            StageMask = PipelineStageFlags2.AllCommandsBit,
        };

        fixed (CommandBufferSubmitInfo* commandPointer = commands)
        {
            var submit = new SubmitInfo2
            {
                SType = StructureType.SubmitInfo2,
                WaitSemaphoreInfoCount = FrameWaitSemaphore.Handle != 0 ? 1u : 0u,
                PWaitSemaphoreInfos = FrameWaitSemaphore.Handle != 0 ? &wait : null,
                CommandBufferInfoCount = (uint)commands.Length,
                PCommandBufferInfos = commandPointer,
                SignalSemaphoreInfoCount = signalCount,
                PSignalSemaphoreInfos = signals,
            };

            Core.Api.QueueSubmit2(Core.GraphicsQueue, 1, &submit, default).Check("vkQueueSubmit2");
        }
    }

    /// <summary>Blocks until the GPU has finished a frame.</summary>
    /// <param name="serial">The frame serial to wait for. Zero waits for nothing.</param>
    /// <exception cref="VulkanException">The wait timed out or the device was lost.</exception>
    /// <remarks>For the swapchain image a previous frame may still be rendering into. The frame ring
    /// only guarantees that the <i>slot</i> being reused has retired, and with more swapchain images
    /// than frames in flight those are not the same question.</remarks>
    public void WaitForFrameSerial(ulong serial)
    {
        if (serial != 0)
        {
            Core.FrameRing.WaitForSerial(serial);
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            using var _ = SubmissionLock.EnterScope();
            FrameCommandBuffers.Clear();
            base.Dispose(disposing);
            return;
        }

        base.Dispose(disposing);
    }
}
