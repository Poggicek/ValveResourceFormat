using System.IO;
using NUnit.Framework;
using ValveResourceFormat;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Materials;

namespace Tests.Renderer.Golden
{
    /// <summary>
    /// One reference scene: what to put in front of the camera, how many simulation steps to take before
    /// capturing, and how far the result may drift from its baseline.
    /// </summary>
    internal sealed class GoldenScene
    {
        /// <summary>
        /// Identifies the scene in the test name, the baseline file name and the failure artifacts.
        /// Keep it stable: renaming a scene orphans its baseline.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>Populates the scene. Runs on the render thread with the GL context current.</summary>
        public required Action<GoldenSceneSetup> Build { get; init; }

        /// <summary>How far this scene's output may drift before it counts as a regression.</summary>
        public ImageTolerance Tolerance { get; init; } = ImageTolerance.Lit;

        /// <summary>
        /// Fixed-timestep simulation steps taken before the frame is captured. Anything time dependent
        /// (animation, particles, exposure adaptation) is sampled at exactly
        /// <c>Frames * <see cref="GoldenRenderHarness.Timestep"/></c> seconds, never at wall-clock time.
        /// </summary>
        public int Frames { get; init; } = 1;

        /// <summary>
        /// Fixture files under <c>Tests/Files</c> the scene needs. A scene whose fixtures are missing is
        /// reported as inconclusive rather than failed, so a trimmed checkout does not read as a regression.
        /// </summary>
        public IReadOnlyList<string> RequiredFixtures { get; init; } = [];

        /// <summary>NUnit shows this as the test case name.</summary>
        public override string ToString() => Name;
    }

    /// <summary>
    /// Everything a scene definition is handed to build itself. Also carries the small number of renderer
    /// switches a scene is allowed to flip (render mode, post-process effects), so those stay in one place
    /// rather than being poked into the renderer from scene code.
    /// </summary>
    internal sealed class GoldenSceneSetup
    {
        /// <summary>The renderer that will draw this scene.</summary>
        public required ValveResourceFormat.Renderer.Renderer Renderer { get; init; }

        /// <summary>Shared loaders and GPU caches.</summary>
        public required RendererContext RendererContext { get; init; }

        /// <summary>Width of the captured image in pixels.</summary>
        public required int Width { get; init; }

        /// <summary>Height of the captured image in pixels.</summary>
        public required int Height { get; init; }

        /// <summary>The scene being populated.</summary>
        public Scene Scene => Renderer.Scene;

        /// <summary>The camera the frame is captured from.</summary>
        public Camera Camera => Renderer.Camera;

        /// <summary>
        /// Overlay text renderer, drawn over the tonemapped frame. Set by the harness before the scene is
        /// built so a scene can queue labels of its own.
        /// </summary>
        public TextRenderer? TextRenderer { get; set; }

        /// <summary>
        /// The file loader backing this scene, exposed so a scene can register a stand-in for an external
        /// reference that would otherwise stop it from being built at all.
        /// </summary>
        public FixtureFileLoader? FileLoader { get; set; }

        /// <summary>
        /// Captures the sun shadow atlas instead of the shaded frame.
        ///
        /// The shadow term is not visible in the colour output of any scene this catalog can build, because
        /// nothing here has an authored material to receive it. Reading the depth atlas back directly pins
        /// the thing that would actually regress -- the cascade's view projection and what the depth-only
        /// pass wrote through it -- rather than a shaded image that would not move if either were wrong.
        /// </summary>
        public bool CaptureShadowAtlas { get; set; }

        /// <summary>
        /// A morph composite to render and capture instead of the shaded frame.
        ///
        /// <para>Captured directly because the composite is an intermediate the scene never shows: it is a
        /// texture of per-vertex deltas that the morph shader path samples, and no fixture here has a model
        /// that samples it. Reading it back is what turns the composite into something a baseline can
        /// hold.</para>
        /// </summary>
        public MorphComposite? MorphComposite { get; set; }

        /// <summary>
        /// Drives the quad overdraw visualisation the way the viewer's Overdraw render mode does: a depth
        /// prime pass, a counting pass that accumulates per-quad shading cost through image stores, and a
        /// fullscreen resolve into a heat map.
        ///
        /// <para>Setting the render mode alone does not do this. The scene shader is only a replacement
        /// shader; the counting passes are driven by the viewer around the render call, so a scene that
        /// merely selects the mode leaves <c>QuadOverdraw</c> untouched -- which is exactly what the call
        /// site census showed was happening.</para>
        /// </summary>
        public bool EnableQuadOverdraw { get; set; }

        /// <summary>
        /// Turns occlusion culling back on and draws the occluded-bounds debug overlay.
        ///
        /// <para>The harness disables occlusion culling everywhere else, because it culls against the
        /// previous frame's depth pyramid and so makes the image a function of how many frames were
        /// rendered. That is only a problem when the frame count varies; it is fixed per scene, so a scene
        /// that opts in and pins its frame count is still reproducible. It must render past the renderer's
        /// one-second occlusion warmup for the pyramid to be built at all.</para>
        /// </summary>
        public bool EnableOcclusionDebug { get; set; }

        /// <summary>
        /// Forces the bloom stage on for this scene.
        ///
        /// It cannot simply be switched on during <see cref="GoldenScene.Build"/>: the renderer replaces its
        /// post-process state from the scene's own post-process volumes on every frame, and a scene built by
        /// hand has none, so anything set beforehand is overwritten before the stage runs. The harness
        /// re-applies this immediately before the post-process pass instead.
        /// </summary>
        public bool EnableBloomAfterRender { get; set; }

        /// <summary>Resources opened through <see cref="LoadFixture"/>, disposed when the scene is torn down.</summary>
        public List<Resource> OpenedResources { get; } = [];

        /// <summary>Absolute path of a file under <c>Tests/Files</c>.</summary>
        public static string FixturePath(string relativePath)
            => Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", relativePath);

        /// <summary>Whether every named fixture is present in this checkout.</summary>
        public static bool FixturesExist(IEnumerable<string> relativePaths)
        {
            foreach (var relativePath in relativePaths)
            {
                if (!File.Exists(FixturePath(relativePath)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Opens a compiled Source 2 resource from <c>Tests/Files</c>, tracked for disposal.</summary>
        public Resource LoadFixture(string relativePath)
        {
            var resource = new Resource
            {
                FileName = relativePath.Replace('\\', '/'),
            };

            resource.Read(FixturePath(relativePath));
            OpenedResources.Add(resource);

            return resource;
        }

        /// <summary>
        /// Points the camera at a target from a fixed offset. Every scene frames itself this way rather than
        /// using <see cref="Camera.FrameObject"/>, whose result depends on the loaded bounding box and would
        /// therefore silently rewrite the shot whenever the geometry changed.
        /// </summary>
        public void PlaceCamera(Vector3 position, Vector3 lookAt)
        {
            Camera.SetLocation(position);
            Camera.LookAt(lookAt);
        }

        /// <summary>
        /// Switches the frame into one of the debug visualisations (for example <c>Normals</c> or
        /// <c>Overdraw</c>), matching what the viewer's render mode dropdown does.
        /// </summary>
        public void SetRenderMode(string renderMode)
        {
            Renderer.ViewBuffer!.Data.RenderMode = RenderModes.GetShaderId(renderMode);

            // The viewer bypasses tonemapping in every debug mode, because the debug shaders already write
            // display-ready colours. Matching that here keeps these baselines comparable to what a developer
            // sees on screen.
            Renderer.Postprocess.Enabled = Renderer.ViewBuffer.Data.RenderMode == 0;

            foreach (var node in Scene.AllNodes)
            {
                node.SetRenderMode(renderMode);
            }
        }
    }
}
