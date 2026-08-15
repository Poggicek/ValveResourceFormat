using SkiaSharp;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.Vulkan.Present;

namespace Tests.Renderer.Golden
{
    /// <summary>
    /// Which RHI backend the golden suite renders through, and the one entry point the tests use to reach
    /// it.
    ///
    /// <para><b>OpenGL unless asked otherwise.</b> The OpenGL run is the oracle every other claim about
    /// this migration is measured against, so it is what an unset environment gets. Selecting Vulkan is
    /// deliberate and, today, expected to fail: see <see cref="HeadlessVulkan"/>.</para>
    ///
    /// <para><b>The variable is <c>VRF_RHI_BACKEND</c>, which already existed.</b> A new variable was not
    /// invented for this: <c>RhiBackendSelection</c> in the presentation layer already reads that name,
    /// already parses <c>gl</c>/<c>opengl</c>/<c>vk</c>/<c>vulkan</c>, and already defaults to OpenGL for
    /// the reasons the contract gives. Adding a second name would have let the suite and the viewer come
    /// up on different backends from the same environment, which is exactly the confusion a bug report
    /// cannot afford. It sits alongside <c>VRF_RHI_RECORDING</c>, which stays independent: recording
    /// selects whether the OpenGL run goes through the RHI, and a Vulkan run turns it on unconditionally
    /// because there is nothing else it could mean.</para>
    /// </summary>
    internal static class GoldenBackend
    {
        /// <summary>The environment variable that selects the backend.</summary>
        public const string EnvironmentVariable = RhiBackendSelection.EnvironmentVariableName;

        /// <summary>The backend this run uses.</summary>
        public static RhiBackend Backend { get; } = RhiBackendSelection.Requested;

        /// <summary>Whether this run renders through Vulkan.</summary>
        public static bool IsVulkan => Backend == RhiBackend.Vulkan;

        /// <summary>Whether a device was created and scenes can be attempted.</summary>
        public static bool Available => IsVulkan ? HeadlessVulkan.Available : HeadlessGL.Available;

        /// <summary>Explains why <see cref="Available"/> is false.</summary>
        public static string UnavailableReason
            => IsVulkan ? HeadlessVulkan.UnavailableReason : HeadlessGL.UnavailableReason;

        /// <summary>The adapter or driver the images were rendered on.</summary>
        public static string DeviceDescription
            => IsVulkan ? HeadlessVulkan.DeviceDescription : HeadlessGL.DeviceDescription;

        /// <summary>Which backend actually came up, named rather than assumed.</summary>
        public static string BackendName => IsVulkan ? nameof(RhiBackend.Vulkan) : HeadlessGL.BackendName;

        /// <summary>Creates the device for the selected backend. Never throws.</summary>
        public static void Initialize()
        {
            if (IsVulkan)
            {
                HeadlessVulkan.Initialize();
                return;
            }

            HeadlessGL.Initialize();
        }

        /// <summary>Draws one scene on the OpenGL path.</summary>
        public static SKBitmap RenderScene(GoldenScene scene) => HeadlessGL.RenderScene(scene);

        /// <summary>Attempts one scene on the Vulkan path and reports every stage.</summary>
        public static VulkanSceneOutcome AttemptVulkanScene(GoldenScene scene) => HeadlessVulkan.RenderScene(scene);

        /// <summary>Destroys the device and stops the render thread.</summary>
        public static void Shutdown()
        {
            if (IsVulkan)
            {
                HeadlessVulkan.Shutdown();
                return;
            }

            HeadlessGL.Shutdown();
        }
    }
}
