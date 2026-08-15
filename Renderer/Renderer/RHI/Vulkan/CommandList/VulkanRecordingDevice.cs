using Silk.NET.Vulkan;
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
/// <b>Several command lists per frame, one signal.</b> <see cref="Submit"/> queues a command list and
/// <see cref="EndFrame"/> hands the whole frame to the queue in a single batch that signals the timeline
/// once. That shape is forced rather than chosen: the frame's timeline value can only be signalled once
/// because a timeline only moves upwards, and a frame legitimately contains several lists &#8212; the
/// scene, the post-process chain and the overlay are three, because a transfer or a dispatch is not
/// valid inside a render pass and the overlay runs after the renderer has closed its own list.
/// </para>
/// </remarks>
public class VulkanRecordingDevice : VulkanDevice
{
    private readonly List<CommandBuffer> PendingSubmission = [];

    private IVulkanDescriptorBinder? Binder;
    private VulkanCommandList? CommandList;

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

    /// <summary>
    /// Supplies the binder a derived device could only build after this constructor had run.
    /// </summary>
    /// <param name="binder">Where the command list writes descriptor bindings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="binder"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A command list already exists, so it was created
    /// without this binder and would keep using none.</exception>
    /// <remarks>
    /// <b>The constructor parameter cannot serve every device.</b> A binder needs the descriptor set
    /// layout and pipeline layout caches to write against, and on <c>VulkanPipelineDevice</c> and
    /// everything derived from it those caches are the device's own and do not exist until its
    /// constructor has finished. Passing one in through the options works only for a caller that builds
    /// the pipeline device separately and composes the two, which is what the golden harness does and
    /// what the presentation layer cannot: its device <i>is</i> the pipeline device. Without this, that
    /// device records a frame whose every <c>BindUniformBuffer</c> throws, which is a Vulkan window that
    /// draws nothing for a reason that has nothing to do with the renderer.
    /// </remarks>
    protected void AttachDescriptorBinder(IVulkanDescriptorBinder binder)
    {
        ArgumentNullException.ThrowIfNull(binder);

        if (CommandList is not null)
        {
            throw new InvalidOperationException(
                $"A command list has already been created, so it captured the binder this device had at the time. {nameof(AttachDescriptorBinder)} must run before the first {nameof(BeginCommandList)}.");
        }

        Binder = binder;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">No device frame is open, or the previous command list
    /// was never submitted.</exception>
    /// <remarks>
    /// <para>
    /// Returns the device's one command list, rebound to a command buffer from this frame's pool. The
    /// returned list is only valid until the next call.
    /// </para>
    /// <para>
    /// <b>Beginning a second list before the first is submitted is refused.</b> One
    /// <see cref="VulkanCommandList"/> is reused, so rebinding it would leave the previous command buffer
    /// recorded, unended and never queued &#8212; its work silently gone. That is invisible on OpenGL,
    /// where a recorded call has already executed by the time the next list is acquired, so it is exactly
    /// the class of mistake the oracle cannot catch and this has to name.
    /// </para>
    /// </remarks>
    public override ICommandList BeginCommandList(string name)
    {
        if (CommandList is { IsRecording: true })
        {
            throw new InvalidOperationException(
                $"Command list '{name}' was begun while the previous one is still recording and unsubmitted. Hand it back with {nameof(IDevice)}.{nameof(Submit)} first; this device reuses one list, so its work would otherwise be discarded.");
        }

        CommandList ??= new VulkanCommandList(this, Binder, name);
        CommandList.Begin(Core.FrameRing.CurrentPool.Acquire(name));

        return CommandList;
    }

    /// <inheritdoc/>
    public override void BeginFrame()
    {
        base.BeginFrame();

        // The pool backing them was just reset, so anything left here belongs to a frame that never
        // ended and its handles are already invalid. The same goes for a list still open from that
        // frame: its recording is gone with the pool, and holding it against the new frame would make
        // one frame's abandoned work look like the next frame's mistake.
        PendingSubmission.Clear();
        CommandList?.Abandon();
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="commandList"/> is not this device's.</exception>
    /// <exception cref="InvalidOperationException">A render pass is still open on it.</exception>
    /// <remarks>
    /// <para>
    /// Closes the command buffer and queues it for the frame's submission. Nothing reaches the GPU until
    /// <see cref="EndFrame"/>, which hands the frame over as one batch.
    /// </para>
    /// <para>
    /// <b>Why the submission is deferred rather than made here.</b> The frame's timeline value can only
    /// be signalled once &#8212; a timeline semaphore only moves upwards &#8212; and
    /// <see cref="Core.VulkanFrameRing"/> treats that signal as permission to reset the slot's command
    /// pool. So the signal must come after every command buffer of the frame has finished. Submitting
    /// each list as it arrives and signalling on the last would not achieve that: submissions to one
    /// queue are ordered when they <i>start</i> and not when they <i>finish</i>, so an earlier
    /// submission could still be executing when a later one's signal lands, and the ring would recycle a
    /// pool out from under it. Batching them into a single <c>vkQueueSubmit2</c> is what makes the signal
    /// genuinely last, because within one batch the signal happens after all of its command buffers
    /// complete.
    /// </para>
    /// </remarks>
    public override void Submit(ICommandList commandList)
    {
        ArgumentNullException.ThrowIfNull(commandList);

        if (!ReferenceEquals(commandList, CommandList) || CommandList is null)
        {
            throw new ArgumentException($"This command list did not come from {nameof(VulkanRecordingDevice)}.{nameof(BeginCommandList)}.", nameof(commandList));
        }

        CommandList.End();
        Core.Api.EndCommandBuffer(CommandList.Handle).Check("vkEndCommandBuffer");

        PendingSubmission.Add(CommandList.Handle);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Hands everything <see cref="Submit"/> queued to the graphics queue as one batch signalling the
    /// frame timeline, then reports that through <c>NotifyFrameSignalled</c> so the base class does not
    /// submit its empty signalling command buffer as well. A frame that submitted nothing leaves that
    /// empty submission to do the signalling, which is what keeps the ring honest either way.
    /// </remarks>
    public override void EndFrame()
    {
        if (PendingSubmission.Count > 0)
        {
            // Everything staged since BeginFrame has to reach the device before the frame that reads it
            // runs. BeginFrame flushes what was staged before the frame opened, which covers resources
            // built during loading, but a frame writes uniforms of its own -- the view constants carrying
            // the projection matrix are written by Renderer.Update, after the frame is open -- and those
            // uploads sit in the batch until something else forces it out. The frame then executes reading
            // buffers whose contents were never copied, which is not a visible error anywhere: the render
            // pass still clears, the draws are still recorded, and every vertex simply transforms by
            // whatever the uninitialised buffer holds.
            Uploads.Flush();

            SubmitFrame();
            PendingSubmission.Clear();

            NotifyFrameSignalled();
        }

        base.EndFrame();
    }

    /// <summary>
    /// Submits the frame's command buffers in one batch that signals the frame timeline.
    /// </summary>
    /// <remarks>
    /// The single-buffer case goes through <see cref="VulkanCoreDevice.SubmitAndSignal"/>, which is the
    /// core's own sanctioned path and by far the common one. The batch below is the same submission with
    /// more than one command buffer in it, written here only because the core exposes no span overload;
    /// it belongs beside <see cref="VulkanCoreDevice.SubmitAndSignal"/> and should move there when that
    /// file can be changed.
    /// </remarks>
    private unsafe void SubmitFrame()
    {
        if (PendingSubmission.Count == 1)
        {
            Core.SubmitAndSignal(PendingSubmission[0]);
            return;
        }

        var infos = new CommandBufferSubmitInfo[PendingSubmission.Count];

        for (var i = 0; i < infos.Length; i++)
        {
            infos[i] = new CommandBufferSubmitInfo
            {
                SType = StructureType.CommandBufferSubmitInfo,
                CommandBuffer = PendingSubmission[i],
            };
        }

        var signal = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = Core.FrameRing.TimelineSemaphore,
            Value = Core.FrameRing.CurrentSerial,
            StageMask = PipelineStageFlags2.AllCommandsBit,
        };

        fixed (CommandBufferSubmitInfo* commands = infos)
        {
            var submit = new SubmitInfo2
            {
                SType = StructureType.SubmitInfo2,
                CommandBufferInfoCount = (uint)infos.Length,
                PCommandBufferInfos = commands,
                SignalSemaphoreInfoCount = 1,
                PSignalSemaphoreInfos = &signal,
            };

            Core.Api.QueueSubmit2(Core.GraphicsQueue, 1, &submit, default).Check("vkQueueSubmit2");
        }
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
