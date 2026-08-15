using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.OpenGL;

namespace ValveResourceFormat.Renderer
{
    /// <summary>
    /// Renders an infinite reference grid on the XY plane.
    /// </summary>
    public class InfiniteGrid
    {
        /// <summary>The two triangles covering clip space that the grid shader unprojects through.</summary>
        private static readonly float[] Vertices =
        [
            -1f, 1f,
            -1f, -1f,
            1f, 1f,
            1f, -1f,
            1f, 1f,
            -1f, -1f,
        ];

        private const int VertexCount = 6;

        private readonly int vao;
        private readonly Shader shader;
        private readonly RenderStateTracker renderState;
        private readonly VertexInputLayout format;

        // The same geometry as the RHI models it. On OpenGL a non-owning view of the loose buffer object
        // below, so that path is unchanged; on any other backend the device owns it and there is no loose
        // object at all.
        private readonly IBuffer? vertexRhiBuffer;

        // Built on the first recorded draw rather than in the constructor, so a layout the contract has no
        // format for throws at the draw that needs it instead of during scene load.
        private VertexInputDesc? vertexInputDesc;

        /// <summary>Initializes the grid geometry and loads the grid shader.</summary>
        /// <param name="scene">Scene providing the renderer context.</param>
        public InfiniteGrid(Scene scene)
        {
            ArgumentNullException.ThrowIfNull(scene);

            shader = scene.RendererContext.ShaderLoader.LoadShader("grid");
            renderState = scene.RendererContext.RenderState;
            format = new VertexInputLayout(sizeof(float) * 2, new VertexAttribute(VertexSlot.Position, DXGI_FORMAT.R32G32_FLOAT));

            var sizeInBytes = Vertices.Length * sizeof(float);
            var device = scene.RendererContext.Device;

            // A separate branch rather than one unified path, for the reason ShapeSceneNode.Init gives:
            // the OpenGL one has to stay exactly the calls it has always been, because it is the oracle.
            if (device is not null && device.Backend != RhiBackend.OpenGL)
            {
                vertexRhiBuffer = device.CreateBuffer(new BufferDesc(
                    sizeInBytes, BufferUsage.Vertex | BufferUsage.CopyDestination, BufferMemory.DeviceLocal, nameof(InfiniteGrid)));

                device.UploadBuffer(vertexRhiBuffer, 0, MemoryMarshal.AsBytes(Vertices.AsSpan()));
                return;
            }

            GL.CreateBuffers(1, out int buffer);
            GL.NamedBufferData(buffer, sizeInBytes, Vertices, BufferUsageHint.StaticDraw);

            vao = format.CreateVertexArray(nameof(InfiniteGrid), buffer);

            // Static geometry, uploaded once and never rewritten, so this view stays valid for the lifetime
            // of the grid.
            vertexRhiBuffer = GLBuffer.Wrap(buffer, sizeInBytes, BufferUsage.Vertex, BufferMemory.DeviceLocal, nameof(InfiniteGrid));

#if DEBUG
            var vaoLabel = nameof(InfiniteGrid);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, buffer, vaoLabel.Length, vaoLabel);
#endif
        }

        /// <summary>Renders the infinite grid for the current frame.</summary>
        /// <param name="commandList">The list to record into, or <see langword="null"/> to draw through
        /// OpenGL directly.</param>
        /// <param name="target">The framebuffer being rendered into, whose attachment formats and sample
        /// count the pipeline needs. Only read when recording; the OpenGL path draws into whatever is
        /// bound.</param>
        /// <remarks>
        /// The world axis lines are this draw too: <c>grid.frag</c> colours the two lines through the
        /// origin red and green itself, so they appear and disappear with the grid rather than separately.
        /// </remarks>
        public void Render(ICommandList? commandList = null, Framebuffer? target = null)
        {
            using var _ = renderState.Scope(blend: true, srcBlend: BlendFactor.SrcAlpha, dstBlend: BlendFactor.OneMinusSrcAlpha);

            shader.Use(commandList);

            if (commandList == null)
            {
                // Zero on a device-backed grid, which made no vertex array because there are no loose
                // OpenGL objects to make one from. Nothing to draw with, and drawing with array zero
                // would be a validation failure rather than a no-op.
                if (vao == 0)
                {
                    return;
                }

                VertexArray.Bind(vao, shader);

                GL.DrawArrays(PrimitiveType.Triangles, 0, VertexCount);
                return;
            }

            // The scope above is the whole of this draw's state; the grid has no material to compose one
            // over it, so the pipeline bakes the pass baseline exactly as applied.
            var state = renderState.CurrentPass;

            vertexInputDesc ??= format.ToVertexInputDesc();

            commandList.BindPipeline(GLRendererDevice.PipelineFor(
                commandList.Device,
                shader,
                in state,
                vertexInputDesc,
                PrimitiveTopology.TriangleList,
                target?.Color is { } color ? [color.RhiFormat] : [],
                target?.Depth?.RhiFormat ?? RhiFormat.Undefined,
                Math.Max(1, target?.NumSamples ?? 0)));

            commandList.BindVertexBuffer(0, vertexRhiBuffer!);
            commandList.Draw(VertexCount);
        }
    }
}
