using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ValvePak;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Entities;
using ValveResourceFormat.Renderer.Input;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.Renderer.World;

/// <summary>
/// Loads every map in the given vpks the way the viewer does, world, spawn groups, entities and a few seconds
/// of simulation, but never draws a frame. Each vpk runs in its own child process, so a crash or a hang only
/// takes that vpk down.
/// </summary>
internal static partial class MapSmokeTest
{
    private const string InProcessFlag = "--in-process";

    // Each update runs the most ticks a frame is allowed, so simulated time passes in as few scene updates as possible
    private const float UpdateTimestep = EntitySystem.MaxTicksPerFrame * EntitySystem.TickInterval;

    private const int ErrorLinesShown = 8;

    // Exit code of a child that found no maps to load
    private const int NoMapsExitCode = 3;

    private sealed class Options
    {
        public List<string> Inputs { get; } = [];
        public int Jobs { get; set; } = 1;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);
        public float SimulateSeconds { get; set; } = 5f;
        public string? LogDirectory { get; set; }
        public string? GameInfo { get; set; }
        public bool InProcess { get; set; }
    }

    private enum Outcome
    {
        Ok,
        NoMaps,
        Failed,
        Crashed,
        TimedOut,
    }

    // Split archives (pak01_000.vpk) are read through their _dir.vpk
    [GeneratedRegex(@"_[0-9]{3}\.vpk$", RegexOptions.IgnoreCase)]
    private static partial Regex ArchiveChunkName();

    public static int Main(string[] args)
    {
        Options options;

        try
        {
            options = ParseArguments(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            PrintUsage();
            return 2;
        }

        var vpks = CollectVpks(options.Inputs);

        if (vpks.Count == 0)
        {
            Console.Error.WriteLine("No vpk files found.");
            return 2;
        }

        return options.InProcess
            ? RunInProcess([.. vpks.Select(vpk => vpk.Path)], options.SimulateSeconds, options.GameInfo)
            : RunIsolated(vpks, options).GetAwaiter().GetResult();
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            Usage: MapSmokeTest <vpk or folder>... [options]

              --jobs <n>         vpks loaded at the same time (default 1)
              --timeout <sec>    kill a vpk's process after this long (default 600)
              --seconds <sec>    simulated entity time after loading (default 5)
              --logs <folder>    write the full output of every vpk that did not pass
              --game <folder>    mount this game for vpks outside of it, such as downloaded workshop items
                                 (the game folder, a mod folder, or its gameinfo.gi)
              --in-process       load in this process, one vpk after another, to debug a failure
            """);
    }

    private static Options ParseArguments(string[] args)
    {
        var options = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            string NextValue() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value.");
            float NextNumber() => float.Parse(NextValue(), CultureInfo.InvariantCulture);

            switch (args[i])
            {
                case "--jobs":
                    options.Jobs = Math.Max(1, (int)NextNumber());
                    break;
                case "--timeout":
                    options.Timeout = TimeSpan.FromSeconds(NextNumber());
                    break;
                case "--seconds":
                    options.SimulateSeconds = NextNumber();
                    break;
                case "--logs":
                    options.LogDirectory = NextValue();
                    break;
                case "--game":
                    options.GameInfo = FindGameInfo(NextValue());
                    break;
                case InProcessFlag:
                    options.InProcess = true;
                    break;
                case "-h" or "--help":
                    throw new ArgumentException(string.Empty);
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option {args[i]}.");
                    }

                    options.Inputs.Add(args[i]);
                    break;
            }
        }

        if (options.Inputs.Count == 0)
        {
            throw new ArgumentException("No vpk files or folders given.");
        }

        return options;
    }

    /// <summary>
    /// Finds the gameinfo.gi of a game, given the file itself, the mod folder holding it, or the game folder above
    /// its mods, where the one that is not core is the game.
    /// </summary>
    private static string FindGameInfo(string path)
    {
        const string GameInfoName = "gameinfo.gi";

        if (File.Exists(path))
        {
            return Path.GetFullPath(path);
        }

        var inFolder = Path.Join(path, GameInfoName);

        if (File.Exists(inFolder))
        {
            return Path.GetFullPath(inFolder);
        }

        var inMod = Directory.Exists(path)
            ? Directory.EnumerateDirectories(path)
                .Where(mod => !Path.GetFileName(mod).Equals("core", StringComparison.OrdinalIgnoreCase))
                .Select(mod => Path.Join(mod, GameInfoName))
                .FirstOrDefault(File.Exists)
            : null;

        return inMod != null ? Path.GetFullPath(inMod) : throw new ArgumentException($"No {GameInfoName} found in {path}.");
    }

    /// <summary>Finds the vpks to load, each named relative to the folder it was found in.</summary>
    private static List<(string Path, string Name)> CollectVpks(List<string> inputs)
    {
        var vpks = new List<(string Path, string Name)>();

        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                var found = Directory.EnumerateFiles(input, "*.vpk", SearchOption.AllDirectories)
                    .Where(path => !ArchiveChunkName().IsMatch(path))
                    .Order(StringComparer.OrdinalIgnoreCase);

                vpks.AddRange(found.Select(path => (Path.GetFullPath(path), Path.GetRelativePath(input, path))));
            }
            else if (File.Exists(input))
            {
                vpks.Add((Path.GetFullPath(input), Path.GetFileName(input)));
            }
            else
            {
                Console.Error.WriteLine($"Not found: {input}");
            }
        }

        return [.. vpks.DistinctBy(vpk => vpk.Path, StringComparer.OrdinalIgnoreCase)];
    }

    private static async Task<int> RunIsolated(List<(string Path, string Name)> vpks, Options options)
    {
        using var cancellation = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        var results = new List<(string Name, Outcome Outcome)>();
        var completed = 0;
        var outputLock = new Lock();
        var totalTimer = Stopwatch.StartNew();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.Jobs,
            CancellationToken = cancellation.Token,
        };

        try
        {
            await Parallel.ForEachAsync(vpks, parallelOptions, async (vpk, cancellationToken) =>
            {
                var timer = Stopwatch.StartNew();
                var (outcome, exitCode, output) = await RunChild(vpk.Path, options, cancellationToken).ConfigureAwait(false);
                string? logPath = null;

                if (outcome is not (Outcome.Ok or Outcome.NoMaps) && options.LogDirectory != null)
                {
                    logPath = Path.Join(options.LogDirectory, Path.ChangeExtension(vpk.Name, ".log"));
                    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                    await File.WriteAllTextAsync(logPath, output, cancellationToken).ConfigureAwait(false);
                }

                using var _ = outputLock.EnterScope();

                completed++;
                results.Add((vpk.Name, outcome));

                var label = outcome switch
                {
                    Outcome.Ok => "OK",
                    Outcome.NoMaps => "NO MAPS",
                    Outcome.Failed => "FAIL",
                    Outcome.Crashed => "CRASH",
                    _ => "TIMEOUT",
                };

                var progress = string.Create(CultureInfo.InvariantCulture, $"[{completed.ToString(CultureInfo.InvariantCulture).PadLeft(vpks.Count.ToString(CultureInfo.InvariantCulture).Length)}/{vpks.Count}]");
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{progress} {label,-7} {timer.Elapsed.TotalSeconds,6:0.0}s  {vpk.Name}"));

                if (outcome is Outcome.Ok or Outcome.NoMaps)
                {
                    return;
                }

                foreach (var line in SummarizeFailure(outcome, exitCode, output))
                {
                    Console.WriteLine($"        {line}");
                }

                if (logPath != null)
                {
                    Console.WriteLine($"        full log: {logPath}");
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Cancelled.");
        }

        var failed = results.Where(r => r.Outcome is not (Outcome.Ok or Outcome.NoMaps)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var tested = results.Count(r => r.Outcome != Outcome.NoMaps);

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{tested - failed.Count}/{tested} vpks with maps passed in {totalTimer.Elapsed:hh\\:mm\\:ss}, {results.Count - tested} had no maps."));

        foreach (var (name, outcome) in failed)
        {
            Console.WriteLine($"  {outcome,-8} {name}");
        }

        return failed.Count > 0 || results.Count < vpks.Count ? 1 : 0;
    }

    private static async Task<(Outcome Outcome, int ExitCode, string Output)> RunChild(string vpk, Options options, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        process.StartInfo.ArgumentList.Add(InProcessFlag);
        process.StartInfo.ArgumentList.Add("--seconds");
        process.StartInfo.ArgumentList.Add(options.SimulateSeconds.ToString(CultureInfo.InvariantCulture));

        if (options.GameInfo != null)
        {
            process.StartInfo.ArgumentList.Add("--game");
            process.StartInfo.ArgumentList.Add(options.GameInfo);
        }

        process.StartInfo.ArgumentList.Add(vpk);

        // Both streams go to one buffer, so a crash trace lands after the map it happened on
        var output = new StringBuilder();
        void Append(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null)
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(e.Data);
            }
        }

        process.OutputDataReceived += Append;
        process.ErrorDataReceived += Append;

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return (Outcome.TimedOut, -1, output.ToString());
        }

        var outcome = process.ExitCode switch
        {
            0 => Outcome.Ok,
            1 => Outcome.Failed,
            NoMapsExitCode => Outcome.NoMaps,
            _ => Outcome.Crashed,
        };

        return (outcome, process.ExitCode, output.ToString());
    }

    private static IEnumerable<string> SummarizeFailure(Outcome outcome, int exitCode, string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (outcome == Outcome.Failed)
        {
            var errors = lines.Where(line => line.StartsWith(ConsoleErrorLogger.ErrorPrefix, StringComparison.Ordinal)).ToList();

            foreach (var line in errors.Take(ErrorLinesShown))
            {
                yield return line;
            }

            if (errors.Count > ErrorLinesShown)
            {
                yield return $"... and {errors.Count - ErrorLinesShown} more";
            }

            yield break;
        }

        if (outcome == Outcome.Crashed)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"exit code 0x{exitCode:X8}");
        }

        // The map being loaded, then the start of the crash output, which names the cause before any stack frames
        var lastMap = Array.FindLastIndex(lines, line => line.StartsWith(MapStartPrefix, StringComparison.Ordinal));

        var crashLines = lines
            .Skip(Math.Max(0, lastMap))
            .Where(line => !line.StartsWith(ConsoleErrorLogger.WarningPrefix, StringComparison.Ordinal))
            .Take(ErrorLinesShown);

        foreach (var line in crashLines)
        {
            yield return line;
        }
    }

    private const string MapStartPrefix = "loading ";

    private static int RunInProcess(List<string> vpks, float simulateSeconds, string? gameInfo)
    {
        var logger = new ConsoleErrorLogger();

        using var window = HiddenGLWindow.Create();

        GLEnvironment.Initialize(logger);

        var mapsLoaded = 0;

        foreach (var vpk in vpks)
        {
            try
            {
                mapsLoaded += LoadVpk(vpk, logger, simulateSeconds, gameInfo);
            }
            catch (Exception e)
            {
                logger.LogError(e, "{Vpk} failed to open", vpk);
            }
        }

        if (logger.ErrorCount > 0)
        {
            return 1;
        }

        return mapsLoaded > 0 ? 0 : NoMapsExitCode;
    }

    /// <returns>The number of maps in the vpk, including those in the map vpks it holds.</returns>
    private static int LoadVpk(string vpk, ConsoleErrorLogger logger, float simulateSeconds, string? gameInfo)
    {
        using var package = new Package();
        package.OptimizeEntriesForBinarySearch(StringComparison.OrdinalIgnoreCase);
        package.Read(vpk);

        using var fileLoader = new NestedMapFileLoader(package, vpk, logger);

        // Mounted on top of anything found by walking up from the vpk, which finds nothing for a downloaded item
        if (gameInfo != null)
        {
            fileLoader.FindAndLoadSearchPaths(gameInfo);
        }

        var mapCount = 0;

        foreach (var map in GetEntries(package, "vmap_c"))
        {
            LoadMapAndReport(fileLoader, map.GetFullPath(), logger, simulateSeconds);
            mapCount++;
        }

        // Workshop addons ship each map as its own vpk under maps/, next to the content it shares with the others
        foreach (var mapPackageEntry in GetEntries(package, "vpk").Where(entry => entry.DirectoryName.StartsWith("maps", StringComparison.OrdinalIgnoreCase)))
        {
            using var mapPackage = new Package();
            mapPackage.SetFileName(mapPackageEntry.GetFullPath());
            mapPackage.OptimizeEntriesForBinarySearch(StringComparison.OrdinalIgnoreCase);
            mapPackage.Read(package.GetMemoryMappedStreamIfPossible(mapPackageEntry));

            fileLoader.MapPackage = mapPackage;

            try
            {
                foreach (var map in GetEntries(mapPackage, "vmap_c"))
                {
                    LoadMapAndReport(fileLoader, map.GetFullPath(), logger, simulateSeconds);
                    mapCount++;
                }
            }
            finally
            {
                fileLoader.MapPackage = null;
            }
        }

        if (mapCount == 0)
        {
            Console.WriteLine($"no vmap_c in {vpk}");
        }

        return mapCount;
    }

    private static List<PackageEntry> GetEntries(Package package, string extension)
        => package.Entries?.GetValueOrDefault(extension) ?? [];

    private static void LoadMapAndReport(GameFileLoader fileLoader, string mapPath, ConsoleErrorLogger logger, float simulateSeconds)
    {
        var errorsBefore = logger.ErrorCount;
        var timer = Stopwatch.StartNew();

        Console.WriteLine($"{MapStartPrefix}{mapPath}");

        try
        {
            var entityCount = LoadMap(fileLoader, mapPath, logger, simulateSeconds);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"loaded {mapPath} in {timer.Elapsed.TotalSeconds:0.0}s, {entityCount} entities"));
        }
        catch (Exception e)
        {
            logger.LogError(e, "{Map} failed to load", mapPath);
        }

        var errors = logger.ErrorCount - errorsBefore;

        if (errors > 0)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{mapPath}: {errors} errors"));
        }
    }

    private static int LoadMap(GameFileLoader fileLoader, string mapPath, ILogger logger, float simulateSeconds)
    {
        using var context = new RendererContext(fileLoader, logger);

        // Nothing is drawn, and a shader that does not compile is not what fails a map
        context.ShaderLoader.CompileShaders = false;

        var graphicsContext = context.Device.CreateContext();
        graphicsContext.Begin();
        GLEnvironment.SetDefaultRenderState();

        var renderer = new Renderer(context);

        try
        {
            renderer.Initialize();
            renderer.LoadRendererResources();

            var textRenderer = new TextRenderer(context, renderer.Camera);
            textRenderer.Load();

            var input = new UserInput(renderer);
            var scene = renderer.Scene;
            var entitySystem = renderer.EntitySystem;

            var world = WorldLoader.LoadMap(mapPath, scene, entitySystem);

            NavMeshSceneNode.AddNavNodesToScene(world.NavMesh, scene, world.NavigationHullNames);
            NavSpaceSceneNode.AddToScene(world.NavSpace, scene);
            NavMeshSceneNode.AddFlowMapNodesToScene(world.NavFlowMap, world.NavMesh, scene, world.NavigationHullNames);
            CS2BombDamageSceneNode.AddBakedBombDamageToScene(world.BombDamage, scene);

            if (world.SpawnCameraMatrix is { } spawn)
            {
                renderer.Camera.SetFromTransformMatrix(spawn);
            }

            input.EntitySystem = entitySystem;
            entitySystem.SpawnPlayer(input.PlayerMovement, scene);

            foreach (var initializing in renderer.Scenes)
            {
                initializing.Initialize();
            }

            renderer.Camera.SetViewportSize(1280, 720);

            for (var time = 0f; time < simulateSeconds; time += UpdateTimestep)
            {
                renderer.Update(new Scene.UpdateContext
                {
                    Camera = renderer.Camera,
                    TextRenderer = textRenderer,
                    Timestep = UpdateTimestep,
                });
            }

            context.WaitForLoadingToStop();

            return entitySystem.Entities.Count;
        }
        finally
        {
            renderer.Dispose();
            graphicsContext.End();
        }
    }
}

/// <summary>
/// Searches the map vpk being loaded out of an addon before the addon itself, the way the viewer opens a vpk
/// inside another one. Addons can carry stale copies of map files, such as an old <c>.nav</c>, next to the map vpk.
/// </summary>
internal sealed class NestedMapFileLoader(Package addonPackage, string addonPath, ILogger logger)
    : GameFileLoader(addonPackage, addonPath, logger)
{
    public Package? MapPackage { get; set; }

    public override (string? PathOnDisk, Package? Package, PackageEntry? PackageEntry) FindFile(string file, bool logNotFound = true)
    {
        if (MapPackage?.FindEntry(file) is { } entry)
        {
            return (null, MapPackage, entry);
        }

        return base.FindFile(file, logNotFound);
    }
}

/// <summary>
/// A hidden window whose GL context is current on the creating thread. Made through GLFW directly, because OpenTK's
/// <c>NativeWindow</c> also enumerates joysticks, which takes longer than loading a small map.
/// </summary>
internal sealed unsafe class HiddenGLWindow : IDisposable
{
    private readonly Window* Handle;

    private HiddenGLWindow(Window* handle)
    {
        Handle = handle;
    }

    public static HiddenGLWindow Create()
    {
        if (!GLFW.Init())
        {
            throw new InvalidOperationException("Failed to initialize GLFW.");
        }

        GLFW.WindowHint(WindowHintBool.Visible, false);
        GLFW.WindowHint(WindowHintClientApi.ClientApi, ClientApi.OpenGlApi);
        GLFW.WindowHint(WindowHintInt.ContextVersionMajor, GLEnvironment.RequiredVersion.Major);
        GLFW.WindowHint(WindowHintInt.ContextVersionMinor, GLEnvironment.RequiredVersion.Minor);
        GLFW.WindowHint(WindowHintOpenGlProfile.OpenGlProfile, OpenGlProfile.Core);
        GLFW.WindowHint(WindowHintBool.OpenGLForwardCompat, true);

        var handle = GLFW.CreateWindow(1, 1, "Map Smoke Test", null, null);

        if (handle == null)
        {
            GLFW.Terminate();
            throw new InvalidOperationException($"Failed to create an OpenGL {GLEnvironment.RequiredVersion} context.");
        }

        GLFW.MakeContextCurrent(handle);
        GL.LoadBindings(new GLFWBindingsContext());

        return new HiddenGLWindow(handle);
    }

    public void Dispose()
    {
        GLFW.DestroyWindow(Handle);
        GLFW.Terminate();
    }
}

/// <summary>
/// Prints warnings and errors, and counts the errors, which is how a map that logs a caught exception still fails.
/// </summary>
internal sealed class ConsoleErrorLogger : ILogger
{
    public const string ErrorPrefix = "ERROR: ";
    public const string WarningPrefix = "warn: ";

    private int Errors;

    public int ErrorCount => Volatile.Read(ref Errors);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);

        if (logLevel < LogLevel.Error)
        {
            Console.WriteLine($"{WarningPrefix}{message}");
            return;
        }

        Interlocked.Increment(ref Errors);

        // The innermost exception is usually the one that says what is wrong with the file
        var cause = exception?.GetBaseException();
        var line = cause == null ? message : $"{message} ({cause.GetType().Name}: {cause.Message})";

        // One write, so lines from the streaming threads cannot land inside the trace
        Console.WriteLine(exception == null ? $"{ErrorPrefix}{line}" : $"{ErrorPrefix}{line}{Environment.NewLine}{exception}");
    }
}
