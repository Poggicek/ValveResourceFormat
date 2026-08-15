using System.Globalization;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Descriptors;

/// <summary>What the allocator has been doing, for a diagnostics overlay or a smoke test.</summary>
/// <param name="FrameSlots">How many per-frame chains there are, one per frame in flight.</param>
/// <param name="AllocatedThisFrame">Sets handed out since the current frame's chain was recycled.</param>
/// <param name="PeakPerFrame">The most sets any one frame has needed.</param>
/// <param name="FramePoolCount">How many <c>VkDescriptorPool</c>s the per-frame chains hold between them.</param>
/// <param name="PersistentAllocated">Sets currently outstanding from the persistent chain.</param>
public readonly record struct VulkanDescriptorAllocatorStatistics(
    int FrameSlots,
    int AllocatedThisFrame,
    int PeakPerFrame,
    int FramePoolCount,
    int PersistentAllocated);

/// <summary>
/// Descriptor set allocation on top of the frames-in-flight ring: one recycled chain per frame slot,
/// plus a persistent chain for sets that outlive a frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>The recycle point is not a hook this class asks anyone to call.</b> A frame slot's sets stop being
/// referenced by the GPU at exactly the moment <see cref="VulkanFrameRing.BeginFrame"/> has waited for
/// that slot's previous serial, and there is no way to observe that from outside without being told.
/// So instead of a <c>BeginFrame</c> the caller must remember, this reads
/// <see cref="VulkanFrameRing.CurrentSerial"/> on every allocation and recycles the slot's chain the
/// first time it sees a serial the slot has not been used for. Forgetting to notify it is therefore not
/// a failure mode, and a caller that never opens a frame simply keeps filling one chain.
/// </para>
/// <para>
/// The serial is what makes this work rather than the slot index: the slot repeats every
/// <see cref="VulkanFrameRing.FramesInFlight"/> frames and the serial never does, so "is this the same
/// frame I last reset for" is a single comparison that cannot alias.
/// </para>
/// <para>
/// Sets allocated before any frame is open belong to slot 0 and are invalidated when the first real
/// frame reaches that slot. Anything that must survive a frame boundary belongs in
/// <see cref="AllocatePersistent"/>.
/// </para>
/// </remarks>
public sealed class VulkanDescriptorAllocator : IDisposable
{
    private readonly VulkanFrameRing FrameRing;
    private readonly VulkanDescriptorPool[] FramePools;
    private readonly ulong[] SlotSerials;
    private readonly VulkanDescriptorPool PersistentPool;

    private bool Disposed;

    /// <summary>Gets the layout cache these sets are allocated against.</summary>
    public VulkanDescriptorLayoutCache Layouts { get; }

    /// <summary>Gets the logical device the pools belong to.</summary>
    public Device Device { get; }

    /// <summary>Creates an allocator.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="debugNames">Used to name the pools.</param>
    /// <param name="frameRing">The ring whose slots the per-frame chains follow.</param>
    /// <param name="layouts">The layout cache, exposed as <see cref="Layouts"/> for convenience.</param>
    /// <param name="sizes">Pool sizing, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public VulkanDescriptorAllocator(
        Vk api,
        Device device,
        VulkanDebugNames debugNames,
        VulkanFrameRing frameRing,
        VulkanDescriptorLayoutCache layouts,
        VulkanDescriptorPoolSizes? sizes = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(debugNames);
        ArgumentNullException.ThrowIfNull(frameRing);
        ArgumentNullException.ThrowIfNull(layouts);

        FrameRing = frameRing;
        Layouts = layouts;
        Device = device;

        FramePools = new VulkanDescriptorPool[frameRing.FramesInFlight];
        SlotSerials = new ulong[frameRing.FramesInFlight];

        for (var i = 0; i < FramePools.Length; i++)
        {
            FramePools[i] = new VulkanDescriptorPool(
                api,
                device,
                debugNames,
                string.Create(CultureInfo.InvariantCulture, $"Descriptor frame chain {i}"),
                sizes);

            // No frame has used this slot yet. The sentinel makes the first allocation of the first
            // frame recycle rather than trusting that a fresh chain and serial zero agree.
            SlotSerials[i] = ulong.MaxValue;
        }

        PersistentPool = new VulkanDescriptorPool(api, device, debugNames, "Descriptor persistent chain", sizes, allowFree: true);
    }

    /// <summary>Gets what the allocator has been doing.</summary>
    public VulkanDescriptorAllocatorStatistics Statistics
    {
        get
        {
            var pools = 0;
            var peak = 0;

            foreach (var chain in FramePools)
            {
                pools += chain.PoolCount;
                peak = Math.Max(peak, chain.PeakAllocatedCount);
            }

            return new VulkanDescriptorAllocatorStatistics(
                FramePools.Length,
                FramePools[CurrentSlot].AllocatedCount,
                peak,
                pools,
                PersistentPool.AllocatedCount);
        }
    }

    private int CurrentSlot => Math.Clamp(FrameRing.FrameIndex, 0, FramePools.Length - 1);

    /// <summary>
    /// Allocates a descriptor set that lives until the frame recording it retires.
    /// </summary>
    /// <param name="layout">The layout to allocate against.</param>
    /// <returns>The set. Valid for this frame only.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The allocator has been disposed.</exception>
    /// <remarks>The normal path. A set costs a bump of a pool offset, and the whole frame's worth is
    /// returned in one <c>vkResetDescriptorPool</c> rather than freed individually.</remarks>
    public DescriptorSet AllocateForFrame(VulkanDescriptorSetLayout layout)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        ArgumentNullException.ThrowIfNull(layout);

        var slot = CurrentSlot;
        RecycleIfNewFrame(slot);

        return FramePools[slot].Allocate(layout);
    }

    /// <summary>
    /// Allocates a descriptor set that survives frame boundaries until <see cref="FreePersistent"/>.
    /// </summary>
    /// <param name="layout">The layout to allocate against.</param>
    /// <returns>The set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The allocator has been disposed.</exception>
    /// <remarks>
    /// For a set whose contents genuinely do not change per frame, such as the reserved global textures
    /// of set 2 once a scene is loaded. Rewriting one while a frame that references it is still in flight
    /// is undefined, so treat it as immutable between <see cref="IDevice.WaitIdle"/> points unless the
    /// caller is doing its own tracking.
    /// </remarks>
    public DescriptorSet AllocatePersistent(VulkanDescriptorSetLayout layout)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        ArgumentNullException.ThrowIfNull(layout);

        return PersistentPool.Allocate(layout);
    }

    /// <summary>Returns a persistent set.</summary>
    /// <param name="set">The set, from <see cref="AllocatePersistent"/>.</param>
    /// <exception cref="ObjectDisposedException">The allocator has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The set did not come from the persistent chain.</exception>
    /// <remarks>Only legal once no in-flight frame can still reference it.</remarks>
    public void FreePersistent(DescriptorSet set)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        PersistentPool.Free(set);
    }

    /// <summary>
    /// Recycles the current frame slot's chain if it has not already been recycled for this frame.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The allocator has been disposed.</exception>
    /// <remarks>Allocation does this for itself. Calling it explicitly after
    /// <see cref="VulkanFrameRing.BeginFrame"/> only moves the cost earlier.</remarks>
    public void BeginFrame()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        RecycleIfNewFrame(CurrentSlot);
    }

    private void RecycleIfNewFrame(int slot)
    {
        var serial = FrameRing.CurrentSerial;

        if (SlotSerials[slot] == serial)
        {
            return;
        }

        // The ring has already waited for whatever this slot held, because that wait is the first thing
        // BeginFrame does and no allocation can reach here before it.
        FramePools[slot].Reset();
        SlotSerials[slot] = serial;
    }

    /// <summary>Destroys every pool this allocator owns.</summary>
    /// <remarks>Only legal once nothing referencing the sets is executing.</remarks>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        foreach (var chain in FramePools)
        {
            chain.Dispose();
        }

        PersistentPool.Dispose();
    }
}
