using System.Diagnostics;
using System.Runtime.InteropServices;
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

    private RHI.IBuffer? vectorOneRhiBuffer;

    /// <summary>Gets <see cref="VectorOneVertexBuffer"/> as an <see cref="RHI.IBuffer"/>, for the draw
    /// recording path that binds it as the default COLOR stream.</summary>
    /// <remarks>
    /// On OpenGL this is the same object <see cref="VectorOneVertexBuffer"/> already registers, so the
    /// buffer is created by exactly the calls it always was. On any other backend the handle route does
    /// not exist at all: there is no OpenGL buffer to make and none to wrap, so the buffer is allocated
    /// through the device and <see cref="VectorOneVertexBuffer"/> is never touched. Going through the
    /// handle there would be two faults at once &#8212; a direct GL call on a device that is not OpenGL,
    /// and a <c>GLBuffer</c> handed to a backend that cannot bind one.
    /// </remarks>
    public RHI.IBuffer VectorOneRhiBuffer
    {
        get
        {
            if (vectorOneRhiBuffer is not null)
            {
                return vectorOneRhiBuffer;
            }

            const int SizeInBytes = 4 * sizeof(float);
            var device = RendererContext.Device;

            if (device is null || device.Backend == RHI.RhiBackend.OpenGL)
            {
                return vectorOneRhiBuffer = standaloneRhiBuffers[VectorOneVertexBuffer];
            }

            var buffer = device.CreateBuffer(new RHI.BufferDesc(
                SizeInBytes,
                RHI.BufferUsage.Vertex | RHI.BufferUsage.CopyDestination,
                RHI.BufferMemory.DeviceLocal,
                nameof(VectorOneVertexBuffer)));

            device.UploadBuffer(buffer, 0, MemoryMarshal.AsBytes<float>([1f, 1f, 1f, 1f]));

            return vectorOneRhiBuffer = buffer;
        }
    }
}
