using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ValveResourceFormat.Renderer.RHI;

namespace ValveResourceFormat.Renderer.Shaders.Spirv;

/// <summary>
/// Compiles GLSL 460 to SPIR-V through glslang, reached via the shaderc C ABI.
/// </summary>
/// <remarks>
/// <para>
/// The shader sources carry a <c>.slang</c> extension but are GLSL 460, not Slang. Real Slang is not
/// involved anywhere in this module.
/// </para>
/// <para>
/// Sources are expected to arrive already flattened by <see cref="ShaderParser"/>, so no include
/// resolver is installed: every <c>#include</c> has been inlined and replaced with <c>#line</c>
/// directives by the time the text reaches here. Those directives are what
/// <see cref="SpirvSourceMap"/> reads back to locate diagnostics.
/// </para>
/// <para>
/// A <see cref="SpirvCompiler"/> may be used from several threads at once. shaderc only requires
/// synchronisation for entry points that take a non-const compiler, and compilation is not one of them.
/// </para>
/// </remarks>
public sealed class SpirvCompiler : IDisposable
{
    private nint compiler;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpirvCompiler"/> class.
    /// </summary>
    /// <exception cref="SpirvCompilerUnavailableException">The native compiler could not be loaded.</exception>
    public SpirvCompiler()
    {
        EnsureNativeLibrary();

        compiler = ShadercNative.shaderc_compiler_initialize();

        if (compiler == 0)
        {
            throw new SpirvCompilerUnavailableException("shaderc_compiler_initialize returned null.");
        }
    }

    private static readonly Lock InstanceLock = new();
    private static SpirvCompiler? shared;

    /// <summary>
    /// Gets a process wide compiler. Creating one is cheap but not free, and the instance is safe to
    /// share across threads.
    /// </summary>
    /// <exception cref="SpirvCompilerUnavailableException">The native compiler could not be loaded.</exception>
    public static SpirvCompiler Shared
    {
        get
        {
            if (shared != null)
            {
                return shared;
            }

            using var _ = InstanceLock.EnterScope();
            return shared ??= new SpirvCompiler();
        }
    }

    /// <summary>
    /// Compiles GLSL to SPIR-V.
    /// </summary>
    /// <param name="source">The shader source, with every <c>#include</c> already flattened.</param>
    /// <param name="stage">Exactly one of <see cref="ShaderStage.Vertex"/>,
    /// <see cref="ShaderStage.Fragment"/> or <see cref="ShaderStage.Compute"/>.</param>
    /// <param name="options">Compile options, or <see langword="null"/> for
    /// <see cref="SpirvCompileOptions.Default"/>.</param>
    /// <returns>The module, or the diagnostics explaining why there isn't one.</returns>
    /// <exception cref="ArgumentException"><paramref name="stage"/> is not a single supported stage.</exception>
    public SpirvCompilationResult Compile(string source, ShaderStage stage, SpirvCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(disposed, this);

        options ??= SpirvCompileOptions.Default;

        var kind = stage switch
        {
            ShaderStage.Vertex => ShadercNative.KindVertex,
            ShaderStage.Fragment => ShadercNative.KindFragment,
            ShaderStage.Compute => ShadercNative.KindCompute,
            _ => throw new ArgumentException($"'{stage}' is not a single compilable stage.", nameof(stage)),
        };

        var (text, sourceMap) = AssembleSource(source, options);

        return CompileCore(text, kind, sourceMap, options);
    }

    /// <summary>
    /// Compiles GLSL to SPIR-V for a <see cref="ShaderProgramType"/>, the stage enum the existing
    /// shader loader uses.
    /// </summary>
    /// <param name="source">The shader source, with every <c>#include</c> already flattened.</param>
    /// <param name="stage">The stage to compile for.</param>
    /// <param name="options">Compile options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The module, or the diagnostics explaining why there isn't one.</returns>
    public SpirvCompilationResult Compile(string source, ShaderProgramType stage, SpirvCompileOptions? options = null)
        => Compile(source, ToShaderStage(stage), options);

    /// <summary>Maps a <see cref="ShaderProgramType"/> to the RHI stage flag for it.</summary>
    /// <param name="stage">The loader's stage enum.</param>
    /// <returns>The matching <see cref="ShaderStage"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stage"/> has no RHI equivalent.</exception>
    public static ShaderStage ToShaderStage(ShaderProgramType stage) => stage switch
    {
        ShaderProgramType.Vertex => ShaderStage.Vertex,
        ShaderProgramType.Fragment => ShaderStage.Fragment,
        ShaderProgramType.Compute => ShaderStage.Compute,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
    };

    /// <summary>
    /// Joins the generated header to the shader body, injecting a <c>#line</c> directive so that a
    /// diagnostic landing in the header is attributed to the header rather than to the shader file.
    /// </summary>
    private static (string Text, SpirvSourceMap Map) AssembleSource(string source, SpirvCompileOptions options)
    {
        var map = options.SourceMap ?? SpirvSourceMap.Empty;

        if (string.IsNullOrEmpty(options.Header))
        {
            return (source, map);
        }

        var (extendedMap, headerIndex) = map.WithSynthetic(options.HeaderName);

        var header = options.Header;
        var builder = new StringBuilder(header.Length + source.Length + 32);

        // #version has to stay the first token in the unit, so the directive goes on the line after it.
        var firstBreak = header.IndexOf('\n', StringComparison.Ordinal);

        if (firstBreak >= 0 && header.AsSpan().TrimStart().StartsWith("#version", StringComparison.Ordinal))
        {
            builder.Append(header, 0, firstBreak + 1);
            builder.Append(CultureInfo.InvariantCulture, $"#line 2 {headerIndex}\n");
            builder.Append(header, firstBreak + 1, header.Length - firstBreak - 1);
        }
        else
        {
            builder.Append(header);
        }

        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.Append('\n');
        }

        builder.Append(source);

        return (builder.ToString(), extendedMap);
    }

    private unsafe SpirvCompilationResult CompileCore(string text, int kind, SpirvSourceMap sourceMap, SpirvCompileOptions options)
    {
        var optionsHandle = ShadercNative.shaderc_compile_options_initialize();

        if (optionsHandle == 0)
        {
            return new SpirvCompilationResult(
                SpirvCompilationStatus.ConfigurationError,
                default,
                [new SpirvDiagnostic(SpirvDiagnosticSeverity.Error, "shaderc_compile_options_initialize returned null.", null, -1, -1, -1, string.Empty)],
                string.Empty);
        }

        try
        {
            ApplyOptions(optionsHandle, options);

            var sourceBytes = Encoding.UTF8.GetBytes(text);
            var fileNameBytes = ShadercNative.EncodeNullTerminated(options.FileName);
            var entryPointBytes = ShadercNative.EncodeNullTerminated(options.EntryPoint);

            nint result;

            fixed (byte* sourcePtr = sourceBytes)
            fixed (byte* fileNamePtr = fileNameBytes)
            fixed (byte* entryPointPtr = entryPointBytes)
            {
                result = ShadercNative.shaderc_compile_into_spv(
                    compiler,
                    sourcePtr,
                    (nuint)sourceBytes.Length,
                    kind,
                    fileNamePtr,
                    entryPointPtr,
                    optionsHandle);
            }

            if (result == 0)
            {
                return new SpirvCompilationResult(
                    SpirvCompilationStatus.NullResultObject,
                    default,
                    [new SpirvDiagnostic(SpirvDiagnosticSeverity.Error, "shaderc_compile_into_spv returned null.", null, -1, -1, -1, string.Empty)],
                    string.Empty);
            }

            try
            {
                var status = (SpirvCompilationStatus)ShadercNative.shaderc_result_get_compilation_status(result);
                var log = ShadercNative.ReadUtf8(ShadercNative.shaderc_result_get_error_message(result));
                var diagnostics = SpirvDiagnosticParser.Parse(log, sourceMap);

                var spirv = ReadOnlyMemory<byte>.Empty;

                if (status == SpirvCompilationStatus.Success)
                {
                    var length = (int)ShadercNative.shaderc_result_get_length(result);
                    var bytes = ShadercNative.shaderc_result_get_bytes(result);

                    if (length > 0 && bytes != null)
                    {
                        var copy = new byte[length];
                        new ReadOnlySpan<byte>(bytes, length).CopyTo(copy);
                        spirv = copy;
                    }
                }

                return new SpirvCompilationResult(status, spirv, diagnostics, log);
            }
            finally
            {
                ShadercNative.shaderc_result_release(result);
            }
        }
        finally
        {
            ShadercNative.shaderc_compile_options_release(optionsHandle);
        }
    }

    private static unsafe void ApplyOptions(nint handle, SpirvCompileOptions options)
    {
        ShadercNative.shaderc_compile_options_set_source_language(handle, ShadercNative.SourceLanguageGlsl);
        ShadercNative.shaderc_compile_options_set_optimization_level(handle, (int)options.OptimizationLevel);

        var (envVersion, spirvVersion) = options.Target switch
        {
            SpirvTargetEnvironment.Vulkan10 => (ShadercNative.EnvVersionVulkan10, ShadercNative.SpirvVersion10),
            SpirvTargetEnvironment.Vulkan11 => (ShadercNative.EnvVersionVulkan11, ShadercNative.SpirvVersion13),
            SpirvTargetEnvironment.Vulkan12 => (ShadercNative.EnvVersionVulkan12, ShadercNative.SpirvVersion15),
            _ => (ShadercNative.EnvVersionVulkan13, ShadercNative.SpirvVersion16),
        };

        ShadercNative.shaderc_compile_options_set_target_env(handle, ShadercNative.TargetEnvVulkan, envVersion);
        ShadercNative.shaderc_compile_options_set_target_spirv(handle, spirvVersion);

        if (options.GenerateDebugInfo)
        {
            ShadercNative.shaderc_compile_options_set_generate_debug_info(handle);
        }

        if (options.WarningsAsErrors)
        {
            ShadercNative.shaderc_compile_options_set_warnings_as_errors(handle);
        }

        if (options.AutoAssignBindings)
        {
            ShadercNative.shaderc_compile_options_set_auto_map_locations(handle, true);
            ShadercNative.shaderc_compile_options_set_auto_bind_uniforms(handle, true);
        }

        if (options.Defines == null)
        {
            return;
        }

        foreach (var (name, value) in options.Defines)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var valueBytes = Encoding.UTF8.GetBytes(value ?? string.Empty);

            fixed (byte* namePtr = nameBytes)
            fixed (byte* valuePtr = valueBytes)
            {
                ShadercNative.shaderc_compile_options_add_macro_definition(
                    handle,
                    namePtr,
                    (nuint)nameBytes.Length,
                    valueBytes.Length == 0 ? null : valuePtr,
                    (nuint)valueBytes.Length);
            }
        }
    }

    private static int nativeLibraryState;

    /// <summary>
    /// Gets or sets an explicit path to the native shaderc module, loaded instead of the one the
    /// default probing logic would find. Set it before the first compile.
    /// </summary>
    /// <remarks>
    /// The module normally ships as a runtime asset and needs no help. This exists for a host that
    /// places it somewhere the loader would not look.
    /// </remarks>
    public static string? NativeLibraryPath { get; set; }

    /// <summary>
    /// Gets a value indicating whether the native compiler can be loaded in this process.
    /// </summary>
    /// <remarks>
    /// Worth checking before offering a Vulkan backend at all: a missing native module is a startup
    /// concern, not something to discover on the first shader compile.
    /// </remarks>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                EnsureNativeLibrary();
                return true;
            }
            catch (SpirvCompilerUnavailableException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Loads the native module, so that a failure surfaces here with a usable message rather than as a
    /// <see cref="DllNotFoundException"/> from an arbitrary entry point.
    /// </summary>
    /// <exception cref="SpirvCompilerUnavailableException">The module could not be loaded.</exception>
    private static void EnsureNativeLibrary()
    {
        if (Volatile.Read(ref nativeLibraryState) == 1)
        {
            return;
        }

        var path = NativeLibraryPath;

        if (!string.IsNullOrEmpty(path))
        {
            if (!NativeLibrary.TryLoad(path, out _))
            {
                throw new SpirvCompilerUnavailableException(
                    $"The native shader compiler at '{path}' could not be loaded.");
            }

            Volatile.Write(ref nativeLibraryState, 1);
            return;
        }

        // Default probing already covers the lib prefix and the platform extension, so the bare name
        // finds shaderc_shared.dll, libshaderc_shared.so and libshaderc_shared.dylib alike.
        if (!NativeLibrary.TryLoad(ShadercNative.LibraryName, typeof(SpirvCompiler).Assembly, null, out _))
        {
            throw new SpirvCompilerUnavailableException(
                $"The native shader compiler '{ShadercNative.LibraryName}' could not be loaded. It ships as a "
                + "runtime asset of the Silk.NET.Shaderc.Native package; check that the package is referenced and "
                + "that its native asset reached the output directory.");
        }

        Volatile.Write(ref nativeLibraryState, 1);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the native compiler if a caller dropped the instance without disposing it. glslang
    /// holds pools behind the handle, so leaking one leaks native memory for the process lifetime.
    /// </summary>
    ~SpirvCompiler()
    {
        Dispose(false);
    }

    private void Dispose(bool disposing)
    {
        _ = disposing;

        if (disposed)
        {
            return;
        }

        disposed = true;

        if (compiler != 0)
        {
            ShadercNative.shaderc_compiler_release(compiler);
            compiler = 0;
        }
    }
}

/// <summary>
/// Thrown when the native shader compiler is not present or cannot be loaded.
/// </summary>
public class SpirvCompilerUnavailableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SpirvCompilerUnavailableException"/> class.</summary>
    public SpirvCompilerUnavailableException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SpirvCompilerUnavailableException"/> class with a message.</summary>
    /// <param name="message">A description of what could not be loaded.</param>
    public SpirvCompilerUnavailableException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SpirvCompilerUnavailableException"/> class with a message and an inner exception.</summary>
    /// <param name="message">A description of what could not be loaded.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public SpirvCompilerUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
