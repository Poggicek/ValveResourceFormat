using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace ValveResourceFormat.Renderer.Shaders.Spirv;

/// <summary>Why a compile ended the way it did. Mirrors <c>shaderc_compilation_status</c>.</summary>
public enum SpirvCompilationStatus
{
    /// <summary>SPIR-V was produced.</summary>
    Success = 0,

    /// <summary>The source declared a stage that does not match the one requested.</summary>
    InvalidStage = 1,

    /// <summary>The source failed to compile.</summary>
    CompilationError = 2,

    /// <summary>An unexpected failure inside the compiler.</summary>
    InternalError = 3,

    /// <summary>The compiler returned nothing.</summary>
    NullResultObject = 4,

    /// <summary>Assembly text could not be parsed.</summary>
    InvalidAssembly = 5,

    /// <summary>The produced module failed validation.</summary>
    ValidationError = 6,

    /// <summary>An optimisation pass failed.</summary>
    TransformationError = 7,

    /// <summary>The options given were not valid for the target.</summary>
    ConfigurationError = 8,

    /// <summary>The native compiler could not be loaded. Not a shaderc status.</summary>
    CompilerUnavailable = 100,
}

/// <summary>
/// The outcome of one GLSL to SPIR-V compile: either the module, or the diagnostics explaining why
/// there isn't one.
/// </summary>
public sealed class SpirvCompilationResult
{
    /// <summary>Gets the status the compiler finished with.</summary>
    public SpirvCompilationStatus Status { get; }

    /// <summary>
    /// Gets the SPIR-V module, ready to hand to
    /// <see cref="RHI.IDevice.CreateShaderModule(ReadOnlySpan{byte}, RHI.ShaderStage, string)"/>.
    /// Empty when the compile failed.
    /// </summary>
    public ReadOnlyMemory<byte> Spirv { get; }

    /// <summary>Gets every message the compiler produced, located where one was recoverable.</summary>
    public ImmutableArray<SpirvDiagnostic> Diagnostics { get; }

    /// <summary>Gets the compiler's unparsed output, kept so nothing is lost if parsing missed a line.</summary>
    public string Log { get; }

    /// <summary>Gets a value indicating whether a module was produced.</summary>
    public bool Success => Status == SpirvCompilationStatus.Success;

    /// <summary>Gets the errors, in the order the compiler emitted them.</summary>
    public IEnumerable<SpirvDiagnostic> Errors
        => Diagnostics.Where(static d => d.Severity == SpirvDiagnosticSeverity.Error);

    /// <summary>Gets the warnings, in the order the compiler emitted them.</summary>
    public IEnumerable<SpirvDiagnostic> Warnings
        => Diagnostics.Where(static d => d.Severity == SpirvDiagnosticSeverity.Warning);

    /// <summary>
    /// Gets the first error that carries a file and line, which is the one worth pointing a user at.
    /// glslang's trailing count line has no location and would otherwise win.
    /// </summary>
    public SpirvDiagnostic? PrimaryError
        => Diagnostics.FirstOrDefault(static d => d.Severity == SpirvDiagnosticSeverity.Error && d.HasLocation)
        ?? Diagnostics.FirstOrDefault(static d => d.Severity == SpirvDiagnosticSeverity.Error);

    internal SpirvCompilationResult(SpirvCompilationStatus status, ReadOnlyMemory<byte> spirv, ImmutableArray<SpirvDiagnostic> diagnostics, string log)
    {
        Status = status;
        Spirv = spirv;
        Diagnostics = diagnostics;
        Log = log;
    }

    /// <summary>Builds a message listing every diagnostic, one per line.</summary>
    /// <returns>The formatted diagnostics, or an empty string when there were none.</returns>
    public string FormatDiagnostics()
    {
        if (Diagnostics.IsDefaultOrEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        foreach (var diagnostic in Diagnostics)
        {
            builder.Append(diagnostic.ToString());
            builder.Append('\n');
        }

        return builder.ToString();
    }
}
