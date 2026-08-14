namespace ValveResourceFormat.Renderer.RHI;

/// <summary>Which backend a device is running.</summary>
public enum RhiBackend
{
    /// <summary>OpenGL 4.6. The parity oracle: both backends ship, and the golden image suite diffs
    /// one against the other.</summary>
    OpenGL,
    /// <summary>Vulkan 1.3.</summary>
    Vulkan,
}

/// <summary>What the device can do. Query rather than assume: the two backends differ, and so do
/// drivers within a backend.</summary>
public interface IDeviceLimits
{
    /// <summary>Gets the largest push constant block, in bytes. The Vulkan floor is 128; the
    /// renderer's per-draw block is 92.</summary>
    int MaxPushConstantSize { get; }

    /// <summary>Gets the largest uniform buffer binding, in bytes. <see cref="GlobalsLayout"/>
    /// assumes the 16384 the GL specification guarantees.</summary>
    int MaxUniformBufferRange { get; }

    /// <summary>Gets the maximum anisotropy the sampler hardware supports.</summary>
    float MaxSamplerAnisotropy { get; }

    /// <summary>Gets the highest sample count supported for multisampled render targets. Replaces
    /// <c>GL.GetInteger(GetPName.MaxSamples)</c>, which the viewers clamp the user's anti-aliasing
    /// setting against.</summary>
    int MaxSampleCount { get; }

    /// <summary>Gets a value indicating whether indirect draws can read their count from a buffer,
    /// backing the renderer's <c>MultiDrawElementsIndirectCount</c> path.</summary>
    bool SupportsDrawIndirectCount { get; }

    /// <summary>Gets a value indicating whether the shading language subgroup operations are
    /// available. The compute passes use 19 of them.</summary>
    bool SupportsShaderSubgroup { get; }

    /// <summary>Gets a value indicating whether a non-zero first instance is honoured by indirect
    /// draws. The renderer passes the scene node id through it.</summary>
    bool SupportsIndirectFirstInstance { get; }

    /// <summary>Determines whether a format can be used for the given purpose on this device.</summary>
    /// <param name="format">The format to test.</param>
    /// <param name="usage">The intended usage.</param>
    /// <returns><see langword="true"/> when the combination is supported.</returns>
    bool SupportsFormat(RhiFormat format, TextureUsage usage);
}

/// <summary>Severity of a diagnostic message the graphics backend reports.</summary>
public enum RhiMessageSeverity
{
    /// <summary>Informational.</summary>
    Info,
    /// <summary>A warning: legal but suspect.</summary>
    Warning,
    /// <summary>An error. The GUI breaks into the debugger on these.</summary>
    Error,
}

/// <summary>Receives diagnostics from the graphics backend.</summary>
/// <param name="severity">How serious the message is.</param>
/// <param name="message">The message text.</param>
/// <remarks>Backs the existing <c>GL.DebugMessageCallback</c> wiring. Vulkan installs its
/// <c>VK_EXT_debug_utils</c> messenger at instance creation, so this must be supplied when the
/// device is created rather than attached afterwards.</remarks>
public delegate void RhiMessageCallback(RhiMessageSeverity severity, string message);

/// <summary>
/// Creates GPU resources and submits work. One device per rendering context; obtain it from
/// <see cref="RendererContext.Device"/>, which the presentation layer assigns during initialization.
/// </summary>
public interface IDevice : IDisposable
{
    /// <summary>Gets which backend this device is running.</summary>
    RhiBackend Backend { get; }

    /// <summary>Gets what this device can do.</summary>
    IDeviceLimits Limits { get; }

    /// <summary>Gets the number of frames that may be in flight at once. Resources written per frame
    /// must be multi-buffered by this factor.</summary>
    int FramesInFlight { get; }

    /// <summary>Gets the index of the frame currently being recorded, cycling within
    /// <see cref="FramesInFlight"/>. Selects which copy of a per-frame resource to write.</summary>
    int FrameIndex { get; }

    /// <summary>Creates a buffer.</summary>
    /// <param name="desc">Creation parameters.</param>
    /// <returns>The buffer.</returns>
    IBuffer CreateBuffer(in BufferDesc desc);

    /// <summary>Creates a texture. Its initial state is <see cref="ResourceState.Undefined"/>.</summary>
    /// <param name="desc">Creation parameters.</param>
    /// <returns>The texture.</returns>
    ITexture CreateTexture(in TextureDesc desc);

    /// <summary>Creates a sampler.</summary>
    /// <param name="desc">Creation parameters.</param>
    /// <returns>The sampler.</returns>
    ISampler CreateSampler(in SamplerDesc desc);

    /// <summary>Creates a shader module from compiled code: SPIR-V on Vulkan, GLSL source on OpenGL.</summary>
    /// <param name="code">The compiled shader code.</param>
    /// <param name="stage">The stage the code was compiled for.</param>
    /// <param name="name">Debug name.</param>
    /// <returns>The module.</returns>
    IShaderModule CreateShaderModule(ReadOnlySpan<byte> code, ShaderStage stage, string name);

    /// <summary>Creates a graphics pipeline, or returns a cached one with the same
    /// <see cref="PipelineCacheKey"/>.</summary>
    /// <param name="desc">Creation parameters.</param>
    /// <returns>The pipeline.</returns>
    IGraphicsPipeline CreateGraphicsPipeline(GraphicsPipelineDesc desc);

    /// <summary>Creates a compute pipeline, or returns a cached one.</summary>
    /// <param name="desc">Creation parameters.</param>
    /// <returns>The pipeline.</returns>
    IComputePipeline CreateComputePipeline(in ComputePipelineDesc desc);

    /// <summary>Uploads data into a buffer, staging it when the destination is not host visible.</summary>
    /// <param name="destination">The buffer to write.</param>
    /// <param name="offsetInBytes">Byte offset to write at.</param>
    /// <param name="data">The bytes to write.</param>
    void UploadBuffer(IBuffer destination, int offsetInBytes, ReadOnlySpan<byte> data);

    /// <summary>Uploads one mip level of one array layer, staging as needed. Compressed formats
    /// expect block-encoded data.</summary>
    /// <param name="destination">The texture to write.</param>
    /// <param name="mipLevel">Mip level to write.</param>
    /// <param name="arrayLayer">Array layer or cube face to write.</param>
    /// <param name="data">The texel or block data.</param>
    void UploadTexture(ITexture destination, int mipLevel, int arrayLayer, ReadOnlySpan<byte> data);

    /// <summary>Begins recording a frame, waiting until the frame slot's previous work has retired.</summary>
    void BeginFrame();

    /// <summary>Acquires a command list for recording.</summary>
    /// <param name="name">Debug label for the recorded work.</param>
    /// <returns>The command list.</returns>
    ICommandList BeginCommandList(string name);

    /// <summary>Submits a finished command list.</summary>
    /// <param name="commandList">The command list to submit.</param>
    void Submit(ICommandList commandList);

    /// <summary>Ends the frame and presents, if a swapchain is attached.</summary>
    void EndFrame();

    /// <summary>Blocks until every submitted operation has finished. For teardown and readback only;
    /// calling this per frame destroys pipelining.</summary>
    void WaitIdle();

    /// <summary>Queues a resource for destruction once every frame that could reference it has
    /// retired. Destroying a resource directly while it is still in flight is undefined.</summary>
    /// <param name="resource">The resource to destroy.</param>
    void DeferredDestroy(IRhiResource resource);

    /// <summary>Opens a labelled scope in graphics debuggers spanning whatever is recorded inside it,
    /// including work recorded by callees on their own command lists. Dispose to close it.</summary>
    /// <param name="name">The label.</param>
    /// <returns>A guard that closes the scope.</returns>
    /// <remarks>The device-level counterpart of <see cref="ICommandList.DebugScope"/>, for callers
    /// that want to name a region without owning a command list at that altitude. Backs the existing
    /// free-standing <c>GLDebugGroup</c> usage.</remarks>
    IDisposable DebugScope(string name);
}
