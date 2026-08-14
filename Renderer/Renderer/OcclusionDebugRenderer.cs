using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.Buffers;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.OpenGL;

namespace ValveResourceFormat.Renderer
{
    /// <summary>
    /// Debug visualization renderer for occluded object bounds in occlusion culling.
    /// Renders bounding boxes directly from GPU buffer using procedural vertex generation.
    /// </summary>
    public class OcclusionDebugRenderer
    {
        // Header layout shared with frustum_cull.comp.slang / occlusion_debug.vert.slang:
        //   uint occludedCount; uint padding[3];                                  (16 bytes)
        //   uint indirectVertexCount/InstanceCount/First/BaseInstance;            (16 bytes)
        // followed by the OccludedBoundDebug[] array.
        private const int HeaderSizeBytes = 32;
        private const int IndirectArgsByteOffset = 16;

        private readonly Shader shader;
        private readonly Shader finalizeShader;
        private readonly Scene scene;
        private readonly RendererContext renderContext;

        internal StorageBuffer? OccludedBoundsDebugGpu;

        /// <summary>Initializes the occlusion debug renderer and loads the debug shaders.</summary>
        /// <param name="scene">Scene to visualize occlusion for.</param>
        /// <param name="rendererContext">Renderer context for loading shaders and GPU resources.</param>
        public OcclusionDebugRenderer(Scene scene, RendererContext rendererContext)
        {
            this.scene = scene;
            renderContext = rendererContext;

            shader = rendererContext.ShaderLoader.LoadShader("occlusion_debug");
            finalizeShader = rendererContext.ShaderLoader.LoadShader("occlusion_debug_finalize");
        }

        /// <summary>Allocates (if needed) and clears the GPU buffer that receives occluded bounds from the culling shader.</summary>
        /// <param name="context">
        /// The pass being drawn, when one is available. Supplying it records the clear and the barrier
        /// that publishes it to the culling shader through <see cref="Scene.RenderContext.CommandList"/>;
        /// omitting it keeps the OpenGL path.
        /// </param>
        public void BindAndClearBuffer(Scene.RenderContext? context = null)
        {
            if (OccludedBoundsDebugGpu == null)
            {
                var totalSize = HeaderSizeBytes + (scene.SceneMeshletCount * Marshal.SizeOf<OccludedBoundDebug>());
                OccludedBoundsDebugGpu = new StorageBuffer(ReservedBufferSlots.OccludedBoundsDebug);
                GL.NamedBufferData(OccludedBoundsDebugGpu.Handle, totalSize, IntPtr.Zero, BufferUsageHint.StreamRead);
            }

            var commandList = context?.CommandList;

            if (commandList != null)
            {
                // Clear the atomic counter, then publish it: the culling shader's atomic increments are
                // shader writes, and without this they can be ordered before the clear that resets them.
                var buffer = OccludedBoundsDebugGpu.RhiBuffer;
                commandList.FillBuffer(buffer, 0, sizeof(uint), 0u);
                commandList.Barrier(new BufferBarrier(buffer, ResourceState.CopyDestination, ResourceState.ShaderWrite));
            }
            else
            {
                // Clear the atomic counter before dispatching
                var zero = 0u;
                GL.ClearNamedBufferSubData(OccludedBoundsDebugGpu.Handle, PixelInternalFormat.R32ui, IntPtr.Zero, sizeof(uint), PixelFormat.RedInteger, PixelType.UnsignedInt, ref zero);
            }

            OccludedBoundsDebugGpu.BindBufferBase();
        }

        /// <summary>
        /// Dispatches a single-invocation compute shader that turns the occluded-object
        /// atomic counter into a <c>DrawArraysIndirectCommand</c>, entirely on the GPU.
        /// </summary>
        /// <param name="context">
        /// The pass being drawn, when one is available. Supplying it records the barrier that makes the
        /// culling shader's atomic counter visible to this dispatch; omitting it keeps the OpenGL path.
        /// </param>
        public void DispatchFinalize(Scene.RenderContext? context = null)
        {
            Debug.Assert(OccludedBoundsDebugGpu is not null);

            // This dispatch reads the counter the culling shader incremented and writes the indirect
            // arguments back into the same buffer, so it both waits on those writes and joins them.
            context?.CommandList?.Barrier(
                new BufferBarrier(OccludedBoundsDebugGpu.RhiBuffer, ResourceState.ShaderWrite, ResourceState.ShaderReadWrite));

            finalizeShader.Use();
            OccludedBoundsDebugGpu.BindBufferBase();
            GL.DispatchCompute(1, 1, 1);
        }

        /// <summary>Renders wireframe bounding boxes for all occluded meshlets, color-coded by whether occlusion was correct.</summary>
        /// <param name="context">
        /// The pass being drawn, when one is available. Supplying it records the indirect draws and the
        /// barrier they depend on through <see cref="Scene.RenderContext.CommandList"/>; omitting it
        /// keeps the OpenGL path.
        /// </param>
        public void Render(Scene.RenderContext? context = null)
        {
            if (!scene.DrawMeshletsIndirect || !scene.EnableOcclusionCulling || OccludedBoundsDebugGpu == null)
            {
                return;
            }

            var commandList = context?.CommandList;

            if (commandList != null)
            {
                // One buffer consumed two ways at the same point: the vertex shader reads the bounds the
                // culling pass wrote, and the indirect fetch reads the arguments the finalize pass wrote.
                // Both transitions go in one call so a backend can merge them into a single barrier
                // rather than paying for two pipeline stalls.
                var buffer = OccludedBoundsDebugGpu.RhiBuffer;
                ReadOnlySpan<BufferBarrier> barriers =
                [
                    new(buffer, ResourceState.ShaderWrite, ResourceState.ShaderRead),
                    new(buffer, ResourceState.ShaderWrite, ResourceState.IndirectArgument),
                ];

                commandList.Barrier(barriers, []);
            }

            var renderState = renderContext.RenderState;
            var state = renderState.CurrentPass;
            state.Blend.BlendEnable = true;
            state.Blend.SrcBlend = BlendFactor.SrcAlpha;
            state.Blend.DstBlend = BlendFactor.OneMinusSrcAlpha;
            state.DepthStencil.DepthTestEnable = true;
            state.DepthStencil.DepthWriteEnable = false;
            using var _ = new RenderPassScope(renderState, in state);

            shader.Use();

            // Bind the occluded bounds buffer to shader
            OccludedBoundsDebugGpu.BindBufferBase();

            GL.BindVertexArray(renderContext.MeshBufferCache.EmptyVAO);

            if (commandList == null)
            {
                GL.BindBuffer(BufferTarget.DrawIndirectBuffer, OccludedBoundsDebugGpu.Handle);
            }

            // First pass: behind depth buffer (correctly occluded) - GREEN
            state.DepthStencil.DepthFunc = Comparison.Farther;
            renderState.Apply(in state);
            shader.SetUniform("g_vColor", new Vector4(0.0f, 1.0f, 0.0f, 0.9f));
            DrawOccludedBounds(commandList, context, in state);

            // Second pass: in front/at depth buffer (incorrectly visible) - RED
            state.DepthStencil.DepthFunc = Comparison.CloserEqual;
            renderState.Apply(in state);
            shader.SetUniform("g_vColor", new Vector4(1.0f, 0.0f, 0.0f, 0.9f));
            DrawOccludedBounds(commandList, context, in state);

            if (commandList == null)
            {
                GL.BindBuffer(BufferTarget.DrawIndirectBuffer, 0);
            }
        }

        // The vertex count and instance count both come from the GPU-written header, so this is a
        // non-indexed indirect draw: the arguments are a DrawArraysIndirectCommand, not the indexed form.
        private void DrawOccludedBounds(ICommandList? commandList, Scene.RenderContext? context, in RenderState state)
        {
            Debug.Assert(OccludedBoundsDebugGpu is not null);

            if (commandList != null)
            {
                // The bounds are generated procedurally from the vertex index against the storage buffer,
                // so there is no vertex buffer and the layout is empty. The two draws differ only in
                // depth function, which is part of the state, so each gets its own cached pipeline.
                var framebuffer = context!.Value.Framebuffer;
                var device = (GLRendererDevice)commandList.Device;

                var pipeline = device.GetOrCreatePipeline(
                    shader,
                    in state,
                    VertexInputDesc.Empty,
                    PrimitiveTopology.LineList,
                    framebuffer.Color is { } color ? [color.RhiFormat] : [],
                    framebuffer.Depth?.RhiFormat ?? RhiFormat.Undefined,
                    Math.Max(1, framebuffer.NumSamples),
                    GLRendererDevice.DrawConstants);

                commandList.BindPipeline(pipeline);
                commandList.DrawIndirect(OccludedBoundsDebugGpu.RhiBuffer, IndirectArgsByteOffset, drawCount: 1);
                return;
            }

            GL.DrawArraysIndirect(PrimitiveType.Lines, (IntPtr)IndirectArgsByteOffset);
        }
    }
}
