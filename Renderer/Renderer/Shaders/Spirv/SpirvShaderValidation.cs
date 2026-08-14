using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ValveResourceFormat.Renderer.Shaders.Spirv;

/// <summary>The outcome of putting one shader stage through the whole GLSL to SPIR-V round trip.</summary>
/// <param name="ShaderName">The renderer shader name, without stage or extension.</param>
/// <param name="Stage">The stage that was compiled.</param>
/// <param name="Result">What the compiler returned.</param>
/// <param name="Reflection">What the module declares, or <see langword="null"/> when it failed to compile.</param>
/// <param name="ContractViolations">Descriptor set problems found by
/// <see cref="SpirvReflection.ValidateDescriptorSets"/>.</param>
/// <param name="Elapsed">Wall clock time for the compile alone, excluding preprocessing.</param>
public sealed record SpirvShaderValidationResult(
    string ShaderName,
    RHI.ShaderStage Stage,
    SpirvCompilationResult Result,
    SpirvReflectionResult? Reflection,
    ImmutableArray<string> ContractViolations,
    TimeSpan Elapsed);

/// <summary>
/// Compiles the renderer's own shaders to SPIR-V and reflects the result, as a round trip over real
/// sources rather than synthetic ones.
/// </summary>
/// <remarks>
/// <para>
/// This module is deliberately not wired into <see cref="ShaderLoader"/>. It reuses
/// <see cref="ShaderParser"/> for preprocessing, which is the part that produces the <c>#line</c>
/// directives diagnostics are mapped through, and rebuilds the compile header the same way the
/// OpenGL path does. That duplication is temporary and collapses when the SPIR-V path is wired in.
/// </para>
/// <para>
/// Today's sources are GL flavoured GLSL and many of them will not compile as Vulkan GLSL until the
/// explicit locations and set/binding decorations land. A failure here is expected information, not
/// necessarily a defect.
/// </para>
/// </remarks>
public static class SpirvShaderValidation
{
    private static readonly ShaderParser Parser = new();

    /// <summary>Gets the renderer shader names that have a vertex or compute entry point.</summary>
    /// <returns>The shader names, sorted.</returns>
    public static ImmutableArray<string> EnumerateShaders()
        => [.. Parser.AvailableShaders.Keys.OrderBy(static name => name, StringComparer.Ordinal)];

    /// <summary>
    /// Preprocesses and compiles every stage of one shader with its default combos.
    /// </summary>
    /// <param name="shaderName">The renderer shader name, for example <c>complex</c>.</param>
    /// <param name="compiler">The compiler to use, or <see langword="null"/> for <see cref="SpirvCompiler.Shared"/>.</param>
    /// <param name="options">A template whose optimisation level, target and debug info are applied to
    /// every stage. The file name, header and source map are supplied per stage.</param>
    /// <returns>One result per stage the shader declares.</returns>
    public static ImmutableArray<SpirvShaderValidationResult> CompileShader(string shaderName, SpirvCompiler? compiler = null, SpirvCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(shaderName);

        compiler ??= SpirvCompiler.Shared;
        options ??= SpirvCompileOptions.Default;

        var parsed = Preprocess(shaderName);
        var header = BuildHeader(parsed);
        var sourceMap = new SpirvSourceMap(parsed.SourceFiles);

        var results = ImmutableArray.CreateBuilder<SpirvShaderValidationResult>();

        foreach (var (programType, source) in parsed.Sources)
        {
            var stage = SpirvCompiler.ToShaderStage(programType);
            var extension = ShaderParser.ProgramTypeToExtension[programType];

            var stageOptions = new SpirvCompileOptions
            {
                FileName = string.Create(CultureInfo.InvariantCulture, $"{shaderName}.{extension}.slang"),
                Header = header,
                SourceMap = sourceMap,
                OptimizationLevel = options.OptimizationLevel,
                Target = options.Target,
                GenerateDebugInfo = options.GenerateDebugInfo,
                WarningsAsErrors = options.WarningsAsErrors,
                AutoAssignBindings = options.AutoAssignBindings,
            };

            var stopwatch = Stopwatch.StartNew();
            var result = compiler.Compile(source, stage, stageOptions);
            stopwatch.Stop();

            SpirvReflectionResult? reflection = null;
            var violations = ImmutableArray<string>.Empty;

            if (result.Success)
            {
                reflection = SpirvReflection.Reflect(result.Spirv.Span);
                violations = SpirvReflection.ValidateDescriptorSets(reflection);
            }

            results.Add(new SpirvShaderValidationResult(shaderName, stage, result, reflection, violations, stopwatch.Elapsed));
        }

        return results.ToImmutable();
    }

    /// <summary>
    /// Compiles every renderer shader with its default combos and reports progress as it goes.
    /// </summary>
    /// <param name="progress">Receives one line per stage compiled, or <see langword="null"/> for none.</param>
    /// <param name="filter">Optional substring restricting which shaders are compiled.</param>
    /// <param name="options">Compile options applied to every stage.</param>
    /// <returns>Every stage result, and the total wall clock time.</returns>
    public static (ImmutableArray<SpirvShaderValidationResult> Results, TimeSpan Total) CompileAll(IProgress<string>? progress = null, string? filter = null, SpirvCompileOptions? options = null)
    {
        using var compiler = new SpirvCompiler();

        var shaders = EnumerateShaders();

        if (filter != null)
        {
            shaders = [.. shaders.Where(name => name.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        }

        var results = ImmutableArray.CreateBuilder<SpirvShaderValidationResult>();
        var stopwatch = Stopwatch.StartNew();

        foreach (var shaderName in shaders)
        {
            ImmutableArray<SpirvShaderValidationResult> shaderResults;

            try
            {
                shaderResults = CompileShader(shaderName, compiler, options);
            }
            catch (ShaderLoader.ShaderCompilerException e)
            {
                // The preprocessor rejected the source before a compile was possible.
                progress?.Report(string.Create(CultureInfo.InvariantCulture, $"{shaderName}: preprocessing failed: {e.Message}"));
                continue;
            }

            foreach (var result in shaderResults)
            {
                progress?.Report(Describe(result));
            }

            results.AddRange(shaderResults);
        }

        stopwatch.Stop();

        return (results.ToImmutable(), stopwatch.Elapsed);
    }

    /// <summary>Formats one stage result as a single line.</summary>
    /// <param name="result">The stage result.</param>
    /// <returns>A one line summary.</returns>
    public static string Describe(SpirvShaderValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Result.Success)
        {
            var primary = result.Result.PrimaryError;

            return string.Create(CultureInfo.InvariantCulture,
                $"FAIL {result.ShaderName} [{result.Stage}] {primary?.ToString() ?? result.Result.Status.ToString()}");
        }

        var reflection = result.Reflection;

        return string.Create(CultureInfo.InvariantCulture,
            $"ok   {result.ShaderName} [{result.Stage}] {result.Result.Spirv.Length} bytes, "
            + $"{reflection?.DescriptorBindings.Length ?? 0} descriptors, "
            + $"{reflection?.PushConstantSizeInBytes ?? 0} byte push block, "
            + $"{reflection?.VertexInputs.Length ?? 0} inputs, {result.Elapsed.TotalMilliseconds:F1} ms");
    }

    /// <summary>
    /// Groups the errors from a run by the message glslang produced, which is what turns a wall of
    /// failures into the short list of constructs that actually need fixing.
    /// </summary>
    /// <param name="results">The stage results to summarise.</param>
    /// <returns>Each distinct error message with how many stages hit it, most frequent first.</returns>
    public static ImmutableArray<(string Message, int Count)> GroupErrors(IEnumerable<SpirvShaderValidationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        return
        [
            .. results
                .Where(static r => !r.Result.Success)
                .SelectMany(static r => r.Result.Errors)
                .Where(static e => e.HasLocation)
                .GroupBy(static e => Normalize(e.Message), StringComparer.Ordinal)
                .Select(static g => (Message: g.Key, Count: g.Count()))
                .OrderByDescending(static g => g.Count)
                .ThenBy(static g => g.Message, StringComparer.Ordinal)
        ];
    }

    // glslang messages are "'token' : explanation". The token varies per shader, the explanation does not.
    private static string Normalize(string message)
    {
        var separator = message.IndexOf(" : ", StringComparison.Ordinal);
        return separator < 0 ? message : message[(separator + 3)..];
    }

    private static ShaderLoader.ParsedShaderData Preprocess(string shaderName)
    {
        var parsed = new ShaderLoader.ParsedShaderData();

        var availableStages = Parser.AvailableShaders.GetValueOrDefault(shaderName)
            ?? throw new ShaderLoader.ShaderCompilerException($"Shader '{shaderName}' does not exist.");

        foreach (var (programType, extension) in ShaderParser.ProgramTypeToExtension)
        {
            if (!availableStages[(int)programType])
            {
                continue;
            }

            var source = Parser.PreprocessShader(
                string.Create(CultureInfo.InvariantCulture, $"{shaderName}.{extension}.slang"),
                parsed);

            parsed.Sources[programType] = source;
            Parser.ClearBuilder();
        }

        parsed.GlobalsLayout = GlobalsLayout.Build(parsed.GlobalsDeclarations);

        return parsed;
    }

    /// <summary>
    /// Rebuilds the preamble the OpenGL path prepends: the version, the hoisted extensions, the
    /// resolved defines and the packed globals block.
    /// </summary>
    private static string BuildHeader(ShaderLoader.ParsedShaderData parsed)
    {
        var header = new StringBuilder();

        header.Append(ShaderParser.ExpectedShaderVersion);
        header.Append('\n');
        header.Append("#extension GL_KHR_shader_subgroup_arithmetic : enable\n");
        header.Append("#extension GL_KHR_shader_subgroup_vote : enable\n");

        foreach (var extension in parsed.Extensions)
        {
            header.Append(extension);
            header.Append('\n');
        }

        foreach (var (defineName, defaultValue) in parsed.Defines)
        {
            header.Append("#define ");
            header.Append(defineName);
            header.Append(' ');
            header.Append(defaultValue.ToString(CultureInfo.InvariantCulture));
            header.Append('\n');
        }

        header.Append(parsed.GlobalsLayout.BlockSource);

        return header.ToString();
    }
}
