using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// A <see cref="VulkanDevice"/> that can also record and submit, which makes it the first Vulkan device
/// with no unimplemented holes in the frame path.
/// </summary>
/// <remarks>
/// <para>
/// The same seam the OpenGL backend uses: <c>GLDevice</c> creates resources and
/// <c>GLRecordingDevice</c> adds recording by deriving from it rather than by editing it. Here
/// <see cref="VulkanDevice"/> owns buffers, textures, samplers, uploads and the frame lifecycle, and
/// this adds <see cref="BeginCommandList"/> and <see cref="Submit"/> without that file changing.
/// Pipeline creation is still the layer above's: a device that can also link pipelines derives from
/// this one, or supplies its <see cref="IVulkanDescriptorBinder"/> to this one's constructor.
/// </para>
/// <para>
/// <b>One command list is created and reused</b>, as on the OpenGL side, but for a different reason:
/// there the caches are what would be thrown away, here it is the attachment view cache. Each
/// <see cref="BeginCommandList"/> rebinds it to a fresh command buffer from the frame slot's pool,
/// which the frame ring resets once the slot's previous work has retired.
/// </para>
/// <para>
/// <b>One submission per frame.</b> See <see cref="Submit"/>: the frame's timeline value can only be
/// signalled once, and a second submission that did not signal it would let the ring recycle a pool
/// whose command buffers were still executing.
/// </para>
/// </remarks>
public class VulkanRecordingDevice : VulkanDevice
{
    private readonly IVulkanDescriptorBinder? Binder;

    private VulkanCommandList? CommandList;
    private ulong SubmittedSerial;

    /// <summary>Creates a device, and the Vulkan core underneath it.</summary>
    /// <param name="messageCallback">Where to route validation and driver diagnostics, or
    /// <see langword="null"/> to drop them.</param>
    /// <param name="options">Core creation parameters, or <see langword="null"/> for the defaults.</param>
    /// <param name="binder">Where the command list writes descriptor bindings, or
    /// <see langword="null"/> when the descriptor layer is not present, in which case the binding calls
    /// refuse and everything else records.</param>
    public VulkanRecordingDevice(
        RhiMessageCallback? messageCallback = null,
        VulkanCoreOptions? options = null,
        IVulkanDescriptorBinder? binder = null)
        : base(messageCallback, options)
    {
        Binder = binder;
    }

    /// <summary>Creates a device over a core the caller already built.</summary>
    /// <param name="core">The bring-up layer to use.</param>
    /// <param name="ownsCore">Whether disposing this device should dispose <paramref name="core"/>.</param>
    /// <param name="binder">Where the command list writes descriptor bindings, or
    /// <see langword="null"/>.</param>
    /// <remarks>For the presentation layer, which must build the instance and device with its platform
    /// surface extensions itself.</remarks>
    public VulkanRecordingDevice(VulkanCoreDevice core, bool ownsCore = false, IVulkanDescriptorBinder? binder = null)
        : base(core, ownsCore)
    {
        Binder = binder;
    }

    /// <inheritdoc/>
    /// <remarks>Returns the device's one command list, rebound to a command buffer from this frame's
    /// pool. The returned list is only valid until the next call.</remarks>
    public override ICommandList BeginCommandList(string name)
    {
        CommandList ??= new VulkanCommandList(this, Binder, name);
        CommandList.Begin(Core.FrameRing.CurrentPool.Acquire(name));

        return CommandList;
    }

    /// <inheritdoc/>
    public override void BeginFrame()
    {
        base.BeginFrame();

        SubmittedSerial = 0;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="commandList"/> is not this device's.</exception>
    /// <exception cref="InvalidOperationException">A command list was already submitted this frame, or
    /// a render pass is still open on this one.</exception>
    /// <remarks>
    /// <para>
    /// Ends the command buffer, submits it through
    /// <see cref="VulkanCoreDevice.SubmitAndSignal"/> so the frame timeline is signalled to this frame's
    /// serial, and then reports that through <c>NotifyFrameSignalled</c>. Without the last step
    /// <see cref="VulkanDevice.EndFrame"/> would submit its empty signalling command buffer as well:
    /// harmless but a wasted submission every frame, and the reason it is there is that the ring
    /// deadlocks three frames later on a serial nothing ever signalled.
    /// </para>
    /// <para>
    /// <b>Why a second submission in one frame is refused.</b> The signal has to be the frame's last
    /// piece of work, because <see cref="Core.VulkanFrameRing"/> takes a signalled serial as permission
    /// to reset that slot's command pool. A timeline semaphore can only be signalled to a strictly
    /// greater value, so a second submission cannot signal the same serial again; and a second
    /// submission that did not signal would leave the ring free to reset a pool whose command buffers
    /// were still executing, since submissions on one queue are ordered when they start and not when
    /// they finish. Recording several passes into one command list is the shape this backend supports;
    /// several command lists per frame needs the core to grow a submission that both waits and signals,
    /// which is a change to a file this layer does not own.
    /// </para>
    /// </remarks>
    public override void Submit(ICommandList commandList)
    {
        ArgumentNullException.ThrowIfNull(commandList);

        if (!ReferenceEquals(commandList, CommandList) || CommandList is null)
        {
            throw new ArgumentException($"This command list did not come from {nameof(VulkanRecordingDevice)}.{nameof(BeginCommandList)}.", nameof(commandList));
        }

        var serial = Core.FrameRing.CurrentSerial;

        if (SubmittedSerial == serial && serial != 0)
        {
            throw new InvalidOperationException(
                $"Frame {serial} has already submitted a command list. Its timeline value can only be signalled once, so record every pass of a frame into the one list.");
        }

        CommandList.End();
        Core.Api.EndCommandBuffer(CommandList.Handle).Check("vkEndCommandBuffer");
        Core.SubmitAndSignal(CommandList.Handle);

        SubmittedSerial = serial;
        NotifyFrameSignalled();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Before the base class waits for idle and starts destroying handles: the cached image views
            // alias images the base class is about to release.
            Core.WaitIdle();

            CommandList?.Dispose();
            CommandList = null;
        }

        base.Dispose(disposing);
    }
}
