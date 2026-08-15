using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.OpenGL;

namespace ValveResourceFormat.Renderer.SceneEnvironment
{
    /// <summary>
    /// Renders a 2D skybox using a fullscreen cube.
    /// </summary>
    public class SceneSkybox2D
    {
        /// <summary>Gets the color tint multiplied with the skybox texture during rendering.</summary>
        public Vector3 Tint { get; init; } = Vector3.One;

        /// <summary>Gets the rotation transform applied to the skybox cube.</summary>
        public Matrix4x4 Transform { get; init; } = Matrix4x4.Identity;

        /// <summary>Gets or sets the material used to render the skybox.</summary>
        public RenderMaterial Material { get; set; }

        /// <summary>The number of vertices in the cube <c>sky.vert</c> generates from <c>gl_VertexID</c>.</summary>
        private const int CubeVertexCount = 36;

        // Allocated on the first recorded draw and reused, the way MeshBatchRenderer reuses one list for a
        // whole batch. The OpenGL path never touches it.
        private List<RenderMaterial.TextureBinding>? textureBindings;

        /// <summary>
        /// Initializes a new instance of the <see cref="SceneSkybox2D"/> class with the given material.
        /// </summary>
        /// <param name="material">The material to use for skybox rendering.</param>
        public SceneSkybox2D(RenderMaterial material)
        {
            Material = material;
        }

        /// <summary>
        /// Releases owned GPU resources.
        /// </summary>
        /// <remarks>Nothing of its own left to release: the vertex array this used to create is now the
        /// renderer's shared empty one, which the mesh buffer cache owns.</remarks>
        public virtual void Delete()
        {
        }

        /// <summary>Renders the skybox using a fullscreen 36-vertex cube draw call.</summary>
        /// <param name="commandList">The list to record into, or <see langword="null"/> to draw through
        /// OpenGL directly.</param>
        /// <param name="target">The framebuffer being rendered into, whose attachment formats and sample
        /// count the pipeline needs. Only read when recording; the OpenGL path draws into whatever is
        /// bound.</param>
        /// <remarks>
        /// <para>
        /// The cube is generated from <c>gl_VertexID</c> and fetches nothing, so its vertex input is
        /// <see cref="VertexInputDesc.Empty"/> and the OpenGL path binds the shared empty vertex array &#8212;
        /// the same pair <see cref="PostProcess.PostProcessRenderer.DrawFullscreenTriangle"/> uses.
        /// </para>
        /// <para>
        /// The pipeline's state is <see cref="RenderMaterial.GetRenderState"/> composed over the scope
        /// opened below, because that is exactly what <see cref="RenderMaterial.Render"/> applies on the
        /// OpenGL path. Baking anything else would make the two backends disagree about a frame neither
        /// could be the oracle for.
        /// </para>
        /// <para>
        /// No push constants: <c>sky.vert</c> reads none of the per-draw block, and the Vulkan pipeline
        /// layout takes its range from SPIR-V reflection rather than from the description anyway.
        /// </para>
        /// </remarks>
        public void Render(ICommandList? commandList = null, Framebuffer? target = null)
        {
            var rendererContext = Material.Shader.RendererContext;

            // A baseline scope: the material composes its own state over it.
            using var _ = rendererContext.RenderState.Scope(depthFunc: Comparison.Equal);

            Material.Shader.Use(commandList);

            if (commandList != null)
            {
                // Before the material, and that order is load bearing on OpenGL. GLGraphicsPipeline.Bind
                // installs the program through Shader.Use, which binds the shader's own default constant
                // buffer to the globals slot on the way past. Binding the pipeline after
                // RenderMaterial.Render would therefore leave the shader defaults in the slot instead of
                // the material's buffer -- and g_matSkyRotation has no source default, so the zero matrix
                // that lands there collapses all 36 vertices onto the origin and the sky disappears
                // altogether. Measured: the whole recording gate's backgrounds went black.
                var state = Material.GetRenderState(rendererContext.RenderState.CurrentPass);

                commandList.BindPipeline(GLRendererDevice.PipelineFor(
                    commandList.Device,
                    Material.Shader,
                    in state,
                    VertexInputDesc.Empty,
                    PrimitiveTopology.TriangleList,
                    target?.Color is { } color ? [color.RhiFormat] : [],
                    target?.Depth?.RhiFormat ?? RhiFormat.Undefined,
                    Math.Max(1, target?.NumSamples ?? 0)));
            }

            Material.Render(null, commandList);
            Material.SetUniform("g_vTint", Tint);
            Material.SetUniform("g_matSkyRotation", Transform);

            if (commandList == null)
            {
                GL.BindVertexArray(rendererContext.MeshBufferCache.EmptyVAO);
                GL.DrawArrays(PrimitiveType.Triangles, 0, CubeVertexCount);
                Material.PostRender();
                return;
            }

            // The sky's cubemap lives in set 3, and a descriptor set has none of OpenGL's leftovers: the
            // texture units Material.Render just bound are invisible here, so the same textures have to be
            // restated as descriptors. The slots come from the shader through CollectTextureBindings rather
            // than from a count, and each carries its own sampler, for the reasons that method gives.
            // After the pipeline bind, so the writes are checked against the set 3 this draw reads.
            textureBindings ??= [];
            Material.CollectTextureBindings(Material.Shader, textureBindings);

            foreach (var binding in textureBindings)
            {
                commandList.BindTexture(binding.DescriptorSet, binding.Binding, binding.Texture.RhiTexture,
                    Material.SamplerFor(binding));
            }

            commandList.Draw(CubeVertexCount);
            Material.PostRender();
        }
    }
}
