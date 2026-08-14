using OpenTK.Graphics.OpenGL;
using GLApi = OpenTK.Graphics.OpenGL.GL;

namespace ValveResourceFormat.Renderer.RHI.OpenGL;

/// <summary>
/// <see cref="IBuffer"/> on OpenGL: one immutable buffer object allocated with
/// <c>glNamedBufferStorage</c>, persistently mapped when the memory is host visible.
/// </summary>
/// <remarks>
/// <para>
/// Storage is immutable, which is what lets a host visible buffer stay mapped for its whole life
/// instead of being mapped and unmapped per frame. That is the same trick
/// <see cref="Buffers.StorageBuffer"/> already plays for its readback path; here it is the rule for
/// every <see cref="BufferMemory.HostUpload"/> and <see cref="BufferMemory.HostReadback"/> buffer,
/// because a persistent mapping is the only shape Vulkan's host visible memory has.
/// </para>
/// <para>
/// <see cref="BufferUsage"/> is recorded but not acted on. OpenGL decides what a buffer is from the
/// target it is bound to, not from how it was created; Vulkan needs the complete set up front. Keeping
/// it here means <see cref="GlBarrierTranslation"/> can still ask what a buffer is for.
/// </para>
/// </remarks>
public sealed class GLBuffer : IBuffer
{
    private IntPtr mapping;
    private readonly bool ownsHandle;

    /// <summary>Gets the OpenGL buffer object name, or 0 once disposed.</summary>
    public int Handle { get; private set; }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public int SizeInBytes { get; }

    /// <inheritdoc/>
    public BufferUsage Usage { get; }

    /// <inheritdoc/>
    public BufferMemory Memory { get; }

    /// <summary>Gets a value indicating whether this buffer is persistently mapped.</summary>
    public bool IsMapped => mapping != IntPtr.Zero;

    /// <inheritdoc/>
    public unsafe Span<byte> MappedData => mapping != IntPtr.Zero
        ? new Span<byte>((void*)mapping, SizeInBytes)
        : throw new InvalidOperationException($"Buffer '{Name}' is {Memory} and has no mapping. Only {nameof(BufferMemory.HostUpload)} and {nameof(BufferMemory.HostReadback)} buffers can be written through {nameof(MappedData)}.");

    /// <summary>Allocates a buffer.</summary>
    /// <param name="desc">Creation parameters.</param>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="BufferDesc.SizeInBytes"/> is not positive.</exception>
    public GLBuffer(in BufferDesc desc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.SizeInBytes, nameof(desc));

        Name = desc.Name ?? string.Empty;
        SizeInBytes = desc.SizeInBytes;
        Usage = desc.Usage;
        Memory = desc.Memory;
        ownsHandle = true;

        GLApi.CreateBuffers(1, out int handle);
        Handle = handle;

        var storage = StorageFlags(desc.Memory);
        GLApi.NamedBufferStorage(handle, SizeInBytes, IntPtr.Zero, storage);

        if (desc.Memory != BufferMemory.DeviceLocal)
        {
            mapping = GLApi.MapNamedBufferRange(handle, IntPtr.Zero, SizeInBytes, AccessFlags(desc.Memory));
        }

#if DEBUG
        if (Name.Length > 0)
        {
            GLApi.ObjectLabel(ObjectLabelIdentifier.Buffer, handle, Name.Length, Name);
        }
#endif
    }

    private GLBuffer(int handle, int sizeInBytes, BufferUsage usage, BufferMemory memory, string name)
    {
        Handle = handle;
        SizeInBytes = sizeInBytes;
        Usage = usage;
        Memory = memory;
        Name = name ?? string.Empty;
        ownsHandle = false;
    }

    /// <summary>
    /// Wraps an existing OpenGL buffer object as an <see cref="IBuffer"/> without taking ownership of it.
    /// </summary>
    /// <param name="handle">The existing buffer object name.</param>
    /// <param name="sizeInBytes">Its size in bytes.</param>
    /// <param name="usage">Every use it is put to, so barrier translation can see it.</param>
    /// <param name="memory">Where its memory lives.</param>
    /// <param name="name">Debug name.</param>
    /// <returns>A non-owning view. <see cref="Dispose"/> does not delete the buffer.</returns>
    /// <remarks>
    /// The bridge for buffers the renderer still creates itself &#8212; <see cref="Buffers.Buffer"/> and
    /// everything derived from it. It exists so a call site can be handed to
    /// <see cref="ICommandList"/> before its allocation has been ported, and it disappears as those
    /// allocations move onto <see cref="IDevice.CreateBuffer"/>. Not a long-term surface: it cannot
    /// exist on Vulkan, where there is no loose handle to adopt.
    /// </remarks>
    public static GLBuffer Wrap(int handle, int sizeInBytes, BufferUsage usage, BufferMemory memory, string name)
        => new(handle, sizeInBytes, usage, memory, name);

    /// <summary>Writes bytes into the buffer, through the mapping when there is one.</summary>
    /// <param name="offsetInBytes">Byte offset to write at.</param>
    /// <param name="data">The bytes to write.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range falls outside the buffer.</exception>
    /// <remarks>Backs <see cref="IDevice.UploadBuffer"/>. A device local buffer takes the
    /// <c>glNamedBufferSubData</c> path, which is the driver's own staging copy; on Vulkan this is a real
    /// staging buffer and a queue submission, which is why callers must not treat it as free.</remarks>
    public unsafe void Upload(int offsetInBytes, ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offsetInBytes);

        if (data.IsEmpty)
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(offsetInBytes + data.Length, SizeInBytes);

        if (mapping != IntPtr.Zero)
        {
            data.CopyTo(new Span<byte>((void*)(mapping + offsetInBytes), SizeInBytes - offsetInBytes));
            FlushRange(offsetInBytes, data.Length);
            return;
        }

        fixed (byte* source = data)
        {
            GLApi.NamedBufferSubData(Handle, offsetInBytes, data.Length, (IntPtr)source);
        }
    }

    /// <inheritdoc/>
    /// <remarks>The mapping is coherent, so nothing has to be flushed on OpenGL. The range is still
    /// validated: a call site that gets it wrong here is a call site that corrupts on Vulkan, where the
    /// flush is real.</remarks>
    public void FlushRange(int offsetInBytes, int sizeInBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offsetInBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeInBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offsetInBytes + sizeInBytes, SizeInBytes);

        if (mapping == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Buffer '{Name}' is {Memory} and has no mapping to flush.");
        }
    }

    // DynamicStorageBit is what makes glNamedBufferSubData legal against immutable storage, which is the
    // only route into a device local buffer. Host visible memory is mapped coherently instead: the
    // renderer writes these every frame and an explicit flush per write would cost more than coherence.
    private static BufferStorageFlags StorageFlags(BufferMemory memory) => memory switch
    {
        BufferMemory.DeviceLocal => BufferStorageFlags.DynamicStorageBit,
        BufferMemory.HostUpload => BufferStorageFlags.MapWriteBit | BufferStorageFlags.MapPersistentBit | BufferStorageFlags.MapCoherentBit | BufferStorageFlags.DynamicStorageBit,
        BufferMemory.HostReadback => BufferStorageFlags.MapReadBit | BufferStorageFlags.MapPersistentBit | BufferStorageFlags.MapCoherentBit,
        _ => throw new ArgumentOutOfRangeException(nameof(memory), memory, "Unknown buffer memory."),
    };

    private static BufferAccessMask AccessFlags(BufferMemory memory) => memory switch
    {
        BufferMemory.HostUpload => BufferAccessMask.MapWriteBit | BufferAccessMask.MapPersistentBit | BufferAccessMask.MapCoherentBit,
        BufferMemory.HostReadback => BufferAccessMask.MapReadBit | BufferAccessMask.MapPersistentBit | BufferAccessMask.MapCoherentBit,
        _ => throw new ArgumentOutOfRangeException(nameof(memory), memory, "Only host visible memory is mapped."),
    };

    /// <summary>Unmaps and deletes the buffer object. A wrapped handle is left alone.</summary>
    public void Dispose()
    {
        if (Handle == 0)
        {
            return;
        }

        if (!ownsHandle)
        {
            Handle = 0;
            return;
        }

        if (mapping != IntPtr.Zero)
        {
            GLApi.UnmapNamedBuffer(Handle);
            mapping = IntPtr.Zero;
        }

        GLApi.DeleteBuffer(Handle);
        Handle = 0;
    }
}
