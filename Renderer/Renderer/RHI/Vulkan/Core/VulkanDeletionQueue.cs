using System.Threading;
using Silk.NET.Vulkan;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Core;

/// <summary>
/// Holds destroyed resources until every frame that could still reference them has retired.
/// </summary>
/// <remarks>
/// <para>
/// This is what backs <see cref="IDevice.DeferredDestroy"/>. Entries are stamped with the serial of
/// the frame during which destruction was requested and released once the GPU timeline has passed it,
/// which is the only moment at which no queued command buffer can still name the handle.
/// </para>
/// <para>
/// The queue is a FIFO rather than a sorted structure because frame serials are monotonic: an entry
/// enqueued later can never become collectable earlier, so the first entry that is not ready ends the
/// sweep.
/// </para>
/// <para>
/// Several existing call sites free eagerly and will have to route through here.
/// <c>QuadOverdraw.Prepare</c> is the clearest: on a resize it calls <c>Delete()</c> on the lock and
/// count textures immediately, which frees images a frame still in flight is sampling.
/// </para>
/// </remarks>
public sealed unsafe class VulkanDeletionQueue : IDisposable
{
    private readonly Vk Api;
    private readonly Device Device;
    private readonly Queue<Entry> Pending = new();
    private readonly Lock Gate = new();

    private bool Disposed;

    /// <summary>Gets the number of resources waiting to be released.</summary>
    public int PendingCount
    {
        get
        {
            lock (Gate)
            {
                return Pending.Count;
            }
        }
    }

    /// <summary>Initializes a new instance of the <see cref="VulkanDeletionQueue"/> class.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device the handles belong to.</param>
    public VulkanDeletionQueue(Vk api, Device device)
    {
        ArgumentNullException.ThrowIfNull(api);

        Api = api;
        Device = device;
    }

    /// <summary>Queues a buffer and the memory backing it.</summary>
    /// <param name="frameSerial">The serial of the frame during which destruction was requested.</param>
    /// <param name="buffer">The buffer to destroy.</param>
    /// <param name="allocation">Its allocation, to return to the allocator.</param>
    /// <param name="allocator">The allocator that owns <paramref name="allocation"/>.</param>
    public void Enqueue(
        ulong frameSerial,
        Silk.NET.Vulkan.Buffer buffer,
        VulkanAllocation allocation,
        VulkanMemoryAllocator allocator)
        => Add(new Entry(frameSerial, EntryKind.Buffer, buffer.Handle, allocation, allocator, null));

    /// <summary>Queues an image and the memory backing it.</summary>
    /// <param name="frameSerial">The serial of the frame during which destruction was requested.</param>
    /// <param name="image">The image to destroy.</param>
    /// <param name="allocation">Its allocation, to return to the allocator.</param>
    /// <param name="allocator">The allocator that owns <paramref name="allocation"/>.</param>
    public void Enqueue(
        ulong frameSerial,
        Image image,
        VulkanAllocation allocation,
        VulkanMemoryAllocator allocator)
        => Add(new Entry(frameSerial, EntryKind.Image, image.Handle, allocation, allocator, null));

    /// <summary>Queues an image view.</summary>
    /// <param name="frameSerial">The serial of the frame during which destruction was requested.</param>
    /// <param name="view">The view to destroy.</param>
    public void Enqueue(ulong frameSerial, ImageView view)
        => Add(new Entry(frameSerial, EntryKind.ImageView, view.Handle, default, null, null));

    /// <summary>Queues a sampler.</summary>
    /// <param name="frameSerial">The serial of the frame during which destruction was requested.</param>
    /// <param name="sampler">The sampler to destroy.</param>
    public void Enqueue(ulong frameSerial, Sampler sampler)
        => Add(new Entry(frameSerial, EntryKind.Sampler, sampler.Handle, default, null, null));

    /// <summary>Queues arbitrary cleanup, for handle types this class does not model.</summary>
    /// <param name="frameSerial">The serial of the frame during which destruction was requested.</param>
    /// <param name="action">What to run once the frame has retired.</param>
    public void Enqueue(ulong frameSerial, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Add(new Entry(frameSerial, EntryKind.Callback, 0, default, null, action));
    }

    private void Add(in Entry entry)
    {
        if (Disposed)
        {
            return;
        }

        lock (Gate)
        {
            Pending.Enqueue(entry);
        }
    }

    /// <summary>Releases everything whose frame has retired.</summary>
    /// <param name="completedSerial">The highest frame serial the GPU has finished.</param>
    /// <returns>How many resources were released.</returns>
    public int Collect(ulong completedSerial)
    {
        if (Disposed)
        {
            return 0;
        }

        var released = 0;

        lock (Gate)
        {
            while (Pending.Count > 0)
            {
                var entry = Pending.Peek();

                if (entry.FrameSerial > completedSerial)
                {
                    break;
                }

                Pending.Dequeue();
                Release(entry);
                released++;
            }
        }

        return released;
    }

    /// <summary>Releases everything regardless of frame, for teardown.</summary>
    /// <remarks>Only safe after the device is idle. <see cref="VulkanCoreDevice.Dispose"/> waits first.</remarks>
    public void Flush()
    {
        lock (Gate)
        {
            while (Pending.Count > 0)
            {
                Release(Pending.Dequeue());
            }
        }
    }

    private void Release(in Entry entry)
    {
        switch (entry.Kind)
        {
            case EntryKind.Buffer:
                Api.DestroyBuffer(Device, new Silk.NET.Vulkan.Buffer(entry.Handle), null);
                entry.Allocator?.Free(entry.Allocation);
                break;

            case EntryKind.Image:
                Api.DestroyImage(Device, new Image(entry.Handle), null);
                entry.Allocator?.Free(entry.Allocation);
                break;

            case EntryKind.ImageView:
                Api.DestroyImageView(Device, new ImageView(entry.Handle), null);
                break;

            case EntryKind.Sampler:
                Api.DestroySampler(Device, new Sampler(entry.Handle), null);
                break;

            case EntryKind.Callback:
                entry.Callback?.Invoke();
                break;

            default:
                break;
        }
    }

    /// <summary>Releases anything still pending. The device must already be idle.</summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Flush();
        Disposed = true;
    }

    private enum EntryKind
    {
        Buffer,
        Image,
        ImageView,
        Sampler,
        Callback,
    }

    private readonly record struct Entry(
        ulong FrameSerial,
        EntryKind Kind,
        ulong Handle,
        VulkanAllocation Allocation,
        VulkanMemoryAllocator? Allocator,
        Action? Callback);
}
