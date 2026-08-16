using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Core;

/// <summary>
/// A ready-to-use Vulkan 1.3 device: instance, physical device, logical device, queues, allocator,
/// upload ring, frame ring and deletion queue, wired together.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not an <see cref="IDevice"/>. It is the backend core that an
/// <see cref="IDevice"/> implementation is built on, so that bring-up, the allocator and the frame
/// lifecycle can be developed and tested without waiting on the resource and command-list layers.
/// Everything the contract needs is exposed: <see cref="FrameIndex"/> and
/// <see cref="FramesInFlight"/> back their contract counterparts, <see cref="DeletionQueue"/> backs
/// <see cref="IDevice.DeferredDestroy"/>, and <see cref="DebugNames"/> backs both the descriptor debug
/// names and <see cref="IDevice.DebugScope"/>.
/// </para>
/// <para>
/// <b>Namespace note.</b> This namespace ends in <c>Vulkan</c>, the same last segment as
/// <c>Silk.NET.Vulkan</c>. That is survivable where the GL backend's <c>RHI.GL</c> was not, because
/// <c>Silk.NET.Vulkan</c> exposes no type named <c>Vulkan</c> for the namespace to shadow. It does
/// collide on <c>Buffer</c>, which is both <c>Silk.NET.Vulkan.Buffer</c> and the globally imported
/// <c>System.Buffer</c>, so buffer handles in this directory are always written out as
/// <c>Silk.NET.Vulkan.Buffer</c> rather than imported.
/// </para>
/// </remarks>
public sealed unsafe class VulkanCoreDevice : IDisposable
{
    private readonly Queue GraphicsQueueHandle;
    private Device DeviceHandle;
    private bool Disposed;

    /// <summary>Gets the Vulkan entry points.</summary>
    public Vk Api { get; }

    /// <summary>Gets the instance, its validation layer and its debug messenger.</summary>
    public VulkanInstance Instance { get; }

    /// <summary>Gets the physical device that was selected and everything queried about it.</summary>
    public VulkanAdapter Adapter { get; }

    /// <summary>Gets the logical device handle.</summary>
    public Device Handle => DeviceHandle;

    /// <summary>Gets the universal queue. Graphics, compute and transfer capable.</summary>
    public Queue GraphicsQueue => GraphicsQueueHandle;

    /// <summary>Gets the queue family the graphics queue belongs to.</summary>
    public uint GraphicsQueueFamily => Adapter.QueueFamilies.Graphics;

    /// <summary>Gets the object naming and debug scope helper.</summary>
    public VulkanDebugNames DebugNames { get; }

    /// <summary>Gets the memory sub-allocator backing every long-lived buffer and image.</summary>
    public VulkanMemoryAllocator Allocator { get; }

    /// <summary>Gets the per-frame streaming allocator. Only for data written and read on the same frame.</summary>
    public VulkanUploadRing UploadRing { get; }

    /// <summary>Gets the deferred destruction queue.</summary>
    public VulkanDeletionQueue DeletionQueue { get; }

    /// <summary>Gets the frames-in-flight ring, its timeline semaphore and its command pools.</summary>
    public VulkanFrameRing FrameRing { get; }

    /// <summary>Gets how many frames may be in flight at once.</summary>
    public int FramesInFlight => FrameRing.FramesInFlight;

    /// <summary>Gets the slot the frame being recorded is using.</summary>
    public int FrameIndex => FrameRing.FrameIndex;

    /// <summary>Gets a value indicating whether the validation layer is loaded.</summary>
    public bool ValidationEnabled => Instance.ValidationEnabled;

    /// <summary>Creates a device.</summary>
    /// <param name="options">Creation parameters.</param>
    /// <exception cref="VulkanException">No suitable device exists, or creation failed.</exception>
    public VulkanCoreDevice(VulkanCoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Instance = new VulkanInstance(options);
        Api = Instance.Api;

        try
        {
            Adapter = VulkanAdapter.Select(Instance, options.DeviceFilter);
            DeviceHandle = CreateLogicalDevice(options);

            Api.GetDeviceQueue(DeviceHandle, Adapter.QueueFamilies.Graphics, 0, out GraphicsQueueHandle);

            DebugNames = new VulkanDebugNames(Instance.DebugUtils, DeviceHandle);
            DebugNames.SetName(ObjectType.Device, (ulong)DeviceHandle.Handle, "VulkanCoreDevice");
            DebugNames.SetName(ObjectType.PhysicalDevice, (ulong)Adapter.Handle.Handle, Adapter.Name);
            DebugNames.SetName(ObjectType.Queue, (ulong)GraphicsQueueHandle.Handle, "Graphics queue");

            Allocator = new VulkanMemoryAllocator(Api, DeviceHandle, Adapter, DebugNames);
            DeletionQueue = new VulkanDeletionQueue(Api, DeviceHandle);

            UploadRing = new VulkanUploadRing(
                Api,
                DeviceHandle,
                Adapter,
                Allocator,
                DebugNames,
                options.FramesInFlight);

            FrameRing = new VulkanFrameRing(
                Api,
                DeviceHandle,
                Adapter.QueueFamilies.Graphics,
                DebugNames,
                DeletionQueue,
                UploadRing,
                options.FramesInFlight);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private Device CreateLogicalDevice(VulkanCoreOptions options)
    {
        // Query first, then enable the intersection of wanted and supported. Requesting a feature the
        // device does not advertise is a validation error and fails creation outright, and the
        // renderer's hard requirements were already gated in VulkanAdapter.Select.
        var supported13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features };
        var supported12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &supported13 };
        var supported11 = new PhysicalDeviceVulkan11Features { SType = StructureType.PhysicalDeviceVulkan11Features, PNext = &supported12 };
        var supported = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &supported11 };

        Api.GetPhysicalDeviceFeatures2(Adapter.Handle, &supported);

        var features13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,

            // Required. The contract's RenderPassDesc describes attachments directly and its barrier
            // model is expressed in stage and access masks that only synchronization2 has.
            DynamicRendering = true,
            Synchronization2 = true,

            // Guarantees a compute dispatch runs full subgroups, so the 19 subgroup intrinsics in the
            // compute passes cannot read a lane that was never launched.
            SubgroupSizeControl = supported13.SubgroupSizeControl,
            ComputeFullSubgroups = supported13.ComputeFullSubgroups,
            Maintenance4 = supported13.Maintenance4,
        };

        var features12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            PNext = &features13,

            // The frame ring is built on a timeline semaphore.
            TimelineSemaphore = true,

            // vkCmdDrawIndexedIndirectCount, the MultiDrawElementsIndirectCount path.
            DrawIndirectCount = true,

            // Subgroup operations on 8, 16 and 64 bit types rather than only 32 bit.
            ShaderSubgroupExtendedTypes = supported12.ShaderSubgroupExtendedTypes,
        };

        var features11 = new PhysicalDeviceVulkan11Features
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            PNext = &features12,

            // gl_BaseInstance, which is how the renderer passes a scene node id into a draw. The shader
            // emitter rewrites every instance-id read as gl_InstanceIndex - gl_BaseInstance, so the SPIR-V
            // declares the DrawParameters capability; creating a module that declares a capability the
            // device did not enable is a validation error (VUID-VkShaderModuleCreateInfo-pCode-08740) and
            // reading the built-in under it is undefined. The query above already asked for this struct
            // and its answer went unused, so this is the enable that was missing rather than a new
            // requirement: a device that does not support it still gets false and fails at the same place
            // it would have anyway.
            ShaderDrawParameters = supported11.ShaderDrawParameters,
        };

        var features = new PhysicalDeviceFeatures
        {
            MultiDrawIndirect = true,
            DrawIndirectFirstInstance = true,

            // Environment lighting is a cube array: the world loader builds one env_cubemap_array image
            // and common/environment.slang reads it through samplerCubeArray, so the SPIR-V declares the
            // SampledCubeArray capability. Without the feature enabled, vkCreateImageView refuses the
            // VK_IMAGE_VIEW_TYPE_CUBE_ARRAY view (VUID-VkImageViewCreateInfo-viewType-01004) and every
            // module declaring that capability is invalid (VUID-VkShaderModuleCreateInfo-pCode-08740).
            // Opening de_mirage raised fourteen of those errors and none of them threw; with this enabled
            // there are none. Measured: the map's image is unchanged either way, so this removes undefined
            // execution rather than a wrong picture -- which is worth doing on its own terms, since
            // undefined work submitted by this renderer is what hung a display driver once already.
            // Required rather than conditional for the same reason multiDrawIndirect is: the shaders
            // declare it unconditionally, so a device without it cannot run this renderer at all, and
            // VulkanAdapter.Select rejects one rather than letting it fail here.
            ImageCubeArray = true,

            SamplerAnisotropy = supported.Features.SamplerAnisotropy,

            // Decal and overlay materials set a depth bias clamp of 0.0005 (RenderMaterial's hasDepthBias
            // path), and a pipeline that states a non-zero clamp without this feature is invalid
            // (VUID-VkGraphicsPipelineCreateInfo-pDynamicStates-00754). A map is full of overlays, so
            // opening one produced that error for every such pipeline. Conditional rather than required:
            // a device without it can still draw everything else, and the clamp only bounds a bias.
            DepthBiasClamp = supported.Features.DepthBiasClamp,

            // QuadOverdraw performs atomics on storage images from the fragment stage.
            FragmentStoresAndAtomics = supported.Features.FragmentStoresAndAtomics,

            // Wireframe and the debug shape rendering.
            FillModeNonSolid = supported.Features.FillModeNonSolid,

            // Per-attachment blend state, which RenderState already models.
            IndependentBlend = supported.Features.IndependentBlend,

            // Compressed texture formats the vtex loader produces.
            TextureCompressionBC = supported.Features.TextureCompressionBC,

            ShaderStorageImageExtendedFormats = supported.Features.ShaderStorageImageExtendedFormats,
        };

        var features2 = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features11,
            Features = features,
        };

        var priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = Adapter.QueueFamilies.Graphics,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        var extensions = options.DeviceExtensions.Count == 0
            ? 0
            : SilkMarshal.StringArrayToPtr((string[])[.. options.DeviceExtensions]);

        try
        {
            var info = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = &features2,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueInfo,
                EnabledExtensionCount = (uint)options.DeviceExtensions.Count,
                PpEnabledExtensionNames = (byte**)extensions,

                // PEnabledFeatures must stay null: the feature set travels in the pNext chain instead,
                // and providing both is a validation error.
                PEnabledFeatures = null,
            };

            Api.CreateDevice(Adapter.Handle, &info, null, out var device).Check("vkCreateDevice");
            return device;
        }
        finally
        {
            if (extensions != 0)
            {
                SilkMarshal.Free(extensions);
            }
        }
    }

    /// <summary>Opens a frame. See <see cref="VulkanFrameRing.BeginFrame"/>.</summary>
    public void BeginFrame() => FrameRing.BeginFrame();

    /// <summary>Closes a frame. See <see cref="VulkanFrameRing.EndFrame"/>.</summary>
    public void EndFrame() => FrameRing.EndFrame();

    /// <summary>Submits a command buffer and signals the frame timeline to the current serial.</summary>
    /// <param name="commandBuffer">The recorded command buffer, already ended.</param>
    /// <remarks>Uses <c>vkQueueSubmit2</c>, the synchronization2 entry point. A frame's last submission
    /// must be the one that signals, which is what makes the slot reusable.</remarks>
    public void SubmitAndSignal(CommandBuffer commandBuffer)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        var commandInfo = new CommandBufferSubmitInfo
        {
            SType = StructureType.CommandBufferSubmitInfo,
            CommandBuffer = commandBuffer,
        };

        var signalInfo = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = FrameRing.TimelineSemaphore,
            Value = FrameRing.CurrentSerial,
            StageMask = PipelineStageFlags2.AllCommandsBit,
        };

        var submit = new SubmitInfo2
        {
            SType = StructureType.SubmitInfo2,
            CommandBufferInfoCount = 1,
            PCommandBufferInfos = &commandInfo,
            SignalSemaphoreInfoCount = 1,
            PSignalSemaphoreInfos = &signalInfo,
        };

        Api.QueueSubmit2(GraphicsQueueHandle, 1, &submit, default).Check("vkQueueSubmit2");
    }

    /// <summary>Blocks until every submitted operation has finished.</summary>
    public void WaitIdle()
    {
        if (DeviceHandle.Handle != 0)
        {
            Api.DeviceWaitIdle(DeviceHandle).Check("vkDeviceWaitIdle");
        }
    }

    /// <summary>Waits for the device to go idle, then tears everything down in dependency order.</summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        if (DeviceHandle.Handle != 0)
        {
            Api.DeviceWaitIdle(DeviceHandle);
        }

        FrameRing?.Dispose();
        DeletionQueue?.Dispose();
        UploadRing?.Dispose();
        Allocator?.Dispose();

        if (DeviceHandle.Handle != 0)
        {
            Api.DestroyDevice(DeviceHandle, null);
            DeviceHandle = default;
        }

        Instance?.Dispose();
    }
}
