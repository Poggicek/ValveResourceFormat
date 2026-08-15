using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Core;

/// <summary>
/// Which queue families a physical device offers, resolved to the three roles the renderer uses.
/// </summary>
/// <param name="Graphics">The universal family. Graphics, compute and transfer capable; always present.</param>
/// <param name="AsyncCompute">A compute family that cannot do graphics, or the same value as
/// <paramref name="Graphics"/> when the device exposes none.</param>
/// <param name="Transfer">A transfer-only family driving the DMA engine, or the same value as
/// <paramref name="Graphics"/> when the device exposes none.</param>
public readonly record struct VulkanQueueFamilies(uint Graphics, uint AsyncCompute, uint Transfer)
{
    /// <summary>Gets a value indicating whether <see cref="AsyncCompute"/> is a genuinely separate family.</summary>
    public bool HasDedicatedCompute => AsyncCompute != Graphics;

    /// <summary>Gets a value indicating whether <see cref="Transfer"/> is a genuinely separate family.</summary>
    public bool HasDedicatedTransfer => Transfer != Graphics;

    /// <summary>Gets the distinct family indices, for the exclusive-sharing case where a resource
    /// must name every family that will touch it.</summary>
    /// <returns>The distinct indices.</returns>
    public IReadOnlyList<uint> Distinct()
    {
        var result = new List<uint>(3) { Graphics };

        if (HasDedicatedCompute)
        {
            result.Add(AsyncCompute);
        }

        if (HasDedicatedTransfer)
        {
            result.Add(Transfer);
        }

        return result;
    }
}

/// <summary>
/// A physical device that meets the renderer's requirements, with everything queried about it that
/// device creation and <see cref="IDeviceLimits"/> need.
/// </summary>
/// <remarks>
/// <para>
/// Selection is a hard gate, not a preference. The renderer's compute passes use 19 subgroup
/// intrinsics and its draw path uses <c>MultiDrawElementsIndirectCount</c>, so a device that cannot
/// do either cannot run the renderer at all. Failing here with a specific message is far better than
/// creating a device that later miscompiles a shader or silently draws nothing.
/// </para>
/// </remarks>
public sealed unsafe class VulkanAdapter
{
    /// <summary>The subgroup operations the renderer's compute passes rely on.</summary>
    public const SubgroupFeatureFlags RequiredSubgroupOperations =
        SubgroupFeatureFlags.BasicBit
        | SubgroupFeatureFlags.VoteBit
        | SubgroupFeatureFlags.ArithmeticBit
        | SubgroupFeatureFlags.BallotBit
        | SubgroupFeatureFlags.ShuffleBit;

    /// <summary>Gets the physical device handle.</summary>
    public PhysicalDevice Handle { get; }

    /// <summary>Gets the human readable device name, as reported by the driver.</summary>
    public string Name { get; }

    /// <summary>Gets the device type, which drives selection scoring.</summary>
    public PhysicalDeviceType DeviceType { get; }

    /// <summary>Gets the core device properties, including limits.</summary>
    public PhysicalDeviceProperties Properties { get; }

    /// <summary>Gets the subgroup properties: size, which operations are supported, and in which stages.</summary>
    public PhysicalDeviceSubgroupProperties SubgroupProperties { get; }

    /// <summary>Gets the memory types and heaps the allocator sub-allocates from.</summary>
    public PhysicalDeviceMemoryProperties MemoryProperties { get; }

    /// <summary>Gets the resolved queue families.</summary>
    public VulkanQueueFamilies QueueFamilies { get; }

    /// <summary>Gets the highest sample count usable for both colour and depth render targets.
    /// Backs <see cref="IDeviceLimits.MaxSampleCount"/>, which the viewers clamp the anti-aliasing
    /// setting against.</summary>
    public int MaxSampleCount { get; }

    /// <summary>Gets the largest push constant block in bytes, clamped into <see cref="int"/>.</summary>
    public int MaxPushConstantSize { get; }

    /// <summary>Gets the largest uniform buffer binding in bytes, clamped into <see cref="int"/>.</summary>
    /// <remarks>The clamp is load bearing. AMD reports <c>0xFFFFFFFF</c> here, which is
    /// <see cref="uint"/> saturation meaning "no practical limit"; converting it unchecked yields
    /// <c>-1</c> and every range check downstream fails.</remarks>
    public int MaxUniformBufferRange { get; }

    /// <summary>Gets the maximum sampler anisotropy.</summary>
    public float MaxSamplerAnisotropy { get; }

    /// <summary>Gets a value indicating whether indirect draws can read their count from a buffer.</summary>
    public bool SupportsDrawIndirectCount { get; }

    /// <summary>Gets a value indicating whether subgroup operations are usable in compute shaders.</summary>
    public bool SupportsShaderSubgroup { get; }

    /// <summary>Gets a value indicating whether a non-zero first instance is honoured by indirect draws.
    /// The renderer passes the scene node id through it.</summary>
    public bool SupportsIndirectFirstInstance { get; }

    /// <summary>Gets a value indicating whether compute dispatches can be forced to full subgroups,
    /// so a partially filled workgroup cannot make a subgroup intrinsic read an inactive lane.</summary>
    public bool SupportsFullSubgroups { get; }

    private VulkanAdapter(
        PhysicalDevice handle,
        string name,
        PhysicalDeviceProperties properties,
        PhysicalDeviceSubgroupProperties subgroupProperties,
        PhysicalDeviceMemoryProperties memoryProperties,
        VulkanQueueFamilies queueFamilies,
        bool supportsDrawIndirectCount,
        bool supportsShaderSubgroup,
        bool supportsIndirectFirstInstance,
        bool supportsFullSubgroups)
    {
        Handle = handle;
        Name = name;
        DeviceType = properties.DeviceType;
        Properties = properties;
        SubgroupProperties = subgroupProperties;
        MemoryProperties = memoryProperties;
        QueueFamilies = queueFamilies;
        SupportsDrawIndirectCount = supportsDrawIndirectCount;
        SupportsShaderSubgroup = supportsShaderSubgroup;
        SupportsIndirectFirstInstance = supportsIndirectFirstInstance;
        SupportsFullSubgroups = supportsFullSubgroups;

        MaxPushConstantSize = ClampToInt(properties.Limits.MaxPushConstantsSize);
        MaxUniformBufferRange = ClampToInt(properties.Limits.MaxUniformBufferRange);
        MaxSamplerAnisotropy = properties.Limits.MaxSamplerAnisotropy;

        var shared = properties.Limits.FramebufferColorSampleCounts & properties.Limits.FramebufferDepthSampleCounts;
        MaxSampleCount = HighestSampleCount(shared);
    }

    private static int ClampToInt(uint value) => value > int.MaxValue ? int.MaxValue : (int)value;

    private static int HighestSampleCount(SampleCountFlags flags)
    {
        if ((flags & SampleCountFlags.Count64Bit) != 0)
        {
            return 64;
        }

        if ((flags & SampleCountFlags.Count32Bit) != 0)
        {
            return 32;
        }

        if ((flags & SampleCountFlags.Count16Bit) != 0)
        {
            return 16;
        }

        if ((flags & SampleCountFlags.Count8Bit) != 0)
        {
            return 8;
        }

        if ((flags & SampleCountFlags.Count4Bit) != 0)
        {
            return 4;
        }

        if ((flags & SampleCountFlags.Count2Bit) != 0)
        {
            return 2;
        }

        return 1;
    }

    /// <summary>Picks the best physical device that meets every requirement.</summary>
    /// <param name="instance">The instance to enumerate.</param>
    /// <param name="filter">An extra predicate, such as "can present to this surface", or
    /// <see langword="null"/>.</param>
    /// <returns>The chosen adapter.</returns>
    /// <exception cref="VulkanException">No device met the requirements. The message lists what each
    /// candidate was missing.</exception>
    public static VulkanAdapter Select(VulkanInstance instance, Func<PhysicalDevice, bool>? filter)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var vk = instance.Api;

        uint count = 0;
        vk.EnumeratePhysicalDevices(instance.Handle, ref count, null).Check("vkEnumeratePhysicalDevices");

        if (count == 0)
        {
            throw new VulkanException("No Vulkan physical devices are present.");
        }

        var devices = new PhysicalDevice[count];

        fixed (PhysicalDevice* p = devices)
        {
            vk.EnumeratePhysicalDevices(instance.Handle, ref count, p).Check("vkEnumeratePhysicalDevices");
        }

        VulkanAdapter? best = null;
        var bestScore = -1;
        var rejections = new List<string>();

        foreach (var device in devices)
        {
            if (filter is not null && !filter(device))
            {
                continue;
            }

            var candidate = Inspect(vk, device, out var rejection);

            if (candidate is null)
            {
                rejections.Add(rejection!);
                continue;
            }

            var score = candidate.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 1000,
                PhysicalDeviceType.IntegratedGpu => 500,
                PhysicalDeviceType.VirtualGpu => 100,
                PhysicalDeviceType.Cpu => 10,
                _ => 1,
            };

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        if (best is null)
        {
            var detail = rejections.Count > 0 ? string.Join("; ", rejections) : "no candidates";
            throw new VulkanException($"No Vulkan device meets the renderer's requirements ({detail}).");
        }

        return best;
    }

    private static VulkanAdapter? Inspect(Vk vk, PhysicalDevice device, out string? rejection)
    {
        rejection = null;

        var subgroup = new PhysicalDeviceSubgroupProperties
        {
            SType = StructureType.PhysicalDeviceSubgroupProperties,
        };

        var properties2 = new PhysicalDeviceProperties2
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &subgroup,
        };

        vk.GetPhysicalDeviceProperties2(device, &properties2);

        var name = Marshal.PtrToStringUTF8((nint)properties2.Properties.DeviceName) ?? "unknown device";

        if (properties2.Properties.ApiVersion < Vk.Version13)
        {
            var major = properties2.Properties.ApiVersion >> 22;
            var minor = (properties2.Properties.ApiVersion >> 12) & 0x3FF;
            rejection = $"{name} reports Vulkan {major}.{minor}, 1.3 is required";
            return null;
        }

        // The contract declares five descriptor sets and Vulkan's guaranteed floor is four, so this is
        // a real limit rather than a formality. A device at the floor would create every pipeline
        // successfully and then be unable to bind all of their sets, which surfaces at the first draw
        // as a resource that is simply absent -- so it is rejected here, where the reason can be said
        // plainly, rather than diagnosed later from a black image.
        if (properties2.Properties.Limits.MaxBoundDescriptorSets < DescriptorSets.Count)
        {
            rejection = $"{name} binds at most {properties2.Properties.Limits.MaxBoundDescriptorSets} descriptor sets, {DescriptorSets.Count} are required";
            return null;
        }

        var features13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
        };

        var features12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            PNext = &features13,
        };

        var features11 = new PhysicalDeviceVulkan11Features
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            PNext = &features12,
        };

        var features2 = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features11,
        };

        vk.GetPhysicalDeviceFeatures2(device, &features2);

        var missing = new List<string>();

        if (!features13.DynamicRendering)
        {
            missing.Add("dynamicRendering");
        }

        if (!features13.Synchronization2)
        {
            missing.Add("synchronization2");
        }

        if (!features12.TimelineSemaphore)
        {
            missing.Add("timelineSemaphore");
        }

        // MultiDrawElementsIndirectCount needs all three: the count-from-buffer entry point, the
        // ability to issue more than one sub-draw, and a first instance the shader can read.
        if (!features12.DrawIndirectCount)
        {
            missing.Add("drawIndirectCount");
        }

        if (!features2.Features.MultiDrawIndirect)
        {
            missing.Add("multiDrawIndirect");
        }

        if (!features2.Features.DrawIndirectFirstInstance)
        {
            missing.Add("drawIndirectFirstInstance");
        }

        if ((subgroup.SupportedOperations & RequiredSubgroupOperations) != RequiredSubgroupOperations)
        {
            missing.Add($"subgroup operations (have {subgroup.SupportedOperations}, need {RequiredSubgroupOperations})");
        }

        if ((subgroup.SupportedStages & ShaderStageFlags.ComputeBit) == 0)
        {
            missing.Add("subgroup operations in compute stage");
        }

        if (missing.Count > 0)
        {
            rejection = $"{name} is missing {string.Join(", ", missing)}";
            return null;
        }

        if (!TrySelectQueueFamilies(vk, device, out var families))
        {
            rejection = $"{name} has no graphics queue family";
            return null;
        }

        var memory = new PhysicalDeviceMemoryProperties();
        vk.GetPhysicalDeviceMemoryProperties(device, &memory);

        return new VulkanAdapter(
            device,
            name,
            properties2.Properties,
            subgroup,
            memory,
            families,
            supportsDrawIndirectCount: true,
            supportsShaderSubgroup: true,
            supportsIndirectFirstInstance: true,
            supportsFullSubgroups: features13.SubgroupSizeControl && features13.ComputeFullSubgroups);
    }

    private static bool TrySelectQueueFamilies(Vk vk, PhysicalDevice device, out VulkanQueueFamilies families)
    {
        families = default;

        uint count = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, null);

        var properties = new QueueFamilyProperties[count];

        fixed (QueueFamilyProperties* p = properties)
        {
            vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, p);
        }

        var graphics = uint.MaxValue;
        var compute = uint.MaxValue;
        var transfer = uint.MaxValue;

        for (var i = 0u; i < count; i++)
        {
            var flags = properties[i].QueueFlags;

            if (graphics == uint.MaxValue && (flags & QueueFlags.GraphicsBit) != 0)
            {
                graphics = i;
            }

            // Prefer a family that cannot do graphics: on AMD and NVIDIA that is a genuinely
            // independent hardware queue rather than a second view of the universal one.
            if (compute == uint.MaxValue
                && (flags & QueueFlags.ComputeBit) != 0
                && (flags & QueueFlags.GraphicsBit) == 0)
            {
                compute = i;
            }

            // A transfer-only family is the DMA engine, which uploads without touching the 3D queue.
            if (transfer == uint.MaxValue
                && (flags & QueueFlags.TransferBit) != 0
                && (flags & QueueFlags.GraphicsBit) == 0
                && (flags & QueueFlags.ComputeBit) == 0)
            {
                transfer = i;
            }
        }

        if (graphics == uint.MaxValue)
        {
            return false;
        }

        families = new VulkanQueueFamilies(
            graphics,
            compute == uint.MaxValue ? graphics : compute,
            transfer == uint.MaxValue ? graphics : transfer);

        return true;
    }
}
