namespace ValveResourceFormat.Renderer.RHI;

/// <summary>How a buffer will be used. Backends that need usage up front (Vulkan) require this to be
/// complete at creation; GL ignores it.</summary>
[Flags]
public enum BufferUsage
{
    /// <summary>No usage. Invalid for creation.</summary>
    None = 0,
    /// <summary>Bound as a vertex buffer.</summary>
    Vertex = 1 << 0,
    /// <summary>Bound as an index buffer.</summary>
    Index = 1 << 1,
    /// <summary>Bound as a uniform (constant) buffer.</summary>
    Uniform = 1 << 2,
    /// <summary>Bound as a shader storage buffer.</summary>
    Storage = 1 << 3,
    /// <summary>Read by an indirect draw or dispatch as its argument buffer.</summary>
    Indirect = 1 << 4,
    /// <summary>Valid as the source of a copy.</summary>
    CopySource = 1 << 5,
    /// <summary>Valid as the destination of a copy or an upload.</summary>
    CopyDestination = 1 << 6,
}

/// <summary>Where a buffer's memory lives, which decides how it is written.</summary>
public enum BufferMemory
{
    /// <summary>Device local, not CPU visible. Written through a staging copy. The default for static data.</summary>
    DeviceLocal,
    /// <summary>Host visible and persistently mapped. For per-frame data written every frame.</summary>
    HostUpload,
    /// <summary>Host visible and cached for reading back. For queries and screenshots.</summary>
    HostReadback,
}

/// <summary>How a texture will be used.</summary>
[Flags]
public enum TextureUsage
{
    /// <summary>No usage. Invalid for creation.</summary>
    None = 0,
    /// <summary>Sampled by a shader.</summary>
    Sampled = 1 << 0,
    /// <summary>Bound as a read/write storage image.</summary>
    Storage = 1 << 1,
    /// <summary>Bound as a colour attachment.</summary>
    ColorTarget = 1 << 2,
    /// <summary>Bound as a depth or depth-stencil attachment.</summary>
    DepthStencilTarget = 1 << 3,
    /// <summary>Valid as the source of a copy or blit.</summary>
    CopySource = 1 << 4,
    /// <summary>Valid as the destination of a copy or an upload.</summary>
    CopyDestination = 1 << 5,
}

/// <summary>Texture shape. Replaces the OpenTK <c>TextureTarget</c> in public signatures.</summary>
public enum TextureDimension
{
    /// <summary>One dimensional.</summary>
    Texture1D,
    /// <summary>Two dimensional.</summary>
    Texture2D,
    /// <summary>An array of two dimensional slices.</summary>
    Texture2DArray,
    /// <summary>Three dimensional (volume).</summary>
    Texture3D,
    /// <summary>Cube map, six faces.</summary>
    TextureCube,
    /// <summary>An array of cube maps.</summary>
    TextureCubeArray,
}

/// <summary>Texture minification and magnification filter.</summary>
public enum FilterMode
{
    /// <summary>Nearest texel.</summary>
    Nearest,
    /// <summary>Linear interpolation.</summary>
    Linear,
}

/// <summary>Filtering applied between mip levels.</summary>
public enum MipFilterMode
{
    /// <summary>No mip sampling.</summary>
    None,
    /// <summary>Nearest mip level.</summary>
    Nearest,
    /// <summary>Linear interpolation between mip levels.</summary>
    Linear,
}

/// <summary>Texture coordinate wrapping outside the zero to one range.</summary>
public enum AddressMode
{
    /// <summary>Tile.</summary>
    Repeat,
    /// <summary>Tile, mirroring alternate tiles.</summary>
    MirroredRepeat,
    /// <summary>Clamp to the edge texel.</summary>
    ClampToEdge,
    /// <summary>Clamp to a constant border colour.</summary>
    ClampToBorder,
}

/// <summary>Primitive topology. Replaces the OpenTK <c>PrimitiveType</c> in public signatures.</summary>
public enum PrimitiveTopology
{
    /// <summary>Isolated points.</summary>
    PointList,
    /// <summary>Isolated line segments.</summary>
    LineList,
    /// <summary>Connected line segments.</summary>
    LineStrip,
    /// <summary>Isolated triangles.</summary>
    TriangleList,
    /// <summary>Connected triangles sharing an edge.</summary>
    TriangleStrip,
}

/// <summary>Width of an index buffer element.</summary>
public enum IndexType
{
    /// <summary>16 bit indices.</summary>
    UInt16,
    /// <summary>32 bit indices.</summary>
    UInt32,
}

/// <summary>Shader pipeline stage.</summary>
[Flags]
public enum ShaderStage
{
    /// <summary>No stage.</summary>
    None = 0,
    /// <summary>Vertex stage.</summary>
    Vertex = 1 << 0,
    /// <summary>Fragment (pixel) stage.</summary>
    Fragment = 1 << 1,
    /// <summary>Compute stage.</summary>
    Compute = 1 << 2,
    /// <summary>Every graphics stage.</summary>
    AllGraphics = Vertex | Fragment,
}

/// <summary>What happens to an attachment's existing contents when a render pass begins.</summary>
public enum LoadOp
{
    /// <summary>Preserve the existing contents.</summary>
    Load,
    /// <summary>Replace with the clear value. Cheaper than <see cref="Load"/> on tiled hardware.</summary>
    Clear,
    /// <summary>Contents become undefined. Cheapest; valid only when the pass writes every texel.</summary>
    DontCare,
}

/// <summary>What happens to an attachment's contents when a render pass ends.</summary>
public enum StoreOp
{
    /// <summary>Write the results back.</summary>
    Store,
    /// <summary>Discard the results. For transient targets such as a resolved MSAA buffer.</summary>
    DontCare,
}

/// <summary>
/// What a resource is currently being used for. Drives barrier insertion.
/// </summary>
/// <remarks>
/// A state-based model rather than raw stage and access masks: the renderer's usage patterns are few
/// and well-known, and an enum the caller cannot get subtly wrong is worth more here than the last
/// few percent of barrier precision. The Vulkan backend expands each state to its
/// <c>VkPipelineStageFlags2</c>, <c>VkAccessFlags2</c> and <c>VkImageLayout</c>; the GL backend maps
/// them onto <c>glMemoryBarrier</c> bits or ignores them.
/// </remarks>
public enum ResourceState
{
    /// <summary>Contents undefined. The starting state of every newly created resource.</summary>
    Undefined,
    /// <summary>Read by a shader as a sampled texture or a uniform or storage buffer.</summary>
    ShaderRead,
    /// <summary>Written by a shader through a storage image or storage buffer.</summary>
    ShaderWrite,
    /// <summary>Both read and written by a shader in the same pass.</summary>
    ShaderReadWrite,
    /// <summary>Bound as a colour attachment.</summary>
    ColorTarget,
    /// <summary>Bound as a writable depth-stencil attachment.</summary>
    DepthWrite,
    /// <summary>Bound as a read-only depth-stencil attachment, and possibly sampled at the same time.</summary>
    DepthRead,
    /// <summary>Read as an indirect draw or dispatch argument buffer.</summary>
    IndirectArgument,
    /// <summary>Read as an index buffer.</summary>
    IndexBuffer,
    /// <summary>Read as a vertex buffer.</summary>
    VertexBuffer,
    /// <summary>The source of a copy or blit.</summary>
    CopySource,
    /// <summary>The destination of a copy or blit.</summary>
    CopyDestination,
    /// <summary>Ready to be handed to the presentation engine.</summary>
    Present,
}
