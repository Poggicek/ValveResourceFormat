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
            HeadlessGL.Initialize();

            // Written to the console rather than to a test's own output, so a CI log says plainly which
            // backend was validated and on what device, whether or not any test then fails.
            TestContext.Progress.WriteLine(HeadlessGL.Available
                ? $"Golden images: {HeadlessGL.BackendName} backend on {HeadlessGL.DeviceDescription}. "
                    + $"Backend error gate {(ValidationGate.IsEnforcing ? "enforcing" : "reporting only")} "
                    + $"({ValidationGate.EnvironmentVariable})."
                : $"Golden images: no device. {HeadlessGL.UnavailableReason}");
        }

        [OneTimeTearDown]
        public void DestroyDevice() => HeadlessGL.Shutdown();

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

            using var actual = HeadlessGL.RenderScene(scene);

            // Before the image is even looked at: a backend that reported an error did not render the
            // frame the baseline describes, whatever the pixels happen to say.
            if (ValidationGate.IsEnforcing && ValidationGate.Collected.Count > 0)
            {
                GoldenImageStore.WriteFailureArtifacts(scene.Name, expected: null, actual);

                Assert.Fail($"The {HeadlessGL.BackendName} backend reported errors while rendering scene "
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
                + $"  Device: {HeadlessGL.DeviceDescription}{Environment.NewLine}"
                + $"  Expected, actual and diff PNGs written to: {artifactDirectory}"
                + (backendErrors.Length == 0
                    ? string.Empty
                    : $"{Environment.NewLine}  The backend also reported:{Environment.NewLine}{backendErrors}"));
        }

        /// <summary>
        /// A run with no reachable GPU is not a regression. Every scene is ignored with the reason the
        /// device could not be created, so the suite stays green on machines that cannot render while still
        /// saying plainly that nothing was checked.
        /// </summary>
        private static void RequireDevice()
        {
            if (HeadlessGL.Available)
            {
                return;
            }

            Assert.Ignore("No OpenGL 4.6 device is available: " + HeadlessGL.UnavailableReason);
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
