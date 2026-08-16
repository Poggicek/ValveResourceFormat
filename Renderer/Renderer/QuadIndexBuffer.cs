using System.Buffers;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.OpenGL;

namespace ValveResourceFormat.Renderer
{
    /// <summary>
    /// Pre-computed index buffer for rendering quads as triangle pairs.
    /// </summary>
    /// <remarks>
    /// Shared by every quad renderer in the tree &#8212; the sprite, trail and morph passes and the text
    /// overlay &#8212; which is why it carries both shapes of the same buffer. On OpenGL it is the
    /// <c>glCreateBuffers</c> name those renderers' vertex arrays are built from, exposed as
    /// <see cref="GLHandle"/>; on any other backend there is no loose name to hand out and
    /// <see cref="GetBuffer"/> allocates the identical indices through the device instead. One allocation
    /// either way: the callers that used to wrap <see cref="GLHandle"/> themselves now ask here, so the
    /// contents cannot drift between them.
    /// </remarks>
    public class QuadIndexBuffer
    {
        /// <summary>
        /// Gets the OpenGL buffer object handle, or 0 on a backend that has no OpenGL in it.
        /// </summary>
        /// <remarks>Zero is not an error there: a device with no OpenGL underneath has no name to make,
        /// and the callers that need one are the vertex arrays, which that backend does not build.</remarks>
        public int GLHandle { get; }

        /// <summary>Gets the size of the index data in bytes.</summary>
        public int SizeInBytes { get; }

        private readonly int indexCount;

        // The same buffer in the two shapes an ICommandList can be handed. Kept apart rather than in one
        // field because which is correct depends on the device the draw records on, and a run that
        // allocated this before the presentation layer named its device must not be stuck with the guess.
        private GLBuffer? glRhiBuffer;
        private IBuffer? deviceBuffer;

        /// <summary>Allocates the buffer and fills it with quad-to-triangle index patterns.</summary>
        /// <param name="size">Total number of indices to generate (must be a multiple of 6).</param>
        /// <param name="device">The device to allocate through, or <see langword="null"/> to resolve one.
        /// Only decides whether the OpenGL name below is made; the device-side storage is created lazily
        /// by <see cref="GetBuffer"/>, once a draw asks for it.</param>
        public QuadIndexBuffer(int size, IDevice? device = null)
        {
            indexCount = size;
            SizeInBytes = size * sizeof(ushort);

#if DEBUG
            System.Diagnostics.Debug.Assert(size % 6 == 0);
#endif

            if (!RendererDevice.IsOpenGL(device))
            {
                // Nothing to create here. A VkBuffer needs a device to allocate from and a queue to upload
                // through, and both are reached in GetBuffer; making a GL name first would be a direct
                // OpenGL call on a device that has no context behind it.
                return;
            }

            GL.CreateBuffers(1, out int handle);
            GLHandle = handle;

#if DEBUG
            var bufferLabel = nameof(QuadIndexBuffer);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, handle, bufferLabel.Length, bufferLabel);
#endif

            var indicesBytes = ArrayPool<byte>.Shared.Rent(SizeInBytes);

            try
            {
                WriteIndices(MemoryMarshal.Cast<byte, ushort>(indicesBytes.AsSpan()), size);

                GL.NamedBufferData(handle, SizeInBytes, indicesBytes, BufferUsageHint.StaticDraw);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(indicesBytes);
            }
        }

        /// <summary>
        /// Gets this buffer as an <see cref="IBuffer"/>, for a draw recorded through an
        /// <see cref="ICommandList"/>.
        /// </summary>
        /// <param name="device">The device the draw is recorded on, or <see langword="null"/> to resolve
        /// one.</param>
        /// <returns>A non-owning view of <see cref="GLHandle"/> on OpenGL, and a device-allocated buffer
        /// holding the same indices on any other backend.</returns>
        /// <remarks>
        /// The two are memoized separately and chosen per call rather than once. A process can create this
        /// before its device exists &#8212; the cache that owns it is built during startup &#8212; and
        /// caching whichever answer the first caller happened to get would hand a <c>GLBuffer</c> to a
        /// Vulkan command list, which is exactly the failure this replaces.
        /// </remarks>
        public IBuffer GetBuffer(IDevice? device = null)
        {
            var resolved = RendererDevice.Resolve(device);

            if (RendererDevice.IsOpenGL(resolved))
            {
                return glRhiBuffer ??= GLBuffer.Wrap(
                    GLHandle, SizeInBytes, BufferUsage.Index, BufferMemory.DeviceLocal, nameof(QuadIndexBuffer));
            }

            if (deviceBuffer is not null)
            {
                return deviceBuffer;
            }

            var indices = new ushort[indexCount];
            WriteIndices(indices, indexCount);

            var created = resolved!.CreateBuffer(new BufferDesc(
                SizeInBytes,
                BufferUsage.Index | BufferUsage.CopyDestination,
                BufferMemory.DeviceLocal,
                nameof(QuadIndexBuffer)));

            resolved.UploadBuffer(created, 0, MemoryMarshal.AsBytes<ushort>(indices));

            return deviceBuffer = created;
        }

        /// <summary>
        /// Writes the quad-to-triangle index pattern, two triangles per four vertices in the winding order
        /// every quad renderer in the tree emits its corners in.
        /// </summary>
        /// <param name="indices">Destination, which may be longer than <paramref name="size"/>.</param>
        /// <param name="size">Number of indices to write.</param>
        private static void WriteIndices(Span<ushort> indices, int size)
        {
            for (var i = 0; i < size / 6; ++i)
            {
                indices[(i * 6) + 0] = (ushort)((i * 4) + 0);
                indices[(i * 6) + 1] = (ushort)((i * 4) + 1);
                indices[(i * 6) + 2] = (ushort)((i * 4) + 2);
                indices[(i * 6) + 3] = (ushort)((i * 4) + 0);
                indices[(i * 6) + 4] = (ushort)((i * 4) + 2);
                indices[(i * 6) + 5] = (ushort)((i * 4) + 3);
            }
        }
    }
}
