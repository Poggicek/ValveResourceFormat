using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTK.Graphics.OpenGL;
using SkiaSharp;
using ValveResourceFormat;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace Tests.Renderer.Golden
{
    /// <summary>
    /// Runs the golden scenes against a real Vulkan device and reports what stops them.
    ///
    /// <para><b>This is not expected to produce an image, and it is not written as though it might.</b>
    /// The renderer still makes several hundred direct OpenGL calls outside the OpenGL RHI backend --
    /// every framebuffer, every shader compile, every texture upload, every buffer -- and none of them can
    /// work on a Vulkan device. A harness that reported "36 failed" would be true and useless. This one
    /// walks each scene through the stages the OpenGL harness performs, records what each stage reached
    /// and what stopped it, and reports the causes grouped so the remaining work is a list rather than a
    /// number.</para>
    ///
    /// <para>Two passes are made per scene, for two different reasons:</para>
    /// <list type="bullet">
    /// <item><description><b>Fidelity.</b> <see cref="GoldenRenderHarness"/> is constructed exactly as the
    /// OpenGL path constructs it, so the first blocker reported is the real one the suite hits, not one of
    /// this file's choosing.</description></item>
    /// <item><description><b>Depth.</b> The first blocker is reached within a few dozen calls, which would
    /// leave everything past it unmeasured. A second staged pass repeats the same sequence while stepping
    /// over each stage's failure, so the shader path, the scene build and the frame are enumerated
    /// too.</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Vulkan needs no window system, so unlike <see cref="HeadlessGL"/> there is nothing here that can
    /// fail for want of a display. The dedicated thread is kept for a different reason: the Vulkan backend
    /// documents itself as not thread safe, and NUnit is free to move tests between workers.
    /// </remarks>
    internal static class HeadlessVulkan
    {
        private static readonly BlockingCollection<Action> WorkQueue = [];
        private static readonly List<VulkanSceneOutcome> Outcomes = [];

        private static Thread? RenderThread;
        private static VulkanGoldenDevice? Device;
        private static RhiDeviceCensus? DeviceCensus;
        private static RendererContext? Context;
        private static FixtureFileLoader? FileLoader;
        private static bool Initialized;

        /// <summary>Whether a Vulkan device was created and scenes can be attempted.</summary>
        public static bool Available { get; private set; }

        /// <summary>
        /// Whether the device cleared an offscreen target and read the right pixels back, and what
        /// happened if it did not.
        /// </summary>
        public static string SelfTestResult { get; private set; } = "not run";

        /// <summary>Explains why <see cref="Available"/> is false. Empty while the device is usable.</summary>
        public static string UnavailableReason { get; private set; } = string.Empty;

        /// <summary>The adapter the device selected, for the run banner.</summary>
        public static string DeviceDescription { get; private set; } = "no device";

        /// <summary>Creates the device. Never throws; a failure is reported through <see cref="Available"/>.</summary>
        public static void Initialize()
        {
            if (Initialized)
            {
                return;
            }

            Initialized = true;

            // Before anything can call into OpenTK: a Vulkan run has no OpenGL context, and an unloaded
            // OpenTK entry point is a null pointer that takes the process with it rather than throwing.
            GLCallTrap.Install();

            // The RHI path is what a Vulkan run is measuring. With it off, the renderer would take its
            // direct OpenGL route everywhere and the device would never be asked for anything at all.
            ValveResourceFormat.Renderer.Renderer.EnableRhiRecording = true;

            RenderThread = new Thread(RenderThreadLoop)
            {
                Name = "GoldenImageVulkan",
                IsBackground = true,
            };

            RenderThread.Start();

            try
            {
                Invoke(CreateDevice);
                Available = true;
            }
            catch (Exception e)
            {
                UnavailableReason = Describe(e);
                Available = false;
            }
        }

        private static void CreateDevice()
        {
            Device = VulkanGoldenDevice.Create(ValidationGate.OnMessage, enableValidation: true);
            DeviceDescription = Device.AdapterName;

            // Run before the census is attached, so the self-test's own device calls are not counted as
            // the suite reaching Vulkan. The distinction matters: the number that says whether the
            // renderer reached the device has to be about the renderer.
            ValidationGate.Reset();

            try
            {
                Device.ClearAndReadBack(GoldenRenderHarness.Width, GoldenRenderHarness.Height, new Vector4(0.25f, 0.5f, 0.75f, 1f));

                SelfTestResult = ValidationGate.Collected.Count == 0
                    ? "passed: cleared an offscreen target, read the expected pixels back, and validation said nothing"
                    : $"pixels correct but validation reported {ValidationGate.Collected.Count} error(s): {ValidationGate.Describe()}";
            }
            catch (Exception e)
            {
                SelfTestResult = $"FAILED: {e.GetType().Name}: {e.Message}";
            }

            FileLoader = new FixtureFileLoader();

            Context = new RendererContext(FileLoader, NullLogger.Instance)
            {
                MaxTextureSize = 256,
            };

            // The renderer is handed the census rather than the device itself, so the run can say what was
            // asked of Vulkan as precisely as the trap says what was asked of OpenGL. A device that is
            // never called and a device that works are indistinguishable without this.
            DeviceCensus = new RhiDeviceCensus(Device);
            Context.Device = DeviceCensus;
        }

        private static string Describe(Exception exception)
        {
            var e = exception is GoldenRenderException { InnerException: { } inner } ? inner : exception;

            return e switch
            {
                DllNotFoundException => $"the Vulkan loader could not be loaded ({e.Message})",
                VulkanException => $"no usable Vulkan 1.3 device ({e.Message})",
                _ => $"{e.GetType().Name}: {e.Message}",
            };
        }

        private static void RenderThreadLoop()
        {
            foreach (var work in WorkQueue.GetConsumingEnumerable())
            {
                work();
            }
        }

        /// <summary>Runs <paramref name="action"/> on the device-owning thread and blocks until it finishes.</summary>
        public static void Invoke(Action action)
        {
            Invoke(() =>
            {
                action();
                return true;
            });
        }

        /// <summary>Runs <paramref name="function"/> on the device-owning thread and returns its result.</summary>
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

        /// <summary>Attempts one scene and returns what happened at every stage.</summary>
        public static VulkanSceneOutcome RenderScene(GoldenScene scene)
        {
            var outcome = Invoke(() => Attempt(scene));

            lock (Outcomes)
            {
                Outcomes.Add(outcome);
            }

            return outcome;
        }

        private static VulkanSceneOutcome Attempt(GoldenScene scene)
        {
            var stages = new List<VulkanStage>();

            GLCallTrap.Reset();

            // Pass one: the OpenGL harness, unmodified. Whatever stops this is the blocker the suite
            // really hits, before any allowance this file might make for it.
            GLCallTrap.CurrentStage = "harness";
            stages.Add(Run("harness-construction", static () =>
            {
                using var harness = new GoldenRenderHarness();
            }));

            var reachedByHarness = GLCallTrap.TotalCalls;

            // Pass two: the same sequence, stepping over each failure, so what lies past the first
            // blocker is measured rather than assumed.
            GLCallTrap.Reset();
            stages.AddRange(Probe(scene, out var captured));

            return new VulkanSceneOutcome(scene.Name, stages, reachedByHarness, GLCallTrap.ReportByFile(), GLCallTrap.TotalCalls)
            {
                Image = captured,
            };
        }

        /// <summary>
        /// Walks the stages of a frame, continuing past each failure.
        /// </summary>
        /// <remarks>
        /// The order mirrors <see cref="GoldenRenderHarness"/>: environment, render targets, renderer
        /// construction and resources, scene build, shader link, one frame, readback. One frame rather
        /// than the scene's own count, because what a stage reaches does not change with repetition and
        /// every extra frame is another chance for a zeroed handle to take the process down.
        /// </remarks>
        private static List<VulkanStage> Probe(GoldenScene scene, out SKBitmap? captured)
        {
            var context = Context!;
            var stages = new List<VulkanStage>();
            SKBitmap? image = null;

            // Re-asserted, not set once at startup. The fidelity pass constructs a GoldenRenderHarness,
            // whose constructor assigns this flag from VRF_RHI_RECORDING -- which on a Vulkan run is
            // normally unset, so it turns recording back off for everything after it. Without this the
            // staged pass would take the direct OpenGL route everywhere and the device would go unused for
            // a reason that had nothing to do with the port.
            ValveResourceFormat.Renderer.Renderer.EnableRhiRecording = true;

            GLCallTrap.CurrentStage = "environment";
            stages.Add(Run("gl-environment", () =>
            {
                // GLEnvironment.Initialize is a capability gate over glGetInteger(MAJOR_VERSION). On a
                // Vulkan device the trap answers 0, so it threw "requires OpenGL 4.6, but you have 0.0" --
                // the query answering honestly about a context that does not exist, not a blocker in the
                // port. It also latches, because GpuRendererAndDriver is assigned before the throw, so
                // only the first scene of a run ever reached it and the report read as a one-scene fault.
                // Not asked at all on a device that is not OpenGL. The gate belongs behind a backend check
                // inside GLEnvironment itself, which is not this harness's file to change.
                if (context.Device is { Backend: RhiBackend.OpenGL })
                {
                    GLEnvironment.Initialize(NullLogger.Instance);
                }

                // Still asked, and still counted. The default render state is real work the renderer needs
                // and it is still direct OpenGL, so the stage stays "ok*" rather than clean and the report
                // keeps saying how much of it has yet to move.
                GLEnvironment.SetDefaultRenderState(context);
            }));

            Framebuffer? sceneFramebuffer = null;
            Framebuffer? captureFramebuffer = null;

            GLCallTrap.CurrentStage = "framebuffers";
            stages.Add(Run("render-targets", () =>
            {
                sceneFramebuffer = Framebuffer.Prepare("GoldenSceneColor", GoldenRenderHarness.Width, GoldenRenderHarness.Height, 1,
                    new Framebuffer.AttachmentFormat(PixelInternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.HalfFloat),
                    Framebuffer.DepthAttachmentFormat.Depth32FStencil8);

                var sceneStatus = sceneFramebuffer.Initialize();

                captureFramebuffer = Framebuffer.Prepare("GoldenCaptureColor", GoldenRenderHarness.Width, GoldenRenderHarness.Height, 0,
                    new Framebuffer.AttachmentFormat(PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte),
                    null);

                var captureStatus = captureFramebuffer.Initialize();

                if (sceneStatus != FramebufferErrorCode.FramebufferComplete || captureStatus != FramebufferErrorCode.FramebufferComplete)
                {
                    throw new GoldenRenderException(
                        $"Framebuffer completeness came back as {sceneStatus} and {captureStatus}; on a Vulkan device there is no framebuffer object to be complete.");
                }
            }));

            ValveResourceFormat.Renderer.Renderer? renderer = null;
            GoldenSceneSetup? setup = null;

            // Deliberately fine grained past this point. The first attempt bundled construction, resource
            // loading, the text renderer and the scene build into two steps, and the first shader that
            // failed to compile took every later step with it -- so the mesh, texture and material paths
            // went unmeasured and were reported as "not reached" when they had simply been skipped.
            // Independent steps are run independently; only a genuine data dependency stops one.
            GLCallTrap.CurrentStage = "renderer-init";
            stages.Add(Run("renderer-construct", () =>
            {
                renderer = new ValveResourceFormat.Renderer.Renderer(context)
                {
                    ShadowTextureSize = 1024,
                };

                renderer.Camera.SetViewportSize(GoldenRenderHarness.Width, GoldenRenderHarness.Height);
                renderer.Camera.FieldOfView = context.FieldOfView;
                renderer.Camera.CreateProjectionMatrix();
            }));

            stages.Add(Run("postprocess-load", () => Require(renderer).Postprocess.Load(1)));
            stages.Add(Run("renderer-initialize", () => Require(renderer).Initialize()));

            stages.Add(Run("renderer-resources", () =>
            {
                var target = Require(renderer);

                target.MainFramebuffer = sceneFramebuffer;
                target.LoadRendererResources();
                target.Scene.EnableOcclusionCulling = false;
            }));

            stages.Add(Run("default-lighting", () => GoldenRenderHarness.LoadDefaultLighting(Require(renderer).Scene)));

            GLCallTrap.CurrentStage = "text";
            stages.Add(Run("text-renderer", () =>
            {
                var target = Require(renderer);
                var textRenderer = new TextRenderer(context, target.Camera);

                textRenderer.Load();

                setup = new GoldenSceneSetup
                {
                    Renderer = target,
                    RendererContext = context,
                    Width = GoldenRenderHarness.Width,
                    Height = GoldenRenderHarness.Height,
                    TextRenderer = textRenderer,
                    FileLoader = FileLoader,
                };
            }));

            GLCallTrap.CurrentStage = "scene-build";
            stages.Add(Run("scene-build", () =>
            {
                // The text renderer is an overlay, not a dependency of the scene. If its shader failed to
                // compile the scene is still buildable, and building it is what reaches the mesh, texture
                // and material paths this run exists to enumerate.
                setup ??= new GoldenSceneSetup
                {
                    Renderer = Require(renderer),
                    RendererContext = context,
                    Width = GoldenRenderHarness.Width,
                    Height = GoldenRenderHarness.Height,
                    FileLoader = FileLoader,
                };

                scene.Build(setup);
            }));

            stages.Add(Run("scene-initialize", () =>
            {
                var target = Require(renderer);

                target.Scene.Initialize();
                target.Camera.RecalculateMatrices();
            }));

            GLCallTrap.CurrentStage = "shaders";
            stages.Add(Run("shader-link", context.ShaderLoader.LinkLoadedShaders));

            GLCallTrap.CurrentStage = "frame";

            // The frame boundary is the presentation layer's, which offscreen is this harness --
            // Renderer.AcquireCommandList says so, and the windowed control does the same. Without it
            // the first BeginCommandList of every scene throws "no frame is open" and every scene
            // reports that instead of whatever would really have stopped it, which hides the causes
            // this run exists to enumerate.
            stages.Add(Run("frame-begin", () => DeviceCensus!.BeginFrame()));

            stages.Add(Run("frame-update", () =>
            {
                var target = Require(renderer);

                target.Update(new Scene.UpdateContext
                {
                    Camera = target.Camera,
                    TextRenderer = setup?.TextRenderer!,
                    Timestep = GoldenRenderHarness.Timestep,
                });
            }));

            stages.Add(Run("frame-render", () =>
            {
                var target = Require(renderer);

                target.Render(new Scene.RenderContext
                {
                    Camera = target.Camera,
                    Framebuffer = sceneFramebuffer!,
                    Scene = target.Scene,
                    Textures = target.Textures,
                });
            }));

            stages.Add(Run("postprocess-render", () => Require(renderer).PostprocessRender(sceneFramebuffer!, captureFramebuffer!)));

            stages.Add(Run("frame-end", () => DeviceCensus!.EndFrame()));

            GLCallTrap.CurrentStage = "readback";
            stages.Add(Run("readback", () => image = ReadCapture(captureFramebuffer)));

            GLCallTrap.CurrentStage = "(none)";
            captured = image;

            sceneFramebuffer?.Delete();
            captureFramebuffer?.Delete();

            if (setup != null)
            {
                foreach (var resource in setup.OpenedResources)
                {
                    resource.Dispose();
                }
            }

            renderer?.Dispose();

            return stages;
        }

        /// <summary>
        /// Copies the capture target back to the CPU through the contract's readback path and turns it
        /// into the bitmap the golden comparison takes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>glReadPixels</c> stood here and returned an all-zero image for every scene, because a Vulkan
        /// run has no OpenGL context behind it. <see cref="ICommandList.CopyTextureToBuffer"/> is what the
        /// contract provides instead -- it is the reason <see cref="BufferMemory.HostReadback"/> exists at
        /// all -- and the sequence below is the one
        /// <see cref="VulkanGoldenDevice.ClearAndReadBack"/> already proves against known pixels: copy into
        /// a host-readable buffer, submit, <see cref="IDevice.WaitIdle"/>, then read
        /// <see cref="IBuffer.MappedData"/>.
        /// </para>
        /// <para>
        /// <b>The rows are not flipped, and that is the one place this differs from the OpenGL harness.</b>
        /// <see cref="GoldenRenderHarness"/> flips because OpenGL hands back the bottom row first. Vulkan
        /// does not: the backend flips Y with a negative viewport height, so clip space +1 lands on image
        /// row 0, and row 0 is the row a viewer sees at the top. Copying rows straight through therefore
        /// produces the same orientation the baselines were recorded in. Flipping here as well would undo
        /// the viewport flip and turn every scene upside down -- which the baseline diff would catch, but
        /// only once a scene renders, so the reasoning is recorded rather than left to be discovered.
        /// </para>
        /// <para>
        /// <b>Channels are reordered, not reinterpreted.</b> The capture target is
        /// <see cref="RhiFormat.R8G8B8A8_UNorm"/>, so the copy delivers red first; the baselines are
        /// <see cref="SKColorType.Bgra8888"/>. Alpha is forced opaque for the same reason the OpenGL
        /// harness forces it: the capture target has no meaningful alpha and an encoded PNG should not
        /// depend on what the last shader left there.
        /// </para>
        /// <para>
        /// <b>No explicit barrier.</b> <c>VulkanTexture</c> tracks its own layout per subresource and
        /// <see cref="ICommandList.CopyTextureToBuffer"/> transitions the source itself from that tracked
        /// state. A <see cref="TextureBarrier"/> here would have to name a state this harness cannot know
        /// -- the capture target is left in whichever state the last stage that got as far as touching it
        /// left it, and on a run where <c>postprocess-render</c> failed nothing touched it at all -- and a
        /// wrong <see cref="TextureBarrier.Before"/> asserts in Debug and is silently ignored in Release.
        /// </para>
        /// <para>
        /// <b>Through the device rather than the census.</b> <see cref="RhiDeviceCensus"/> answers "what
        /// did the renderer ask of Vulkan"; this copy is the harness's own work, exactly as the device
        /// self-test is, and the self-test is already deliberately run before the census is attached.
        /// Counting a harness submission would also delete a true finding, since the report's
        /// command-lists-begun-but-never-submitted note is keyed on the census seeing no
        /// <see cref="IDevice.Submit"/>.
        /// </para>
        /// <para>
        /// <b>The all-zero check is not a check on the copy.</b> Dropping the <see cref="IDevice.Submit"/>
        /// below was tried, and the readback buffer came back holding the device self-test's clear colour
        /// rather than zeroes -- host-visible memory is recycled, so a copy that never ran reads as stale
        /// contents, not as nothing. The guard says the stages above drew nothing into the target; what
        /// says the copy itself works is <see cref="VulkanGoldenDevice.ClearAndReadBack"/>, which checks
        /// values it chose.
        /// </para>
        /// </remarks>
        /// <exception cref="GoldenRenderException">There is no device or no capture target, or the copy
        /// came back empty.</exception>
        private static SKBitmap ReadCapture(Framebuffer? captureFramebuffer)
        {
            var device = Device
                ?? throw new GoldenRenderException("No Vulkan device was created, so nothing can be read back.");

            var color = captureFramebuffer?.Color
                ?? throw new GoldenRenderException("The capture target was never created, so there is nothing to read back.");

            var width = GoldenRenderHarness.Width;
            var height = GoldenRenderHarness.Height;
            var sizeInBytes = width * height * 4;

            using var readback = device.CreateBuffer(new BufferDesc(sizeInBytes,
                BufferUsage.CopyDestination, BufferMemory.HostReadback, "GoldenVulkanReadback"));

            device.BeginFrame();

            var commandList = device.BeginCommandList("GoldenReadback");

            commandList.CopyTextureToBuffer(color.RhiTexture, 0, 0, readback);

            device.Submit(commandList);
            device.EndFrame();

            // The copy has to have completed before the mapping is read, and this device submits once per
            // frame, so there is no finer-grained fence to wait on than the device itself.
            device.WaitIdle();

            var pixels = readback.MappedData[..sizeInBytes];

            if (pixels.IndexOfAnyExcept((byte)0) < 0)
            {
                throw new GoldenRenderException(
                    "The readback copy completed and the capture target was entirely zero, so nothing was drawn into it. "
                    + "This is a statement about the stages above rather than about the readback: the copy goes through "
                    + "ICommandList.CopyTextureToBuffer, and the device self-test reported at the top of this file proves "
                    + "that path returns the pixels it was given.");
            }

            var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var destination = bitmap.GetPixelSpan();

            for (var i = 0; i < sizeInBytes; i += 4)
            {
                destination[i + 0] = pixels[i + 2];
                destination[i + 1] = pixels[i + 1];
                destination[i + 2] = pixels[i + 0];
                destination[i + 3] = 255;
            }

            return bitmap;
        }

        /// <summary>
        /// The renderer a stage needs, or an explanation that the stage before it never produced one.
        /// </summary>
        /// <remarks>A stage that cannot run for want of an earlier stage's output is recorded as failed
        /// with that reason, rather than silently skipped: a stage missing from the report and a stage
        /// that was never reachable are different findings.</remarks>
        private static ValveResourceFormat.Renderer.Renderer Require(ValveResourceFormat.Renderer.Renderer? renderer)
            => renderer ?? throw new GoldenRenderException("No renderer was constructed, so this stage could not be attempted.");

        /// <summary>Runs one stage, turning whatever it throws into a recorded outcome.</summary>
        private static VulkanStage Run(string name, Action step)
        {
            var before = GLCallTrap.TotalCalls;

            try
            {
                step();
                return new VulkanStage(name, null, GLCallTrap.TotalCalls - before);
            }
            catch (Exception e)
            {
                return new VulkanStage(name, e, GLCallTrap.TotalCalls - before);
            }
        }

        /// <summary>
        /// The whole run's findings: what blocked the scenes, grouped by cause rather than listed by scene.
        /// </summary>
        public static string Report()
        {
            List<VulkanSceneOutcome> outcomes;

            lock (Outcomes)
            {
                outcomes = [.. Outcomes];
            }

            if (outcomes.Count == 0)
            {
                return "Vulkan golden run: no scene was attempted.";
            }

            var lines = new List<string>
            {
                $"Vulkan golden image run on {DeviceDescription}: {outcomes.Count} scene(s) attempted, "
                    + $"{outcomes.Count(static outcome => outcome.Rendered)} rendered.",
                $"Device self-test (clear an offscreen target, copy it back, check the pixels): {SelfTestResult}.",
                string.Empty,
                "Blockers grouped by cause (stage :: exception type :: message):",
            };

            var byCause = outcomes
                .SelectMany(static outcome => outcome.Stages
                    .Where(static stage => stage.Failure != null)
                    .Select(stage => (Scene: outcome.Scene, Stage: stage)))
                .GroupBy(static entry => $"{entry.Stage.Name} :: {entry.Stage.Failure!.GetType().Name} :: {Condense(entry.Stage.Failure!.Message)}",
                    StringComparer.Ordinal)
                .OrderByDescending(static group => group.Count())
                .ToList();

            foreach (var group in byCause)
            {
                var scenes = group.Select(static entry => entry.Scene).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                var shown = string.Join(", ", scenes.Take(4));
                var more = scenes.Count > 4 ? $" (+{scenes.Count - 4} more)" : string.Empty;

                lines.Add($"  [{scenes.Count,2} scenes] {group.Key}");
                lines.Add($"             scenes: {shown}{more}");
            }

            lines.Add(string.Empty);
            lines.Add("Stage reach across every scene. 'clean' means the stage completed AND issued no direct");
            lines.Add("OpenGL call, so it is the only column that means the stage would work on Vulkan; a stage");
            lines.Add("that completed while the trap swallowed its OpenGL is not a stage that passed.");

            foreach (var stageName in outcomes.SelectMany(static outcome => outcome.Stages.Select(static stage => stage.Name)).Distinct(StringComparer.Ordinal))
            {
                var attempts = outcomes.SelectMany(static outcome => outcome.Stages).Where(stage => stage.Name == stageName).ToList();
                var completed = attempts.Count(static stage => stage.Failure == null);
                var clean = attempts.Count(static stage => stage.Failure == null && stage.DirectGLCalls == 0);
                var calls = attempts.Sum(static stage => stage.DirectGLCalls);

                lines.Add($"  {stageName,-22} {completed,3}/{attempts.Count,-3} completed, {clean,3} clean, {calls,8:N0} direct GL calls");
            }

            lines.Add(string.Empty);
            lines.Add($"Direct OpenGL reached on a Vulkan device across the whole run "
                + $"({GLCallTrap.TotalCallsThisRun:N0} calls that had no context to reach), by file:");
            lines.Add(GLCallTrap.ReportByFile(wholeRun: true));

            lines.Add(string.Empty);
            lines.Add($"What the run asked of the Vulkan device, in the same period ({DeviceCensus?.TotalCalls ?? 0:N0} calls):");
            lines.Add(DeviceCensus?.Report() ?? "  no device was created.");

            foreach (var note in DerivedNotes())
            {
                lines.Add(string.Empty);
                lines.Add(note);
            }

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// Conclusions the two censuses support between them, stated only when the numbers support them.
        /// </summary>
        /// <remarks>
        /// These are derived from the counts rather than written into the report as prose, so a note that
        /// stops being true stops being printed. The frame-lifecycle one is the reason this exists: it is
        /// invisible on the OpenGL oracle, because <c>GLRecordingDevice</c> executes each recorded call
        /// immediately and needs neither a device frame nor a submission, so 36 green scenes prove nothing
        /// about it. On Vulkan the same recording goes into a command buffer that is never handed to a
        /// queue.
        /// </remarks>
        private static IEnumerable<string> DerivedNotes()
        {
            if (DeviceCensus is not { } census)
            {
                yield break;
            }

            var begun = census.CountOf("BeginCommandList");
            var submitted = census.CountOf("Submit");
            var frames = census.CountOf("BeginFrame");

            if (begun > 0 && submitted == 0)
            {
                yield return $"Note: {begun} command list(s) were begun and none was submitted, over {frames} device frame(s). "
                    + "Nothing outside the presentation layer calls IDevice.BeginFrame, EndFrame or Submit -- "
                    + "GUI/Controls/VulkanControl.cs is the only caller in the tree -- so an offscreen consumer records "
                    + "work that is never handed to a queue. The OpenGL backend hides this by executing each recorded call "
                    + "immediately, which is why the 36 passing OpenGL scenes do not catch it.";
            }

            if (census.TotalCalls == 0)
            {
                yield return "Note: the renderer never called the RHI device at all, so nothing here is evidence about the "
                    + "Vulkan backend's correctness -- only about how far a scene gets before it needs OpenGL.";
            }
        }

        /// <summary>
        /// Writes <see cref="Report"/> next to the golden failure artifacts and returns where it went.
        /// </summary>
        public static string WriteReport()
        {
            Directory.CreateDirectory(GoldenImageStore.FailureDirectory);

            var path = Path.Combine(GoldenImageStore.FailureDirectory, "vulkan-blockers.txt");

            File.WriteAllText(path, Report());

            return path;
        }

        private static string Condense(string message)
        {
            var newline = message.IndexOf('\n', StringComparison.Ordinal);
            var single = newline >= 0 ? message[..newline] : message;

            return single.Length > 160 ? single[..160] + "..." : single;
        }

        /// <summary>Destroys the device and stops the thread.</summary>
        public static void Shutdown()
        {
            if (!Initialized)
            {
                return;
            }

            if (Available)
            {
                Invoke(() =>
                {
                    Context?.Dispose();
                    Context = null;

                    Device?.Dispose();
                    Device = null;

                    FileLoader?.Dispose();
                    FileLoader = null;
                });
            }

            WorkQueue.CompleteAdding();
            RenderThread?.Join(TimeSpan.FromSeconds(30));

            Available = false;
            Initialized = false;
        }
    }

    /// <summary>One stage of a Vulkan scene attempt.</summary>
    /// <param name="Name">Which stage.</param>
    /// <param name="Failure">What it threw, or <see langword="null"/> when it completed.</param>
    /// <param name="DirectGLCalls">Direct OpenGL calls trapped while it ran.</param>
    internal sealed record VulkanStage(string Name, Exception? Failure, int DirectGLCalls);

    /// <summary>What happened to one scene on the Vulkan device.</summary>
    /// <param name="Scene">The scene name.</param>
    /// <param name="Stages">Every stage attempted, in order.</param>
    /// <param name="HarnessGLCalls">Direct OpenGL calls the unmodified OpenGL harness reached before failing.</param>
    /// <param name="DirectGLReport">The direct OpenGL surface the staged pass reached, grouped by file.</param>
    /// <param name="DirectGLCalls">Direct OpenGL calls the staged pass reached in total.</param>
    internal sealed record VulkanSceneOutcome(
        string Scene,
        IReadOnlyList<VulkanStage> Stages,
        int HarnessGLCalls,
        string DirectGLReport,
        int DirectGLCalls)
    {
        /// <summary>The captured frame, when the scene got as far as producing one. Owned by the caller.</summary>
        public SKBitmap? Image { get; init; }

        /// <summary>Whether every stage completed, which would mean the scene actually produced an image.</summary>
        public bool Rendered => Stages.All(static stage => stage.Failure == null);

        /// <summary>The first stage that failed, which is the blocker to report for this scene.</summary>
        public VulkanStage? FirstFailure => Stages.FirstOrDefault(static stage => stage.Failure != null);

        /// <summary>A failure message naming the blocker and what the scene reached before it.</summary>
        public string Describe()
        {
            var lines = new List<string>();

            foreach (var stage in Stages)
            {
                // "ok*" rather than "ok": the stage completed, but only because the trap swallowed the
                // OpenGL calls it made. On a real Vulkan device those calls have nowhere to go.
                var status = stage.Failure != null
                    ? "FAIL"
                    : stage.DirectGLCalls == 0 ? "ok  " : "ok* ";

                var detail = stage.Failure == null
                    ? string.Empty
                    : $" -- {stage.Failure.GetType().Name}: {stage.Failure.Message}";

                lines.Add($"  {status} {stage.Name,-22} {stage.DirectGLCalls,7:N0} direct GL calls{detail}");
            }

            lines.Add(string.Empty);
            lines.Add("  Direct OpenGL reached on a Vulkan device, by file:");
            lines.Add(DirectGLReport);

            return string.Join(Environment.NewLine, lines);
        }
    }
}
