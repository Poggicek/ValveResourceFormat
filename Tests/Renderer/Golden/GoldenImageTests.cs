using System.Linq;
using NUnit.Framework;

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

            // The device line above names the adapter; this one says why that adapter and not another. They
            // are separate because the interesting failure is the pair disagreeing -- a run that meant to be
            // on the CPU driver and is not -- and a single sentence would let that pass unread.
            if (GoldenBackend.DriverSelection.Length > 0)
            {
                TestContext.Progress.WriteLine($"Golden images: {GoldenBackend.DriverSelection}");

                // Named up front rather than at teardown, because the run this matters for does not reach
                // its teardown: a driver fault kills the process, and this file is then the only surviving
                // account of what it was doing.
                TestContext.Progress.WriteLine($"Golden images: stage trace at {HeadlessVulkan.TracePath}");
            }
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
        /// Renders a scene on the Vulkan device and scores it against the OpenGL baseline.
        ///
        /// <para><b>The pixel difference is the verdict, not the stage list.</b> A scene can fail a stage
        /// the port has yet to reach -- the skybox, the overlay text -- and still produce a frame, and the
        /// only useful thing to say about that frame is how far it is from the image OpenGL produces. So
        /// the image is diffed whenever there is one, exactly as an OpenGL run diffs its own, and the
        /// number is recorded on the outcome so the run's report can print the whole table in one place.
        /// The stages are still named in the failure text, because "5% of pixels differ" and "and the text
        /// renderer threw" are the two halves of the same diagnosis.</para>
        ///
        /// <para>A scene that failed a stage and still matched the baseline is not passed quietly: the
        /// stages are reported and the scene fails on them. Matching pixels do not make a thrown exception
        /// go away, and this suite has been bitten before by results that were green because something had
        /// stopped being checked.</para>
        /// </summary>
        private static void RunOnVulkan(GoldenScene scene)
        {
            var outcome = GoldenBackend.AttemptVulkanScene(scene);

            using var actual = outcome.Image;

            var stageReport = $"{outcome.Describe()}"
                + (ValidationGate.Collected.Count == 0
                    ? string.Empty
                    : $"{Environment.NewLine}  Vulkan validation also reported:{Environment.NewLine}{ValidationGate.Describe()}");

            if (actual == null)
            {
                var blocker = outcome.FirstFailure;

                outcome.Score = $"NO IMAGE   blocked at {blocker?.Name} ({blocker?.Failure?.GetType().Name})";

                Assert.Fail($"Scene '{scene.Name}' produced no image on the Vulkan device, so it could not be "
                    + $"scored. First blocker: {blocker?.Name} ({blocker?.Failure?.GetType().Name})."
                    + $"{Environment.NewLine}{stageReport}");

                return;
            }

            var baselinePath = GoldenImageStore.FindBaseline(scene.Name);

            if (baselinePath == null)
            {
                var directory = GoldenImageStore.WriteFailureArtifacts(scene.Name, expected: null, actual);

                outcome.Score = "NOT SCORED no OpenGL baseline exists";

                Assert.Fail($"Scene '{scene.Name}' rendered on Vulkan but has no OpenGL baseline to be compared "
                    + $"against. The captured image was written to {directory}.");

                return;
            }

            using var expected = GoldenImageStore.LoadBaseline(baselinePath);

            if (expected.Width != actual.Width || expected.Height != actual.Height)
            {
                var mismatched = GoldenImageStore.WriteFailureArtifacts(scene.Name, expected: null, actual);

                outcome.Score = $"NOT SCORED captured {actual.Width}x{actual.Height}, baseline is "
                    + $"{expected.Width}x{expected.Height}";

                Assert.Fail($"Scene '{scene.Name}' rendered at {actual.Width}x{actual.Height} on Vulkan but its "
                    + $"baseline is {expected.Width}x{expected.Height}. Written to {mismatched}.");

                return;
            }

            var diff = ImageDiff.Compare(expected, actual, scene.Tolerance);
            var within = diff.IsWithin(scene.Tolerance);

            outcome.Score = $"{(within ? "match" : "DIFFER"),-6} {diff.Describe(scene.Tolerance)}";

            if (within && outcome.Failures.Count == 0)
            {
                TestContext.Out.WriteLine($"{scene.Name}: Vulkan matches the OpenGL baseline. {diff.Describe(scene.Tolerance)}");
                return;
            }

            // Written whenever the scene is going to fail, matching the OpenGL path: the three PNGs are how
            // a difference gets looked at rather than only counted.
            var artifactDirectory = GoldenImageStore.WriteFailureArtifacts(scene.Name, expected, actual);

            var headline = within
                ? $"Scene '{scene.Name}' matched the OpenGL baseline on Vulkan, but {outcome.Failures.Count} "
                    + "stage(s) of it failed. The image is not evidence that those stages are unnecessary."
                : $"Scene '{scene.Name}' rendered on Vulkan and deviates from the OpenGL baseline.";

            Assert.Fail($"{headline}{Environment.NewLine}"
                + $"  {diff.Describe(scene.Tolerance)}{Environment.NewLine}"
                + (outcome.CaptureCaveat.Length == 0
                    ? string.Empty
                    : $"  Note: {outcome.CaptureCaveat}{Environment.NewLine}")
                + $"  Device: {GoldenBackend.DeviceDescription}{Environment.NewLine}"
                + $"  Expected, actual and diff PNGs written to: {artifactDirectory}{Environment.NewLine}"
                + stageReport);
        }

        /// <summary>
        /// A run with no reachable GPU is not a regression. Every scene is ignored with the reason the
        /// device could not be created, so the suite stays green on machines that cannot render while still
        /// saying plainly that nothing was checked.
        ///
        /// <para><b>Unless the run was pinned to a software driver</b>, in which case it fails instead. A
        /// CPU device is a file in this tree rather than a property of the machine, so there is no honest
        /// "this box cannot" for it -- the driver is missing, the manifest is wrong, or the loader returned
        /// a hardware adapter that was refused. Ignoring those would turn a run that verified nothing into a
        /// green one, and the whole reason this gate is on a CPU device is that a silently wrong answer here
        /// was what cost somebody a reboot.</para>
        /// </summary>
        private static void RequireDevice()
        {
            if (GoldenBackend.Available)
            {
                return;
            }

            if (GoldenBackend.UnavailableIsFatal)
            {
                Assert.Fail($"No {GoldenBackend.BackendName} device is available and this run is pinned to a "
                    + $"software driver, so that is a setup fault rather than a machine without a GPU: "
                    + GoldenBackend.UnavailableReason);
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
