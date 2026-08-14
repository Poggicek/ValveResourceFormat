using OpenTK.Graphics.OpenGL;
using GLApi = OpenTK.Graphics.OpenGL.GL;

namespace ValveResourceFormat.Renderer.RHI.OpenGL;

/// <summary>
/// What the OpenGL context this device runs on can do, queried once at device creation.
/// </summary>
/// <remarks>
/// Queried rather than assumed, per the contract. The values come from the context that is current when
/// the device is constructed, so a device must be created with its context current.
/// </remarks>
public sealed class GLDeviceLimits : IDeviceLimits
{
    /// <inheritdoc/>
    /// <remarks>OpenGL has no push constants. The block is emulated as loose program uniforms by
    /// <see cref="GLPushConstantBlock"/>, which has no size limit worth reporting, so this reports the
    /// 128 byte floor every Vulkan implementation guarantees. A block that fits here fits everywhere.</remarks>
    public int MaxPushConstantSize => 128;

    /// <inheritdoc/>
    public int MaxUniformBufferRange { get; }

    /// <inheritdoc/>
    public float MaxSamplerAnisotropy { get; }

    /// <inheritdoc/>
    public int MaxSampleCount { get; }

    /// <inheritdoc/>
    public bool SupportsDrawIndirectCount { get; }

    /// <inheritdoc/>
    public bool SupportsShaderSubgroup { get; }

    /// <inheritdoc/>
    /// <remarks>Base instance has been core since OpenGL 4.2, and the renderer already relies on it to
    /// pass the scene node id through an indirect draw.</remarks>
    public bool SupportsIndirectFirstInstance => true;

    /// <summary>Queries the current OpenGL context.</summary>
    public GLDeviceLimits()
    {
        MaxUniformBufferRange = GLApi.GetInteger(GetPName.MaxUniformBlockSize);
        MaxSampleCount = GLApi.GetInteger(GetPName.MaxSamples);
        MaxSamplerAnisotropy = GLApi.GetFloat((GetPName)ExtTextureFilterAnisotropic.MaxTextureMaxAnisotropyExt);

        var extensions = GetExtensions();

        // ARB_indirect_parameters is core in 4.6, which is the version the renderer targets, but the
        // renderer already guards its MultiDrawElementsIndirectCount path and drivers do lie.
        SupportsDrawIndirectCount = extensions.Contains("GL_ARB_indirect_parameters")
            || GLApi.GetInteger(GetPName.MajorVersion) > 4
            || (GLApi.GetInteger(GetPName.MajorVersion) == 4 && GLApi.GetInteger(GetPName.MinorVersion) >= 6);

        SupportsShaderSubgroup = extensions.Contains("GL_KHR_shader_subgroup");
    }

    private static HashSet<string> GetExtensions()
    {
        var count = GLApi.GetInteger(GetPName.NumExtensions);
        var extensions = new HashSet<string>(count, StringComparer.Ordinal);

        for (var i = 0; i < count; i++)
        {
            extensions.Add(GLApi.GetString(StringNameIndexed.Extensions, i));
        }

        return extensions;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Decided from the format's own properties rather than a driver query. Every format the RHI carries
    /// is one OpenGL 4.6 requires support for, so the only real questions are structural: a compressed
    /// format cannot be rendered into or written as a storage image, a depth format cannot be a colour
    /// attachment, and a colour format cannot be a depth attachment. Those are the mistakes a call site
    /// actually makes.
    /// </remarks>
    public bool SupportsFormat(RhiFormat format, TextureUsage usage)
    {
        if (format == RhiFormat.Undefined || usage == TextureUsage.None)
        {
            return false;
        }

        var isDepth = RhiFormatInfo.IsDepth(format);
        var isCompressed = RhiFormatInfo.IsCompressed(format);

        if (usage.HasFlag(TextureUsage.ColorTarget) && (isDepth || isCompressed))
        {
            return false;
        }

        if (usage.HasFlag(TextureUsage.DepthStencilTarget) && !isDepth)
        {
            return false;
        }

        if (usage.HasFlag(TextureUsage.Storage) && (isCompressed || isDepth))
        {
            return false;
        }

        // The tables are total and throw rather than substitute, so an unmappable format answers false
        // here instead of failing at allocation.
        try
        {
            FormatTables.ToGLSizedInternalFormat(format);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// <see cref="IDevice"/> on OpenGL. Creates resources against the OpenGL context that is current when it
/// is constructed, and must only be used while that context is current.
/// </summary>
/// <remarks>
/// <para>
/// OpenGL has no device object, so this one owns very little: it is the factory the contract requires and
/// the place the context's limits and diagnostics are read from. Obtain one through
/// <see cref="RendererContext.Device"/>, which the presentation layer assigns during initialization;
/// construct it only from that layer.
/// </para>
/// <para>
/// Pipeline creation and command list recording are declared <see langword="virtual"/> and throw here.
/// They need a linked <see cref="Shader"/> and a recorder respectively, both of which belong to other
/// parts of the backend; a derived device supplies them without this file having to change.
/// </para>
/// </remarks>
public class GLDevice : IDevice
{
    private readonly RhiMessageCallback? messageCallback;
    private readonly DebugProc? debugDelegate;
    private readonly List<IRhiResource>[] pendingDestroy;
    private bool disposed;

    /// <inheritdoc/>
    public RhiBackend Backend => RhiBackend.OpenGL;

    /// <inheritdoc/>
    public IDeviceLimits Limits { get; }

    /// <inheritdoc/>
    /// <remarks>One. OpenGL pipelines frames inside the driver and gives no way to observe the boundary,
    /// so there is nothing for a caller to multi-buffer against. The Vulkan backend reports more, which
    /// is why per-frame resources must still be indexed by <see cref="FrameIndex"/> rather than assumed
    /// single.</remarks>
    public int FramesInFlight => 1;

    /// <inheritdoc/>
    public int FrameIndex { get; private set; }

    /// <summary>
    /// Gets the OpenGL sampler object bound when a call site passes no sampler.
    /// </summary>
    /// <remarks>
    /// Zero, which is not a linear-repeat sampler but OpenGL's instruction to use the parameters set on
    /// the texture object itself. That is what the renderer relies on today: <see cref="RenderTexture"/>
    /// carries its own filtering and wrap state, and <see cref="Materials.MaterialLoader"/> already
    /// returns 0 from its sampler cache for the default case. Binding a real sampler here instead would
    /// override every one of those textures and change the image. The Vulkan backend, which has no such
    /// fallback, must create an actual default sampler and textures must stop carrying state.
    /// </remarks>
    public static int DefaultSamplerHandle => 0;

    /// <summary>Creates a device over the current OpenGL context.</summary>
    /// <param name="messageCallback">Where to route driver diagnostics, or <see langword="null"/> to
    /// leave whatever debug callback the context already has installed alone.</param>
    /// <exception cref="InvalidOperationException">No OpenGL context is current.</exception>
    public GLDevice(RhiMessageCallback? messageCallback = null)
    {
        Limits = new GLDeviceLimits();

        this.messageCallback = messageCallback;
        pendingDestroy = new List<IRhiResource>[FramesInFlight];

        for (var i = 0; i < pendingDestroy.Length; i++)
        {
            pendingDestroy[i] = [];
        }

        if (messageCallback is not null)
        {
            // Held in a field: the delegate is marshalled to a native function pointer and would
            // otherwise be collected while the driver still holds it.
            debugDelegate = OnDebugMessage;

            GLApi.Enable(EnableCap.DebugOutput);
            GLApi.DebugMessageCallback(debugDelegate, IntPtr.Zero);
        }
    }

    /// <inheritdoc/>
    public IBuffer CreateBuffer(in BufferDesc desc) => new GLBuffer(in desc);

    /// <inheritdoc/>
    public ITexture CreateTexture(in TextureDesc desc) => new GLTexture(in desc);

    /// <inheritdoc/>
    /// <remarks>Anisotropy is clamped to <see cref="IDeviceLimits.MaxSamplerAnisotropy"/> here, so a call
    /// site can ask for more than the hardware has without producing a GL error.</remarks>
    public ISampler CreateSampler(in SamplerDesc desc)
    {
        var clamped = desc with { MaxAnisotropy = Math.Clamp(desc.MaxAnisotropy, 1f, Limits.MaxSamplerAnisotropy) };

        return new GLSampler(in clamped, DescribeSampler(in clamped));
    }

    private static string DescribeSampler(in SamplerDesc desc)
        => $"{desc.MinFilter}/{desc.MagFilter}/{desc.MipFilter} {desc.AddressU},{desc.AddressV},{desc.AddressW} aniso {desc.MaxAnisotropy}";

    /// <inheritdoc/>
    public IShaderModule CreateShaderModule(ReadOnlySpan<byte> code, ShaderStage stage, string name)
        => new GLShaderModule(code, stage, name);

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, on this type. A graphics pipeline needs a linked
    /// <see cref="Shader"/>, which <see cref="ShaderLoader"/> owns; a derived device that can reach one
    /// overrides this and constructs a <see cref="GLGraphicsPipeline"/>.</exception>
    public virtual IGraphicsPipeline CreateGraphicsPipeline(GraphicsPipelineDesc desc)
        => throw new NotSupportedException($"{nameof(GLDevice)} cannot link programs. Override {nameof(CreateGraphicsPipeline)} on a device that can reach {nameof(ShaderLoader)}, and build a {nameof(GLGraphicsPipeline)} from the linked program.");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, on this type. See
    /// <see cref="CreateGraphicsPipeline"/>.</exception>
    public virtual IComputePipeline CreateComputePipeline(in ComputePipelineDesc desc)
        => throw new NotSupportedException($"{nameof(GLDevice)} cannot link programs. Override {nameof(CreateComputePipeline)} on a device that can reach {nameof(ShaderLoader)}, and build a {nameof(GLComputePipeline)} from the linked program.");

    /// <inheritdoc/>
    public void UploadBuffer(IBuffer destination, int offsetInBytes, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (destination is not GLBuffer buffer)
        {
            throw new ArgumentException($"Expected a {nameof(GLBuffer)}, got {destination.GetType().Name}.", nameof(destination));
        }

        buffer.Upload(offsetInBytes, data);
    }

    /// <inheritdoc/>
    public void UploadTexture(ITexture destination, int mipLevel, int arrayLayer, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (destination is not GLTexture texture)
        {
            throw new ArgumentException($"Expected a {nameof(GLTexture)}, got {destination.GetType().Name}.", nameof(destination));
        }

        texture.Upload(mipLevel, arrayLayer, data);
    }

    /// <inheritdoc/>
    public virtual void BeginFrame()
    {
        // Nothing submitted for this slot can still be running: OpenGL retires work in order and the
        // slot has been round the ring since. Releasing here rather than at DeferredDestroy is what
        // makes the deferral real, so a call site written against this stays correct on Vulkan.
        ReleasePending(FrameIndex);
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, on this type. Recording belongs to the command
    /// list layer; a derived device returns its recorder.</exception>
    public virtual ICommandList BeginCommandList(string name)
        => throw new NotSupportedException($"{nameof(GLDevice)} records no commands. Override {nameof(BeginCommandList)} on a device that supplies an {nameof(ICommandList)} implementation.");

    /// <inheritdoc/>
    /// <remarks>Nothing to do on OpenGL, where recording a command and submitting it are the same act.
    /// A derived device that buffers its recording overrides this.</remarks>
    public virtual void Submit(ICommandList commandList)
    {
        ArgumentNullException.ThrowIfNull(commandList);
    }

    /// <inheritdoc/>
    public virtual void EndFrame()
    {
        FrameIndex = (FrameIndex + 1) % FramesInFlight;
    }

    /// <inheritdoc/>
    public void WaitIdle()
    {
        GLApi.Finish();

        for (var i = 0; i < pendingDestroy.Length; i++)
        {
            ReleasePending(i);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Queued rather than destroyed immediately even though OpenGL would tolerate the latter.
    /// Honouring the deferral here is what keeps every caller written against this interface correct when
    /// it runs on Vulkan, where destroying a resource an in-flight frame still references is undefined.</remarks>
    public void DeferredDestroy(IRhiResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        pendingDestroy[FrameIndex].Add(resource);
    }

    private void ReleasePending(int frameIndex)
    {
        var pending = pendingDestroy[frameIndex];

        foreach (var resource in pending)
        {
            resource.Dispose();
        }

        pending.Clear();
    }

    /// <inheritdoc/>
    public IDisposable DebugScope(string name) => new GLDebugScope(name);

    private void OnDebugMessage(DebugSource source, DebugType type, int id, DebugSeverity severity, int length, IntPtr message, IntPtr userParam)
    {
        if (messageCallback is null)
        {
            return;
        }

        var text = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(message, length) ?? string.Empty;

        var mapped = type == DebugType.DebugTypeError
            ? RhiMessageSeverity.Error
            : severity switch
            {
                DebugSeverity.DebugSeverityHigh => RhiMessageSeverity.Error,
                DebugSeverity.DebugSeverityMedium or DebugSeverity.DebugSeverityLow => RhiMessageSeverity.Warning,
                _ => RhiMessageSeverity.Info,
            };

        messageCallback(mapped, $"[{source} {type}] {text}");
    }

    /// <summary>Releases everything still queued for destruction.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases everything still queued for destruction.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposed || !disposing)
        {
            return;
        }

        disposed = true;

        for (var i = 0; i < pendingDestroy.Length; i++)
        {
            ReleasePending(i);
        }
    }

    /// <summary>
    /// The guard <see cref="DebugScope"/> returns: a <c>glPushDebugGroup</c> closed on disposal.
    /// </summary>
    /// <remarks>A class rather than the existing <see cref="GLDebugGroup"/> ref struct, because the
    /// interface returns <see cref="IDisposable"/>. It issues no timing query, so it does not disturb
    /// <see cref="PerfStats"/>.</remarks>
    private sealed class GLDebugScope : IDisposable
    {
        private bool closed;

        public GLDebugScope(string name)
        {
            GLApi.PushDebugGroup(DebugSourceExternal.DebugSourceApplication, 0, name.Length, name);
        }

        public void Dispose()
        {
            if (closed)
            {
                return;
            }

            closed = true;
            GLApi.PopDebugGroup();
        }
    }
}
