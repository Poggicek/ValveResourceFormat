using System.Text;
using OpenTK.Graphics.OpenGL;
using GLApi = OpenTK.Graphics.OpenGL.GL;

namespace ValveResourceFormat.Renderer.RHI.OpenGL;

/// <summary>
/// <see cref="IShaderModule"/> on OpenGL: one compiled shader object, ready to be attached to a program.
/// </summary>
/// <remarks>
/// <para>
/// A module is a compiled stage, not a linked program. OpenGL cannot draw with one on its own, so
/// <see cref="GLGraphicsPipeline"/> takes a linked <see cref="Shader"/> instead; this type exists so the
/// contract's <see cref="IDevice.CreateShaderModule"/> has a real implementation and so a pipeline cache
/// key has a content hash to be built from.
/// </para>
/// <para>
/// The code is GLSL source on this backend and SPIR-V on Vulkan, which is why the interface takes bytes
/// rather than a string. Producing either is <see cref="ShaderLoader"/>'s job, not the device's.
/// </para>
/// </remarks>
public sealed class GLShaderModule : IShaderModule
{
    /// <summary>Gets the OpenGL shader object name, or 0 once disposed.</summary>
    public int Handle { get; private set; }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public ShaderStage Stage { get; }

    /// <inheritdoc/>
    public ulong ContentHash { get; }

    /// <summary>Compiles a shader stage from GLSL source.</summary>
    /// <param name="code">The GLSL source, UTF-8 encoded.</param>
    /// <param name="stage">The stage to compile it for.</param>
    /// <param name="name">Debug name.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stage"/> is not a single stage this
    /// backend compiles.</exception>
    /// <exception cref="InvalidOperationException">The source failed to compile. The message carries the
    /// driver's info log.</exception>
    public GLShaderModule(ReadOnlySpan<byte> code, ShaderStage stage, string name)
    {
        Name = name ?? string.Empty;
        Stage = stage;
        ContentHash = Fnv1a(code);

        Handle = GLApi.CreateShader(ToGLShaderType(stage));
        GLApi.ShaderSource(Handle, Encoding.UTF8.GetString(code));
        GLApi.CompileShader(Handle);
        GLApi.GetShader(Handle, ShaderParameter.CompileStatus, out var status);

        if (status != 1)
        {
            var log = GLApi.GetShaderInfoLog(Handle);

            GLApi.DeleteShader(Handle);
            Handle = 0;

            throw new InvalidOperationException($"Failed to compile {stage} shader '{Name}': {log}");
        }

#if DEBUG
        if (Name.Length > 0)
        {
            GLApi.ObjectLabel(ObjectLabelIdentifier.Shader, Handle, Name.Length, Name);
        }
#endif
    }

    private static ShaderType ToGLShaderType(ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => ShaderType.VertexShader,
        ShaderStage.Fragment => ShaderType.FragmentShader,
        ShaderStage.Compute => ShaderType.ComputeShader,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "A shader module is one stage; AllGraphics and None cannot be compiled."),
    };

    // FNV-1a, matching GLGraphicsPipeline: HashCode is seeded per process, and a pipeline cache that
    // outlives a run would miss every entry if the hash changed between runs.
    private const ulong FnvOffsetBasis = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    private static ulong Fnv1a(ReadOnlySpan<byte> data)
    {
        var hash = FnvOffsetBasis;

        foreach (var b in data)
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        return hash;
    }

    /// <summary>Deletes the shader object. A program that has already linked it is unaffected.</summary>
    public void Dispose()
    {
        if (Handle == 0)
        {
            return;
        }

        GLApi.DeleteShader(Handle);
        Handle = 0;
    }
}
