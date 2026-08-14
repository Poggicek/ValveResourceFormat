using System.Runtime.InteropServices;
using System.Text;

namespace ValveResourceFormat.Renderer.Shaders.Spirv;

// The shaderc C ABI (libshaderc/include/shaderc/shaderc.h). shaderc_shared embeds glslang and
// SPIRV-Tools, so this is glslang reached through the only stable C entry point either project ships.
// Every signature here is blittable so the LibraryImport generator emits no marshalling stubs, which
// is what keeps the module PublishAot clean.
//
// Every import carries DefaultDllImportSearchPaths(SafeDirectories), which keeps the OS from falling
// back to the current working directory and makes a planted shaderc_shared.dll harder. It does not
// stop the module being found: the runtime resolves native assets from deps.json and from the
// directory a single file host extracts into, handing LoadLibrary an absolute path before these flags
// ever apply. SpirvCompiler.EnsureNativeLibrary does the same resolution up front.
#pragma warning disable CA1707 // Identifiers should not contain underscores - these mirror the C names exactly

internal static unsafe partial class ShadercNative
{
    /// <summary>The native module name, resolved by the default probing logic to
    /// <c>shaderc_shared.dll</c>, <c>libshaderc_shared.so</c> or <c>libshaderc_shared.dylib</c>.</summary>
    internal const string LibraryName = "shaderc_shared";

    private const DllImportSearchPath SearchPaths = DllImportSearchPath.SafeDirectories;

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial nint shaderc_compiler_initialize();

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial void shaderc_compiler_release(nint compiler);

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial nint shaderc_compile_options_initialize();

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial void shaderc_compile_options_release(nint options);

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial void shaderc_compile_options_add_macro_definition(nint options, byte* name, nuint nameLength, byte* value, nuint valueLength);

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial void shaderc_compile_options_set_source_language(nint options, int language);

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial void shaderc_compile_options_set_optimization_level(nint options, int level);

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial void shaderc_compile_options_set_target_env(nint options, int target, uint version);

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial void shaderc_compile_options_set_target_spirv(nint options, uint version);

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial void shaderc_compile_options_set_generate_debug_info(nint options);

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial void shaderc_compile_options_set_warnings_as_errors(nint options);

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial void shaderc_compile_options_set_auto_map_locations(nint options, [MarshalAs(UnmanagedType.U1)] bool autoMap);

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial void shaderc_compile_options_set_auto_bind_uniforms(nint options, [MarshalAs(UnmanagedType.U1)] bool autoBind);

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial nint shaderc_compile_into_spv(nint compiler, byte* sourceText, nuint sourceTextSize, int shaderKind, byte* inputFileName, byte* entryPointName, nint additionalOptions);

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial void shaderc_result_release(nint result);

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial nuint shaderc_result_get_length(nint result);

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial nuint shaderc_result_get_num_warnings(nint result);

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial nuint shaderc_result_get_num_errors(nint result);

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial int shaderc_result_get_compilation_status(nint result);

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial byte* shaderc_result_get_bytes(nint result);

    [LibraryImport(LibraryName)]
    [DefaultDllImportSearchPaths(SearchPaths)]
    internal static partial byte* shaderc_result_get_error_message(nint result);

    // shaderc_shader_kind
    internal const int KindVertex = 0;
    internal const int KindFragment = 1;
    internal const int KindCompute = 2;

    // shaderc_source_language
    internal const int SourceLanguageGlsl = 0;

    // shaderc_target_env
    internal const int TargetEnvVulkan = 0;
    internal const int TargetEnvOpenGl = 1;

    // shaderc_env_version
    internal const uint EnvVersionVulkan10 = 1u << 22;
    internal const uint EnvVersionVulkan11 = (1u << 22) | (1u << 12);
    internal const uint EnvVersionVulkan12 = (1u << 22) | (2u << 12);
    internal const uint EnvVersionVulkan13 = (1u << 22) | (3u << 12);

    // shaderc_spirv_version
    internal const uint SpirvVersion10 = 0x0001_0000;
    internal const uint SpirvVersion13 = 0x0001_0300;
    internal const uint SpirvVersion15 = 0x0001_0500;
    internal const uint SpirvVersion16 = 0x0001_0600;

    /// <summary>Reads a NUL terminated UTF-8 string returned by the native library.</summary>
    internal static string ReadUtf8(byte* value)
        => value == null ? string.Empty : Marshal.PtrToStringUTF8((nint)value) ?? string.Empty;

    /// <summary>
    /// Encodes <paramref name="value"/> as NUL terminated UTF-8. The terminator is required by every
    /// shaderc entry point that does not also take a length.
    /// </summary>
    internal static byte[] EncodeNullTerminated(string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        var buffer = new byte[count + 1];
        Encoding.UTF8.GetBytes(value, buffer);
        return buffer;
    }
}

#pragma warning restore CA1707
