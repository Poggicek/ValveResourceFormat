using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ValveResourceFormat.Renderer.Buffers;
using static ValveResourceFormat.Renderer.Shaders.ShaderLoader;

namespace ValveResourceFormat.Renderer.Shaders
{
    /// <summary>A stage-to-stage varying declared by one shader.</summary>
    /// <param name="Name">The varying name, which both stages declare it under.</param>
    /// <param name="Type">The declared GLSL type.</param>
    /// <param name="Locations">The number of interface locations the declaration consumes.</param>
    public readonly record struct ShaderVarying(string Name, string Type, int Locations);

    /// <summary>
    /// Preprocesses shader source files and extracts defines and render modes.
    /// </summary>
    public partial class ShaderParser
    {
        /// <summary>The embedded-resource namespace prefix used to locate shader files in the assembly manifest.</summary>
        public const string ShaderDirectory = "Renderer.Shaders.";

        /// <summary>The GLSL version directive that must appear as the first line of every shader file.</summary>
        public const string ExpectedShaderVersion = "#version 460";
        private const string RenderModeDefinePrefix = "renderMode_";

        [GeneratedRegex("^#include \"(?<IncludeName>[^\"]+)\"")]
        private static partial Regex RegexInclude();
        [GeneratedRegex("^#define (?<ParamName>(?:renderMode|GameVfx|F|S|D)_\\S+) (?<DefaultValue>[0-9]+)")]
        private static partial Regex RegexDefine();

        // regex that detects
        // uniform sampler{dim} x;
        // uniform sampler{dim} a; // SrgbRead(true)
        // uniform vec{dim} b = vec3(1.0); // SrgbRead(true)
        // uniform sampler{dim} c; // SrgbRead(true) Sampler(UserConfig)
        // uniform sampler{dim} d; // Sampler(UserConfig)
        // layout(binding = n) uniform sampler{dim} e;
        // uniform layout(binding = n) sampler{dim} f;
        [GeneratedRegex("^(?:layout\\s*\\([^)]*\\)\\s*)?uniform (?:layout\\s*\\([^)]*\\)\\s*)?(?<Type>\\S+) (?<Name>[A-Za-z_][A-Za-z0-9_]*)(?<Array>\\[[^\\]]*\\])?(?:\\s*=\\s*(?<Default>[^;]+?))?\\s*;[ \t]*(?:// )?(?<SrgbRead>SrgbRead\\(true\\))?[ \t]*(?<SamplerUserConfig>Sampler\\(UserConfig\\))?")]
        private static partial Regex RegexUniform();

        [GeneratedRegex("^#extension .+$")]
        private static partial Regex RegexExtension();

        [GeneratedRegex("^#define (?<From>(?:g|F)_[A-Za-z0-9_]+) (?<To>[A-Za-z_][A-Za-z0-9_]*)$")]
        private static partial Regex RegexUniformAlias();

        // An attribute declaration of a vertex shader, for example "in vec4 vCOLOR;". Multiline, because the
        // locations are stamped over the whole assembled source rather than line by line.
        [GeneratedRegex(@"^in\s+[a-z0-9]+\s+(?<Name>[A-Za-z_][A-Za-z0-9_]*)\s*;", RegexOptions.Multiline)]
        private static partial Regex RegexVertexAttribute();

        // A declaration that places itself, which the allocation has to work around rather than stamp
        [GeneratedRegex(@"^layout\s*\(\s*location\s*=\s*(?<Location>[0-9]+)\s*\)\s*in\s+[a-z0-9]+\s+(?<Name>[A-Za-z_][A-Za-z0-9_]*)\s*;", RegexOptions.Multiline)]
        private static partial Regex RegexLocatedVertexAttribute();

        /// <summary>
        /// Writes each attribute's location into its declaration, once every declaration in the shader is
        /// known. A custom attribute takes a slot this set leaves free, so a vertex struct declaring the same
        /// set reaches the same numbers without either side naming one.
        /// </summary>
        private static string StampAttributeLocations(string source, HashSet<string> declaredAttributes)
        {
            var pinned = RegexLocatedVertexAttribute().Matches(source)
                .ToDictionary(match => match.Groups["Name"].Value, match => int.Parse(match.Groups["Location"].Value, CultureInfo.InvariantCulture), StringComparer.Ordinal);

            var locations = VertexAttributeLocations.Allocate(declaredAttributes.Concat(pinned.Keys), pinned);

            return RegexVertexAttribute().Replace(source, match =>
                $"layout (location = {locations[match.Groups["Name"].Value].ToString(CultureInfo.InvariantCulture)}) {match.Value}");
        }

        private static bool IsPackableUniformName(string name)
            => name.StartsWith("g_", StringComparison.Ordinal) || name.StartsWith("F_", StringComparison.Ordinal);

        private readonly StringBuilder builder = new(1024);

        /// <summary>Clears the internal <see cref="StringBuilder"/> so it is ready for the next preprocessing pass.</summary>
        public void ClearBuilder()
        {
            builder.Clear();
        }

        /// <summary>Reads and preprocesses a shader source file, resolving includes and extracting defines, render modes, and uniform names into <paramref name="parsedData"/>.</summary>
        /// <param name="shaderFile">The shader file path or embedded resource name (e.g. <c>complex.vert.slang</c>).</param>
        /// <param name="parsedData">The data container that accumulates extracted metadata across all stages.</param>
        /// <returns>The preprocessed GLSL source text ready for driver compilation.</returns>
        /// <remarks>
        /// The dialect comes from <see cref="ParsedShaderData.Flavour"/>. Stage-to-stage varyings cannot be located
        /// until every stage has been read, so a Vulkan parse is only finished once
        /// <see cref="StampInterfaceLocations"/> has run over all of them.
        /// </remarks>
        public string PreprocessShader(string shaderFile, ParsedShaderData parsedData)
        {
            var sourceFileNumber = parsedData.SourceFiles.Count;
            var resolvedIncludes = new HashSet<string>(4);
            var isVertexStage = GetTypeFromFileName(shaderFile) == ShaderProgramType.Vertex;
            var declaredAttributes = new HashSet<string>(StringComparer.Ordinal);

            var uniformAliases = new Dictionary<string, string>(0);

            void AppendLineNumber(int a, int b)
            {
                builder.Append("#line ");
                builder.Append(a.ToString(CultureInfo.InvariantCulture));
                builder.Append(' ');
                builder.Append(b.ToString(CultureInfo.InvariantCulture));
                builder.Append('\n');
            }

            // simulate first time compile
            builder.Append($"// {Guid.CreateVersion7()}");

            void LoadShaderString(string shaderFileToLoad, string? parentFile, bool isInclude)
            {
                if (parentFile != null)
                {
                    var folder = Path.GetDirectoryName(parentFile);

                    if (!string.IsNullOrEmpty(folder))
                    {
                        shaderFileToLoad = Path.Combine(folder, shaderFileToLoad);
                    }

                    var constPath = AppContext.BaseDirectory;
                    shaderFileToLoad = Path.GetFullPath(shaderFileToLoad, constPath);
                    shaderFileToLoad = Path.GetRelativePath(constPath, shaderFileToLoad);
                    shaderFileToLoad = shaderFileToLoad.Replace(Path.DirectorySeparatorChar, '/');

                    if (!resolvedIncludes.Add(shaderFileToLoad))
                    {
                        return;
                    }
                }

                using var stream = GetShaderStream(shaderFileToLoad);
                using var reader = new StreamReader(stream);
                string? line;
                var lineNum = 1;
                var currentSourceFileNumber = sourceFileNumber++;
                parsedData.SourceFiles.Add(shaderFileToLoad);

#if DEBUG
                var currentSourceLines = new List<string>();
                parsedData.SourceFileLines.Add(currentSourceLines);
#endif

                builder.EnsureCapacity(builder.Length + (int)stream.Length);

                while ((line = reader.ReadLine()) != null)
                {
                    lineNum++;

#if DEBUG
                    if (!line.All(static c => char.IsAscii(c)))
                    {
                        // At least on nvidia, trying to compile GLSL with non ascii characters will throw bizarre errors like
                        // wrong #line source-line error, or EOF.
                        throw new ShaderCompilerException($"Line {lineNum} in '{shaderFileToLoad}' contains non-ASCII characters.");
                    }
#endif

                    if (lineNum == 2)
                    {
                        if (line != ExpectedShaderVersion)
                        {
                            throw new ShaderCompilerException($"First line must be '{ExpectedShaderVersion}' in '{shaderFileToLoad}'");
                        }

#if DEBUG
                        currentSourceLines.Add($"// :VrfPreprocessed {line}");
#endif

                        builder.Append('\n');

                        AppendLineNumber(lineNum, currentSourceFileNumber);

                        // We add #version even in includes so that they can be compiled individually for better editing experience
                        // Skip #version for main shader - will be prepended in header
                        continue;
                    }

#if DEBUG
                    currentSourceLines.Add(line);
#endif

                    {
                        line = line.Trim(); // we will be outputting trimmed lines to compile too

                        // Includes
                        var match = RegexInclude().Match(line);

                        if (match.Success)
                        {
                            // Recursively append included shaders

                            var includeName = match.Groups["IncludeName"].Value;

                            AppendLineNumber(1, sourceFileNumber);
                            LoadShaderString(includeName, shaderFileToLoad, isInclude: true);
                            AppendLineNumber(lineNum, currentSourceFileNumber);

                            continue;
                        }

                        // Defines
                        match = RegexDefine().Match(line);

                        if (match.Success)
                        {
                            var defineName = match.Groups["ParamName"].Value;
                            var defaultValueStr = match.Groups["DefaultValue"].Value;
                            var value = byte.Parse(defaultValueStr, CultureInfo.InvariantCulture);

                            if (defineName.StartsWith(RenderModeDefinePrefix, StringComparison.Ordinal))
                            {
                                var renderMode = defineName[RenderModeDefinePrefix.Length..];

                                parsedData.RenderModes.Add(renderMode);

                                value = RenderModes.GetShaderId(renderMode);

                                if (value == 0)
                                {
                                    var renderModeObj = new RenderModes.RenderMode(renderMode);
                                    var index = RenderModes.Items.IndexOf(renderModeObj);

                                    if (index == -1)
                                    {
                                        Debug.Assert(false); // Add to <see cref="RenderModes.Items"/> if this assert is hit

                                        RenderModes.Items = RenderModes.Items.Add(renderModeObj);
                                        index = RenderModes.Items.IndexOf(renderModeObj);
                                    }

                                    value = (byte)index;
                                    RenderModes.AddShaderId(renderMode, value);
                                }

                                builder.Append("#define ");
                                builder.Append(defineName);
                                builder.Append(' ');
                                builder.Append(value.ToString(CultureInfo.InvariantCulture));
                                builder.Append(" // :VrfPreprocessed\n");

                                continue;
                            }

                            // Defines are removed from source code and will be prepended later
                            if (!parsedData.Defines.TryAdd(defineName, value))
                            {
                                // Defines can be shared between vert and frag
                                if (parsedData.Defines[defineName] != value)
                                {
                                    throw new ShaderCompilerException($"Line {lineNum} in '{shaderFileToLoad}' contains a duplicate define '{defineName}' with different default value");
                                }
                            }

                            // Dropping the line without re-syncing would report every error below it
                            // one line early per define removed, which the render mode branch above
                            // avoids by emitting a replacement line. Includes and #endif re-sync often
                            // enough to hide it, so the skew shows up as an error blamed on a
                            // plausible-looking neighbour rather than as an obvious wrong number.
                            AppendLineNumber(lineNum, currentSourceFileNumber);

                            continue;
                        }

                        match = RegexExtension().Match(line);

                        if (match.Success)
                        {
                            parsedData.Extensions.Add(line);

                            builder.Append("// :VrfHoisted ");
                            builder.Append(line);
                            builder.Append('\n');
                            continue;
                        }

                        match = RegexUniformAlias().Match(line);

                        if (match.Success)
                        {
                            uniformAliases[match.Groups["From"].Value] = match.Groups["To"].Value;
                        }

                        // sRGB uniforms or samplers
                        match = RegexUniform().Match(line);
                        if (match.Success)
                        {
                            var uniformType = match.Groups["Type"].Value;
                            var uniformName = match.Groups["Name"].Value;

                            if (uniformAliases.TryGetValue(uniformName, out var aliasedName))
                            {
                                uniformName = aliasedName;
                            }

                            parsedData.Uniforms.Add(uniformName);

                            if (uniformType.StartsWith("sampler", StringComparison.Ordinal) && MaterialLoader.IsReservedTexture(uniformName))
                            {
                                parsedData.ReservedTextures.Add(uniformName);
                            }

                            if (match.Groups["SrgbRead"].Success)
                            {
                                parsedData.SrgbUniforms.Add(uniformName);
                            }
                            if (match.Groups["SamplerUserConfig"].Success)
                            {
                                parsedData.SamplerUserConfigUniforms.Add(uniformName);
                            }

                            if (!match.Groups["Array"].Success
                            && IsPackableUniformName(uniformName)
                            && GlobalsLayout.TryGetType(uniformType, out var constantType))
                            {
                                var defaultGroup = match.Groups["Default"];

                                parsedData.GlobalsDeclarations.Add(new GlobalsDeclaration(uniformName, constantType,
                                    defaultGroup.Success ? defaultGroup.Value : null, match.Groups["SrgbRead"].Success));

                                builder.Append("// :VrfPacked ");
                                builder.Append(line);
                                builder.Append('\n');
                                continue;
                            }
                        }

                        // Collected now, located once the whole declaring set is known
                        if (isVertexStage)
                        {
                            match = RegexVertexAttribute().Match(line);

                            if (match.Success)
                            {
                                declaredAttributes.Add(match.Groups["Name"].Value);
                            }
                        }
                    }

                    builder.Append(line);
                    builder.Append('\n');

                    if (line.Contains("#endif", StringComparison.Ordinal))
                    {
                        // Fix an issue where #include is inside of an #if, which messes up line numbers
                        AppendLineNumber(lineNum, currentSourceFileNumber);
                    }
                }
            }

            LoadShaderString(shaderFile, null, isInclude: false);

            var source = builder.ToString();

            if (declaredAttributes.Count > 0)
            {
                source = StampAttributeLocations(source, declaredAttributes);
            }

            if (parsedData.Flavour == ShaderFlavour.Vulkan)
            {
                source = VulkanGlsl.Decorate(source, GetTypeFromFileName(shaderFile), parsedData);
            }

            return source;
        }

        /// <summary>
        /// Locates every stage-to-stage varying of a fully preprocessed Vulkan shader, rewriting each stage in
        /// <see cref="ParsedShaderData.Sources"/> in place.
        /// </summary>
        /// <param name="parsedData">A shader whose stages have all been through <see cref="PreprocessShader"/>.</param>
        /// <remarks>
        /// A varying has to carry the same location in the stage that writes it and the stage that reads it, so the
        /// numbers can only be handed out once the whole interface is known. Locations are allocated over the union
        /// of the declarations of every stage, in name order, which both stages then agree on without either naming
        /// the other. Declarations behind an inactive combo still consume their locations, since the combo is not
        /// resolved until the shader is compiled.
        /// </remarks>
        public static void StampInterfaceLocations(ParsedShaderData parsedData)
        {
            ArgumentNullException.ThrowIfNull(parsedData);

            if (parsedData.Flavour != ShaderFlavour.Vulkan || parsedData.Varyings.Count == 0)
            {
                return;
            }

            var locations = VulkanGlsl.AllocateVaryingLocations(parsedData.Varyings, parsedData.VaryingLocations);

            foreach (var (name, location) in locations)
            {
                parsedData.VaryingLocations[name] = location;
            }

            foreach (var stage in parsedData.Sources.Keys.ToArray())
            {
                parsedData.Sources[stage] = VulkanGlsl.StampVaryings(parsedData.Sources[stage], stage, locations);
            }
        }

        internal static readonly Dictionary<ShaderProgramType, string> ProgramTypeToExtension = new()
        {
            { ShaderProgramType.Vertex, "vert" },
            { ShaderProgramType.Fragment, "frag" },
            { ShaderProgramType.Compute, "comp" }
        };

        internal static readonly Dictionary<string, ShaderProgramType> ExtensionToProgramType = ProgramTypeToExtension
            .ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

        private static ShaderProgramType GetTypeFromFileName(string fileName)
        {
            var ext = fileName[^(ShaderFileExtension.Length - 1)..^SlangExtension.Length];
            return ExtensionToProgramType.GetValueOrDefault(ext, ShaderProgramType.Max);
        }

        /// <summary>Gets the map from shader base names to a bool array indicating which pipeline stages (vertex, fragment, compute) exist in a mounted shader directory, on disk, or in the assembly manifest.</summary>
        public Dictionary<string, bool[]> AvailableShaders { get; private set; }

        /// <summary>Initializes a new instance of the <see cref="ShaderParser"/> class and discovers all available shader files.</summary>
        public ShaderParser()
        {
            AvailableShaders = GetAvailableShaders();
        }

        /// <summary>Rediscovers the available shader files, picking up changes to <see cref="ShaderRegistry"/>.</summary>
        internal void RefreshAvailableShaders()
        {
            AvailableShaders = GetAvailableShaders();
        }

        private static Dictionary<string, bool[]> GetAvailableShaders()
        {
            Dictionary<string, bool[]> availableShaders = [];

            void LogShaderStage(string shaderName, ShaderProgramType shaderType)
            {
                if (shaderType >= ShaderProgramType.Max)
                {
                    return;
                }

                if (!availableShaders.TryGetValue(shaderName, out var stages))
                {
                    stages = new bool[3];
                    availableShaders[shaderName] = stages;
                }

                stages[(int)shaderType] = true;
            }

            foreach (var file in ShaderRegistry.EnumerateShaderFiles($"*{SlangExtension}"))
            {
                var fileName = Path.GetFileName(file);

                if (fileName.Length <= ShaderFileExtension.Length)
                {
                    continue;
                }

                LogShaderStage(ShaderNameFromPath(file), GetTypeFromFileName(fileName));
            }

            if (ShaderSourceDirectory != null)
            {
                var dirInfo = new DirectoryInfo(ShaderSourceDirectory);
                var files = dirInfo.GetFiles($"*{SlangExtension}", SearchOption.TopDirectoryOnly);

                foreach (var file in files)
                {
                    var shaderName = ShaderNameFromPath(file.FullName);
                    var shaderType = GetTypeFromFileName(file.Name);
                    LogShaderStage(shaderName, shaderType);
                }
            }
            else
            {
                var resources = Assembly.GetExecutingAssembly().GetManifestResourceNames()
                    .Where(static r => r.StartsWith(ShaderDirectory, StringComparison.Ordinal))
                    .Where(static r => r.EndsWith(SlangExtension, StringComparison.Ordinal));
                foreach (var resource in resources)
                {
                    var shaderName = resource[ShaderDirectory.Length..^ShaderFileExtension.Length];
                    var shaderType = GetTypeFromFileName(resource);
                    LogShaderStage(shaderName, shaderType);
                }
            }

            return availableShaders;
        }

        /// <summary>
        /// Opens a shader source file, preferring the directories mounted through <see cref="ShaderRegistry"/> over the
        /// shaders shipped with the renderer.
        /// </summary>
        /// <param name="name">Root relative shader file name using forward slashes (e.g. <c>common/lighting.slang</c>).</param>
        private static Stream GetShaderStream(string name)
        {
            return ShaderRegistry.TryOpenShaderFile(name) ?? GetBuiltinShaderStream(name);
        }

        private const string ShaderSourceDirectoryMetadataKey = "ShaderSourceDirectory";

        /// <summary>
        /// Gets the folder holding the shaders shipped with the renderer as loose files on disk, or <see langword="null"/>
        /// when only the copies embedded in this assembly are available.
        /// </summary>
        /// <remarks>
        /// Debug builds record the absolute path of the shader folder at compile time so that shader files can be edited
        /// and hot reloaded without rebuilding. It is not recorded in release builds, and points at a folder that does not
        /// exist when the assembly is used on another machine, in which case the embedded shaders are used instead.
        /// </remarks>
        public static string? ShaderSourceDirectory { get; } = FindShaderSourceDirectory();

        private static string? FindShaderSourceDirectory()
        {
            foreach (var metadata in typeof(ShaderParser).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (metadata.Key == ShaderSourceDirectoryMetadataKey
                && !string.IsNullOrEmpty(metadata.Value)
                && Directory.Exists(metadata.Value))
                {
                    return metadata.Value;
                }
            }

            return null;
        }

        private static Stream GetBuiltinShaderStream(string name)
        {
            if (ShaderSourceDirectory != null)
            {
                var path = Path.Combine(ShaderSourceDirectory, name);

                if (File.Exists(path))
                {
                    return OpenShaderFile(path);
                }
            }

            var resourceName = $"{ShaderDirectory}{name.Replace('/', '.')}";
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException($"Shader file '{name}' was not found on disk or among the embedded shaders.", name);

            return stream;
        }

        private static FileStream OpenShaderFile(string path)
        {
            try
            {
                return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (IOException e)
            {
                Console.Error.WriteLine(e.Message);

                // Sometimes hot reloading shaders throws "The process cannot access the file x because it is being used by another process."
                // Just sleep for a second and try again
                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(1));

                return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
        }
    }

    /// <summary>
    /// Rewrites preprocessed GLSL into the dialect Vulkan accepts: explicit locations on every stage interface,
    /// <c>set</c> and <c>binding</c> decorations on every resource, the per-draw uniforms moved into a push
    /// constant block, and the constructs Vulkan GLSL has no representation for taken out.
    /// </summary>
    /// <remarks>
    /// The descriptor set numbering is the one fixed in <c>RHI/CONTRACT.md</c>. The binding numbers are the
    /// existing <see cref="ReservedBufferSlots"/> and <see cref="ReservedTextureSlots"/> values, unchanged: those
    /// two enums deliberately overlap their uniform and storage index spaces, which GL keeps apart per binding
    /// target and Vulkan does not, so the sets are what makes the collision unrepresentable.
    /// </remarks>
    public static partial class VulkanGlsl
    {
        /// <summary>Descriptor set holding the uniform buffers, bound by <see cref="ReservedBufferSlots"/> UBO number.</summary>
        public const int UniformBufferSet = 0;

        /// <summary>Descriptor set holding the storage buffers, bound by <see cref="ReservedBufferSlots"/> SSBO number.</summary>
        public const int StorageBufferSet = 1;

        /// <summary>Descriptor set holding the globally bound textures and images, bound by <see cref="ReservedTextureSlots"/> number.</summary>
        public const int GlobalTextureSet = 2;

        /// <summary>Descriptor set holding the textures a material supplies, numbered per shader in declaration order.</summary>
        public const int MaterialTextureSet = 3;

        /// <summary>
        /// The size of the per-draw push constant block in bytes, inside the 128 byte floor every Vulkan
        /// implementation guarantees. Query <c>IDeviceLimits.MaxPushConstantSize</c> before growing it.
        /// </summary>
        public const int PushConstantSize = 92;

        /// <summary>The name given to the generated push constant block.</summary>
        public const string PushConstantBlockName = "VrfPushConstants";

        // The uniform the shaders spell as a bool. A block member cannot be one, so it is carried as a uint and
        // put back behind its original name by a macro.
        private const string InstancingMemberName = "bIsInstancing";
        private const string InstancingStorageName = "bIsInstancingValue";

        /// <summary>
        /// The per-draw set, in the order that packs it to exactly <see cref="PushConstantSize"/> bytes under
        /// std430 rules. These are today's <c>glProgramUniform</c> call sites in <c>MeshBatchRenderer</c>.
        /// </summary>
        private static readonly (string Type, string Name, int Size)[] PushConstantMembers =
        [
            ("mat3x4", "transform", 48),
            ("uvec3", "uAnimationData", 12),

            // Fills the 4 byte hole the uvec3 leaves, so the vec2 lands on its 8 byte alignment without padding
            ("int", "morphVertexIdOffset", 4),

            ("vec2", "morphCompositeTextureSize", 8),
            ("uint", "meshId", 4),
            ("uint", "shaderId", 4),
            ("uint", "shaderProgramId", 4),
            ("uint", "vTint", 4),
            ("uint", InstancingStorageName, 4),
        ];

        /// <summary>The type each push constant is declared with in the shader sources, by the name it is declared under.</summary>
        private static readonly FrozenDictionary<string, string> PushConstantSourceTypes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["transform"] = "mat3x4",
            ["uAnimationData"] = "uvec3",
            ["morphVertexIdOffset"] = "int",
            ["morphCompositeTextureSize"] = "vec2",
            ["meshId"] = "uint",
            ["shaderId"] = "uint",
            ["shaderProgramId"] = "uint",
            ["vTint"] = "uint",
            [InstancingMemberName] = "bool",
        }.ToFrozenDictionary(StringComparer.Ordinal);

        private static readonly FrozenDictionary<string, ReservedTextureSlots> ReservedTextureSlotByName = BuildReservedTextureSlots();

        /// <summary>
        /// Gets the push constant block, declared without an instance name so that its members keep the global
        /// names the shader sources already use.
        /// </summary>
        public static string PushConstantBlockSource { get; } = BuildPushConstantBlock();

        private static string BuildPushConstantBlock()
        {
            var builder = new StringBuilder(512);
            builder.Append(CultureInfo.InvariantCulture, $"layout(push_constant) uniform {PushConstantBlockName}\n{{\n");

            var offset = 0;

            foreach (var (type, name, size) in PushConstantMembers)
            {
                builder.Append(CultureInfo.InvariantCulture, $"    layout(offset = {offset}) {type} {name};\n");
                offset += size;
            }

            builder.Append("};\n");

            // The block is the contract both sides read, so a member added without adjusting the documented size
            // has to fail here rather than silently write past what the pipeline layout declares.
            if (offset != PushConstantSize)
            {
                throw new ShaderCompilerException($"The push constant block is {offset} bytes, but the RHI contract fixes it at {PushConstantSize}.");
            }

            builder.Append(CultureInfo.InvariantCulture, $"#define {InstancingMemberName} ({InstancingStorageName} != 0u)\n");

            return builder.ToString();
        }

        private static FrozenDictionary<string, ReservedTextureSlots> BuildReservedTextureSlots()
        {
            // Rebuilt from the same public enum MaterialLoader reads, which keeps its own copy private.
            var slotByName = new Dictionary<string, ReservedTextureSlots>(StringComparer.Ordinal);

            foreach (var field in typeof(ReservedTextureSlots).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.GetCustomAttribute<SamplerNameAttribute>() is not { } attribute)
                {
                    continue;
                }

                var slot = (ReservedTextureSlots)field.GetRawConstantValue()!;

                foreach (var name in attribute.Names)
                {
                    slotByName.Add(name, slot);
                }
            }

            return slotByName.ToFrozenDictionary(StringComparer.Ordinal);
        }

        // A uniform block. Matched on its packing qualifier, which every one of ours carries.
        [GeneratedRegex(@"^layout\s*\(\s*(?<Qualifiers>[^)]*\bstd140\b[^)]*)\)", RegexOptions.Multiline)]
        private static partial Regex RegexUniformBlockLayout();

        // A shader storage block, likewise.
        [GeneratedRegex(@"^layout\s*\(\s*(?<Qualifiers>[^)]*\bstd430\b[^)]*)\)", RegexOptions.Multiline)]
        private static partial Regex RegexStorageBlockLayout();

        // layout(binding = n, rgba8) uniform writeonly image2D name;
        [GeneratedRegex(@"^layout\s*\(\s*(?<Qualifiers>[^)]*)\)\s*uniform\s+(?<Rest>[^;]*\b[iu]?image[0-9A-Za-z]*\s+[A-Za-z_][A-Za-z0-9_]*\s*;)", RegexOptions.Multiline)]
        private static partial Regex RegexImageDeclaration();

        // uniform sampler2D g_tFoo; and the macro typed sampler of texture_decode
        [GeneratedRegex(@"^uniform\s+(?<Type>[iu]?sampler[0-9A-Za-z]*|TEXTURE_TYPE)\s+(?<Name>[A-Za-z_][A-Za-z0-9_]*)\s*;", RegexOptions.Multiline)]
        private static partial Regex RegexSamplerDeclaration();

        // A stage interface declaration. Requires the terminating semicolon, so that an 'in' or 'out' function
        // parameter sitting on its own line cannot match.
        [GeneratedRegex(@"^(?<Qualifiers>(?:(?:flat|noperspective|smooth|centroid|sample|precise|invariant)\s+)*)(?<Direction>in|out)\s+(?<Type>[A-Za-z_][A-Za-z0-9_]*)\s+(?<Name>[A-Za-z_][A-Za-z0-9_]*)\s*(?<Array>\[[^\]]*\])?\s*;", RegexOptions.Multiline)]
        private static partial Regex RegexStageInterface();

        // A fragment output that places itself, which the allocation works around rather than stamps
        [GeneratedRegex(@"^layout\s*\(\s*location\s*=\s*(?<Location>[0-9]+)\s*\)\s*out\s+", RegexOptions.Multiline)]
        private static partial Regex RegexLocatedOutput();

        // A stage interface declaration that places itself, likewise
        [GeneratedRegex(@"^layout\s*\(\s*location\s*=\s*(?<Location>[0-9]+)\s*\)\s*(?<Qualifiers>(?:(?:flat|noperspective|smooth|centroid|sample|precise|invariant)\s+)*)(?<Direction>in|out)\s+(?<Type>[A-Za-z_][A-Za-z0-9_]*)\s+(?<Name>[A-Za-z_][A-Za-z0-9_]*)\s*(?<Array>\[[^\]]*\])?\s*;", RegexOptions.Multiline)]
        private static partial Regex RegexLocatedStageInterface();

        // Any remaining default block uniform, which is what Vulkan GLSL has no representation for
        [GeneratedRegex(@"^uniform\s+(?<Type>[A-Za-z_][A-Za-z0-9_]*(?:\[[^\]]*\])?)\s+(?<Name>[A-Za-z_][A-Za-z0-9_]*)\s*(?<Array>\[[^\]]*\])?(?:\s*=\s*[^;]+?)?\s*;[^\n]*", RegexOptions.Multiline)]
        private static partial Regex RegexLooseUniform();

        [GeneratedRegex(@"\bstruct\s+(?<Name>[A-Za-z_][A-Za-z0-9_]*)\s*\{(?<Body>[^}]*)\}")]
        private static partial Regex RegexStructDefinition();

        [GeneratedRegex(@"^\s*(?<Type>[A-Za-z_][A-Za-z0-9_]*)\s+(?<Name>[A-Za-z_][A-Za-z0-9_]*)\s*(?<Array>\[[^\]]*\])?\s*;", RegexOptions.Multiline)]
        private static partial Regex RegexStructMember();

        [GeneratedRegex(@"\bgl_VertexID\b")]
        private static partial Regex RegexVertexIdBuiltin();

        [GeneratedRegex(@"\bgl_InstanceID\b")]
        private static partial Regex RegexInstanceIdBuiltin();

        /// <summary>
        /// Rewrites one preprocessed stage into Vulkan GLSL, and records what it found on <paramref name="parsedData"/>.
        /// </summary>
        /// <param name="source">The preprocessed stage source, with its vertex inputs already located.</param>
        /// <param name="stage">The stage this source belongs to.</param>
        /// <param name="parsedData">The shader being parsed, shared by every stage of it.</param>
        /// <returns>The rewritten source. Stage-to-stage varyings are collected but not yet located.</returns>
        /// <remarks>
        /// Every rewrite replaces text within its own line, so the <c>#line</c> directives threaded through the
        /// source keep pointing at the right place and compiler errors still name the file they came from.
        /// </remarks>
        internal static string Decorate(string source, ShaderProgramType stage, ParsedShaderData parsedData)
        {
            // Samplers and images first: both passes prefix a layout qualifier, which takes those declarations out
            // of the way of the loose uniform pass that follows.
            source = RegexSamplerDeclaration().Replace(source, match => DecorateSampler(match, parsedData));

            source = RegexImageDeclaration().Replace(source,
                match => $"layout(set = {GlobalTextureSet}, {match.Groups["Qualifiers"].Value}) uniform {match.Groups["Rest"].Value}");

            source = RegexUniformBlockLayout().Replace(source, match => $"layout(set = {UniformBufferSet}, {match.Groups["Qualifiers"].Value})");
            source = RegexStorageBlockLayout().Replace(source, match => $"layout(set = {StorageBufferSet}, {match.Groups["Qualifiers"].Value})");

            source = RegexLooseUniform().Replace(source, match => RewriteLooseUniform(match, parsedData));

            var structs = BuildStructLocationCounts(source);

            source = CollectAndLocateStageInterface(source, stage, parsedData, structs);

            // Vulkan spells these differently, and they cannot be macroed because a macro may not redefine a gl_ name.
            source = RegexVertexIdBuiltin().Replace(source, "gl_VertexIndex");
            source = RegexInstanceIdBuiltin().Replace(source, "(gl_InstanceIndex - gl_BaseInstance)");

            return source;
        }

        private static string DecorateSampler(Match match, ParsedShaderData parsedData)
        {
            var name = match.Groups["Name"].Value;

            int set, binding;

            if (ReservedTextureSlotByName.TryGetValue(name, out var slot))
            {
                set = GlobalTextureSet;
                binding = (int)slot;
            }
            else
            {
                set = MaterialTextureSet;

                // Numbered per shader in declaration order, shared across its stages because the whole shader
                // parses into one ParsedShaderData
                if (!parsedData.MaterialTextureBindings.TryGetValue(name, out binding))
                {
                    binding = parsedData.MaterialTextureBindings.Count;
                    parsedData.MaterialTextureBindings.Add(name, binding);
                }
            }

            return $"layout(set = {set}, binding = {binding}) {match.Value}";
        }

        private static string RewriteLooseUniform(Match match, ParsedShaderData parsedData)
        {
            var name = match.Groups["Name"].Value;
            var type = match.Groups["Type"].Value;

            if (!PushConstantSourceTypes.TryGetValue(name, out var expectedType))
            {
                // Neither packed into the globals block nor part of the per-draw set, so nothing can carry it
                parsedData.VulkanDiagnostics.Add($"'{type} {name}' is a default block uniform with nowhere to live in Vulkan GLSL.");
                return match.Value;
            }

            if (type != expectedType)
            {
                // Same name as a push constant but a different type, so the block's member would collide with it
                parsedData.VulkanDiagnostics.Add(
                    $"'{name}' is declared as '{type}' but the push constant block declares it as '{expectedType}', which collides.");
                return match.Value;
            }

            return $"// :VrfPushConstant {match.Value}";
        }

        private static string CollectAndLocateStageInterface(string source, ShaderProgramType stage, ParsedShaderData parsedData,
            IReadOnlyDictionary<string, int> structs)
        {
            // A varying that places itself keeps the location it was given, and the rest allocate around it
            foreach (Match located in RegexLocatedStageInterface().Matches(source))
            {
                var direction = located.Groups["Direction"].Value;

                if (!IsVarying(stage, direction))
                {
                    continue;
                }

                var name = located.Groups["Name"].Value;
                var type = located.Groups["Type"].Value;

                parsedData.Varyings.TryAdd(name, new ShaderVarying(name, type, GetLocationCount(type, located.Groups["Array"].Value, structs)));
                parsedData.VaryingLocations[name] = int.Parse(located.Groups["Location"].Value, CultureInfo.InvariantCulture);
            }

            // A fragment output is local to its stage, so it is allocated here. A varying has to agree with the
            // stage on the other side of it, so it is only recorded, and located once every stage has been read.
            var outputs = new Dictionary<string, int>(StringComparer.Ordinal);

            if (stage == ShaderProgramType.Fragment)
            {
                var used = 0;

                foreach (Match located in RegexLocatedOutput().Matches(source))
                {
                    used |= 1 << int.Parse(located.Groups["Location"].Value, CultureInfo.InvariantCulture);
                }

                var declared = new SortedSet<string>(StringComparer.Ordinal);

                foreach (Match match in RegexStageInterface().Matches(source))
                {
                    if (match.Groups["Direction"].Value == "out")
                    {
                        declared.Add(match.Groups["Name"].Value);
                    }
                }

                var free = 0;

                foreach (var name in declared)
                {
                    while ((used & (1 << free)) != 0)
                    {
                        free++;
                    }

                    outputs[name] = free;
                    used |= 1 << free;
                }
            }

            return RegexStageInterface().Replace(source, match =>
            {
                var direction = match.Groups["Direction"].Value;
                var name = match.Groups["Name"].Value;

                if (stage == ShaderProgramType.Fragment && direction == "out")
                {
                    return $"layout(location = {outputs[name]}) {match.Value}";
                }

                if (!IsVarying(stage, direction))
                {
                    return match.Value;
                }

                var type = match.Groups["Type"].Value;
                var locations = GetLocationCount(type, match.Groups["Array"].Value, structs);

                if (parsedData.Varyings.TryGetValue(name, out var existing))
                {
                    if (existing.Locations != locations)
                    {
                        parsedData.VulkanDiagnostics.Add(
                            $"Varying '{name}' spans {existing.Locations} location(s) as '{existing.Type}' in one stage and {locations} as '{type}' in another.");
                    }
                }
                else
                {
                    parsedData.Varyings.Add(name, new ShaderVarying(name, type, locations));
                }

                // Located by StampInterfaceLocations, once the union of every stage's declarations is known
                return match.Value;
            });
        }

        /// <summary>
        /// Hands out a location range to every varying, in name order, so that the stage writing one and the stage
        /// reading it arrive at the same number independently.
        /// </summary>
        /// <param name="varyings">Every varying declared by any stage of one shader.</param>
        /// <param name="pinned">
        /// Varyings that place themselves with an explicit qualifier in the source. They keep the location they were
        /// given, and the rest allocate around them.
        /// </param>
        /// <returns>The first location of each varying, by name.</returns>
        internal static FrozenDictionary<string, int> AllocateVaryingLocations(IReadOnlyDictionary<string, ShaderVarying> varyings,
            IReadOnlyDictionary<string, int>? pinned = null)
        {
            var locations = new Dictionary<string, int>(varyings.Count, StringComparer.Ordinal);
            var used = new HashSet<int>();

            if (pinned != null)
            {
                foreach (var (name, location) in pinned)
                {
                    locations[name] = location;

                    var span = varyings.TryGetValue(name, out var varying) ? Math.Max(1, varying.Locations) : 1;

                    for (var i = 0; i < span; i++)
                    {
                        used.Add(location + i);
                    }
                }
            }

            foreach (var name in varyings.Keys.Order(StringComparer.Ordinal))
            {
                if (locations.ContainsKey(name))
                {
                    continue;
                }

                var span = Math.Max(1, varyings[name].Locations);

                // Lowest run of free locations wide enough to hold it
                var start = 0;

                while (Enumerable.Range(start, span).Any(used.Contains))
                {
                    start++;
                }

                locations[name] = start;

                for (var i = 0; i < span; i++)
                {
                    used.Add(start + i);
                }
            }

            return locations.ToFrozenDictionary(StringComparer.Ordinal);
        }

        /// <summary>Writes the allocated location into every varying declaration of one stage.</summary>
        /// <param name="source">A stage already through <see cref="Decorate"/>.</param>
        /// <param name="stage">The stage this source belongs to.</param>
        /// <param name="locations">The allocation from <see cref="AllocateVaryingLocations"/>.</param>
        /// <returns>The stage source with every varying located.</returns>
        internal static string StampVaryings(string source, ShaderProgramType stage, IReadOnlyDictionary<string, int> locations)
        {
            return RegexStageInterface().Replace(source, match =>
            {
                return IsVarying(stage, match.Groups["Direction"].Value) && locations.TryGetValue(match.Groups["Name"].Value, out var location)
                    ? $"layout(location = {location.ToString(CultureInfo.InvariantCulture)}) {match.Value}"
                    : match.Value;
            });
        }

        /// <summary>
        /// Whether a declaration in this direction crosses between stages, rather than being a vertex input or a
        /// fragment output.
        /// </summary>
        private static bool IsVarying(ShaderProgramType stage, string direction) => (stage, direction) switch
        {
            (ShaderProgramType.Vertex, "out") => true,
            (ShaderProgramType.Fragment, "in") => true,
            _ => false,
        };

        private static FrozenDictionary<string, int> BuildStructLocationCounts(string source)
        {
            var bodies = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (Match match in RegexStructDefinition().Matches(source))
            {
                bodies[match.Groups["Name"].Value] = match.Groups["Body"].Value;
            }

            var counts = new Dictionary<string, int>(bodies.Count, StringComparer.Ordinal);

            if (bodies.Count == 0)
            {
                return counts.ToFrozenDictionary(StringComparer.Ordinal);
            }

            // Repeated so that a struct built out of other structs settles; the nesting here is one or two deep
            for (var pass = 0; pass < 4; pass++)
            {
                foreach (var (name, body) in bodies)
                {
                    var total = 0;

                    foreach (Match member in RegexStructMember().Matches(body))
                    {
                        total += GetLocationCount(member.Groups["Type"].Value, member.Groups["Array"].Value, counts);
                    }

                    counts[name] = total;
                }
            }

            return counts.ToFrozenDictionary(StringComparer.Ordinal);
        }

        /// <summary>
        /// The number of interface locations one declaration consumes. A scalar or vector takes one, a matrix takes
        /// one per column, a struct takes the sum of its members, and an array multiplies by its length.
        /// </summary>
        private static int GetLocationCount(string type, string array, IReadOnlyDictionary<string, int> structs)
        {
            var perElement = 1;

            if (type.StartsWith("mat", StringComparison.Ordinal) || type.StartsWith("dmat", StringComparison.Ordinal))
            {
                // matN is N columns, and so is matNxM
                foreach (var c in type)
                {
                    if (char.IsAsciiDigit(c))
                    {
                        perElement = c - '0';
                        break;
                    }
                }
            }
            else if (structs.TryGetValue(type, out var members))
            {
                perElement = members;
            }

            return perElement * GetArrayLength(array);
        }

        private static int GetArrayLength(string array)
        {
            if (string.IsNullOrEmpty(array))
            {
                return 1;
            }

            var inner = array.AsSpan().Trim("[]").Trim();

            // A length given by a define is not resolved until the shader compiles, so it cannot be sized here
            return int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) && length > 0 ? length : 1;
        }
    }
}
