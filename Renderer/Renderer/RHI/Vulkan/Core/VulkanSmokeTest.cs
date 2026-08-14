using System.Globalization;
using System.Text;
using Silk.NET.Vulkan;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Core;

/// <summary>What <see cref="VulkanSmokeTest.Run"/> observed.</summary>
/// <param name="Succeeded">Whether every stage passed and validation stayed silent.</param>
/// <param name="ValidationActive">Whether <c>VK_LAYER_KHRONOS_validation</c> was actually loaded. A
/// pass with this <see langword="false"/> proves only that the code ran, not that it is correct: the
/// layer ships with the Vulkan SDK and is absent on machines that only have a driver.</param>
/// <param name="DeviceName">The physical device that was used.</param>
/// <param name="BufferRoundTripped">Whether the buffer read back byte for byte.</param>
/// <param name="ImageRoundTripped">Whether the image read back byte for byte.</param>
/// <param name="ValidationErrors">Error-severity messages caused by this code's use of the API.
/// Loader complaints about third-party overlay layers installed on the machine are excluded; see
/// <see cref="VulkanInstance.ValidationErrorCount"/>.</param>
/// <param name="ValidationWarnings">Warning-severity messages caused by this code's use of the API.</param>
/// <param name="Messages">Every message the debug messenger delivered, including the loader's.</param>
/// <param name="Statistics">Allocator state at the end, before teardown.</param>
public readonly record struct VulkanSmokeTestResult(
    bool Succeeded,
    bool ValidationActive,
    string DeviceName,
    bool BufferRoundTripped,
    bool ImageRoundTripped,
    int ValidationErrors,
    int ValidationWarnings,
    IReadOnlyList<string> Messages,
    VulkanMemoryStatistics Statistics)
{
    /// <summary>Renders the result as a short report.</summary>
    /// <returns>The report text.</returns>
    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"Vulkan smoke test: {(Succeeded ? "PASS" : "FAIL")}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"  device            {DeviceName}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture,
            $"  validation layer  {(ValidationActive ? "loaded" : "NOT LOADED - this run proves little")}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"  buffer round trip {BufferRoundTripped}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"  image round trip  {ImageRoundTripped}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"  validation        {ValidationErrors} errors, {ValidationWarnings} warnings");
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
/// Stands the whole core up and tears it back down: device creation, a device-local buffer and an
/// optimally tiled image, an upload through the ring, a read back, and a byte comparison, with the
/// validation layer required to stay completely silent.
/// </summary>
/// <remarks>
/// <para>
/// This is the acceptance test for the bring-up. It is written against nothing but this directory and
/// the frozen contract's format tables, so it can run before any <see cref="IDevice"/> implementation
/// exists, and it exercises every piece that a backend depends on being correct: instance and device
/// creation, physical device selection, memory type selection, sub-allocation, the upload ring, the
/// frames-in-flight ring, timeline signalling and waiting, synchronization2 barriers, dynamic-rendering
/// era layout transitions, debug naming, and the deletion queue.
/// </para>
/// <para>
/// A tolerated validation warning becomes an unreproducible crash later, so
/// <see cref="VulkanSmokeTestResult.Succeeded"/> requires the counters to be exactly zero rather than
/// merely free of errors.
/// </para>
/// </remarks>
public static unsafe class VulkanSmokeTest
{
    private const int BufferBytes = 64 * 1024;
    private const int ImageWidth = 64;
    private const int ImageHeight = 64;
    private const RhiFormat ImageFormat = RhiFormat.R8G8B8A8_UNorm;

    /// <summary>Runs the smoke test.</summary>
    /// <param name="options">Overrides, or <see langword="null"/> for validation-on defaults.</param>
    /// <returns>What happened.</returns>
    public static VulkanSmokeTestResult Run(VulkanCoreOptions? options = null)
    {
        var messages = new List<string>();

        var effective = (options ?? new VulkanCoreOptions()) with
        {
            EnableValidation = options?.EnableValidation ?? true,
            EnableSynchronizationValidation = options?.EnableSynchronizationValidation ?? true,
            ApplicationName = "VulkanSmokeTest",
            MessageCallback = (severity, message) =>
            {
                lock (messages)
                {
                    messages.Add($"{severity}: {message}");
                }
            },
        };

        using var device = new VulkanCoreDevice(effective);

        var bufferOk = false;
        var imageOk = false;
        VulkanMemoryStatistics statistics = default;

        device.BeginFrame();

        var pool = device.FrameRing.CurrentPool;
        var command = pool.Acquire("VulkanSmokeTest");

        BufferProbe buffer;
        ImageProbe image;

        // The scope must close while the command buffer is still recording: vkCmdEndDebugUtilsLabelEXT
        // is a command like any other, so letting a using block run it after vkEndCommandBuffer is a
        // validation error rather than a formality.
        using (device.DebugNames.Scope(command, "Smoke test"))
        {
            buffer = new BufferProbe(device, command);
            image = new ImageProbe(device, command);
        }

        device.Api.EndCommandBuffer(command).Check("vkEndCommandBuffer");
        device.SubmitAndSignal(command);
        device.EndFrame();

        device.FrameRing.WaitForSerial(device.FrameRing.CurrentSerial);

        bufferOk = buffer.Verify(device);
        imageOk = image.Verify(device);

        statistics = device.Allocator.Statistics;

        buffer.Destroy(device);
        image.Destroy(device);

        // The deletion queue only releases once the frame that could reference the resources has
        // retired, so the test drains it explicitly rather than leaking into Dispose.
        device.WaitIdle();
        device.DeletionQueue.Collect(device.FrameRing.CompletedSerial);

        var errors = device.Instance.ValidationErrorCount;
        var warnings = device.Instance.ValidationWarningCount;

        return new VulkanSmokeTestResult(
            bufferOk && imageOk && errors == 0 && warnings == 0,
            device.ValidationEnabled,
            device.Adapter.Name,
            bufferOk,
            imageOk,
            errors,
            warnings,
            messages,
            statistics);
    }

    private static Silk.NET.Vulkan.Buffer CreateBuffer(
        VulkanCoreDevice device,
        ulong size,
        BufferUsageFlags usage,
        BufferMemory memory,
        string name,
        out VulkanAllocation allocation)
    {
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };

        device.Api.CreateBuffer(device.Handle, &info, null, out var buffer).Check("vkCreateBuffer");
        device.DebugNames.SetName(buffer, name);

        allocation = device.Allocator.AllocateForBuffer(buffer, memory, name);
        return buffer;
    }

    private static void Barrier(VulkanCoreDevice device, CommandBuffer command, in ImageMemoryBarrier2 barrier)
    {
        var local = barrier;

        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &local,
        };

        device.Api.CmdPipelineBarrier2(command, &dependency);
    }

    private static void Barrier(VulkanCoreDevice device, CommandBuffer command, in BufferMemoryBarrier2 barrier)
    {
        var local = barrier;

        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1,
            PBufferMemoryBarriers = &local,
        };

        device.Api.CmdPipelineBarrier2(command, &dependency);
    }

    private sealed class BufferProbe
    {
        private readonly Silk.NET.Vulkan.Buffer Device_;
        private readonly VulkanAllocation DeviceAllocation;
        private readonly Silk.NET.Vulkan.Buffer Readback;
        private readonly VulkanAllocation ReadbackAllocation;
        private readonly byte[] Expected = new byte[BufferBytes];

        public BufferProbe(VulkanCoreDevice device, CommandBuffer command)
        {
            for (var i = 0; i < Expected.Length; i++)
            {
                Expected[i] = (byte)(i * 31 + 7);
            }

            // Staged through the upload ring, which is what a real per-frame upload does.
            var staging = device.UploadRing.Write<byte>(Expected);

            Device_ = CreateBuffer(
                device,
                BufferBytes,
                BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.StorageBufferBit,
                BufferMemory.DeviceLocal,
                "SmokeTest device buffer",
                out DeviceAllocation);

            Readback = CreateBuffer(
                device,
                BufferBytes,
                BufferUsageFlags.TransferDstBit,
                BufferMemory.HostReadback,
                "SmokeTest readback buffer",
                out ReadbackAllocation);

            var upload = new BufferCopy { SrcOffset = staging.Offset, DstOffset = 0, Size = BufferBytes };
            device.Api.CmdCopyBuffer(command, staging.Buffer, Device_, 1, &upload);

            Barrier(device, command, new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.AllTransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.AllTransferBit,
                DstAccessMask = AccessFlags2.TransferReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = Device_,
                Offset = 0,
                Size = Vk.WholeSize,
            });

            var download = new BufferCopy { SrcOffset = 0, DstOffset = 0, Size = BufferBytes };
            device.Api.CmdCopyBuffer(command, Device_, Readback, 1, &download);
        }

        public bool Verify(VulkanCoreDevice device)
        {
            device.Allocator.Invalidate(ReadbackAllocation);
            return ReadbackAllocation.AsSpan()[..BufferBytes].SequenceEqual(Expected);
        }

        public void Destroy(VulkanCoreDevice device)
        {
            var serial = device.FrameRing.CurrentSerial;
            device.DeletionQueue.Enqueue(serial, Device_, DeviceAllocation, device.Allocator);
            device.DeletionQueue.Enqueue(serial, Readback, ReadbackAllocation, device.Allocator);
        }
    }

    private sealed class ImageProbe
    {
        private const int Bytes = ImageWidth * ImageHeight * 4;

        private readonly Image Image_;
        private readonly VulkanAllocation ImageAllocation;
        private readonly Silk.NET.Vulkan.Buffer Readback;
        private readonly VulkanAllocation ReadbackAllocation;
        private readonly byte[] Expected = new byte[Bytes];

        public ImageProbe(VulkanCoreDevice device, CommandBuffer command)
        {
            for (var i = 0; i < Expected.Length; i++)
            {
                Expected[i] = (byte)(i * 17 + 3);
            }

            var staging = device.UploadRing.Write<byte>(Expected);

            var info = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = FormatTables.ToVkFormat(ImageFormat),
                Extent = new Extent3D(ImageWidth, ImageHeight, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };

            device.Api.CreateImage(device.Handle, &info, null, out Image_).Check("vkCreateImage");
            device.DebugNames.SetName(Image_, "SmokeTest image");
            ImageAllocation = device.Allocator.AllocateForImage(Image_, "SmokeTest image");

            Readback = CreateBuffer(
                device,
                Bytes,
                BufferUsageFlags.TransferDstBit,
                BufferMemory.HostReadback,
                "SmokeTest image readback",
                out ReadbackAllocation);

            var range = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };

            Barrier(device, command, new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,

                // Nothing has touched the image yet, so there is no source stage to wait on. None is
                // the synchronization2 spelling of that; TopOfPipe would be a legacy no-op with the
                // same meaning but is discouraged in sync2 barriers.
                SrcStageMask = PipelineStageFlags2.None,
                SrcAccessMask = AccessFlags2.None,
                DstStageMask = PipelineStageFlags2.AllTransferBit,
                DstAccessMask = AccessFlags2.TransferWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = Image_,
                SubresourceRange = range,
            });

            var upload = new BufferImageCopy
            {
                BufferOffset = staging.Offset,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                ImageOffset = default,
                ImageExtent = new Extent3D(ImageWidth, ImageHeight, 1),
            };

            device.Api.CmdCopyBufferToImage(command, staging.Buffer, Image_, ImageLayout.TransferDstOptimal, 1, &upload);

            Barrier(device, command, new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.AllTransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.AllTransferBit,
                DstAccessMask = AccessFlags2.TransferReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = Image_,
                SubresourceRange = range,
            });

            var download = upload with { BufferOffset = 0 };
            device.Api.CmdCopyImageToBuffer(command, Image_, ImageLayout.TransferSrcOptimal, Readback, 1, &download);
        }

        public bool Verify(VulkanCoreDevice device)
        {
            device.Allocator.Invalidate(ReadbackAllocation);
            return ReadbackAllocation.AsSpan()[..Bytes].SequenceEqual(Expected);
        }

        public void Destroy(VulkanCoreDevice device)
        {
            var serial = device.FrameRing.CurrentSerial;
            device.DeletionQueue.Enqueue(serial, Image_, ImageAllocation, device.Allocator);
            device.DeletionQueue.Enqueue(serial, Readback, ReadbackAllocation, device.Allocator);
        }
    }
}
