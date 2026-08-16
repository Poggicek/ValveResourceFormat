using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTK.Graphics.OpenGL;
using SkiaSharp;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Materials;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.OpenGL;

namespace Tests.Renderer.Golden
{
    /// <summary>
    /// Draws a <see cref="GoldenScene"/> offscreen and hands back the pixels.
    ///
    /// The frame it produces follows the same order the viewer uses (update, shadow passes, scene passes,
    /// post-process, overlay text), so a regression anywhere in that chain shows up here. What it does not
    /// share with the viewer is any source of variation: the resolution, the camera, the timestep and the
    /// frame count are all fixed, nothing is driven by wall-clock time, and no input is read.
    /// </summary>
    internal sealed class GoldenRenderHarness : IDisposable
    {
        /// <summary>Captured image width. Small enough to keep 20 baselines cheap, large enough to see shape.</summary>
        public const int Width = 320;

        /// <summary>Captured image height, at the 4:3 aspect the renderer's field of view is defined against.</summary>
        public const int Height = 240;

        /// <summary>
        /// The simulation step every frame advances by, chosen as a plain 60Hz tick. Fixed rather than
        /// measured: a frame time taken from a stopwatch would make every animated scene irreproducible.
        /// </summary>
        public const float Timestep = 1f / 60f;

        /// <summary>
        /// MSAA sample count of the scene framebuffer. One sample rather than none, because the
        /// post-process chain resolves from a multisample texture and asserts it was given one; one sample
        /// also avoids depending on a driver's multisample resolve pattern.
        /// </summary>
        private const int SampleCount = 1;

        private readonly RendererContext rendererContext;
        private readonly FixtureFileLoader fileLoader;
        private readonly GLDevice device;
        private readonly QuadOverdraw quadOverdraw;
        private readonly Framebuffer sceneFramebuffer;
        private readonly Framebuffer captureFramebuffer;
        private readonly byte[] readbackBuffer = new byte[Width * Height * 4];

        // Built on the first scene that asks for one and kept for the rest of the run. It holds nothing
        // scene specific -- the shader, the state tracker and the vertex buffer all come from the shared
        // context -- so rebuilding it per scene would only allocate another buffer nothing frees.
        private InfiniteGrid? baseGrid;

        private bool disposed;

        /// <summary>
        /// Creates the shared context and the two framebuffers every scene renders through. Must be called
        /// on the render thread.
        /// </summary>
        public GoldenRenderHarness()
        {
            // No package and no current file: nothing in Tests/Files sits in a game tree, so external
            // references never resolve and materials fall back to the renderer's own error material. That
            // fallback is deterministic, which is all the harness needs. See the coverage note in the
            // scene catalog for what it costs.
            fileLoader = new FixtureFileLoader();

            rendererContext = new RendererContext(fileLoader, NullLogger.Instance)
            {
                // Capped so a fixture with a large texture cannot change what is sampled depending on how
                // much VRAM the machine has.
                MaxTextureSize = 256,
            };

            // The RHI device, created with the diagnostic callback the validation gate reads. Assigning it
            // to the context is what the presentation layer does in the real application, so the renderer
            // sees the same shape here. It also installs the driver's debug message callback, which is how
            // a golden run reports a GL error today and a Vulkan validation error once that backend lands.
            // VRF_RHI_CENSUS swaps in a device whose command list notes which renderer type issued each
            // draw, so "which RHI call sites does the suite reach?" is answered by measurement.
            device = RhiCallSiteCensus.IsEnabled
                ? new CensusRecordingDevice(rendererContext, ValidationGate.OnMessage)
                : new GLRecordingDevice(rendererContext, ValidationGate.OnMessage);

            rendererContext.Device = device;

            // Set VRF_RHI_RECORDING=1 to run the scenes through the RHI instead of straight OpenGL.
            // Off by default because the migration cannot yet honour the contract end to end, and a
            // scene that cannot record fails loudly rather than quietly falling back. This is the
            // switch that measures how far the port has actually got: with it on, a scene passing
            // means the RHI produced the same pixels as the OpenGL path, which is the only claim
            // about this migration worth making.
            ValveResourceFormat.Renderer.Renderer.EnableRhiRecording = Environment.GetEnvironmentVariable("VRF_RHI_RECORDING") == "1";

            // Synchronous delivery, so a message is raised inside the call that caused it and on this
            // thread. Asynchronous delivery is allowed to straggle past the end of the scene, which would
            // make the validation gate attribute an error to whichever scene happened to run next.
            GL.Enable(EnableCap.DebugOutputSynchronous);

            // Reverse-Z clip control, the render state baseline and the seamless cubemap filtering the
            // shaders assume. Without this the depth test runs the wrong way round and every draw is
            // rejected, which looks exactly like a scene that rendered nothing.
            GLEnvironment.SetDefaultRenderState(rendererContext);

            sceneFramebuffer = Framebuffer.Prepare("GoldenSceneColor", Width, Height, SampleCount,
                new Framebuffer.AttachmentFormat(PixelInternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.HalfFloat),
                Framebuffer.DepthAttachmentFormat.Depth32FStencil8);

            ThrowIfIncomplete(sceneFramebuffer.Initialize(), nameof(sceneFramebuffer));
            sceneFramebuffer.ClearMask |= ClearBufferMask.StencilBufferBit;

            // The post-process chain writes display-ready sRGB-encoded bytes here, which is what gets
            // compared. Reading back an Rgba16f target instead would compare pre-tonemap values and miss
            // every regression in the tonemap, gamma and colour correction stages.
            captureFramebuffer = Framebuffer.Prepare("GoldenCaptureColor", Width, Height, 0,
                new Framebuffer.AttachmentFormat(PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte),
                null);

            ThrowIfIncomplete(captureFramebuffer.Initialize(), nameof(captureFramebuffer));

            quadOverdraw = new QuadOverdraw(rendererContext);
            quadOverdraw.Load();
        }

        /// <summary>Which RHI backend this harness renders through.</summary>
        public string BackendName => device.Backend.ToString();

        private static void ThrowIfIncomplete(FramebufferErrorCode status, string name)
        {
            if (status != FramebufferErrorCode.FramebufferComplete)
            {
                throw new GoldenRenderException($"The golden image {name} is incomplete: {status}.");
            }
        }

        /// <summary>Renders <paramref name="scene"/> and returns the captured frame, top row first.</summary>
        public SKBitmap Render(GoldenScene scene)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            RhiCallSiteCensus.CurrentScene = scene.Name;

            var renderer = new ValveResourceFormat.Renderer.Renderer(rendererContext);
            GoldenSceneSetup? setup = null;

            try
            {
                setup = Prepare(renderer, scene);

                // Link every program the scene pulled in before drawing, so the first frame is not the one
                // that pays for compilation. The viewer does the same in its prewarm pass.
                rendererContext.ShaderLoader.LinkLoadedShaders();

                for (var frame = 0; frame < scene.Frames; frame++)
                {
                    RenderFrame(renderer, setup);
                }

                if (setup.MorphComposite is { } morphComposite)
                {
                    return RenderAndReadMorphComposite(renderer, morphComposite);
                }

                return setup.CaptureShadowAtlas
                    ? ReadShadowAtlas(renderer)
                    : ReadCapture();
            }
            finally
            {
                if (setup != null)
                {
                    foreach (var resource in setup.OpenedResources)
                    {
                        resource.Dispose();
                    }
                }

                // Renderer.Dispose does not own the shadow buffers it allocated in Initialize.
                renderer.ShadowDepthBuffer?.Delete();
                renderer.BarnLightShadowBuffer?.Delete();
                renderer.Dispose();
            }
        }

        private GoldenSceneSetup Prepare(ValveResourceFormat.Renderer.Renderer renderer, GoldenScene scene)
        {
            var textRenderer = new TextRenderer(rendererContext, renderer.Camera);
            textRenderer.Load();

            renderer.Postprocess.Load(SampleCount);
            renderer.Postprocess.FullScreenGamma = 2.01f;
            renderer.Postprocess.ExposureCompensation = -0.4f;

            renderer.ShadowTextureSize = 1024;
            renderer.Initialize();
            renderer.MainFramebuffer = sceneFramebuffer;
            renderer.LoadRendererResources();

            // Occlusion culling decides what to draw from the previous frame's depth pyramid, which makes
            // the image a function of how many frames were rendered rather than of the scene. Frustum and
            // meshlet culling stay on; they depend only on the fixed camera.
            renderer.Scene.EnableOcclusionCulling = false;

            LoadDefaultLighting(renderer.Scene);

            renderer.Camera.SetViewportSize(Width, Height);
            renderer.Camera.FieldOfView = rendererContext.FieldOfView;
            renderer.Camera.CreateProjectionMatrix();

            var setup = new GoldenSceneSetup
            {
                Renderer = renderer,
                RendererContext = rendererContext,
                Width = Width,
                Height = Height,
            };

            setup.TextRenderer = textRenderer;
            setup.FileLoader = fileLoader;
            scene.Build(setup);

            if (setup.EnableOcclusionDebug)
            {
                // Opted back in for this scene only, and paid for with a pinned frame count long enough to
                // clear the renderer's occlusion warmup.
                renderer.Scene.EnableOcclusionCulling = true;
                renderer.Scene.OcclusionDebugEnabled = true;
            }

            if (setup.EnableQuadOverdraw)
            {
                quadOverdraw.SetRenderMode("Overdraw");
            }

            // One-time GPU setup for the populated scene: octrees, lighting and instancing buffers, env map
            // and light probe bindings. The viewers do this in their post-load step, after the scene's nodes
            // exist and before the first frame.
            renderer.Scene.Initialize();

            if (renderer.Scene.FogInfo.CubeFogActive
                && renderer.Scene.FogInfo.CubemapFog?.CubemapFogTexture is { } cubemapFogTexture)
            {
                renderer.Textures.RemoveAll(static texture => texture.Slot == ReservedTextureSlots.FogCubeTexture);
                renderer.Textures.Add(new(ReservedTextureSlots.FogCubeTexture, "g_tFogCubeTexture", cubemapFogTexture));
            }

            renderer.Camera.CreateProjectionMatrix();
            renderer.Camera.RecalculateMatrices();

            return setup;
        }

        /// <summary>
        /// Lights the scene the way the viewers do when the asset carries no lighting of its own: the
        /// renderer's own embedded sky cubemap as the image based light, plus the default sun.
        /// </summary>
        /// <remarks>Shared with <see cref="HeadlessVulkan"/>, so the two backends light a scene from the
        /// same call rather than from two copies that could drift apart.</remarks>
        internal static void LoadDefaultLighting(Scene scene)
        {
            const string resourceName = "Renderer.Resources.sky_furnace.vtex_c";

            var rendererAssembly = Assembly.GetAssembly(typeof(RendererContext))
                ?? throw new GoldenRenderException("Could not locate the renderer assembly.");

            using var stream = rendererAssembly.GetManifestResourceStream(resourceName)
                ?? throw new GoldenRenderException($"The renderer assembly no longer embeds '{resourceName}'.");

            using var resource = new Resource { FileName = "sky_furnace.vtex_c" };
            resource.Read(stream);

            ValveResourceFormat.Renderer.Renderer.LoadDefaultLighting(scene, resource);
        }

        private void RenderFrame(ValveResourceFormat.Renderer.Renderer renderer, GoldenSceneSetup setup)
        {
            var textRenderer = setup.TextRenderer!;

            renderer.Update(new Scene.UpdateContext
            {
                Camera = renderer.Camera,
                TextRenderer = textRenderer,
                Timestep = Timestep,
            });

            var renderContext = new Scene.RenderContext
            {
                Camera = renderer.Camera,
                Framebuffer = sceneFramebuffer,
                Scene = renderer.Scene,
                Textures = renderer.Textures,
            };

            if (setup.EnableQuadOverdraw)
            {
                quadOverdraw.Prepare(Width, Height);
                renderContext.ReplacementShader = quadOverdraw.SceneShader;
            }

            renderer.Render(renderContext);

            if (setup.EnableQuadOverdraw)
            {
                // A second pass over the same geometry with counting enabled, then the heat map resolve.
                // The order mirrors the viewer exactly: the first render leaves a depth buffer so the
                // counting pass shades only what is actually visible.
                quadOverdraw.BeginCountingPass(sceneFramebuffer);
                renderer.RenderScenesWithView(renderContext);
                quadOverdraw.EndCountingPass(sceneFramebuffer);

                RecordOverPass(sceneFramebuffer, "GoldenQuadOverdraw", renderer, context => quadOverdraw.Render(context));
            }

            if (setup.EnableOcclusionDebug && renderer.Scene.OcclusionDebug is { } occlusionDebug)
            {
                RecordOverPass(sceneFramebuffer, "GoldenOcclusionDebug", renderer, context => occlusionDebug.Render(context));
            }

            if (setup.EnableBaseGrid)
            {
                // Built on the first frame that wants it and kept, as the viewer keeps one per scene.
                baseGrid ??= new InfiniteGrid(renderer.Scene);

                // Through BeginOverlay rather than RecordOverPass, because the viewer's grid draw is a
                // BeginOverlay and the difference is load bearing: it is what rebinds the view constants
                // and the depth range onto a list of its own. The grid reads both.
                using var overlay = renderer.BeginOverlay(sceneFramebuffer, "Base Grid");

                baseGrid.Render(overlay.CommandList, sceneFramebuffer);
            }

            if (setup.EnableBloomAfterRender)
            {
                renderer.Postprocess.State = renderer.Postprocess.State with { HasBloom = true };
            }

            renderer.PostprocessRender(sceneFramebuffer, captureFramebuffer);

            // Overlay text is drawn after the tonemap, straight into the capture target, exactly as the
            // viewer draws it over the presented framebuffer.
            captureFramebuffer.Bind(FramebufferTarget.Framebuffer);
            GL.Viewport(0, 0, Width, Height);
            RenderOverlayText(renderer, textRenderer);
        }

        /// <summary>
        /// Draws the queued overlay text over the tonemapped frame.
        ///
        /// <para>The overlay is the one pass the renderer does not own: it runs after
        /// <see cref="ValveResourceFormat.Renderer.Renderer.PostprocessRender"/> has finished, so the
        /// renderer's own command list has already been closed. That leaves opening a list for it to the
        /// presentation layer, which offscreen is this harness. Doing so is also the only way the suite
        /// reaches <see cref="TextRenderer"/>'s RHI path at all: handed no render context, the text renderer
        /// takes its direct OpenGL route and the recorded path goes unchecked however many text scenes
        /// exist.</para>
        /// </summary>
        private void RenderOverlayText(ValveResourceFormat.Renderer.Renderer renderer, TextRenderer textRenderer)
        {
            var commandList = ValveResourceFormat.Renderer.Renderer.EnableRhiRecording
                ? device.BeginCommandList("GoldenOverlay")
                : null;

            if (commandList == null)
            {
                textRenderer.Render(renderer.Camera, renderer.ResolvedSceneDepth);
                return;
            }

            // Loaded rather than cleared: the tonemapped frame is already in this target and the overlay
            // draws on top of it.
            var pass = KeepContents(captureFramebuffer.RenderPass("GoldenOverlay"));

            commandList.BeginRenderPass(pass);

            try
            {
                textRenderer.Render(renderer.Camera, renderer.ResolvedSceneDepth, new Scene.RenderContext
                {
                    Camera = renderer.Camera,
                    Framebuffer = captureFramebuffer,
                    Scene = renderer.Scene,
                    Textures = renderer.Textures,
                    CommandList = commandList,
                });
            }
            finally
            {
                commandList.EndRenderPass();
            }
        }

        /// <summary>
        /// Runs an overlay step over an already-drawn framebuffer, inside its own recorded pass when the
        /// RHI path is on and directly through OpenGL when it is not.
        ///
        /// <para>These steps run after <see cref="ValveResourceFormat.Renderer.Renderer.Render"/> has
        /// closed its command list, so each needs a list of its own. Handing them one is what puts their
        /// RHI paths under test; called with no context they silently take the OpenGL route and the
        /// recorded path is never executed however many scenes select the mode.</para>
        /// </summary>
        private void RecordOverPass(Framebuffer framebuffer, string name,
            ValveResourceFormat.Renderer.Renderer renderer, Action<Scene.RenderContext?> draw)
        {
            if (!ValveResourceFormat.Renderer.Renderer.EnableRhiRecording)
            {
                draw(null);
                return;
            }

            var commandList = device.BeginCommandList(name);
            var pass = KeepContents(framebuffer.RenderPass(name));

            commandList.BeginRenderPass(pass);

            try
            {
                draw(new Scene.RenderContext
                {
                    Camera = renderer.Camera,
                    Framebuffer = framebuffer,
                    Scene = renderer.Scene,
                    Textures = renderer.Textures,
                    CommandList = commandList,
                });
            }
            finally
            {
                commandList.EndRenderPass();
            }
        }

        /// <summary>
        /// Rewrites a pass descriptor to load its attachments instead of clearing them.
        /// </summary>
        /// <remarks>
        /// A stencil aspect already marked <see cref="LoadOp.DontCare"/> stays that way: the framebuffer
        /// marks it so when its depth format carries no stencil, and asking to load an aspect that does not
        /// exist is an error rather than a no-op.
        /// </remarks>
        private static RenderPassDesc KeepContents(RenderPassDesc desc)
        {
            var colors = new ColorAttachmentDesc[desc.ColorAttachments.Length];

            for (var i = 0; i < colors.Length; i++)
            {
                colors[i] = desc.ColorAttachments[i] with { LoadOp = LoadOp.Load };
            }

            var depth = desc.DepthAttachment is { } attachment
                ? attachment with
                {
                    DepthLoadOp = LoadOp.Load,
                    StencilLoadOp = attachment.StencilLoadOp == LoadOp.DontCare ? LoadOp.DontCare : LoadOp.Load,
                }
                : desc.DepthAttachment;

            return desc with { ColorAttachments = colors, DepthAttachment = depth };
        }

        /// <summary>
        /// The square of the morph composite atlas that gets captured, in texels.
        /// </summary>
        /// <remarks>
        /// The composite target is allocated at 2048 square whatever the morph needs, and where within it a
        /// rectangle lands is derived from the atlas dimensions, so a corner crop can miss the drawn
        /// rectangles entirely -- it did, and produced a uniform image that would have passed forever
        /// without testing anything. The whole target is read and reduced instead.
        /// </remarks>
        private const int MorphCaptureTexels = 2048;

        /// <summary>
        /// Composites the morph targets and reads the result back, magnified so the atlas rectangles are
        /// large enough in the image to be compared.
        ///
        /// <para>Signed values, mapped about mid grey rather than clamped at zero. The composite holds
        /// per-vertex deltas which are as often negative as positive, and clamping would throw away half of
        /// what distinguishes one atlas rectangle from another -- which is the whole subject of this
        /// scene.</para>
        /// </summary>
        private SKBitmap RenderAndReadMorphComposite(ValveResourceFormat.Renderer.Renderer renderer, MorphComposite morphComposite)
        {
            RecordOverPass(captureFramebuffer, "GoldenMorphComposite", renderer, context => morphComposite.Render(context));

            var texels = new float[MorphCaptureTexels * MorphCaptureTexels * 3];

            GL.GetTextureSubImage(morphComposite.CompositeTexture.Handle, 0,
                0, 0, 0, MorphCaptureTexels, MorphCaptureTexels, 1,
                PixelFormat.Rgb, PixelType.Float, texels.Length * sizeof(float), texels);

            var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var pixels = bitmap.GetPixelSpan();

            static byte Encode(float value)
                => (byte)Math.Clamp((int)((0.5f + 0.5f * value) * 255f + 0.5f), 0, 255);

            // Square aspect, so the atlas is not stretched and the boundary between one rectangle and the
            // next stays where it is.
            var offsetX = (Width - Height) / 2;

            for (var y = 0; y < Height; y++)
            {
                var startY = y * MorphCaptureTexels / Height;
                var endY = Math.Max(startY + 1, (y + 1) * MorphCaptureTexels / Height);

                for (var x = 0; x < Width; x++)
                {
                    var destination = (y * Width + x) * 4;
                    pixels[destination + 3] = 255;

                    if (x < offsetX || x >= offsetX + Height)
                    {
                        continue;
                    }

                    var startX = (x - offsetX) * MorphCaptureTexels / Height;
                    var endX = Math.Max(startX + 1, (x - offsetX + 1) * MorphCaptureTexels / Height);

                    // The largest magnitude in the block rather than its average. A morph rectangle covers
                    // a small part of a large atlas, and averaging would dilute it back into the zeroes
                    // around it until the image said nothing.
                    var peak = new float[3];

                    for (var sourceY = startY; sourceY < endY; sourceY++)
                    {
                        for (var sourceX = startX; sourceX < endX; sourceX++)
                        {
                            var source = (sourceY * MorphCaptureTexels + sourceX) * 3;

                            for (var channel = 0; channel < 3; channel++)
                            {
                                var value = texels[source + channel];

                                if (MathF.Abs(value) > MathF.Abs(peak[channel]))
                                {
                                    peak[channel] = value;
                                }
                            }
                        }
                    }

                    pixels[destination + 2] = Encode(peak[0]);
                    pixels[destination + 1] = Encode(peak[1]);
                    pixels[destination + 0] = Encode(peak[2]);
                }
            }

            return bitmap;
        }

        /// <summary>
        /// Reads the sun shadow atlas back as a greyscale image, downsampled to the capture size.
        ///
        /// The atlas is reverse-Z, so the near plane is 1 and the far plane is 0; the mapping below keeps
        /// that orientation, meaning near geometry is bright and the cleared background is black. The
        /// downsample takes the maximum of each source block rather than their average, because a thin
        /// shadow caster that lands on a handful of texels has to survive into the image to be checked.
        /// </summary>
        private static SKBitmap ReadShadowAtlas(ValveResourceFormat.Renderer.Renderer renderer)
        {
            var atlas = renderer.ShadowDepthBuffer?.Depth
                ?? throw new GoldenRenderException("The renderer has no sun shadow atlas to capture.");

            var atlasSize = atlas.Width;
            var depth = new float[atlasSize * atlasSize];

            GL.GetTextureImage(atlas.Handle, 0, PixelFormat.DepthComponent, PixelType.Float,
                depth.Length * sizeof(float), depth);

            var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var pixels = bitmap.GetPixelSpan();

            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var startX = x * atlasSize / Width;
                    var endX = Math.Max(startX + 1, (x + 1) * atlasSize / Width);
                    var startY = y * atlasSize / Height;
                    var endY = Math.Max(startY + 1, (y + 1) * atlasSize / Height);

                    var peak = 0f;

                    for (var sourceY = startY; sourceY < endY; sourceY++)
                    {
                        for (var sourceX = startX; sourceX < endX; sourceX++)
                        {
                            peak = MathF.Max(peak, depth[sourceY * atlasSize + sourceX]);
                        }
                    }

                    // Square root, so the depth range where casters actually sit occupies enough of the
                    // 0..255 output that a small change in it is still a visible change in the image.
                    var value = (byte)Math.Clamp((int)(MathF.Sqrt(Math.Clamp(peak, 0f, 1f)) * 255f + 0.5f), 0, 255);
                    var offset = (y * Width + x) * 4;

                    pixels[offset + 0] = value;
                    pixels[offset + 1] = value;
                    pixels[offset + 2] = value;
                    pixels[offset + 3] = 255;
                }
            }

            return bitmap;
        }

        private SKBitmap ReadCapture()
        {
            captureFramebuffer.Bind(FramebufferTarget.ReadFramebuffer);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.ReadPixels(0, 0, Width, Height, PixelFormat.Bgra, PixelType.UnsignedByte, readbackBuffer);

            var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var pixels = bitmap.GetPixelSpan();
            var stride = Width * 4;

            // OpenGL hands back the bottom row first. Flipped by copying rows rather than by drawing the
            // bitmap through a mirrored canvas, which would resample and perturb the very pixels being
            // compared.
            for (var y = 0; y < Height; y++)
            {
                var source = readbackBuffer.AsSpan((Height - 1 - y) * stride, stride);
                var destination = pixels.Slice(y * stride, stride);
                source.CopyTo(destination);

                // The capture target has no meaningful alpha; forcing it opaque keeps the encoded PNG from
                // depending on whatever the last shader happened to leave in that channel.
                for (var x = 3; x < stride; x += 4)
                {
                    destination[x] = 255;
                }
            }

            return bitmap;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            sceneFramebuffer.Delete();
            captureFramebuffer.Delete();
            quadOverdraw.Dispose();
            device.Dispose();
            rendererContext.Dispose();
            fileLoader.Dispose();
        }
    }
}
