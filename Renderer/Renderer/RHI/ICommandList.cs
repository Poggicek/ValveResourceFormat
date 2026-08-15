namespace ValveResourceFormat.Renderer.RHI;

/// <summary>A barrier moving one buffer between usages.</summary>
/// <param name="Buffer">The buffer to transition.</param>
/// <param name="Before">What it was last used for.</param>
/// <param name="After">What it will be used for next.</param>
public readonly record struct BufferBarrier(IBuffer Buffer, ResourceState Before, ResourceState After);

/// <summary>A barrier moving one texture between usages, which on Vulkan also changes its image layout.</summary>
/// <param name="Texture">The texture to transition.</param>
/// <param name="Before">What it was last used for.</param>
/// <param name="After">What it will be used for next.</param>
/// <param name="BaseMipLevel">First mip level to transition.</param>
/// <param name="MipLevelCount">Number of mip levels to transition, or -1 for all remaining.</param>
public readonly record struct TextureBarrier(
    ITexture Texture,
    ResourceState Before,
    ResourceState After,
    int BaseMipLevel = 0,
    int MipLevelCount = -1);

/// <summary>
/// Records GPU work. Obtained from <see cref="IDevice.BeginCommandList"/> and handed back to
/// <see cref="IDevice.Submit"/>.
/// </summary>
/// <remarks>
/// <para>
/// Draw and dispatch commands are valid only between <see cref="BeginRenderPass"/> and
/// <see cref="EndRenderPass"/>, except <see cref="Dispatch"/>, which is valid only outside one.
/// </para>
/// <para>
/// The barrier methods are explicit even though the OpenGL backend largely ignores them. Model them
/// for Vulkan: OpenGL is the backend that tolerates a missing barrier, Vulkan is the one that
/// corrupts, and a barrier omitted at a call site becomes a race that reproduces on one vendor's
/// driver only.
/// </para>
/// </remarks>
public interface ICommandList : IDisposable
{
    /// <summary>Gets the device that produced this command list.</summary>
    IDevice Device { get; }

    // ---- render passes ----

    /// <summary>Begins a render pass. Sets the viewport and scissor to the full attachment extent.</summary>
    /// <param name="desc">Attachments and their load and store operations.</param>
    void BeginRenderPass(in RenderPassDesc desc);

    /// <summary>Ends the current render pass.</summary>
    void EndRenderPass();

    /// <summary>Sets the viewport.</summary>
    /// <param name="x">Left edge in pixels.</param>
    /// <param name="y">Bottom edge in pixels, counted upwards from the bottom of the target, as
    /// <c>glViewport</c> takes it. The Vulkan backend converts to its own top-left origin with a
    /// negative viewport height.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="minDepth">Minimum depth. Carries the depth range: there is deliberately no
    /// separate depth-range call, because Vulkan has none.</param>
    /// <param name="maxDepth">Maximum depth.</param>
    /// <remarks>The Vulkan backend flips Y with a negative viewport height rather than in shaders,
    /// which keeps screen-space derivatives and the post-process chain correct.</remarks>
    void SetViewport(int x, int y, int width, int height, float minDepth = 0f, float maxDepth = 1f);

    /// <summary>Sets the scissor rectangle.</summary>
    /// <param name="x">Left edge in pixels.</param>
    /// <param name="y">Bottom edge in pixels, counted upwards from the bottom of the target, as
    /// <c>glScissor</c> takes it.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    void SetScissor(int x, int y, int width, int height);

    // ---- binding ----

    /// <summary>Binds a graphics pipeline.</summary>
    /// <param name="pipeline">The pipeline to bind.</param>
    void BindPipeline(IGraphicsPipeline pipeline);

    /// <summary>Binds a compute pipeline.</summary>
    /// <param name="pipeline">The pipeline to bind.</param>
    void BindPipeline(IComputePipeline pipeline);

    /// <summary>Binds a vertex buffer.</summary>
    /// <param name="binding">The binding index, matching a <see cref="VertexBindingDesc"/>.</param>
    /// <param name="buffer">The buffer to bind.</param>
    /// <param name="offsetInBytes">Byte offset of the first element.</param>
    void BindVertexBuffer(int binding, IBuffer buffer, int offsetInBytes = 0);

    /// <summary>Binds an index buffer.</summary>
    /// <param name="buffer">The buffer to bind.</param>
    /// <param name="indexType">Element width.</param>
    /// <param name="offsetInBytes">Byte offset of the first index.</param>
    void BindIndexBuffer(IBuffer buffer, IndexType indexType, int offsetInBytes = 0);

    /// <summary>Binds a uniform buffer into <see cref="DescriptorSets.UniformBuffers"/>.</summary>
    /// <param name="binding">The slot, a <see cref="Buffers.ReservedBufferSlots"/> value.</param>
    /// <param name="buffer">The buffer to bind.</param>
    /// <param name="offsetInBytes">Byte offset of the bound range.</param>
    /// <param name="sizeInBytes">Length of the bound range, or -1 for the rest of the buffer.</param>
    void BindUniformBuffer(int binding, IBuffer buffer, int offsetInBytes = 0, int sizeInBytes = -1);

    /// <summary>Binds a storage buffer into <see cref="DescriptorSets.StorageBuffers"/>.</summary>
    /// <param name="binding">The slot, a <see cref="Buffers.ReservedBufferSlots"/> value.</param>
    /// <param name="buffer">The buffer to bind.</param>
    /// <param name="offsetInBytes">Byte offset of the bound range.</param>
    /// <param name="sizeInBytes">Length of the bound range, or -1 for the rest of the buffer.</param>
    void BindStorageBuffer(int binding, IBuffer buffer, int offsetInBytes = 0, int sizeInBytes = -1);

    /// <summary>Binds a texture and its sampler for sampling.</summary>
    /// <param name="descriptorSet">Either <see cref="DescriptorSets.ReservedTextures"/> or
    /// <see cref="DescriptorSets.MaterialTextures"/>.</param>
    /// <param name="binding">The slot within that set.</param>
    /// <param name="texture">The texture to bind.</param>
    /// <param name="sampler">The sampler to bind, or <see langword="null"/> for the device default.</param>
    /// <remarks>
    /// The two backends differ on what the default means, and the difference is load bearing. On
    /// OpenGL it is sampler object 0, which defers to the texture object's own parameters &#8212; the
    /// behaviour <see cref="RenderTexture"/> and <see cref="Materials.MaterialLoader"/> rely on
    /// today, and binding a real sampler there would override every texture and change the image.
    /// Vulkan has no such fallback and must supply a genuine default, so textures cannot keep
    /// carrying sampler state once that backend is live.
    /// </remarks>
    void BindTexture(int descriptorSet, int binding, ITexture texture, ISampler? sampler = null);

    /// <summary>
    /// Writes a block into a per-frame ring buffer and binds it as a uniform buffer for this draw.
    /// </summary>
    /// <typeparam name="T">The block type. Must be blittable and match the shader declaration.</typeparam>
    /// <param name="binding">The slot within <see cref="DescriptorSets.UniformBuffers"/>.</param>
    /// <param name="data">The value to write.</param>
    /// <remarks>For per-pass and viewer-local constants that are neither a material's
    /// <see cref="GlobalsLayout"/> block nor part of the 92-byte per-draw push constant range. The
    /// allocation lives until the frame retires, so callers need not manage its lifetime.</remarks>
    void BindTransientUniform<T>(int binding, in T data) where T : unmanaged;

    /// <summary>Binds a texture as a read/write storage image.</summary>
    /// <param name="binding">The image unit within <see cref="DescriptorSets.StorageImages"/>, which
    /// is a separate index space from sampled textures and must not be confused with one.</param>
    /// <param name="texture">The texture to bind.</param>
    /// <param name="mipLevel">Which mip level to bind.</param>
    void BindStorageTexture(int binding, ITexture texture, int mipLevel = 0);

    /// <summary>Writes push constants for the bound pipeline.</summary>
    /// <typeparam name="T">The push constant block type. Must be blittable and match the shader
    /// declaration byte for byte.</typeparam>
    /// <param name="data">The value to write.</param>
    /// <param name="offsetInBytes">Byte offset within the push constant block, matching a
    /// <see cref="PushConstantRange.OffsetInBytes"/>.</param>
    /// <remarks>The renderer's per-draw block is 92 bytes. Check
    /// <see cref="IDeviceLimits.MaxPushConstantSize"/> before exceeding 128.</remarks>
    void SetPushConstants<T>(in T data, int offsetInBytes = 0) where T : unmanaged;

    // ---- draws ----

    /// <summary>Draws non-indexed primitives.</summary>
    /// <param name="vertexCount">Vertices to draw.</param>
    /// <param name="instanceCount">Instances to draw.</param>
    /// <param name="firstVertex">First vertex.</param>
    /// <param name="firstInstance">First instance. The renderer passes the scene node id here.</param>
    void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0);

    /// <summary>Draws indexed primitives.</summary>
    /// <param name="indexCount">Indices to draw.</param>
    /// <param name="instanceCount">Instances to draw.</param>
    /// <param name="firstIndex">First index.</param>
    /// <param name="baseVertex">Value added to every index before fetching.</param>
    /// <param name="firstInstance">First instance. The renderer passes the scene node id here.</param>
    void DrawIndexed(int indexCount, int instanceCount = 1, int firstIndex = 0, int baseVertex = 0, int firstInstance = 0);

    /// <summary>Draws non-indexed primitives from an argument buffer.</summary>
    /// <param name="argumentBuffer">The buffer holding the draw arguments, as four-uint
    /// <c>DrawArraysIndirectCommand</c> structures.</param>
    /// <param name="offsetInBytes">Byte offset of the first argument structure.</param>
    /// <param name="drawCount">Number of draws.</param>
    /// <param name="strideInBytes">Bytes between argument structures, or 0 for tightly packed.</param>
    /// <remarks>Distinct from <see cref="DrawIndexedIndirect"/>: the argument layout differs, and
    /// <see cref="OcclusionDebugRenderer"/> has a compute pass writing the non-indexed form.</remarks>
    void DrawIndirect(IBuffer argumentBuffer, int offsetInBytes, int drawCount, int strideInBytes = 0);

    /// <summary>Draws indexed primitives from an argument buffer.</summary>
    /// <param name="argumentBuffer">The buffer holding the draw arguments.</param>
    /// <param name="offsetInBytes">Byte offset of the first argument structure.</param>
    /// <param name="drawCount">Number of draws.</param>
    /// <param name="strideInBytes">Bytes between argument structures, or 0 for tightly packed.</param>
    void DrawIndexedIndirect(IBuffer argumentBuffer, int offsetInBytes, int drawCount, int strideInBytes = 0);

    /// <summary>Draws indexed primitives from an argument buffer, taking the draw count from a second
    /// buffer. Backs the renderer's <c>MultiDrawElementsIndirectCount</c> path.</summary>
    /// <param name="argumentBuffer">The buffer holding the draw arguments.</param>
    /// <param name="argumentOffsetInBytes">Byte offset of the first argument structure.</param>
    /// <param name="countBuffer">The buffer holding the draw count.</param>
    /// <param name="countOffsetInBytes">Byte offset of the count.</param>
    /// <param name="maxDrawCount">Upper bound on the draw count.</param>
    /// <param name="strideInBytes">Bytes between argument structures, or 0 for tightly packed.</param>
    /// <remarks>Requires <see cref="IDeviceLimits.SupportsDrawIndirectCount"/>.</remarks>
    void DrawIndexedIndirectCount(
        IBuffer argumentBuffer,
        int argumentOffsetInBytes,
        IBuffer countBuffer,
        int countOffsetInBytes,
        int maxDrawCount,
        int strideInBytes = 0);

    // ---- compute ----

    /// <summary>Dispatches a compute workload. Not valid inside a render pass.</summary>
    /// <param name="groupCountX">Workgroups on X.</param>
    /// <param name="groupCountY">Workgroups on Y.</param>
    /// <param name="groupCountZ">Workgroups on Z.</param>
    void Dispatch(int groupCountX, int groupCountY = 1, int groupCountZ = 1);

    /// <summary>Dispatches a compute workload whose group counts come from a buffer.</summary>
    /// <param name="argumentBuffer">The buffer holding the group counts.</param>
    /// <param name="offsetInBytes">Byte offset of the counts.</param>
    void DispatchIndirect(IBuffer argumentBuffer, int offsetInBytes);

    // ---- synchronisation ----

    /// <summary>Inserts barriers. Batch every transition that happens at the same point into one call;
    /// separate calls cost separate pipeline stalls.</summary>
    /// <param name="bufferBarriers">Buffer transitions.</param>
    /// <param name="textureBarriers">Texture transitions.</param>
    void Barrier(ReadOnlySpan<BufferBarrier> bufferBarriers, ReadOnlySpan<TextureBarrier> textureBarriers);

    /// <summary>Inserts a single texture barrier.</summary>
    /// <param name="barrier">The transition.</param>
    void Barrier(in TextureBarrier barrier);

    /// <summary>Inserts a single buffer barrier.</summary>
    /// <param name="barrier">The transition.</param>
    void Barrier(in BufferBarrier barrier);

    // ---- transfers ----

    /// <summary>Copies between buffers.</summary>
    /// <param name="source">Source buffer.</param>
    /// <param name="sourceOffsetInBytes">Byte offset to read from.</param>
    /// <param name="destination">Destination buffer.</param>
    /// <param name="destinationOffsetInBytes">Byte offset to write to.</param>
    /// <param name="sizeInBytes">Bytes to copy.</param>
    void CopyBuffer(IBuffer source, int sourceOffsetInBytes, IBuffer destination, int destinationOffsetInBytes, int sizeInBytes);

    /// <summary>Copies one mip level between textures of the same format and extent.</summary>
    /// <param name="source">Source texture.</param>
    /// <param name="destination">Destination texture.</param>
    /// <param name="mipLevel">Mip level to copy.</param>
    void CopyTexture(ITexture source, ITexture destination, int mipLevel = 0);

    /// <summary>Copies texel data out of a texture into a buffer, for readback to the CPU.</summary>
    /// <param name="source">Source texture, which must be in <see cref="ResourceState.CopySource"/>.</param>
    /// <param name="mipLevel">Mip level to read.</param>
    /// <param name="arrayLayer">Array layer or cube face to read.</param>
    /// <param name="destination">Destination buffer, normally <see cref="BufferMemory.HostReadback"/>.</param>
    /// <param name="destinationOffsetInBytes">Byte offset to write at.</param>
    /// <remarks>The only route from a render target back to the CPU, and therefore what backs
    /// screenshots, texture export and the package browser's thumbnails. Read the result through
    /// <see cref="IBuffer.MappedData"/> after <see cref="IDevice.WaitIdle"/>.</remarks>
    void CopyTextureToBuffer(ITexture source, int mipLevel, int arrayLayer, IBuffer destination, int destinationOffsetInBytes = 0);

    /// <summary>Blits between textures, scaling and converting format. Replaces
    /// <c>glBlitNamedFramebuffer</c>.</summary>
    /// <param name="source">Source texture.</param>
    /// <param name="destination">Destination texture.</param>
    /// <param name="filter">Filter used when the extents differ.</param>
    void BlitTexture(ITexture source, ITexture destination, FilterMode filter = FilterMode.Linear);

    /// <summary>Fills a buffer range with a repeating 32 bit value. Replaces
    /// <c>glClearNamedBufferSubData</c>, which the cull passes use to reset counters.</summary>
    /// <param name="buffer">The buffer to fill.</param>
    /// <param name="offsetInBytes">Byte offset to start at.</param>
    /// <param name="sizeInBytes">Bytes to fill.</param>
    /// <param name="value">The 32 bit value to repeat.</param>
    void FillBuffer(IBuffer buffer, int offsetInBytes, int sizeInBytes, uint value);

    /// <summary>Fills one mip level of a texture with a raw 32 bit value, outside a render pass.
    /// Replaces <c>glClearTexImage</c>.</summary>
    /// <param name="texture">The texture to fill.</param>
    /// <param name="mipLevel">Mip level to fill.</param>
    /// <param name="value">The raw 32 bit value, reinterpreted according to the texture's format.</param>
    /// <remarks>Distinct from a <see cref="LoadOp.Clear"/> attachment: this targets storage images,
    /// which are not attachments, and takes a raw integer rather than a
    /// <see cref="ColorAttachmentDesc.ClearColor"/>. The overdraw counters are cleared to
    /// <see cref="uint.MaxValue"/>, which no float clear colour can represent.</remarks>
    void ClearTexture(ITexture texture, int mipLevel, uint value);

    // ---- debugging ----

    /// <summary>Opens a labelled scope in graphics debuggers. Dispose to close it.</summary>
    /// <param name="name">The label.</param>
    /// <returns>A guard that closes the scope.</returns>
    IDisposable DebugScope(string name);

    /// <summary>Inserts a one-off marker in graphics debuggers.</summary>
    /// <param name="name">The marker text.</param>
    void DebugMarker(string name);
}
