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

    /// <summary>Byte offset of the integer <c>(0, 0, 0, 1)</c> within
    /// <see cref="DefaultAttributeVertexBuffer"/>.</summary>
    public const int DefaultAttributeIntegerOffset = 4 * sizeof(float);

    /// <summary>Size of <see cref="DefaultAttributeVertexBuffer"/> in bytes.</summary>
    private const int DefaultAttributeSizeInBytes = 8 * sizeof(float);

    /// <summary>Contents of <see cref="DefaultAttributeVertexBuffer"/>: <c>(0, 0, 0, 1)</c> as four floats,
    /// then the same as four 32 bit integers.</summary>
    /// <remarks>
    /// Two encodings rather than one because the numeric class of a vertex attribute's format has to match
    /// the class the shader declares &#8212; a <c>uvec4</c> input fed from a float format is a Vulkan
    /// interface violation rather than a conversion. The float quad sits at offset 0 and the integer quad at
    /// <see cref="DefaultAttributeIntegerOffset"/>, so one stride-zero buffer serves both. <c>0f</c> and
    /// <c>0</c> share a bit pattern; <c>1f</c> and <c>1</c> do not, which is the whole reason for the second
    /// half. Written through the machine's own layout rather than as literal bytes, so it does not encode an
    /// assumption about endianness.
    /// </remarks>
    private static readonly byte[] DefaultAttributeData = BuildDefaultAttributeData();

    private static byte[] BuildDefaultAttributeData()
    {
        var data = new byte[DefaultAttributeSizeInBytes];

        MemoryMarshal.Cast<byte, float>(data.AsSpan(0, DefaultAttributeIntegerOffset))[3] = 1f;
        MemoryMarshal.Cast<byte, int>(data.AsSpan(DefaultAttributeIntegerOffset))[3] = 1;

        return data;
    }

    private int defaultAttributeVertexBuffer = -1;

    /// <summary>
    /// Gets a lazily created vertex buffer holding <c>(0, 0, 0, 1)</c>, the value OpenGL reads from a
    /// vertex attribute no array supplies. Bound with a stride of zero, it supplies the same constant to
    /// every vertex.
    /// </summary>
    /// <remarks>
    /// :VertexInputParity - the counterpart of <see cref="VectorOneVertexBuffer"/> for the inputs a shader
    /// reads and the mesh has no stream for. The two differ in value on purpose. White is what the engine
    /// substitutes for a missing COLOR stream, so it has to be stated on both paths or the two disagree;
    /// this one is what OpenGL already yields for an attribute that is simply not there, so stating it on
    /// the Vulkan path makes that path match OpenGL rather than changing what OpenGL does. See
    /// <c>MeshBatchRenderer.DescribeVertexInput</c>.
    /// </remarks>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public int DefaultAttributeVertexBuffer
    {
        get
        {
            if (defaultAttributeVertexBuffer == -1)
            {
                GL.CreateBuffers(1, out defaultAttributeVertexBuffer);
                GL.NamedBufferData(defaultAttributeVertexBuffer, DefaultAttributeSizeInBytes, DefaultAttributeData, BufferUsageHint.StaticDraw);

                standaloneRhiBuffers[defaultAttributeVertexBuffer] = RHI.OpenGL.GLBuffer.Wrap(
                    defaultAttributeVertexBuffer,
                    DefaultAttributeSizeInBytes,
                    RHI.BufferUsage.Vertex,
                    RHI.BufferMemory.DeviceLocal,
                    nameof(DefaultAttributeVertexBuffer));

#if DEBUG
                var bufferLabel = nameof(DefaultAttributeVertexBuffer);
                GL.ObjectLabel(ObjectLabelIdentifier.Buffer, defaultAttributeVertexBuffer, bufferLabel.Length, bufferLabel);
#endif
            }

            return defaultAttributeVertexBuffer;
        }
    }

    private RHI.IBuffer? defaultAttributeRhiBuffer;

    /// <summary>Gets <see cref="DefaultAttributeVertexBuffer"/> as an <see cref="RHI.IBuffer"/>, for the
    /// draw recording path that binds it for every input the shader reads and the mesh does not have.</summary>
    /// <remarks>Allocated exactly as <see cref="VectorOneRhiBuffer"/> is, and for the same reason: the
    /// handle route is OpenGL's alone, and on any other backend there is no OpenGL buffer to wrap.</remarks>
    public RHI.IBuffer DefaultAttributeRhiBuffer
    {
        get
        {
            if (defaultAttributeRhiBuffer is not null)
            {
                return defaultAttributeRhiBuffer;
            }

            var device = RendererContext.Device;

            if (device is null || device.Backend == RHI.RhiBackend.OpenGL)
            {
                return defaultAttributeRhiBuffer = standaloneRhiBuffers[DefaultAttributeVertexBuffer];
            }

            var buffer = device.CreateBuffer(new RHI.BufferDesc(
                DefaultAttributeSizeInBytes,
                RHI.BufferUsage.Vertex | RHI.BufferUsage.CopyDestination,
                RHI.BufferMemory.DeviceLocal,
                nameof(DefaultAttributeVertexBuffer)));

            device.UploadBuffer(buffer, 0, DefaultAttributeData);

            return defaultAttributeRhiBuffer = buffer;
        }
    }
}
