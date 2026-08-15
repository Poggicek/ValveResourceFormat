using System.Linq;
using NUnit.Framework;
using SkiaSharp;

namespace Tests.Renderer.Golden
{
    /// <summary>
    /// Renders every scene in <see cref="GoldenSceneCatalog"/> offscreen and compares the result against a
    /// recorded baseline.
    ///
    /// <para>This is the only test coverage the renderer has that involves a GPU. It exists to catch the
    /// class of change that compiles, runs, and quietly draws the wrong thing: a barrier in the wrong place,
    /// a transposed matrix, a texture uploaded in the wrong format. None of those fail an assertion; all of
    /// them move pixels.</para>
    ///
    /// <para>To regenerate the baselines after an intentional visual change, set
    /// <c>VRF_GOLDEN_UPDATE=1</c> and run the suite. Review the resulting PNG diff before committing it.</para>
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class GoldenImageTests
    {
        /// <summary>
        /// The scene cases NUnit enumerates. Names rather than <see cref="GoldenScene"/> instances: a public
        /// test method cannot take an internal parameter type, and the name is what identifies the scene in
        /// the test name, the baseline file and the failure artifacts anyway.
        /// </summary>
        public static IEnumerable<string> Cases
            => GoldenSceneCatalog.Scenes.Select(static scene => scene.Name);

        [OneTimeSetUp]
        public void CreateDevice()
        {
            GoldenBackend.Initialize();

            // Written to the console rather than to a test's own output, so a CI log says plainly which
            // backend was validated and on what device, whether or not any test then fails.
            TestContext.Progress.WriteLine(GoldenBackend.Available
                ? $"Golden images: {GoldenBackend.BackendName} backend on {GoldenBackend.DeviceDescription}. "
                    + $"Backend error gate {(ValidationGate.IsEnforcing ? "enforcing" : "reporting only")} "
                    + $"({ValidationGate.EnvironmentVariable})."
                : $"Golden images: no device. {GoldenBackend.UnavailableReason}");
        }

        [OneTimeTearDown]
        public void DestroyDevice()
        {
            if (RhiCallSiteCensus.IsEnabled)
            {
                TestContext.Progress.WriteLine(RhiCallSiteCensus.Report());
            }

            if (GoldenBackend.IsVulkan)
            {
                // The whole point of a Vulkan run is not that scenes failed but what stopped them, so the
                // report is written to a file as well as to the progress stream: a test runner is free to
                // swallow whatever a one-time teardown writes, and this is the deliverable.
                var written = HeadlessVulkan.WriteReport();

                TestContext.Progress.WriteLine(HeadlessVulkan.Report());
                TestContext.Progress.WriteLine($"Vulkan blocker report written to {written}.");
            }

            GoldenBackend.Shutdown();
        }

        [Test]
        [TestCaseSource(nameof(Cases))]
        public void SceneMatchesBaseline(string sceneName)
        {
            RequireDevice();

            var scene = GoldenSceneCatalog.Scenes.Single(candidate => candidate.Name == sceneName);

            if (!GoldenSceneSetup.FixturesExist(scene.RequiredFixtures))
            {
                var missing = string.Join(", ", scene.RequiredFixtures.Where(
                    static fixture => !GoldenSceneSetup.FixturesExist([fixture])));

                Assert.Ignore($"Scene '{scene.Name}' needs fixtures that are not in this checkout: {missing}.");
            }

            ValidationGate.Reset();

            if (GoldenBackend.IsVulkan)
            {
                RunOnVulkan(scene);
                return;
            }

            using var actual = GoldenBackend.RenderScene(scene);

            // Before the image is even looked at: a backend that reported an error did not render the
            // frame the baseline describes, whatever the pixels happen to say.
            if (ValidationGate.IsEnforcing && ValidationGate.Collected.Count > 0)
            {
                GoldenImageStore.WriteFailureArtifacts(scene.Name, expected: null, actual);

                Assert.Fail($"The {GoldenBackend.BackendName} backend reported errors while rendering scene "
                    + $"'{scene.Name}'.{Environment.NewLine}{ValidationGate.Describe()}");
            }

            if (GoldenImageStore.IsUpdating)
            {
                var written = GoldenImageStore.WriteBaseline(scene.Name, actual);
                Assert.Inconclusive($"Recorded a new baseline for '{scene.Name}' at {written}. "
                    + $"Unset {GoldenImageStore.UpdateEnvironmentVariable} to compare against it.");
            }

            var baselinePath = GoldenImageStore.FindBaseline(scene.Name);

            if (baselinePath == null)
            {
                GoldenImageStore.WriteFailureArtifacts(scene.Name, expected: null, actual);

                Assert.Fail($"Scene '{scene.Name}' has no recorded baseline. "
                    + $"Run the suite with {GoldenImageStore.UpdateEnvironmentVariable}=1 to record one, "
                    + "then review it before committing.");
            }

            using var expected = GoldenImageStore.LoadBaseline(baselinePath!);

            if (expected.Width != actual.Width || expected.Height != actual.Height)
            {
                GoldenImageStore.WriteFailureArtifacts(scene.Name, expected, actual);

                Assert.Fail($"Scene '{scene.Name}' rendered at {actual.Width}x{actual.Height} "
                    + $"but its baseline is {expected.Width}x{expected.Height}.");
            }

            var diff = ImageDiff.Compare(expected, actual, scene.Tolerance);

            if (diff.IsWithin(scene.Tolerance))
            {
                TestContext.Out.WriteLine($"{scene.Name}: {diff.Describe(scene.Tolerance)}");
                return;
            }

            var artifactDirectory = GoldenImageStore.WriteFailureArtifacts(scene.Name, expected, actual);

            var backendErrors = ValidationGate.Describe();

            Assert.Fail($"Golden image regression in scene '{scene.Name}'.{Environment.NewLine}"
                + $"  {diff.Describe(scene.Tolerance)}{Environment.NewLine}"
                + $"  Device: {GoldenBackend.DeviceDescription}{Environment.NewLine}"
                + $"  Expected, actual and diff PNGs written to: {artifactDirectory}"
                + (backendErrors.Length == 0
                    ? string.Empty
                    : $"{Environment.NewLine}  The backend also reported:{Environment.NewLine}{backendErrors}"));
        }

        /// <summary>
        /// Runs a scene on the Vulkan device and reports what stopped it.
        ///
        /// <para>A failure here is the expected result and says so: the renderer still reaches OpenGL
        /// directly for its framebuffers, shaders, textures and buffers, none of which exists on a Vulkan
        /// device. What makes the failure worth having is its content -- which stage was reached, and
        /// which direct OpenGL call sites the scene touched on the way -- so the message carries the whole
        /// staged report rather than one exception.</para>
        ///
        /// <para>If a scene ever does render, it is diffed against the same baseline the OpenGL run is
        /// compared to. That comparison is the reason the RHI has two backends at all, and until a scene
        /// gets that far nothing has ever made it.</para>
        /// </summary>
        private static void RunOnVulkan(GoldenScene scene)
        {
            var outcome = GoldenBackend.AttemptVulkanScene(scene);

            using var actual = outcome.Image;

            if (!outcome.Rendered || actual == null)
            {
                var blocker = outcome.FirstFailure;

                Assert.Fail($"Scene '{scene.Name}' did not render on the Vulkan device. "
                    + $"First blocker: {blocker?.Name} ({blocker?.Failure?.GetType().Name}).{Environment.NewLine}"
                    + $"{outcome.Describe()}"
                    + (ValidationGate.Collected.Count == 0
                        ? string.Empty
                        : $"{Environment.NewLine}  Vulkan validation also reported:{Environment.NewLine}{ValidationGate.Describe()}"));

                return;
            }

            var baselinePath = GoldenImageStore.FindBaseline(scene.Name);

            if (baselinePath == null)
            {
                Assert.Fail($"Scene '{scene.Name}' rendered on Vulkan but has no OpenGL baseline to be compared against.");
                return;
            }

            using var expected = GoldenImageStore.LoadBaseline(baselinePath);
            var diff = ImageDiff.Compare(expected, actual, scene.Tolerance);

            if (diff.IsWithin(scene.Tolerance))
            {
                TestContext.Out.WriteLine($"{scene.Name}: Vulkan matches the OpenGL baseline. {diff.Describe(scene.Tolerance)}");
                return;
            }

            var artifactDirectory = GoldenImageStore.WriteFailureArtifacts(scene.Name, expected, actual);

            Assert.Fail($"Scene '{scene.Name}' rendered on Vulkan but deviates from the OpenGL baseline.{Environment.NewLine}"
                + $"  {diff.Describe(scene.Tolerance)}{Environment.NewLine}"
                + $"  Device: {GoldenBackend.DeviceDescription}{Environment.NewLine}"
                + $"  Expected, actual and diff PNGs written to: {artifactDirectory}");
        }

        /// <summary>
        /// A run with no reachable GPU is not a regression. Every scene is ignored with the reason the
        /// device could not be created, so the suite stays green on machines that cannot render while still
        /// saying plainly that nothing was checked.
        /// </summary>
        private static void RequireDevice()
        {
            if (GoldenBackend.Available)
            {
                return;
            }

            Assert.Ignore($"No {GoldenBackend.BackendName} device is available: " + GoldenBackend.UnavailableReason);
        }

        /// <summary>
        /// Fails if the catalog has grown a duplicate name. Two scenes sharing a name would share a baseline
        /// file, and the second would silently overwrite and then be compared against the first.
        /// </summary>
        [Test]
        public void SceneNamesAreUnique()
        {
            var duplicates = GoldenSceneCatalog.Scenes
                .GroupBy(static scene => scene.Name, StringComparer.Ordinal)
                .Where(static group => group.Count() > 1)
                .Select(static group => group.Key)
                .ToList();

            Assert.That(duplicates, Is.Empty, "Golden scene names must be unique, they name the baseline files.");
        }
    }
}
