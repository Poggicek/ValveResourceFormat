using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.OpenGL;

namespace ValveResourceFormat.Renderer.Buffers
{
    /// <summary>
    /// Base class for OpenGL buffer objects with automatic binding management.
    /// </summary>
    public abstract class Buffer
    {
        /// <summary>Gets the OpenGL buffer target type.</summary>
        public BufferTarget Target { get; }

        /// <summary>Gets the OpenGL buffer object handle, or 0 when there is no storage yet or the device
        /// is not an OpenGL one.</summary>
        /// <remarks>Derived from <see cref="RhiBuffer"/> rather than owned. A buffer's identity is now the
        /// <see cref="IBuffer"/> the device handed out; this is the OpenGL name inside it, kept because the
        /// renderer still has direct GL call sites that need one.</remarks>
        public int Handle => (rhiBuffer as GLBuffer)?.Handle ?? 0;

        /// <summary>Gets the shader binding point index.</summary>
        public int BindingPoint { get; }
        /// <summary>Gets the debug name for this buffer.</summary>
        public string Name { get; }

        /// <summary>Gets or sets the current size of the buffer in bytes.</summary>
        public virtual int Size { get; set; }

        private readonly IDevice? explicitDevice;
        private IBuffer? rhiBuffer;

        /// <summary>Initializes a new buffer with the given target, binding point, and debug name.</summary>
        /// <param name="target">The OpenGL buffer target, which also decides <see cref="RhiUsage"/>.</param>
        /// <param name="bindingPoint">The shader binding point index.</param>
        /// <param name="name">Debug name, surfaced to graphics debuggers.</param>
        /// <param name="device">The device to allocate through, or <see langword="null"/> to resolve one
        /// from <see cref="RendererDevice"/>.</param>
        /// <remarks>No storage is allocated here. A buffer's size is not known until a subclass has one to
        /// put in it, and <see cref="IDevice.CreateBuffer"/> takes the size up front, so allocation happens
        /// at the first <see cref="EnsureStorage"/>. That is also why this no longer creates an OpenGL name
        /// eagerly: a name without storage is an OpenGL-only concept with no Vulkan equivalent.</remarks>
        protected Buffer(BufferTarget target, int bindingPoint, string name, IDevice? device = null)
        {
            Target = target;
            BindingPoint = bindingPoint;
            Name = name;
            explicitDevice = device;
        }

        /// <summary>
        /// Gets this buffer as an <see cref="IBuffer"/>, allocating its storage if it has none.
        /// </summary>
        /// <remarks>This is the buffer, not a view onto it: the device created it and
        /// <see cref="Delete"/> destroys it through the device. Sized from <see cref="Size"/>, so a
        /// subclass must set that before anything reads this.</remarks>
        public IBuffer RhiBuffer
        {
            get
            {
                EnsureStorage(Size);

                return rhiBuffer ?? throw new InvalidOperationException(
                    $"Buffer '{Name}' has no storage: its size is {Size}. Set Size, or upload data, before asking for the RHI buffer.");
            }
        }

        /// <summary>Gets the device this buffer allocates through.</summary>
        protected IDevice? Device => RendererDevice.Resolve(explicitDevice);

        /// <summary>
        /// Allocates or reallocates the storage so it is exactly <paramref name="sizeInBytes"/> long.
        /// </summary>
        /// <param name="sizeInBytes">The size the buffer must have. Zero or less releases the storage.</param>
        /// <returns><see langword="true"/> when new storage was created, so the caller knows the contents
        /// are undefined and must be written.</returns>
        /// <remarks>
        /// Reallocating rather than resizing is what the OpenGL path already did &#8212;
        /// <c>glNamedBufferData</c> discards and replaces the store &#8212; and it is the only thing a
        /// <c>VkBuffer</c> can do, since its size is fixed at creation. The old storage goes through
        /// <see cref="IDevice.DeferredDestroy"/>, not a direct delete, because a frame in flight may still
        /// be reading it.
        /// </remarks>
        protected bool EnsureStorage(int sizeInBytes)
        {
            if (rhiBuffer is not null && rhiBuffer.SizeInBytes == sizeInBytes)
            {
                return false;
            }

            ReleaseStorage();

            if (sizeInBytes <= 0)
            {
                return false;
            }

            var device = Device;

            if (device is null)
            {
                // No device yet: the legacy OpenGL path, which is what the renderer ran on before the RHI
                // existed and what tooling without a presentation layer still runs on.
                GL.CreateBuffers(1, out int handle);
                GL.NamedBufferData(handle, sizeInBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                rhiBuffer = GLBuffer.Wrap(handle, sizeInBytes, RhiUsage, RhiMemory, Name);
                ownsLegacyHandle = true;
            }
            else
            {
                rhiBuffer = device.CreateBuffer(new BufferDesc(sizeInBytes, RhiUsage, RhiMemory, Name));
                ownsLegacyHandle = false;
            }

            Size = sizeInBytes;
            return true;
        }

        private bool ownsLegacyHandle;

        /// <summary>Writes bytes into the buffer, allocating storage first when it has none.</summary>
        /// <param name="data">The bytes to write.</param>
        /// <param name="offsetInBytes">Byte offset to write at.</param>
        /// <remarks>Goes through <see cref="IDevice.UploadBuffer"/>, which stages the copy when the
        /// destination is not host visible. On Vulkan that staging happens outside any frame, which is
        /// correct here: these buffers are filled at load time.</remarks>
        protected void Upload(ReadOnlySpan<byte> data, int offsetInBytes = 0)
        {
            EnsureStorage(Math.Max(Size, offsetInBytes + data.Length));

            if (rhiBuffer is null || data.IsEmpty)
            {
                return;
            }

            var device = Device;

            if (device is not null)
            {
                device.UploadBuffer(rhiBuffer, offsetInBytes, data);
                return;
            }

            ((GLBuffer)rhiBuffer).Upload(offsetInBytes, data);
        }

        private void ReleaseStorage()
        {
            if (rhiBuffer is null)
            {
                return;
            }

            var device = Device;

            if (device is not null && !ownsLegacyHandle)
            {
                device.DeferredDestroy(rhiBuffer);
            }
            else if (ownsLegacyHandle && rhiBuffer is GLBuffer legacy)
            {
                GL.DeleteBuffer(legacy.Handle);
                legacy.Dispose();
            }

            rhiBuffer = null;
        }

        /// <summary>Gets every use this buffer is put to, as the RHI models them.</summary>
        /// <remarks>OpenGL decides what a buffer is from the target it is bound to, so this is derived
        /// from <see cref="Target"/>. Vulkan needs it stated at creation, which is what
        /// <see cref="BufferDesc.Usage"/> is for.</remarks>
        protected virtual BufferUsage RhiUsage => Target switch
        {
            BufferTarget.UniformBuffer => BufferUsage.Uniform | BufferUsage.CopyDestination,
            BufferTarget.ShaderStorageBuffer => BufferUsage.Storage | BufferUsage.CopySource | BufferUsage.CopyDestination,
            BufferTarget.ArrayBuffer => BufferUsage.Vertex | BufferUsage.CopyDestination,
            BufferTarget.ElementArrayBuffer => BufferUsage.Index | BufferUsage.CopyDestination,
            BufferTarget.DrawIndirectBuffer => BufferUsage.Indirect | BufferUsage.CopyDestination,
            _ => BufferUsage.CopySource | BufferUsage.CopyDestination,
        };

        /// <summary>Gets where this buffer's memory lives, as the RHI models it.</summary>
        /// <remarks>Device local unless a subclass maps its storage persistently, which
        /// <see cref="StorageBuffer"/> does for its readback path.</remarks>
        protected virtual BufferMemory RhiMemory => BufferMemory.DeviceLocal;

        /// <summary>Binds this buffer to its binding point using <c>glBindBufferBase</c>.</summary>
        /// <remarks><b>Not ported.</b> See <see cref="BindBufferBase(ReservedBufferSlots)"/>.</remarks>
        public void BindBufferBase() => BindBufferBase(BindingPoint);

        /// <summary>Binds this buffer to a binding point other than its own. Binding one buffer to several
        /// points at once is allowed; all of the blocks reading it are declared <c>readonly</c>.</summary>
        /// <param name="bindingPoint">The slot to bind to instead of <see cref="BindingPoint"/>.</param>
        /// <remarks><b>Not ported.</b> See <see cref="BindBufferBase(ReservedBufferSlots)"/>.</remarks>
        public void BindBufferBase(ReservedBufferSlots bindingPoint) => BindBufferBase((int)bindingPoint);

        /// <summary>
        /// Binds this buffer to a binding point using <c>glBindBufferBase</c>.
        /// </summary>
        /// <param name="bindingPoint">The slot to bind to.</param>
        /// <remarks>
        /// <b>This is not ported and does nothing on a non-OpenGL device.</b> The replacement is
        /// <see cref="ICommandList.BindUniformBuffer"/> or <see cref="ICommandList.BindStorageBuffer"/>,
        /// recorded on a command list the caller holds; every caller of this method still has to move.
        /// <para>
        /// Skipping rather than throwing keeps the OpenGL oracle byte-identical and keeps the Vulkan probe
        /// reaching later stages, but be aware of what it costs: an OpenGL buffer binding is global state
        /// that persists, so a missing or misdirected bind renders no differently in the golden suite.
        /// Binding is invisible to the oracle by construction, and nothing here is verified by it.
        /// </para>
        /// </remarks>
        public void BindBufferBase(int bindingPoint)
        {
            if (!RendererDevice.IsOpenGL(explicitDevice))
            {
                return;
            }

            GL.BindBufferBase((BufferRangeTarget)Target, bindingPoint, Handle);
        }

        /// <summary>Destroys this buffer's storage through the device that created it.</summary>
        public virtual void Delete() => ReleaseStorage();
    }
}
