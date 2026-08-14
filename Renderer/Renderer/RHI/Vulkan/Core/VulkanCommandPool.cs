using Silk.NET.Vulkan;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Core;

/// <summary>
/// A command pool and the command buffers allocated from it, recycled as a unit once per frame.
/// </summary>
/// <remarks>
/// <para>
/// One of these exists per frame slot per queue family. Recycling happens with
/// <c>vkResetCommandPool</c> on the whole pool rather than per command buffer: resetting individually
/// forces the driver to keep a per-buffer allocator and return memory piecemeal, while resetting the
/// pool hands back one arena. Because the pool is reset as a unit, every buffer it ever handed out
/// becomes invalid at once, which is exactly the frame-slot lifetime.
/// </para>
/// <para>
/// This means a pool must never be shared between threads recording simultaneously. Command pools are
/// externally synchronized in Vulkan; giving each recording thread its own pool per frame slot is the
/// intended structure.
/// </para>
/// </remarks>
public sealed unsafe class VulkanCommandPool : IDisposable
{
    private const int GrowChunk = 4;

    private readonly Vk Api;
    private readonly Device Device;
    private readonly VulkanDebugNames DebugNames;
    private readonly string Name;
    private readonly List<CommandBuffer> Buffers = [];

    private CommandPool Pool;
    private int NextFree;
    private bool Disposed;

    /// <summary>Gets the queue family the recorded work can be submitted to.</summary>
    public uint QueueFamilyIndex { get; }

    /// <summary>Gets how many command buffers have been allocated from this pool over its lifetime.</summary>
    public int AllocatedCount => Buffers.Count;

    /// <summary>Initializes a new instance of the <see cref="VulkanCommandPool"/> class.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="queueFamilyIndex">The family recorded work targets.</param>
    /// <param name="debugNames">Used to name the pool and its buffers.</param>
    /// <param name="name">Debug name.</param>
    public VulkanCommandPool(
        Vk api,
        Device device,
        uint queueFamilyIndex,
        VulkanDebugNames debugNames,
        string name)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(debugNames);
        ArgumentNullException.ThrowIfNull(name);

        Api = api;
        Device = device;
        QueueFamilyIndex = queueFamilyIndex;
        DebugNames = debugNames;
        Name = name;

        var info = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = queueFamilyIndex,

            // Transient tells the driver the buffers are short lived, which lets it use a cheaper
            // allocation strategy. No ResetCommandBuffer bit: the whole pool is reset at once.
            Flags = CommandPoolCreateFlags.TransientBit,
        };

        Api.CreateCommandPool(Device, &info, null, out Pool).Check("vkCreateCommandPool");
        DebugNames.SetName(ObjectType.CommandPool, Pool.Handle, name);
    }

    /// <summary>Takes a command buffer, ready to record into.</summary>
    /// <param name="name">Debug name for the recorded work.</param>
    /// <returns>A command buffer already in the recording state.</returns>
    public CommandBuffer Acquire(string name)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        if (NextFree == Buffers.Count)
        {
            Grow();
        }

        var buffer = Buffers[NextFree++];
        DebugNames.SetName(buffer, name);

        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        Api.BeginCommandBuffer(buffer, &begin).Check("vkBeginCommandBuffer");
        return buffer;
    }

    private void Grow()
    {
        var info = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = Pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = GrowChunk,
        };

        var fresh = new CommandBuffer[GrowChunk];

        fixed (CommandBuffer* p = fresh)
        {
            Api.AllocateCommandBuffers(Device, &info, p).Check("vkAllocateCommandBuffers");
        }

        Buffers.AddRange(fresh);
    }

    /// <summary>Recycles every command buffer handed out since the last reset.</summary>
    /// <remarks>Only legal once the GPU has finished the work recorded into them. The frame ring
    /// waits on the timeline before calling this.</remarks>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        Api.ResetCommandPool(Device, Pool, 0).Check("vkResetCommandPool");
        NextFree = 0;
    }

    /// <summary>Destroys the pool and every command buffer allocated from it.</summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        if (Pool.Handle != 0)
        {
            // Destroying the pool frees its command buffers; freeing them individually first is
            // redundant work the specification explicitly does not require.
            Api.DestroyCommandPool(Device, Pool, null);
            Pool = default;
        }

        Buffers.Clear();
    }
}
