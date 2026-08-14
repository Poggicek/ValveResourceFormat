using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace ValveResourceFormat.Renderer.Shaders.Spirv;

/// <summary>
/// Resolves the file token in a compiler message back to the shader file the author wrote.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ShaderParser"/> flattens every <c>#include</c> into one translation unit and stamps
/// <c>#line &lt;line&gt; &lt;index&gt;</c> at each transition, where <c>index</c> is the position in
/// <see cref="ShaderLoader.ParsedShaderData.SourceFiles"/>. glslang honours those directives, so the
/// line it reports is already the original file's line and only the index has to be mapped.
/// </para>
/// <para>
/// The token is not always a number. glslang prints the name it was given for the string the source
/// was submitted as, so index 0 usually appears as the shader file name while every included file
/// appears as its bare index. Both are resolved here.
/// </para>
/// </remarks>
public sealed class SpirvSourceMap
{
    private readonly ImmutableArray<string> sourceFiles;
    private readonly Dictionary<string, int> byName;

    /// <summary>Gets the shader files, indexed as the <c>#line</c> directives number them.</summary>
    public ImmutableArray<string> SourceFiles => sourceFiles;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpirvSourceMap"/> class.
    /// </summary>
    /// <param name="sourceFiles">The file names in <c>#line</c> index order. Pass
    /// <see cref="ShaderLoader.ParsedShaderData.SourceFiles"/> straight through.</param>
    public SpirvSourceMap(IEnumerable<string> sourceFiles)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);

        this.sourceFiles = [.. sourceFiles];
        byName = new Dictionary<string, int>(this.sourceFiles.Length, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < this.sourceFiles.Length; i++)
        {
            // First wins: an include pulled in twice keeps its first index, matching the parser.
            byName.TryAdd(this.sourceFiles[i], i);

            var leaf = LeafName(this.sourceFiles[i]);

            if (leaf != this.sourceFiles[i])
            {
                byName.TryAdd(leaf, i);
            }
        }
    }

    /// <summary>An empty map, for compiles that were not produced by <see cref="ShaderParser"/>.</summary>
    public static SpirvSourceMap Empty { get; } = new([]);

    /// <summary>
    /// Adds a synthetic entry and returns its index. Used for the generated header, which has no file
    /// of its own but still has to be nameable when an error lands in it.
    /// </summary>
    /// <param name="name">The display name to register.</param>
    /// <returns>A map that also contains <paramref name="name"/>, and the index it was given.</returns>
    public (SpirvSourceMap Map, int Index) WithSynthetic(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return (new SpirvSourceMap(sourceFiles.Append(name)), sourceFiles.Length);
    }

    /// <summary>Resolves a file token from a compiler message.</summary>
    /// <param name="fileToken">The text the compiler printed where a file belongs: an index, a file
    /// name, or something unrecognised.</param>
    /// <returns>The index into <see cref="SourceFiles"/> and the resolved file name. The index is -1
    /// and the name is the token itself when it does not resolve.</returns>
    public (int Index, string? Name) Resolve(string fileToken)
    {
        if (string.IsNullOrEmpty(fileToken))
        {
            return (-1, null);
        }

        if (int.TryParse(fileToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return index >= 0 && index < sourceFiles.Length
                ? (index, sourceFiles[index])
                : (index, null);
        }

        if (byName.TryGetValue(fileToken, out var named))
        {
            return (named, sourceFiles[named]);
        }

        var leaf = LeafName(fileToken);

        return byName.TryGetValue(leaf, out var byLeaf)
            ? (byLeaf, sourceFiles[byLeaf])
            : (-1, fileToken);
    }

    private static string LeafName(string path)
    {
        var slash = path.LastIndexOfAny(['/', '\\']);
        return slash < 0 ? path : path[(slash + 1)..];
    }
}
