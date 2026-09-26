using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Utils;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Materials;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.Renderer.Shaders;
using ValveResourceFormat.Renderer.World;
using ValveResourceFormat.Renderer.World.Diff;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
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
        public enum DiffViewMode
        {
            Differences,
            NewBuild,
            OldBuild,
            Split,
            Heatmap,
        }

        private readonly World oldWorld;
        private readonly ResourceExtRefList? oldExternalReferences;

        public ValveResourceFormat.Renderer.Renderer OldRenderer { get; }
        public WorldLoader? OldLoadedWorld { get; private set; }

        private Framebuffer? oldFramebuffer;
        private Framebuffer? newTonemapped;
        private Framebuffer? oldTonemapped;
        private Shader? compositeShader;

        private DiffViewMode viewMode = DiffViewMode.Differences;

        /// <summary>The builds held to peek at, in the order their keys went down, so the last one pressed shows.</summary>
        private readonly List<DiffViewMode> peekedBuilds = [];
        private bool matchExposure = true;
        private float splitPosition = 0.5f;
        private float depthTolerance = 2f;
        private float colorThreshold = 0.1f;
        private float unchangedDim = 0.6f;
        private float lastFrameTime;
        private float uptimeBeforeUpdate;
        private bool freezeFoliage = true;
        private float frozenShaderTime;
        private ComboBox? viewModeComboBox;

        /// <summary>Gets every difference between the two builds, once loaded.</summary>
        public MapDiffResult? Diff { get; private set; }

        private MapDiffListControl? diffList;
        private ChangeHighlightRenderer? highlightRenderer;
        private MapDiffEntry? focusedEntry;
        private bool showFocusedChange = true;

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
            highlightRenderer?.Delete();
            OldRenderer.Dispose();
            oldFramebuffer?.Delete();
            newTonemapped?.Delete();
            oldTonemapped?.Delete();

            base.Dispose();

            viewModeComboBox?.Dispose();
            diffList?.Dispose();
        }

        public DiffViewMode ViewMode
        {
            get => viewMode;
            set
            {
                viewMode = value;
                NotifyViewModeChanged();

                if (viewModeComboBox != null && viewModeComboBox.SelectedIndex != (int)value)
                {
                    viewModeComboBox.SelectedIndex = (int)value;
                }
            }
        }

        /// <summary>Gets the view being shown, which is a build while its peek key (Q or E) is held.</summary>
        public DiffViewMode EffectiveViewMode => peekedBuilds.Count > 0 ? peekedBuilds[^1] : viewMode;

        private DiffViewMode? shownViewMode;

        /// <summary>Refreshes the keybinding bar, which marks the key of the view being shown.</summary>
        private void NotifyViewModeChanged()
        {
            // Holding a peek key repeats the key down, only redraw the bar when the view really changes
            if (shownViewMode == EffectiveViewMode)
            {
                return;
            }

            shownViewMode = EffectiveViewMode;
            Program.MainForm.ShowSelectedTabKeybindings();
        }

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
            highlightRenderer = new ChangeHighlightRenderer(Scene.RendererContext);

            OldRenderer.LoadRendererResources();
        }

        protected override void LoadScene()
        {
            base.LoadScene();

            ReportLoadingStatus("Loading the old build…");

            OldLoadedWorld = new WorldLoader(oldWorld, OldRenderer.Scene, OldRenderer.EntitySystem)
            {
                LoadingProgress = GuiContext.LoadingProgress,
            };

            OldLoadedWorld.Load(oldExternalReferences);

            foreach (var spawnGroup in OldLoadedWorld.SpawnGroups)
            {
                OldRenderer.AddSpawnGroup(spawnGroup);
            }

            Debug.Assert(LoadedWorld != null);

            ReportLoadingStatus("Comparing the builds…");

            // Read through the same loaders the scenes were loaded with, so the resources are already cached
            var options = new MapDiffOptions();
            var oldSource = MapDiffSource.Load(OldRenderer.RendererContext.FileLoader, oldWorld, options);
            var newSource = MapDiffSource.Load(Scene.RendererContext.FileLoader, LoadedWorld.World, options);

            Diff = MapDiffer.Compute(oldSource, newSource, options);
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

        protected override void PrewarmRenderer()
        {
            base.PrewarmRenderer();

            Debug.Assert(oldFramebuffer != null);

            OldRenderer.RendererContext.ShaderLoader.LinkLoadedShaders();
            OldRenderer.DisableAllCulling = true;
            OldRenderer.Prewarming = true;

            try
            {
                RenderOldBuild(1f / 60f);
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
            lastFrameTime = frameTime;
            uptimeBeforeUpdate = Renderer.Uptime;
            Renderer.ShaderTimeOverride = freezeFoliage ? frozenShaderTime : null;

            base.OnPaint(frameTime);
        }

        protected override void SetRenderMode(string renderMode)
        {
            base.SetRenderMode(renderMode);

            if (OldRenderer.ViewBuffer == null)
            {
                return;
            }

            OldRenderer.ViewBuffer.Data.RenderMode = Renderer.ViewBuffer!.Data.RenderMode;
            OldRenderer.Postprocess.Enabled = Renderer.Postprocess.Enabled;

            foreach (var scene in OldRenderer.Scenes)
            {
                scene.EnableCompaction = renderMode != "Meshlets";

                foreach (var node in scene.AllNodes)
                {
                    node.SetRenderMode(renderMode);
                }
            }
        }

        // A layer or collision group that only the old build has must still be listed, or it could not be shown
        protected override IEnumerable<SceneNode> NodesForLayerLists => base.NodesForLayerLists.Concat(OldRenderer.Scene.AllNodes);

        protected override void ShowPhysicsGroups(HashSet<string> physicsGroups, bool renderTranslucent)
        {
            base.ShowPhysicsGroups(physicsGroups, renderTranslucent);
            ShowPhysicsGroups(OldRenderer, physicsGroups, renderTranslucent);
        }

        protected override void SetEnabledLayers(HashSet<string> layers)
        {
            base.SetEnabledLayers(layers);

            foreach (var scene in OldRenderer.Scenes)
            {
                scene.SetEnabledLayers(layers);
            }
        }

        /// <summary>Carries the settings the sidebar changes on the new build over to the old one.</summary>
        private void SyncOldRendererSettings()
        {
            OldRenderer.Camera = Renderer.Camera;
            OldRenderer.IsWireframe = Renderer.IsWireframe;
            OldRenderer.ShowSkybox = Renderer.ShowSkybox;
            OldRenderer.EnableBarnLights = Renderer.EnableBarnLights;
            OldRenderer.LockedCullFrustum = Renderer.LockedCullFrustum;
            OldRenderer.LockedCullPosition = Renderer.LockedCullPosition;
            OldRenderer.EntitySystem.Enabled = Renderer.EntitySystem.Enabled;
            OldRenderer.Postprocess.ColorCorrectionEnabled = Renderer.Postprocess.ColorCorrectionEnabled;
            OldRenderer.Postprocess.CustomExposure = matchExposure ? Renderer.Postprocess.TonemapScalar : Renderer.Postprocess.CustomExposure;
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

        /// <summary>Boxes the selected difference, over everything.</summary>
        private void RenderHighlight(Framebuffer framebuffer)
        {
            if (!showFocusedChange || highlightRenderer == null)
            {
                return;
            }

            framebuffer.Bind(FramebufferTarget.Framebuffer);
            GL.Viewport(0, 0, framebuffer.Width, framebuffer.Height);
            highlightRenderer.Render(focusedEntry);
        }

        protected override void BlitFramebufferToScreen()
        {
            Debug.Assert(MainFramebuffer != null && GLDefaultFramebuffer != null);
            Debug.Assert(oldFramebuffer != null && newTonemapped != null && oldTonemapped != null && compositeShader != null);

            var mode = EffectiveViewMode;

            if (peekedBuilds.Count > 0)
            {
                // Peeking looks the same as either build's own view, so say which one it is
                TextRenderer.AddText(new ValveResourceFormat.Renderer.TextRenderer.TextRenderRequest
                {
                    X = 8f,
                    Y = 28f,
                    Scale = 22f,
                    Color = mode == DiffViewMode.OldBuild ? ChangeHighlightRenderer.OldColor : ChangeHighlightRenderer.NewColor,
                    Text = mode == DiffViewMode.OldBuild ? "OLD BUILD" : "NEW BUILD",
                });
            }

            if (mode == DiffViewMode.NewBuild)
            {
                base.BlitFramebufferToScreen();
                RenderHighlight(GLDefaultFramebuffer);
                return;
            }

            RenderOldBuild(lastFrameTime);

            if (mode == DiffViewMode.OldBuild)
            {
                OldRenderer.PostprocessRender(oldFramebuffer, GLDefaultFramebuffer);
                RenderHighlight(GLDefaultFramebuffer);
                return;
            }

            Renderer.PostprocessRender(MainFramebuffer, newTonemapped);
            OldRenderer.PostprocessRender(oldFramebuffer, oldTonemapped);

            using var _ = new GLDebugGroup("Map Diff Composite");

            GLDefaultFramebuffer.Bind(FramebufferTarget.Framebuffer);
            GL.Viewport(0, 0, GLDefaultFramebuffer.Width, GLDefaultFramebuffer.Height);

            if (!Matrix4x4.Invert(Renderer.Camera.ProjectionMatrix, out var projectionToView))
            {
                return;
            }

            compositeShader.SetUniform("g_nMode", (int)mode);
            compositeShader.SetUniform("g_flSplitX", splitPosition * GLDefaultFramebuffer.Width);
            compositeShader.SetUniform("g_flDepthTolerance", depthTolerance);
            compositeShader.SetUniform("g_flColorThreshold", colorThreshold);
            compositeShader.SetUniform("g_flUnchangedDim", unchangedDim);
            compositeShader.SetUniform("g_vInvProjRow3", new Vector4(projectionToView.M14, projectionToView.M24, projectionToView.M34, projectionToView.M44));
            compositeShader.SetUniform("g_vViewportZRange", new Vector2(ValveResourceFormat.Renderer.Renderer.DepthRange.Scene.Near, ValveResourceFormat.Renderer.Renderer.DepthRange.Scene.Far));

            compositeShader.Use();
            compositeShader.SetTexture(0, "g_tNewColor", newTonemapped.Color);
            compositeShader.SetTexture(1, "g_tOldColor", oldTonemapped.Color);
            compositeShader.SetTexture(2, "g_tNewDepth", Renderer.ResolvedSceneDepth);
            compositeShader.SetTexture(3, "g_tOldDepth", OldRenderer.ResolvedSceneDepth);

            using var state = GraphicsContext.RenderState.Scope(depthTest: false, depthWrite: false, blend: false);

            GL.BindVertexArray(Scene.RendererContext.MeshBufferCache.EmptyVAO);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

            // Drawn over the composite, where it would otherwise count as unchanged and be dimmed
            RenderHighlight(GLDefaultFramebuffer);
        }

        protected override void OnKeyDown(Keys keyData)
        {
            if (PeekedBuild(keyData) is { } build)
            {
                // Key repeat moves it to the end as well, which keeps the build pressed last on top
                peekedBuilds.Remove(build);
                peekedBuilds.Add(build);
                NotifyViewModeChanged();

                // Q would also fly the camera up, moving it away from what is being compared
                return;
            }
            else if (keyData == Keys.M)
            {
                ViewMode = (DiffViewMode)(((int)viewMode + 1) % Enum.GetValues<DiffViewMode>().Length);
            }
            else if (keyData == Keys.F1)
            {
                ViewMode = DiffViewMode.NewBuild;
            }
            else if (keyData == Keys.F2)
            {
                ViewMode = DiffViewMode.OldBuild;
            }
            else if (keyData == Keys.F3)
            {
                ViewMode = DiffViewMode.Differences;
            }
            else if (keyData == Keys.F4)
            {
                ViewMode = DiffViewMode.Heatmap;
            }
            else if (keyData == Keys.N)
            {
                diffList?.SelectRelative(1);
            }
            else if (keyData == (Keys.Shift | Keys.N))
            {
                diffList?.SelectRelative(-1);
            }

            base.OnKeyDown(keyData);
        }

        protected override void OnKeyUp(Keys keyCode)
        {
            if (PeekedBuild(keyCode) is { } build)
            {
                peekedBuilds.Remove(build);
                NotifyViewModeChanged();
            }

            base.OnKeyUp(keyCode);
        }

        private static DiffViewMode? PeekedBuild(Keys key) => key switch
        {
            Keys.Q => DiffViewMode.OldBuild,
            Keys.E => DiffViewMode.NewBuild,
            _ => null,
        };

        private void ReleasePeekedBuilds()
        {
            // Key ups are not seen once focus moves elsewhere
            peekedBuilds.Clear();
            NotifyViewModeChanged();
        }

        protected override void AddUiControls()
        {
            Debug.Assert(UiControl != null);

            GLControl?.LostFocus += (_, _) => ReleasePeekedBuilds();

            using (UiControl.BeginGroup("Compare"))
            {
                viewModeComboBox = UiControl.AddSelection("View", (_, i) => ViewMode = (DiffViewMode)i);
                viewModeComboBox.Items.AddRange(["Differences", "New build", "Old build", "Split", "Heatmap"]);
                viewModeComboBox.SelectedIndex = (int)viewMode;

                UiControl.AddControl(new Label
                {
                    Text = "Hold Q for the old build or E for the new build, and let go to return to the view. M cycles the view, N and Shift+N step through the changes.",
                    MaximumSize = new System.Drawing.Size(UiControl.AdjustForDPI(200), 0),
                    AutoSize = true,
                });

                AddSlider("Split", splitPosition, v => splitPosition = v, v => $"{v:P0}");
                AddSlider("Depth tolerance", depthTolerance / 16f, v => depthTolerance = v * 16f, _ => $"{depthTolerance:0.0} units");
                AddSlider("Color threshold", colorThreshold / 0.5f, v => colorThreshold = v * 0.5f, _ => $"{colorThreshold:0.00}");
                AddSlider("Dim unchanged", unchangedDim, v => unchangedDim = v, v => $"{v:P0}");

                UiControl.AddCheckBox("Match exposure", matchExposure, v => matchExposure = v);
                UiControl.AddCheckBox("Ignore lighting", false, v => SelectRenderMode(v ? "Color" : "Default"));
                UiControl.AddCheckBox("Freeze foliage sway", freezeFoliage, v =>
                {
                    freezeFoliage = v;
                    frozenShaderTime = Renderer.Uptime;
                });
                UiControl.AddCheckBox("Box the selected change", showFocusedChange, v => showFocusedChange = v);
            }

            base.AddUiControls();

            if (Diff == null)
            {
                return;
            }

            var oldName = Path.GetFileNameWithoutExtension((OldRenderer.RendererContext.FileLoader as VrfGuiContext)?.FileName) ?? "old";
            var newName = Path.GetFileNameWithoutExtension(GuiContext.FileName);

            diffList = new MapDiffListControl(Diff, oldName, newName)
            {
                Dock = DockStyle.Right,
                Width = UiControl.AdjustForDPI(480),
            };
            diffList.EntrySelected += (_, entry) => FocusDiffEntry(entry);

            // Docked right of the view rather than in the sidebar, so the list and the world are seen together
            var container = UiControl.GLControlContainer;
            container.Controls.Add(diffList);
            container.Controls.Add(new Splitter { Dock = DockStyle.Right, Width = UiControl.AdjustForDPI(4) });
            GLControl?.BringToFront();
        }

        /// <summary>Flies the camera to a difference and selects what it is about, in whichever build has it.</summary>
        public void FocusDiffEntry(MapDiffEntry entry)
        {
            Debug.Assert(SelectedNodeRenderer != null);

            focusedEntry = entry;

            if (entry.NewEntity != null && LoadedWorld != null
                && FindLoadedEntity(LoadedWorld.Entities, entry.NewEntity) is { } entity && Renderer.FindNode(entity) is { } node)
            {
                SelectAndFocusNode(node);
                return;
            }

            SelectedNodeRenderer.SelectNode(null);

            var bounds = entry.Bounds;

            // Only the old build has the entity, so its node there is the one to frame
            if (entry.NewEntity == null && entry.OldEntity != null && OldLoadedWorld != null
                && FindLoadedEntity(OldLoadedWorld.Entities, entry.OldEntity) is { } oldEntity && OldRenderer.FindNode(oldEntity) is { } oldNode
                && oldNode.BoundingBox.Size.MaxComponent() >= 1f)
            {
                bounds = oldNode.BoundingBox;
            }

            FocusCameraOnBounds(bounds);
        }

        /// <summary>
        /// Finds the entity a scene was loaded from that a compared entity was read from. The comparison reads the
        /// entity lumps again, so they are equal but not the same objects.
        /// </summary>
        private static EntityLump.Entity? FindLoadedEntity(List<EntityLump.Entity> loaded, EntityLump.Entity compared)
        {
            var id = compared.GetStringProperty("hammeruniqueid");
            var classname = compared.GetStringProperty("classname");
            var origin = compared.GetVector3Property("origin");

            EntityLump.Entity? best = null;
            var bestDistance = float.MaxValue;
            var bestMatchesId = false;

            foreach (var entity in loaded)
            {
                if (entity.GetStringProperty("classname") != classname)
                {
                    continue;
                }

                var matchesId = id != null && entity.GetStringProperty("hammeruniqueid") == id;

                if (bestMatchesId && !matchesId)
                {
                    continue;
                }

                var distance = Vector3.DistanceSquared(entity.GetVector3Property("origin"), origin);

                if ((matchesId && !bestMatchesId) || distance < bestDistance)
                {
                    best = entity;
                    bestDistance = distance;
                    bestMatchesId = matchesId;
                }
            }

            return best;
        }

        /// <summary>Boxes the selected difference where it was in red and where it is in green, joined when it moved.</summary>
        private sealed class ChangeHighlightRenderer(RendererContext rendererContext) : LineDebugRenderer(rendererContext, nameof(ChangeHighlightRenderer))
        {
            public static readonly Color32 OldColor = new(1f, 0.3f, 0.3f, 1f);
            public static readonly Color32 NewColor = new(0.3f, 1f, 0.4f, 1f);
            private static readonly Color32 PathColor = new(0.4f, 0.7f, 1f, 1f);

            private readonly List<SimpleVertex> vertices = [];
            private MapDiffEntry? uploaded;

            public void Render(MapDiffEntry? entry)
            {
                if (entry == null)
                {
                    return;
                }

                if (entry != uploaded)
                {
                    uploaded = entry;
                    vertices.Clear();

                    if (entry.OldBounds is { } oldBounds)
                    {
                        ShapeSceneNode.AddBox(vertices, oldBounds, OldColor);
                    }

                    if (entry.NewBounds is { } newBounds)
                    {
                        ShapeSceneNode.AddBox(vertices, newBounds, NewColor);
                    }

                    if (entry is { OldBounds: { } from, NewBounds: { } to } && Vector3.Distance(from.Center, to.Center) > 1f)
                    {
                        ShapeSceneNode.AddLine(vertices, from.Center, to.Center, PathColor);
                    }

                    Upload(vertices);
                }

                RenderLines(disableDepthTest: true);
            }
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
