using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using GUI.Utils;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Materials;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.Renderer.Shaders;
using ValveResourceFormat.Renderer.World;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Utils;

namespace GUI.Types.GLViewers
{
    /// <summary>
    /// World viewer that draws an older build of the same map through a second renderer from the same camera,
    /// and composites the two to show what changed.
    /// </summary>
    class GLWorldDiffViewer : GLWorldViewer
    {
        /// <summary>How the two builds are shown. The values match the modes in map_diff.frag.slang.</summary>
        private enum DiffViewMode
        {
            Differences,
            NewBuild,
            OldBuild,
            Split,
            Heatmap,
        }

        private readonly World oldWorld;
        private readonly ResourceExtRefList? oldExternalReferences;

        private readonly ValveResourceFormat.Renderer.Renderer OldRenderer;

        private Framebuffer? oldFramebuffer;
        private Framebuffer? newTonemapped;
        private Framebuffer? oldTonemapped;
        private Shader? compositeShader;

        private DiffViewMode viewMode = DiffViewMode.Differences;
        private bool showOldWhileHeld;
        private float splitPosition = 0.5f;
        private float depthTolerance = 2f;
        private float colorThreshold = 0.1f;
        private float unchangedDim = 0.6f;
        private float uptimeBeforeUpdate;
        private bool freezeFoliage = true;
        private float frozenShaderTime;
        private ComboBox? viewModeComboBox;

        public GLWorldDiffViewer(VrfGuiContext vrfGuiContext, RendererContext rendererContext, World world, ResourceExtRefList? externalReferences,
            RendererContext oldRendererContext, World oldWorld, ResourceExtRefList? oldExternalReferences)
            : base(vrfGuiContext, rendererContext, world, externalReferences)
        {
            this.oldWorld = oldWorld;
            this.oldExternalReferences = oldExternalReferences;

            OldRenderer = new(oldRendererContext)
            {
                Camera = Renderer.Camera,
            };
        }

        public override void Dispose()
        {
            // Delete GL resources before the base disposes the GL context
            OldRenderer.Dispose();
            oldFramebuffer?.Delete();
            newTonemapped?.Delete();
            oldTonemapped?.Delete();

            base.Dispose();

            viewModeComboBox?.Dispose();
        }

        private DiffViewMode ViewMode
        {
            get => viewMode;
            set
            {
                viewMode = value;

                if (viewModeComboBox != null && viewModeComboBox.SelectedIndex != (int)value)
                {
                    viewModeComboBox.SelectedIndex = (int)value;
                }
            }
        }

        private DiffViewMode EffectiveViewMode => showOldWhileHeld ? DiffViewMode.OldBuild : viewMode;

        protected override bool RequiresSceneDepth => EffectiveViewMode is DiffViewMode.Differences or DiffViewMode.Heatmap;

        public override void PreSceneLoad()
        {
            base.PreSceneLoad();

            Debug.Assert(MainFramebuffer != null);

            OldRenderer.Postprocess.Load(NumSamples);
            OldRenderer.Postprocess.FullScreenGamma = Renderer.Postprocess.FullScreenGamma;
            OldRenderer.Postprocess.ExposureCompensation = Renderer.Postprocess.ExposureCompensation;

            OldRenderer.ShadowTextureSize = Renderer.ShadowTextureSize;
            OldRenderer.Initialize();

            oldFramebuffer = Framebuffer.Prepare(nameof(oldFramebuffer), MainFramebuffer.Width, MainFramebuffer.Height, NumSamples,
                MainFramebuffer.ColorFormat, MainFramebuffer.DepthFormat);
            oldFramebuffer.Initialize();
            OldRenderer.MainFramebuffer = oldFramebuffer;

            newTonemapped = Framebuffer.Prepare(nameof(newTonemapped), MainFramebuffer.Width, MainFramebuffer.Height, 0, ImageFormat.RGBA8888, null);
            newTonemapped.Initialize();

            oldTonemapped = Framebuffer.Prepare(nameof(oldTonemapped), MainFramebuffer.Width, MainFramebuffer.Height, 0, ImageFormat.RGBA8888, null);
            oldTonemapped.Initialize();

            compositeShader = Scene.RendererContext.ShaderLoader.LoadShader("map_diff");

            OldRenderer.LoadRendererResources();
        }

        protected override void LoadScene()
        {
            base.LoadScene();

            ReportLoadingStatus("Loading the old build…");

            var oldLoadedWorld = new WorldLoader(oldWorld, OldRenderer.Scene, OldRenderer.EntitySystem)
            {
                LoadingProgress = GuiContext.LoadingProgress,
            };

            oldLoadedWorld.Load(oldExternalReferences);

            foreach (var spawnGroup in oldLoadedWorld.SpawnGroups)
            {
                OldRenderer.AddSpawnGroup(spawnGroup);
            }
        }

        public override void PostSceneLoad()
        {
            base.PostSceneLoad();

            foreach (var scene in OldRenderer.Scenes)
            {
                scene.Initialize();
            }

            var oldScene = OldRenderer.Scene;

            if (oldScene.FogInfo.CubeFogActive && oldScene.FogInfo.CubemapFog?.CubemapFogTexture is { } cubemapTexture)
            {
                OldRenderer.Textures.RemoveAll(t => t.Slot == ReservedTextureSlots.FogCubeTexture);
                OldRenderer.Textures.Add(new(ReservedTextureSlots.FogCubeTexture, "g_tFogCubeTexture", cubemapTexture));
            }
        }

        // The base prewarm paints a frame, which draws the old build too
        protected override void PrewarmRenderer()
        {
            OldRenderer.RendererContext.ShaderLoader.LinkLoadedShaders();
            OldRenderer.DisableAllCulling = true;
            OldRenderer.Prewarming = true;

            try
            {
                base.PrewarmRenderer();
            }
            finally
            {
                OldRenderer.DisableAllCulling = false;
                OldRenderer.Prewarming = false;
            }
        }

        protected override void OnResize(int w, int h)
        {
            base.OnResize(w, h);

            if (w <= 0 || h <= 0)
            {
                return;
            }

            oldFramebuffer?.Resize(w, h, NumSamples);
            newTonemapped?.Resize(w, h);
            oldTonemapped?.Resize(w, h);
        }

        protected override void OnPaint(float frameTime)
        {
            uptimeBeforeUpdate = Renderer.Uptime;
            Renderer.ShaderTimeOverride = freezeFoliage ? frozenShaderTime : null;

            base.OnPaint(frameTime);
        }

        protected override IEnumerable<ValveResourceFormat.Renderer.Renderer> Renderers => [Renderer, OldRenderer];

        /// <summary>Carries the settings the sidebar changes on the new build over to the old one.</summary>
        private void SyncOldRendererSettings()
        {
            OldRenderer.IsWireframe = Renderer.IsWireframe;
            OldRenderer.ShowSkybox = Renderer.ShowSkybox;
            OldRenderer.EnableBarnLights = Renderer.EnableBarnLights;
            OldRenderer.LockedCullFrustum = Renderer.LockedCullFrustum;
            OldRenderer.LockedCullPosition = Renderer.LockedCullPosition;
            OldRenderer.EntitySystem.Enabled = Renderer.EntitySystem.Enabled;
            OldRenderer.Postprocess.ColorCorrectionEnabled = Renderer.Postprocess.ColorCorrectionEnabled;
            // Each build adapting its own exposure would change the brightness of every pixel
            OldRenderer.Postprocess.CustomExposure = Renderer.Postprocess.TonemapScalar;
            OldRenderer.ForceResolveSceneDepth = RequiresSceneDepth;

            foreach (var scene in OldRenderer.Scenes)
            {
                scene.FogEnabled = Scene.FogEnabled;
                scene.ShowToolsMaterials = Scene.ShowToolsMaterials;
                scene.EnablePvsCulling = Scene.EnablePvsCulling;
                scene.EnableIndirectDraws = Scene.EnableIndirectDraws;
                scene.EnableOcclusionCulling = Scene.EnableOcclusionCulling;
                scene.EnableDepthPrepass = Scene.EnableDepthPrepass;
                scene.EnableTiledLightCulling = Scene.EnableTiledLightCulling;
            }
        }

        private void RenderOldBuild(float frameTime)
        {
            Debug.Assert(oldFramebuffer != null);

            SyncOldRendererSettings();

            // Started from the same uptime and advanced by the same step, the two builds end up at bit identical
            // times, so whatever shaders animate by time, like foliage in the wind, moves the same in both. The old
            // build also skips frames while only the new one is shown, and would fall behind otherwise.
            OldRenderer.Uptime = uptimeBeforeUpdate;
            OldRenderer.ShaderTimeOverride = Renderer.ShaderTimeOverride;

            using (new GLDebugGroup("Old Build Update"))
            {
                OldRenderer.Update(new Scene.UpdateContext
                {
                    TextRenderer = TextRenderer,
                    Timestep = frameTime,
                    Camera = Renderer.Camera,
                });
            }

            using (new GLDebugGroup("Old Build Render"))
            {
                OldRenderer.Render(oldFramebuffer);
            }
        }

        protected override void BlitFramebufferToScreen()
        {
            Debug.Assert(MainFramebuffer != null && GLDefaultFramebuffer != null);
            Debug.Assert(oldFramebuffer != null && newTonemapped != null && oldTonemapped != null && compositeShader != null);

            var mode = EffectiveViewMode;

            if (mode == DiffViewMode.NewBuild)
            {
                base.BlitFramebufferToScreen();
                return;
            }

            RenderOldBuild(Renderer.DeltaTime);

            if (mode == DiffViewMode.OldBuild)
            {
                OldRenderer.PostprocessRender(oldFramebuffer, GLDefaultFramebuffer);
                return;
            }

            Renderer.PostprocessRender(MainFramebuffer, newTonemapped);
            OldRenderer.PostprocessRender(oldFramebuffer, oldTonemapped);

            using var _ = new GLDebugGroup("Map Diff Composite");

            GLDefaultFramebuffer.Bind(FramebufferTarget.Framebuffer);
            GL.Viewport(0, 0, GLDefaultFramebuffer.Width, GLDefaultFramebuffer.Height);

            compositeShader.SetUniform("g_nMode", (int)mode);
            compositeShader.SetUniform("g_flSplitX", splitPosition * GLDefaultFramebuffer.Width);
            compositeShader.SetUniform("g_flDepthTolerance", depthTolerance);
            compositeShader.SetUniform("g_flColorThreshold", colorThreshold);
            compositeShader.SetUniform("g_flUnchangedDim", unchangedDim);

            compositeShader.Use();
            compositeShader.SetTexture(0, "g_tNewColor", newTonemapped.Color);
            compositeShader.SetTexture(1, "g_tOldColor", oldTonemapped.Color);
            compositeShader.SetTexture(2, "g_tNewDepth", Renderer.ResolvedSceneDepth);
            compositeShader.SetTexture(3, "g_tOldDepth", OldRenderer.ResolvedSceneDepth);

            using var state = GraphicsContext.RenderState.Scope(depthTest: false, depthWrite: false, blend: false);

            GL.BindVertexArray(Scene.RendererContext.MeshBufferCache.EmptyVAO);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }

        protected override void OnKeyDown(Keys keyData)
        {
            if (keyData == Keys.B)
            {
                showOldWhileHeld = true;
            }
            else if (keyData == Keys.M)
            {
                ViewMode = (DiffViewMode)(((int)viewMode + 1) % Enum.GetValues<DiffViewMode>().Length);
            }

            base.OnKeyDown(keyData);
        }

        protected override void OnKeyUp(Keys keyCode)
        {
            if (keyCode == Keys.B)
            {
                showOldWhileHeld = false;
            }

            base.OnKeyUp(keyCode);
        }

        protected override void AddUiControls()
        {
            Debug.Assert(UiControl != null);

            using (UiControl.BeginGroup("Compare"))
            {
                viewModeComboBox = UiControl.AddSelection("View", (_, i) => viewMode = (DiffViewMode)i);
                viewModeComboBox.Items.AddRange(["Differences", "New build", "Old build", "Split", "Heatmap"]);
                viewModeComboBox.SelectedIndex = (int)viewMode;

                UiControl.AddControl(new Label
                {
                    Text = "Hold B for the old build, M cycles the view.",
                    MaximumSize = new System.Drawing.Size(UiControl.AdjustForDPI(200), 0),
                    AutoSize = true,
                });

                AddSlider("Split", splitPosition, v => splitPosition = v, v => $"{v:P0}");
                AddSlider("Depth tolerance", depthTolerance / 16f, v => depthTolerance = v * 16f, _ => $"{depthTolerance:0.0} units");
                AddSlider("Color threshold", colorThreshold / 0.5f, v => colorThreshold = v * 0.5f, _ => $"{colorThreshold:0.00}");
                AddSlider("Dim unchanged", unchangedDim, v => unchangedDim = v, v => $"{v:P0}");

                UiControl.AddCheckBox("Freeze foliage sway", freezeFoliage, v =>
                {
                    freezeFoliage = v;
                    frozenShaderTime = Renderer.Uptime;
                });
            }

            base.AddUiControls();
        }

        private void AddSlider(string name, float initial, Action<float> onChange, Func<float, string> format)
        {
            Debug.Assert(UiControl != null);

            var label = new Label { AutoSize = true };

            void Update(float value)
            {
                onChange(value);
                label.Text = $"{name}: {format(value)}";
            }

            UiControl.AddControl(label);
            UiControl.AddTrackBar(Update, initial);
            Update(initial);
        }
    }
}
