using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.Buffers;
using ValveResourceFormat.Renderer.PostProcess;
using ValveResourceFormat.Renderer.SceneEnvironment;
using ValveResourceFormat.ResourceTypes;

namespace ValveResourceFormat.Renderer;

/// <summary>
/// Main renderer for Source 2 scenes with support for shadows, post-processing, and multiple render passes.
/// </summary>
public class Renderer
{
    /// <summary>
    /// Depth range for a single layer of the scene.
    /// </summary>
    /// <param name="Start">The starting depth value from the viewers perspective. Note: 1.0 = closest.</param>
    /// <param name="End">The ending depth value from the viewers perspective. Note: 0.0 = furthest.</param>
    public record DepthRange(float Start, float End)
    {
        /// <summary>The window-space near value.</summary>
        public float Near { get; } = End;

        /// <summary>The window-space far value.</summary>
        public float Far { get; } = Start;

        /// <summary>Applies the depth range to the current render state.</summary>
        public void Apply()
        {
            GL.DepthRange(Near, Far);
        }

        /// <summary>The main scene.</summary>
        public static readonly DepthRange Scene = new(0.95f, 0.05f);

        /// <summary>Reserved for the first-person viewmodel, always in front of the main scene.</summary>
        public static readonly DepthRange Viewmodel = new(1.0f, Scene.Start);

        /// <summary>Reserved for the 3D sky, always behind the main scene.</summary>
        public static readonly DepthRange Sky = new(Scene.End, 0f);
    }

    /// <summary>
    /// Occlusion culling is held off until <see cref="Uptime"/> passes this, since the geometry, shader
    /// specialization, and camera position are all still settling right after load; culling against a
    /// depth pyramid from those first frames risks hiding things that should be visible.
    /// </summary>
    private const float OcclusionCullWarmupSeconds = 1f;

    /// <summary>
    /// Total time elapsed since the renderer was started, in seconds.
    /// </summary>
    public float Uptime { get; set; }

    /// <summary>
    /// Time elapsed since the last frame, in seconds.
    /// </summary>
    public float DeltaTime { get; set; }

    /// <summary>
    /// Shared renderer context containing loaders and caches.
    /// </summary>
    public RendererContext RendererContext { get; }

    /// <summary>
    /// The RHI command list this renderer records into, or <see langword="null"/> when it is drawing
    /// through OpenGL directly. Assigned per frame by <see cref="DrawMainScene"/> and
    /// <see cref="Render(Scene.RenderContext)"/> from <see cref="Device"/>, and put on the
    /// <see cref="Scene.RenderContext"/> they build, which carries it to every scene node and renderer.
    /// </summary>
    /// <remarks>Re-read it after a render call rather than assigning it: both entry points take a fresh
    /// list per frame, because acquiring one is what rewinds it. A caller that wants to own the lifetime
    /// instead passes a <see cref="Scene.RenderContext"/> that already carries a list to
    /// <see cref="Render(Scene.RenderContext)"/>, which then neither replaces nor submits it.</remarks>
    public RHI.ICommandList? CommandList { get; set; }

    /// <summary>
    /// A device whose <see cref="RHI.IDevice.BeginCommandList"/> threw, remembered so the probe is not
    /// repeated every frame. Held by reference, so replacing the device retries.
    /// </summary>
    private RHI.IDevice? nonRecordingDevice;

    /// <summary>
    /// The graphics device this renderer draws with, assigned by the presentation layer on
    /// <see cref="RendererContext"/>. <see langword="null"/> until a backend has been brought up.
    /// </summary>
    public RHI.IDevice? Device => RendererContext.Device;

    /// <summary>
    /// Active camera used for view and projection transforms.
    /// </summary>
    public Camera Camera { get; set; }

    /// <summary>
    /// Secondary camera used to render the first-person viewmodel layer with its own FOV.
    /// Synced to <see cref="Camera"/>'s position/orientation each frame; see <see cref="RenderScenesWithView"/>.
    /// </summary>
    public Camera ViewmodelCamera { get; }

    /// <summary>
    /// Per-frame rendering statistics, including CPU/GPU profiling timings
    /// </summary>
    public PerfStats PerfStats { get; } = new();

    /// <summary>
    /// The main scene to render.
    /// </summary>
    public Scene Scene { get; set; }

    /// <summary>
    /// Optional 3D skybox scene rendered behind the main scene.
    /// </summary>
    public Scene? SkyboxScene { get; set; }

    /// <summary>
    /// Optional 2D skybox rendered as the scene background.
    /// </summary>
    public SceneSkybox2D? Skybox2D { get; set; }

    /// <summary>
    /// Default background used when no skybox is available.
    /// </summary>
    public SceneBackground? BaseBackground { get; protected set; }

    /// <summary>
    /// GPU uniform buffer containing per-view constants such as view-projection matrices.
    /// </summary>
    public UniformBuffer<ViewConstants>? ViewBuffer { get; set; }

    /// <summary>Gets the fullscreen tile mask overlay drawn in the tile debug render modes.</summary>
    public LightTilesOverlay LightTilesOverlay { get; }

    /// <summary>
    /// Named textures bound to reserved slots for all render passes.
    /// </summary>
    public List<(ReservedTextureSlots Slot, string Name, RenderTexture Texture)> Textures { get; } = [];

    internal Shader depthOnlyShader = null!;
    private readonly Frustum barnLightShadowFrustum = new();
    /// <summary>
    /// Depth-only framebuffer used for directional (sun) light shadow mapping.
    /// </summary>
    public Framebuffer? ShadowDepthBuffer { get; private set; }

    /// <summary>
    /// Depth-only framebuffer atlas used for barn light shadow mapping.
    /// </summary>
    public Framebuffer? BarnLightShadowBuffer { get; private set; }
    /// <summary>
    /// Resolved (non-MSAA) scene color in rgba16f format, used for refraction, bloom input, and luminance computation.
    /// Filled by <see cref="GrabFramebufferCopy"/>.
    /// </summary>
    public RenderTexture? ResolvedSceneColor { get; private set; }

    /// <summary>
    /// Resolved (non-MSAA) scene depth in R32F format, used for the depth pyramid and occlusion culling.
    /// Filled by <see cref="GrabFramebufferCopy"/>.
    /// </summary>
    public RenderTexture? ResolvedSceneDepth { get; private set; }

    /// <summary>
    /// When set, forces <see cref="ResolvedSceneDepth"/> to be refreshed this frame even if no material
    /// or occlusion pass requests it. Used by overlays (e.g. world-space text) that need the scene depth
    /// to occlude themselves against geometry. Must be set before <see cref="Render(Scene.RenderContext)"/>.
    /// </summary>
    public bool ForceResolveSceneDepth { get; set; }

    private readonly Shader[] histogramShaders = new Shader[2];
    private readonly StorageBuffer[] histogramBuffers = new StorageBuffer[2];

    // Injected
    /// <summary>
    /// Target framebuffer for the main scene render; must be set before calling <see cref="Render(Scene.RenderContext)"/>.
    /// </summary>
    public Framebuffer? MainFramebuffer { get; set; }

    /// <summary>
    /// Post-processing renderer handling tone mapping, bloom, and MSAA resolve.
    /// </summary>
    public PostProcessRenderer Postprocess { get; set; }

    /// <summary>
    /// When not <see langword="null"/>, culling uses this frustum instead of the camera frustum, freezing the cull state.
    /// </summary>
    public Frustum? LockedCullFrustum { get; set; }

    /// <summary>
    /// When not <see langword="null"/>, PVS queries use this position instead of the camera position, freezing the PVS state.
    /// </summary>
    public Vector3? LockedCullPosition { get; set; }

    /// <summary>
    /// When <see langword="true"/>, every form of culling (CPU frustum, GPU meshlet, occlusion, PVS, shadow
    /// frustum) is bypassed so the whole scene is submitted.
    /// </summary>
    public bool DisableAllCulling { get; set; }

    /// <summary>Reused so <see cref="Scene.GetFrustumCullResults"/> keeps its cache across pre-warm calls.</summary>
    private readonly Frustum noCullFrustum = Frustum.CreateEmpty();

    /// <summary>The frustum to cull against, or <see langword="null"/> to use the camera's own frustum.</summary>
    private Frustum? CullFrustum => DisableAllCulling ? noCullFrustum : LockedCullFrustum;

    // options
    /// <summary>
    /// Width and height in texels of the shadow depth buffers.
    /// </summary>
    public int ShadowTextureSize { get; set; } = 1024;

    /// <summary>
    /// When <see langword="true"/>, geometry is rendered as wireframe lines.
    /// </summary>
    public bool IsWireframe { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the 3D skybox scene is included in scene rendering. Does not affect the 2D skybox.
    /// </summary>
    public bool ShowSkybox { get; set; } = true;

    /// <summary>
    /// Initializes a new renderer with the given context.
    /// </summary>
    /// <param name="rendererContext">Shared context providing loaders and caches.</param>
    public Renderer(RendererContext rendererContext)
    {
        RendererContext = rendererContext;
        Postprocess = new(rendererContext);
        LightTilesOverlay = new(rendererContext);
        Camera = new Camera(rendererContext.FieldOfView);
        ViewmodelCamera = new Camera();
        Scene = new Scene(rendererContext);
    }

    /// <summary>
    /// Default sun angles for lighting used by viewers without lighting information
    /// </summary>
    public static Vector2 DefaultSunAngles { get; } = new(80f, 170f);

    /// <summary>
    /// Default sun color for lighting used by viewers without lighting information
    /// </summary>
    public static Vector4 DefaultSunColor { get; } = new(new Vector3(255, 247, 235) / 255.0f, 2.5f);

    /// <summary>
    /// Load default lighting, used by viewers without lighting information
    /// </summary>
    public static void LoadDefaultLighting(Scene scene, Resource ibl)
    {
        var texture = scene.RendererContext.MaterialLoader.LoadTexture(ibl, true);
        var environmentMap = new SceneEnvMap(scene, new AABB(new Vector3(float.MinValue), new Vector3(float.MaxValue)))
        {
            Transform = Matrix4x4.Identity,
            EdgeFadeDists = Vector3.Zero,
            HandShake = 0,
            ProjectionMode = 0,
            EnvMapTexture = texture,
        };

        scene.LightingInfo.AddEnvironmentMap(environmentMap);
        scene.LightingInfo.UseSceneBoundsForSunLightFrustum = true;

        var sunForward = EntityTransformHelper.EulerAnglesToForwardDirection(new Vector3(DefaultSunAngles.X, DefaultSunAngles.Y, 0f));
        scene.LightingInfo.LightingData.SunDirection = new Vector4(-sunForward, 0f);
        scene.LightingInfo.LightingData.SunColor =
            new Vector4(new Vector3(DefaultSunColor.X, DefaultSunColor.Y, DefaultSunColor.Z) * DefaultSunColor.W, 1f);
    }

    /// <summary>
    /// Allocates GPU resources required for rendering; must be called once before <see cref="Render(Scene.RenderContext)"/>.
    /// </summary>
    public void Initialize()
    {
        ViewBuffer = new UniformBuffer<ViewConstants>(ReservedBufferSlots.View);
        Skybox2D = BaseBackground = new SceneBackground(Scene);

        ShadowDepthBuffer = Framebuffer.Prepare(nameof(ShadowDepthBuffer), ShadowTextureSize, ShadowTextureSize, 0, null, Framebuffer.DepthAttachmentFormat.Depth32F);
        ShadowDepthBuffer.Initialize();
        ShadowDepthBuffer.ClearMask = ClearBufferMask.DepthBufferBit;
        Debug.Assert(ShadowDepthBuffer.Depth != null);

        GL.DrawBuffer(DrawBufferMode.None);
        GL.ReadBuffer(ReadBufferMode.None);
        ShadowDepthBuffer.SetShadowDepthSamplerState();
        Textures.Add(new(ReservedTextureSlots.ShadowDepthBufferDepth, "g_tShadowDepthBufferDepth", ShadowDepthBuffer.Depth));

        // Barn light shadow atlas
        BarnLightShadowBuffer = Framebuffer.Prepare(nameof(BarnLightShadowBuffer), 4, 4, 0, null, Framebuffer.DepthAttachmentFormat.Depth16);
        BarnLightShadowBuffer.Initialize();
        BarnLightShadowBuffer.ClearMask = ClearBufferMask.DepthBufferBit;
        Debug.Assert(BarnLightShadowBuffer.Depth != null);

        GL.DrawBuffer(DrawBufferMode.None);
        GL.ReadBuffer(ReadBufferMode.None);
        BarnLightShadowBuffer.SetShadowDepthSamplerState(true);
        Textures.Add(new(ReservedTextureSlots.BarnLightShadowDepth, "g_tBarnLightShadowDepth", BarnLightShadowBuffer.Depth));

        depthOnlyShader = Scene.RendererContext.ShaderLoader.LoadShader("depth_only");

        histogramShaders[0] = Scene.RendererContext.ShaderLoader.LoadShader("histogram");
        histogramShaders[1] = Scene.RendererContext.ShaderLoader.LoadShader("histogram", ("D_HISTOGRAM_MODE", 1));

        histogramBuffers[0] = StorageBuffer.Allocate<uint>(ReservedBufferSlots.Histogram, 256, BufferUsageHint.DynamicDraw);
        histogramBuffers[1] = StorageBuffer.Allocate<uint>(ReservedBufferSlots.AverageLuminance, 4, BufferUsageHint.DynamicRead);

        // Created through the RhiFormat overloads so the textures describe themselves completely: the MSAA
        // resolve binds both as storage images through the command list, which takes the image format from
        // the texture rather than from the call site.
        ResolvedSceneColor = RenderTexture.Create(4, 4, RHI.RhiFormat.R16G16B16A16_SFloat);
        ResolvedSceneColor.SetFiltering(TextureMinFilter.Linear, TextureMagFilter.Linear);
        ResolvedSceneColor.SetWrapMode(TextureWrapMode.ClampToEdge);

        ResolvedSceneDepth = RenderTexture.Create(4, 4, RHI.RhiFormat.R32_SFloat);

        Textures.Add(new(ReservedTextureSlots.SceneColor, "g_tSceneColor", ResolvedSceneColor));
        Textures.Add(new(ReservedTextureSlots.SceneDepth, "g_tSceneDepth", ResolvedSceneDepth));

        EnsureDepthPyramidSize(256, 256);
    }

    /// <summary>Slots out of <see cref="MaterialLoader.ShaderTextures"/> that have been resolved.</summary>
    private readonly HashSet<ReservedTextureSlots> loadedShaderTextures = [];

    /// <summary>
    /// Loads any used texture from the <see cref="MaterialLoader.ShaderTextures"/> list.
    /// </summary>
    private void LoadShaderTextures()
    {
        if (loadedShaderTextures.Count == MaterialLoader.ShaderTextures.Count)
        {
            return;
        }

        var declared = RendererContext.ShaderLoader.DeclaredReservedTextures;

        foreach (var (slot, name, path) in MaterialLoader.ShaderTextures)
        {
            if (!declared.Contains(name) || !loadedShaderTextures.Add(slot))
            {
                continue;
            }

            using var resource = RendererContext.FileLoader.LoadFileCompiled(path);

            var texture = resource != null
                ? RendererContext.MaterialLoader.LoadTexture(resource)
                : RendererContext.MaterialLoader.GetDefaultColor();

            Textures.Add(new(slot, name, texture));
        }
    }

    /// <summary>
    /// Loads embedded or game-provided BRDF LUT, cube fog, and blue noise textures into <see cref="Textures"/>.
    /// </summary>
    public void LoadRendererResources()
    {
        var rendererAssembly = Assembly.GetAssembly(typeof(RendererContext)) ?? throw new InvalidOperationException("Failed to get renderer assembly");
        const string vtexFileName = "brdf_lut.vtex_c";

        // Load brdf lut, preferably from game.
        var brdfLutResource = RendererContext.FileLoader.LoadFile("textures/dev/" + vtexFileName);

        const int BrdfTextureSize = 64;
        const int BrdfTextureDepth = 3;

        if (brdfLutResource?.DataBlock is not Texture gameBrdfLut
        || gameBrdfLut.Width != BrdfTextureSize
        || gameBrdfLut.Height != BrdfTextureSize
        || gameBrdfLut.Depth != BrdfTextureDepth
        || gameBrdfLut.Format != VTexFormat.RGBA16161616F)
        {
            brdfLutResource?.Dispose();
            brdfLutResource = null;
        }

        try
        {
            if (brdfLutResource == null)
            {
                // Will be used by LoadTexture, and disposed by resource
                var brdfStream = rendererAssembly.GetManifestResourceStream("Renderer.Resources." + vtexFileName)
                    ?? throw new InvalidOperationException($"Failed to load embedded resource: {vtexFileName}");

                brdfLutResource = new Resource() { FileName = vtexFileName };
                brdfLutResource.Read(brdfStream);
            }

            var brdfLutTexture = Scene.RendererContext.MaterialLoader.LoadTexture(brdfLutResource);
            brdfLutTexture.SetWrapMode(TextureWrapMode.ClampToEdge);
            Textures.Add(new(ReservedTextureSlots.BRDFLookup, "g_tBRDFLookup", brdfLutTexture));
        }
        finally
        {
            brdfLutResource?.Dispose();
        }

        // Load default cube fog texture.
        using var cubeFogStream = rendererAssembly.GetManifestResourceStream("Renderer.Resources.sky_furnace.vtex_c") ?? throw new InvalidOperationException("Failed to load embedded cube fog texture.");
        using var cubeFogResource = new Resource() { FileName = "default_cube.vtex_c" };
        cubeFogResource.Read(cubeFogStream);

        var defaultCubeTexture = Scene.RendererContext.MaterialLoader.LoadTexture(cubeFogResource);
        Textures.Add(new(ReservedTextureSlots.FogCubeTexture, "g_tFogCubeTexture", defaultCubeTexture));


        const string blueNoiseName = "blue_noise_256.vtex_c";
        var blueNoiseResource = RendererContext.FileLoader.LoadFile("textures/dev/" + blueNoiseName);

        try
        {
            Stream? blueNoiseStream; // Same method as brdf

            if (blueNoiseResource == null)
            {
                blueNoiseStream = rendererAssembly.GetManifestResourceStream("Renderer.Resources." + blueNoiseName);

                if (blueNoiseStream == null)
                {
                    throw new InvalidOperationException($"Failed to load embedded resource: {blueNoiseName}");
                }

                blueNoiseResource = new Resource() { FileName = blueNoiseName };
                blueNoiseResource.Read(blueNoiseStream);
            }

            var blueNoise = Scene.RendererContext.MaterialLoader.LoadTexture(blueNoiseResource);
            Postprocess.BlueNoise = blueNoise;
            Textures.Add(new(ReservedTextureSlots.BlueNoise, "g_tBlueNoise", blueNoise));
        }
        finally
        {
            blueNoiseResource?.Dispose();
        }
    }

    void UpdatePerViewGpuBuffers(Scene scene, Camera camera, float deltaTime, RHI.ICommandList? commandList = null)
    {
        Debug.Assert(ViewBuffer != null);

        {
            // Skip occlusion culling if the camera moved too much -- we use last frame depth
            var moveDelta = ViewBuffer.Data.CameraPosition - camera.Location;
            var eyeDelta = ViewBuffer.Data.CameraDirWs - camera.Forward;

            var t = moveDelta.LengthSquared();
            var t2 = eyeDelta.LengthSquared();

            if (t > 5000f || t2 > 0.5f)
            {
                scene.DepthPyramidValid = false;
            }
            else
            {
                ViewBuffer.Data.WorldToProjectionPrev = scene.DepthPyramidViewProjection;
            }

            scene.UpdateIndirectRenderingState();
        }

        camera.SetViewConstants(ViewBuffer.Data);
        scene.SetFogConstants(ViewBuffer.Data);

        var cullWidth = (int)ViewBuffer.Data.ViewportSize.X;
        var cullHeight = (int)ViewBuffer.Data.ViewportSize.Y;

        var tileCullEnabled = LockedCullFrustum == null && scene.EnableTiledLightCulling;
        scene.LightBinner.Update(ViewBuffer.Data, cullWidth, cullHeight, tileCullEnabled);
        SkyboxScene?.LightBinner.Update(ViewBuffer.Data, cullWidth, cullHeight, tileCullEnabled);

        BindUniformBuffer(commandList, ViewBuffer);
        ViewBuffer.Update();

        // A locked cull frustum leaves the indirect buffers untouched, freezing the cull state. Disabled
        // culling still has to dispatch, otherwise the indirect draw commands keep the previous contents.
        Frustum? gpuCullFrustum = DisableAllCulling
            ? noCullFrustum
            : LockedCullFrustum == null ? camera.ViewFrustum : null;

        if (gpuCullFrustum.HasValue)
        {
            if (scene.DrawMeshletsIndirect)
            {
                scene.MeshletCullGpu(gpuCullFrustum.Value);
            }

            if (scene.CompactMeshletDraws)
            {
                scene.CompactIndirectDraws();
            }

            using (new GLDebugGroup("Cull Tiles and Depth Bins"))
            {
                scene.LightBinner.Dispatch();
                SkyboxScene?.LightBinner.Dispatch();
            }
        }

        if (Postprocess != null)
        {
            Postprocess.State = scene.PostProcessInfo.CurrentState;
            Postprocess.ResolveColorCorrection(scene.PostProcessInfo.ActiveLuts, commandList);
            Postprocess.CalculateTonemapScalar(deltaTime);
        }
    }

    private static void RenderTranslucentLayer(Scene scene, Scene.RenderContext renderContext)
    {
        scene.RenderOpaqueRefractLayer(renderContext);
        scene.RenderWaterLayer(renderContext);

        using var _ = scene.RendererContext.RenderState.Scope(depthWrite: false, blend: true);

        scene.RenderTranslucentLayer(renderContext);
    }

    /// <summary>
    /// Whether the renderer records through <see cref="RHI.ICommandList"/> instead of calling OpenGL
    /// directly. Off until the migration can honour the contract end to end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A draw needs two things neither of which it carries itself: an open render pass, and a bound
    /// pipeline to take its topology and vertex input from. The passes now exist &#8212; the scene,
    /// shadow and overlay work below all runs inside one &#8212; so turning this on gets as far as the
    /// pipeline, and every scene that draws through the RHI then throws "a draw needs a graphics
    /// pipeline bound".
    /// </para>
    /// <para>
    /// Turn it on once the draw sites bind pipelines. With the golden harness on the recording device,
    /// 25 of 33 scenes already render identically with this set; the 8 that do not are the ones
    /// reaching <see cref="SceneNodes.ShapeSceneNode"/>, which is the only draw site the suite covers.
    /// Expect that suite to be the thing that says whether it worked.
    /// </para>
    /// </remarks>
    public static bool EnableRhiRecording { get; set; }

    /// <summary>
    /// Acquires this frame's command list from <see cref="Device"/>, or returns <see langword="null"/>
    /// when there is no device or it does not record, in which case every pass draws through OpenGL as
    /// before.
    /// </summary>
    /// <remarks>
    /// Opening and closing the frame itself stays with the presentation layer, which owns the swapchain
    /// and is the only thing that knows where a frame really begins: the renderer draws one part of one,
    /// with post-processing and overlays still to come after it returns.
    /// </remarks>
    /// <param name="name">Debug label for the recorded work.</param>
    private RHI.ICommandList? AcquireCommandList(string name = "Scene")
    {
        if (!EnableRhiRecording)
        {
            return null;
        }

        var device = RendererContext.Device;

        if (device is null || ReferenceEquals(device, nonRecordingDevice))
        {
            return null;
        }

        try
        {
            return device.BeginCommandList(name);
        }
        catch (NotSupportedException e)
        {
            // A device that creates resources but records nothing, which GLDevice documents as its own
            // behaviour. Remembered so a viewer holding one does not pay a throw every frame.
            nonRecordingDevice = device;

            // Reported rather than swallowed: falling back is safe, but silently taking the OpenGL route
            // on a device that was meant to record would look exactly like the RHI path working.
            RendererContext.Logger.LogWarning(e,
                "{Device} cannot record, so the renderer is drawing through OpenGL directly and no RHI path will run.",
                device.GetType().Name);

            return null;
        }
    }

    /// <summary>
    /// Holds an open render pass so it can be closed with <c>using</c>. The default value holds nothing
    /// and closes nothing, which is what every helper returns while the renderer is not recording.
    /// </summary>
    private readonly struct RhiPass(RHI.ICommandList? commandList) : IDisposable
    {
        /// <summary>Ends the pass, if one was opened.</summary>
        public void Dispose() => commandList?.EndRenderPass();
    }

    /// <summary>
    /// Opens a render pass over the context's framebuffer, or does nothing when not recording.
    /// </summary>
    /// <param name="renderContext">The pass being drawn, supplying the framebuffer and command list.</param>
    /// <param name="name">Debug label for the pass.</param>
    /// <param name="keepContents">
    /// <see langword="true"/> to load what the attachments already hold instead of clearing them. Every
    /// pass after the first in a frame must set this, or it would erase what the earlier ones drew.
    /// </param>
    /// <returns>A guard that ends the pass.</returns>
    /// <remarks>
    /// A frame is split into several passes rather than one because transfers and compute dispatches are
    /// not valid inside a pass, and the scene has both in the middle of it: the framebuffer grab and the
    /// depth pyramid sit between the opaque and translucent halves.
    /// </remarks>
    private static RhiPass BeginPass(in Scene.RenderContext renderContext, string name, bool keepContents = false)
        => BeginPass(renderContext.CommandList, renderContext.Framebuffer, name, keepContents);

    /// <summary>
    /// Opens a render pass over an explicit framebuffer, or does nothing when not recording.
    /// </summary>
    /// <param name="commandList">The command list to record into, or <see langword="null"/> to do nothing.</param>
    /// <param name="framebuffer">The framebuffer whose attachments the pass renders into.</param>
    /// <param name="name">Debug label for the pass.</param>
    /// <param name="keepContents">Whether to load the attachments rather than clear them.</param>
    /// <param name="clearDepth">
    /// The depth value to clear to, or <see langword="null"/> for the reverse-Z far plane the framebuffer
    /// itself describes. Only the barn light atlas needs this: it is the one target the renderer draws
    /// with forward depth, so it clears to the opposite end of the range from everything else.
    /// </param>
    /// <returns>A guard that ends the pass.</returns>
    private static RhiPass BeginPass(RHI.ICommandList? commandList, Framebuffer framebuffer, string name,
        bool keepContents = false, float? clearDepth = null)
    {
        if (commandList is null)
        {
            return default;
        }

        var desc = framebuffer.RenderPass(name);

        if (keepContents)
        {
            desc = KeepContents(in desc);
        }

        if (clearDepth is { } depth && desc.DepthAttachment is { } attachment)
        {
            desc = desc with { DepthAttachment = attachment with { ClearDepth = depth } };
        }

        commandList.BeginRenderPass(desc);

        return new RhiPass(commandList);
    }

    /// <summary>
    /// Rewrites a descriptor's load operations to preserve what its attachments already hold.
    /// </summary>
    /// <param name="desc">The descriptor to rewrite.</param>
    /// <returns>The same attachments, loaded rather than cleared.</returns>
    /// <remarks>A stencil aspect that was already <see cref="RHI.LoadOp.DontCare"/> stays that way: the
    /// framebuffer marks it so when the depth format carries no stencil, and asking to load one that
    /// does not exist is an error rather than a no-op.</remarks>
    private static RHI.RenderPassDesc KeepContents(in RHI.RenderPassDesc desc)
    {
        var colors = new RHI.ColorAttachmentDesc[desc.ColorAttachments.Length];

        for (var i = 0; i < colors.Length; i++)
        {
            colors[i] = desc.ColorAttachments[i] with { LoadOp = RHI.LoadOp.Load };
        }

        var depth = desc.DepthAttachment is { } attachment
            ? attachment with
            {
                DepthLoadOp = RHI.LoadOp.Load,
                StencilLoadOp = attachment.StencilLoadOp == RHI.LoadOp.DontCare ? RHI.LoadOp.DontCare : RHI.LoadOp.Load,
            }
            : (RHI.DepthAttachmentDesc?)null;

        return desc with { ColorAttachments = colors, DepthAttachment = depth };
    }

    /// <summary>
    /// Renders the opaque and translucent layers of the main scene to <see cref="MainFramebuffer"/>.
    /// </summary>
    public void DrawMainScene()
    {
        if (MainFramebuffer is null)
        {
            throw new InvalidOperationException("MainFramebuffer must be set before rendering");
        }

        // Re-acquired every frame rather than kept: acquiring is what rewinds the OpenGL backend's one
        // reused list, so skipping it would leak its transient uniform ring across frames.
        CommandList = AcquireCommandList();

        var renderContext = new Scene.RenderContext
        {
            Camera = Camera,
            Framebuffer = MainFramebuffer,
            CommandList = CommandList,
            Scene = Scene,
            Textures = Textures,
        };

        LoadShaderTextures();
        UpdatePerViewGpuBuffers(Scene, Camera, DeltaTime, renderContext.CommandList);
        Scene.SetSceneBuffers(renderContext.CommandList);

        Scene.RenderOpaqueLayer(renderContext);
        RenderTranslucentLayer(Scene, renderContext);

        if (renderContext.CommandList is { } commandList)
        {
            RendererContext.Device?.Submit(commandList);
        }
    }

    /// <summary>
    /// Renders the scene to the specified framebuffer. The result will be in linear space.
    /// </summary>
    /// <param name="framebuffer">Framebuffer with hdr color support.</param>
    public void Render(Framebuffer framebuffer)
    {
        // No command list seeded from the property on purpose: it still holds the previous frame's list,
        // and passing that back in would read as caller-owned, so Render would neither rewind nor
        // resubmit it. Leaving it null is what makes Render take a fresh one for this frame.
        var renderContext = new Scene.RenderContext
        {
            Camera = Camera,
            Framebuffer = framebuffer,
            Scene = Scene,
            Textures = Textures,
        };

        Render(renderContext);
    }


    /// <summary>
    /// Renders shadows and then the full scene using the provided render context.
    /// </summary>
    public void Render(Scene.RenderContext renderContext)
    {
        // A context that already carries a list belongs to whoever built it, so it is neither replaced
        // nor submitted here. Otherwise this frame's list is ours to take and hand back.
        var ownedCommandList = renderContext.CommandList is null ? AcquireCommandList() : null;

        if (ownedCommandList is not null)
        {
            renderContext.CommandList = ownedCommandList;
            CommandList = ownedCommandList;
        }

        LoadShaderTextures();

        // Render backfaces into shadow maps
        GL.FrontFace(FrontFaceDirection.Cw);

        RenderSceneShadows(renderContext);
        RenderBarnLightShadows(renderContext);

        GL.FrontFace(FrontFaceDirection.Ccw);

        RenderScenesWithView(renderContext);

        if (ownedCommandList is not null)
        {
            RendererContext.Device?.Submit(ownedCommandList);
        }
    }

    /// <summary>
    /// Renders the main and skybox scenes using the camera and framebuffer specified in the render context.
    /// </summary>
    public void RenderScenesWithView(Scene.RenderContext renderContext)
    {
        if (ViewBuffer == null)
        {
            throw new InvalidOperationException("Initialize() must be called before rendering");
        }

        var (w, h) = (renderContext.Framebuffer.Width, renderContext.Framebuffer.Height);

        GL.Viewport(0, 0, w, h);
        ViewBuffer.Data.ViewportSize = new Vector2(w, h);
        ViewBuffer.Data.InvViewportSize = Vector2.One / ViewBuffer.Data.ViewportSize;

        // A frame at the baseline. Entry applies it, so the clear (which obeys the write masks)
        // does not see state latched by the previous frame.
        using var frameScope = RendererContext.RenderState.Scope();
        renderContext.Framebuffer.BindAndClear();

        var isMainFramebuffer = ReferenceEquals(renderContext.Framebuffer, MainFramebuffer);
        var isStandardPass = renderContext.ReplacementShader == null && isMainFramebuffer;

        if (!isStandardPass)
        {
            PerfStats.Active.SuspendTriangleCounter();
        }

        var isWireframe = IsWireframe && isStandardPass; // To avoid toggling it mid frame
        var computeFramebufferLuminance = Postprocess.State.ExposureSettings.AutoExposureEnabled;


        // TODO: check if renderpass allows wireframe mode
        // TODO+: replace wireframe shaders with solid color
        // A baseline so sub-passes and materials compose over it. Disposed before post-processing,
        // which must not inherit wireframe.
        var wireframeScope = isWireframe
            ? RendererContext.RenderState.Scope(fillMode: FillMode.Wireframe)
            : default;

        UpdatePerViewGpuBuffers(Scene, renderContext.Camera, DeltaTime, renderContext.CommandList);

        // The opaque half. Ends before the framebuffer grab, which copies and dispatches compute, and
        // neither is valid inside a pass.
        var opaquePass = BeginPass(in renderContext, "Scene Opaque");

        using (new GLDebugGroup("Viewmodel Opaque"))
        {
            var mainCamera = renderContext.Camera;

            ViewmodelCamera.CopyFrom(mainCamera);
            ViewmodelCamera.FieldOfView = ComputeViewmodelFov();
            ViewmodelCamera.CreateProjectionMatrix();
            ViewmodelCamera.RecalculateMatrices();

            DepthRange.Viewmodel.Apply();

            ViewmodelCamera.SetViewConstants(ViewBuffer.Data);
            Scene.SetFogConstants(ViewBuffer.Data);

            var viewmodelTileRemap = ViewmodelCamera.GetPixelRemapTo(mainCamera, ViewBuffer.Data.ViewportSize);
            Scene.LightBinner.SetPixelRemap(viewmodelTileRemap);

            BindUniformBuffer(renderContext.CommandList, ViewBuffer);
            ViewBuffer.Update();
            Scene.SetSceneBuffers(renderContext.CommandList);

            renderContext.Camera = ViewmodelCamera;
            renderContext.Scene = Scene;
            Scene.RenderViewmodelOpaqueLayer(renderContext);
            renderContext.Camera = mainCamera;

            DepthRange.Scene.Apply();

            mainCamera.SetViewConstants(ViewBuffer.Data);
            Scene.SetFogConstants(ViewBuffer.Data);
            Scene.LightBinner.SetPixelRemap(ViewConstants.PixelRemapIdentity);
            BindUniformBuffer(renderContext.CommandList, ViewBuffer);
            ViewBuffer.Update();
        }

        Scene.SetSceneBuffers(renderContext.CommandList);

        using (new GLDebugGroup("Main Scene Opaque Render"))
        {
            renderContext.Scene = Scene;
            Scene.RenderOpaqueLayer(renderContext, isStandardPass ? depthOnlyShader : null);
        }

        // Opened inside the block below, once the copies that have to sit between the two passes are done.
        var translucentPass = default(RhiPass);

        //using (new GLDebugGroup("Sky Render"))
        {
            DepthRange.Sky.Apply();

            renderContext.ReplacementShader?.SetUniform1AllVariants("isSkybox", 1u);
            var skyboxScene = SkyboxScene;
            var render3DSkybox = ShowSkybox && skyboxScene != null;
            var (copyColor, copyDepth) = (Scene.WantsSceneColor, Scene.WantsSceneDepth);
            copyDepth |= ForceResolveSceneDepth;
            Postprocess.HasOutlineObjects = Scene.HasOutlineObjects;

            if (render3DSkybox)
            {
                Debug.Assert(skyboxScene is not null); // analyzer is failing here

                // The skybox is a different Scene with its own lighting buffers, but not a different
                // frame: it draws into the same pass through the same renderContext below, so it records
                // into that context's list rather than one of its own.
                skyboxScene.SetSceneBuffers(renderContext.CommandList);
                renderContext.Scene = skyboxScene;

                copyColor |= skyboxScene.WantsSceneColor;
                copyDepth |= skyboxScene.WantsSceneDepth;
                Postprocess.HasOutlineObjects |= skyboxScene.HasOutlineObjects;

                using var _ = new GLDebugGroup("3D Sky Scene");
                skyboxScene.RenderOpaqueLayer(renderContext);
            }

            if (!isWireframe)
            {
                using (new GLDebugGroup("2D Sky Render"))
                {
                    Skybox2D?.Render();
                }
            }

            copyColor |= computeFramebufferLuminance;

            // Everything below copies or dispatches, so the opaque pass has to close first even when
            // nothing ends up being copied.
            opaquePass.Dispose();

            if (isMainFramebuffer)
            {
                var generateDepthPyramid = Scene.EnableOcclusionCulling
                    && Scene.DrawMeshletsIndirect
                    && LockedCullFrustum == null
                    && !DisableAllCulling
                    && Uptime >= OcclusionCullWarmupSeconds;

                copyDepth |= generateDepthPyramid;
                Scene.DepthPyramidValid = !DisableAllCulling && (generateDepthPyramid || LockedCullFrustum != null);

                GrabFramebufferCopy(renderContext.Framebuffer, copyColor, copyDepth, renderContext.CommandList);

                if (generateDepthPyramid)
                {
                    Debug.Assert(ResolvedSceneColor != null && ResolvedSceneDepth != null);
                    EnsureDepthPyramidSize(renderContext.Framebuffer.Width, renderContext.Framebuffer.Height);
                    Scene.GenerateDepthPyramid(ResolvedSceneDepth);
                    Scene.DepthPyramidViewProjection = Camera.ViewProjectionMatrix;
                    Scene.DepthPyramidValid = true;
                }
            }

            // The translucent half, loading rather than clearing so the opaque results it composites
            // over survive.
            translucentPass = BeginPass(in renderContext, "Scene Translucent", keepContents: true);

            if (render3DSkybox)
            {
                Debug.Assert(skyboxScene is not null); // analyzer is failing here

                using (new GLDebugGroup("3D Sky Scene Translucent Render"))
                {
                    RenderTranslucentLayer(skyboxScene, renderContext);
                }

                // Back to main scene.
                Scene.SetSceneBuffers(renderContext.CommandList);
                renderContext.Scene = Scene;
            }

            renderContext.ReplacementShader?.SetUniform1AllVariants("isSkybox", 0u);
            DepthRange.Scene.Apply();
        }

        using (new GLDebugGroup("Main Scene Translucent Render"))
        {
            RenderTranslucentLayer(Scene, renderContext);
        }

        using (new GLDebugGroup("Viewmodel Translucent"))
        {
            var mainCamera = renderContext.Camera;

            DepthRange.Viewmodel.Apply();

            ViewmodelCamera.SetViewConstants(ViewBuffer.Data);
            Scene.SetFogConstants(ViewBuffer.Data);
            Scene.LightBinner.SetPixelRemap(
                ViewmodelCamera.GetPixelRemapTo(mainCamera, ViewBuffer.Data.ViewportSize));
            ViewBuffer.BindBufferBase();
            ViewBuffer.Update();

            renderContext.Camera = ViewmodelCamera;
            Scene.RenderViewmodelTranslucentLayer(renderContext);
            renderContext.Camera = mainCamera;

            DepthRange.Scene.Apply();

            mainCamera.SetViewConstants(ViewBuffer.Data);
            Scene.SetFogConstants(ViewBuffer.Data);
            Scene.LightBinner.SetPixelRemap(ViewConstants.PixelRemapIdentity);
            ViewBuffer.BindBufferBase();
            ViewBuffer.Update();
        }

        // Closed before the luminance histogram below, which dispatches compute.
        translucentPass.Dispose();

        wireframeScope.Dispose();

        if (isStandardPass)
        {
            if (computeFramebufferLuminance)
            {
                ComputeAverageLuminance(renderContext);
            }

            // The overlays that draw on top of the finished scene, in a pass of their own because the
            // histogram above had to run outside one.
            using var overlayPass = BeginPass(in renderContext, "Scene Overlays", keepContents: true);

            if (Postprocess.HasOutlineObjects)
            {
                RenderOutlineLayer(renderContext);
            }

            var overlayBatch = ValveResourceFormat.Renderer.LightTilesOverlay.BatchFor(ViewBuffer!.Data.RenderMode);

            if (overlayBatch != ValveResourceFormat.Renderer.LightTilesOverlay.Batch.None)
            {
                var (tileBase, words) = Scene.LightBinner.GetOverlayRegion(
                    overlayBatch == ValveResourceFormat.Renderer.LightTilesOverlay.Batch.EnvMaps);

                LightTilesOverlay.Render(Scene.LightBinner.CullBits, tileBase, words);
            }
        }
        else
        {
            PerfStats.Active.ResumeTriangleCounter();
        }
    }

    /// <summary>
    /// Computes the first-person viewmodel camera's FOV.
    /// </summary>
    private float ComputeViewmodelFov()
    {
        var fovRatio = RendererContext.FieldOfView / 90f;

        return RendererContext.ViewmodelFieldOfView * fovRatio;
    }

    /// <summary>
    /// Renders opaque shadow casters for the directional (sun) light into <see cref="ShadowDepthBuffer"/>.
    /// </summary>
    public void RenderSceneShadows(Scene.RenderContext renderContext)
    {
        if (ShadowDepthBuffer is null || ViewBuffer is null)
        {
            throw new InvalidOperationException("Initialize() must be called before rendering");
        }

        // A pass at the baseline. Entry applies it, so the depth clear (which obeys the write mask)
        // and the state-less depth-only draws do not see state latched by earlier draws.
        using var _ = RendererContext.RenderState.Scope();

        GL.Viewport(0, 0, ShadowDepthBuffer.Width, ShadowDepthBuffer.Height);
        ShadowDepthBuffer.Bind(FramebufferTarget.Framebuffer);
        GL.DepthRange(0, 1);

        // The pass opened below clears depth through its load op, so this is the OpenGL path's clear only.
        // Left in place when recording it would be a raw clear outside any pass, which Vulkan rejects.
        if (renderContext.CommandList is null)
        {
            GL.Clear(ClearBufferMask.DepthBufferBit);
        }

        renderContext.Framebuffer = ShadowDepthBuffer;
        renderContext.Scene = Scene;

        ViewBuffer.Data.WorldToProjection = Scene.LightingInfo.SunViewProjection;
        var worldToShadow = Scene.LightingInfo.SunViewProjection;
        ViewBuffer.Data.WorldToShadow = worldToShadow;
        ViewBuffer.Data.SunLightShadowBias = Scene.LightingInfo.SunLightShadowBias;
        ViewBuffer.Update();

        using var shadowPass = BeginPass(in renderContext, "Sun Shadows");

        using (new GLDebugGroup("Direct Light Shadows"))
        {
            PerfStats.Active.Count(Counter.DirectionalShadowMap);
            Scene.RenderOpaqueShadows(renderContext, depthOnlyShader, Scene.CulledShadowDrawCalls);
        }
    }

    private void RenderBarnLightShadows(Scene.RenderContext renderContext)
    {
        Debug.Assert(ViewBuffer != null);

        if (!ViewBuffer.Data!.ExperimentalLightsEnabled)
        {
            return;
        }

        if (Scene.LightingInfo.ShadowMapper.ShadowCasters.Count == 0)
        {
            return;
        }

        using var _ = new GLDebugGroup("Barn Light Shadows");
        Debug.Assert(BarnLightShadowBuffer != null);

        // The barn shadow atlas uses forward depth, unlike the reverse-Z main view.
        using (RendererContext.RenderState.Scope(depthFunc: Comparison.FartherEqual, slopeScaledDepthBias: 2f))
        {
            GL.DepthRange(0.0, 1.0);
            GL.ClearDepth(1.0);

            BarnLightShadowBuffer.Bind(FramebufferTarget.Framebuffer);

            var atlasSize = Scene.LightingInfo.BarnLightShadowAtlasSize;

            if (BarnLightShadowBuffer.Resize(atlasSize, atlasSize))
            {
                BarnLightShadowBuffer.SetShadowDepthSamplerState(true);
                Textures.RemoveAll(t => t.Slot == ReservedTextureSlots.BarnLightShadowDepth);
                Textures.Add(new(ReservedTextureSlots.BarnLightShadowDepth, "g_tBarnLightShadowDepth", BarnLightShadowBuffer.Depth!));
            }

            // Opened before the scissor is enabled, since beginning a pass turns scissoring off. The
            // atlas is the one target drawn with forward depth, so its far plane is 1 rather than 0.
            using var barnPass = BeginPass(renderContext.CommandList, BarnLightShadowBuffer,
                "Barn Light Shadows", clearDepth: 1f);

            GL.Enable(EnableCap.ScissorTest);
            GL.Viewport(0, 0, BarnLightShadowBuffer.Width, BarnLightShadowBuffer.Height);
            GL.Scissor(0, 0, BarnLightShadowBuffer.Width, BarnLightShadowBuffer.Height);

            // Not gated on the command list the way the sun atlas clear above is, deliberately: no golden
            // scene reaches this method at all, so removing the redundant clear here would be an untested
            // edit to unreached code. The pass opened above does clear depth to 1 through its load op, so
            // the same `if (renderContext.CommandList is null)` guard applies once a fixture with barn
            // light shadow casters exists to check it.
            GL.Clear(ClearBufferMask.DepthBufferBit);

            foreach (var caster in Scene.LightingInfo.ShadowMapper.ShadowCasters)
            {
                var region = caster.Region;

                if (region.Width == 0)
                {
                    continue;
                }

                PerfStats.Active.Count(Counter.BarnShadowMap);

                GL.Viewport(region.X, region.Y, region.Width, region.Height);
                GL.Scissor(region.X, region.Y, region.Width, region.Height);

                // The pass opens covering the whole atlas, so without this a recorded draw would ignore
                // the caster's region and write over every other face.
                renderContext.CommandList?.SetViewport(region.X, region.Y, region.Width, region.Height);
                renderContext.CommandList?.SetScissor(region.X, region.Y, region.Width, region.Height);

                ViewBuffer.Data.WorldToProjection = caster.WorldToFrustum;
                ViewBuffer.Update();

                barnLightShadowFrustum.Update(caster.WorldToFrustum);

                // This is performing culling mid render, reusing the scene draw lists.
                // Should be in update loop.
                Scene.SetupBarnLightFaceShadow(caster.Light, caster.FaceIndex, barnLightShadowFrustum);

                Scene.RenderOpaqueShadows(renderContext, depthOnlyShader, caster.Light.FaceShadowCache[caster.FaceIndex].DrawCalls!);
            }

            GL.Disable(EnableCap.ScissorTest);
            GL.ClearDepth(0.0);
        }
    }

    private void ComputeAverageLuminance(Scene.RenderContext renderContext)
    {
        Debug.Assert(ResolvedSceneColor != null);

        using var _ = new GLDebugGroup("Compute Average Luminance");

        var commandList = renderContext.CommandList;

        var width = ResolvedSceneColor.Width;
        var height = ResolvedSceneColor.Height;

        static void Dispatch(RHI.ICommandList? commandList, Shader shader, RenderTexture texture, int x, int y)
        {
            var logMin = -8f;
            var logRange = 13f;

            shader.Use();
            PostProcess.PostProcessRenderer.BindComputePipeline(commandList, shader);
            PostProcess.PostProcessRenderer.BindTexture(commandList, shader, 0, "inputImage", texture);
            shader.SetUniform1("logMinLuminance", logMin);
            shader.SetUniform1("logLuminanceRange", logRange);

            PostProcess.PostProcessRenderer.Dispatch(commandList, x, y);
        }

        histogramBuffers[0].Clear();
        BindStorageBuffer(commandList, histogramBuffers[0]);
        BindStorageBuffer(commandList, histogramBuffers[1]);

        var inputTex = ResolvedSceneColor;

        // Build histogram
        var groupsX = Math.Max(1, (width + 15) / 16);
        var groupsY = Math.Max(1, (height + 15) / 16);
        Dispatch(commandList, histogramShaders[0], inputTex, groupsX, groupsY);
        HistogramBarrier(commandList, histogramBuffers[0], readBack: false);

        // Reduce histogram
        Dispatch(commandList, histogramShaders[1], inputTex, 1, 1); // local_size_x = 256

        // The reduction's result is read by the client through a persistent mapping, which is the second
        // half of what the OpenGL path's buffer update bit orders.
        HistogramBarrier(commandList, histogramBuffers[1], readBack: true);

        var output = Vector4.Zero;
        histogramBuffers[1].Read(ref output);
        Postprocess.AverageLuminance = output.X;
    }

    /// <summary>Binds a storage buffer to its reserved slot, through the command list when recording.</summary>
    private static void BindStorageBuffer(RHI.ICommandList? commandList, StorageBuffer buffer)
    {
        if (commandList is null)
        {
            buffer.BindBufferBase();
            return;
        }

        commandList.BindStorageBuffer(buffer.BindingPoint, buffer.RhiBuffer);
    }

    /// <summary>Binds a uniform buffer to its reserved slot, through the command list when recording.</summary>
    /// <remarks>The scene-wide equivalent is <see cref="Scene.BindUniformBuffer"/>; this one exists so the
    /// view buffer, which belongs to the renderer rather than to any scene, binds the same way.</remarks>
    private static void BindUniformBuffer(RHI.ICommandList? commandList, Buffers.Buffer buffer)
    {
        if (commandList is null)
        {
            buffer.BindBufferBase();
            return;
        }

        commandList.BindUniformBuffer(buffer.BindingPoint, buffer.RhiBuffer);
    }

    /// <summary>Orders a histogram pass's storage writes against whatever reads them next.</summary>
    private static void HistogramBarrier(RHI.ICommandList? commandList, StorageBuffer buffer, bool readBack)
    {
        if (commandList is null)
        {
            var flags = MemoryBarrierFlags.ShaderStorageBarrierBit;

            if (readBack)
            {
                flags |= MemoryBarrierFlags.BufferUpdateBarrierBit;
            }

            GL.MemoryBarrier(flags);
            return;
        }

        // Two transitions rather than one, batched into a single call: the reduction's output is both read
        // by the next shader and mapped for the client, and the OpenGL backend unions their barrier bits.
        RHI.BufferBarrier[] barriers = readBack
            ? [
                new(buffer.RhiBuffer, RHI.ResourceState.ShaderWrite, RHI.ResourceState.ShaderRead),
                new(buffer.RhiBuffer, RHI.ResourceState.ShaderWrite, RHI.ResourceState.CopySource),
            ]
            : [new(buffer.RhiBuffer, RHI.ResourceState.ShaderWrite, RHI.ResourceState.ShaderRead)];

        commandList.Barrier(barriers, []);
    }

    private void RenderOutlineLayer(Scene.RenderContext renderContext)
    {
        using var _ = new GLDebugGroup("Outline Stencil Write");

        var outlineState = RendererContext.RenderState.CurrentPass;
        outlineState.DepthStencil.DepthWriteEnable = false;
        outlineState.DepthStencil.DepthTestEnable = false;
        outlineState.DepthStencil.Stencil = outlineState.DepthStencil.Stencil with
        {
            StencilEnable = true,
            Func = Comparison.Always,
            PassOp = StencilOperation.Replace,
        };
        outlineState.DepthStencil.StencilRef = 1;
        outlineState.Rasterizer.CullMode = CullMode.None;

        using (new RenderPassScope(RendererContext.RenderState, in outlineState))
        {
            SkyboxScene?.RenderOutlineLayer(renderContext);
            Scene.RenderOutlineLayer(renderContext);
        }
    }

    private void EnsureResolvedTextureSize(int width, int height)
    {
        if (ResolvedSceneColor!.Width != width ||
            ResolvedSceneColor.Height != height)
        {
            ResolvedSceneColor.Delete();
            ResolvedSceneColor = RenderTexture.Create(width, height, RHI.RhiFormat.R16G16B16A16_SFloat);
            ResolvedSceneColor.SetFiltering(TextureMinFilter.Linear, TextureMagFilter.Linear);
            ResolvedSceneColor.SetWrapMode(TextureWrapMode.ClampToEdge);

            ResolvedSceneDepth!.Delete();
            ResolvedSceneDepth = RenderTexture.Create(width, height, RHI.RhiFormat.R32_SFloat);

            Textures.RemoveAll(static t => t.Slot == ReservedTextureSlots.SceneColor || t.Slot == ReservedTextureSlots.SceneDepth);
            Textures.Add(new(ReservedTextureSlots.SceneColor, "g_tSceneColor", ResolvedSceneColor));
            Textures.Add(new(ReservedTextureSlots.SceneDepth, "g_tSceneDepth", ResolvedSceneDepth));
        }
    }

    /// <summary>
    /// Resolves MSAA and copies color and/or depth from the framebuffer into <see cref="ResolvedSceneColor"/> and <see cref="ResolvedSceneDepth"/>.
    /// </summary>
    /// <param name="framebuffer">The multisampled framebuffer to resolve.</param>
    /// <param name="copyColor">Whether to resolve colour.</param>
    /// <param name="copyDepth">Whether to resolve depth.</param>
    /// <param name="commandList">The list to record into, or <see langword="null"/> to run through OpenGL.
    /// The resolve dispatches compute, so no render pass may be open on it.</param>
    public void GrabFramebufferCopy(Framebuffer framebuffer, bool copyColor, bool copyDepth,
        RHI.ICommandList? commandList = null)
    {
        if (!copyColor && !copyDepth)
        {
            return;
        }

        using var _ = new GLDebugGroup("Framebuffer Copy");

        EnsureResolvedTextureSize(framebuffer.Width, framebuffer.Height);

        Postprocess.ResolveMsaa(framebuffer, ResolvedSceneColor!, ResolvedSceneDepth!, copyColor, copyDepth, commandList);

        framebuffer.Bind(FramebufferTarget.Framebuffer);
    }

    /// <summary>
    /// Multisampling resolve, postprocess the image, and convert to gamma.
    /// </summary>
    /// <param name="inputFramebuffer">The multisampled scene framebuffer to read.</param>
    /// <param name="outputFramebuffer">Where the tonemapped image is written.</param>
    /// <param name="flipY">Whether the image is flipped vertically on the way out.</param>
    /// <remarks>
    /// <para>
    /// This takes a command list of its own rather than the scene's. Post-processing runs after
    /// <see cref="Render(Scene.RenderContext)"/> has already submitted, and on the OpenGL backend
    /// acquiring a list is what rewinds it, so reusing the scene's would replay into a list whose work
    /// has been handed over.
    /// </para>
    /// <para>
    /// The chain only records when <paramref name="outputFramebuffer"/> is texture backed. A render pass
    /// can never name the presented surface &#8212; framebuffer 0 has no texture handle for an attachment
    /// to name &#8212; so a viewer that tonemaps straight onto the screen keeps the OpenGL path until the
    /// presentation layer splits that draw in two: the chain into an offscreen colour target, and a
    /// backend-specific blit from it to the screen.
    /// </para>
    /// </remarks>
    public void PostprocessRender(Framebuffer inputFramebuffer, Framebuffer outputFramebuffer, bool flipY = false)
    {
        using var _ = new GLDebugGroup("Post Processing");

        inputFramebuffer.Bind(FramebufferTarget.ReadFramebuffer);
        outputFramebuffer.Bind(FramebufferTarget.DrawFramebuffer);

        Debug.Assert(inputFramebuffer.NumSamples > 0);
        Debug.Assert(outputFramebuffer.NumSamples == 0);

        EnsureResolvedTextureSize(inputFramebuffer.Width, inputFramebuffer.Height);

        var commandList = outputFramebuffer.Color is null ? null : AcquireCommandList("Post Process");

        Postprocess.Render(inputFramebuffer, outputFramebuffer, ResolvedSceneColor!, Camera, flipY, commandList);

        if (commandList is not null)
        {
            RendererContext.Device?.Submit(commandList);
        }
    }

    /// <summary>
    /// Gets or sets whether the vsnd name of every active positioned sound is billboarded in the world.
    /// </summary>
    public bool ShowSoundDebug { get; set; }

    // Reused buffers for the sound debug billboards and 2D (non-positioned) sound list
    private readonly List<(Vector3 Position, string Text)> debugWorldSounds = [];
    private readonly List<string> debugFlatSounds = [];

    /// <summary>
    /// Releases GPU resources owned by this renderer.
    /// </summary>
    public void Dispose()
    {
        ViewBuffer?.Dispose();
        Scene?.Dispose();
        SkyboxScene?.Dispose();
        PerfStats?.Dispose();
        ResolvedSceneColor?.Delete();
        ResolvedSceneDepth?.Delete();
        Skybox2D?.Delete();

        if (BaseBackground != Skybox2D && BaseBackground != null)
        {
            BaseBackground.Delete();
        }
    }

    /// <summary>
    /// Advances the simulation, updates scene draw calls, and prepares shadow data for the next frame.
    /// </summary>
    public void Update(Scene.UpdateContext updateContext)
    {
        if (ViewBuffer is null || ShadowDepthBuffer is null)
        {
            throw new InvalidOperationException("Initialize() must be called before updating");
        }

        Uptime += updateContext.Timestep;
        DeltaTime = updateContext.Timestep;
        ViewBuffer.Data.Time = Uptime;

        updateContext = updateContext with { Uptime = Uptime };

        Camera.RecalculateMatrices();

        Scene.Update(updateContext);
        SkyboxScene?.Update(updateContext);

        Scene.PostProcessInfo.UpdatePostProcessing(updateContext.Camera, updateContext.Timestep);

        Scene.SetupSceneShadows(updateContext.Camera, DisableAllCulling ? -1 : ShadowDepthBuffer.Width);

        if (ViewBuffer.Data.ExperimentalLightsEnabled)
        {
            Scene.LightingInfo.BinBarnLights(Camera, ShadowTextureSize);
        }

        if (!DisableAllCulling && Scene is { EnablePvsCulling: true, VoxelVisibility: not null })
        {
            var pvsPosition = LockedCullPosition ?? updateContext.Camera.Location;
            Scene.CurrentFramePvs = Scene.VoxelVisibility.GetPVSForPoint(pvsPosition);
        }
        else
        {
            Scene.CurrentFramePvs = null;
        }

        var cullFrustum = CullFrustum;
        Scene.CollectSceneDrawCalls(updateContext.Camera, cullFrustum);
        SkyboxScene?.CollectSceneDrawCalls(updateContext.Camera, cullFrustum);

        if (ShowSoundDebug && Sound.Player != null)
        {
            CollectSoundDebugText(updateContext);
        }
    }

    /// <summary>
    /// Queues a billboard per audible positioned sound, and a bottom-right corner list of the
    /// non-positioned (2D) ones.
    /// </summary>
    private void CollectSoundDebugText(Scene.UpdateContext updateContext)
    {
        debugWorldSounds.Clear();
        debugFlatSounds.Clear();
        Sound.Player!.CollectDebugSounds(debugWorldSounds, debugFlatSounds);

        foreach (var (position, text) in debugWorldSounds)
        {
            updateContext.TextRenderer.AddTextBillboard(position, new TextRenderer.TextRenderRequest
            {
                Scale = 8f,
                Text = text,
                CenterHorizontal = true,
                Color = new Color32(0.4f, 1f, 0.4f, 1f),
            }, updateContext.Camera);
        }

        if (debugFlatSounds.Count == 0)
        {
            return;
        }

        const float scale = 10f;
        const float lineHeight = scale * 1.5f;
        const float marginRight = 8f;
        const float marginBottom = 8f;

        // Right edge every line is aligned to, so the ".vsnd" suffix lines up flush against the screen corner.
        var cornerX = updateContext.Camera.WindowSize.X - marginRight;
        var y = updateContext.Camera.WindowSize.Y - marginBottom - (debugFlatSounds.Count * lineHeight);

        foreach (var text in debugFlatSounds)
        {
            updateContext.TextRenderer.AddText(new TextRenderer.TextRenderRequest
            {
                X = cornerX - TextRenderer.MeasureTextWidth(text, scale),
                Y = y,
                Scale = scale,
                Text = text,
                Color = new Color32(0.4f, 1f, 1f, 1f),
            });

            y += lineHeight;
        }
    }

    void EnsureDepthPyramidSize(int width, int height)
    {
        // Get the target pyramid size
        var maxDim = Math.Max(width, height);
        var cappedDim = Math.Min(maxDim, 256);
        var targetSize = 1 << (int)Math.Floor(Math.Log2(cappedDim));

        if (Scene.DepthPyramid != null && Scene.DepthPyramid.Width == targetSize && Scene.DepthPyramid.Height == targetSize)
        {
            return;
        }

        Scene.DepthPyramid?.Delete();

        // Calculate mips needed to go from targetSize down to 1x1
        var maxMipLevel = (int)Math.Log2(targetSize);

        Scene.DepthPyramid = RenderTexture.Create(targetSize, targetSize, SizedInternalFormat.R32f, maxMipLevel + 1);
        Scene.DepthPyramid.SetLabel("DepthPyramid");

        Scene.DepthPyramid.SetBaseMaxLevel(0, maxMipLevel);
    }
}
