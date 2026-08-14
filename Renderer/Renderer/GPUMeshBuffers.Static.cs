using System.Diagnostics;
using OpenTK.Graphics.OpenGL;
namespace ValveResourceFormat.Renderer;

public partial class GPUMeshBufferCache
{
    private QuadIndexBuffer? quadIndices;

    /// <summary>Gets the shared quad index buffer used for rendering quad-based geometry as triangle pairs.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public QuadIndexBuffer QuadIndices
    {
        get
        {
            quadIndices ??= new QuadIndexBuffer(65532);

            return quadIndices;
        }
    }

    private int emptyVAO = -1;

    /// <summary>Gets a lazily created empty vertex array object with no attributes, used for attributeless draws.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public int EmptyVAO
    {
        get
        {
            if (emptyVAO == -1)
            {
                GL.CreateVertexArrays(1, out emptyVAO);

#if DEBUG
                var vaoLabel = nameof(EmptyVAO);
                GL.ObjectLabel(ObjectLabelIdentifier.VertexArray, emptyVAO, vaoLabel.Length, vaoLabel);
#endif
            }

            return emptyVAO;
        }
    }

    private int vectorOneVertexBuffer = -1;

    /// <summary>Gets a lazily created vertex buffer containing a single <c>(1, 1, 1, 1)</c> float4, used as a default color attribute.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public int VectorOneVertexBuffer
    {
        get
        {
            if (vectorOneVertexBuffer == -1)
            {
                const int SizeInBytes = 4 * sizeof(float);

                GL.CreateBuffers(1, out vectorOneVertexBuffer);
                GL.NamedBufferData(vectorOneVertexBuffer, SizeInBytes, [1f, 1f, 1f, 1f], BufferUsageHint.StaticDraw);

                // Registered so GetRhiBuffer can resolve it: AddMissingAttributes hands this buffer out as
                // a VertexDrawBuffer, and a size stated here is a real size rather than a fabricated one.
                standaloneRhiBuffers[vectorOneVertexBuffer] = RHI.OpenGL.GLBuffer.Wrap(
                    vectorOneVertexBuffer,
                    SizeInBytes,
                    RHI.BufferUsage.Vertex,
                    RHI.BufferMemory.DeviceLocal,
                    nameof(VectorOneVertexBuffer));

#if DEBUG
                var bufferLabel = nameof(VectorOneVertexBuffer);
                GL.ObjectLabel(ObjectLabelIdentifier.Buffer, vectorOneVertexBuffer, bufferLabel.Length, bufferLabel);
#endif
            }

            return vectorOneVertexBuffer;
        }
    }
}
