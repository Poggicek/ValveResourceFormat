
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

        private readonly GLBuffer?[] rhiVertexBuffers;
        private readonly GLBuffer?[] rhiIndexBuffers;
        private readonly int[] vertexBufferSizes;
        private readonly int[] indexBufferSizes;

        /// <summary>Gets one of this mesh's vertex buffers as an <see cref="IBuffer"/>, for
        /// <see cref="ICommandList.BindVertexBuffer"/>.</summary>
        /// <param name="index">Which vertex buffer.</param>
        /// <returns>A non-owning view of the same OpenGL object; <see cref="Delete"/> still frees it.</returns>
        public IBuffer RhiVertexBuffer(int index)
            => rhiVertexBuffers[index] ??= GLBuffer.Wrap(
                VertexBuffers[index],
                vertexBufferSizes[index],
                BufferUsage.Vertex | BufferUsage.Storage | BufferUsage.CopyDestination,
                BufferMemory.DeviceLocal,
                $"VertexBuffer{index}");

        /// <summary>Gets one of this mesh's index buffers as an <see cref="IBuffer"/>, for
        /// <see cref="ICommandList.BindIndexBuffer"/>.</summary>
        /// <param name="index">Which index buffer.</param>
        /// <returns>A non-owning view of the same OpenGL object; <see cref="Delete"/> still frees it.</returns>
        public IBuffer RhiIndexBuffer(int index)
            => rhiIndexBuffers[index] ??= GLBuffer.Wrap(
                IndexBuffers[index],
                indexBufferSizes[index],
                BufferUsage.Index | BufferUsage.Storage | BufferUsage.CopyDestination,
                BufferMemory.DeviceLocal,
                $"IndexBuffer{index}");

        /// <summary>Uploads all vertex and index buffers from the provided <see cref="VBIB"/> to the GPU.</summary>
        /// <param name="vbib">Source vertex and index buffer data.</param>
        public GPUMeshBuffers(VBIB vbib)
        {
            ArgumentNullException.ThrowIfNull(vbib);

            VertexBuffers = new int[vbib.VertexBuffers.Count];
            GL.CreateBuffers(vbib.VertexBuffers.Count, VertexBuffers);

            for (var i = 0; i < vbib.VertexBuffers.Count; i++)
            {
                GL.NamedBufferData(VertexBuffers[i], (IntPtr)vbib.VertexBuffers[i].TotalSizeInBytes, vbib.VertexBuffers[i].Data, BufferUsageHint.StaticDraw);
            }

            IndexBuffers = new int[vbib.IndexBuffers.Count];
            GL.CreateBuffers(vbib.IndexBuffers.Count, IndexBuffers);

            for (var i = 0; i < vbib.IndexBuffers.Count; i++)
            {
                GL.NamedBufferData(IndexBuffers[i], (IntPtr)vbib.IndexBuffers[i].TotalSizeInBytes, vbib.IndexBuffers[i].Data, BufferUsageHint.StaticDraw);
            }

            vertexBufferSizes = new int[vbib.VertexBuffers.Count];

            for (var i = 0; i < vbib.VertexBuffers.Count; i++)
            {
                vertexBufferSizes[i] = (int)vbib.VertexBuffers[i].TotalSizeInBytes;
            }

            indexBufferSizes = new int[vbib.IndexBuffers.Count];

            for (var i = 0; i < vbib.IndexBuffers.Count; i++)
            {
                indexBufferSizes[i] = (int)vbib.IndexBuffers[i].TotalSizeInBytes;
            }

            rhiVertexBuffers = new GLBuffer?[VertexBuffers.Length];
            rhiIndexBuffers = new GLBuffer?[IndexBuffers.Length];
        }

        /// <summary>Deletes all GPU vertex and index buffers.</summary>
        public void Delete()
        {
            GL.DeleteBuffers(VertexBuffers.Length, VertexBuffers);
            GL.DeleteBuffers(IndexBuffers.Length, IndexBuffers);
        }
    }
}
