
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.OpenGL;

namespace ValveResourceFormat.Renderer
{
    /// <summary>
    /// GPU vertex and index buffers created from <see cref="VBIB"/> mesh data.
    /// </summary>
    public class GPUMeshBuffers
    {
        /// <summary>Gets the OpenGL handles for each uploaded vertex buffer.</summary>
        public int[] VertexBuffers { get; private set; }

        /// <summary>Gets the OpenGL handles for each uploaded index buffer.</summary>
        public int[] IndexBuffers { get; private set; }

        private readonly IBuffer?[] rhiVertexBuffers;
        private readonly IBuffer?[] rhiIndexBuffers;
        private readonly int[] vertexBufferSizes;
        private readonly int[] indexBufferSizes;

        /// <summary>Gets one of this mesh's vertex buffers, for
        /// <see cref="ICommandList.BindVertexBuffer"/>.</summary>
        /// <param name="index">Which vertex buffer.</param>
        /// <returns>The buffer. This object owns it and <see cref="Delete"/> destroys it.</returns>
        public IBuffer RhiVertexBuffer(int index)
            => rhiVertexBuffers[index] ?? throw new ObjectDisposedException(nameof(GPUMeshBuffers));

        /// <summary>Gets one of this mesh's index buffers, for
        /// <see cref="ICommandList.BindIndexBuffer"/>.</summary>
        /// <param name="index">Which index buffer.</param>
        /// <returns>The buffer. This object owns it and <see cref="Delete"/> destroys it.</returns>
        public IBuffer RhiIndexBuffer(int index)
            => rhiIndexBuffers[index] ?? throw new ObjectDisposedException(nameof(GPUMeshBuffers));

        /// <summary>Gets the size in bytes of one of this mesh's vertex buffers.</summary>
        /// <param name="index">Which vertex buffer.</param>
        /// <returns>The size in bytes, as the source <see cref="VBIB"/> reported it.</returns>
        public int VertexBufferSize(int index) => vertexBufferSizes[index];

        /// <summary>Gets the size in bytes of one of this mesh's index buffers.</summary>
        /// <param name="index">Which index buffer.</param>
        /// <returns>The size in bytes, as the source <see cref="VBIB"/> reported it.</returns>
        public int IndexBufferSize(int index) => indexBufferSizes[index];

        /// <summary>Uploads all vertex and index buffers from the provided <see cref="VBIB"/> to the GPU.</summary>
        /// <param name="vbib">Source vertex and index buffer data.</param>
        public GPUMeshBuffers(VBIB vbib) : this(vbib, null)
        {
        }

        /// <summary>Uploads all vertex and index buffers from the provided <see cref="VBIB"/> to the GPU.</summary>
        /// <param name="vbib">Source vertex and index buffer data.</param>
        /// <param name="device">The device to allocate through, or <see langword="null"/> to resolve one
        /// from <see cref="RendererDevice"/>.</param>
        /// <remarks>The buffers are created at their final size and filled once, which is what mesh data is:
        /// uploaded at load time and never written again. On Vulkan that upload is staged outside any
        /// frame, which is why it is safe to do here rather than during rendering.</remarks>
        public GPUMeshBuffers(VBIB vbib, IDevice? device)
        {
            ArgumentNullException.ThrowIfNull(vbib);

            this.device = device;

            VertexBuffers = new int[vbib.VertexBuffers.Count];
            IndexBuffers = new int[vbib.IndexBuffers.Count];
            vertexBufferSizes = new int[vbib.VertexBuffers.Count];
            indexBufferSizes = new int[vbib.IndexBuffers.Count];
            rhiVertexBuffers = new IBuffer?[VertexBuffers.Length];
            rhiIndexBuffers = new IBuffer?[IndexBuffers.Length];

            for (var i = 0; i < vbib.VertexBuffers.Count; i++)
            {
                var source = vbib.VertexBuffers[i];
                vertexBufferSizes[i] = (int)source.TotalSizeInBytes;
                rhiVertexBuffers[i] = Allocate(vertexBufferSizes[i], VertexUsage, $"VertexBuffer{i}", source.Data);
                VertexBuffers[i] = HandleOf(rhiVertexBuffers[i]);
            }

            for (var i = 0; i < vbib.IndexBuffers.Count; i++)
            {
                var source = vbib.IndexBuffers[i];
                indexBufferSizes[i] = (int)source.TotalSizeInBytes;
                rhiIndexBuffers[i] = Allocate(indexBufferSizes[i], IndexUsage, $"IndexBuffer{i}", source.Data);
                IndexBuffers[i] = HandleOf(rhiIndexBuffers[i]);
            }
        }

        private const BufferUsage VertexUsage = BufferUsage.Vertex | BufferUsage.Storage | BufferUsage.CopyDestination;
        private const BufferUsage IndexUsage = BufferUsage.Index | BufferUsage.Storage | BufferUsage.CopyDestination;

        private readonly IDevice? device;

        private IDevice? Device => RendererDevice.Resolve(device);

        private static int HandleOf(IBuffer? buffer) => (buffer as GLBuffer)?.Handle ?? 0;

        private IBuffer Allocate(int sizeInBytes, BufferUsage usage, string name, byte[] data)
        {
            var resolved = Device;

            if (resolved is null)
            {
                // No device yet: the legacy OpenGL path, which is what the renderer ran on before the RHI
                // existed and what tooling without a presentation layer still runs on.
                GL.CreateBuffers(1, out int handle);
                GL.NamedBufferData(handle, sizeInBytes, data, BufferUsageHint.StaticDraw);
                return GLBuffer.Wrap(handle, sizeInBytes, usage, BufferMemory.DeviceLocal, name);
            }

            var buffer = resolved.CreateBuffer(new BufferDesc(sizeInBytes, usage, BufferMemory.DeviceLocal, name));
            resolved.UploadBuffer(buffer, 0, data.AsSpan(0, sizeInBytes));
            return buffer;
        }

        /// <summary>Deletes all GPU vertex and index buffers.</summary>
        /// <remarks>
        /// Any wrapper handed out by <see cref="RhiVertexBuffer"/> or <see cref="RhiIndexBuffer"/> is
        /// disposed too. Disposing a non-owning wrapper deletes nothing but zeroes its handle, so a
        /// reference that outlived this call reads as plainly invalid rather than silently addressing
        /// whatever unrelated buffer OpenGL later assigns that handle to. Same reasoning as
        /// <see cref="GPUMeshBufferCache.InvalidateVertexArrayObjectsForFreedBuffers"/>.
        /// </remarks>
        public void Delete()
        {
            Release(rhiVertexBuffers);
            Release(rhiIndexBuffers);

            Array.Clear(VertexBuffers);
            Array.Clear(IndexBuffers);
        }

        private void Release(IBuffer?[] buffers)
        {
            var resolved = Device;

            foreach (var buffer in buffers)
            {
                if (buffer is null)
                {
                    continue;
                }

                if (resolved is not null)
                {
                    resolved.DeferredDestroy(buffer);
                }
                else if (buffer is GLBuffer legacy)
                {
                    GL.DeleteBuffer(legacy.Handle);
                    legacy.Dispose();
                }
            }

            Array.Clear(buffers);
        }
    }
}
