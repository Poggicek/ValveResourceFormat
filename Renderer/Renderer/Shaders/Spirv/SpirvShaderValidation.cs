using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace ValveResourceFormat.Renderer.Shaders.Spirv;

/// <summary>The outcome of putting one shader stage through the whole GLSL to SPIR-V round trip.</summary>
/// <param name="ShaderName">The renderer shader name, without stage or extension.</param>
/// <param name="Stage">The stage that was compiled.</param>
/// <param name="Flavour">The dialect the source was preprocessed into before compiling.</param>
/// <param name="Result">What the compiler returned.</param>
/// <param name="Reflection">What the module declares, or <see langword="null"/> when it failed to compile.</param>
/// <param name="ContractViolations">Descriptor set problems found by
/// <see cref="SpirvReflection.ValidateDescriptorSets"/>.</param>
/// <param name="SourceDiagnostics">Constructs the preprocessor found that Vulkan GLSL will not accept,
/// from <see cref="ShaderLoader.ParsedShaderData.VulkanDiagnostics"/>. Worth reading even when the
/// stage compiled, and worth reading instead of the compiler log when it did not: glslang reports only
/// the first offending declaration per translation unit, so it undercounts how much work is left.</param>
/// <param name="EmittedMaterialTextureBindings">The slot the preprocessor assigned each material
/// sampler, from <see cref="ShaderLoader.ParsedShaderData.MaterialTextureBindings"/>, before glslang
/// saw the source. Carried so a caller can check what the module ended up declaring against what
/// emission asked for, which is the one link in the chain neither side of the renderer can verify on
/// its own.</param>
/// <param name="Elapsed">Wall clock time for the compile alone, excluding preprocessing.</param>
public sealed record SpirvShaderValidationResult(
    string ShaderName,
    RHI.ShaderStage Stage,
    ShaderFlavour Flavour,
    SpirvCompilationResult Result,
    SpirvReflectionResult? Reflection,
    ImmutableArray<string> ContractViolations,
    ImmutableArray<string> SourceDiagnostics,
    ImmutableArray<KeyValuePair<string, int>> EmittedMaterialTextureBindings,
    TimeSpan Elapsed);

/// <summary>
/// Compiles the renderer's own shaders to SPIR-V and reflects the result, as a round trip over real
/// sources rather than synthetic ones.
/// </summary>
/// <remarks>
/// <para>
/// This module is deliberately not wired into <see cref="ShaderLoader"/>. It reuses
/// <see cref="ShaderParser"/> for preprocessing, which is the part that produces the <c>#line</c>
/// directives diagnostics are mapped through, and rebuilds the compile header the same way
/// <see cref="ShaderLoader"/> does. That duplication is temporary and collapses when the SPIR-V path
/// is wired in.
/// </para>
/// <para>
/// Because the header and the <see cref="ShaderLoader.ParsedShaderData"/> are rebuilt rather than
/// borrowed, both have to carry the <see cref="ShaderFlavour"/> across explicitly. A
/// <see cref="ShaderLoader.ParsedShaderData"/> constructed without one preprocesses as
/// <see cref="ShaderFlavour.OpenGL"/> whatever <see cref="ShaderLoader.Flavour"/> says, which makes a
/// Vulkan run silently measure the OpenGL sources instead.
/// </para>
/// <para>
/// A failure here is information about how far the shader port has got, not necessarily a defect.
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
    /// <param name="flavour">The dialect to preprocess into, or <see langword="null"/> to follow
    /// <see cref="ShaderLoader.Flavour"/>. Pass it explicitly to compare both dialects in one process
    /// without disturbing the static property.</param>
    /// <returns>One result per stage the shader declares.</returns>
    public static ImmutableArray<SpirvShaderValidationResult> CompileShader(string shaderName, SpirvCompiler? compiler = null, SpirvCompileOptions? options = null, ShaderFlavour? flavour = null)
    {
        ArgumentNullException.ThrowIfNull(shaderName);

        compiler ??= SpirvCompiler.Shared;
        options ??= SpirvCompileOptions.Default;

        var resolvedFlavour = flavour ?? ShaderLoader.Flavour;

        var parsed = Preprocess(shaderName, resolvedFlavour);
        var header = BuildHeader(parsed, shaderName);
        var sourceMap = new SpirvSourceMap(parsed.SourceFiles);
        var sourceDiagnostics = parsed.VulkanDiagnostics.ToImmutableArray();
        var emittedBindings = parsed.MaterialTextureBindings.ToImmutableArray();

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

            results.Add(new SpirvShaderValidationResult(shaderName, stage, resolvedFlavour, result, reflection, violations, sourceDiagnostics, emittedBindings, stopwatch.Elapsed));
        }

        return results.ToImmutable();
    }

    /// <summary>
    /// Compiles every renderer shader with its default combos and reports progress as it goes.
    /// </summary>
    /// <param name="progress">Receives one line per stage compiled, or <see langword="null"/> for none.</param>
    /// <param name="filter">Optional substring restricting which shaders are compiled.</param>
    /// <param name="options">Compile options applied to every stage.</param>
    /// <param name="flavour">The dialect to preprocess into, or <see langword="null"/> to follow
    /// <see cref="ShaderLoader.Flavour"/>.</param>
    /// <returns>Every stage result, and the total wall clock time.</returns>
    public static (ImmutableArray<SpirvShaderValidationResult> Results, TimeSpan Total) CompileAll(IProgress<string>? progress = null, string? filter = null, SpirvCompileOptions? options = null, ShaderFlavour? flavour = null)
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
                shaderResults = CompileShader(shaderName, compiler, options, flavour);
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
                $"FAIL {result.ShaderName} [{result.Stage}/{result.Flavour}] {primary?.ToString() ?? result.Result.Status.ToString()}");
        }

        var reflection = result.Reflection;

        return string.Create(CultureInfo.InvariantCulture,
            $"ok   {result.ShaderName} [{result.Stage}/{result.Flavour}] {result.Result.Spirv.Length} bytes, "
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

    /// <summary>
    /// Collects the preprocessor's Vulkan diagnostics across a run, one entry per shader that raised
    /// each message.
    /// </summary>
    /// <param name="results">The stage results to summarise.</param>
    /// <returns>Each shader paired with a construct Vulkan GLSL will not accept, ordered by shader.</returns>
    /// <remarks>
    /// This is the honest count of remaining work. A stage's diagnostics come from the shared
    /// <see cref="ShaderLoader.ParsedShaderData"/>, so they repeat across that shader's stages and are
    /// deduplicated here; and glslang stops after the first offending declaration in a unit, so the
    /// compiler log alone always reports fewer than there are.
    /// </remarks>
    public static ImmutableArray<(string ShaderName, string Message)> GroupSourceDiagnostics(IEnumerable<SpirvShaderValidationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        return
        [
            .. results
                .SelectMany(static r => r.SourceDiagnostics.Select(d => (r.ShaderName, Message: d)))
                .Distinct()
                .OrderBy(static p => p.ShaderName, StringComparer.Ordinal)
                .ThenBy(static p => p.Message, StringComparer.Ordinal)
        ];
    }

    // glslang messages are "'token' : explanation". The token varies per shader, the explanation does not.
    private static string Normalize(string message)
    {
        var separator = message.IndexOf(" : ", StringComparison.Ordinal);
        return separator < 0 ? message : message[(separator + 3)..];
    }

    private static ShaderLoader.ParsedShaderData Preprocess(string shaderName, ShaderFlavour flavour)
    {
        // The flavour has to be on the ParsedShaderData before the first PreprocessShader call, because
        // that is what ShaderParser reads to decide whether to emit locations and set/binding
        // decorations. Constructing it without one silently preprocesses as OpenGL.
        var parsed = new ShaderLoader.ParsedShaderData { Flavour = flavour };

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

        // Varyings can only be located once every stage that shares them has been read, so this runs
        // after the loop, exactly as ShaderLoader.GetOrParseShader does.
        ShaderParser.StampInterfaceLocations(parsed);

        return parsed;
    }

    /// <summary>
    /// The preamble the renderer prepends, built by the renderer's own code rather than a copy of it.
    /// </summary>
    /// <remarks>
    /// This used to be a reimplementation, and the two drifted: the copy here missed the Vulkan push
    /// constant block, so validation measured a header the renderer never compiles. Calling
    /// <see cref="ShaderLoader.BuildHeader"/> is what makes a measurement here evidence about the real
    /// path. The shader file name is passed as the requested name because validation compiles renderer
    /// shaders as themselves, never as a <c>.vfx</c> variant.
    /// </remarks>
    private static string BuildHeader(ShaderLoader.ParsedShaderData parsed, string shaderName)
        => ShaderLoader.BuildHeader(parsed, shaderName, EmptyArguments);

    private static readonly Dictionary<string, byte> EmptyArguments = [];
}
