using System.Threading;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Present;

/// <summary>
/// The <see cref="VulkanDevice"/> a windowed viewer renders and presents through.
/// </summary>
/// <remarks>
/// <para>
/// This is the real device, not a swapchain-shaped stand-in. It adds exactly three things the resource
/// device cannot supply on its own, all of them consequences of presenting: the instance and device
/// extensions a surface needs, a submission that can wait on an acquire semaphore and signal a present
/// semaphore, and a lock making the single queue safe to share between several windows.
/// </para>
/// <para>
/// <b>The instance and device are process-lifetime.</b> <see cref="VulkanInstance.Dispose"/> calls
/// <c>Vk.Dispose</c>, which unloads the shared <c>vulkan-1</c> module; a second device created after
/// that access-violates inside the loader. So this type is reference counted and only
/// <see cref="Shutdown"/> destroys anything, which is how a real device behaves anyway &#8212; nothing in a
/// viewer wants the GPU device torn down because the last tab closed.
/// </para>
/// <para>
/// <b>The presented image is not named by a render pass.</b> Per the contract, getting a frame onto the
/// screen is a backend-specific step outside the RHI. On Vulkan that step is acquire, render into the
/// acquired image, present &#8212; and because a swapchain image is a real <c>VkImage</c>, it is surfaced as
/// a <see cref="VulkanSwapchainTexture"/> and rendered into directly, with none of the full-screen blit
/// the OpenGL backend is forced to pay.
/// </para>
/// <para>
/// Not thread safe beyond <see cref="SubmissionLock"/>, like the rest of the backend.
/// </para>
/// </remarks>
public sealed class VulkanPresentDevice : VulkanDevice
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

    private VulkanPresentDevice(VulkanCoreDevice core)
        : base(core, ownsCore: true)
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
    public static VulkanPresentDevice Acquire(RhiMessageCallback? messageCallback = null, VulkanCoreOptions? options = null)
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

            Shared = new VulkanPresentDevice(new VulkanCoreDevice(effective));
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
    /// <returns>The shared device, or <see langword="null"/>.</returns>
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

    /// <summary>Takes a command buffer from the current frame's pool, ready to record into.</summary>
    /// <param name="name">Debug name for the recorded work.</param>
    /// <returns>A command buffer already in the recording state.</returns>
    /// <exception cref="InvalidOperationException">No frame is open.</exception>
    public CommandBuffer BeginFrameCommands(string name) => Core.FrameRing.CurrentPool.Acquire(name);

    /// <summary>
    /// Ends and submits the frame's command buffer, waiting on the image the presentation engine handed
    /// over and signalling both the semaphore the present will wait on and the frame timeline.
    /// </summary>
    /// <param name="commandBuffer">The command buffer to end and submit.</param>
    /// <param name="waitSemaphore">The binary semaphore <c>vkAcquireNextImageKHR</c> signals, or a
    /// default handle to wait on nothing.</param>
    /// <param name="signalSemaphore">The binary semaphore <c>vkQueuePresentKHR</c> will wait on, or a
    /// default handle to signal nothing.</param>
    /// <exception cref="VulkanException">Ending or submitting the command buffer failed.</exception>
    /// <remarks>
    /// <para>
    /// <b>This is the frame's terminal submission and the only one that may signal the timeline.</b>
    /// <see cref="VulkanFrameRing"/> records the frame's serial against its slot on
    /// <c>EndFrame</c> and the next pass through the ring waits for that serial, so a frame that ends
    /// without signalling deadlocks the ring; and signalling one timeline value twice is a validation
    /// error, so a command list submitted earlier in the same frame must not signal it either.
    /// <see cref="VulkanDevice.NotifyFrameSignalled"/> is called here, which is what stops
    /// <see cref="VulkanDevice.EndFrame"/> from adding the empty signalling submission it would
    /// otherwise need.
    /// </para>
    /// <para>
    /// The acquire semaphore is waited at <c>ALL_COMMANDS</c> rather than at the one stage that happens
    /// to touch the image today. The renderer draws into the acquired image directly, so the first thing
    /// to read or write it is whatever the frame callback records, and naming a narrower stage here
    /// would silently become wrong the moment that changes.
    /// </para>
    /// </remarks>
    public unsafe void SubmitFrameCommands(CommandBuffer commandBuffer, VkSemaphore waitSemaphore, VkSemaphore signalSemaphore)
    {
        using var _ = SubmissionLock.EnterScope();

        Core.Api.EndCommandBuffer(commandBuffer).Check("vkEndCommandBuffer");

        var commandInfo = new CommandBufferSubmitInfo
        {
            SType = StructureType.CommandBufferSubmitInfo,
            CommandBuffer = commandBuffer,
        };

        var wait = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = waitSemaphore,
            StageMask = PipelineStageFlags2.AllCommandsBit,
        };

        // Two signals: the binary semaphore the present waits on, and the frame timeline. A binary
        // semaphore cannot carry a value and a timeline semaphore cannot be waited on by
        // vkQueuePresentKHR, so both are needed and neither substitutes for the other.
        var signals = stackalloc SemaphoreSubmitInfo[2];
        var signalCount = 0u;

        if (signalSemaphore.Handle != 0)
        {
            signals[signalCount++] = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = signalSemaphore,
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

        var submit = new SubmitInfo2
        {
            SType = StructureType.SubmitInfo2,
            WaitSemaphoreInfoCount = waitSemaphore.Handle != 0 ? 1u : 0u,
            PWaitSemaphoreInfos = waitSemaphore.Handle != 0 ? &wait : null,
            CommandBufferInfoCount = 1,
            PCommandBufferInfos = &commandInfo,
            SignalSemaphoreInfoCount = signalCount,
            PSignalSemaphoreInfos = signals,
        };

        Core.Api.QueueSubmit2(Core.GraphicsQueue, 1, &submit, default).Check("vkQueueSubmit2");

        NotifyFrameSignalled();
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
    public override void BeginFrame()
    {
        using var _ = SubmissionLock.EnterScope();
        base.BeginFrame();
    }

    /// <inheritdoc/>
    public override void EndFrame()
    {
        using var _ = SubmissionLock.EnterScope();
        base.EndFrame();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            using var _ = SubmissionLock.EnterScope();
            base.Dispose(disposing);
            return;
        }

        base.Dispose(disposing);
    }
}
