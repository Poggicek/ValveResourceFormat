using System.Globalization;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Descriptors;

/// <summary>
/// How large a <c>VkDescriptorPool</c> to create, expressed per set rather than in absolute descriptor
/// counts.
/// </summary>
/// <remarks>
/// A pool is sized once at creation and cannot grow, so the numbers here are an average set's shape
/// multiplied by <see cref="MaxSetsPerPool"/>. Getting them wrong is not fatal &#8212;
/// <see cref="VulkanDescriptorPool"/> chains another pool when one runs out &#8212; but a badly sized
/// pool chains often, and every chain is a driver allocation.
/// </remarks>
public sealed record VulkanDescriptorPoolSizes
{
    /// <summary>Gets how many sets one pool can hand out.</summary>
    public int MaxSetsPerPool { get; init; } = 256;

    /// <summary>Gets the uniform buffer descriptors budgeted per set. The canonical set 0 declares eight.</summary>
    public int UniformBuffersPerSet { get; init; } = 8;

    /// <summary>Gets the storage buffer descriptors budgeted per set. The canonical set 1 declares sixteen.</summary>
    public int StorageBuffersPerSet { get; init; } = 8;

    /// <summary>Gets the combined image sampler descriptors budgeted per set. The canonical set 2 declares eighteen.</summary>
    public int CombinedImageSamplersPerSet { get; init; } = 12;

    /// <summary>Gets the sampled image descriptors budgeted per set.</summary>
    public int SampledImagesPerSet { get; init; } = 2;

    /// <summary>Gets the storage image descriptors budgeted per set. The compute passes bind at most three.</summary>
    public int StorageImagesPerSet { get; init; } = 2;

    /// <summary>Gets the standalone sampler descriptors budgeted per set.</summary>
    public int SamplersPerSet { get; init; } = 2;
}

/// <summary>
/// A chain of <c>VkDescriptorPool</c>s that allocates sets and is recycled as a unit.
/// </summary>
/// <remarks>
/// <para>
/// Recycling is <c>vkResetDescriptorPool</c> on the whole chain rather than
/// <c>vkFreeDescriptorSets</c> per set, for the same reason
/// <see cref="Core.VulkanCommandPool"/> resets its pool: freeing individually forces the driver to keep
/// a per-set allocator and fragments the pool, while resetting hands back one arena. Every set the
/// chain ever handed out becomes invalid at once, which is exactly a frame slot's lifetime.
/// </para>
/// <para>
/// <c>VK_ERROR_OUT_OF_POOL_MEMORY</c> and <c>VK_ERROR_FRAGMENTED_POOL</c> are the two results an
/// application is expected to handle rather than treat as failures, so allocation checks for them and
/// chains another pool. Passing them to <see cref="VulkanResultExtensions.Check"/> like any other result
/// would turn a routine growth event into an exception.
/// </para>
/// </remarks>
public sealed unsafe class VulkanDescriptorPool : IDisposable
{
    private const int MaxSetsCeiling = 4096;

    private readonly Vk Api;
    private readonly Device Device;
    private readonly VulkanDebugNames DebugNames;
    private readonly VulkanDescriptorPoolSizes Sizes;
    private readonly string Name;
    private readonly bool AllowFree;
    private readonly List<DescriptorPool> Pools = [];
    private readonly Dictionary<ulong, DescriptorPool> SetOwners = [];

    private int CurrentPool = -1;
    private int NextMaxSets;
    private bool Disposed;

    /// <summary>Gets how many <c>VkDescriptorPool</c>s the chain holds.</summary>
    public int PoolCount => Pools.Count;

    /// <summary>Gets how many sets have been allocated since the last <see cref="Reset"/>.</summary>
    public int AllocatedCount { get; private set; }

    /// <summary>Gets the most sets allocated between any two resets over this chain's lifetime.</summary>
    /// <remarks>The number to size <see cref="VulkanDescriptorPoolSizes.MaxSetsPerPool"/> from. A peak
    /// far above it means the chain is growing every frame.</remarks>
    public int PeakAllocatedCount { get; private set; }

    /// <summary>Creates a pool chain.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="debugNames">Used to name each pool.</param>
    /// <param name="name">Debug name for the chain.</param>
    /// <param name="sizes">How large to make each pool, or <see langword="null"/> for the defaults.</param>
    /// <param name="allowFree">Whether individual sets may be returned with <see cref="Free"/>. Costs
    /// the driver a per-set allocator, so leave it off for a chain that is reset wholesale.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public VulkanDescriptorPool(
        Vk api,
        Device device,
        VulkanDebugNames debugNames,
        string name,
        VulkanDescriptorPoolSizes? sizes = null,
        bool allowFree = false)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(debugNames);
        ArgumentNullException.ThrowIfNull(name);

        Api = api;
        Device = device;
        DebugNames = debugNames;
        Name = name;
        Sizes = sizes ?? new VulkanDescriptorPoolSizes();
        AllowFree = allowFree;
        NextMaxSets = Math.Max(1, Sizes.MaxSetsPerPool);
    }

    /// <summary>Allocates one descriptor set.</summary>
    /// <param name="layout">The layout to allocate against.</param>
    /// <returns>The set, valid until the next <see cref="Reset"/> or <see cref="Free"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The chain has been disposed.</exception>
    /// <exception cref="VulkanException">Allocation failed for a reason growing the chain cannot fix.</exception>
    public DescriptorSet Allocate(VulkanDescriptorSetLayout layout)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        ArgumentNullException.ThrowIfNull(layout);

        if (CurrentPool < 0)
        {
            Grow();
        }

        var result = TryAllocateFrom(Pools[CurrentPool], layout, out var set);

        if (result is Result.ErrorOutOfPoolMemory or Result.ErrorFragmentedPool)
        {
            // Expected, not a failure: this pool is full. Move to the next one, creating it if the
            // chain has not been grown that far since the last reset.
            if (CurrentPool + 1 == Pools.Count)
            {
                Grow();
            }
            else
            {
                CurrentPool++;
            }

            result = TryAllocateFrom(Pools[CurrentPool], layout, out set);
        }

        result.Check("vkAllocateDescriptorSets");

        AllocatedCount++;
        PeakAllocatedCount = Math.Max(PeakAllocatedCount, AllocatedCount);

        if (AllowFree)
        {
            // A set may only be freed back to the pool it came from, and the handle does not say which
            // that was. Recorded only for a chain that can free, so the reset-wholesale chain pays
            // nothing for it.
            SetOwners[set.Handle] = Pools[CurrentPool];
        }

        return set;
    }

    private Result TryAllocateFrom(DescriptorPool pool, VulkanDescriptorSetLayout layout, out DescriptorSet set)
    {
        var handle = layout.Handle;

        var info = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool,
            DescriptorSetCount = 1,
            PSetLayouts = &handle,
        };

        DescriptorSet allocated;
        var result = Api.AllocateDescriptorSets(Device, &info, &allocated);
        set = allocated;
        return result;
    }

    /// <summary>Returns one set to the pool it came from.</summary>
    /// <param name="set">The set to free.</param>
    /// <exception cref="InvalidOperationException">The chain was not created with <c>allowFree</c>, or
    /// the set did not come from it.</exception>
    /// <remarks>Only for the persistent chain. A per-frame chain is recycled by <see cref="Reset"/>.</remarks>
    public void Free(DescriptorSet set)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        if (!AllowFree)
        {
            throw new InvalidOperationException($"'{Name}' was created without the free bit, so its sets can only be released by resetting the whole chain.");
        }

        if (set.Handle == 0)
        {
            return;
        }

        if (!SetOwners.Remove(set.Handle, out var pool))
        {
            throw new InvalidOperationException($"That descriptor set did not come from '{Name}'. Freeing a set to the wrong pool is undefined behaviour, so it is refused here rather than passed on.");
        }

        var handle = set;
        Api.FreeDescriptorSets(Device, pool, 1, &handle).Check("vkFreeDescriptorSets");
        AllocatedCount = Math.Max(0, AllocatedCount - 1);
    }

    /// <summary>Recycles every set the chain has handed out.</summary>
    /// <remarks>Only legal once the GPU has finished every submission that referenced them. The frame
    /// ring's wait on the slot's timeline value is what establishes that.</remarks>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        foreach (var pool in Pools)
        {
            Api.ResetDescriptorPool(Device, pool, 0).Check("vkResetDescriptorPool");
        }

        CurrentPool = Pools.Count == 0 ? -1 : 0;
        AllocatedCount = 0;
        SetOwners.Clear();
    }

    private static DescriptorPoolSize Size(DescriptorType type, int perSet, int maxSets) => new()
    {
        Type = type,
        DescriptorCount = (uint)Math.Max(1, perSet * maxSets),
    };

    private void Grow()
    {
        var maxSets = Math.Min(NextMaxSets, MaxSetsCeiling);

        Span<DescriptorPoolSize> sizes =
        [
            Size(DescriptorType.UniformBuffer, Sizes.UniformBuffersPerSet, maxSets),
            Size(DescriptorType.StorageBuffer, Sizes.StorageBuffersPerSet, maxSets),
            Size(DescriptorType.CombinedImageSampler, Sizes.CombinedImageSamplersPerSet, maxSets),
            Size(DescriptorType.SampledImage, Sizes.SampledImagesPerSet, maxSets),
            Size(DescriptorType.StorageImage, Sizes.StorageImagesPerSet, maxSets),
            Size(DescriptorType.Sampler, Sizes.SamplersPerSet, maxSets),
        ];

        fixed (DescriptorPoolSize* p = sizes)
        {
            var info = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = (uint)maxSets,
                PoolSizeCount = (uint)sizes.Length,
                PPoolSizes = p,
                Flags = AllowFree ? DescriptorPoolCreateFlags.FreeDescriptorSetBit : DescriptorPoolCreateFlags.None,
            };

            Api.CreateDescriptorPool(Device, &info, null, out var pool).Check("vkCreateDescriptorPool");

            DebugNames.SetName(ObjectType.DescriptorPool, pool.Handle, string.Create(CultureInfo.InvariantCulture, $"{Name} pool {Pools.Count}"));
            Pools.Add(pool);
        }

        CurrentPool = Pools.Count - 1;

        // Each chained pool is twice the last, so a chain that keeps overflowing settles in a logarithmic
        // number of driver allocations rather than one per frame.
        NextMaxSets = Math.Min(maxSets * 2, MaxSetsCeiling);
    }

    /// <summary>Destroys every pool in the chain and every set allocated from them.</summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        foreach (var pool in Pools)
        {
            if (pool.Handle != 0)
            {
                Api.DestroyDescriptorPool(Device, pool, null);
            }
        }

        Pools.Clear();
        SetOwners.Clear();
        CurrentPool = -1;
    }
}
