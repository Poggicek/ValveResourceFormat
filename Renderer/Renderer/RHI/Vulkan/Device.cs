using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// What the physical device this device runs on can do, read from it at creation.
/// </summary>
/// <remarks>
/// Every value here is a real answer from the driver rather than a constant. <see cref="VulkanAdapter"/>
/// has already queried and clamped most of them during selection, including the clamp that matters:
/// AMD reports <c>0xFFFFFFFF</c> for <c>maxUniformBufferRange</c>, which converts to <c>-1</c> if taken
/// unchecked and fails every downstream range check.
/// </remarks>
public sealed class VulkanDeviceLimits : IDeviceLimits
{
    private readonly Vk Api;
    private readonly VulkanAdapter Adapter;

    /// <inheritdoc/>
    public int MaxPushConstantSize => Adapter.MaxPushConstantSize;

    /// <inheritdoc/>
    public int MaxUniformBufferRange => Adapter.MaxUniformBufferRange;

    /// <inheritdoc/>
    public float MaxSamplerAnisotropy => Adapter.MaxSamplerAnisotropy;

    /// <inheritdoc/>
    /// <remarks>The highest count usable for colour <b>and</b> depth together, which is what a render
    /// pass with both attachments can actually use.</remarks>
    public int MaxSampleCount => Adapter.MaxSampleCount;

    /// <inheritdoc/>
    public bool SupportsDrawIndirectCount => Adapter.SupportsDrawIndirectCount;

    /// <inheritdoc/>
    public bool SupportsShaderSubgroup => Adapter.SupportsShaderSubgroup;

    /// <inheritdoc/>
    public bool SupportsIndirectFirstInstance => Adapter.SupportsIndirectFirstInstance;

    /// <summary>Gets a value indicating whether compute dispatches can be forced to full subgroups.</summary>
    public bool SupportsFullSubgroups => Adapter.SupportsFullSubgroups;

    /// <summary>Reads the limits of a physical device.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="adapter">The physical device that was selected.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public VulkanDeviceLimits(Vk api, VulkanAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(adapter);

        Api = api;
        Adapter = adapter;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Answered by <c>vkGetPhysicalDeviceFormatProperties</c> against the optimal tiling feature mask,
    /// not by reasoning about the format. That distinction is the whole reason this is a device query
    /// rather than a static table: <see cref="RhiFormat.D24_UNorm_S8_UInt"/> is absent on most AMD
    /// hardware and <see cref="RhiFormat.ETC2_R8G8B8_UNorm"/> on most desktop hardware, and both are
    /// formats the renderer can legitimately be handed by a resource file. A static answer would claim
    /// support the device does not have.
    /// </remarks>
    public unsafe bool SupportsFormat(RhiFormat format, TextureUsage usage)
    {
        if (format == RhiFormat.Undefined || usage == TextureUsage.None)
        {
            return false;
        }

        Format vkFormat;

        // The tables are total and throw rather than substitute, so a format with no Vulkan equivalent
        // answers false here instead of failing at image creation.
        try
        {
            vkFormat = FormatTables.ToVkFormat(format);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        Api.GetPhysicalDeviceFormatProperties(Adapter.Handle, vkFormat, out var properties);

        var features = properties.OptimalTilingFeatures;
        var required = FormatFeatureFlags.None;

        if (usage.HasFlag(TextureUsage.Sampled))
        {
            required |= FormatFeatureFlags.SampledImageBit;
        }

        if (usage.HasFlag(TextureUsage.Storage))
        {
            required |= FormatFeatureFlags.StorageImageBit;
        }

        if (usage.HasFlag(TextureUsage.ColorTarget))
        {
            required |= FormatFeatureFlags.ColorAttachmentBit;
        }

        if (usage.HasFlag(TextureUsage.DepthStencilTarget))
        {
            required |= FormatFeatureFlags.DepthStencilAttachmentBit;
        }

        if (usage.HasFlag(TextureUsage.CopySource))
        {
            required |= FormatFeatureFlags.TransferSrcBit;
        }

        if (usage.HasFlag(TextureUsage.CopyDestination))
        {
            required |= FormatFeatureFlags.TransferDstBit;
        }

        return (features & required) == required;
    }
}

/// <summary>
/// <see cref="IDevice"/> on Vulkan, built over the bring-up layer in
/// <see cref="ValveResourceFormat.Renderer.RHI.Vulkan.Core"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the resource half of the backend: buffers, textures, samplers, shader modules, uploads,
/// the frame lifecycle and deferred destruction. Pipeline creation and command list recording are
/// declared <see langword="virtual"/> and throw, and this type is deliberately left unsealed, so the
/// agents who own those layers derive from it rather than editing this file &#8212; the same seam
/// <c>GLDevice</c> leaves for <c>GLRendererDevice</c>.
/// </para>
/// <para>
/// <b>Diagnostics are a creation parameter, not a setter.</b> <c>VK_EXT_debug_utils</c> installs its
/// messenger as part of <c>vkCreateInstance</c>, and the messages worth having most are the ones the
/// instance emits while it is being built. An <see cref="RhiMessageCallback"/> supplied afterwards
/// could never see them, which is why the contract shapes it this way.
/// </para>
/// <para>
/// <b>The device owns its core by default.</b> The parameterless-options constructor builds a
/// <see cref="VulkanCoreDevice"/> and disposes it; the constructor taking one adopts a core the
/// presentation layer already created for its surface, and leaves its lifetime alone.
/// </para>
/// <para>
/// Not thread safe, like the rest of the backend.
/// </para>
/// </remarks>
public class VulkanDevice : IDevice
{
    private readonly bool OwnsCore;
    private readonly List<IRhiResource> PendingDestroy = [];

    private VulkanSampler? DefaultSamplerValue;
    private bool FrameSignalled;
    private bool DisposedValue;

    /// <summary>Gets the bring-up layer this device is built on.</summary>
    public VulkanCoreDevice Core { get; }

    /// <inheritdoc/>
    public RhiBackend Backend => RhiBackend.Vulkan;

    /// <inheritdoc/>
    public IDeviceLimits Limits { get; }

    /// <inheritdoc/>
    public int FramesInFlight => Core.FramesInFlight;

    /// <inheritdoc/>
    public int FrameIndex => Core.FrameIndex;

    /// <summary>Gets the batching staging-upload path behind <see cref="UploadBuffer"/> and
    /// <see cref="UploadTexture"/>.</summary>
    public VulkanUploadContext Uploads { get; }

    /// <summary>
    /// Gets the sampler bound when a call site passes none.
    /// </summary>
    /// <remarks>
    /// A real linear-repeat sampler, unlike <c>GLDevice.DefaultSamplerHandle</c>, which is zero and
    /// means "use the parameters on the texture object". Vulkan has no such fallback because a
    /// <c>VkImage</c> carries no sampling state, so a texture that relied on its own filtering state on
    /// OpenGL must be given an explicit sampler here. Created on first use so a device that never
    /// samples anything does not make one.
    /// </remarks>
    public VulkanSampler DefaultSampler => DefaultSamplerValue ??= (VulkanSampler)CreateSampler(new SamplerDesc());

    /// <summary>Creates a device, and the Vulkan core underneath it.</summary>
    /// <param name="messageCallback">Where to route validation and driver diagnostics, or
    /// <see langword="null"/> to drop them.</param>
    /// <param name="options">Core creation parameters, or <see langword="null"/> for the defaults. Its
    /// <see cref="VulkanCoreOptions.MessageCallback"/> is replaced by
    /// <paramref name="messageCallback"/>.</param>
    /// <exception cref="VulkanException">No suitable device exists, or creation failed.</exception>
    public VulkanDevice(RhiMessageCallback? messageCallback = null, VulkanCoreOptions? options = null)
        : this(new VulkanCoreDevice((options ?? new VulkanCoreOptions()) with { MessageCallback = messageCallback }), ownsCore: true)
    {
    }

    /// <summary>Creates a device over a core the caller already built.</summary>
    /// <param name="core">The bring-up layer to use.</param>
    /// <param name="ownsCore">Whether disposing this device should dispose <paramref name="core"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="core"/> is <see langword="null"/>.</exception>
    /// <remarks>For the presentation layer, which must create the instance and device with its platform
    /// surface extensions and a device filter that can present, and therefore cannot let this class
    /// build the core itself. The diagnostics callback belongs to
    /// <see cref="VulkanCoreOptions.MessageCallback"/> in that case, for the reason given on the class.</remarks>
    public VulkanDevice(VulkanCoreDevice core, bool ownsCore = false)
    {
        ArgumentNullException.ThrowIfNull(core);

        Core = core;
        OwnsCore = ownsCore;
        Limits = new VulkanDeviceLimits(core.Api, core.Adapter);

        Uploads = new VulkanUploadContext(
            core.Api,
            core.Handle,
            core.GraphicsQueue,
            core.GraphicsQueueFamily,
            core.Allocator,
            core.DebugNames);
    }

    /// <inheritdoc/>
    public IBuffer CreateBuffer(in BufferDesc desc)
        => new VulkanBuffer(Core.Api, Core.Handle, Core.Allocator, Core.DebugNames, in desc);

    /// <inheritdoc/>
    public ITexture CreateTexture(in TextureDesc desc)
        => new VulkanTexture(Core.Api, Core.Handle, Core.Allocator, Core.DebugNames, in desc);

    /// <inheritdoc/>
    /// <remarks>Anisotropy is clamped to <see cref="IDeviceLimits.MaxSamplerAnisotropy"/> here, and to
    /// 1 when the device did not enable the <c>samplerAnisotropy</c> feature, so a call site can ask for
    /// more than the hardware has without producing a validation error.</remarks>
    public ISampler CreateSampler(in SamplerDesc desc)
    {
        var clamped = desc with { MaxAnisotropy = Math.Clamp(desc.MaxAnisotropy, 1f, Math.Max(1f, Limits.MaxSamplerAnisotropy)) };

        return new VulkanSampler(Core.Api, Core.Handle, Core.DebugNames, in clamped, DescribeSampler(in clamped));
    }

    private static string DescribeSampler(in SamplerDesc desc)
        => $"{desc.MinFilter}/{desc.MagFilter}/{desc.MipFilter} {desc.AddressU},{desc.AddressV},{desc.AddressW} aniso {desc.MaxAnisotropy}";

    /// <inheritdoc/>
    /// <remarks>The code is SPIR-V on this backend, not GLSL source.</remarks>
    public IShaderModule CreateShaderModule(ReadOnlySpan<byte> code, ShaderStage stage, string name)
        => new VulkanShaderModule(Core.Api, Core.Handle, Core.DebugNames, code, stage, name);

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, on this type.</exception>
    /// <remarks>A graphics pipeline needs a pipeline layout built from the contract's descriptor set
    /// table, a <c>VkPipelineCache</c>, and the dynamic-rendering attachment formats reflected out of
    /// the SPIR-V. Those belong to the pipeline layer; a derived device that owns them overrides this
    /// without this file having to change.</remarks>
    public virtual IGraphicsPipeline CreateGraphicsPipeline(GraphicsPipelineDesc desc)
        => throw new NotSupportedException($"{nameof(VulkanDevice)} creates no pipelines. Override {nameof(CreateGraphicsPipeline)} on a device that owns the pipeline layouts and the {nameof(PipelineCacheKey)} cache.");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, on this type. See
    /// <see cref="CreateGraphicsPipeline"/>.</exception>
    public virtual IComputePipeline CreateComputePipeline(in ComputePipelineDesc desc)
        => throw new NotSupportedException($"{nameof(VulkanDevice)} creates no pipelines. Override {nameof(CreateComputePipeline)} on a device that owns the pipeline layouts and the {nameof(PipelineCacheKey)} cache.");

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not a <see cref="VulkanBuffer"/>.</exception>
    /// <remarks>A host visible buffer is written straight through its persistent mapping. A device local
    /// one goes through <see cref="Uploads"/>, which is a real staging buffer and a real queue
    /// submission, so unlike the OpenGL backend this is not something a caller may treat as free.</remarks>
    public void UploadBuffer(IBuffer destination, int offsetInBytes, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (destination is not VulkanBuffer buffer)
        {
            throw new ArgumentException($"Expected a {nameof(VulkanBuffer)}, got {destination.GetType().Name}.", nameof(destination));
        }

        if (buffer.IsMapped)
        {
            buffer.WriteMapped(offsetInBytes, data);
            return;
        }

        Uploads.StageBuffer(buffer, offsetInBytes, data);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not a <see cref="VulkanTexture"/>.</exception>
    /// <remarks>Leaves the texture in <see cref="ResourceState.CopyDestination"/> with its tracked state
    /// updated. Transition it to whatever it is actually for at the point of use; this layer does not
    /// guess.</remarks>
    public void UploadTexture(ITexture destination, int mipLevel, int arrayLayer, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (destination is not VulkanTexture texture)
        {
            throw new ArgumentException($"Expected a {nameof(VulkanTexture)}, got {destination.GetType().Name}.", nameof(destination));
        }

        Uploads.StageTexture(texture, mipLevel, arrayLayer, data);
    }

    /// <inheritdoc/>
    /// <remarks>Outstanding uploads are flushed first, so anything staged before the frame opened is on
    /// the device before the frame's work can read it.</remarks>
    public virtual void BeginFrame()
    {
        ObjectDisposedException.ThrowIf(DisposedValue, this);

        Uploads.Flush();
        Core.BeginFrame();

        FrameSignalled = false;
        ReleasePending();
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, on this type. Recording belongs to the command
    /// list layer; a derived device returns its recorder.</exception>
    public virtual ICommandList BeginCommandList(string name)
        => throw new NotSupportedException($"{nameof(VulkanDevice)} records no commands. Override {nameof(BeginCommandList)} on a device that supplies an {nameof(ICommandList)} implementation.");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, on this type. See <see cref="BeginCommandList"/>.</exception>
    /// <remarks>An overriding implementation must submit through
    /// <see cref="VulkanCoreDevice.SubmitAndSignal"/> and then call <see cref="NotifyFrameSignalled"/>,
    /// so that <see cref="EndFrame"/> knows the frame's timeline value has been signalled.</remarks>
    public virtual void Submit(ICommandList commandList)
    {
        ArgumentNullException.ThrowIfNull(commandList);

        throw new NotSupportedException($"{nameof(VulkanDevice)} records no commands. Override {nameof(Submit)} alongside {nameof(BeginCommandList)}, submit via {nameof(VulkanCoreDevice)}.{nameof(VulkanCoreDevice.SubmitAndSignal)}, and call {nameof(NotifyFrameSignalled)}.");
    }

    /// <summary>Records that a submission this frame has signalled the frame timeline.</summary>
    /// <remarks>Call after <see cref="VulkanCoreDevice.SubmitAndSignal"/>. It stops
    /// <see cref="EndFrame"/> from submitting the empty signalling command buffer it would otherwise
    /// need.</remarks>
    protected void NotifyFrameSignalled() => FrameSignalled = true;

    /// <inheritdoc/>
    /// <remarks>
    /// If nothing has signalled the frame timeline this frame, an empty command buffer is submitted to
    /// signal it. That is not a formality: <see cref="VulkanFrameRing"/> records the frame's serial
    /// against its slot on <c>EndFrame</c> and the next pass through the ring waits for that serial, so
    /// a frame that ends without signalling deadlocks the ring three frames later. Keeping the ring
    /// honest here is what lets this device be driven before a command list layer exists.
    /// </remarks>
    public virtual void EndFrame()
    {
        ObjectDisposedException.ThrowIf(DisposedValue, this);

        if (!FrameSignalled)
        {
            var command = Core.FrameRing.CurrentPool.Acquire("VulkanDevice empty frame");
            Core.Api.EndCommandBuffer(command).Check("vkEndCommandBuffer");
            Core.SubmitAndSignal(command);
            FrameSignalled = true;
        }

        Core.EndFrame();
    }

    /// <inheritdoc/>
    public void WaitIdle()
    {
        Uploads.Flush();
        Core.WaitIdle();
        Core.DeletionQueue.Collect(Core.FrameRing.CompletedSerial);
        ReleasePending();
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="resource"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The backend's own resource types hand their handles straight to
    /// <see cref="VulkanDeletionQueue"/>, stamped with the serial of the frame being recorded, and are
    /// released once the GPU timeline has passed it. Anything else is held and disposed at the same
    /// point, which is the best that can be done for a type this device did not create.
    /// </remarks>
    public void DeferredDestroy(IRhiResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var serial = Core.FrameRing.CurrentSerial;
        var queue = Core.DeletionQueue;

        switch (resource)
        {
            case VulkanBuffer buffer:
                buffer.EnqueueDestroy(queue, serial);
                break;

            case VulkanTexture texture:
                texture.EnqueueDestroy(queue, serial);
                break;

            case VulkanSampler sampler:
                sampler.EnqueueDestroy(queue, serial);
                break;

            case VulkanShaderModule module:
                module.EnqueueDestroy(queue, serial);
                break;

            default:
                PendingDestroy.Add(resource);
                break;
        }
    }

    private void ReleasePending()
    {
        if (PendingDestroy.Count == 0)
        {
            return;
        }

        foreach (var resource in PendingDestroy)
        {
            resource.Dispose();
        }

        PendingDestroy.Clear();
    }

    /// <inheritdoc/>
    /// <remarks>A queue-level label, which is the only Vulkan construct that spans work recorded by
    /// callees onto command buffers the opener never sees.</remarks>
    public IDisposable DebugScope(string name) => Core.DebugNames.QueueScope(Core.GraphicsQueue, name);

    /// <summary>Waits for the device to go idle and releases everything this device owns.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Waits for the device to go idle and releases everything this device owns.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (DisposedValue || !disposing)
        {
            return;
        }

        DisposedValue = true;

        // Everything below destroys handles, which is only legal once nothing is executing.
        Core.WaitIdle();

        Uploads.Dispose();
        DefaultSamplerValue?.Dispose();
        DefaultSamplerValue = null;

        ReleasePending();
        Core.DeletionQueue.Flush();

        if (OwnsCore)
        {
            Core.Dispose();
        }
    }
}
