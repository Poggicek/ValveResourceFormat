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
        /// <summary>Gets the OpenGL buffer object handle.</summary>
        public int Handle { get; }
        /// <summary>Gets the shader binding point index.</summary>
        public int BindingPoint { get; }
        /// <summary>Gets the debug name for this buffer.</summary>
        public string Name { get; }

        /// <summary>Gets or sets the current size of the buffer in bytes.</summary>
        public virtual int Size { get; set; }


        /// <summary>Initializes a new buffer with the given target, binding point, and debug name.</summary>
        protected Buffer(BufferTarget target, int bindingPoint, string name)
        {
            Target = target;
            GL.CreateBuffers(1, out int handle);
            Handle = handle;
            BindingPoint = bindingPoint;
            Name = name;

#if DEBUG
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, Handle, Name.Length, Name);
#endif
        }

        private GLBuffer? rhiBuffer;

        /// <summary>
        /// Gets this buffer as an <see cref="IBuffer"/>, so it can be passed to <see cref="ICommandList"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A non-owning wrapper around the same OpenGL object, not a second allocation: this buffer keeps
        /// ownership and <see cref="Delete"/> is still what frees it. It is the bridge that lets call
        /// sites move onto the RHI before their allocations do, and it goes away as those allocations
        /// move onto <see cref="IDevice.CreateBuffer"/>.
        /// </para>
        /// <para>
        /// Rebuilt whenever <see cref="Size"/> changes, because the buffers derived from this type
        /// reallocate in place through <c>glNamedBufferData</c> and a wrapper cached across that would
        /// report a stale size.
        /// </para>
        /// </remarks>
        public IBuffer RhiBuffer
        {
            get
            {
                if (rhiBuffer is null || rhiBuffer.SizeInBytes != Size || rhiBuffer.Handle != Handle)
                {
                    rhiBuffer = GLBuffer.Wrap(Handle, Size, RhiUsage, RhiMemory, Name);
                }

                return rhiBuffer;
            }
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
        public void BindBufferBase()
        {
            GL.BindBufferBase((BufferRangeTarget)Target, BindingPoint, Handle);
        }

        /// <summary>Binds this buffer to a binding point other than its own. Binding one buffer to several
        /// points at once is allowed; all of the blocks reading it are declared <c>readonly</c>.</summary>
        /// <param name="bindingPoint">The slot to bind to instead of <see cref="BindingPoint"/>.</param>
        public void BindBufferBase(ReservedBufferSlots bindingPoint)
        {
            GL.BindBufferBase((BufferRangeTarget)Target, (int)bindingPoint, Handle);
        }

        /// <summary>Deletes the underlying OpenGL buffer object.</summary>
        public virtual void Delete()
        {
            GL.DeleteBuffer(Handle);
        }
    }
}
