using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.Vulkan;
using ValveResourceFormat.Renderer.Shaders.Spirv;

namespace ValveResourceFormat.Renderer.Shaders
{
    /// <summary>
    /// Shader stage types in the rendering pipeline.
    /// </summary>
    public enum ShaderProgramType
    {
        /// <summary>Vertex shader stage.</summary>
        Vertex = 0,
        /// <summary>Fragment (pixel) shader stage.</summary>
        Fragment = 1,
        /// <summary>Compute shader stage.</summary>
        Compute = 2,
        /// <summary>Sentinel value equal to the number of shader stages.</summary>
        Max = 3,
    }

    /// <summary>
    /// The GLSL dialect one shader source is generated in. The shader files themselves are written once and
    /// preprocessed into whichever dialect the active backend consumes.
    /// </summary>
    public enum ShaderFlavour
    {
        /// <summary>Desktop OpenGL 4.6 GLSL, compiled by the GL driver. The default.</summary>
        OpenGL = 0,

        /// <summary>
        /// Vulkan GLSL, compiled to SPIR-V. Adds explicit locations and <c>set</c>/<c>binding</c> decorations, moves
        /// the per-draw uniforms into a push constant block, and drops constructs Vulkan GLSL rejects.
        /// </summary>
        Vulkan = 1,
    }

    /// <summary>
    /// Compiles and caches OpenGL shader programs from source files.
    /// </summary>
    public partial class ShaderLoader : IDisposable
    {
        [GeneratedRegex(@"^(?<SourceFile>[0-9]+)\((?<Line>[0-9]+)\) ?: error")]
        private static partial Regex NvidiaGlslError();

        [GeneratedRegex(@"ERROR: (?<SourceFile>[0-9]+):(?<Line>[0-9]+):")]
        private static partial Regex AmdGlslError();

        [GeneratedRegex(@"^(?<SourceFile>[0-9]+):(?<Line>[0-9]+)\((?<Column>[0-9]+)\):")]
        private static partial Regex Mesa3dGlslError();

        private readonly Dictionary<ulong, Shader> CachedShaders = [];

        /// <summary>Gets the number of compiled shader variants currently held in the cache.</summary>
        public int ShaderCount => CachedShaders.Count;

        /// <summary>
        /// Gets every reserved texture sampler declared by the shaders loaded so far. Read off the shader source, so
        /// unlike a shader's own <see cref="Shader.ReservedTexturesUsed"/> it is known before the program links, and
        /// it includes samplers behind a combo the linker went on to drop. Grow only, so a renderer can pick up what
        /// was added since it last looked; see <see cref="MaterialLoader.ShaderTextures"/>.
        /// </summary>
        public HashSet<string> DeclaredReservedTextures { get; } = [];

        private static readonly Dictionary<string, byte> EmptyArgs = [];
        private static readonly Lock ParserLock = new();
        private static readonly Dictionary<(ShaderFlavour Flavour, string Name), ParsedShaderData> ParsedCache = [];

        /// <summary>
        /// Gets or sets the GLSL dialect shaders are generated in. Set it before any shader is parsed; sources
        /// already in the cache keep the dialect they were parsed with.
        /// </summary>
        public static ShaderFlavour Flavour { get; set; } = ShaderFlavour.OpenGL;

        private static readonly ShaderParser Parser = new();

        private readonly RendererContext RendererContext;

        // Keyed by Shader identity. The modules cannot live on Shader itself: that type is the OpenGL
        // program wrapper and is owned elsewhere, so the mapping is held beside it here.
        private readonly Dictionary<Shader, Dictionary<ShaderProgramType, IShaderModule>> SpirvModules = [];

        /// <summary>
        /// Gets a value indicating whether shaders are compiled to SPIR-V rather than through OpenGL.
        /// </summary>
        /// <remarks>
        /// Keyed off the device actually in use, not off <see cref="Flavour"/>. The dialect is a
        /// property of the generated source; whether the result is handed to <c>glCompileShader</c> or
        /// to <see cref="IDevice.CreateShaderModule"/> is a property of the backend, and only the
        /// backend can decide it. A device that is absent means the OpenGL path, which is what every
        /// existing caller already relies on.
        /// </remarks>
        public bool UseSpirvPath => RendererContext.Device?.Backend == RhiBackend.Vulkan;

        /// <summary>
        /// Gets the dialect this loader preprocesses into: Vulkan GLSL whenever the device is Vulkan,
        /// otherwise whatever <see cref="Flavour"/> selects.
        /// </summary>
        private ShaderFlavour EffectiveFlavour => UseSpirvPath ? ShaderFlavour.Vulkan : Flavour;

        /// <summary>Gets the shader modules compiled for a shader, or <see langword="null"/> on the OpenGL path.</summary>
        /// <param name="shader">The shader to look up.</param>
        /// <returns>One module per stage the shader declares.</returns>
        /// <remarks>What a pipeline is built from. The modules are owned by this loader and are
        /// destroyed with it.</remarks>
        public IReadOnlyDictionary<ShaderProgramType, IShaderModule>? GetShaderModules(Shader shader)
            => SpirvModules.GetValueOrDefault(shader);

        /// <summary>
        /// Preprocessed shader source with defines, uniforms, and compiled stage code.
        /// </summary>
        public class ParsedShaderData
        {
            /// <summary>Gets the GLSL dialect this shader was preprocessed into.</summary>
            public ShaderFlavour Flavour { get; init; }

            /// <summary>Gets the map of define names to their default byte values extracted from the shader source.</summary>
            public Dictionary<string, byte> Defines { get; } = [];

            /// <summary>
            /// Gets the stage-to-stage varyings declared by any stage, by name. Collected for the Vulkan flavour only,
            /// which has to give each one an explicit location.
            /// </summary>
            public Dictionary<string, ShaderVarying> Varyings { get; } = [];

            /// <summary>Gets the first interface location allocated to each varying in <see cref="Varyings"/>.</summary>
            public Dictionary<string, int> VaryingLocations { get; } = [];

            /// <summary>
            /// Gets the binding number given to each sampler that a material supplies, within
            /// <see cref="VulkanGlsl.MaterialTextureSet"/>. Numbered per shader, in declaration order.
            /// </summary>
            public Dictionary<string, int> MaterialTextureBindings { get; } = [];

            /// <summary>
            /// Gets the constructs found in the source that Vulkan GLSL will not accept. Empty for the OpenGL
            /// flavour, which accepts all of them.
            /// </summary>
            public List<string> VulkanDiagnostics { get; } = [];

            /// <summary>Gets the set of render mode names declared in the shader source.</summary>
            public HashSet<string> RenderModes { get; } = [];

            /// <summary>Gets the set of uniform names declared in the shader source.</summary>
            public HashSet<string> Uniforms { get; } = [];

            /// <summary>
            /// Gets the <c>#extension</c> directives hoisted out of the shader source. They have to precede
            /// every non-preprocessor token, and the packed uniform block is one, so the header emits them.
            /// </summary>
            public HashSet<string> Extensions { get; } = [];

            /// <summary>Gets the packable uniform declarations collected from every stage, in source order.</summary>
            public List<GlobalsDeclaration> GlobalsDeclarations { get; } = [];

            /// <summary>
            /// Gets the packed layout of <see cref="GlobalsDeclarations"/>. Built once all stages have been
            /// preprocessed, and shared by every static combo variant compiled from this source.
            /// </summary>
            public GlobalsLayout GlobalsLayout { get; set; } = GlobalsLayout.Empty;

            /// <summary>Gets the set of uniform names annotated with <c>// SrgbRead(true)</c>.</summary>
            public HashSet<string> SrgbUniforms { get; } = [];

            /// <summary>Gets the set of sampler uniform names annotated with <c>// Sampler(UserConfig)</c>.</summary>
            public HashSet<string> SamplerUserConfigUniforms { get; } = [];

            /// <summary>
            /// Gets the reserved texture samplers declared in the shader source. A superset of what the linker
            /// keeps, since the source is read before any combo is resolved; see <see cref="Shader.ReservedTexturesUsed"/>.
            /// </summary>
            public HashSet<string> ReservedTextures { get; } = [];

            /// <summary>Gets the preprocessed GLSL source text for each shader stage.</summary>
            public Dictionary<ShaderProgramType, string> Sources { get; } = [];

            /// <summary>Gets the ordered list of source file names included during preprocessing, used for error reporting.</summary>
            public List<string> SourceFiles { get; } = [];
#if DEBUG
            /// <summary>Gets the per-file line lists used to display source lines in error messages (debug builds only).</summary>
            public List<List<string>> SourceFileLines { get; } = [];
#endif
        }

        static ShaderLoader()
        {
            Task.Run(() =>
            {
                try
                {
                    foreach (var shader in Parser.AvailableShaders.Keys)
                    {
                        // Warms the cache for the statically selected dialect. A Vulkan device warms its
                        // own entries on first load; the cache is keyed by dialect so neither evicts the
                        // other.
                        GetOrParseShader(shader, Flavour);
                    }
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e.ToString());
                    Parser.ClearBuilder();

#if DEBUG
                    System.Diagnostics.Debugger.Break();
#endif
                }
            });
        }

        /// <summary>Initializes a new instance of the <see cref="ShaderLoader"/> class.</summary>
        /// <param name="rendererContext">The renderer context that owns this loader.</param>
        public ShaderLoader(RendererContext rendererContext)
        {
            RendererContext = rendererContext;
        }

        /// <summary>Loads or retrieves a cached shader compiled with the specified static combos.</summary>
        /// <param name="shaderName">The Source 2 shader name (e.g. <c>complex.vfx</c>), or a renderer shader file name that must exist (e.g. <c>grid</c>). See <see cref="GetShaderFileByName"/>.</param>
        /// <param name="combos">Static combo name/value pairs to activate.</param>
        public Shader LoadShader(string shaderName, params (string ComboName, byte ComboValue)[] combos)
        {
            var args = combos.ToDictionary(c => c.ComboName, c => c.ComboValue);
            return LoadShader(shaderName, args);
        }

        /// <summary>Loads or retrieves a cached shader compiled with the given argument dictionary.</summary>
        /// <param name="shaderName">The Source 2 shader name (e.g. <c>complex.vfx</c>), or a renderer shader file name that must exist (e.g. <c>grid</c>). See <see cref="GetShaderFileByName"/>.</param>
        /// <param name="arguments">Static combo parameter overrides, or <see langword="null"/> for defaults.</param>
        /// <param name="blocking">When <see langword="true"/>, waits for linking to complete before returning.</param>
        public Shader LoadShader(string shaderName, IReadOnlyDictionary<string, byte>? arguments = null, bool blocking = true)
        {
            arguments ??= EmptyArgs;

            var shaderFileName = GetShaderFileByName(shaderName);
            var parsedData = GetOrParseShader(shaderFileName, EffectiveFlavour);
            var shaderCacheHash = CalculateShaderCacheHash(shaderName, parsedData.Defines, arguments);

            if (CachedShaders.TryGetValue(shaderCacheHash, out var cachedShader))
            {
                return cachedShader;
            }

            var shader = CompileAndLinkShader(shaderName, shaderFileName, parsedData, arguments, blocking: blocking);
            CachedShaders[shaderCacheHash] = shader;
            return shader;
        }

        /// <summary>
        /// Collects the link status of every shader loaded so far, which materials load without waiting for.
        /// Until this runs a shader is linked by the first draw that uses it, and a draw call is queued before that
        /// happens: run it before rendering a frame that has to see each shader's final state, such as a pre-warm
        /// pass. Must be called on the thread holding the GL context.
        /// </summary>
        /// <remarks>
        /// A no-op on the SPIR-V path. There is nothing to link: a SPIR-V module is complete when it is
        /// created, and what joins the stages together is the pipeline, built later from the modules.
        /// Calling <see cref="Shader.EnsureLoaded"/> there would be a <c>glGetProgram</c> on a program
        /// that does not exist.
        /// </remarks>
        public void LinkLoadedShaders()
        {
            if (UseSpirvPath)
            {
                return;
            }

            foreach (var shader in CachedShaders.Values)
            {
                if (!shader.EnsureLoaded())
                {
                    GL.GetProgramInfoLog(shader.Program, out var log);
                    RendererContext.Logger.LogError("Shader '{ShaderName}' failed to link: {Log}", shader.Name, log);
                }
            }
        }

        /// <summary>
        /// Drops all preprocessed shader sources and rediscovers the available shader files. Called when the
        /// <see cref="ShaderRegistry"/> changes; shader programs that have already been compiled are not affected.
        /// </summary>
        internal static void InvalidateParsedShaders()
        {
            using var _ = ParserLock.EnterScope();

            ParsedCache.Clear();
            Parser.ClearBuilder();
            Parser.RefreshAvailableShaders();
        }

        private static ParsedShaderData GetOrParseShader(string shaderFileName, ShaderFlavour flavour)
        {
            using var _ = ParserLock.EnterScope();

            if (ParsedCache.TryGetValue((flavour, shaderFileName), out var cached))
            {
                return cached;
            }

            var parsedData = new ParsedShaderData { Flavour = flavour };

            var availableStages = Parser.AvailableShaders.GetValueOrDefault(shaderFileName)
                ?? throw new FileNotFoundException($"Shader '{shaderFileName}' does not exist.");

            if (availableStages.Length == 0
            || availableStages[(int)ShaderProgramType.Vertex] == false && availableStages[(int)ShaderProgramType.Compute] == false)
            {
                throw new InvalidDataException($"Shader '{shaderFileName}' does not have a vertex or compute stage.");
            }

            foreach (var (@type, extension) in ShaderParser.ProgramTypeToExtension)
            {
                if (!availableStages[(int)@type])
                {
                    continue;
                }

                var nameWithExtension = $"{shaderFileName}.{extension}.slang";

                var shaderSource = Parser.PreprocessShader(nameWithExtension, parsedData);
                parsedData.Sources[@type] = shaderSource;
                Parser.ClearBuilder();
            }

            parsedData.GlobalsLayout = GlobalsLayout.Build(parsedData.GlobalsDeclarations);

            // Varyings can only be located once every stage that shares them has been read
            ShaderParser.StampInterfaceLocations(parsedData);

            ParsedCache[(flavour, shaderFileName)] = parsedData;
            return parsedData;
        }

        /// <summary>
        /// Trims the stages actually compiled for a shader. <c>depth_only</c> with no combos writes
        /// depth alone, so it has no fragment stage to compile.
        /// </summary>
        private static Dictionary<ShaderProgramType, string> SelectStages(string shaderName, ParsedShaderData parsedData, IReadOnlyDictionary<string, byte> arguments)
        {
            var sources = parsedData.Sources;

            if (shaderName == "depth_only" && arguments.Count == 0)
            {
                sources = new(sources);
                sources.Remove(ShaderProgramType.Fragment);
            }

            return sources;
        }

        private Shader CompileAndLinkShader(string shaderName, string shaderFileName, ParsedShaderData parsedData, IReadOnlyDictionary<string, byte> arguments, bool blocking = true)
        {
            if (UseSpirvPath)
            {
                return CompileSpirvShader(shaderName, shaderFileName, parsedData, arguments);
            }

            var shaderProgram = -1;

            try
            {
                var sources = SelectStages(shaderName, parsedData, arguments);

                static ShaderType ToShaderType(ShaderProgramType type) => type switch
                {
                    ShaderProgramType.Vertex => ShaderType.VertexShader,
                    ShaderProgramType.Fragment => ShaderType.FragmentShader,
                    ShaderProgramType.Compute => ShaderType.ComputeShader,
                    _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
                };

                var shaderObjects = new int[sources.Count];
                var shaderSources = new string[sources.Count];
                var s = 0;
                foreach (var (stage, source) in sources)
                {
                    shaderObjects[s] = GL.CreateShader(ToShaderType(stage));
                    shaderSources[s] = source!;
                    s++;
                }

                CompileShaderObjects(shaderObjects, shaderSources, shaderFileName, shaderName, arguments, parsedData);

                shaderProgram = GL.CreateProgram();

#if DEBUG
                GL.ObjectLabel(ObjectLabelIdentifier.Program, shaderProgram, shaderFileName.Length, shaderFileName);
                for (var i = 0; i < shaderObjects.Length; i++)
                {
                    GL.ObjectLabel(ObjectLabelIdentifier.Shader, shaderObjects[i], shaderFileName.Length, shaderFileName);
                }
#endif

                // What the source declares is known before the program links, and the renderer needs it that
                // early to have a texture bound by the first draw that samples it. Only ever grows.
                DeclaredReservedTextures.UnionWith(parsedData.ReservedTextures);

                var shader = new Shader(shaderName, RendererContext)
                {
#if DEBUG
                    FileName = shaderFileName,
#endif

                    Parameters = arguments,
                    GlobalsLayout = parsedData.GlobalsLayout,
                    Program = shaderProgram,
                    ShaderObjects = shaderObjects,
                    RenderModes = parsedData.RenderModes,
                    UniformNames = parsedData.Uniforms,
                    SrgbUniforms = parsedData.SrgbUniforms,
                    SamplerUserConfigUniforms = parsedData.SamplerUserConfigUniforms,

                    // Copied, because the parsed data is shared by every variant of this shader while the
                    // linker trims each variant's own set down to what its combos kept.
                    ReservedTexturesUsed = [.. parsedData.ReservedTextures],
                };

                foreach (var shaderObj in shaderObjects)
                {
                    GL.AttachShader(shader.Program, shaderObj);
                }

                GL.LinkProgram(shader.Program);

                // Not getting link status straight away allows the driver to perform parallelized shader compilation
                // TODO: Ideally we want this to work for initial load too.
                if (blocking)
                {
                    if (!shader.EnsureLoaded())
                    {
                        GL.GetProgramInfoLog(shader.Program, out var log);
                        ThrowShaderError(log, string.Concat(shaderFileName, GetArgumentDescription(arguments)), shaderName, "Failed to link shader", parsedData);
                    }
                }

                var argsDescription = GetArgumentDescription(SortAndFilterArguments(parsedData.Defines, arguments));
                var compiledStatus = blocking ? " and linked" : string.Empty;

                // Only Valve shader names are resolved to a different file, so naming both would be noise
                if (IsVfxShaderName(shaderName))
                {
                    RendererContext.Logger.LogInformation("Shader '{ShaderName}' as '{ShaderFileName}'{ArgsDescription} compiled{CompiledStatus} successfully (program={Program})", shaderName, shaderFileName, argsDescription, compiledStatus, shader.Program);
                }
                else
                {
                    RendererContext.Logger.LogInformation("Shader '{ShaderName}'{ArgsDescription} compiled{CompiledStatus} successfully (program={Program})", shaderName, argsDescription, compiledStatus, shader.Program);
                }

                return shader;
            }
            catch (ShaderCompilerException)
            {
                if (shaderProgram > -1)
                {
                    GL.DeleteProgram(shaderProgram);
                }

                throw;
            }
        }

        /// <summary>
        /// Compiles every stage to SPIR-V and creates a shader module per stage on the device.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This path issues no OpenGL call at all, which is the point: on a Vulkan device there is no GL
        /// context for <c>glCompileShader</c> to run against, and the golden suite's Vulkan run died at
        /// four separate load stages for exactly that reason.
        /// </para>
        /// <para>
        /// There is no link step. A Vulkan pipeline is what binds stages together, and it is created
        /// later from these modules; that is why the modules are registered with the pipeline device
        /// here, since <see cref="IDevice.CreateShaderModule"/> keeps only a handle and a hash and a
        /// pipeline layout cannot be derived from that alone.
        /// </para>
        /// </remarks>
        /// <summary>
        /// Finds whatever will record a module's SPIR-V interface for this device, or
        /// <see langword="null"/> when nothing will.
        /// </summary>
        /// <remarks>
        /// The device is asked whether it can register, rather than what it is, because that is the only
        /// probe that survives being wrapped or composed — and both happen: the golden harness decorates
        /// its device to count calls, and composes one that forwards pipeline creation. A concrete type
        /// test was tried and could never succeed through either.
        /// </remarks>
        private static ISpirvModuleRegistry? ResolveModuleRegistry(IDevice device)
            => device as ISpirvModuleRegistry;

        private bool WarnedAboutMissingRegistry;

        /// <summary>
        /// Creates one stage's module and records its interface, so a pipeline can later derive a layout
        /// from it.
        /// </summary>
        /// <remarks>
        /// Creation goes through <see cref="IDevice.CreateShaderModule"/> in both cases rather than
        /// through the pipeline device's combined call, so the device the renderer was handed is the
        /// device that owns the module. Registration is then a separate step, which is exactly the case
        /// the pipeline layer documents that second entry point for.
        /// </remarks>
        private IShaderModule CreateModule(IDevice device, ISpirvModuleRegistry? registry, ReadOnlySpan<byte> spirv, ShaderStage stage, string debugName)
        {
            var module = device.CreateShaderModule(spirv, stage, debugName);

            if (registry != null)
            {
                try
                {
                    registry.RegisterModuleInterface(module, spirv);
                }
                catch
                {
                    module.Dispose();
                    throw;
                }

                return module;
            }

            // Warn once rather than per module: without this the only symptom is a pipeline refusing to
            // be built much later, naming a call site that did nothing wrong.
            if (!WarnedAboutMissingRegistry)
            {
                WarnedAboutMissingRegistry = true;

                RendererContext.Logger.LogError(
                    "{Device} does not expose {Registry}, so no shader module's SPIR-V interface can be recorded and every pipeline built from one will refuse to be created. The device must implement {Registry}, forwarding to the pipeline device it wraps or composes.",
                    device.GetType().Name, nameof(ISpirvModuleRegistry), nameof(ISpirvModuleRegistry));
            }

            return module;
        }

        private Shader CompileSpirvShader(string shaderName, string shaderFileName, ParsedShaderData parsedData, IReadOnlyDictionary<string, byte> arguments)
        {
            var device = RendererContext.Device
                ?? throw new ShaderCompilerException($"Cannot compile '{shaderName}' to SPIR-V because the renderer context has no device.");

            var sources = SelectStages(shaderName, parsedData, arguments);
            var headerText = BuildHeader(parsedData, shaderName, arguments);
            var sourceMap = new SpirvSourceMap(parsedData.SourceFiles);
            var describedFile = string.Concat(shaderFileName, GetArgumentDescription(arguments));
            var registry = ResolveModuleRegistry(device);

            var modules = new Dictionary<ShaderProgramType, IShaderModule>(sources.Count);

            try
            {
                foreach (var (stage, source) in sources)
                {
                    var rhiStage = SpirvCompiler.ToShaderStage(stage);
                    var debugName = string.Create(CultureInfo.InvariantCulture, $"{shaderFileName}.{ShaderParser.ProgramTypeToExtension[stage]}");

                    var result = SpirvCompiler.Shared.Compile(source, rhiStage, new SpirvCompileOptions
                    {
                        FileName = string.Create(CultureInfo.InvariantCulture, $"{shaderFileName}.{ShaderParser.ProgramTypeToExtension[stage]}.slang"),
                        Header = headerText,
                        SourceMap = sourceMap,
                        Target = SpirvTargetEnvironment.Vulkan13,
                    });

                    if (!result.Success)
                    {
                        ThrowSpirvError(result, describedFile, shaderName, "Failed to set up shader", parsedData);
                    }

                    modules[stage] = CreateModule(device, registry, result.Spirv.Span, rhiStage, debugName);
                }
            }
            catch
            {
                foreach (var module in modules.Values)
                {
                    module.Dispose();
                }

                throw;
            }

            // What the source declares is known before any pipeline exists, and the renderer needs it
            // that early to have a texture bound by the first draw that samples it. Only ever grows.
            DeclaredReservedTextures.UnionWith(parsedData.ReservedTextures);

            var shader = new Shader(shaderName, RendererContext)
            {
#if DEBUG
                FileName = shaderFileName,
#endif

                Parameters = arguments,
                GlobalsLayout = parsedData.GlobalsLayout,

                // There is no GL program or GL shader object behind a SPIR-V module. Leaving these at
                // zero rather than inventing a handle keeps any accidental GL call an obvious no-op
                // instead of corrupting an unrelated object.
                Program = 0,
                ShaderObjects = [],

                RenderModes = parsedData.RenderModes,
                UniformNames = parsedData.Uniforms,
                SrgbUniforms = parsedData.SrgbUniforms,
                SamplerUserConfigUniforms = parsedData.SamplerUserConfigUniforms,
                ReservedTexturesUsed = [.. parsedData.ReservedTextures],
            };

            SpirvModules[shader] = modules;

            var argsDescription = GetArgumentDescription(SortAndFilterArguments(parsedData.Defines, arguments));

            if (IsVfxShaderName(shaderName))
            {
                RendererContext.Logger.LogInformation("Shader '{ShaderName}' as '{ShaderFileName}'{ArgsDescription} compiled to SPIR-V ({StageCount} stage(s))", shaderName, shaderFileName, argsDescription, modules.Count);
            }
            else
            {
                RendererContext.Logger.LogInformation("Shader '{ShaderName}'{ArgsDescription} compiled to SPIR-V ({StageCount} stage(s))", shaderName, argsDescription, modules.Count);
            }

            return shader;
        }

        /// <summary>
        /// Reports a SPIR-V compile failure, mapped back to the shader file and line the author wrote.
        /// </summary>
        /// <remarks>
        /// glslang's log is worded differently from every GL driver's, so the three driver patterns do
        /// not match it and <see cref="SpirvDiagnosticParser"/> does the locating instead. The failure
        /// then reads the same and annotates CI the same as the OpenGL path, which is what keeps hot
        /// reload pointing at the edited line on either backend.
        /// </remarks>
        private static void ThrowSpirvError(SpirvCompilationResult result, string shaderFile, ReadOnlySpan<char> originalShaderName, string errorType, ParsedShaderData parsedData)
        {
            var primary = result.PrimaryError;
            var info = result.FormatDiagnostics();

            if (string.IsNullOrWhiteSpace(info))
            {
                info = string.Create(CultureInfo.InvariantCulture, $"The shader compiler reported {result.Status} without a message.");
            }

            ThrowLocatedShaderError(
                info,
                primary?.SourceFile,
                primary?.SourceFileIndex ?? -1,
                primary?.Line ?? -1,
                shaderFile,
                originalShaderName,
                errorType,
                parsedData);
        }

        /// <summary>
        /// Builds the preamble prepended to every stage: the version, the hoisted extensions, the
        /// resolved static combo defines, the packed globals block for the flavour, and for Vulkan the
        /// per-draw push constant block.
        /// </summary>
        /// <param name="parsedData">The preprocessed shader.</param>
        /// <param name="originalShaderName">The name the shader was requested under, which decides
        /// which <c>GameVfx_</c> variant define is turned on.</param>
        /// <param name="arguments">Static combo overrides.</param>
        /// <returns>The header text.</returns>
        /// <remarks>
        /// Shared by the OpenGL and SPIR-V paths, and by <see cref="SpirvShaderValidation"/>, so that
        /// what validation measures is what the renderer compiles. It used to be rebuilt in each of the
        /// three, which is how the flavour ever managed to differ between them.
        /// </remarks>
        internal static string BuildHeader(ParsedShaderData parsedData, string originalShaderName, IReadOnlyDictionary<string, byte> arguments)
        {
            var header = new StringBuilder();
            header.Append(ShaderParser.ExpectedShaderVersion);
            header.Append('\n');

            header.Append("#extension GL_KHR_shader_subgroup_arithmetic : enable\n");
            header.Append("#extension GL_KHR_shader_subgroup_vote : enable\n");

            foreach (var extension in parsedData.Extensions)
            {
                header.Append(extension);
                header.Append('\n');
            }

            // Only Valve shader names activate a shader variant, renderer shader files are loaded as themselves
            var variantName = IsVfxShaderName(originalShaderName)
                ? $"GameVfx_{Path.GetFileNameWithoutExtension(originalShaderName)}"
                : null;

            // Add all defines (with argument overrides or defaults)
            foreach (var (defineName, defaultValue) in parsedData.Defines)
            {
                var value = defaultValue;

                if (defineName == variantName)
                {
                    value = 1;
                }
                else if (arguments.TryGetValue(defineName, out var argValue))
                {
                    value = argValue;
                }

                header.Append("#define ");
                header.Append(defineName);
                header.Append(' ');
                header.Append(value.ToString(CultureInfo.InvariantCulture));
                header.Append('\n');
            }

            header.Append(parsedData.GlobalsLayout.GetBlockSource(parsedData.Flavour));

            if (parsedData.Flavour == ShaderFlavour.Vulkan)
            {
                // The per-draw set, which GL sets one glProgramUniform at a time
                header.Append(VulkanGlsl.PushConstantBlockSource);
            }

            return header.ToString();
        }

        private static void CompileShaderObjects(int[] shaderObjects, string[] shaderSources, string shaderFile, string originalShaderName, IReadOnlyDictionary<string, byte> arguments, ParsedShaderData parsedData)
        {
            var headerText = BuildHeader(parsedData, originalShaderName, arguments);

            for (var i = 0; i < shaderObjects.Length; i++)
            {
                CompileShaderObject(shaderObjects[i], shaderFile, originalShaderName, arguments, headerText, shaderSources[i], parsedData);
            }
        }

        private static void CompileShaderObject(int shader, string shaderFile, ReadOnlySpan<char> originalShaderName, IReadOnlyDictionary<string, byte> arguments, string headerText, string shaderText, ParsedShaderData parsedData)
        {
            string[] sources = [headerText, shaderText];
            int[] lengths = [sources[0].Length, sources[1].Length];

            GL.ShaderSource(shader, sources.Length, sources, lengths);

            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var shaderStatus);

            if (shaderStatus != 1)
            {
                GL.GetShaderInfoLog(shader, out var log);

                ThrowShaderError(log, string.Concat(shaderFile, GetArgumentDescription(arguments)), originalShaderName, "Failed to set up shader", parsedData);
            }
        }

        private static void ThrowShaderError(string info, string shaderFile, ReadOnlySpan<char> originalShaderName, string errorType, ParsedShaderData parsedData)
        {
            // Attempt to parse error message to get the line number so we can print the actual line
            var errorMatch = NvidiaGlslError().Match(info);

            if (!errorMatch.Success)
            {
                errorMatch = AmdGlslError().Match(info);
            }

            if (!errorMatch.Success)
            {
                errorMatch = Mesa3dGlslError().Match(info);
            }

            string? sourceFile = null;
            var errorLine = -1;
            var errorSourceFile = -1;

            if (errorMatch.Success)
            {
                errorSourceFile = int.Parse(errorMatch.Groups["SourceFile"].Value, CultureInfo.InvariantCulture);
                errorLine = int.Parse(errorMatch.Groups["Line"].Value, CultureInfo.InvariantCulture);

                if (errorSourceFile >= 0 && errorSourceFile < parsedData.SourceFiles.Count)
                {
                    sourceFile = parsedData.SourceFiles[errorSourceFile];
                }
            }

            ThrowLocatedShaderError(info, sourceFile, errorSourceFile, errorLine, shaderFile, originalShaderName, errorType, parsedData);
        }

        /// <summary>
        /// Reports a compile or link failure whose location has already been resolved to a shader file
        /// and line, whichever compiler resolved it.
        /// </summary>
        /// <remarks>
        /// The GL drivers and glslang word their logs differently and are matched by different patterns,
        /// but a located failure has to read the same and produce the same CI annotation either way,
        /// because hot reload and the CI log both consume this.
        /// </remarks>
        private static void ThrowLocatedShaderError(string info, string? sourceFile, int errorSourceFile, int errorLine, string shaderFile, ReadOnlySpan<char> originalShaderName, string errorType, ParsedShaderData parsedData)
        {
#if DEBUG
            // Output GitHub Actions annotation https://docs.github.com/en/actions/reference/workflow-commands-for-github-actions
            if (IsCI)
            {
                var annotation = "::error ";

                if (sourceFile != null)
                {
                    annotation += $"file={ShaderParser.ShaderDirectory.Replace('.', '/')}{sourceFile},";
                    if (errorLine > 0)
                    {
                        annotation += $"line={errorLine},";
                    }
                }

                var errorMessage = $"{info}\n({shaderFile}, original={originalShaderName})";
                annotation += $"title={nameof(ShaderCompilerException)}::{errorMessage.Replace("\n", "%0A", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal)}";

                Console.WriteLine(annotation);
            }
#endif

            if (sourceFile != null)
            {
                info += $"\nError in {sourceFile} on line {errorLine}";

#if DEBUG
                if (errorLine > 0 && errorLine <= parsedData.SourceFileLines[errorSourceFile].Count)
                {
                    info += $":\n{parsedData.SourceFileLines[errorSourceFile][errorLine - 1]}\n";
                }
#endif
            }

            throw new ShaderCompilerException($"{errorType} {shaderFile} (original={originalShaderName}):\n\n{info}");
        }

        /// <summary>Returns the bare shader name from a full shader file path by stripping the trailing stage extension (<c>.vert.slang</c>, <c>.frag.slang</c>, or <c>.comp.slang</c>).</summary>
        /// <param name="shaderFilePath">The full path or file name of the shader (e.g. <c>/path/complex.vert.slang</c>).</param>
        /// <returns>The shader name without directory or extension (e.g. <c>complex</c>).</returns>
        public static string ShaderNameFromPath(string shaderFilePath)
        {
            return Path.GetFileName(shaderFilePath[..^ShaderFileExtension.Length]);
        }

        /// <summary>The file extension for Slang shader source files (<c>.slang</c>).</summary>
        public const string SlangExtension = ".slang";

        /// <summary>The file extension used to identify vertex shader entry points (<c>.vert.slang</c>).</summary>
        public const string ShaderFileExtension = ".vert.slang";
        // No longer used by the renderer itself, kept so that names that were required before still resolve
        const string VrfInternalShaderPrefix = "vrf.";

        /// <summary>
        /// Resolves a shader name to the renderer shader file that draws it. Mappings registered in
        /// <see cref="ShaderRegistry"/> take priority over the built-in ones.
        /// </summary>
        /// <param name="shaderName">
        /// A Source 2 shader name ending in <c>.vfx</c>, which is mapped to the renderer shader that best matches it and
        /// falls back to <c>complex</c> when it is unknown. Any other name is the renderer shader file to load directly,
        /// and must exist.
        /// </param>
        /// <returns>The renderer shader name without stage or extension (e.g. <c>complex</c>).</returns>
        public static string GetShaderFileByName(string shaderName)
        {
            if (ShaderRegistry.Mappings.TryGetValue(shaderName, out var customShaderFile))
            {
                return customShaderFile;
            }

            // TODO: Consider naming renderer shaders with a .slang extension, so that they read as explicitly as .vfx names do
            if (!IsVfxShaderName(shaderName))
            {
                // Not a Valve shader name, so it names a renderer shader file directly.
                // Unknown names are not silently drawn with 'complex', loading them throws instead.
                return shaderName.StartsWith(VrfInternalShaderPrefix, StringComparison.Ordinal)
                    ? shaderName[VrfInternalShaderPrefix.Length..]
                    : shaderName;
            }

            return GetBuiltinShaderFileByName(shaderName);
        }

        /// <summary>The file extension of Source 2 shader names (<c>.vfx</c>).</summary>
        public const string VfxExtension = ".vfx";

        private static bool IsVfxShaderName(string shaderName)
        {
            return shaderName.EndsWith(VfxExtension, StringComparison.Ordinal);
        }

        // Map Valve's shader names to shader files VRF has
        private static string GetBuiltinShaderFileByName(string shaderName) => shaderName switch
        {
            "sky.vfx" => "sky",
            "tools_sprite.vfx" => "sprite",
            "global_lit_simple.vfx" => "global_lit_simple",
            "vr_black_unlit.vfx" or "csgo_black_unlit.vfx" => "vr_black_unlit",
            "vr_unlit.vfx" => "vr_unlit",
            "vr_standard.vfx" => "vr_standard",
            "water_dota.vfx" => "water",
            "csgo_water_fancy.vfx" => "water_csgo",
            "hero.vfx" or "hero_underlords.vfx" => "dota_hero",
            "multiblend.vfx" => "multiblend",
            "csgo_effects.vfx" => "csgo_effects",
            "csgo_environment.vfx" or "csgo_environment_blend.vfx" => "csgo_environment",
            "environment_blend.vfx" => "environment_blend",
            "pbr.vfx" => "pbr",
            "citadel_overlay.vfx" => "citadel_overlay",

            _ => "complex",
        };

        /// <inheritdoc/>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>Releases managed resources held by this loader.</summary>
        /// <param name="disposing"><see langword="true"/> when called from <see cref="Dispose()"/>.</param>
        protected virtual void Dispose(bool disposing)
        {
            foreach (var shader in CachedShaders.Values)
            {
                shader.Default.Delete();
            }

            CachedShaders.Clear();

            foreach (var modules in SpirvModules.Values)
            {
                foreach (var module in modules.Values)
                {
                    module.Dispose();
                }
            }

            SpirvModules.Clear();
        }

        private static IEnumerable<KeyValuePair<string, byte>> SortAndFilterArguments(Dictionary<string, byte> defines, IReadOnlyDictionary<string, byte> arguments)
        {
            return arguments
                .Where(p => defines.ContainsKey(p.Key))
                .Where(static p => p.Value != 0) // Shader defines should already default to zero
                .OrderBy(static p => p.Key);
        }

        private static string GetArgumentDescription(IEnumerable<KeyValuePair<string, byte>> arguments)
        {
            var sb = new StringBuilder();
            var first = true;

            foreach (var param in arguments)
            {
                if (first)
                {
                    first = false;
                    sb.Append(" (");
                }
                else
                {
                    sb.Append(", ");
                }

                sb.Append(param.Key);

                if (param.Value != 1)
                {
                    sb.Append('=');
                    sb.Append(param.Value);
                }
            }

            if (!first)
            {
                sb.Append(')');
            }

            return sb.ToString();
        }

        private static readonly byte[] NewLineArray = "\n"u8.ToArray();

        private static ulong CalculateShaderCacheHash(string shaderName, Dictionary<string, byte> defines, IReadOnlyDictionary<string, byte> arguments)
        {
            var hash = new XxHash3(StringToken.MURMUR2SEED);
            hash.Append(MemoryMarshal.AsBytes(shaderName.AsSpan()));

            var argsOrdered = SortAndFilterArguments(defines, arguments);
            Span<byte> valueSpan = stackalloc byte[1];

            foreach (var (key, value) in argsOrdered)
            {
                hash.Append(NewLineArray);
                hash.Append(MemoryMarshal.AsBytes(key.AsSpan()));
                hash.Append(NewLineArray);

                valueSpan[0] = value;
                hash.Append(valueSpan);
            }

            return hash.GetCurrentHashAsUInt64();
        }

#if DEBUG
        /// <summary>Recompiles all cached shaders, or only those derived from the given file if specified (debug builds only).</summary>
        /// <param name="name">Optional shader file name that changed; when <see langword="null"/> all shaders are reloaded.</param>
        public void ReloadAllShaders(string? name = null)
        {
            Parser.ClearBuilder();

            // Picks up shader files that were created after startup, including ones in mounted directories
            Parser.RefreshAvailableShaders();

            if (name != null && ShaderParser.ExtensionToProgramType.Keys.Any(ext => name.EndsWith($".{ext}.slang", StringComparison.Ordinal)))
            {
                // If a named shader changed (not an include), then we can only reload this shader
                name = ShaderNameFromPath(name!);

                foreach (var flavour in Enum.GetValues<ShaderFlavour>())
                {
                    ParsedCache.Remove((flavour, name!));
                }
            }
            else
            {
                // Otherwise reload all shaders (common, etc)
                ParsedCache.Clear();
                name = null;
            }

            foreach (var shader in CachedShaders.Values)
            {
                if (name != null && shader.FileName != name)
                {
                    continue;
                }

                var fileName = GetShaderFileByName(shader.Name);
                var parsed = GetOrParseShader(fileName, EffectiveFlavour);
                var newShader = CompileAndLinkShader(shader.Name, fileName, parsed, shader.Parameters, blocking: false);

                if (UseSpirvPath)
                {
                    ReplaceSpirvModules(shader, newShader);
                    continue;
                }

                shader.ReplaceWith(newShader);
            }
        }

        /// <summary>
        /// Moves a freshly compiled shader's modules onto the instance the renderer already holds, and
        /// destroys the modules it replaces.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="Shader.ReplaceWith"/> cannot be used here: it opens with a <c>glDeleteProgram</c>,
        /// and on a Vulkan device there is neither a program to delete nor a context to delete it in.
        /// The state it copies across is instead already shared, because both instances were built from
        /// the same <see cref="ParsedShaderData"/>; only the modules differ.
        /// </para>
        /// <para>
        /// <b>Pipelines built from the old modules are not invalidated by this.</b> A pipeline holds the
        /// modules it was created from, and nothing here tells the pipeline cache to drop them, so a
        /// reloaded shader does not reach the screen until pipeline invalidation is wired. Reload
        /// recompiles and reports diagnostics correctly on Vulkan today; it does not yet redraw.
        /// </para>
        /// </remarks>
        private void ReplaceSpirvModules(Shader shader, Shader newShader)
        {
            if (!SpirvModules.Remove(newShader, out var replacement))
            {
                return;
            }

            if (SpirvModules.TryGetValue(shader, out var previous))
            {
                foreach (var module in previous.Values)
                {
                    RendererContext.Device?.DeferredDestroy(module);
                }
            }

            SpirvModules[shader] = replacement;

            RendererContext.Logger.LogInformation(
                "Shader '{ShaderName}' recompiled to SPIR-V; pipelines built from the previous modules are not invalidated yet", shader.Name);
        }

        /// <summary>Compiles every known shader (and all their define combinations) to validate correctness (debug builds only).</summary>
        /// <param name="progressReporter">Receives status messages as each shader variant is compiled.</param>
        /// <param name="logger">Logger used when constructing the internal <see cref="RendererContext"/>.</param>
        /// <param name="filter">Optional substring to restrict which shader files are validated.</param>
        public static void ValidateShaders(IProgress<string> progressReporter, ILogger logger, string? filter = null)
        {
            using var renderContext = new RendererContext(new ValveResourceFormat.IO.GameFileLoader(null, null), logger);
            var loader = renderContext.ShaderLoader;
            var folder = ShaderParser.ShaderSourceDirectory
                ?? throw new DirectoryNotFoundException("Shader validation requires the shader source files, but this build only has the embedded copies.");

            var vertShaders = Directory.GetFiles(folder, "*.vert.slang");
            var compShaders = Directory.GetFiles(folder, "*.comp.slang");
            var allShaders = vertShaders.Concat(compShaders).ToArray();

            if (filter != null)
            {
                allShaders = [.. allShaders.Where(s => Path.GetFileName(s).Contains(filter, StringComparison.OrdinalIgnoreCase))];
            }

            GLEnvironment.Initialize(renderContext.Logger);
            GLEnvironment.EnableParallelShaderCompile();

            foreach (var shader in allShaders)
            {
                var shaderName = ShaderNameFromPath(shader);

                if (IsCI)
                {
                    Console.WriteLine($"::group::Shader {shaderName}");
                }

                progressReporter.Report($"Compiling {shaderName}");

                if (shaderName == "texture_decode")
                {
                    loader.LoadShader(shaderName, new Dictionary<string, byte>
                    {
                        ["S_TYPE_TEXTURE2D"] = 1,
                    });
                    continue;
                }

                // Compile without waiting on link status so the driver can link the variants in parallel;
                // statuses are collected at the end of this shader's batch
                loader.LoadShader(shaderName, blocking: false);

                // Test all defines one by one
                var shaderFileName = GetShaderFileByName(shaderName);
                var parsed = GetOrParseShader(shaderFileName, loader.EffectiveFlavour);
                var defines = parsed.Defines.Where(static x => !x.Key.StartsWith("GameVfx_", StringComparison.Ordinal)).ToDictionary();
                var variants = parsed.Defines.Keys.Where(static x => x.StartsWith("GameVfx_", StringComparison.Ordinal));
                var sourceLines = parsed.SourceFileLines;
                var maxValues = ExtractMaxDefineValues(defines, sourceLines);

                foreach (var define in defines.Keys)
                {
                    var maxValue = maxValues.GetValueOrDefault(define, 1);

                    for (var value = 1; value <= maxValue; value++)
                    {
                        progressReporter.Report($"Compiling {shaderName} with {define}={value}");

                        loader.LoadShader(shaderName, new Dictionary<string, byte>
                        {
                            [define] = (byte)value,
                        }, blocking: false);
                    }
                }

                // Test all variants
                foreach (var name in variants)
                {
                    var vfxName = string.Concat(name.AsSpan()["GameVfx_".Length..], ".vfx");
                    progressReporter.Report($"Compiling variant {vfxName}");

                    loader.LoadShader(vfxName, blocking: false);

                    // Test all defines one by one in combination with the shader variant name
                    foreach (var define in defines.Keys)
                    {
                        var maxValue = maxValues.GetValueOrDefault(define, 1);

                        for (var value = 1; value <= maxValue; value++)
                        {
                            progressReporter.Report($"Compiling variant {vfxName} with {define}={value}");

                            loader.LoadShader(vfxName, new Dictionary<string, byte>
                            {
                                [define] = (byte)value,
                            }, blocking: false);
                        }
                    }

                    // Test all defines at once with their maximum values
                    progressReporter.Report($"Compiling variant {vfxName} with all defines");

                    loader.LoadShader(vfxName, defines.Keys.ToDictionary(static d => d, d => (byte)maxValues.GetValueOrDefault(d, 1)), blocking: false);
                }

                // Collect the link statuses that were deferred above, failing like a blocking load would
                progressReporter.Report($"Linking {shaderName} variants");

                foreach (var compiledShader in loader.CachedShaders.Values)
                {
                    if (compiledShader.IsLoaded)
                    {
                        continue;
                    }

                    if (!compiledShader.EnsureLoaded())
                    {
                        GL.GetProgramInfoLog(compiledShader.Program, out var log);
                        var argsDescription = GetArgumentDescription(SortAndFilterArguments(parsed.Defines, compiledShader.Parameters));
                        ThrowShaderError(log, string.Concat(shaderFileName, argsDescription), compiledShader.Name, "Failed to link shader", parsed);
                    }
                }

                if (IsCI)
                {
                    Console.WriteLine("::endgroup::");
                }
            }

            progressReporter.Report($"Validated {loader.CachedShaders.Count} shader variants");
        }

        private static bool? _isCI;
        private static bool IsCI => _isCI ??= Environment.GetEnvironmentVariable("CI") != null;

        [GeneratedRegex(@"(?<DefineName>(?:F|S|D)_\S+)\s*(?<Operator>>=|<=|>|<|==|!=)\s*(?<Value>\d+)")]
        private static partial Regex ShaderDefineConditions();

        private static Dictionary<string, int> ExtractMaxDefineValues(Dictionary<string, byte> defines, List<List<string>> allSourceLines)
        {
            var maxValues = new Dictionary<string, int>();

            foreach (var sourceLines in allSourceLines)
            {
                foreach (var line in sourceLines)
                {
                    var matches = ShaderDefineConditions().Matches(line);
                    foreach (Match match in matches)
                    {
                        var defineName = match.Groups["DefineName"].Value;
                        var operator_ = match.Groups["Operator"].Value;
                        var value = int.Parse(match.Groups["Value"].Value, CultureInfo.InvariantCulture);

                        if (defines.ContainsKey(defineName))
                        {
                            var testValue = operator_ switch
                            {
                                ">" => value + 1,
                                "<" => Math.Max(1, value - 1),
                                ">=" => value,
                                "<=" => value,
                                "==" => value,
                                "!=" => Math.Max(value + 1, 2),
                                _ => value
                            };

                            if (testValue > maxValues.GetValueOrDefault(defineName, 1))
                            {
                                maxValues[defineName] = testValue;
                            }
                        }
                    }
                }
            }

            return maxValues;
        }
#endif

        /// <summary>
        /// Exception thrown when shader compilation or linking fails.
        /// </summary>
        public class ShaderCompilerException : Exception
        {
            /// <summary>Initializes a new instance of the <see cref="ShaderCompilerException"/> class.</summary>
            public ShaderCompilerException()
            {
            }

            /// <summary>Initializes a new instance of the <see cref="ShaderCompilerException"/> class with a message.</summary>
            /// <param name="message">The error message describing the compilation or link failure.</param>
            public ShaderCompilerException(string message) : base(message)
            {
            }

            /// <summary>Initializes a new instance of the <see cref="ShaderCompilerException"/> class with a message and an inner exception.</summary>
            /// <param name="message">The error message describing the compilation or link failure.</param>
            /// <param name="innerException">The exception that caused this exception.</param>
            public ShaderCompilerException(string message, Exception innerException) : base(message, innerException)
            {
            }
        }
    }
}
