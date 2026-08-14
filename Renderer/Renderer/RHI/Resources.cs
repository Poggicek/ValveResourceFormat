namespace ValveResourceFormat.Renderer.RHI;

/// <summary>Base of every GPU object the device hands out. Disposal is deferred: a backend must keep
/// the object alive until every frame that referenced it has retired.</summary>
public interface IRhiResource : IDisposable
{
    /// <summary>Gets the debug name, surfaced to graphics debuggers. Mirrors the existing
    /// <c>GL.ObjectLabel</c> convention.</summary>
    string Name { get; }
}

/// <summary>A GPU buffer.</summary>
public interface IBuffer : IRhiResource
{
    /// <summary>Gets the size in bytes.</summary>
    int SizeInBytes { get; }

    /// <summary>Gets the usage flags the buffer was created with.</summary>
    BufferUsage Usage { get; }

    /// <summary>Gets where the buffer's memory lives.</summary>
    BufferMemory Memory { get; }

    /// <summary>Gets the persistently mapped span, for <see cref="BufferMemory.HostUpload"/> and
    /// <see cref="BufferMemory.HostReadback"/> buffers.</summary>
    /// <exception cref="InvalidOperationException">The buffer is <see cref="BufferMemory.DeviceLocal"/>.</exception>
    Span<byte> MappedData { get; }

    /// <summary>Flags a mapped range as written, so a backend on non-coherent memory can flush it.
    /// Cheap and required; skipping it is a silent correctness bug on some hardware.</summary>
    /// <param name="offsetInBytes">Start of the written range.</param>
    /// <param name="sizeInBytes">Length of the written range.</param>
    void FlushRange(int offsetInBytes, int sizeInBytes);
}

/// <summary>A GPU texture, with all of its mip levels and array layers.</summary>
public interface ITexture : IRhiResource
{
    /// <summary>Gets the width in texels of mip level zero.</summary>
    int Width { get; }

    /// <summary>Gets the height in texels of mip level zero.</summary>
    int Height { get; }

    /// <summary>Gets the depth of a volume texture, or the array layer count, or 1.</summary>
    int Depth { get; }

    /// <summary>Gets the number of mip levels.</summary>
    int MipLevels { get; }

    /// <summary>Gets the sample count. 1 means not multisampled.</summary>
    int SampleCount { get; }

    /// <summary>Gets the pixel format.</summary>
    RhiFormat Format { get; }

    /// <summary>Gets the texture shape.</summary>
    TextureDimension Dimension { get; }

    /// <summary>Gets the usage flags the texture was created with.</summary>
    TextureUsage Usage { get; }

    /// <summary>Creates a view onto a subset of this texture's mips and layers, aliasing its memory.
    /// Replaces <c>glTextureView</c>. The view does not own the memory and must be disposed before
    /// its parent.</summary>
    /// <param name="baseMipLevel">First mip level in the view.</param>
    /// <param name="mipLevelCount">Number of mip levels in the view.</param>
    /// <param name="baseArrayLayer">First array layer in the view.</param>
    /// <param name="arrayLayerCount">Number of array layers in the view.</param>
    /// <param name="format">Format to reinterpret as, or <see cref="RhiFormat.Undefined"/> to keep the parent's.</param>
    /// <returns>The view.</returns>
    ITexture CreateView(int baseMipLevel, int mipLevelCount, int baseArrayLayer, int arrayLayerCount, RhiFormat format = RhiFormat.Undefined);
}

/// <summary>A sampler. Separate from the texture, as it already is in
/// <see cref="Materials.MaterialLoader"/>'s <c>glCreateSamplers</c> path.</summary>
public interface ISampler : IRhiResource
{
}

/// <summary>A compiled shader stage.</summary>
public interface IShaderModule : IRhiResource
{
    /// <summary>Gets the stage this module was compiled for.</summary>
    ShaderStage Stage { get; }

    /// <summary>Gets a content hash of the compiled code, for use as part of a pipeline cache key.</summary>
    ulong ContentHash { get; }
}

/// <summary>A compiled graphics pipeline.</summary>
public interface IGraphicsPipeline : IRhiResource
{
    /// <summary>Gets the descriptor this pipeline was created from.</summary>
    GraphicsPipelineDesc Description { get; }
}

/// <summary>A compiled compute pipeline.</summary>
public interface IComputePipeline : IRhiResource
{
    /// <summary>Gets the local workgroup size declared by the shader.</summary>
    (int X, int Y, int Z) WorkgroupSize { get; }
}

/// <summary>A render target a command list can draw into. Either an offscreen texture set or a
/// swapchain image.</summary>
public interface IRenderTarget : IRhiResource
{
    /// <summary>Gets the width in pixels.</summary>
    int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    int Height { get; }

    /// <summary>Gets the sample count. 1 means not multisampled.</summary>
    int SampleCount { get; }

    /// <summary>Gets the colour attachment formats, in attachment order.</summary>
    IReadOnlyList<RhiFormat> ColorFormats { get; }

    /// <summary>Gets the depth-stencil format, or <see cref="RhiFormat.Undefined"/> when there is none.</summary>
    RhiFormat DepthFormat { get; }
}
