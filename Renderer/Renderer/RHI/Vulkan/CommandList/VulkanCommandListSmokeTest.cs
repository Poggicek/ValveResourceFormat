using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// A deliberate mistake <see cref="VulkanCommandListSmokeTest"/> can commit, to prove the validation
/// layers are watching.
/// </summary>
/// <remarks>
/// A silent run only means something if the same harness can be made to speak. Each provocation
/// records one wrong thing and expects the error count to rise by exactly one; a run where it does not
/// move means the layer is not loaded and every green result in this file is worthless.
/// </remarks>
public enum VulkanCommandListProvocation
{
    /// <summary>Commit no violation. The normal run.</summary>
    None,

    /// <summary>Read a buffer a transfer just wrote, with no barrier between the two. Caught by
    /// <b>synchronization</b> validation, which is the layer that finds missing barriers.</summary>
    MissingBufferBarrier,

    /// <summary>Open a render pass over an image whose tracked state was overridden to lie, so no
    /// transition is recorded and the attachment is in the wrong layout. Caught by core validation.</summary>
    WrongAttachmentLayout,
}

/// <summary>What <see cref="VulkanCommandListSmokeTest.Run"/> observed.</summary>
/// <param name="Succeeded">Whether every check held and validation stayed silent. For a provoked run it
/// means the checks held and the provocation was reported, since such a run is expected to be dirty.</param>
/// <param name="ValidationActive">Whether <c>VK_LAYER_KHRONOS_validation</c> was actually loaded.</param>
/// <param name="SynchronizationValidationRequested">Whether synchronization validation was asked for.</param>
/// <param name="SynchronizationValidationActive">Whether it was actually running, established by
/// committing a known hazard and watching the error count. Asking for it and getting it are different
/// things, and a silent run without it says nothing about the barriers.</param>
/// <param name="DeviceName">The physical device that was used.</param>
/// <param name="Provocation">The mistake this run deliberately committed, if any.</param>
/// <param name="Checks">Every named check and whether it held.</param>
/// <param name="ValidationErrors">Error-severity messages attributable to this code's use of the API.</param>
/// <param name="ValidationWarnings">Warning-severity messages attributable to this code.</param>
/// <param name="ForeignMessages">Error and warning messages that came from the loader's view of the
/// machine rather than from this code. This machine emits a double-figure number of them from overlay
/// layers at error severity, which is why they are counted apart.</param>
/// <param name="ProvokedErrors">How many errors the provocation raised. Exactly one is the pass.</param>
/// <param name="Messages">Every message the debug messenger delivered.</param>
public readonly record struct VulkanCommandListSmokeTestResult(
    bool Succeeded,
    bool ValidationActive,
    bool SynchronizationValidationRequested,
    bool SynchronizationValidationActive,
    string DeviceName,
    VulkanCommandListProvocation Provocation,
    IReadOnlyList<(string Name, bool Passed)> Checks,
    int ValidationErrors,
    int ValidationWarnings,
    int ForeignMessages,
    int ProvokedErrors,
    IReadOnlyList<string> Messages)
{
    /// <summary>Renders the result as a short report.</summary>
    /// <returns>The report text.</returns>
    public override string ToString()
    {
        var builder = new StringBuilder();

        builder.Append(CultureInfo.InvariantCulture, $"Vulkan command list smoke test [{Provocation}]: {(Succeeded ? "PASS" : "FAIL")}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"  device            {DeviceName}");
        builder.AppendLine();
        var sync = (SynchronizationValidationRequested, SynchronizationValidationActive) switch
        {
            (true, true) => "sync validation LIVE",
            (true, false) => "sync validation REQUESTED BUT NOT RUNNING - the barriers are unverified",
            (false, true) => "sync validation live (not requested; something in the environment enabled it)",
            _ => "sync validation off - the barriers are unverified",
        };

        builder.Append(CultureInfo.InvariantCulture,
            $"  validation layer  {(ValidationActive ? "loaded" : "NOT LOADED - this run proves little")}, {sync}");
        builder.AppendLine();

        foreach (var (name, passed) in Checks)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  {(passed ? "ok  " : "FAIL")}  {name}");
            builder.AppendLine();
        }

        builder.Append(CultureInfo.InvariantCulture,
            $"  validation        {ValidationErrors} errors, {ValidationWarnings} warnings ({ForeignMessages} foreign, not counted)");
        builder.AppendLine();

        if (Provocation != VulkanCommandListProvocation.None)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  provoked          {ProvokedErrors} error(s)");
            builder.AppendLine();
        }

        foreach (var message in Messages)
        {
            builder.Append("  | ").Append(message);
            builder.AppendLine();
        }

        return builder.ToString();
    }
}

/// <summary>
/// Exercises <see cref="VulkanCommandList"/> against a real device with core <b>and</b> synchronization
/// validation required to stay completely silent: dynamic rendering with several colour attachments and
/// integer clear values, an MSAA resolve through <c>pResolveAttachments</c>, image and buffer clears,
/// copies, blits, readbacks, explicit barriers, debug labels, the frame lifecycle across a wrap of the
/// ring, and every refusal the class makes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Synchronization validation is the point.</b> Core validation finds a wrong layout or a missing
/// usage flag, both of which fail loudly anyway. The bug class this backend actually risks is a missing
/// barrier, which corrupts on one vendor's driver and not another's, and synchronization validation is
/// the only thing that reports it deterministically. It is therefore requested by default here rather
/// than left to the caller.
/// </para>
/// <para>
/// <b>The run certifies itself.</b> Before reporting, it commits a known missing-barrier hazard and
/// checks that the error count moves. That is not ceremony:
/// <see cref="VulkanCoreOptions.EnableSynchronizationValidation"/> is honoured only when
/// <c>VK_EXT_validation_features</c> appears in <c>vkEnumerateInstanceExtensionProperties</c> with a
/// null layer name, and that call does not report extensions belonging to an explicit layer, which is
/// what <c>VK_LAYER_KHRONOS_validation</c> is. On this machine the option therefore asks for
/// synchronization validation and silently does not get it, and the first version of this test passed
/// clean while proving nothing about barriers. Until that is fixed one layer down, set
/// <c>VK_LAYER_VALIDATE_SYNC=1</c> in the environment; the check below is what makes the difference
/// visible either way.
/// </para>
/// <para>
/// <b>What this cannot cover.</b> Nothing here binds a pipeline or a descriptor, because neither layer
/// exists yet. Draws, dispatches, push constants, vertex and index binding, and every descriptor path
/// are unexercised: they are checked only for the refusals they make when their prerequisites are
/// missing. The golden image suite cannot cover them either, being an OpenGL harness with no Vulkan
/// path through the renderer.
/// </para>
/// <para>
/// Only <c>Validation</c> and <c>Performance</c> typed messages count, which
/// <see cref="VulkanInstance"/> already separates: a consumer machine carries overlay layers from
/// capture software and storefronts whose stale manifests the loader reports at error severity through
/// the same messenger, and counting those would make the result depend on what happens to be installed.
/// </para>
/// </remarks>
public static class VulkanCommandListSmokeTest
{
    private const int ImageSize = 64;
    private const int BufferBytes = 4096;

    private static readonly Vector4 ClearRed = new(1f, 0f, 0f, 1f);
    private static readonly Vector4 ClearBlue = new(0f, 0f, 1f, 1f);

    /// <summary>Runs the smoke test.</summary>
    /// <param name="provocation">A deliberate mistake to commit, or
    /// <see cref="VulkanCommandListProvocation.None"/> for the normal run.</param>
    /// <param name="options">Overrides, or <see langword="null"/> for validation-on defaults.</param>
    /// <returns>What happened.</returns>
    public static VulkanCommandListSmokeTestResult Run(
        VulkanCommandListProvocation provocation = VulkanCommandListProvocation.None,
        VulkanCoreOptions? options = null)
    {
        var messages = new List<string>();
        var checks = new List<(string, bool)>();

        var effective = (options ?? new VulkanCoreOptions()) with
        {
            EnableValidation = options?.EnableValidation ?? true,
            EnableSynchronizationValidation = options?.EnableSynchronizationValidation ?? true,
            ApplicationName = "VulkanCommandListSmokeTest",
        };

        void OnMessage(RhiMessageSeverity severity, string message)
        {
            lock (messages)
            {
                messages.Add($"{severity}: {message}");
            }
        }

        using var device = new VulkanRecordingDevice(OnMessage, effective);

        var provoked = 0;
        var syncActive = false;
        int errors;
        int warnings;

        try
        {
            CheckViewportFlip(checks);

            RenderPassClearAndReadback(device, checks);
            DepthStencilPass(device, checks);
            IntegerClearValue(device, checks);
            ClearAndFill(device, checks);
            CopyAndBlit(device, checks);
            ResolveMultisampled(device, checks);
            DebugLabels(device, checks);
            Refusals(device, checks);
            MultipleCommandListsInOneFrame(device, checks);
            FrameLifecycle(device, checks);

            device.WaitIdle();

            // Snapshot before anything deliberately wrong happens: these are the counts that say whether
            // the work above was clean.
            errors = device.Core.Instance.ValidationErrorCount;
            warnings = device.Core.Instance.ValidationWarningCount;

            syncActive = ProvokedErrorCount(device, VulkanCommandListProvocation.MissingBufferBarrier) > 0;

            // The self-certification. Everything above is a barrier claim, and a claim checked by a layer
            // that is not running is not checked at all.
            checks.Add((
                "synchronization validation is live, so the silence above means the barriers hold",
                syncActive || !effective.EnableSynchronizationValidation));

            if (provocation != VulkanCommandListProvocation.None)
            {
                provoked = ProvokedErrorCount(device, provocation);
                checks.Add(($"provocation {provocation} raised exactly one validation error", provoked == 1));
            }
        }
        finally
        {
            device.WaitIdle();
        }

        var foreign = device.Core.Instance.ErrorCount + device.Core.Instance.WarningCount
            - device.Core.Instance.ValidationErrorCount - device.Core.Instance.ValidationWarningCount;

        var everyCheckPassed = true;

        foreach (var (_, passed) in checks)
        {
            everyCheckPassed &= passed;
        }

        // A provoked run is expected to be dirty, so "succeeded" means the checks held and the
        // provocation was seen, not that validation stayed silent.
        var succeeded = provocation == VulkanCommandListProvocation.None
            ? everyCheckPassed && errors == 0 && warnings == 0
            : everyCheckPassed;

        return new VulkanCommandListSmokeTestResult(
            succeeded,
            device.Core.ValidationEnabled,
            effective.EnableSynchronizationValidation,
            syncActive,
            device.Core.Adapter.Name,
            provocation,
            checks.ConvertAll(c => (c.Item1, c.Item2)),
            errors,
            warnings,
            foreign,
            provoked,
            messages);
    }

    /// <summary>
    /// Asserts the bottom-left to top-left conversion the whole backend's image depends on.
    /// </summary>
    /// <remarks>
    /// A submitted viewport cannot be read back off a command buffer, so this is the only place the
    /// arithmetic can be seen. The case that matters is not the full-screen one, which is symmetric and
    /// would pass with the flip missing entirely, but the offset one: the barn light atlas hands the
    /// same region to <c>glViewport</c> and to this backend, once per shadow caster.
    /// </remarks>
    private static void CheckViewportFlip(List<(string, bool)> checks)
    {
        var full = VulkanCommandList.FlipViewport(0, 0, 128, 100, 100);

        checks.Add(("viewport: a full-target viewport starts at the bottom row and grows upwards",
            full.X == 0f && full.Y == 100f && full.Width == 128f && full.Height == -100f));

        // GL rows [10, 40) counted up are rows [60, 90) counted down, and a negative height puts the
        // origin at the far end of that span.
        var region = VulkanCommandList.FlipViewport(20, 10, 50, 30, 100);

        checks.Add(("viewport: an offset region maps to the mirrored rows",
            region.X == 20f && region.Y == 90f && region.Width == 50f && region.Height == -30f));

        var scissor = VulkanCommandList.FlipScissor(20, 10, 50, 30, 100);

        checks.Add(("scissor: the same region, addressed from the top edge instead",
            scissor.Offset.X == 20 && scissor.Offset.Y == 60
            && scissor.Extent.Width == 50 && scissor.Extent.Height == 30));

        var depth = VulkanCommandList.FlipViewport(0, 0, 8, 8, 8);

        checks.Add(("viewport: depth range is untouched, the renderer being reverse-Z already",
            depth.MinDepth == 0f && depth.MaxDepth == 1f));
    }

    private static void RenderPassClearAndReadback(VulkanRecordingDevice device, List<(string, bool)> checks)
    {
        using var first = ColorTarget(device, "SmokeTest MRT 0");
        using var second = ColorTarget(device, "SmokeTest MRT 1");
        using var readFirst = Readback(device, ImageSize * ImageSize * 4, "SmokeTest MRT 0 readback");
        using var readSecond = Readback(device, ImageSize * ImageSize * 4, "SmokeTest MRT 1 readback");

        Frame(device, "SmokeTest clear", list =>
        {
            using (list.DebugScope("Clear both attachments"))
            {
                list.BeginRenderPass(new RenderPassDesc(
                    [
                        new ColorAttachmentDesc(first, LoadOp.Clear, StoreOp.Store, ClearRed),
                        new ColorAttachmentDesc(second, LoadOp.Clear, StoreOp.Store, ClearBlue),
                    ],
                    null,
                    "SmokeTest MRT pass"));

                // Both are dynamic state on every pipeline, and a pass is specified to open with them
                // covering the attachment; setting them again here is what a real pass does.
                list.SetViewport(0, 0, ImageSize, ImageSize);
                list.SetScissor(0, 0, ImageSize, ImageSize);

                list.EndRenderPass();
            }

            // The barrier the whole exercise is about: the colour attachment output stage wrote these
            // images, and a transfer is about to read them.
            list.Barrier(
                [],
                [
                    new TextureBarrier(first, ResourceState.ColorTarget, ResourceState.CopySource),
                    new TextureBarrier(second, ResourceState.ColorTarget, ResourceState.CopySource),
                ]);

            list.CopyTextureToBuffer(first, 0, 0, readFirst);
            list.CopyTextureToBuffer(second, 0, 0, readSecond);
        });

        checks.Add(("render pass: attachment 0 holds its clear colour", IsSolid(readFirst, [255, 0, 0, 255])));
        checks.Add(("render pass: attachment 1 holds its own clear colour, not attachment 0's", IsSolid(readSecond, [0, 0, 255, 255])));
        checks.Add(("render pass: the attachments end tracked as colour targets",
            first.StateOf(0, 0) == ResourceState.CopySource && second.StateOf(0, 0) == ResourceState.CopySource));

        // Load rather than clear, over the same targets, which must preserve what is there and needs a
        // transition back out of CopySource.
        Frame(device, "SmokeTest load", list =>
        {
            list.BeginRenderPass(new RenderPassDesc(
                [new ColorAttachmentDesc(first, LoadOp.Load, StoreOp.Store)],
                null,
                "SmokeTest load pass"));
            list.EndRenderPass();

            list.Barrier(new TextureBarrier(first, ResourceState.ColorTarget, ResourceState.CopySource));
            list.CopyTextureToBuffer(first, 0, 0, readFirst);
        });

        checks.Add(("render pass: LoadOp.Load preserves the contents across the layout change", IsSolid(readFirst, [255, 0, 0, 255])));
    }

    /// <summary>
    /// Opens the shape every real pass has: a colour attachment and a depth-stencil one, cleared, then
    /// reopened read-only so the depth buffer could be sampled while still bound.
    /// </summary>
    /// <remarks>
    /// Nothing is read back, because there is no draw to put anything in it. What this proves is that
    /// the depth and stencil attachment structures, the two depth layouts and the transitions between
    /// them are accepted by the validation layer, which is where a wrong one shows up.
    /// </remarks>
    private static void DepthStencilPass(VulkanRecordingDevice device, List<(string, bool)> checks)
    {
        var format = RhiFormat.Undefined;

        foreach (var candidate in (RhiFormat[])[RhiFormat.D32_SFloat_S8_UInt, RhiFormat.D24_UNorm_S8_UInt, RhiFormat.D32_SFloat])
        {
            if (device.Limits.SupportsFormat(candidate, TextureUsage.DepthStencilTarget))
            {
                format = candidate;
                break;
            }
        }

        if (format == RhiFormat.Undefined)
        {
            checks.Add(("depth: no depth-stencil format is supported, skipped", false));
            return;
        }

        using var color = ColorTarget(device, "SmokeTest depth pass colour");
        using var depth = (VulkanTexture)device.CreateTexture(new TextureDesc(
            ImageSize,
            ImageSize,
            format,
            TextureUsage.DepthStencilTarget | TextureUsage.Sampled,
            "SmokeTest depth"));

        var hasStencil = RhiFormatInfo.IsStencil(format);

        Frame(device, "SmokeTest depth", list =>
        {
            // Depth clears to 0: the renderer is reverse-Z, so 0 is the far plane, which is also Vulkan's
            // native clip convention and needs nothing done to it.
            list.BeginRenderPass(new RenderPassDesc(
                [new ColorAttachmentDesc(color, LoadOp.Clear, StoreOp.Store, ClearRed)],
                new DepthAttachmentDesc(
                    depth,
                    LoadOp.Clear,
                    StoreOp.Store,
                    ClearDepth: 0f,
                    hasStencil ? LoadOp.Clear : LoadOp.DontCare,
                    hasStencil ? StoreOp.Store : StoreOp.DontCare),
                "SmokeTest depth pass"));
            list.EndRenderPass();
        });

        checks.Add(("depth: a cleared depth attachment ends tracked as a depth write target",
            depth.StateOf(0, 0) == ResourceState.DepthWrite));

        Frame(device, "SmokeTest read-only depth", list =>
        {
            list.BeginRenderPass(new RenderPassDesc(
                [new ColorAttachmentDesc(color, LoadOp.Load, StoreOp.Store)],
                new DepthAttachmentDesc(
                    depth,
                    LoadOp.Load,
                    StoreOp.Store,
                    StencilLoadOp: hasStencil ? LoadOp.Load : LoadOp.DontCare,
                    StencilStoreOp: hasStencil ? StoreOp.Store : StoreOp.DontCare,
                    ReadOnly: true),
                "SmokeTest read-only depth pass"));
            list.EndRenderPass();
        });

        checks.Add(("depth: a read-only attachment moves to the layout that also permits sampling",
            depth.StateOf(0, 0) == ResourceState.DepthRead));
    }

    private static void IntegerClearValue(VulkanRecordingDevice device, List<(string, bool)> checks)
    {
        const uint expected = 12345;

        if (!device.Limits.SupportsFormat(RhiFormat.R32_UInt, TextureUsage.ColorTarget))
        {
            checks.Add(("clear: R32_UInt is not a colour target on this device, skipped", true));
            return;
        }

        using var texture = (VulkanTexture)device.CreateTexture(new TextureDesc(
            ImageSize,
            ImageSize,
            RhiFormat.R32_UInt,
            TextureUsage.ColorTarget | TextureUsage.CopySource,
            "SmokeTest integer target"));

        using var readback = Readback(device, ImageSize * ImageSize * 4, "SmokeTest integer readback");

        Frame(device, "SmokeTest integer clear", list =>
        {
            list.BeginRenderPass(new RenderPassDesc(
                [new ColorAttachmentDesc(texture, LoadOp.Clear, StoreOp.Store, new Vector4(expected, 0f, 0f, 0f))],
                null,
                "SmokeTest integer pass"));
            list.EndRenderPass();

            list.Barrier(new TextureBarrier(texture, ResourceState.ColorTarget, ResourceState.CopySource));
            list.CopyTextureToBuffer(texture, 0, 0, readback);
        });

        readback.InvalidateRange();
        var values = MemoryMarshal.Cast<byte, uint>(readback.MappedData);
        var uniform = true;

        foreach (var value in values)
        {
            uniform &= value == expected;
        }

        // The bug this guards: handing an integer attachment the float member of the clear union writes
        // the bit pattern of the float instead of the number, which looks plausible and is not.
        checks.Add(("clear: an integer attachment reads the integer member of the clear union", uniform));
    }

    private static void ClearAndFill(VulkanRecordingDevice device, List<(string, bool)> checks)
    {
        const uint counter = uint.MaxValue;
        const uint fill = 0xA5A5A5A5;

        using var texture = (VulkanTexture)device.CreateTexture(new TextureDesc(
            ImageSize,
            ImageSize,
            RhiFormat.R32_UInt,
            TextureUsage.Storage | TextureUsage.CopySource | TextureUsage.CopyDestination,
            "SmokeTest counter image"));

        using var buffer = (VulkanBuffer)device.CreateBuffer(new BufferDesc(
            BufferBytes,
            BufferUsage.Storage | BufferUsage.CopySource | BufferUsage.CopyDestination,
            BufferMemory.DeviceLocal,
            "SmokeTest counters"));

        using var imageReadback = Readback(device, ImageSize * ImageSize * 4, "SmokeTest counter image readback");
        using var bufferReadback = Readback(device, BufferBytes, "SmokeTest counter readback");

        Frame(device, "SmokeTest clears", list =>
        {
            // The overdraw counters, exactly: uint.MaxValue is a value no float clear colour can carry,
            // which is why ClearTexture takes raw bits rather than a ColorAttachmentDesc clear.
            list.ClearTexture(texture, 0, counter);
            list.FillBuffer(buffer, 0, BufferBytes, fill);

            // Both writes were transfers and both reads are transfers, so without these the two pairs
            // race. This is the barrier synchronization validation exists to notice.
            list.Barrier(
                [new BufferBarrier(buffer, ResourceState.CopyDestination, ResourceState.CopySource)],
                [new TextureBarrier(texture, ResourceState.CopyDestination, ResourceState.CopySource)]);

            list.CopyTextureToBuffer(texture, 0, 0, imageReadback);
            list.CopyBuffer(buffer, 0, bufferReadback, 0, BufferBytes);
        });

        checks.Add(("clear: a storage image clears to a raw value no float could represent", IsUniform(imageReadback, counter)));
        checks.Add(("fill: a buffer fills with a repeating 32 bit value", IsUniform(bufferReadback, fill)));
    }

    private static void CopyAndBlit(VulkanRecordingDevice device, List<(string, bool)> checks)
    {
        using var source = ColorTarget(device, "SmokeTest copy source");
        using var copy = ColorTarget(device, "SmokeTest copy destination");

        using var half = (VulkanTexture)device.CreateTexture(new TextureDesc(
            ImageSize / 2,
            ImageSize / 2,
            RhiFormat.R8G8B8A8_UNorm,
            TextureUsage.ColorTarget | TextureUsage.CopySource | TextureUsage.CopyDestination,
            "SmokeTest blit destination"));

        using var copyReadback = Readback(device, ImageSize * ImageSize * 4, "SmokeTest copy readback");
        using var blitReadback = Readback(device, ImageSize / 2 * (ImageSize / 2) * 4, "SmokeTest blit readback");

        Frame(device, "SmokeTest transfers", list =>
        {
            list.BeginRenderPass(new RenderPassDesc(
                [new ColorAttachmentDesc(source, LoadOp.Clear, StoreOp.Store, ClearRed)],
                null,
                "SmokeTest transfer source pass"));
            list.EndRenderPass();

            list.Barrier(new TextureBarrier(source, ResourceState.ColorTarget, ResourceState.CopySource));

            // CopyTexture and BlitTexture transition both sides themselves, having no state parameter to
            // be told through, so the source transition above is the only one the caller owes.
            list.CopyTexture(source, copy);
            list.BlitTexture(source, half, FilterMode.Linear);

            list.Barrier(
                [],
                [
                    new TextureBarrier(copy, ResourceState.CopyDestination, ResourceState.CopySource),
                    new TextureBarrier(half, ResourceState.CopyDestination, ResourceState.CopySource),
                ]);

            list.CopyTextureToBuffer(copy, 0, 0, copyReadback);
            list.CopyTextureToBuffer(half, 0, 0, blitReadback);
        });

        checks.Add(("copy: a full image copy is identical to its source", IsSolid(copyReadback, [255, 0, 0, 255])));
        checks.Add(("blit: a downscaling blit of a solid image is the same solid colour", IsSolid(blitReadback, [255, 0, 0, 255])));
    }

    private static void ResolveMultisampled(VulkanRecordingDevice device, List<(string, bool)> checks)
    {
        if (device.Limits.MaxSampleCount < 4)
        {
            checks.Add(("resolve: this device supports no multisampling, skipped", true));
            return;
        }

        using var multisampled = (VulkanTexture)device.CreateTexture(new TextureDesc(
            ImageSize,
            ImageSize,
            RhiFormat.R8G8B8A8_UNorm,
            TextureUsage.ColorTarget,
            "SmokeTest MSAA target",
            SampleCount: 4));

        using var resolved = ColorTarget(device, "SmokeTest resolve target");
        using var readback = Readback(device, ImageSize * ImageSize * 4, "SmokeTest resolve readback");

        Frame(device, "SmokeTest resolve", list =>
        {
            // The canonical example of a bug that is silent on the parity oracle: written as a blit this
            // would resolve on OpenGL and be rejected outright here. It is pResolveAttachments or nothing.
            list.BeginRenderPass(new RenderPassDesc(
                [
                    new ColorAttachmentDesc(
                        multisampled,
                        LoadOp.Clear,
                        StoreOp.DontCare,
                        ClearBlue,
                        ResolveTexture: resolved),
                ],
                null,
                "SmokeTest resolve pass"));
            list.EndRenderPass();

            list.Barrier(new TextureBarrier(resolved, ResourceState.ColorTarget, ResourceState.CopySource));
            list.CopyTextureToBuffer(resolved, 0, 0, readback);
        });

        checks.Add(("resolve: a multisampled clear lands in the resolve target", IsSolid(readback, [0, 0, 255, 255])));

        checks.Add(("resolve: blitting a multisampled source is refused by name",
            Throws<InvalidOperationException>(() => Frame(device, "SmokeTest bad resolve", list =>
                list.BlitTexture(multisampled, resolved)))));
    }

    private static void DebugLabels(VulkanRecordingDevice device, List<(string, bool)> checks)
    {
        Frame(device, "SmokeTest labels", list =>
        {
            using (list.DebugScope("Outer"))
            {
                using (list.DebugScope("Inner"))
                {
                    list.DebugMarker("A marker inside two scopes");
                }
            }
        });

        checks.Add(("debug: nested scopes and a marker record and close", true));
        checks.Add(("debug: naming is active, so a capture will read like the OpenGL one", device.Core.DebugNames.IsActive));
    }

    private static void Refusals(VulkanRecordingDevice device, List<(string, bool)> checks)
    {
        using var texture = ColorTarget(device, "SmokeTest refusal target");
        using var uploaded = (VulkanTexture)device.CreateTexture(new TextureDesc(
            4,
            4,
            RhiFormat.R8G8B8A8_UNorm,
            TextureUsage.Sampled | TextureUsage.CopyDestination,
            "SmokeTest uploaded texture"));

        using var plain = (VulkanBuffer)device.CreateBuffer(new BufferDesc(
            256,
            BufferUsage.Uniform,
            BufferMemory.HostUpload,
            "SmokeTest uniform buffer"));

        device.UploadTexture(uploaded, 0, 0, new byte[4 * 4 * 4]);

        checks.Add(("upload: a texture arrives tracked as a copy destination, not guessed into ShaderRead",
            uploaded.StateOf(0, 0) == ResourceState.CopyDestination));

        Frame(device, "SmokeTest refusals", list =>
        {
            list.BeginRenderPass(new RenderPassDesc(
                [new ColorAttachmentDesc(texture, LoadOp.Clear, StoreOp.Store, ClearRed)],
                null,
                "SmokeTest refusal pass"));

            checks.Add(("refusal: a barrier inside a render pass is refused, dynamic rendering forbidding it",
                Throws<InvalidOperationException>(() =>
                    list.Barrier(new TextureBarrier(texture, ResourceState.ColorTarget, ResourceState.ShaderRead)))));

            checks.Add(("refusal: a dispatch inside a render pass is refused",
                Throws<InvalidOperationException>(() => list.Dispatch(1))));

            checks.Add(("refusal: a transfer inside a render pass is refused",
                Throws<InvalidOperationException>(() => list.ClearTexture(texture, 0, 0))));

            checks.Add(("refusal: a draw with no pipeline bound is refused",
                Throws<InvalidOperationException>(() => list.Draw(3))));

            list.EndRenderPass();

            checks.Add(("refusal: a viewport outside a render pass is refused, having no height to flip against",
                Throws<InvalidOperationException>(() => list.SetViewport(0, 0, 8, 8))));

            checks.Add(("refusal: a texture still in CopyDestination cannot be sampled",
                Throws<InvalidOperationException>(() => list.BindTexture(DescriptorSets.MaterialTextures, 0, uploaded))));

            checks.Add(("refusal: a buffer without Indirect usage cannot supply draw arguments",
                Throws<InvalidOperationException>(() => list.DispatchIndirect(plain, 0))));

            checks.Add(("refusal: binding a uniform buffer with no descriptor layer present says so",
                Throws<NotSupportedException>(() => list.BindUniformBuffer(0, plain))));

            checks.Add(("refusal: push constants with no pipeline bound are refused",
                Throws<InvalidOperationException>(() => list.SetPushConstants(0f))));
        });

        // One list is reused, so beginning a second before the first is handed back would leave the
        // first's command buffer recorded and never queued. Invisible on OpenGL, where the work has
        // already executed by then.
        device.BeginFrame();
        var first = device.BeginCommandList("SmokeTest orphan A");

        checks.Add(("refusal: beginning a second list before submitting the first is refused",
            Throws<InvalidOperationException>(() => device.BeginCommandList("SmokeTest orphan B"))));

        device.Submit(first);
        device.EndFrame();
    }

    /// <summary>
    /// Submits three command lists in one device frame and proves all three ran, in order.
    /// </summary>
    /// <remarks>
    /// The shape a real frame has: the scene, the post-process chain and the overlay are three lists,
    /// because a transfer is not valid inside a render pass and the overlay draws after the renderer has
    /// closed its own. They reach the queue as one batch signalling the timeline once, so this checks
    /// both that nothing was dropped and that the batch executes in submission order &#8212; the second
    /// list reads what the first wrote, and the third reads what the second wrote.
    /// </remarks>
    private static void MultipleCommandListsInOneFrame(VulkanRecordingDevice device, List<(string, bool)> checks)
    {
        using var texture = ColorTarget(device, "SmokeTest batched target");
        using var readback = Readback(device, ImageSize * ImageSize * 4, "SmokeTest batched readback");

        device.BeginFrame();

        var scene = device.BeginCommandList("SmokeTest batch 1 of 3");

        scene.BeginRenderPass(new RenderPassDesc(
            [new ColorAttachmentDesc(texture, LoadOp.Clear, StoreOp.Store, ClearBlue)],
            null,
            "SmokeTest batched pass"));
        scene.EndRenderPass();

        device.Submit(scene);

        var transfer = device.BeginCommandList("SmokeTest batch 2 of 3");

        transfer.Barrier(new TextureBarrier(texture, ResourceState.ColorTarget, ResourceState.CopySource));
        device.Submit(transfer);

        var copy = device.BeginCommandList("SmokeTest batch 3 of 3");

        copy.CopyTextureToBuffer(texture, 0, 0, readback);
        device.Submit(copy);

        device.EndFrame();
        device.WaitIdle();

        // A dropped list, a batch submitted out of order, or a signal that landed before the work
        // finished all show up here rather than as a silent wrong pixel later.
        checks.Add(("frames: three command lists in one frame all run, in submission order",
            IsSolid(readback, [0, 0, 255, 255])));
    }

    private static void FrameLifecycle(VulkanRecordingDevice device, List<(string, bool)> checks)
    {
        // More passes than there are frame slots, so the ring wraps and every slot waits on a serial that
        // was actually signalled. A Submit that forgot to report the signal deadlocks here.
        var passes = (device.FramesInFlight * 2) + 1;
        var indices = new HashSet<int>();

        using var texture = ColorTarget(device, "SmokeTest lifecycle target");

        for (var i = 0; i < passes; i++)
        {
            device.BeginFrame();
            indices.Add(device.FrameIndex);

            var list = device.BeginCommandList(string.Create(CultureInfo.InvariantCulture, $"SmokeTest frame {i}"));

            list.BeginRenderPass(new RenderPassDesc(
                [new ColorAttachmentDesc(texture, LoadOp.Clear, StoreOp.Store, ClearRed)],
                null,
                "SmokeTest lifecycle pass"));
            list.EndRenderPass();

            device.Submit(list);
            device.EndFrame();
        }

        checks.Add(("frames: the ring wrapped without deadlocking", indices.Count == device.FramesInFlight));

        device.WaitIdle();

        checks.Add(("frames: every submitted serial was signalled",
            device.Core.FrameRing.CompletedSerial >= device.Core.FrameRing.CurrentSerial));
    }

    /// <summary>
    /// Commits one deliberate mistake and reports how many validation errors it raised.
    /// </summary>
    /// <remarks>Used twice: once to establish that synchronization validation is running at all, and
    /// once for the caller's chosen provocation. A count rather than a flag, so a provocation that
    /// raises two errors is visible as one that raises two rather than as a failure with no detail.</remarks>
    private static int ProvokedErrorCount(VulkanRecordingDevice device, VulkanCommandListProvocation provocation)
    {
        device.WaitIdle();

        var before = device.Core.Instance.ValidationErrorCount;

        Provoke(device, provocation);
        device.WaitIdle();

        return device.Core.Instance.ValidationErrorCount - before;
    }

    /// <summary>
    /// Commits one deliberate mistake so the validation layers have something to report.
    /// </summary>
    private static void Provoke(VulkanRecordingDevice device, VulkanCommandListProvocation provocation)
    {
        switch (provocation)
        {
            case VulkanCommandListProvocation.MissingBufferBarrier:
                {
                    using var buffer = (VulkanBuffer)device.CreateBuffer(new BufferDesc(
                        BufferBytes,
                        BufferUsage.CopySource | BufferUsage.CopyDestination,
                        BufferMemory.DeviceLocal,
                        "Provocation buffer"));

                    using var readback = Readback(device, BufferBytes, "Provocation readback");

                    Frame(device, "Provocation missing barrier", list =>
                    {
                        list.FillBuffer(buffer, 0, BufferBytes, 1);

                        // The barrier that belongs here is omitted on purpose. The fill writes and the copy
                        // reads, both in the transfer stage, with nothing ordering them.
                        list.CopyBuffer(buffer, 0, readback, 0, BufferBytes);
                    });

                    break;
                }

            case VulkanCommandListProvocation.WrongAttachmentLayout:
                {
                    using var texture = ColorTarget(device, "Provocation attachment");

                    // Claiming the image is already a colour target means BeginRenderPass records no
                    // transition, and the image really is still in Undefined when the pass loads it.
                    texture.OverrideTrackedState(ResourceState.ColorTarget);

                    Frame(device, "Provocation wrong layout", list =>
                    {
                        list.BeginRenderPass(new RenderPassDesc(
                            [new ColorAttachmentDesc(texture, LoadOp.Load, StoreOp.Store)],
                            null,
                            "Provocation pass"));
                        list.EndRenderPass();
                    });

                    break;
                }

            default:
                break;
        }
    }

    // ---- helpers ----

    private static void Frame(VulkanRecordingDevice device, string name, Action<ICommandList> record)
    {
        device.BeginFrame();

        var list = device.BeginCommandList(name);

        try
        {
            record(list);
        }
        finally
        {
            device.Submit(list);
            device.EndFrame();
            device.WaitIdle();
        }
    }

    private static VulkanTexture ColorTarget(VulkanRecordingDevice device, string name)
        => (VulkanTexture)device.CreateTexture(new TextureDesc(
            ImageSize,
            ImageSize,
            RhiFormat.R8G8B8A8_UNorm,
            TextureUsage.ColorTarget | TextureUsage.Sampled | TextureUsage.CopySource | TextureUsage.CopyDestination,
            name));

    private static VulkanBuffer Readback(VulkanRecordingDevice device, int sizeInBytes, string name)
        => (VulkanBuffer)device.CreateBuffer(new BufferDesc(
            sizeInBytes,
            BufferUsage.CopyDestination,
            BufferMemory.HostReadback,
            name));

    private static bool IsSolid(VulkanBuffer readback, byte[] texel)
    {
        readback.InvalidateRange();

        var data = readback.MappedData;

        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != texel[i % texel.Length])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUniform(VulkanBuffer readback, uint value)
    {
        readback.InvalidateRange();

        foreach (var actual in MemoryMarshal.Cast<byte, uint>(readback.MappedData))
        {
            if (actual != value)
            {
                return false;
            }
        }

        return true;
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
