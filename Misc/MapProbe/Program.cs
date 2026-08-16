using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SkiaSharp;
using SteamDatabase.ValvePak;
using Tests.Renderer.Golden;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Materials;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.OpenGL;
using ValveResourceFormat.Renderer.RHI.Vulkan;
using ValveResourceFormat.Renderer.World;
using VrfRenderer = ValveResourceFormat.Renderer.Renderer;

namespace MapProbe;

/// <summary>
/// Loads a real map and renders it offscreen through either RHI backend, so "the map does not render on
/// Vulkan" becomes an image, a number and a command transcript instead of a report.
///
/// <para><b>Why this is a tool and not a golden scene.</b> The golden suite covers 37 hand-built scenes and
/// no map, which is its largest blind spot: <c>world_static_batch</c> builds <c>ModelSceneNode</c>s and
/// reaches no indirect draw at all, so the entire aggregate and map-loading path is unmeasured. Closing
/// that gap with a test is not possible -- a map means game content, and <c>de_mirage.vpk</c> alone is
/// 170 MB that cannot be vendored or fetched in CI. So the measurement lives here instead, pointed at
/// whatever the person running it happens to own.</para>
///
/// <para><b>Never run by CI, and it says so when the content is absent.</b> Every path that needs game
/// content checks for it first and exits <see cref="ExitContentMissing"/> with the path it wanted. There is
/// no fallback, no skip and no silent success: a run that cannot find a map is an error, because a probe
/// that passed by doing nothing is worse than no probe.</para>
///
/// <para><b>One backend per process, by necessity.</b> The Vulkan run installs a process-wide OpenGL call
/// trap and pins the Vulkan loader's driver selection before its first call, neither of which can be undone.
/// Comparing the backends therefore means two runs and <c>--diff</c>, not one run with a switch.</para>
/// </summary>
internal static class Program
{
    /// <summary>The run produced an image.</summary>
    private const int ExitOk = 0;

    /// <summary>The probe threw. The exception is on standard error.</summary>
    private const int ExitFailed = 1;

    /// <summary>The command line could not be understood.</summary>
    private const int ExitUsage = 2;

    /// <summary>
    /// The environment refuses to run: no software Vulkan driver is vendored, or the loader handed back a
    /// hardware adapter. Distinct from a probe failure because nothing was measured.
    /// </summary>
    private const int ExitEnvironment = 3;

    /// <summary>
    /// The game content the run needs is not on this machine. Distinct from every other code, and never
    /// zero: this is the one a CI runner would hit, and it must be loud.
    /// </summary>
    private const int ExitContentMissing = 4;

    private static int Main(string[] args)
    {
        var options = Options.Parse(args, out var parseError);

        if (options == null)
        {
            Console.Error.WriteLine(parseError);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Options.Usage);
            return ExitUsage;
        }

        try
        {
            if (options.DiffLeft != null && options.DiffRight != null)
            {
                return Diff(options);
            }

            return options.ListMaps ? ListMaps(options) : Render(options);
        }
#pragma warning disable CA1031 // The top level of a command line tool: every exception is a report, not a crash.
        catch (Exception e)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("PROBE FAILED: " + e);
            return ExitFailed;
        }
    }

    /// <summary>
    /// Opens the package the run was pointed at, or explains that the content is not here.
    /// </summary>
    /// <remarks>
    /// The one gate between this tool and a machine that has no games installed. It is checked before the
    /// device is created rather than after, so a run on a bare CI agent costs nothing and cannot be
    /// mistaken for a Vulkan finding.
    /// </remarks>
    private static Package? OpenPackage(Options options)
    {
        if (!File.Exists(options.Vpk))
        {
            Console.Error.WriteLine($"CONTENT MISSING: no VPK at '{options.Vpk}'.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("MapProbe renders maps out of installed game content, which is why it is a tool");
            Console.Error.WriteLine("rather than a test: no map is vendored in this repository and none can be. Point");
            Console.Error.WriteLine("--vpk at a map package from a Source 2 game you have installed, for example:");
            Console.Error.WriteLine(@"  ...\steamapps\common\Counter-Strike Global Offensive\game\csgo\maps\de_mirage.vpk");
            Console.Error.WriteLine(@"  ...\steamapps\workshop\content\730\<id>\<map>.vpk");

            return null;
        }

        var package = new Package();
        package.Read(options.Vpk);

        return package;
    }

    /// <summary>
    /// The maps a package holds. Printed by <c>--list</c>, and used to default <c>--map</c> when the run
    /// did not name one, as <c>Misc/RenderTest</c> does.
    /// </summary>
    private static List<string> MapsIn(Package package)
        => package.Entries?.TryGetValue("vmap_c", out var maps) == true
            ? [.. maps.Select(static entry => entry.GetFullPath())]
            : [];

    private static int ListMaps(Options options)
    {
        using var package = OpenPackage(options);

        if (package == null)
        {
            return ExitContentMissing;
        }

        var maps = MapsIn(package);

        Console.WriteLine($"{options.Vpk}: {maps.Count} map(s)");

        foreach (var map in maps)
        {
            Console.WriteLine("  " + map);
        }

        return maps.Count > 0 ? ExitOk : ExitContentMissing;
    }

    // Everything this method creates -- the package, the file loader, the renderer context, the GL window,
    // the device and the two log writers -- lives for the whole run and is torn down at the end of
    // RenderWith or by process exit. There is no scope for a `using` to end at that is not the process, and
    // a probe that closed its trace writer early would lose the lines it exists to write.
#pragma warning disable CA2000
    private static int Render(Options options)
    {
        var log = new StringBuilder();

        void Say(string line)
        {
            Console.WriteLine(line);
            Console.Out.Flush();
            log.AppendLine(line);
        }

        var package = OpenPackage(options);

        if (package == null)
        {
            return ExitContentMissing;
        }

        var map = options.Map;

        if (string.IsNullOrEmpty(map))
        {
            var maps = MapsIn(package);

            if (maps.Count == 0)
            {
                package.Dispose();

                Console.Error.WriteLine($"CONTENT MISSING: '{options.Vpk}' contains no vmap_c file, so there is no map "
                    + "to render. Pass a map package, or --list a candidate to see what is in one.");

                return ExitContentMissing;
            }

            map = maps[0];
        }

        Say($"probe backend={options.Backend} map={map} vpk={options.Vpk}");
        Say($"       size={options.Width}x{options.Height} frames={options.Frames} maxtex={options.MaxTextureSize}");

        var logger = options.Verbose ? new ConsoleLogger() : (ILogger)NullLogger.Instance;

        var context = new RendererContext(new GameFileLoader(package, options.Vpk), logger)
        {
            MaxTextureSize = options.MaxTextureSize,
        };

        IDevice device;
        NativeWindow? window = null;

        if (options.Backend == "vk")
        {
            // Before the first Vulkan call of the process: the loader reads its driver-selection environment
            // at instance creation and there is no second chance. This is what keeps the run off the display
            // driver, which is not optional -- see SoftwareVulkanIcd.
            SoftwareVulkanIcd.Configure();

            if (SoftwareVulkanIcd.ConfigurationError is { } error)
            {
                Say("FATAL: " + error);
                return ExitEnvironment;
            }

            Say("driver: " + SoftwareVulkanIcd.Status);

            // Before OpenTK is touched: with no GL context an unloaded entry point is a null pointer that
            // takes the process with it rather than throwing.
            GLCallTrap.Install();

            // The RHI path is what a Vulkan run measures. With it off the renderer takes its direct OpenGL
            // route everywhere and the device is never asked for anything.
            VrfRenderer.EnableRhiRecording = true;

            // Every message is written as it arrives rather than collected. The characteristic Vulkan map
            // failure kills the process inside the driver, so anything reported only at the end is never
            // reported at all -- which is exactly when the messages are worth most.
            var validationLog = options.ValidationPath == null
                ? null
                : new StreamWriter(options.ValidationPath, append: false) { AutoFlush = true };

            if (validationLog != null)
            {
                ValidationGate.Sink = (severity, message) => validationLog.WriteLine($"[{severity}] {message}");
            }

            var vulkan = VulkanGoldenDevice.Create(ValidationGate.OnMessage, enableValidation: true);

            Say($"adapter: {vulkan.AdapterName} (device type {vulkan.AdapterTypeName})");

            // The answer, as opposed to the request made above. A hardware adapter here means the loader
            // fell through to the display driver, which is the configuration that hung one and bugchecked a
            // machine, so the device is destroyed before a single command is submitted to it.
            if (SoftwareVulkanIcd.RequireCpuDevice && !vulkan.AdapterIsCpu)
            {
                vulkan.Dispose();
                Say("FATAL: the loader handed back a non-CPU adapter. Nothing was submitted; the device was destroyed.");
                return ExitEnvironment;
            }

            vulkan.ClearAndReadBack(options.Width, options.Height, new Vector4(0.25f, 0.5f, 0.75f, 1f));
            Say("self test: cleared an offscreen target and read the expected pixels back.");

            device = vulkan;
        }
        else
        {
            GLFWProvider.CheckForMainThread = false;

            window = new NativeWindow(new NativeWindowSettings
            {
                APIVersion = GLEnvironment.RequiredVersion,
                Flags = ContextFlags.ForwardCompatible,
                RedBits = 8,
                GreenBits = 8,
                BlueBits = 8,
                AlphaBits = 0,
                DepthBits = 0,
                StencilBits = 0,
                StartFocused = false,
                StartVisible = false,
                ClientSize = new(4, 4),
                AutoLoadBindings = false,
                AutoIconify = false,
                WindowBorder = WindowBorder.Hidden,
                WindowState = WindowState.Normal,
                Title = "VRF map probe",
            });

            window.Context.MakeCurrent();
            GL.LoadBindings(new GLFWBindingsContext());
            GLEnvironment.Initialize(logger);

            Say("adapter: " + (GLEnvironment.GpuRendererAndDriver ?? "unknown"));

            VrfRenderer.EnableRhiRecording = options.Recording;
            GL.Enable(EnableCap.DebugOutputSynchronous);

            device = new GLRecordingDevice(context, ValidationGate.OnMessage);
        }

        if (options.TracePath != null)
        {
            var trace = new StreamWriter(options.TracePath, append: false) { AutoFlush = true };

            device = new TracingDevice(device, trace, options.SyncEachFrame);
        }

        // The renderer is handed the census rather than the device, so the run can say how much of a map
        // frame reaches the device at all. A device that is never called and a device that works look
        // identical from the outside, and on a map that distinction is the whole question.
        var census = new RhiDeviceCensus(device);

        context.Device = census;

        // The probe's own readback goes through the device underneath, exactly as the golden harness does
        // it: the census answers "what did the *renderer* ask of the backend", and counting this tool's
        // submissions in that number would delete the finding.
        return RenderWith(options, map, context, census, device, window, Say, log);
    }
#pragma warning restore CA2000

    private static int RenderWith(Options options, string map, RendererContext context, RhiDeviceCensus census,
        IDevice readbackDevice, NativeWindow? window, Action<string> say, StringBuilder log)
    {
        var isVulkan = census.Backend == RhiBackend.Vulkan;

        if (isVulkan)
        {
            VulkanCommandCensus.IsEnabled = true;
            VulkanCommandCensus.Reset();
        }

        GLEnvironment.SetDefaultRenderState(context);

        // The same two targets, formats and tonemap constants the golden harness uses, so an image from
        // this tool is comparable with one from the suite rather than merely similar to it.
        var sceneFramebuffer = Framebuffer.Prepare("ProbeSceneColor", options.Width, options.Height, 1,
            new Framebuffer.AttachmentFormat(PixelInternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.HalfFloat),
            Framebuffer.DepthAttachmentFormat.Depth32FStencil8);

        sceneFramebuffer.Initialize();
        sceneFramebuffer.ClearMask |= ClearBufferMask.StencilBufferBit;

        var captureFramebuffer = Framebuffer.Prepare("ProbeCaptureColor", options.Width, options.Height, 0,
            new Framebuffer.AttachmentFormat(PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte),
            null);

        captureFramebuffer.Initialize();

        var renderer = new VrfRenderer(context)
        {
            ShadowTextureSize = 1024,
        };

        renderer.Camera.SetViewportSize(options.Width, options.Height);
        renderer.Camera.FieldOfView = context.FieldOfView;
        renderer.Camera.CreateProjectionMatrix();

        renderer.Postprocess.Load(1);
        renderer.Postprocess.FullScreenGamma = 2.01f;
        renderer.Postprocess.ExposureCompensation = -0.4f;

        renderer.Initialize();
        renderer.MainFramebuffer = sceneFramebuffer;
        renderer.LoadRendererResources();
        renderer.Scene.EnableOcclusionCulling = false;

        var textRenderer = new TextRenderer(context, renderer.Camera);
        textRenderer.Load();

        var timer = Stopwatch.StartNew();
        var world = WorldLoader.LoadMap(map, renderer.Scene);
        timer.Stop();

        say($"world loaded in {timer.Elapsed.TotalSeconds:F1}s: {renderer.Scene.AllNodes.Count()} nodes, "
            + $"{world.Entities.Count} entities");

        if (world.SkyboxScene != null)
        {
            renderer.SkyboxScene = world.SkyboxScene;
        }

        if (world.Skybox2D != null)
        {
            renderer.Skybox2D = world.Skybox2D;
        }

        DropNodes(options, renderer.Scene, say);

        renderer.Scene.Initialize();
        world.SkyboxScene?.Initialize();

        if (renderer.Scene.FogInfo.CubeFogActive
            && renderer.Scene.FogInfo.CubemapFog?.CubemapFogTexture is { } cubemapFogTexture)
        {
            renderer.Textures.RemoveAll(static texture => texture.Slot == ReservedTextureSlots.FogCubeTexture);
            renderer.Textures.Add(new(ReservedTextureSlots.FogCubeTexture, "g_tFogCubeTexture", cubemapFogTexture));
        }

        PlaceCamera(options, renderer, world, say);

        context.ShaderLoader.LinkLoadedShaders();

        say(DescribeScene(renderer.Scene));

        for (var frame = 0; frame < options.Frames; frame++)
        {
            census.BeginFrame();

            try
            {
                renderer.Update(new Scene.UpdateContext
                {
                    Camera = renderer.Camera,
                    TextRenderer = textRenderer,
                    Timestep = 1f / 60f,
                });

                renderer.Render(new Scene.RenderContext
                {
                    Camera = renderer.Camera,
                    Framebuffer = sceneFramebuffer,
                    Scene = renderer.Scene,
                    Textures = renderer.Textures,
                });

                renderer.PostprocessRender(sceneFramebuffer, captureFramebuffer);
            }
            finally
            {
                // A frame left open would make every later BeginCommandList throw "a frame is already
                // open", turning one failure into every later one.
                census.EndFrame();
            }

            say($"frame {frame} recorded");
        }

        var bitmap = isVulkan
            ? ReadCaptureVulkan(readbackDevice, captureFramebuffer, options)
            : ReadCaptureOpenGL(captureFramebuffer, options);

        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var stream = File.Create(options.Output))
        {
            data.SaveTo(stream);
        }

        say($"wrote {options.Output}");
        say(DescribeImage(bitmap));

        if (isVulkan)
        {
            // What separates the two very different causes that both end in a black PNG. The scene target's
            // first pass opens with LoadOp.Clear, so an executed frame leaves it non-zero whether or not a
            // single draw produced a fragment: zero here means the recorded work never ran, and non-zero
            // with a black capture means the frame ran and the tonemap is what failed to write.
            say("  " + ProbeNonZero(readbackDevice, sceneFramebuffer.Color, "scene colour"));
            say("  recorded commands: " + VulkanCommandCensus.Summary().Replace(Environment.NewLine, " | "));

            // The three censuses the golden harness reports, for the same reason it reports all three: what
            // never left OpenGL, what reached the device, and what reached a command buffer are different
            // questions, and a frame that records no draw looks identical to one that records a hundred
            // into the wrong attachment in the first two.
            say($"direct OpenGL still reached on a Vulkan device ({GLCallTrap.TotalCalls:N0} calls with no context "
                + "to reach), by file:");
            say(GLCallTrap.ReportByFile());
        }

        say($"what the renderer asked of the device ({census.TotalCalls:N0} calls):");
        say(census.Report());

        var validation = ValidationGate.Describe();

        if (!string.IsNullOrEmpty(validation))
        {
            say("validation errors:");
            say(validation);
        }

        if (options.LogPath != null)
        {
            File.WriteAllText(options.LogPath, log.ToString());
        }

        if (options.TranscriptPath != null && isVulkan)
        {
            File.WriteAllText(options.TranscriptPath, string.Join(Environment.NewLine, VulkanCommandCensus.Transcript));
        }

        bitmap.Dispose();
        renderer.Dispose();
        sceneFramebuffer.Delete();
        captureFramebuffer.Delete();

        // Through the decorator chain, not the outermost wrapper: both the census and TracingDevice
        // implement Dispose as a no-op, so disposing either would tear down nothing at all and leave the
        // real device -- and on Vulkan its instance, allocator and validation messenger -- alive.
        RendererDevice.Unwrap(census)?.Dispose();

        window?.Dispose();

        return ExitOk;
    }

    /// <summary>
    /// Removes scene nodes by type name, which is how a map that renders wrongly is bisected down to the
    /// kind of node responsible.
    /// </summary>
    /// <remarks>
    /// The instrument that turned "the map does not render" into named causes. A map builds thousands of
    /// nodes of a handful of types, and dropping types one at a time until the image changes is the only
    /// cheap way to find which path is at fault; every alternative involves guessing from a transcript.
    /// <para><c>--keep</c> is the complement rather than a second filter: naming what to keep is far shorter
    /// than naming the six things to drop.</para>
    /// </remarks>
    private static void DropNodes(Options options, Scene scene, Action<string> say)
    {
        if (options.Drop.Count == 0 && options.Keep.Count == 0)
        {
            return;
        }

        var dropped = 0;

        foreach (var node in scene.AllNodes.ToList())
        {
            var name = node.GetType().Name;

            var drop = options.Drop.Exists(pattern => name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                || (options.Keep.Count > 0
                    && !options.Keep.Exists(pattern => name.Contains(pattern, StringComparison.OrdinalIgnoreCase)));

            if (!drop)
            {
                continue;
            }

            // Both lists, because the scene keeps static and dynamic nodes separately and a node's own type
            // does not say which it went into.
            scene.Remove(node, dynamic: false);
            scene.Remove(node, dynamic: true);
            dropped++;
        }

        say($"dropped {dropped} nodes by type filter");
    }

    /// <summary>
    /// Places the camera identically on both backends: an explicit position when one was given, the map's
    /// own spawn marker when it has one, and a fixed fallback otherwise.
    /// </summary>
    /// <remarks>
    /// Determinism is the whole requirement. The two backends run in separate processes and their images
    /// are compared, so a camera derived from anything that could differ between the runs would put the
    /// difference into the diff and attribute it to the port.
    /// </remarks>
    private static void PlaceCamera(Options options, VrfRenderer renderer, WorldLoader world, Action<string> say)
    {
        var camera = renderer.Camera;

        if (options.CameraPosition is { } position)
        {
            if (options.CameraTarget is { } target)
            {
                camera.SetLocation(position);
                camera.LookAt(target);
            }
            else
            {
                camera.SetLocationPitchYaw(position, options.CameraPitch, options.CameraYaw);
            }
        }
        else if (world.SpawnCameraMatrix is { } spawn)
        {
            camera.SetFromTransformMatrix(spawn);
        }
        else
        {
            camera.SetLocation(new Vector3(256));
            camera.LookAt(Vector3.Zero);
        }

        camera.CreateProjectionMatrix();
        camera.RecalculateMatrices();

        say($"camera at {camera.Location.X:F1} {camera.Location.Y:F1} {camera.Location.Z:F1} "
            + $"pitch {camera.Pitch * 180f / MathF.PI:F1} yaw {camera.Yaw * 180f / MathF.PI:F1}");
    }

    /// <summary>
    /// What the map built, counted by the distinction that matters: aggregates carry the indirect draw path
    /// that no golden scene reaches, and models carry the one every golden scene reaches.
    /// </summary>
    private static string DescribeScene(Scene scene)
    {
        var aggregates = 0;
        var fragments = 0;
        var models = 0;
        var other = 0;
        var indirectDraws = 0;

        foreach (var node in scene.AllNodes)
        {
            switch (node)
            {
                case SceneAggregate aggregate:
                    aggregates++;
                    indirectDraws += aggregate.IndirectDrawCount;
                    break;

                case SceneAggregate.Fragment:
                    fragments++;
                    break;

                case ValveResourceFormat.Renderer.SceneNodes.ModelSceneNode:
                    models++;
                    break;

                default:
                    other++;
                    break;
            }
        }

        return $"scene: {aggregates} aggregates ({indirectDraws} indirect draws), {fragments} fragments, "
            + $"{models} models, {other} other nodes; "
            + $"indirect buffer {(scene.IndirectDrawsGpu == null ? "absent" : "present")}";
    }

    /// <summary>
    /// Summarises an image in the terms that distinguish the failures this tool sees: black, uniform, or
    /// actually shaded.
    /// </summary>
    /// <remarks>
    /// The colour histogram is what separates "one clear colour covering the frame" from "a rendered
    /// frame", which the mean alone cannot: a scene tonemapped to a flat grey and a scene with geometry in
    /// it can share a mean and differ in every other respect.
    /// </remarks>
    private static string DescribeImage(SKBitmap bitmap)
    {
        var pixels = bitmap.GetPixelSpan();
        long sum = 0;
        var nonBlack = 0;
        var histogram = new Dictionary<uint, int>();

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var b = pixels[i + 0];
            var g = pixels[i + 1];
            var r = pixels[i + 2];

            sum += r + g + b;

            if (r > 2 || g > 2 || b > 2)
            {
                nonBlack++;
            }

            var key = (uint)((r << 16) | (g << 8) | b);
            histogram[key] = histogram.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        var total = pixels.Length / 4;
        var top = histogram.OrderByDescending(static pair => pair.Value).Take(3)
            .Select(static pair => $"#{pair.Key:x6}x{pair.Value}");

        return $"image: mean luma {(double)sum / (total * 3):F1}, {nonBlack} of {total} pixels above near-black "
            + $"({100.0 * nonBlack / total:F1}%), {histogram.Count} distinct colours, top {string.Join(" ", top)}";
    }

    /// <summary>Reads the capture target back through OpenGL, flipping rows as OpenGL requires.</summary>
    private static SKBitmap ReadCaptureOpenGL(Framebuffer captureFramebuffer, Options options)
    {
        var stride = options.Width * 4;
        var buffer = new byte[stride * options.Height];

        captureFramebuffer.Bind(FramebufferTarget.ReadFramebuffer);
        GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
        GL.ReadPixels(0, 0, options.Width, options.Height, PixelFormat.Bgra, PixelType.UnsignedByte, buffer);

        var bitmap = new SKBitmap(options.Width, options.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var pixels = bitmap.GetPixelSpan();

        for (var y = 0; y < options.Height; y++)
        {
            var source = buffer.AsSpan((options.Height - 1 - y) * stride, stride);
            var destination = pixels.Slice(y * stride, stride);

            source.CopyTo(destination);

            for (var x = 3; x < stride; x += 4)
            {
                destination[x] = 255;
            }
        }

        return bitmap;
    }

    /// <summary>
    /// Reads the capture target back through the RHI contract's readback path.
    /// </summary>
    /// <remarks>
    /// Rows are not flipped, unlike the OpenGL path: the Vulkan backend flips Y with a negative viewport
    /// height, so row 0 is already the top row. Channels are reordered because the capture target is
    /// R8G8B8A8 and the bitmap is BGRA; alpha is forced opaque because the target has no meaningful alpha.
    /// This is the same sequence <c>VulkanGoldenDevice.ClearAndReadBack</c> proves against known pixels.
    /// </remarks>
    private static SKBitmap ReadCaptureVulkan(IDevice device, Framebuffer captureFramebuffer, Options options)
    {
        var color = captureFramebuffer.Color ?? throw new InvalidOperationException("No capture target.");
        var sizeInBytes = options.Width * options.Height * 4;

        using var readback = device.CreateBuffer(new BufferDesc(sizeInBytes,
            BufferUsage.CopyDestination, BufferMemory.HostReadback, "ProbeReadback"));

        device.BeginFrame();

        var commandList = device.BeginCommandList("ProbeReadback");
        commandList.CopyTextureToBuffer(color.RhiTexture, 0, 0, readback);

        device.Submit(commandList);
        device.EndFrame();

        // This device submits once per frame, so there is no finer-grained fence to wait on than the device.
        device.WaitIdle();

        var source = readback.MappedData[..sizeInBytes];

        var bitmap = new SKBitmap(options.Width, options.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var destination = bitmap.GetPixelSpan();

        for (var i = 0; i < sizeInBytes; i += 4)
        {
            destination[i + 0] = source[i + 2];
            destination[i + 1] = source[i + 1];
            destination[i + 2] = source[i + 0];
            destination[i + 3] = 255;
        }

        return bitmap;
    }

    /// <summary>
    /// Reads a texture back and says how much of it is non-zero.
    /// </summary>
    /// <remarks>
    /// Deliberately never throws. This runs alongside a result that is already being reported, and a probe
    /// that replaced that result with an exception about probing would destroy the finding it supports.
    /// </remarks>
    private static string ProbeNonZero(IDevice device, RenderTexture? texture, string label)
    {
        if (texture == null)
        {
            return $"{label}: never created.";
        }

        try
        {
            var rhi = texture.RhiTexture;
            var sizeInBytes = rhi is VulkanTexture vulkan
                ? vulkan.MipSizeInBytes(0)
                : texture.Width * texture.Height * 4;

            using var readback = device.CreateBuffer(new BufferDesc(sizeInBytes,
                BufferUsage.CopyDestination, BufferMemory.HostReadback, "ProbeNonZero"));

            device.BeginFrame();

            var commandList = device.BeginCommandList("ProbeNonZero");
            commandList.CopyTextureToBuffer(rhi, 0, 0, readback);

            device.Submit(commandList);
            device.EndFrame();
            device.WaitIdle();

            var pixels = readback.MappedData[..sizeInBytes];
            var nonZero = 0;

            for (var i = 0; i < pixels.Length; i++)
            {
                if (pixels[i] != 0)
                {
                    nonZero++;
                }
            }

            return $"{label} ('{rhi.Name}', {texture.Width}x{texture.Height}, {rhi.Format}): "
                + $"{nonZero:N0} of {sizeInBytes:N0} bytes are non-zero.";
        }
#pragma warning disable CA1031 // A probe alongside a result must not replace the result it describes.
        catch (Exception e)
#pragma warning restore CA1031
        {
            return $"{label} could not be probed: {e.GetType().Name}: {e.Message}";
        }
    }

    /// <summary>
    /// Scores two PNGs against each other with the golden suite's own tolerances.
    /// </summary>
    /// <remarks>
    /// A separate mode rather than something the render does, because the two backends cannot share a
    /// process: the Vulkan run pins the loader and traps OpenGL globally. Comparing therefore always means
    /// two runs and a third invocation.
    /// </remarks>
    private static int Diff(Options options)
    {
        if (!File.Exists(options.DiffLeft) || !File.Exists(options.DiffRight))
        {
            Console.Error.WriteLine($"Cannot diff: '{options.DiffLeft}' and '{options.DiffRight}' must both exist.");
            return ExitUsage;
        }

        using var left = SKBitmap.Decode(options.DiffLeft);
        using var right = SKBitmap.Decode(options.DiffRight);

        var result = ImageDiff.Compare(left, right, ImageTolerance.Lit);

        Console.WriteLine($"diff {options.DiffLeft} vs {options.DiffRight}");
        Console.WriteLine("  " + result.Describe(ImageTolerance.Lit));
        Console.WriteLine("  left:  " + DescribeImage(left));
        Console.WriteLine("  right: " + DescribeImage(right));

        if (options.OutputWasGiven)
        {
            using var rendered = ImageDiff.Render(left, right);
            using var image = SKImage.FromBitmap(rendered);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(options.Output);

            data.SaveTo(stream);

            Console.WriteLine("  wrote " + options.Output);
        }

        return ExitOk;
    }

    private sealed class ConsoleLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            Console.Error.WriteLine($"[{logLevel}] {formatter(state, exception)}");
        }
    }

    private sealed class Options
    {
        public const string Usage = """
            MapProbe --vpk <path.vpk> [--map maps/name.vmap_c] [--backend gl|vk] [--out <image.png>]
                     [--frames N] [--width N] [--height N] [--maxtex N] [--no-recording]
                     [--camera x,y,z] [--look x,y,z] [--pitch deg] [--yaw deg]
                     [--drop TypeName,...] [--keep TypeName,...]
                     [--log <file>] [--trace <file>] [--validation <file>] [--transcript <file>]
                     [--sync] [--verbose]
            MapProbe --vpk <path.vpk> --list
            MapProbe --diff <left.png> <right.png> [--out <diff.png>]

            Renders one map offscreen through an RHI backend and writes a PNG. --map defaults to the first
            map in the package. Exit codes: 0 rendered, 1 probe threw, 2 usage, 3 environment refused,
            4 game content missing.
            """;

        public string Backend { get; private set; } = "gl";
        public string Vpk { get; private set; } = string.Empty;
        public string Map { get; private set; } = string.Empty;
        public string Output { get; private set; } = "probe.png";
        public bool OutputWasGiven { get; private set; }
        public bool ListMaps { get; private set; }
        public int Frames { get; private set; } = 2;
        public int Width { get; private set; } = 320;
        public int Height { get; private set; } = 240;
        public int MaxTextureSize { get; private set; } = 256;
        public bool Recording { get; private set; } = true;
        public bool Verbose { get; private set; }
        public Vector3? CameraPosition { get; private set; }
        public Vector3? CameraTarget { get; private set; }
        public float CameraPitch { get; private set; }
        public float CameraYaw { get; private set; }
        public string? LogPath { get; private set; }
        public string? TranscriptPath { get; private set; }
        public string? TracePath { get; private set; }
        public string? ValidationPath { get; private set; }
        public bool SyncEachFrame { get; private set; }
        public List<string> Drop { get; } = [];
        public List<string> Keep { get; } = [];
        public string? DiffLeft { get; private set; }
        public string? DiffRight { get; private set; }

        public static Options? Parse(string[] args, out string error)
        {
            var options = new Options();

            error = string.Empty;

            for (var i = 0; i < args.Length; i++)
            {
                var flag = args[i];

                // Reported rather than thrown: a trailing flag with no value is the commonest way to mistype
                // this command line, and an IndexOutOfRangeException is a poor way to say so.
                string? Next()
                {
                    if (i + 1 >= args.Length)
                    {
                        return null;
                    }

                    return args[++i];
                }

                var value = flag switch
                {
                    "--backend" or "--vpk" or "--map" or "--out" or "--frames" or "--width" or "--height"
                        or "--maxtex" or "--camera" or "--look" or "--pitch" or "--yaw" or "--log"
                        or "--trace" or "--validation" or "--drop" or "--keep" or "--transcript"
                        or "--diff" => Next(),
                    _ => string.Empty,
                };

                if (value == null)
                {
                    error = $"'{flag}' needs a value.";
                    return null;
                }

                switch (flag)
                {
                    case "--backend": options.Backend = value; break;
                    case "--vpk": options.Vpk = value; break;
                    case "--map": options.Map = value; break;
                    case "--out": options.Output = value; options.OutputWasGiven = true; break;
                    case "--frames": options.Frames = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--width": options.Width = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--height": options.Height = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--maxtex": options.MaxTextureSize = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--no-recording": options.Recording = false; break;
                    case "--verbose": options.Verbose = true; break;
                    case "--list": options.ListMaps = true; break;
                    case "--camera": options.CameraPosition = ParseVector(value); break;
                    case "--look": options.CameraTarget = ParseVector(value); break;
                    case "--pitch": options.CameraPitch = float.Parse(value, CultureInfo.InvariantCulture) * MathF.PI / 180f; break;
                    case "--yaw": options.CameraYaw = float.Parse(value, CultureInfo.InvariantCulture) * MathF.PI / 180f; break;
                    case "--log": options.LogPath = value; break;
                    case "--trace": options.TracePath = value; break;
                    case "--validation": options.ValidationPath = value; break;
                    case "--sync": options.SyncEachFrame = true; break;
                    case "--drop": options.Drop.AddRange(value.Split(',')); break;
                    case "--keep": options.Keep.AddRange(value.Split(',')); break;
                    case "--transcript": options.TranscriptPath = value; break;

                    case "--diff":
                        options.DiffLeft = value;
                        options.DiffRight = Next();

                        if (options.DiffRight == null)
                        {
                            error = "'--diff' needs two images.";
                            return null;
                        }

                        break;

                    default:
                        error = $"Unknown option '{flag}'.";
                        return null;
                }
            }

            if (options.DiffLeft != null)
            {
                return options;
            }

            if (string.IsNullOrEmpty(options.Vpk))
            {
                error = "--vpk is required: this tool renders maps out of installed game content.";
                return null;
            }

            if (options.Backend is not ("gl" or "vk"))
            {
                error = $"--backend must be 'gl' or 'vk', not '{options.Backend}'.";
                return null;
            }

            return options;
        }

        private static Vector3 ParseVector(string text)
        {
            var parts = text.Split(',');

            if (parts.Length != 3)
            {
                throw new FormatException($"'{text}' is not an x,y,z vector.");
            }

            return new Vector3(
                float.Parse(parts[0], CultureInfo.InvariantCulture),
                float.Parse(parts[1], CultureInfo.InvariantCulture),
                float.Parse(parts[2], CultureInfo.InvariantCulture));
        }
    }
}
