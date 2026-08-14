using OpenTK.Graphics.OpenGL;
using GLApi = OpenTK.Graphics.OpenGL.GL;

namespace ValveResourceFormat.Renderer.RHI.OpenGL;

/// <summary>
/// <see cref="ISampler"/> on OpenGL: one sampler object, created with <c>glCreateSamplers</c>.
/// </summary>
/// <remarks>
/// Sampler objects are already how <see cref="Materials.MaterialLoader"/> supplies filtering and wrap
/// state, and they override whatever the texture object carries for the unit they are bound to. That
/// separation is what Vulkan requires, so nothing about this changes shape when the backend does.
/// </remarks>
public sealed class GLSampler : ISampler
{
    /// <summary>Gets the OpenGL sampler object name, or 0 once disposed.</summary>
    public int Handle { get; private set; }

    /// <inheritdoc/>
    public string Name { get; }

    /// <summary>Gets the parameters this sampler was created from.</summary>
    public SamplerDesc Description { get; }

    /// <summary>Creates a sampler.</summary>
    /// <param name="desc">Creation parameters.</param>
    /// <param name="name">Debug name.</param>
    /// <remarks>Anisotropy is applied only when <see cref="SamplerDesc.MaxAnisotropy"/> is greater than 1,
    /// which keeps the existing behaviour of only asking for it when the device reports at least 4x. The
    /// caller is responsible for having clamped the value to
    /// <see cref="IDeviceLimits.MaxSamplerAnisotropy"/>; <see cref="GLDevice.CreateSampler"/> does.</remarks>
    public GLSampler(in SamplerDesc desc, string name)
    {
        Name = name ?? string.Empty;
        Description = desc;

        GLApi.CreateSamplers(1, out int handle);
        Handle = handle;

        GLApi.SamplerParameter(handle, SamplerParameterName.TextureMinFilter, (int)ToGLMinFilter(desc.MinFilter, desc.MipFilter));
        GLApi.SamplerParameter(handle, SamplerParameterName.TextureMagFilter, (int)ToGLMagFilter(desc.MagFilter));
        GLApi.SamplerParameter(handle, SamplerParameterName.TextureWrapS, (int)ToGLWrap(desc.AddressU));
        GLApi.SamplerParameter(handle, SamplerParameterName.TextureWrapT, (int)ToGLWrap(desc.AddressV));
        GLApi.SamplerParameter(handle, SamplerParameterName.TextureWrapR, (int)ToGLWrap(desc.AddressW));

        if (desc.MaxAnisotropy > 1f)
        {
            GLApi.SamplerParameter(handle, (SamplerParameterName)ExtTextureFilterAnisotropic.TextureMaxAnisotropyExt, desc.MaxAnisotropy);
        }

        if (desc.CompareOp is { } compare)
        {
            GLApi.SamplerParameter(handle, SamplerParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
            GLApi.SamplerParameter(handle, SamplerParameterName.TextureCompareFunc, (int)ToGLCompare(compare));
        }

#if DEBUG
        if (Name.Length > 0)
        {
            GLApi.ObjectLabel(ObjectLabelIdentifier.Sampler, handle, Name.Length, Name);
        }
#endif
    }

    private static TextureMinFilter ToGLMinFilter(FilterMode min, MipFilterMode mip) => (min, mip) switch
    {
        (FilterMode.Nearest, MipFilterMode.None) => TextureMinFilter.Nearest,
        (FilterMode.Nearest, MipFilterMode.Nearest) => TextureMinFilter.NearestMipmapNearest,
        (FilterMode.Nearest, MipFilterMode.Linear) => TextureMinFilter.NearestMipmapLinear,
        (FilterMode.Linear, MipFilterMode.None) => TextureMinFilter.Linear,
        (FilterMode.Linear, MipFilterMode.Nearest) => TextureMinFilter.LinearMipmapNearest,
        (FilterMode.Linear, MipFilterMode.Linear) => TextureMinFilter.LinearMipmapLinear,
        _ => throw new ArgumentOutOfRangeException(nameof(min), (min, mip), "Unknown filter combination."),
    };

    private static TextureMagFilter ToGLMagFilter(FilterMode mag) => mag switch
    {
        FilterMode.Nearest => TextureMagFilter.Nearest,
        FilterMode.Linear => TextureMagFilter.Linear,
        _ => throw new ArgumentOutOfRangeException(nameof(mag), mag, "Unknown filter mode."),
    };

    private static TextureWrapMode ToGLWrap(AddressMode address) => address switch
    {
        AddressMode.Repeat => TextureWrapMode.Repeat,
        AddressMode.MirroredRepeat => TextureWrapMode.MirroredRepeat,
        AddressMode.ClampToEdge => TextureWrapMode.ClampToEdge,
        AddressMode.ClampToBorder => TextureWrapMode.ClampToBorder,
        _ => throw new ArgumentOutOfRangeException(nameof(address), address, "Unknown address mode."),
    };

    // The same table RenderState applies to the depth test, so a shadow comparison follows the renderer's
    // reverse-Z convention rather than restating it.
    private static DepthFunction ToGLCompare(Comparison comparison) => comparison switch
    {
        Comparison.Never => DepthFunction.Never,
        Comparison.Less => DepthFunction.Less,
        Comparison.Equal => DepthFunction.Equal,
        Comparison.LessEqual => DepthFunction.Lequal,
        Comparison.Greater => DepthFunction.Greater,
        Comparison.NotEqual => DepthFunction.Notequal,
        Comparison.GreaterEqual => DepthFunction.Gequal,
        Comparison.Always => DepthFunction.Always,
        Comparison.Closer => DepthFunction.Greater,
        Comparison.CloserEqual => DepthFunction.Gequal,
        Comparison.Farther => DepthFunction.Less,
        Comparison.FartherEqual => DepthFunction.Lequal,
        _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison, "Unknown comparison."),
    };

    /// <summary>Deletes the sampler object.</summary>
    public void Dispose()
    {
        if (Handle == 0)
        {
            return;
        }

        GLApi.DeleteSampler(Handle);
        Handle = 0;
    }
}
