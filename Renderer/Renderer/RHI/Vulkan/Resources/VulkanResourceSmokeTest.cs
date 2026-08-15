using System.Globalization;
using System.Text;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// A deliberate API misuse the smoke test can commit, to prove the validation layer is watching.
/// </summary>
/// <remarks>
/// A test that has only ever passed is not evidence that it can fail. Each of these records a single
/// invalid command and expects <see cref="VulkanInstance.ValidationErrorCount"/> to rise; a run in
/// which the count does not move means validation is not actually loaded and every green result in
/// this file is worthless.
/// </remarks>
public enum VulkanSmokeTestProvocation
{
    /// <summary>Commit no violation. The normal run.</summary>
    None,

    /// <summary>Copy more bytes into a buffer than it holds.</summary>
    OversizedCopy,

    /// <summary>Name a layout the destination image is not in when copying into it.</summary>
    WrongImageLayout,
}

/// <summary>What <see cref="VulkanResourceSmokeTest.Run"/> observed.</summary>
/// <param name="Succeeded">Whether every stage passed and validation stayed silent. Always
/// <see langword="false"/> for a run with a provocation, which expects the opposite.</param>
/// <param name="ValidationActive">Whether <c>VK_LAYER_KHRONOS_validation</c> was actually loaded. A
/// pass with this <see langword="false"/> proves only that the code ran.</param>
/// <param name="DeviceName">The physical device that was used.</param>
/// <param name="Provocation">The violation this run deliberately committed, if any.</param>
/// <param name="Checks">Every named check and whether it held.</param>
/// <param name="ValidationErrors">Error-severity messages attributable to this code's use of the API.
/// Loader complaints about third-party overlay layers are excluded; see
/// <see cref="VulkanInstance.ValidationErrorCount"/>.</param>
/// <param name="ValidationWarnings">Warning-severity messages attributable to this code.</param>
/// <param name="ForeignMessages">Error and warning messages that came from the loader's view of the
/// machine rather than from this code, counted separately so an overlay layer cannot fail the run.</param>
/// <param name="Messages">Every message the debug messenger delivered.</param>
/// <param name="Statistics">Allocator state at the end, before teardown.</param>
public readonly record struct VulkanResourceSmokeTestResult(
    bool Succeeded,
    bool ValidationActive,
    string DeviceName,
    VulkanSmokeTestProvocation Provocation,
    IReadOnlyList<(string Name, bool Passed)> Checks,
    int ValidationErrors,
    int ValidationWarnings,
    int ForeignMessages,
    IReadOnlyList<string> Messages,
    VulkanMemoryStatistics Statistics)
{
    /// <summary>Renders the result as a short report.</summary>
    /// <returns>The report text.</returns>
    public override string ToString()
    {
        var builder = new StringBuilder();

        builder.Append(CultureInfo.InvariantCulture, $"Vulkan resource smoke test [{Provocation}]: {(Succeeded ? "PASS" : "FAIL")}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"  device            {DeviceName}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture,
            $"  validation layer  {(ValidationActive ? "loaded" : "NOT LOADED - this run proves little")}");
        builder.AppendLine();

        foreach (var (name, passed) in Checks)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  {(passed ? "ok  " : "FAIL")}  {name}");
            builder.AppendLine();
        }

        builder.Append(CultureInfo.InvariantCulture,
            $"  validation        {ValidationErrors} errors, {ValidationWarnings} warnings ({ForeignMessages} foreign, not counted)");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture,
            $"  allocator         {Statistics.BlockCount} blocks, {Statistics.ReservedBytes} reserved, {Statistics.UsedBytes} used");
        builder.AppendLine();

        foreach (var message in Messages)
        {
            builder.Append("  | ").Append(message);
            builder.AppendLine();
        }

        return builder.ToString();
    }
}

/// <summary>
/// Exercises <see cref="VulkanDevice"/>'s resource layer against a real device with the validation
/// layer required to stay completely silent: buffers and textures of several formats including a block
/// compressed one and a depth-stencil one, uploads, readbacks with a byte comparison, views including
/// a stencil-aspect one, explicit layout transitions, the frame lifecycle, and deferred destruction.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="VulkanSmokeTest"/> one layer up. That one proves the bring-up is
/// sound while writing raw Vulkan; this one proves the same things happen correctly when they are
/// driven through <see cref="IDevice"/>, <see cref="IBuffer"/>, <see cref="ITexture"/> and
/// <see cref="ISampler"/>.
/// </para>
/// <para>
/// A tolerated validation warning becomes an unreproducible crash later, so
/// <see cref="VulkanResourceSmokeTestResult.Succeeded"/> requires the counters to be exactly zero
/// rather than merely free of errors. Only <c>Validation</c> and <c>Performance</c> typed messages are
/// counted: a consumer machine routinely carries overlay layers from capture software and storefronts
/// whose stale manifests the loader reports at error severity through the same messenger, and counting
/// those would make the result depend on what the developer happens to have installed.
/// </para>
/// <para>
/// Run it with a <see cref="VulkanSmokeTestProvocation"/> to confirm the check has teeth. Each
/// provocation records one invalid command and expects the error count to rise.
/// </para>
/// </remarks>
public static unsafe class VulkanResourceSmokeTest
{
    private const int BufferBytes = 64 * 1024;
    private const int ImageSize = 64;
    private const int CubeSize = 32;

    /// <summary>Runs the smoke test.</summary>
    /// <param name="provocation">A deliberate violation to commit, or
    /// <see cref="VulkanSmokeTestProvocation.None"/> for the normal run.</param>
    /// <param name="options">Overrides, or <see langword="null"/> for validation-on defaults.</param>
    /// <returns>What happened.</returns>
    public static VulkanResourceSmokeTestResult Run(
        VulkanSmokeTestProvocation provocation = VulkanSmokeTestProvocation.None,
        VulkanCoreOptions? options = null)
    {
        var messages = new List<string>();
        var checks = new List<(string, bool)>();

        var effective = (options ?? new VulkanCoreOptions()) with
        {
            EnableValidation = options?.EnableValidation ?? true,
            EnableSynchronizationValidation = options?.EnableSynchronizationValidation ?? true,
            ApplicationName = "VulkanResourceSmokeTest",
        };

        void OnMessage(RhiMessageSeverity severity, string message)
        {
            lock (messages)
            {
                messages.Add($"{severity}: {message}");
            }
        }

        using var device = new VulkanDevice(OnMessage, effective);

        VulkanMemoryStatistics statistics;
        int errorsBefore;

        try
        {
            CheckLimits(device, checks);

            RoundTripBuffer(device, checks);
            RoundTripTexture(device, checks);
            RoundTripCompressedTexture(device, checks);
            RoundTripCubeFace(device, checks);
            ExerciseDepthStencil(device, checks);
            ExerciseSamplers(device, checks);
            ExerciseFrameLifecycle(device, checks);

            statistics = device.Core.Allocator.Statistics;

            errorsBefore = device.Core.Instance.ValidationErrorCount;

            if (provocation != VulkanSmokeTestProvocation.None)
            {
                Provoke(device, provocation);

                var raised = device.Core.Instance.ValidationErrorCount > errorsBefore;
                checks.Add(($"provocation {provocation} was reported by validation", raised));
            }
        }
        finally
        {
            device.WaitIdle();
        }

        var errors = device.Core.Instance.ValidationErrorCount;
        var warnings = device.Core.Instance.ValidationWarningCount;
        var foreign = device.Core.Instance.ErrorCount + device.Core.Instance.WarningCount - errors - warnings;

        var everyCheckPassed = true;

        foreach (var (_, passed) in checks)
        {
            everyCheckPassed &= passed;
        }

        // A provoked run is expected to be dirty, so "succeeded" means the checks held and the
        // provocation was seen, not that validation stayed silent.
        var succeeded = provocation == VulkanSmokeTestProvocation.None
            ? everyCheckPassed && errors == 0 && warnings == 0
            : everyCheckPassed;

        return new VulkanResourceSmokeTestResult(
            succeeded,
            device.Core.ValidationEnabled,
            device.Core.Adapter.Name,
            provocation,
            checks.ConvertAll(c => (c.Item1, c.Item2)),
            errors,
            warnings,
            foreign,
            messages,
            statistics);
    }

    private static void CheckLimits(VulkanDevice device, List<(string, bool)> checks)
    {
        var limits = device.Limits;

        checks.Add(("limits: push constant block is at least the 128 byte floor", limits.MaxPushConstantSize >= 128));
        checks.Add(("limits: uniform buffer range is positive", limits.MaxUniformBufferRange > 0));
        checks.Add(("limits: max sample count is a positive power of two",
            limits.MaxSampleCount > 0 && (limits.MaxSampleCount & (limits.MaxSampleCount - 1)) == 0));
        checks.Add(("limits: anisotropy is at least 1", limits.MaxSamplerAnisotropy >= 1f));
        checks.Add(("limits: drawIndirectCount is gated on by adapter selection", limits.SupportsDrawIndirectCount));
        checks.Add(("limits: subgroup ops are gated on by adapter selection", limits.SupportsShaderSubgroup));
        checks.Add(("limits: indirect first instance is gated on by adapter selection", limits.SupportsIndirectFirstInstance));

        checks.Add(("limits: R8G8B8A8_UNorm is sampleable", limits.SupportsFormat(RhiFormat.R8G8B8A8_UNorm, TextureUsage.Sampled)));
        checks.Add(("limits: D32_SFloat is a depth target", limits.SupportsFormat(RhiFormat.D32_SFloat, TextureUsage.DepthStencilTarget)));

        // The three answers a static table would get wrong, and the reason SupportsFormat is a driver
        // query. A compressed format cannot be rendered into or written as a storage image on any
        // device, and Undefined is not a format at all.
        checks.Add(("limits: BC3 is not a colour target", !limits.SupportsFormat(RhiFormat.BC3_UNorm, TextureUsage.ColorTarget)));
        checks.Add(("limits: BC3 is not a storage image", !limits.SupportsFormat(RhiFormat.BC3_UNorm, TextureUsage.Storage)));
        checks.Add(("limits: Undefined supports nothing", !limits.SupportsFormat(RhiFormat.Undefined, TextureUsage.Sampled)));
    }

    private static void RoundTripBuffer(VulkanDevice device, List<(string, bool)> checks)
    {
        var expected = new byte[BufferBytes];

        for (var i = 0; i < expected.Length; i++)
        {
            expected[i] = (byte)((i * 31) + 7);
        }

        using var deviceBuffer = (VulkanBuffer)device.CreateBuffer(new BufferDesc(
            BufferBytes,
            BufferUsage.Storage | BufferUsage.CopySource | BufferUsage.CopyDestination,
            BufferMemory.DeviceLocal,
            "SmokeTest device buffer"));

        using var readback = (VulkanBuffer)device.CreateBuffer(new BufferDesc(
            BufferBytes,
            BufferUsage.CopyDestination,
            BufferMemory.HostReadback,
            "SmokeTest buffer readback"));

        // Device local, so this takes the staging path through VulkanUploadContext.
        device.UploadBuffer(deviceBuffer, 0, expected);

        var command = device.Uploads.BeginBatch();
        deviceBuffer.RecordBarrier(command, ResourceState.CopyDestination, ResourceState.CopySource);

        var copy = new BufferCopy { SrcOffset = 0, DstOffset = 0, Size = BufferBytes };
        device.Core.Api.CmdCopyBuffer(command, deviceBuffer.Handle, readback.Handle, 1, &copy);

        device.Uploads.Flush();

        readback.InvalidateRange();
        checks.Add(("buffer: device local round trip is byte identical", readback.MappedData[..BufferBytes].SequenceEqual(expected)));

        // The host visible path, which writes straight through the persistent mapping.
        using var hostBuffer = (VulkanBuffer)device.CreateBuffer(new BufferDesc(
            256,
            BufferUsage.Uniform,
            BufferMemory.HostUpload,
            "SmokeTest host buffer"));

        var small = expected.AsSpan(0, 256);
        device.UploadBuffer(hostBuffer, 0, small);

        checks.Add(("buffer: host upload mapping holds what was written", hostBuffer.MappedData[..256].SequenceEqual(small)));
        checks.Add(("buffer: device local buffer refuses MappedData", Throws<InvalidOperationException>(() => _ = deviceBuffer.MappedData)));
        checks.Add(("buffer: undeclared usage is refused by name",
            Throws<InvalidOperationException>(() => hostBuffer.RequireUsage(BufferUsage.Indirect, "A test"))));
    }

    private static void RoundTripTexture(VulkanDevice device, List<(string, bool)> checks)
    {
        const int bytes = ImageSize * ImageSize * 4;

        var expected = new byte[bytes];

        for (var i = 0; i < expected.Length; i++)
        {
            expected[i] = (byte)((i * 17) + 3);
        }

        using var texture = (VulkanTexture)device.CreateTexture(new TextureDesc(
            ImageSize,
            ImageSize,
            RhiFormat.R8G8B8A8_UNorm,
            TextureUsage.Sampled | TextureUsage.CopySource | TextureUsage.CopyDestination,
            "SmokeTest 2D texture",
            MipLevels: 3));

        checks.Add(("texture: starts in Undefined", texture.StateOf(0, 0) == ResourceState.Undefined));

        device.UploadTexture(texture, 0, 0, expected);

        checks.Add(("texture: upload leaves it tracked as CopyDestination", texture.StateOf(0, 0) == ResourceState.CopyDestination));

        using var readback = (VulkanBuffer)device.CreateBuffer(new BufferDesc(
            bytes,
            BufferUsage.CopyDestination,
            BufferMemory.HostReadback,
            "SmokeTest 2D readback"));

        var command = device.Uploads.BeginBatch();

        // The tracked state is the barrier's oldLayout, and the expected-state argument is a check on
        // this call site's belief rather than an input to it.
        texture.TransitionTo(command, ResourceState.CopySource, ResourceState.CopyDestination);

        CopyImageToBuffer(device, command, texture, readback, 0, 0);
        device.Uploads.Flush();

        readback.InvalidateRange();
        checks.Add(("texture: R8G8B8A8_UNorm mip 0 round trip is byte identical", readback.MappedData[..bytes].SequenceEqual(expected)));
        checks.Add(("texture: tracked state follows the transition", texture.StateOf(0, 0) == ResourceState.CopySource));

        // A view of the tail of the mip chain, which must not disturb the parent's tracking.
        using var view = (VulkanTexture)texture.CreateView(1, 2, 0, 1);

        checks.Add(("view: reports the mip 1 extent", view.Width == ImageSize / 2 && view.MipLevels == 2));
        checks.Add(("view: shares the parent's image", view.Handle.Handle == texture.Handle.Handle));
        checks.Add(("view: reads the parent's tracked state", view.StateOf(0, 0) == texture.StateOf(1, 0)));

        var mipCommand = device.Uploads.BeginBatch();
        view.TransitionTo(mipCommand, ResourceState.ShaderRead);
        device.Uploads.Flush();

        checks.Add(("view: transitioning through a view updates the parent's tracking",
            texture.StateOf(1, 0) == ResourceState.ShaderRead && texture.StateOf(2, 0) == ResourceState.ShaderRead));
        checks.Add(("view: a transition through a view leaves other mips alone", texture.StateOf(0, 0) == ResourceState.CopySource));
        checks.Add(("texture: mip size rounds correctly", texture.MipSizeInBytes(1) == (ImageSize / 2) * (ImageSize / 2) * 4));
    }

    private static void RoundTripCompressedTexture(VulkanDevice device, List<(string, bool)> checks)
    {
        if (!device.Limits.SupportsFormat(RhiFormat.BC3_UNorm, TextureUsage.Sampled))
        {
            checks.Add(("compressed: BC3 unsupported on this device, skipped", true));
            return;
        }

        // 64x64 of 4x4 blocks at 16 bytes each.
        const int blocks = (ImageSize / 4) * (ImageSize / 4);
        const int bytes = blocks * 16;

        var expected = new byte[bytes];

        for (var i = 0; i < expected.Length; i++)
        {
            expected[i] = (byte)((i * 13) + 11);
        }

        using var texture = (VulkanTexture)device.CreateTexture(new TextureDesc(
            ImageSize,
            ImageSize,
            RhiFormat.BC3_UNorm,
            TextureUsage.Sampled | TextureUsage.CopySource | TextureUsage.CopyDestination,
            "SmokeTest BC3 texture"));

        checks.Add(("compressed: mip 0 size is a whole number of blocks", texture.MipSizeInBytes(0) == bytes));

        device.UploadTexture(texture, 0, 0, expected);

        using var readback = (VulkanBuffer)device.CreateBuffer(new BufferDesc(
            bytes,
            BufferUsage.CopyDestination,
            BufferMemory.HostReadback,
            "SmokeTest BC3 readback"));

        var command = device.Uploads.BeginBatch();
        texture.TransitionTo(command, ResourceState.CopySource, ResourceState.CopyDestination);
        CopyImageToBuffer(device, command, texture, readback, 0, 0);
        device.Uploads.Flush();

        readback.InvalidateRange();
        checks.Add(("compressed: BC3 round trip is byte identical", readback.MappedData[..bytes].SequenceEqual(expected)));

        checks.Add(("compressed: a wrongly sized upload is refused before validation sees it",
            Throws<ArgumentOutOfRangeException>(() => device.UploadTexture(texture, 0, 0, new byte[bytes - 16]))));
    }

    private static void RoundTripCubeFace(VulkanDevice device, List<(string, bool)> checks)
    {
        const int bytes = CubeSize * CubeSize * 4;
        const int face = 3;

        var expected = new byte[bytes];

        for (var i = 0; i < expected.Length; i++)
        {
            expected[i] = (byte)((i * 7) + 29);
        }

        using var cube = (VulkanTexture)device.CreateTexture(new TextureDesc(
            CubeSize,
            CubeSize,
            RhiFormat.R8G8B8A8_UNorm,
            TextureUsage.Sampled | TextureUsage.CopySource | TextureUsage.CopyDestination,
            "SmokeTest cube",
            Dimension: TextureDimension.TextureCube));

        checks.Add(("cube: has six array layers", cube.LayerCount == 6));

        device.UploadTexture(cube, 0, face, expected);

        using var readback = (VulkanBuffer)device.CreateBuffer(new BufferDesc(
            bytes,
            BufferUsage.CopyDestination,
            BufferMemory.HostReadback,
            "SmokeTest cube readback"));

        var command = device.Uploads.BeginBatch();
        cube.TransitionTo(command, ResourceState.CopySource);
        CopyImageToBuffer(device, command, cube, readback, 0, face);
        device.Uploads.Flush();

        readback.InvalidateRange();
        checks.Add(("cube: face 3 round trip is byte identical", readback.MappedData[..bytes].SequenceEqual(expected)));

        using var faceView = (VulkanTexture)cube.CreateView(0, 1, face, 1);
        checks.Add(("cube: a single face view is one layer", faceView.LayerCount == 1));
    }

    private static void ExerciseDepthStencil(VulkanDevice device, List<(string, bool)> checks)
    {
        var format = device.Limits.SupportsFormat(RhiFormat.D32_SFloat_S8_UInt, TextureUsage.DepthStencilTarget)
            ? RhiFormat.D32_SFloat_S8_UInt
            : RhiFormat.D24_UNorm_S8_UInt;

        if (!device.Limits.SupportsFormat(format, TextureUsage.DepthStencilTarget))
        {
            checks.Add(("depth: no depth-stencil format supported, skipped", false));
            return;
        }

        using var depth = (VulkanTexture)device.CreateTexture(new TextureDesc(
            ImageSize,
            ImageSize,
            format,
            TextureUsage.DepthStencilTarget | TextureUsage.Sampled,
            "SmokeTest depth-stencil"));

        // The requirement Framebuffer.cs depends on: a stencil-aspect view of the depth attachment.
        using var stencilView = (VulkanTexture)depth.CreateView(0, 1, 0, 1, aspect: TextureAspect.Stencil);
        using var depthView = (VulkanTexture)depth.CreateView(0, 1, 0, 1, aspect: TextureAspect.Depth);

        checks.Add(("depth: a stencil aspect view is created", stencilView.Aspect == ImageAspectFlags.StencilBit));
        checks.Add(("depth: a depth aspect view is created", depthView.Aspect == ImageAspectFlags.DepthBit));
        checks.Add(("depth: the parent addresses both aspects",
            depth.Aspect == (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)));

        // A stencil view of a depth-only format is the mistake worth failing loudly on.
        using var depthOnly = (VulkanTexture)device.CreateTexture(new TextureDesc(
            8,
            8,
            RhiFormat.D32_SFloat,
            TextureUsage.DepthStencilTarget,
            "SmokeTest depth only"));

        checks.Add(("depth: a stencil view of a depth-only format is refused",
            Throws<ArgumentOutOfRangeException>(() => depthOnly.CreateView(0, 1, 0, 1, aspect: TextureAspect.Stencil))));

        var command = device.Uploads.BeginBatch();
        depth.TransitionTo(command, ResourceState.DepthWrite, ResourceState.Undefined);
        depth.TransitionTo(command, ResourceState.DepthRead, ResourceState.DepthWrite);
        depth.TransitionTo(command, ResourceState.ShaderRead, ResourceState.DepthRead);
        device.Uploads.Flush();

        checks.Add(("depth: transitions through the attachment states are tracked", depth.StateOf(0, 0) == ResourceState.ShaderRead));

        // A depth image sampled as ShaderRead must land in DepthStencilReadOnlyOptimal, not the colour
        // layout. Getting this wrong is legal-looking and produces a validation error at the bind.
        checks.Add(("depth: ShaderRead maps to the depth read-only layout",
            VulkanResourceStates.ForImage(ResourceState.ShaderRead, isDepth: true).Layout == ImageLayout.DepthStencilReadOnlyOptimal));
        checks.Add(("states: a buffer state is refused for an image",
            Throws<ArgumentOutOfRangeException>(() => VulkanResourceStates.ForImage(ResourceState.IndexBuffer))));
        checks.Add(("states: an image state is refused for a buffer",
            Throws<ArgumentOutOfRangeException>(() => VulkanResourceStates.ForBuffer(ResourceState.ColorTarget))));
    }

    private static void ExerciseSamplers(VulkanDevice device, List<(string, bool)> checks)
    {
        using var linear = (VulkanSampler)device.CreateSampler(new SamplerDesc());

        using var shadow = (VulkanSampler)device.CreateSampler(new SamplerDesc(
            AddressU: AddressMode.ClampToBorder,
            AddressV: AddressMode.ClampToBorder,
            CompareOp: Comparison.Closer));

        using var aniso = (VulkanSampler)device.CreateSampler(new SamplerDesc(MaxAnisotropy: 1024f));

        checks.Add(("sampler: linear repeat is created", linear.Handle.Handle != 0));
        checks.Add(("sampler: a comparison sampler is created", shadow.Handle.Handle != 0));
        checks.Add(("sampler: anisotropy is clamped to the device limit",
            aniso.Description.MaxAnisotropy <= device.Limits.MaxSamplerAnisotropy));
        checks.Add(("sampler: the device default sampler exists", device.DefaultSampler.Handle.Handle != 0));
    }

    private static void ExerciseFrameLifecycle(VulkanDevice device, List<(string, bool)> checks)
    {
        // More passes than there are frame slots, so the ring wraps and every slot has to wait on a
        // serial that was actually signalled. A frame ending without a signal deadlocks here.
        var passes = (device.FramesInFlight * 2) + 1;
        var indices = new HashSet<int>();

        for (var i = 0; i < passes; i++)
        {
            device.BeginFrame();
            indices.Add(device.FrameIndex);
            device.EndFrame();
        }

        checks.Add(("frames: the ring wrapped without deadlocking", indices.Count == device.FramesInFlight));

        // Deferred destruction of a resource created inside the frame loop. The using is deliberate as
        // well as required: handing a resource to DeferredDestroy releases its handles to the queue and
        // makes the later Dispose a no-op, so the two cannot destroy it twice.
        using var scratch = (VulkanBuffer)device.CreateBuffer(new BufferDesc(
            1024,
            BufferUsage.Storage,
            BufferMemory.DeviceLocal,
            "SmokeTest deferred buffer"));

        var pendingBefore = device.Core.DeletionQueue.PendingCount;
        device.DeferredDestroy(scratch);

        checks.Add(("deletion: DeferredDestroy queues rather than destroys",
            device.Core.DeletionQueue.PendingCount > pendingBefore));
        checks.Add(("deletion: the resource gave up its handle to the queue", scratch.Handle.Handle == 0));

        device.WaitIdle();

        checks.Add(("deletion: the queue drains once the device is idle", device.Core.DeletionQueue.PendingCount == 0));

        using (device.DebugScope("SmokeTest scope"))
        {
            checks.Add(("device: a debug scope opens and closes", true));
        }

        checks.Add(("device: reports the Vulkan backend", device.Backend == RhiBackend.Vulkan));
    }

    private static void CopyImageToBuffer(
        VulkanDevice device,
        CommandBuffer command,
        VulkanTexture texture,
        VulkanBuffer destination,
        int mipLevel,
        int arrayLayer)
    {
        var (width, height, depth) = texture.MipExtent(mipLevel);

        var copy = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = texture.Aspect,
                MipLevel = (uint)mipLevel,
                BaseArrayLayer = (uint)arrayLayer,
                LayerCount = 1,
            },
            ImageOffset = default,
            ImageExtent = new Extent3D((uint)width, (uint)height, (uint)depth),
        };

        device.Core.Api.CmdCopyImageToBuffer(
            command,
            texture.Handle,
            ImageLayout.TransferSrcOptimal,
            destination.Handle,
            1,
            &copy);
    }

    /// <summary>
    /// Records one deliberately invalid command so the validation layer has something to report.
    /// </summary>
    /// <remarks>
    /// The command is recorded and then thrown away rather than submitted. Both violations are caught
    /// by the layer at record time, so submitting adds nothing and would mean deliberately running a
    /// command whose behaviour is undefined on real hardware.
    /// </remarks>
    private static void Provoke(VulkanDevice device, VulkanSmokeTestProvocation provocation)
    {
        var api = device.Core.Api;

        using var pool = new VulkanCommandPool(
            api,
            device.Core.Handle,
            device.Core.GraphicsQueueFamily,
            device.Core.DebugNames,
            "SmokeTest provocation pool");

        var command = pool.Acquire("SmokeTest provocation");

        // Every resource outlives vkEndCommandBuffer. Destroying one while it is still bound to a
        // recording command buffer is itself a validation error, and a provocation that raises two
        // errors cannot show which one the layer was meant to catch.
        using var source = (VulkanBuffer)device.CreateBuffer(new BufferDesc(
            4096,
            BufferUsage.CopySource,
            BufferMemory.HostUpload,
            "Provocation source"));

        using var tiny = (VulkanBuffer)device.CreateBuffer(new BufferDesc(
            256,
            BufferUsage.CopyDestination,
            BufferMemory.DeviceLocal,
            "Provocation tiny destination"));

        using var texture = (VulkanTexture)device.CreateTexture(new TextureDesc(
            16,
            16,
            RhiFormat.R8G8B8A8_UNorm,
            TextureUsage.CopyDestination | TextureUsage.Sampled,
            "Provocation image"));

        switch (provocation)
        {
            case VulkanSmokeTestProvocation.OversizedCopy:
                {
                    // 4096 bytes into a 256 byte buffer.
                    var copy = new BufferCopy { SrcOffset = 0, DstOffset = 0, Size = 4096 };
                    api.CmdCopyBuffer(command, source.Handle, tiny.Handle, 1, &copy);
                    break;
                }

            case VulkanSmokeTestProvocation.WrongImageLayout:
                {
                    var copy = new BufferImageCopy
                    {
                        BufferOffset = 0,
                        ImageSubresource = new ImageSubresourceLayers
                        {
                            AspectMask = ImageAspectFlags.ColorBit,
                            MipLevel = 0,
                            BaseArrayLayer = 0,
                            LayerCount = 1,
                        },
                        ImageExtent = new Extent3D(16, 16, 1),
                    };

                    // The image is in Undefined and this names ShaderReadOnlyOptimal, which is neither its
                    // real layout nor a layout a transfer destination may be in.
                    api.CmdCopyBufferToImage(
                        command,
                        source.Handle,
                        texture.Handle,
                        ImageLayout.ShaderReadOnlyOptimal,
                        1,
                        &copy);
                    break;
                }

            default:
                break;
        }

        api.EndCommandBuffer(command).Check("vkEndCommandBuffer");
    }

    private static bool Throws<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }
}
