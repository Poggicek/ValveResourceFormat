using System.Diagnostics;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.OpenGL;
using ValveResourceFormat.Renderer.SceneEnvironment;
using ValveResourceFormat.Renderer.World;

namespace ValveResourceFormat.Renderer.PostProcess
{
    /// <summary>
    /// Post-processing renderer for tonemapping, color grading, and adaptive exposure.
    /// </summary>
    public class PostProcessRenderer
    {
        private readonly RendererContext RendererContext;
        private Shader? shaderMsaaResolve;
        private Shader? shaderDepthResolve;
        private Shader? shaderPostProcess;
        private Shader? shaderPostProcessBloom;
        private Shader? shaderCombineLuts;
        private RenderTexture? combinedLut;
        private static readonly string[] LutSamplerNames =
            ["g_tColorCorrection0", "g_tColorCorrection1", "g_tColorCorrection2", "g_tColorCorrection3"];
        private readonly OutlineRenderer Outline;

        /// <summary>Gets or sets the blue noise texture used for dithering in the tonemap pass.</summary>
        public RenderTexture? BlueNoise { get; set; }

        // Seeded, so the dither sequence is the same from one run to the next. The offset still
        // changes every frame, which is what breaks up the banding; what the seed buys is a frame
        // that reproduces. Unseeded, the tonemap pass put a floor of 2/255 under every comparison,
        // which is the dither amplitude itself, and the golden image budgets all had to clear it.
        private readonly Random random = new(Seed: 0x5EED);

        /// <summary>Gets or sets the scene average luminance used for auto-exposure calculations.</summary>
        public float AverageLuminance { get; set; }
        /// <summary>Gets or sets the current post-processing state (tonemap, bloom, exposure settings).</summary>
        public PostProcessState State { get; set; }
        /// <summary>Gets or sets a value indicating whether post-processing is active.</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>Gets or sets a value indicating whether color correction LUT application is active.</summary>
        public bool ColorCorrectionEnabled { get; set; } = true;
        /// <summary>Gets or sets a value indicating whether any scene objects require outline rendering this frame.</summary>
        public bool HasOutlineObjects { get; set; }

        /// <summary>Gets the per-frame raw exposure scalar history used for temporal smoothing.</summary>
        public List<float> ExposureHistory { get; } = new(10);

        /// <summary>Gets or sets a manually overridden exposure value; set to -1 to use auto-exposure.</summary>
        public float CustomExposure { get; set; } = -1;

        /// <summary>Gets or sets the display gamma, the game's brightness setting. 2.2 is identity; lower is brighter.</summary>
        public float FullScreenGamma { get; set; } = 2.2f;

        /// <summary>
        /// Gets or sets the viewer's exposure compensation in stops, added on top of whatever the post
        /// process volume authors. Applied after the exposure clamp, so it still works on the many scenes
        /// that pin exposure to an authored bound.
        /// </summary>
        public float ExposureCompensation { get; set; }

        /// <summary>The displayed luminance that auto-exposure places the scene's log-average on.</summary>
        private const float MiddleGrey = 0.18f;

        /// <summary>
        /// Gets the pre-tonemap scene luminance auto-exposure aims for: the value the active tonemap
        /// curve displays as <see cref="MiddleGrey"/>.
        /// </summary>
        public float ExposureTargetLuminance { get; private set; } = MiddleGrey;
        /// <summary>Gets the smoothed exposure value applied in the current frame.</summary>
        public float CurrentExposure { get; private set; } = 1.0f;
        /// <summary>Gets the target exposure value that <see cref="CurrentExposure"/> is adapting towards.</summary>
        public float TargetExposure { get; private set; }
        /// <summary>Gets or sets the final linear tonemap scalar passed to the post-process shader.</summary>
        public float TonemapScalar { get; set; }
        /// <summary>Gets the bloom renderer used for the multi-pass Gaussian bloom effect.</summary>
        public BloomRenderer Bloom { get; private set; }
        /// <summary>Gets the depth-of-field renderer.</summary>
        public DOFRenderer DOF { get; private set; }

        /// <summary>Gets the HDR color attachment format used by post-process framebuffers.</summary>
        public static Framebuffer.AttachmentFormat DefaultColorFormat => new(PixelInternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.Float);

        /// <summary>
        /// Initializes a new <see cref="PostProcessRenderer"/> using the given renderer context.
        /// </summary>
        /// <param name="rendererContext">The renderer context providing shader loading and mesh buffer access.</param>
        public PostProcessRenderer(RendererContext rendererContext)
        {
            RendererContext = rendererContext;
            Bloom = new BloomRenderer(rendererContext, this);
            DOF = new DOFRenderer(rendererContext);
            Outline = new OutlineRenderer(rendererContext);
        }

        /// <summary>
        /// Loads all post-processing shaders and initializes sub-renderers for the given MSAA sample count.
        /// </summary>
        /// <param name="msaaSamples">The MSAA sample count used to select shader variants.</param>
        public void Load(int msaaSamples)
        {
            var msaa = (byte)msaaSamples;
            shaderMsaaResolve = RendererContext.ShaderLoader.LoadShader("msaa_resolve", ("D_MSAA_SAMPLES", msaa));
            shaderDepthResolve = RendererContext.ShaderLoader.LoadShader("depth_resolve", ("D_MSAA_SAMPLES", msaa));
            shaderPostProcess = RendererContext.ShaderLoader.LoadShader("post_processing", ("D_BLOOM", 0));
            shaderPostProcessBloom = RendererContext.ShaderLoader.LoadShader("post_processing", ("D_BLOOM", 1));
            shaderCombineLuts = RendererContext.ShaderLoader.LoadShader("combine_luts");

            DOF.MsaaSamples = msaa;
            Bloom.Load();
            Outline.Load();
        }

        #region RHI helpers shared by the post-process chain

        // The chain is a sequence of fullscreen triangles and compute dispatches over textures the
        // renderer allocates itself, so every helper below is the same shape: record through the command
        // list when there is one, and issue the OpenGL call the chain has always issued when there is not.
        // Nothing here decides what is drawn; the two paths must stay pixel identical.

        /// <summary>
        /// Draws the fullscreen triangle every post-process pass is built on, binding a pipeline first
        /// when recording.
        /// </summary>
        /// <param name="commandList">The list to record into, or <see langword="null"/> to draw through OpenGL.</param>
        /// <param name="rendererContext">The context supplying the empty vertex array and the render state.</param>
        /// <param name="shader">The program to draw with, which must already be in use.</param>
        /// <param name="target">The framebuffer being rendered into, whose attachment format the pipeline
        /// needs. Only read when recording; the OpenGL path draws into whatever is bound.</param>
        /// <remarks>
        /// The pipeline takes its state from <see cref="RenderStateTracker.CurrentPass"/>, which is the
        /// state the enclosing scope has already applied, so binding it re-applies nothing. Its vertex
        /// input is <see cref="VertexInputDesc.Empty"/>: the triangle is generated from the vertex index
        /// and fetches nothing, which is what the empty vertex array stands for on the OpenGL path.
        /// </remarks>
        internal static void DrawFullscreenTriangle(ICommandList? commandList, RendererContext rendererContext,
            Shader shader, Framebuffer? target)
        {
            if (commandList == null)
            {
                GL.BindVertexArray(rendererContext.MeshBufferCache.EmptyVAO);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                return;
            }

            var device = (GLRendererDevice)commandList.Device;
            var state = rendererContext.RenderState.CurrentPass;

            commandList.BindPipeline(device.GetOrCreatePipeline(
                shader,
                in state,
                VertexInputDesc.Empty,
                PrimitiveTopology.TriangleList,
                target?.Color is { } color ? [color.RhiFormat] : [],
                RhiFormat.Undefined,
                Math.Max(1, target?.NumSamples ?? 0)));

            commandList.Draw(3);
        }

        /// <summary>Binds a compute program as a pipeline, when recording.</summary>
        /// <param name="commandList">The list to record into, or <see langword="null"/> to do nothing.</param>
        /// <param name="shader">The compute program, which must already be in use.</param>
        /// <remarks>Cached on the program's identity by the device, so this costs a dictionary lookup
        /// after the first call.</remarks>
        internal static void BindComputePipeline(ICommandList? commandList, Shader shader)
        {
            if (commandList == null)
            {
                return;
            }

            // Disposed straight away: the module only names an already linked program, the pipeline does
            // not keep it, and releasing it releases nothing.
            using var module = GLRendererDevice.ModuleFor(shader, ShaderStage.Compute);

            commandList.BindPipeline(commandList.Device.CreateComputePipeline(new ComputePipelineDesc(module, shader.Name)));
        }

        /// <summary>Points a sampler uniform at a texture unit and binds the texture there.</summary>
        /// <param name="commandList">The list to record the bind into, or <see langword="null"/> to bind directly.</param>
        /// <param name="shader">The program whose sampler uniform is being set.</param>
        /// <param name="slot">The texture unit, which is also the binding within the descriptor set.</param>
        /// <param name="name">The sampler uniform name.</param>
        /// <param name="texture">The texture to bind, or <see langword="null"/> to do nothing.</param>
        /// <param name="descriptorSet">Which set the binding belongs to. Defaults to the reserved globals.</param>
        /// <remarks>
        /// Only the bind moves onto the command list. Which unit a sampler reads is program state rather
        /// than a binding, so it stays a <c>glProgramUniform</c> on both paths &#8212; and a program that
        /// does not declare the sampler binds nothing at all, exactly as <c>Shader.SetTexture</c> does.
        /// </remarks>
        internal static void BindTexture(ICommandList? commandList, Shader shader, int slot, string name,
            RenderTexture? texture, int descriptorSet = DescriptorSets.ReservedTextures)
        {
            if (texture == null)
            {
                return;
            }

            if (commandList == null)
            {
                shader.SetTexture(slot, name, texture);
                return;
            }

            if (shader.GetUniformLocation(name) < 0)
            {
                return;
            }

            shader.SetUniform1(name, slot);

            var binding = descriptorSet == DescriptorSets.MaterialTextures
                ? slot - RenderMaterial.TextureUnitStart
                : slot;

            commandList.BindTexture(descriptorSet, binding, texture.RhiTexture);
        }

        /// <summary>Binds a texture as the storage image a compute pass writes through.</summary>
        /// <param name="commandList">The list to record the bind into, or <see langword="null"/> to bind directly.</param>
        /// <param name="binding">The image unit.</param>
        /// <param name="texture">The texture to bind.</param>
        /// <param name="format">The format the OpenGL path declares the image with.</param>
        /// <param name="layered">Whether every layer of a volume or array texture is bound at once.</param>
        internal static void BindStorageImage(ICommandList? commandList, int binding, RenderTexture texture,
            SizedInternalFormat format, bool layered = false)
        {
            if (commandList == null)
            {
                GL.BindImageTexture(binding, texture.Handle, 0, layered, 0, TextureAccess.WriteOnly, format);
                return;
            }

            commandList.BindStorageTexture(binding, texture.RhiTexture);
        }

        /// <summary>Dispatches a compute workload.</summary>
        /// <param name="commandList">The list to record into, or <see langword="null"/> to dispatch directly.</param>
        /// <param name="groupsX">Workgroups on X.</param>
        /// <param name="groupsY">Workgroups on Y.</param>
        /// <param name="groupsZ">Workgroups on Z.</param>
        internal static void Dispatch(ICommandList? commandList, int groupsX, int groupsY = 1, int groupsZ = 1)
        {
            if (commandList == null)
            {
                GL.DispatchCompute(groupsX, groupsY, groupsZ);
                return;
            }

            commandList.Dispatch(groupsX, groupsY, groupsZ);
        }

        /// <summary>
        /// Makes the image stores of a finished compute pass visible to the sampling and image loads that
        /// read them next.
        /// </summary>
        /// <param name="commandList">The list to record into, or <see langword="null"/> to issue the barrier directly.</param>
        /// <param name="first">The texture that was written.</param>
        /// <param name="second">A second texture written by the same batch, or <see langword="null"/>.</param>
        /// <remarks>Batched into one call, as the contract asks: separate calls cost separate pipeline
        /// stalls, and on OpenGL they collapse to the union of their barrier bits anyway.</remarks>
        internal static void ShaderWriteBarrier(ICommandList? commandList, RenderTexture first, RenderTexture? second = null)
        {
            if (commandList == null)
            {
                GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
                return;
            }

            TextureBarrier[] barriers = second == null
                ? [new(first.RhiTexture, ResourceState.ShaderWrite, ResourceState.ShaderRead)]
                : [
                    new(first.RhiTexture, ResourceState.ShaderWrite, ResourceState.ShaderRead),
                    new(second.RhiTexture, ResourceState.ShaderWrite, ResourceState.ShaderRead),
                ];

            commandList.Barrier([], barriers);
        }

        /// <summary>
        /// Describes a framebuffer as a render pass that keeps what its attachments already hold.
        /// </summary>
        /// <param name="framebuffer">The framebuffer to render into.</param>
        /// <param name="name">Debug label for the pass.</param>
        /// <param name="colorMipLevel">Mip level of the colour attachment to render into.</param>
        /// <returns>The pass descriptor.</returns>
        /// <remarks>Most of the chain composites onto or fully overwrites its target rather than starting
        /// from a clear, and the two are only interchangeable when a pass covers every texel &#8212; which
        /// the bloom chain's partial-viewport downsamples do not.</remarks>
        internal static RenderPassDesc LoadPass(Framebuffer framebuffer, string name, int colorMipLevel = 0)
        {
            var desc = framebuffer.RenderPass(name, colorMipLevel);
            var colors = new ColorAttachmentDesc[desc.ColorAttachments.Length];

            for (var i = 0; i < colors.Length; i++)
            {
                colors[i] = desc.ColorAttachments[i] with { LoadOp = LoadOp.Load };
            }

            return desc with { ColorAttachments = colors };
        }

        /// <summary>Holds an open render pass so it can be closed with <c>using</c>. The default value
        /// holds nothing and closes nothing, which is what the OpenGL path gets.</summary>
        /// <param name="commandList">The list the pass was opened on, or <see langword="null"/>.</param>
        internal readonly struct PostPass(ICommandList? commandList) : IDisposable
        {
            /// <summary>Ends the pass, if one was opened.</summary>
            public void Dispose() => commandList?.EndRenderPass();
        }

        /// <summary>Opens a render pass over a framebuffer, or does nothing when not recording.</summary>
        /// <param name="commandList">The list to record into, or <see langword="null"/> to do nothing.</param>
        /// <param name="framebuffer">The framebuffer whose attachments the pass renders into.</param>
        /// <param name="name">Debug label for the pass.</param>
        /// <param name="colorMipLevel">Mip level of the colour attachment to render into.</param>
        /// <param name="clear">Whether the attachments are cleared rather than loaded, matching what
        /// <see cref="Framebuffer.BindAndClear"/> would do at the same point on the OpenGL path.</param>
        /// <returns>A guard that ends the pass.</returns>
        internal static PostPass BeginPass(ICommandList? commandList, Framebuffer framebuffer, string name,
            int colorMipLevel = 0, bool clear = false)
        {
            if (commandList == null)
            {
                return default;
            }

            commandList.BeginRenderPass(clear
                ? framebuffer.RenderPass(name, colorMipLevel)
                : LoadPass(framebuffer, name, colorMipLevel));

            return new PostPass(commandList);
        }

        /// <summary>Sets the viewport to a rectangle anchored at the origin.</summary>
        /// <param name="commandList">The list to record into, or <see langword="null"/> to set it directly.</param>
        /// <param name="width">Viewport width in pixels.</param>
        /// <param name="height">Viewport height in pixels.</param>
        /// <remarks>A pass opens covering its whole attachment, so this is only needed where the chain
        /// draws into part of one &#8212; which the bloom downsamples and the tonemap both do.</remarks>
        internal static void SetViewport(ICommandList? commandList, int width, int height)
        {
            if (commandList == null)
            {
                GL.Viewport(0, 0, width, height);
                return;
            }

            commandList.SetViewport(0, 0, width, height);
        }

        #endregion

        /// <summary>
        /// Resolves MSAA color and/or depth from <paramref name="source"/> using compute shaders.
        /// Color and depth are written to standalone <see cref="RenderTexture"/> targets.
        /// Uses Karis average for HDR-aware color resolve, min filter for depth (conservative for reverse-Z).
        /// </summary>
        /// <param name="source">The multisampled framebuffer to resolve.</param>
        /// <param name="destColor">Where resolved colour is written.</param>
        /// <param name="destDepth">Where resolved depth is written.</param>
        /// <param name="resolveColor">Whether to resolve colour.</param>
        /// <param name="resolveDepth">Whether to resolve depth.</param>
        /// <param name="commandList">The list to record into, or <see langword="null"/> to run through OpenGL.</param>
        /// <remarks>A compute resolve rather than an attachment one, because the colour filter is a Karis
        /// average rather than the box filter a hardware resolve applies.</remarks>
        public void ResolveMsaa(Framebuffer source, RenderTexture destColor, RenderTexture destDepth,
            bool resolveColor, bool resolveDepth, ICommandList? commandList = null)
        {
            Debug.Assert(shaderMsaaResolve != null && shaderDepthResolve != null);

            var groupsX = (destColor.Width + 7) / 8;
            var groupsY = (destColor.Height + 7) / 8;

            if (resolveColor)
            {
                shaderMsaaResolve.Use();
                BindComputePipeline(commandList, shaderMsaaResolve);
                BindTexture(commandList, shaderMsaaResolve, 0, "g_tSourceMsaa", source.Color);
                BindStorageImage(commandList, 1, destColor, SizedInternalFormat.Rgba16f);
                Dispatch(commandList, groupsX, groupsY);
            }

            if (resolveDepth)
            {
                shaderDepthResolve.Use();
                BindComputePipeline(commandList, shaderDepthResolve);
                BindTexture(commandList, shaderDepthResolve, 0, "g_tSourceDepthMsaa", source.Depth);
                BindStorageImage(commandList, 1, destDepth, SizedInternalFormat.R32f);
                Dispatch(commandList, groupsX, groupsY);
            }

            if (resolveColor || resolveDepth)
            {
                // Only what was actually written is transitioned. OpenGL would not notice the difference,
                // since its barrier is global, but a transition out of a state a texture was never in is
                // a validation error on Vulkan.
                ShaderWriteBarrier(commandList,
                    resolveColor ? destColor : destDepth,
                    resolveColor && resolveDepth ? destDepth : null);
            }
        }

        /// <summary>
        /// Resolves the frame's weighted color correction LUTs into <see cref="PostProcessState.ColorCorrectionLUT"/>.
        /// A single full-weight LUT passes through; anything else runs the combine compute shader, which
        /// weighs up to four LUTs and fills the remainder with the neutral LUT.
        /// </summary>
        /// <param name="luts">The LUTs contributing this frame with their blend weights.</param>
        /// <param name="commandList">The list to record into, or <see langword="null"/> to run through OpenGL.</param>
        public void ResolveColorCorrection(List<WeightedLut> luts, ICommandList? commandList = null)
        {
            if (luts.Count == 0)
            {
                State = State with { ColorCorrectionLUT = null, NumLutsActive = 0 };
                return;
            }

            if (luts.Count == 1 && luts[0].Weight >= 0.999f)
            {
                State = State with
                {
                    ColorCorrectionLUT = luts[0].Lut,
                    ColorCorrectionLutDimensions = luts[0].Dimensions,
                    NumLutsActive = 1,
                };
                return;
            }

            Debug.Assert(shaderCombineLuts != null);

            var dimensions = luts[0].Dimensions;

            if (combinedLut == null || combinedLut.Width != dimensions)
            {
                combinedLut?.Delete();
                combinedLut = new RenderTexture(TextureTarget.Texture3D, dimensions, dimensions, dimensions, 1);
                combinedLut.SetLabel("CombinedColorCorrectionLUT");
                combinedLut.SetWrapMode(TextureWrapMode.ClampToEdge);
                combinedLut.SetFiltering(TextureMinFilter.Linear, TextureMagFilter.Linear);
                GL.TextureStorage3D(combinedLut.Handle, 1, SizedInternalFormat.Rgba8, dimensions, dimensions, dimensions);

                // Recorded so RhiTexture can describe the volume completely, which is what the storage
                // image binding below needs to declare its format.
                combinedLut.RhiFormat = RhiFormat.R8G8B8A8_UNorm;
            }

            var weights = Vector4.Zero;
            var totalWeight = 0f;

            shaderCombineLuts.Use();
            BindComputePipeline(commandList, shaderCombineLuts);

            for (var i = 0; i < WorldPostProcessInfo.MaxBlendedLuts; i++)
            {
                // The combine shader texel-fetches, so every input has to share the output's dimensions;
                // a mismatched LUT drops out and its share goes to the neutral remainder.
                var valid = i < luts.Count && luts[i].Dimensions == dimensions;
                var entry = valid ? luts[i] : luts[0];
                var weight = valid ? entry.Weight : 0f;

                weights[i] = weight;
                totalWeight += weight;
                BindTexture(commandList, shaderCombineLuts, i, LutSamplerNames[i], entry.Lut);
            }

            shaderCombineLuts.SetUniform("g_vColorCorrectionWeights0", weights);
            shaderCombineLuts.SetUniform("g_flIdentityWeight", MathF.Max(0f, 1f - totalWeight));

            BindStorageImage(commandList, 0, combinedLut, SizedInternalFormat.Rgba8, layered: true);

            var groups = (dimensions + 3) / 4;
            Dispatch(commandList, groups, groups, groups);
            ShaderWriteBarrier(commandList, combinedLut);

            State = State with
            {
                ColorCorrectionLUT = combinedLut,
                ColorCorrectionLutDimensions = dimensions,
                NumLutsActive = luts.Count,
            };
        }

        private void SetPostProcessUniforms(Shader shader, TonemapSettings TonemapSettings)
        {
            // Randomize dither offset every frame
            var ditherOffset = new Vector2(random.NextSingle(), random.NextSingle());

            // Dither by one 255th of frame color originally. Modified to be twice that, because it looks better.
            shader.SetUniform("g_vBlueNoiseDitherParams", new Vector4(ditherOffset, 1.0f / 256.0f, 2.0f / 255.0f));

            shader.SetUniform("g_flExposureBiasScaleFactor", MathF.Pow(2.0f, TonemapSettings.ExposureBias));
            shader.SetUniform("g_flShoulderStrength", TonemapSettings.ShoulderStrength);
            shader.SetUniform("g_flLinearStrength", TonemapSettings.LinearStrength);
            shader.SetUniform("g_flLinearAngle", TonemapSettings.LinearAngle);
            shader.SetUniform("g_flToeStrength", TonemapSettings.ToeStrength);
            shader.SetUniform("g_flToeNum", TonemapSettings.ToeNum);
            shader.SetUniform("g_flToeDenom", TonemapSettings.ToeDenom);

            var effectiveWhitePoint = TonemapSettings.EffectiveWhitePoint;
            var tonemappedWhitePoint = TonemapSettings.ApplyTonemapping(effectiveWhitePoint);
            shader.SetUniform("g_flWhitePoint", effectiveWhitePoint);
            shader.SetUniform("g_flWhitePointScale", 1.0f / tonemappedWhitePoint);
            shader.SetUniform("g_flFullScreenGamma", FullScreenGamma);
        }

        /// <summary>
        /// Resolves MSAA, applies DOF/bloom, tonemaps, and writes the final LDR image to <paramref name="colorBufferDraw"/>.
        /// </summary>
        /// <param name="colorBufferRead">The multisampled scene framebuffer to read.</param>
        /// <param name="colorBufferDraw">Where the tonemapped image is written.</param>
        /// <param name="resolveTarget">The single-sampled texture the MSAA resolve writes into.</param>
        /// <param name="camera">The active camera, used by the depth-of-field circle-of-confusion pass.</param>
        /// <param name="flipY">Whether the image is flipped vertically on the way out.</param>
        /// <param name="commandList">The list to record into, or <see langword="null"/> to run through OpenGL.</param>
        /// <remarks>
        /// A render pass never names the presented surface, so <paramref name="colorBufferDraw"/> has to be
        /// a texture-backed target for <paramref name="commandList"/> to be usable. <see cref="Renderer"/>
        /// is what decides that and passes <see langword="null"/> when it does not hold.
        /// </remarks>
        public void Render(Framebuffer colorBufferRead, Framebuffer colorBufferDraw,
            RenderTexture resolveTarget, Camera camera, bool flipY, ICommandList? commandList = null)
        {
            Debug.Assert(shaderMsaaResolve != null);
            Debug.Assert(shaderPostProcess != null && shaderPostProcessBloom != null);

            Debug.Assert(BlueNoise != null);

            using var _ = RendererContext.RenderState.Scope(depthTest: false, depthWrite: false);

            using (new GLDebugGroup("MSAA Resolve"))
            {
                var msaaResolveShader = DOF.Enabled ? DOF.MsaaResolveDof : shaderMsaaResolve;

                msaaResolveShader.Use();
                BindComputePipeline(commandList, msaaResolveShader);
                BindTexture(commandList, msaaResolveShader, 0, "g_tSourceMsaa", colorBufferRead.Color);
                BindStorageImage(commandList, 1, resolveTarget, SizedInternalFormat.Rgba16f);
                msaaResolveShader.SetUniform("g_bFlipY", flipY);

                if (DOF.Enabled)
                {
                    DOF.SetDofResolveShaderUniforms(msaaResolveShader, camera, colorBufferRead.Depth!, commandList);
                }

                var groupsX = (resolveTarget.Width + 7) / 8;
                var groupsY = (resolveTarget.Height + 7) / 8;
                Dispatch(commandList, groupsX, groupsY);
                ShaderWriteBarrier(commandList, resolveTarget);
            }

            RenderTexture resolvedScene = resolveTarget;

            if (DOF.Enabled)
            {
                resolvedScene = DOF.Render(resolveTarget, commandList);
            }

            using (new GLDebugGroup("Tonemapping, Color Correction, Bloom"))
            {
                var postProcessShader = State.HasBloom == true ? shaderPostProcessBloom : shaderPostProcess;

                // Bloom opens passes of its own over the ping-pong chain, so the tonemap target is only
                // bound once it is finished.
                if (State.HasBloom)
                {
                    Bloom.Render(resolvedScene, commandList);
                }

                colorBufferDraw.Bind(FramebufferTarget.DrawFramebuffer);
                postProcessShader.Use();

                // Loaded rather than cleared: the tonemap covers every texel of its own viewport, but that
                // viewport is the scene's size rather than the target's, so a clear would wipe whatever
                // sits outside it.
                using var pass = BeginPass(commandList, colorBufferDraw, "Tonemap");

                SetViewport(commandList, colorBufferRead.Width, colorBufferRead.Height);

                BindTexture(commandList, postProcessShader, 0, "g_tColorBuffer", resolvedScene);
                BindTexture(commandList, postProcessShader, 2, "g_tColorCorrectionLUT",
                    State.ColorCorrectionLUT ?? RendererContext.MaterialLoader.GetDefaultVolume());

                // Bound here too, in case post processing runs before the scene binds it.
                BindTexture(commandList, postProcessShader, (int)ReservedTextureSlots.BlueNoise, "g_tBlueNoise", BlueNoise);

                if (State.HasBloom)
                {
                    BindTexture(commandList, postProcessShader, 4, "g_tBloom", Bloom.AccumulationResult);
                    // these seem to all be needed at once due to transitions between post process volumes, we don't do that yet
                    // NormalizedBloomStrengths seems to act as a blending factor "how much of each bloom mode do we have right now"
                    var bloomStrengths = new Vector3(State.BloomSettings.AddBloomStrength, State.BloomSettings.ScreenBloomStrength, State.BloomSettings.BlurBloomStrength);
                    var normalizedStrenghts = Vector3.Normalize(bloomStrengths);
                    postProcessShader.SetUniform("g_vNormalizedBloomStrengths", normalizedStrenghts);
                    postProcessShader.SetUniform("g_vUnNormalizedBloomStrengths", bloomStrengths);
                }
                postProcessShader.SetUniform("g_bFlipY", flipY);

                postProcessShader.SetUniform("g_bPostProcessEnabled", Enabled);

                postProcessShader.SetUniform("g_flToneMapScalarLinear", TonemapScalar);
                SetPostProcessUniforms(postProcessShader, State.TonemapSettings);

                var invDimensions = 1.0f / State.ColorCorrectionLutDimensions;
                var invRange = new Vector2(1.0f - invDimensions, 0.5f * invDimensions);
                postProcessShader.SetUniform("g_vColorCorrectionColorRange", invRange);
                postProcessShader.SetUniform("g_flColorCorrectionDefaultWeight", (State.NumLutsActive > 0 && ColorCorrectionEnabled) ? State.ColorCorrectionWeight : 0f);

                DrawFullscreenTriangle(commandList, RendererContext, postProcessShader, colorBufferDraw);
            }

            if (HasOutlineObjects)
            {
                using var outlineGroup = new GLDebugGroup("Outline Edge");
                Debug.Assert(colorBufferRead.Stencil != null);
                Outline.Render(colorBufferRead.Stencil, colorBufferRead.NumSamples, flipY, colorBufferDraw, commandList);
            }
        }

        /// <summary>
        /// Updates <see cref="TonemapScalar"/> based on auto-exposure logic or <see cref="CustomExposure"/>.
        /// </summary>
        /// <param name="deltaTime">Elapsed time in seconds since the last frame, used for exposure adaptation speed.</param>
        public void CalculateTonemapScalar(float deltaTime)
        {
            var exposure = 1.0f;

            if (CustomExposure != -1)
            {
                TonemapScalar = CustomExposure;
                State = State with { ExposureSettings = State.ExposureSettings with { AutoExposureEnabled = false } };
                ReportTonemapStats();
                return;
            }

            exposure = AutoAdjustExposure(exposure, deltaTime);

            exposure *= MathF.Pow(2.0f, State.ExposureSettings.ExposureCompensation + ExposureCompensation);
            TonemapScalar = exposure;

            ReportTonemapStats();
        }

        /// <summary> Publishes tonemapping state to the render stats.</summary>
        private void ReportTonemapStats()
        {
            var settings = State.ExposureSettings;
            var stats = PerfStats.Active;

            stats.Set(Metric.SceneLuminance, AverageLuminance);
            stats.Set(Metric.TonemapScalar, TonemapScalar);
            stats.Set(Metric.FullScreenGamma, FullScreenGamma);
            stats.Set(Metric.ExposureTargetLuminance, ExposureTargetLuminance);
            stats.Set(Metric.Exposure, CurrentExposure);
            stats.Set(Metric.ExposureMin, settings.AutoExposureEnabled ? settings.ExposureMin : 0f);
            stats.Set(Metric.ExposureMax, settings.AutoExposureEnabled ? settings.ExposureMax : 0f);
        }

        private float AutoAdjustExposure(float exposure, float deltaTime)
        {
            if (!State.ExposureSettings.AutoExposureEnabled)
            {
                return exposure;
            }

            var curveInput = State.TonemapSettings.InvertTonemapping(MiddleGrey);
            ExposureTargetLuminance = float.IsNaN(curveInput) ? MiddleGrey : curveInput;

            var rawScalar = ExposureTargetLuminance / AverageLuminance;
            if (!float.IsFinite(rawScalar))
            {
                return exposure;
            }

            if (ExposureHistory.Count >= 10)
            {
                ExposureHistory.RemoveAt(0);
            }

            ExposureHistory.Add(rawScalar);

            var settings = State.ExposureSettings;
            // CurrentExposure is persistent between frames

            // Sequential min-then-max clamp: authored data contains locked (min == max) and even
            // inverted ranges
            var (min, max) = (settings.ExposureMin, settings.ExposureMax);
            var clampedScalar = MathF.Min(MathF.Max(rawScalar, min), max);
            if (ExposureHistory.Count == 10)
            {
                var weightedSum = 0.0f;
                var weightTotal = 0.0f;

                for (var i = 0; i < 10; i++)
                {
                    var weight = Math.Abs(5 - i) * 0.2f; // might be (5 - Math.Abs(5 - i))
                    weightTotal += weight;
                    weightedSum += weight * ExposureHistory[i];
                }

                clampedScalar = MathF.Min(MathF.Max(weightedSum * (1.0f / weightTotal), min), max);
            }

            if (!float.IsFinite(clampedScalar))
            {
                return CurrentExposure;
            }

            TargetExposure = clampedScalar;

            //if (Unknown > 0.0)
            //    TargetExposure *= Unknown;

            if (settings.ExposureSpeedUp == 0.0)
            {
                CurrentExposure = TargetExposure;
                return TargetExposure;
            }

            var adaptRate = CurrentExposure < TargetExposure ? settings.ExposureSpeedUp : settings.ExposureSpeedDown;

            var logCurrent = MathF.Log2(CurrentExposure);
            var logTarget = MathF.Log2(TargetExposure);
            var logDiff = MathF.Abs(logCurrent - logTarget);

            if (logDiff < settings.ExposureSmoothingRange)
            {
                adaptRate = MathF.Min(logDiff * 0.5f, adaptRate);
            }

            if (CurrentExposure > TargetExposure)
            {
                adaptRate = -adaptRate;
            }

            // Decide the clamp direction before the deltaTime multiply
            var adaptingUpward = adaptRate >= 0.0;
            adaptRate *= deltaTime;

            var newScalar = MathF.Pow(2, logCurrent + adaptRate);
            newScalar = adaptingUpward
                ? MathF.Min(newScalar, TargetExposure)
                : MathF.Max(newScalar, TargetExposure);

            if (!float.IsFinite(newScalar))
            {
                newScalar = TargetExposure;
            }

            CurrentExposure = newScalar;
            return newScalar;
        }
    }
}
