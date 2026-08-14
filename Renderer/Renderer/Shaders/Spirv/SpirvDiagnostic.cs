using System.Globalization;

namespace ValveResourceFormat.Renderer.Shaders.Spirv;

/// <summary>How severe a shader compiler diagnostic is.</summary>
public enum SpirvDiagnosticSeverity
{
    /// <summary>Informational text the compiler emitted that carries no location, such as the trailing
    /// "N compilation errors" summary line.</summary>
    Note,

    /// <summary>A warning. Compilation still produced SPIR-V unless warnings were promoted to errors.</summary>
    Warning,

    /// <summary>An error. No SPIR-V was produced.</summary>
    Error,
}

/// <summary>
/// One compiler message, mapped back through <c>#line</c> expansion to the shader file and line the
/// author actually wrote.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ShaderParser"/> inlines every <c>#include</c> and stamps <c>#line &lt;line&gt; &lt;fileIndex&gt;</c>
/// as it goes, where <c>fileIndex</c> indexes <see cref="ShaderLoader.ParsedShaderData.SourceFiles"/>.
/// glslang honours those directives, so the location it reports is already expressed in the original
/// file's coordinates. This type carries that pair resolved to a file name, which is the same fidelity
/// <see cref="ShaderLoader"/> recovers today by regex-matching driver logs, and is what shader hot
/// reload needs to point at the edited line.
/// </para>
/// </remarks>
/// <param name="Severity">How severe the message is.</param>
/// <param name="Message">The message text with the location prefix stripped.</param>
/// <param name="SourceFile">The originating shader file, or <see langword="null"/> when the message
/// carried no location or the index could not be resolved.</param>
/// <param name="SourceFileIndex">Index into the source file list the compile was given, or -1.</param>
/// <param name="Line">One based line number within <paramref name="SourceFile"/>, or -1.</param>
/// <param name="Column">One based column, or -1. glslang does not report columns; Mesa style logs do.</param>
/// <param name="RawText">The compiler's original, unparsed line.</param>
public sealed record SpirvDiagnostic(
    SpirvDiagnosticSeverity Severity,
    string Message,
    string? SourceFile,
    int SourceFileIndex,
    int Line,
    int Column,
    string RawText)
{
    /// <summary>Gets a value indicating whether a file and line were recovered for this diagnostic.</summary>
    public bool HasLocation => Line > 0;

    /// <summary>Formats the diagnostic as <c>file:line: severity: message</c>.</summary>
    public override string ToString()
    {
        var severity = Severity switch
        {
            SpirvDiagnosticSeverity.Error => "error",
            SpirvDiagnosticSeverity.Warning => "warning",
            _ => "note",
        };

        if (!HasLocation)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{severity}: {Message}");
        }

        var file = SourceFile ?? string.Create(CultureInfo.InvariantCulture, $"<source {SourceFileIndex}>");

        return Column > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{file}:{Line}:{Column}: {severity}: {Message}")
            : string.Create(CultureInfo.InvariantCulture, $"{file}:{Line}: {severity}: {Message}");
    }
}
