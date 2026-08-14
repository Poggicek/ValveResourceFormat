using System.Collections.Concurrent;
using System.Linq;
using ValveResourceFormat.Renderer.RHI;

namespace Tests.Renderer.Golden
{
    /// <summary>
    /// Turns backend diagnostics into test failures.
    ///
    /// <para>The device is created with an <see cref="RhiMessageCallback"/>, so whatever the backend has to
    /// say arrives here: <c>GL_KHR_debug</c> messages from the OpenGL device today, and
    /// <c>VK_EXT_debug_utils</c> messages from the Vulkan one once that backend exists. Nothing in this
    /// class is backend specific, which is the point -- the gate does not need revisiting when the backend
    /// changes underneath it.</para>
    ///
    /// <para><b>On <c>VRF_VULKAN_VALIDATION</c>.</b> The publish gate workflow sets that variable, enables
    /// the Khronos validation layers, and expects this suite to fail from its own debug callback rather
    /// than leaving the workflow's log grep to do the work. When the variable is set, any message at
    /// <see cref="RhiMessageSeverity.Error"/> fails the scene that produced it. When it is not set, messages
    /// are still collected and reported in the failure text of a scene that failed for another reason,
    /// because a driver complaint is usually the explanation for a bad image.</para>
    /// </summary>
    internal static class ValidationGate
    {
        /// <summary>
        /// Set by the publish gate workflow. When it is <c>1</c>, a backend error is a test failure in its
        /// own right rather than a diagnostic attached to some other failure.
        /// </summary>
        public const string EnvironmentVariable = "VRF_VULKAN_VALIDATION";

        private static readonly ConcurrentQueue<string> Messages = new();

        /// <summary>Whether backend errors fail tests on their own.</summary>
        public static bool IsEnforcing
            => Environment.GetEnvironmentVariable(EnvironmentVariable) is "1" or "true";

        /// <summary>Errors seen since the last <see cref="Reset"/>.</summary>
        public static IReadOnlyList<string> Collected => Messages.ToArray();

        /// <summary>The callback handed to the device at creation.</summary>
        public static void OnMessage(RhiMessageSeverity severity, string message)
        {
            if (severity != RhiMessageSeverity.Error)
            {
                return;
            }

            // Bounded, so a driver that produces one message per draw call cannot turn a failing run into
            // an out-of-memory one.
            if (Messages.Count < 64)
            {
                Messages.Enqueue(message);
            }
        }

        /// <summary>Clears the collected messages before a scene renders.</summary>
        public static void Reset()
        {
            while (Messages.TryDequeue(out _))
            {
            }
        }

        /// <summary>
        /// Describes the collected errors for a failure message, or an empty string when there were none.
        /// </summary>
        public static string Describe()
        {
            var collected = Collected;

            if (collected.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(Environment.NewLine, collected.Select(static message => "    " + message));
        }
    }
}
