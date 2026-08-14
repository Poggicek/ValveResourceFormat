using ValveResourceFormat.Renderer.RHI;

namespace ValveResourceFormat.Renderer.Shaders.Spirv;

/// <summary>How hard SPIRV-Tools works on the module before it is returned.</summary>
public enum SpirvOptimizationLevel
{
    /// <summary>No optimisation. Keeps names and the closest correspondence to the source, so this is
    /// what a hot reload build wants.</summary>
    None = 0,

    /// <summary>Optimise for module size.</summary>
    Size = 1,

    /// <summary>Optimise for runtime performance.</summary>
    Performance = 2,
}

/// <summary>Which environment the SPIR-V is produced for.</summary>
public enum SpirvTargetEnvironment
{
    /// <summary>Vulkan 1.0, SPIR-V 1.0.</summary>
    Vulkan10,

    /// <summary>Vulkan 1.1, SPIR-V 1.3.</summary>
    Vulkan11,

    /// <summary>Vulkan 1.2, SPIR-V 1.5.</summary>
    Vulkan12,

    /// <summary>Vulkan 1.3, SPIR-V 1.6. The version the RHI contract targets.</summary>
    Vulkan13,
}

/// <summary>
/// Everything a compile needs beyond the source text and the stage.
/// </summary>
public sealed class SpirvCompileOptions
{
    /// <summary>
    /// Gets the name reported for the shader in diagnostics. glslang prints it wherever the source's
    /// own <c>#line</c> directives have not overridden the file, so it should be the real shader file
    /// name (for example <c>complex.vert.slang</c>) and match <see cref="SourceMap"/> entry zero.
    /// </summary>
    public string FileName { get; init; } = "shader";

    /// <summary>Gets the entry point name. GLSL has no other option than <c>main</c>.</summary>
    public string EntryPoint { get; init; } = "main";

    /// <summary>
    /// Gets the preamble prepended to the source, normally the <c>#version</c> line, the
    /// <c>#extension</c> directives, the resolved static combo defines and the packed globals block.
    /// </summary>
    /// <remarks>
    /// It is passed separately rather than concatenated by the caller so that a <c>#line</c> directive
    /// can be injected after the <c>#version</c> line, which gives errors inside the generated header a
    /// truthful location instead of attributing them to line N of the shader file. See
    /// <see cref="HeaderName"/>.
    /// </remarks>
    public string? Header { get; init; }

    /// <summary>Gets the name reported for diagnostics that land in <see cref="Header"/>.</summary>
    public string HeaderName { get; init; } = "<generated header>";

    /// <summary>
    /// Gets the preprocessor macros to define, on top of anything already in <see cref="Header"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Defines { get; init; }

    /// <summary>
    /// Gets the map from the source's <c>#line</c> file indices to shader file names. Pass a map built
    /// from <see cref="ShaderLoader.ParsedShaderData.SourceFiles"/> to get located diagnostics.
    /// </summary>
    public SpirvSourceMap? SourceMap { get; init; }

    /// <summary>Gets the optimisation level. Defaults to <see cref="SpirvOptimizationLevel.None"/>.</summary>
    public SpirvOptimizationLevel OptimizationLevel { get; init; } = SpirvOptimizationLevel.None;

    /// <summary>Gets the target environment. Defaults to <see cref="SpirvTargetEnvironment.Vulkan13"/>.</summary>
    public SpirvTargetEnvironment Target { get; init; } = SpirvTargetEnvironment.Vulkan13;

    /// <summary>Gets a value indicating whether OpName and OpLine debug information is kept.</summary>
    public bool GenerateDebugInfo { get; init; }

    /// <summary>Gets a value indicating whether warnings fail the compile.</summary>
    public bool WarningsAsErrors { get; init; }

    /// <summary>
    /// Gets a value indicating whether glslang assigns locations and bindings to declarations that do
    /// not carry them.
    /// </summary>
    /// <remarks>
    /// Off by default and it should stay off for anything the renderer ships. The numbers glslang
    /// invents do not follow the descriptor set scheme in the RHI contract, so a pipeline layout built
    /// from <see cref="DescriptorSets"/> would bind the wrong resource without erroring. It exists to
    /// let a GL flavoured shader be compiled for triage before the decorations are written.
    /// </remarks>
    public bool AutoAssignBindings { get; init; }

    /// <summary>The defaults: Vulkan 1.3, unoptimised, no auto binding.</summary>
    public static SpirvCompileOptions Default { get; } = new();
}
