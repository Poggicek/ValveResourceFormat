using System.Collections.Concurrent;
using System.Threading;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SkiaSharp;
using ValveResourceFormat.Renderer;

namespace Tests.Renderer.Golden
{
    /// <summary>
    /// Owns the process-wide offscreen OpenGL context the golden image suite renders through.
    ///
    /// The context lives on a single dedicated thread for its whole lifetime. GLFW is not thread safe and
    /// an OpenGL context may only be current on one thread at a time, while NUnit is free to move tests
    /// between worker threads. Marshalling every GL call onto one owned thread sidesteps both problems and
    /// keeps driver-side state (program specialization, buffer orphaning) consistent across scenes, which
    /// matters for reproducing an image byte for byte.
    ///
    /// When no device can be obtained the class stays unavailable and records why, so tests can skip with a
    /// useful message instead of failing on a machine without a GPU.
    /// </summary>
    internal static class HeadlessGL
    {
        /// <summary>Work items handed to <see cref="RenderThreadLoop"/>.</summary>
        private static readonly BlockingCollection<Action> WorkQueue = [];

        private static Thread? renderThread;
        private static NativeWindow? window;
        private static GoldenRenderHarness? harness;
        private static bool initialized;

        /// <summary>Whether an OpenGL context was successfully created and is ready for use.</summary>
        public static bool Available { get; private set; }

        /// <summary>Explains why <see cref="Available"/> is false. Empty while the device is usable.</summary>
        public static string UnavailableReason { get; private set; } = string.Empty;

        /// <summary>GPU and driver string reported by the context, for failure diagnostics.</summary>
        public static string DeviceDescription { get; private set; } = "no device";

        /// <summary>
        /// Which RHI backend the images were rendered through. Named rather than assumed, so a failure
        /// message from a validation run says which backend's validation actually ran.
        /// </summary>
        public static string BackendName { get; private set; } = "none";

        /// <summary>
        /// Creates the context if it does not exist yet. Safe to call repeatedly; only the first call does work.
        /// Never throws: a failure is reported through <see cref="Available"/> and <see cref="UnavailableReason"/>.
        /// </summary>
        public static void Initialize()
        {
            if (initialized)
            {
                return;
            }

            initialized = true;

            renderThread = new Thread(RenderThreadLoop)
            {
                Name = "GoldenImageGL",
                IsBackground = true,
            };
            renderThread.Start();

            try
            {
                Invoke(CreateContext);
                Available = true;
            }
            catch (Exception e)
            {
                UnavailableReason = DescribeFailure(e);
                Available = false;
            }
        }

        /// <summary>
        /// Turns a context creation failure into a message that says what to do about it, rather than a raw
        /// stack trace. The distinction that matters to a reader is "this machine has no GPU" versus "this
        /// machine has one and the renderer cannot use it".
        /// </summary>
        private static string DescribeFailure(Exception exception)
        {
            // Context creation runs through Invoke, which wraps whatever the render thread threw. The
            // wrapper says nothing useful about why there is no device, so report the original.
            var e = exception is GoldenRenderException { InnerException: { } inner } ? inner : exception;

            var cause = e switch
            {
                DllNotFoundException => $"the GLFW native library could not be loaded ({e.Message})",
                GLFWException => $"GLFW could not create an OpenGL 4.6 core context ({e.Message})",
                NotSupportedException => $"the OpenGL implementation is too old for the renderer ({e.Message})",
                _ => $"{e.GetType().Name}: {e.Message}",
            };

            return cause + ". " + SoftwareRasteriserAdvice;
        }

        /// <summary>
        /// What can be done about a machine with no device.
        ///
        /// <para>The short answer for a headless Linux runner is that a software rasteriser does not rescue
        /// this suite as it stands. llvmpipe implements OpenGL 4.5, one version below the 4.6 the renderer
        /// requires, and it is not the binding constraint anyway: GLFW creates its context through a real
        /// window system, so on a runner with neither X11 nor Wayland it fails at <c>glfwInit</c> before any
        /// driver is consulted. Reaching llvmpipe headlessly means creating the context through EGL with a
        /// surfaceless platform display, which is a different context-creation path from the one the viewer
        /// uses and therefore a second thing to keep working.</para>
        ///
        /// <para>WARP is the Direct3D software rasteriser and does not serve OpenGL at all.</para>
        ///
        /// <para>The path that does work is the one the publish gate workflow already takes: Mesa's lavapipe
        /// is a software <em>Vulkan</em> device, needs no window system, and is deterministic across runner
        /// generations. Once the Vulkan backend can render a frame, this suite gets a real device in CI by
        /// rendering through that backend rather than through GLFW.</para>
        /// </summary>
        private const string SoftwareRasteriserAdvice =
            "No golden images were checked. A software rasteriser does not currently substitute: llvmpipe is "
            + "OpenGL 4.5 against the 4.6 the renderer requires, and GLFW needs a window system regardless, "
            + "while WARP is Direct3D only. CI coverage arrives with the Vulkan backend, which can render "
            + "against Mesa lavapipe with no display server.";

        private static void CreateContext()
        {
            // NUnit owns the process entry thread, so the render thread can never be GLFW's idea of the
            // "main" thread. The check is advisory; all GLFW calls are still serialised onto one thread.
            GLFWProvider.CheckForMainThread = false;

            var settings = new NativeWindowSettings
            {
                APIVersion = GLEnvironment.RequiredVersion,
                Flags = ContextFlags.ForwardCompatible,
                RedBits = 8,
                GreenBits = 8,
                BlueBits = 8,
                AlphaBits = 0,

                // Everything is rendered into framebuffer objects, so the window surface itself needs no
                // depth or stencil. Asking for none also stops drivers picking different default formats.
                DepthBits = 0,
                StencilBits = 0,
                StartFocused = false,
                StartVisible = false,
                ClientSize = new(4, 4),
                AutoLoadBindings = false,
                AutoIconify = false,
                WindowBorder = WindowBorder.Hidden,
                WindowState = WindowState.Normal,
                Title = "VRF golden image harness",
            };

            window = new NativeWindow(settings);
            window.Context.MakeCurrent();

            GL.LoadBindings(new GLFWBindingsContext());

            GLEnvironment.Initialize(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            DeviceDescription = GLEnvironment.GpuRendererAndDriver ?? "unknown device";

            // The harness allocates the render targets every scene draws into, so its lifetime and its
            // thread are exactly the context's. Owning it here is what lets the tests hold no GPU state of
            // their own, and makes a driver that cannot provide those targets read as an unusable device
            // rather than as twenty failing scenes.
            harness = new GoldenRenderHarness();
            BackendName = harness.BackendName;
        }

        private static void RenderThreadLoop()
        {
            foreach (var work in WorkQueue.GetConsumingEnumerable())
            {
                work();
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the context-owning thread and blocks until it finishes,
        /// rethrowing whatever it threw on the calling thread.
        /// </summary>
        public static void Invoke(Action action)
        {
            Invoke(() =>
            {
                action();
                return true;
            });
        }

        /// <summary>
        /// Runs <paramref name="function"/> on the context-owning thread and returns its result, rethrowing
        /// whatever it threw on the calling thread.
        /// </summary>
        public static T Invoke<T>(Func<T> function)
        {
            using var done = new ManualResetEventSlim(false);
            var result = default(T);
            Exception? failure = null;

            WorkQueue.Add(() =>
            {
                try
                {
                    result = function();
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    done.Set();
                }
            });

            done.Wait();

            if (failure != null)
            {
                throw new GoldenRenderException("The golden image render thread threw.", failure);
            }

            return result!;
        }

        /// <summary>Draws one scene on the context-owning thread and returns the captured frame.</summary>
        public static SKBitmap RenderScene(GoldenScene scene)
        {
            if (harness == null)
            {
                throw new GoldenRenderException("No golden image device is available.");
            }

            return Invoke(() => harness.Render(scene));
        }

        /// <summary>Destroys the context and stops the render thread. Called once when the suite finishes.</summary>
        public static void Shutdown()
        {
            if (!initialized)
            {
                return;
            }

            if (Available)
            {
                Invoke(() =>
                {
                    harness?.Dispose();
                    harness = null;

                    window?.Dispose();
                    window = null;
                });
            }

            WorkQueue.CompleteAdding();
            renderThread?.Join(TimeSpan.FromSeconds(10));

            Available = false;
            initialized = false;
        }
    }

    /// <summary>Wraps a failure that happened on the render thread so its origin is not lost.</summary>
    internal sealed class GoldenRenderException : Exception
    {
        public GoldenRenderException()
        {
        }

        public GoldenRenderException(string message) : base(message)
        {
        }

        public GoldenRenderException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
