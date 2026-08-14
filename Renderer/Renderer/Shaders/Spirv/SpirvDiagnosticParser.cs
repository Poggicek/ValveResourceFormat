using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ValveResourceFormat.Renderer.Shaders.Spirv;

/// <summary>
/// Turns a shader compiler log into located <see cref="SpirvDiagnostic"/> values.
/// </summary>
/// <remarks>
/// <para>
/// Recognises the glslang log shaderc returns as well as the three driver formats
/// <see cref="ShaderLoader"/> already parses, so one parser serves both the SPIR-V path and the
/// existing OpenGL path rather than the two drifting apart.
/// </para>
/// <para>
/// The file token is whatever <c>#line</c> put there. <see cref="ShaderParser"/> stamps numeric
/// indices into <see cref="ShaderLoader.ParsedShaderData.SourceFiles"/>, but glslang substitutes the
/// name it was given for the string the source was submitted as, so both a number and a file name
/// have to resolve.
/// </para>
/// </remarks>
public static partial class SpirvDiagnosticParser
{
    // glslang: "ERROR: 0:57: 'foo' : undeclared identifier"
    [GeneratedRegex(@"^(?<Severity>ERROR|WARNING):\s+(?<File>[^:\s][^:]*):(?<Line>[0-9]+):\s*(?<Message>.*)$")]
    private static partial Regex GlslangError();

    // shaderc / gcc style: "complex.vert.slang:57: error: 'foo' : undeclared identifier"
    [GeneratedRegex(@"^(?<File>[^:]+):(?<Line>[0-9]+):\s*(?<Severity>error|warning|note):\s*(?<Message>.*)$")]
    private static partial Regex GccStyleError();

    // Mesa: "0:57(12): error: ..."
    [GeneratedRegex(@"^(?<File>[^:]+):(?<Line>[0-9]+)\((?<Column>[0-9]+)\):\s*(?<Message>.*)$")]
    private static partial Regex Mesa3dError();

    // Nvidia: "0(57) : error C1503: ..."
    [GeneratedRegex(@"^(?<File>[^(]+)\((?<Line>[0-9]+)\)\s*:\s*(?<Severity>error|warning)\s*(?<Message>.*)$")]
    private static partial Regex NvidiaError();

    // A message with a severity but no location, such as glslang's trailing count line.
    [GeneratedRegex(@"^(?<Severity>ERROR|WARNING):\s*(?<Message>.*)$")]
    private static partial Regex SeverityOnly();

    // Ordered most specific first. GlslangError leads because its "ERROR: " prefix would otherwise be
    // eaten by GccStyleError treating "ERROR" as a file name.
    private static readonly Regex[] LocatedFormats = [GlslangError(), GccStyleError(), Mesa3dError(), NvidiaError()];

    /// <summary>
    /// Parses a compiler log into one diagnostic per recognised line.
    /// </summary>
    /// <param name="log">The raw compiler output.</param>
    /// <param name="sourceMap">Resolves the file token in a message to a shader file name. May be
    /// <see langword="null"/>, in which case the token is reported verbatim.</param>
    /// <returns>The diagnostics, in the order the compiler emitted them.</returns>
    public static ImmutableArray<SpirvDiagnostic> Parse(string? log, SpirvSourceMap? sourceMap)
    {
        if (string.IsNullOrWhiteSpace(log))
        {
            return [];
        }

        var diagnostics = ImmutableArray.CreateBuilder<SpirvDiagnostic>();

        foreach (var rawLine in log.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r', ' ', '\t');

            if (line.Length == 0)
            {
                continue;
            }

            diagnostics.Add(ParseLine(line, sourceMap));
        }

        return diagnostics.ToImmutable();
    }

    /// <summary>Parses a single compiler log line.</summary>
    /// <param name="line">One line of compiler output.</param>
    /// <param name="sourceMap">Resolves the file token, or <see langword="null"/> to report it verbatim.</param>
    /// <returns>The diagnostic. Unrecognised text becomes a <see cref="SpirvDiagnosticSeverity.Note"/>
    /// carrying no location, never a dropped message.</returns>
    public static SpirvDiagnostic ParseLine(string line, SpirvSourceMap? sourceMap)
    {
        ArgumentNullException.ThrowIfNull(line);

        foreach (var regex in LocatedFormats)
        {
            var match = regex.Match(line);

            if (!match.Success)
            {
                continue;
            }

            var fileToken = match.Groups["File"].Value.Trim();
            var lineNumber = int.Parse(match.Groups["Line"].Value, CultureInfo.InvariantCulture);
            var column = match.Groups["Column"].Success
                ? int.Parse(match.Groups["Column"].Value, CultureInfo.InvariantCulture)
                : -1;

            var (fileIndex, fileName) = Resolve(fileToken, sourceMap);

            return new SpirvDiagnostic(
                ParseSeverity(match.Groups["Severity"], SpirvDiagnosticSeverity.Error),
                match.Groups["Message"].Value.Trim(),
                fileName,
                fileIndex,
                lineNumber,
                column,
                line);
        }

        var severityOnly = SeverityOnly().Match(line);

        if (severityOnly.Success)
        {
            return new SpirvDiagnostic(
                ParseSeverity(severityOnly.Groups["Severity"], SpirvDiagnosticSeverity.Error),
                severityOnly.Groups["Message"].Value.Trim(),
                null,
                -1,
                -1,
                -1,
                line);
        }

        return new SpirvDiagnostic(SpirvDiagnosticSeverity.Note, line, null, -1, -1, -1, line);
    }

    private static SpirvDiagnosticSeverity ParseSeverity(Group group, SpirvDiagnosticSeverity fallback)
    {
        if (!group.Success)
        {
            return fallback;
        }

        return group.Value.ToUpperInvariant() switch
        {
            "ERROR" => SpirvDiagnosticSeverity.Error,
            "WARNING" => SpirvDiagnosticSeverity.Warning,
            "NOTE" => SpirvDiagnosticSeverity.Note,
            _ => fallback,
        };
    }

    private static (int Index, string? Name) Resolve(string fileToken, SpirvSourceMap? sourceMap)
    {
        if (sourceMap == null)
        {
            return (-1, fileToken.Length == 0 ? null : fileToken);
        }

        return sourceMap.Resolve(fileToken);
    }
}
