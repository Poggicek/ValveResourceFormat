using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using GLApi = OpenTK.Graphics.OpenGL.GL;

namespace ValveResourceFormat.Renderer.RHI.OpenGL;

/// <summary>
/// <see cref="ICommandList"/> on OpenGL. Records by issuing immediately: OpenGL has no command buffer,
/// so recording a command and submitting it are the same act.
/// </summary>
/// <remarks>
/// <para>
/// The contract is shaped for dynamic rendering and Vulkan's explicit binding model, neither of which
/// OpenGL has. Three things are emulated here, and each is cached so the emulation costs no more GL
/// calls than the hand-written code it replaces:
/// </para>
/// <para>
/// A render pass becomes a framebuffer object, built from its attachments and cached by their
/// identity, plus explicit clears and invalidations for the load and store operations. A pipeline's
/// vertex input becomes a vertex array object, cached by the vertex input hash the pipeline already
/// computes for its cache key. Push constants become loose program uniforms, which
/// <see cref="GLPushConstantBlock"/> already diffs field by field.
/// </para>
/// <para>
/// Reuse one across frames and call <see cref="Reset"/> at the start of each. The caches are what make
/// the emulation cheap, and rebuilding them every frame would cost far more than it saves;
/// <see cref="Reset"/> releases the per-frame state, which is the transient uniform ring and the
/// bindings, and keeps them.
/// </para>
/// </remarks>
public sealed class GLCommandList : ICommandList
{
    private const int MaxVertexBindings = 16;
    private const int FramebufferCacheLimit = 64;
    private const int TransientBlockSize = 256 * 1024;

    private readonly RenderStateTracker renderState;
    private readonly string name;

    private readonly List<CachedFramebuffer> framebuffers = [];
    private readonly Dictionary<ulong, int> vertexArrays = [];
    private readonly List<GLBuffer> transientBlocks = [];

    private readonly VertexBinding[] vertexBindings = new VertexBinding[MaxVertexBindings];
    private IndexBinding indexBinding;

    private int transientBlockIndex;
    private int transientOffset;
    private int transientAlignment;

    private GLGraphicsPipeline? graphicsPipeline;
    private GLComputePipeline? computePipeline;
    private int boundVertexArray;

    private bool inRenderPass;
    private int passFramebuffer;
    private int passWidth;
    private int passHeight;
    private ColorAttachmentDesc[] passColorAttachments = [];
    private DepthAttachmentDesc? passDepthAttachment;

    private bool disposed;

    /// <inheritdoc/>
    public IDevice Device { get; }

    /// <summary>Creates a command list that records into the current OpenGL context.</summary>
    /// <param name="device">The device that produced this command list.</param>
    /// <param name="renderState">The state tracker of the context being recorded into. A pipeline
    /// applies its <see cref="RenderState"/> through this, and it is what lets a clear force the write
    /// masks open without losing the pass baseline.</param>
    /// <param name="name">Debug label for the recorded work.</param>
    /// <remarks>
    /// Public so that any device can hand one out. <see cref="GLDevice.BeginCommandList"/> throws by
    /// design, and the device that overrides it is the one that can also link programs; rather than
    /// subclassing <see cref="GLDevice"/> here and forcing that device to inherit from two places, the
    /// override is one line: <c>new GLCommandList(this, renderState, name)</c>.
    /// </remarks>
    public GLCommandList(IDevice device, RenderStateTracker renderState, string name)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(renderState);

        Device = device;
        this.renderState = renderState;
        this.name = name ?? string.Empty;
    }

    /// <summary>
    /// Releases the state that belongs to one frame, so the command list can record the next one.
    /// </summary>
    /// <remarks>
    /// Rewinds the transient uniform ring and drops the bindings and the bound pipeline. The framebuffer
    /// and vertex array caches survive: they are keyed by identity, cost GL calls to rebuild, and stay
    /// valid for as long as the resources they name do.
    /// </remarks>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        transientBlockIndex = 0;
        transientOffset = 0;

        Array.Clear(vertexBindings);
        indexBinding = default;

        graphicsPipeline = null;
        computePipeline = null;
        boundVertexArray = 0;

        inRenderPass = false;
        passColorAttachments = [];
        passDepthAttachment = null;
    }

    // ---- render passes ----

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">The pass has no attachments, or its attachments are not all
    /// the same size.</exception>
    /// <exception cref="InvalidOperationException">A pass is already open, or the attachments do not
    /// form a complete framebuffer.</exception>
    public void BeginRenderPass(in RenderPassDesc desc)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (inRenderPass)
        {
            throw new InvalidOperationException($"A render pass is already open on command list '{name}'. Close it with {nameof(EndRenderPass)} first.");
        }

        var colors = desc.ColorAttachments ?? [];

        if (colors.Length == 0 && desc.DepthAttachment is null)
        {
            throw new ArgumentException("A render pass needs at least one attachment.", nameof(desc));
        }

        (passWidth, passHeight) = PassExtent(colors, desc.DepthAttachment);

        passFramebuffer = GetOrCreateFramebuffer(colors, desc.DepthAttachment);
        passColorAttachments = colors;
        passDepthAttachment = desc.DepthAttachment;
        inRenderPass = true;

        GLApi.BindFramebuffer(FramebufferTarget.Framebuffer, passFramebuffer);

        // The contract says a pass starts with viewport and scissor covering the whole attachment.
        // Scissoring off rather than scissoring to the full extent is the same thing with one less
        // piece of state to get wrong, and it keeps the clears below unscissored.
        GLApi.Disable(EnableCap.ScissorTest);
        GLApi.Viewport(0, 0, passWidth, passHeight);
        GLApi.Scissor(0, 0, passWidth, passHeight);

        ApplyLoadOps(colors, desc.DepthAttachment);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">No pass is open.</exception>
    public void EndRenderPass()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureInRenderPass();

        ResolveAttachments();
        ApplyStoreOps();

        inRenderPass = false;
        passColorAttachments = [];
        passDepthAttachment = null;
    }

    /// <inheritdoc/>
    public void SetViewport(int x, int y, int width, int height, float minDepth = 0f, float maxDepth = 1f)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        GLApi.Viewport(x, y, width, height);
        GLApi.DepthRange(minDepth, maxDepth);
    }

    /// <inheritdoc/>
    public void SetScissor(int x, int y, int width, int height)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        GLApi.Enable(EnableCap.ScissorTest);
        GLApi.Scissor(x, y, width, height);
    }

    // ---- binding ----

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="pipeline"/> is not a <see cref="GLGraphicsPipeline"/>.</exception>
    public void BindPipeline(IGraphicsPipeline pipeline)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(pipeline);

        if (pipeline is not GLGraphicsPipeline glPipeline)
        {
            throw new ArgumentException($"Expected a {nameof(GLGraphicsPipeline)}, got {pipeline.GetType().Name}.", nameof(pipeline));
        }

        graphicsPipeline = glPipeline;
        computePipeline = null;

        glPipeline.Bind(renderState);

        // The vertex array carries the attribute formats, which belong to the pipeline on Vulkan and to
        // the VAO here. Switching pipelines can therefore switch VAOs, and the buffers bound so far have
        // to be reattached to the new one.
        var vertexArray = GetOrCreateVertexArray(glPipeline);

        if (vertexArray != boundVertexArray)
        {
            boundVertexArray = vertexArray;
            GLApi.BindVertexArray(vertexArray);
            ReapplyBufferBindings();
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="pipeline"/> is not a <see cref="GLComputePipeline"/>.</exception>
    public void BindPipeline(IComputePipeline pipeline)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(pipeline);

        if (pipeline is not GLComputePipeline glPipeline)
        {
            throw new ArgumentException($"Expected a {nameof(GLComputePipeline)}, got {pipeline.GetType().Name}.", nameof(pipeline));
        }

        computePipeline = glPipeline;
        graphicsPipeline = null;

        glPipeline.Bind();
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="binding"/> is outside the supported range.</exception>
    public void BindVertexBuffer(int binding, IBuffer buffer, int offsetInBytes = 0)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(binding);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(binding, MaxVertexBindings);

        var handle = HandleOf(buffer);

        // Recorded as well as applied: a later pipeline bind can move to a different vertex array, which
        // starts with no buffers attached and has to be given these again.
        vertexBindings[binding] = new VertexBinding(handle, offsetInBytes, true);

        if (boundVertexArray != 0)
        {
            ApplyVertexBinding(binding);
        }
    }

    /// <inheritdoc/>
    public void BindIndexBuffer(IBuffer buffer, IndexType indexType, int offsetInBytes = 0)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var handle = HandleOf(buffer);

        indexBinding = new IndexBinding(handle, indexType, offsetInBytes, true);

        if (boundVertexArray != 0)
        {
            GLApi.VertexArrayElementBuffer(boundVertexArray, handle);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Uniform and storage buffers keep separate binding index spaces on OpenGL, so the
    /// contract's split of them across descriptor sets 0 and 1 needs no renumbering here. It exists for
    /// Vulkan, where the two share one space and the reserved slots would collide.</remarks>
    public void BindUniformBuffer(int binding, IBuffer buffer, int offsetInBytes = 0, int sizeInBytes = -1)
        => BindBufferRange(BufferRangeTarget.UniformBuffer, binding, buffer, offsetInBytes, sizeInBytes);

    /// <inheritdoc/>
    public void BindStorageBuffer(int binding, IBuffer buffer, int offsetInBytes = 0, int sizeInBytes = -1)
        => BindBufferRange(BufferRangeTarget.ShaderStorageBuffer, binding, buffer, offsetInBytes, sizeInBytes);

    private void BindBufferRange(BufferRangeTarget target, int binding, IBuffer buffer, int offsetInBytes, int sizeInBytes)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);

        var handle = HandleOf(buffer);
        var size = sizeInBytes < 0 ? buffer.SizeInBytes - offsetInBytes : sizeInBytes;

        GLApi.BindBufferRange(target, binding, handle, offsetInBytes, size);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="descriptorSet"/> is not a texture set.</exception>
    /// <remarks>
    /// OpenGL has one flat texture unit namespace where the contract has two sets, so the set decides
    /// the base: the reserved globals of set 2 land on their own slot number, and set 3's per-material
    /// bindings land above <see cref="Materials.RenderMaterial.TextureUnitStart"/>. That is exactly the
    /// numbering <see cref="Materials.RenderMaterial.Render"/> already uses, which is what keeps the two
    /// paths a parity oracle for each other while material binding is being ported.
    /// </remarks>
    public void BindTexture(int descriptorSet, int binding, ITexture texture, ISampler? sampler = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(texture);

        var unit = descriptorSet switch
        {
            DescriptorSets.ReservedTextures => binding,
            DescriptorSets.MaterialTextures => Materials.RenderMaterial.TextureUnitStart + binding,
            _ => throw new ArgumentOutOfRangeException(nameof(descriptorSet), descriptorSet,
                $"Only sets {DescriptorSets.ReservedTextures} and {DescriptorSets.MaterialTextures} hold textures."),
        };

        GLApi.BindTextureUnit(unit, HandleOf(texture));

        // Sampler object 0 is not a default sampler, it is the instruction to use the parameters on the
        // texture object. That is what the renderer's textures still carry, so a null sampler must stay 0.
        GLApi.BindSampler(unit, sampler is GLSampler glSampler ? glSampler.Handle : GLDevice.DefaultSamplerHandle);
    }

    /// <inheritdoc/>
    public void BindTransientUniform<T>(int binding, in T data) where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var size = Unsafe.SizeOf<T>();
        var (block, offset) = AllocateTransient(size);
        var source = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in data), 1));

        source.CopyTo(block.MappedData.Slice(offset, size));
        block.FlushRange(offset, size);

        GLApi.BindBufferRange(BufferRangeTarget.UniformBuffer, binding, block.Handle, offset, size);
    }

    /// <inheritdoc/>
    public void BindStorageTexture(int binding, ITexture texture, int mipLevel = 0)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(texture);

        var glTexture = AsGLTexture(texture);
        var layered = glTexture.GLLayerCount() > 1;

        GLApi.BindImageTexture(binding, glTexture.Handle, mipLevel, layered, 0, TextureAccess.ReadWrite, glTexture.InternalFormat);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">No pipeline is bound.</exception>
    public void SetPushConstants<T>(in T data, int offsetInBytes = 0) where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (graphicsPipeline is not null)
        {
            graphicsPipeline.SetPushConstants(in data, offsetInBytes);
            return;
        }

        if (computePipeline is not null)
        {
            computePipeline.SetPushConstants(in data, offsetInBytes);
            return;
        }

        throw new InvalidOperationException("Push constants are written to the bound pipeline's program, and no pipeline is bound.");
    }

    // ---- draws ----

    /// <inheritdoc/>
    public void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0)
    {
        var topology = TopologyForDraw();

        GLApi.DrawArraysInstancedBaseInstance(topology, firstVertex, vertexCount, instanceCount, (uint)firstInstance);
    }

    /// <inheritdoc/>
    /// <remarks><paramref name="firstInstance"/> reaches the shader as <c>gl_BaseInstance</c>, which is
    /// how the renderer passes the scene node id into a draw.</remarks>
    public void DrawIndexed(int indexCount, int instanceCount = 1, int firstIndex = 0, int baseVertex = 0, int firstInstance = 0)
    {
        var topology = TopologyForDraw();
        var (type, elementSize) = IndexFormat();
        var offset = indexBinding.OffsetInBytes + (firstIndex * elementSize);

        GLApi.DrawElementsInstancedBaseVertexBaseInstance(
            topology, indexCount, type, (IntPtr)offset, instanceCount, baseVertex, (uint)firstInstance);
    }

    /// <inheritdoc/>
    public void DrawIndirect(IBuffer argumentBuffer, int offsetInBytes, int drawCount, int strideInBytes = 0)
    {
        var topology = TopologyForDraw();

        BindIndirectBuffer(argumentBuffer);
        GLApi.MultiDrawArraysIndirect(topology, (IntPtr)offsetInBytes, drawCount, strideInBytes);
    }

    /// <inheritdoc/>
    public void DrawIndexedIndirect(IBuffer argumentBuffer, int offsetInBytes, int drawCount, int strideInBytes = 0)
    {
        var topology = TopologyForDraw();
        var (type, _) = IndexFormat();

        BindIndirectBuffer(argumentBuffer);
        GLApi.MultiDrawElementsIndirect(topology, type, (IntPtr)offsetInBytes, drawCount, strideInBytes);
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">The device cannot read a draw count from a buffer.</exception>
    public void DrawIndexedIndirectCount(
        IBuffer argumentBuffer,
        int argumentOffsetInBytes,
        IBuffer countBuffer,
        int countOffsetInBytes,
        int maxDrawCount,
        int strideInBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(countBuffer);

        if (!Device.Limits.SupportsDrawIndirectCount)
        {
            throw new NotSupportedException($"This device cannot take an indirect draw count from a buffer. Guard the call site with {nameof(IDeviceLimits)}.{nameof(IDeviceLimits.SupportsDrawIndirectCount)}.");
        }

        var topology = TopologyForDraw();
        var (type, _) = IndexFormat();

        BindIndirectBuffer(argumentBuffer);
        GLApi.BindBuffer(BufferTarget.ParameterBuffer, HandleOf(countBuffer));

        GLApi.MultiDrawElementsIndirectCount(
            topology, type, (IntPtr)argumentOffsetInBytes, (IntPtr)countOffsetInBytes, maxDrawCount, strideInBytes);
    }

    // ---- compute ----

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">A render pass is open.</exception>
    public void Dispatch(int groupCountX, int groupCountY = 1, int groupCountZ = 1)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureOutsideRenderPass();

        GLApi.DispatchCompute(groupCountX, groupCountY, groupCountZ);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">A render pass is open.</exception>
    public void DispatchIndirect(IBuffer argumentBuffer, int offsetInBytes)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureOutsideRenderPass();

        GLApi.BindBuffer(BufferTarget.DispatchIndirectBuffer, HandleOf(argumentBuffer));
        GLApi.DispatchComputeIndirect((IntPtr)offsetInBytes);
    }

    // ---- synchronisation ----

    /// <inheritdoc/>
    /// <remarks>Collapses to one <c>glMemoryBarrier</c> carrying the union of the bits, because the
    /// OpenGL barrier is global rather than per resource. Most transitions translate to nothing and
    /// cost no GL call; see <see cref="GlBarrierTranslation"/> for which and why.</remarks>
    public void Barrier(ReadOnlySpan<BufferBarrier> bufferBarriers, ReadOnlySpan<TextureBarrier> textureBarriers)
        => GlBarrierTranslation.Submit(GlBarrierTranslation.Translate(bufferBarriers, textureBarriers));

    /// <inheritdoc/>
    public void Barrier(in TextureBarrier barrier)
        => GlBarrierTranslation.Submit(GlBarrierTranslation.Translate(in barrier));

    /// <inheritdoc/>
    public void Barrier(in BufferBarrier barrier)
        => GlBarrierTranslation.Submit(GlBarrierTranslation.Translate(in barrier));

    // ---- transfers ----

    /// <inheritdoc/>
    public void CopyBuffer(IBuffer source, int sourceOffsetInBytes, IBuffer destination, int destinationOffsetInBytes, int sizeInBytes)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        GLApi.CopyNamedBufferSubData(HandleOf(source), HandleOf(destination), (IntPtr)sourceOffsetInBytes, (IntPtr)destinationOffsetInBytes, sizeInBytes);
    }

    /// <inheritdoc/>
    public void CopyTexture(ITexture source, ITexture destination, int mipLevel = 0)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var src = AsGLTexture(source);
        var dst = AsGLTexture(destination);
        var (width, height, depth) = src.MipExtent(mipLevel);
        var layers = src.Dimension == TextureDimension.Texture3D ? depth : src.GLLayerCount();

        // ImageTarget and TextureTarget are the same GL enumerants; OpenTK just gives the image copy
        // entry points their own enum.
        GLApi.CopyImageSubData(
            src.Handle, (ImageTarget)src.Target, mipLevel, 0, 0, 0,
            dst.Handle, (ImageTarget)dst.Target, mipLevel, 0, 0, 0,
            width, height, layers);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The texture's format cannot be transferred, which is
    /// every block compressed format.</exception>
    public void CopyTextureToBuffer(ITexture source, int mipLevel, int arrayLayer, IBuffer destination, int destinationOffsetInBytes = 0)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var texture = AsGLTexture(source);
        var (width, height, _) = texture.MipExtent(mipLevel);

        var pixelFormat = FormatTables.ToGLPixelFormat(texture.Format)
            ?? throw new InvalidOperationException($"Texture '{texture.Name}' is {texture.Format}, a compressed format with no client pixel format to read back through.");
        var pixelType = FormatTables.ToGLPixelType(texture.Format)!.Value;

        var buffer = HandleOf(destination);
        var available = destination.SizeInBytes - destinationOffsetInBytes;

        // Reading into a bound pixel pack buffer is what keeps the copy on the GPU; the same call with no
        // buffer bound would stall and land in client memory.
        GLApi.BindBuffer(BufferTarget.PixelPackBuffer, buffer);

        try
        {
            GLApi.GetTextureSubImage(
                texture.Handle, mipLevel, 0, 0, arrayLayer, width, height, 1,
                pixelFormat, pixelType, available, (IntPtr)destinationOffsetInBytes);
        }
        finally
        {
            GLApi.BindBuffer(BufferTarget.PixelPackBuffer, 0);
        }
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException"><paramref name="source"/> is multisampled.</exception>
    /// <remarks>
    /// Rejects a multisampled source on purpose. <c>glBlitFramebuffer</c> would resolve it silently and
    /// this backend is the parity oracle, so allowing it here would let a resolve-by-blit pass on OpenGL
    /// and fail on Vulkan, where <c>vkCmdBlitImage</c> rejects a multisampled source outright. Resolve
    /// through <see cref="ColorAttachmentDesc.ResolveTexture"/>, which both backends implement natively.
    /// </remarks>
    public void BlitTexture(ITexture source, ITexture destination, FilterMode filter = FilterMode.Linear)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var src = AsGLTexture(source);
        var dst = AsGLTexture(destination);

        if (src.SampleCount > 1)
        {
            throw new InvalidOperationException(
                $"Texture '{src.Name}' is multisampled and cannot be blitted. Resolve it with {nameof(ColorAttachmentDesc)}.{nameof(ColorAttachmentDesc.ResolveTexture)} instead: a blit would resolve it here but fail on Vulkan.");
        }

        var isDepth = RhiFormatInfo.IsDepth(src.Format);
        var mask = isDepth ? ClearBufferMask.DepthBufferBit : ClearBufferMask.ColorBufferBit;

        if (isDepth && RhiFormatInfo.IsStencil(src.Format))
        {
            mask |= ClearBufferMask.StencilBufferBit;
        }

        // A depth or stencil blit has to be unfiltered; OpenGL rejects anything else.
        var glFilter = isDepth || filter == FilterMode.Nearest ? BlitFramebufferFilter.Nearest : BlitFramebufferFilter.Linear;

        var readFbo = GetOrCreateTransferFramebuffer(src, isDepth);
        var drawFbo = GetOrCreateTransferFramebuffer(dst, isDepth);

        if (!isDepth)
        {
            GLApi.NamedFramebufferReadBuffer(readFbo, ReadBufferMode.ColorAttachment0);
        }

        GLApi.BlitNamedFramebuffer(
            readFbo, drawFbo,
            0, 0, src.Width, src.Height,
            0, 0, dst.Width, dst.Height,
            mask, glFilter);

        RestorePassFramebuffer();
    }

    /// <inheritdoc/>
    public unsafe void FillBuffer(IBuffer buffer, int offsetInBytes, int sizeInBytes, uint value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var handle = HandleOf(buffer);

        // The pointer overload, because the generic one this binding offers is deprecated and its
        // suggested replacement does not exist here.
        GLApi.ClearNamedBufferSubData(
            handle, PixelInternalFormat.R32ui, (IntPtr)offsetInBytes, sizeInBytes,
            PixelFormat.RedInteger, PixelType.UnsignedInt, (IntPtr)(&value));
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The texture's format has no client format to fill through.</exception>
    public void ClearTexture(ITexture texture, int mipLevel, uint value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var glTexture = AsGLTexture(texture);

        var pixelFormat = FormatTables.ToGLPixelFormat(glTexture.Format)
            ?? throw new InvalidOperationException($"Texture '{glTexture.Name}' is {glTexture.Format}, a compressed format that cannot be cleared.");
        var pixelType = FormatTables.ToGLPixelType(glTexture.Format)!.Value;

        GLApi.ClearTexImage(glTexture.Handle, mipLevel, pixelFormat, pixelType, ref value);
    }

    // ---- debugging ----

    /// <inheritdoc/>
    public IDisposable DebugScope(string name)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        return new DebugGroup(name);
    }

    /// <inheritdoc/>
    public void DebugMarker(string name)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(name);

        GLApi.DebugMessageInsert(DebugSourceExternal.DebugSourceApplication, DebugType.DebugTypeMarker, 0, DebugSeverity.DebugSeverityNotification, name.Length, name);
    }

    // ---- render pass internals ----

    private static (int Width, int Height) PassExtent(ColorAttachmentDesc[] colors, DepthAttachmentDesc? depth)
    {
        if (colors.Length > 0)
        {
            var texture = AsGLTexture(colors[0].Texture);
            var (width, height, _) = texture.MipExtent(colors[0].MipLevel);

            return (width, height);
        }

        var depthTexture = AsGLTexture(depth!.Value.Texture);
        var (depthWidth, depthHeight, _) = depthTexture.MipExtent(depth.Value.MipLevel);

        return (depthWidth, depthHeight);
    }

    private void ApplyLoadOps(ColorAttachmentDesc[] colors, DepthAttachmentDesc? depth)
    {
        var clearsColor = false;

        foreach (var attachment in colors)
        {
            clearsColor |= attachment.LoadOp == LoadOp.Clear;
        }

        var clearsDepth = depth is { DepthLoadOp: LoadOp.Clear };
        var clearsStencil = depth is { StencilLoadOp: LoadOp.Clear };

        DiscardOnLoad(colors, depth);

        if (!clearsColor && !clearsDepth && !clearsStencil)
        {
            return;
        }

        // Framebuffer clears obey the colour, depth and stencil write masks, and between scopes OpenGL
        // holds whatever the last draw applied. Forcing the masks open for the clear is what stops a
        // pass inheriting a restrictive mask from the previous one and clearing nothing.
        var clearState = renderState.CurrentPass;
        clearState.DepthStencil.DepthWriteEnable = true;
        clearState.Blend.RenderTargetWriteMask = 0xF;

        var stencil = clearState.DepthStencil.Stencil;
        stencil.WriteMask = 0xFF;
        clearState.DepthStencil.Stencil = stencil;

        using var scope = new RenderPassScope(renderState, in clearState);

        for (var i = 0; i < colors.Length; i++)
        {
            if (colors[i].LoadOp == LoadOp.Clear)
            {
                ClearColorAttachment(i, colors[i]);
            }
        }

        ClearDepthStencil(depth, clearsDepth, clearsStencil);
    }

    private void ClearColorAttachment(int index, in ColorAttachmentDesc attachment)
    {
        var format = AsGLTexture(attachment.Texture).Format;
        var color = attachment.ClearColor;

        // An integer attachment takes an integer clear; handing it the float entry point clears garbage.
        switch (ClearKindOf(format))
        {
            case ClearKind.SignedInteger:
                Span<int> signed = [(int)color.X, (int)color.Y, (int)color.Z, (int)color.W];
                GLApi.ClearNamedFramebuffer(passFramebuffer, ClearBuffer.Color, index, ref signed[0]);
                break;

            case ClearKind.UnsignedInteger:
                Span<uint> unsigned = [(uint)color.X, (uint)color.Y, (uint)color.Z, (uint)color.W];
                // The unsigned entry point only has a uint framebuffer overload in this binding.
                GLApi.ClearNamedFramebuffer((uint)passFramebuffer, ClearBuffer.Color, index, ref unsigned[0]);
                break;

            default:
                Span<float> floats = [color.X, color.Y, color.Z, color.W];
                GLApi.ClearNamedFramebuffer(passFramebuffer, ClearBuffer.Color, index, ref floats[0]);
                break;
        }
    }

    private void ClearDepthStencil(DepthAttachmentDesc? depth, bool clearsDepth, bool clearsStencil)
    {
        if (depth is not { } attachment)
        {
            return;
        }

        if (clearsDepth && clearsStencil)
        {
            GLApi.ClearNamedFramebuffer(passFramebuffer, ClearBufferCombined.DepthStencil, 0, attachment.ClearDepth, attachment.ClearStencil);
            return;
        }

        if (clearsDepth)
        {
            var value = attachment.ClearDepth;
            GLApi.ClearNamedFramebuffer(passFramebuffer, ClearBuffer.Depth, 0, ref value);
            return;
        }

        if (clearsStencil)
        {
            var value = (int)attachment.ClearStencil;
            GLApi.ClearNamedFramebuffer(passFramebuffer, ClearBuffer.Stencil, 0, ref value);
        }
    }

    private void DiscardOnLoad(ColorAttachmentDesc[] colors, DepthAttachmentDesc? depth)
    {
        Span<FramebufferAttachment> discard = stackalloc FramebufferAttachment[colors.Length + 2];
        var count = 0;

        for (var i = 0; i < colors.Length; i++)
        {
            if (colors[i].LoadOp == LoadOp.DontCare)
            {
                discard[count++] = FramebufferAttachment.ColorAttachment0 + i;
            }
        }

        if (depth is { DepthLoadOp: LoadOp.DontCare })
        {
            discard[count++] = FramebufferAttachment.DepthAttachment;
        }

        if (depth is { StencilLoadOp: LoadOp.DontCare } && RhiFormatInfo.IsStencil(AsGLTexture(depth.Value.Texture).Format))
        {
            discard[count++] = FramebufferAttachment.StencilAttachment;
        }

        if (count > 0)
        {
            GLApi.InvalidateNamedFramebufferData(passFramebuffer, count, ref discard[0]);
        }
    }

    private void ApplyStoreOps()
    {
        Span<FramebufferAttachment> discard = stackalloc FramebufferAttachment[passColorAttachments.Length + 2];
        var count = 0;

        for (var i = 0; i < passColorAttachments.Length; i++)
        {
            if (passColorAttachments[i].StoreOp == StoreOp.DontCare)
            {
                discard[count++] = FramebufferAttachment.ColorAttachment0 + i;
            }
        }

        if (passDepthAttachment is { DepthStoreOp: StoreOp.DontCare })
        {
            discard[count++] = FramebufferAttachment.DepthAttachment;
        }

        if (passDepthAttachment is { StencilStoreOp: StoreOp.DontCare } && RhiFormatInfo.IsStencil(AsGLTexture(passDepthAttachment.Value.Texture).Format))
        {
            discard[count++] = FramebufferAttachment.StencilAttachment;
        }

        if (count > 0)
        {
            GLApi.InvalidateNamedFramebufferData(passFramebuffer, count, ref discard[0]);
        }
    }

    private void ResolveAttachments()
    {
        for (var i = 0; i < passColorAttachments.Length; i++)
        {
            if (passColorAttachments[i].ResolveTexture is not { } resolveTexture)
            {
                continue;
            }

            var destination = AsGLTexture(resolveTexture);
            var resolveFbo = GetOrCreateTransferFramebuffer(destination, isDepth: false);

            // glBlitFramebuffer between a multisampled read and a single-sampled draw IS the resolve, and
            // is the only mechanism OpenGL has for one. That is not the same thing as exposing a blit as
            // the resolve API: on Vulkan this becomes pResolveAttachments, not vkCmdBlitImage.
            GLApi.NamedFramebufferReadBuffer(passFramebuffer, (ReadBufferMode)((int)ReadBufferMode.ColorAttachment0 + i));

            GLApi.BlitNamedFramebuffer(
                passFramebuffer, resolveFbo,
                0, 0, passWidth, passHeight,
                0, 0, destination.Width, destination.Height,
                ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        }

        if (passColorAttachments.Length > 0)
        {
            RestorePassFramebuffer();
        }
    }

    private void RestorePassFramebuffer()
    {
        if (inRenderPass)
        {
            GLApi.BindFramebuffer(FramebufferTarget.Framebuffer, passFramebuffer);
        }
    }

    // ---- framebuffer cache ----

    private int GetOrCreateFramebuffer(ColorAttachmentDesc[] colors, DepthAttachmentDesc? depth)
    {
        Span<AttachmentKey> colorKeys = stackalloc AttachmentKey[colors.Length];

        for (var i = 0; i < colors.Length; i++)
        {
            colorKeys[i] = KeyOf(colors[i].Texture, colors[i].MipLevel, colors[i].ArrayLayer);
        }

        AttachmentKey? depthKey = depth is { } d ? KeyOf(d.Texture, d.MipLevel, d.ArrayLayer) : null;

        foreach (var cached in framebuffers)
        {
            if (cached.Matches(colorKeys, depthKey))
            {
                return cached.Framebuffer;
            }
        }

        return CreateFramebuffer(colors, depth, colorKeys, depthKey);
    }

    private int CreateFramebuffer(ColorAttachmentDesc[] colors, DepthAttachmentDesc? depth, ReadOnlySpan<AttachmentKey> colorKeys, AttachmentKey? depthKey)
    {
        GLApi.CreateFramebuffers(1, out int fbo);

        for (var i = 0; i < colors.Length; i++)
        {
            Attach(fbo, FramebufferAttachment.ColorAttachment0 + i, colors[i].Texture, colors[i].MipLevel, colors[i].ArrayLayer);
        }

        if (depth is { } depthAttachment)
        {
            var format = AsGLTexture(depthAttachment.Texture).Format;
            var point = RhiFormatInfo.IsStencil(format) ? FramebufferAttachment.DepthStencilAttachment : FramebufferAttachment.DepthAttachment;

            Attach(fbo, point, depthAttachment.Texture, depthAttachment.MipLevel, depthAttachment.ArrayLayer);
        }

        SetDrawBuffers(fbo, colors.Length);

        var status = GLApi.CheckNamedFramebufferStatus(fbo, FramebufferTarget.Framebuffer);

        if (status != FramebufferStatus.FramebufferComplete)
        {
            GLApi.DeleteFramebuffer(fbo);
            throw new InvalidOperationException($"Render pass attachments do not form a complete framebuffer: {status}.");
        }

        EvictIfFull();
        framebuffers.Add(new CachedFramebuffer(fbo, colorKeys.ToArray(), depthKey));

        return fbo;
    }

    private static void SetDrawBuffers(int fbo, int colorCount)
    {
        if (colorCount == 0)
        {
            // A depth-only pass must say so, or OpenGL leaves the default colour draw buffer selected on
            // a framebuffer that has no colour attachment and reports it incomplete.
            GLApi.NamedFramebufferDrawBuffers(fbo, 0, ref Unsafe.NullRef<DrawBuffersEnum>());
            return;
        }

        Span<DrawBuffersEnum> buffers = stackalloc DrawBuffersEnum[colorCount];

        for (var i = 0; i < colorCount; i++)
        {
            buffers[i] = DrawBuffersEnum.ColorAttachment0 + i;
        }

        GLApi.NamedFramebufferDrawBuffers(fbo, colorCount, ref buffers[0]);
    }

    private static void Attach(int fbo, FramebufferAttachment point, ITexture texture, int mipLevel, int arrayLayer)
    {
        var glTexture = AsGLTexture(texture);

        if (glTexture.GLLayerCount() > 1)
        {
            GLApi.NamedFramebufferTextureLayer(fbo, point, glTexture.Handle, mipLevel, arrayLayer);
            return;
        }

        GLApi.NamedFramebufferTexture(fbo, point, glTexture.Handle, mipLevel);
    }

    private int GetOrCreateTransferFramebuffer(GLTexture texture, bool isDepth)
    {
        var key = new AttachmentKey(texture.Handle, 0, 0);

        foreach (var cached in framebuffers)
        {
            if (cached.IsTransferFor(key, isDepth))
            {
                return cached.Framebuffer;
            }
        }

        GLApi.CreateFramebuffers(1, out int fbo);

        if (isDepth)
        {
            var point = RhiFormatInfo.IsStencil(texture.Format) ? FramebufferAttachment.DepthStencilAttachment : FramebufferAttachment.DepthAttachment;
            GLApi.NamedFramebufferTexture(fbo, point, texture.Handle, 0);
            SetDrawBuffers(fbo, 0);
        }
        else
        {
            GLApi.NamedFramebufferTexture(fbo, FramebufferAttachment.ColorAttachment0, texture.Handle, 0);
            SetDrawBuffers(fbo, 1);
        }

        EvictIfFull();
        framebuffers.Add(CachedFramebuffer.Transfer(fbo, key, isDepth));

        return fbo;
    }

    private void EvictIfFull()
    {
        if (framebuffers.Count < FramebufferCacheLimit)
        {
            return;
        }

        // Attachments are keyed by texture handle, so a resize retires every entry that referenced the
        // old textures. Evicting oldest-first bounds what that leaves behind.
        var oldest = framebuffers[0];
        framebuffers.RemoveAt(0);
        GLApi.DeleteFramebuffer(oldest.Framebuffer);
    }

    private static AttachmentKey KeyOf(ITexture texture, int mipLevel, int arrayLayer)
        => new(AsGLTexture(texture).Handle, mipLevel, arrayLayer);

    // ---- vertex array cache ----

    private int GetOrCreateVertexArray(GLGraphicsPipeline pipeline)
    {
        var hash = pipeline.CacheKey.VertexInputHash;

        if (vertexArrays.TryGetValue(hash, out var existing))
        {
            return existing;
        }

        GLApi.CreateVertexArrays(1, out int vao);

        var input = pipeline.Description.VertexInput;

        foreach (var attribute in input.Attributes ?? [])
        {
            GLApi.EnableVertexArrayAttrib(vao, attribute.Location);
            SetAttribFormat(vao, attribute.Location, attribute.Format, attribute.OffsetInBytes);
            GLApi.VertexArrayAttribBinding(vao, attribute.Location, attribute.Binding);
        }

        foreach (var binding in input.Bindings ?? [])
        {
            GLApi.VertexArrayBindingDivisor(vao, binding.Binding, binding.PerInstance ? 1 : 0);
        }

        vertexArrays[hash] = vao;
        return vao;
    }

    // Mirrors VertexArray.SetAttribFormat, which does the same job from the DXGI format the vertex
    // layouts carry. :VertexAttributeFormat - keep the two in step.
    private static void SetAttribFormat(int vao, int location, RhiFormat format, int offsetInBytes)
    {
        var (count, type, normalized, integer) = format switch
        {
            RhiFormat.R32_SFloat => (1, VertexAttribType.Float, false, false),
            RhiFormat.R32G32_SFloat => (2, VertexAttribType.Float, false, false),
            RhiFormat.R32G32B32_SFloat => (3, VertexAttribType.Float, false, false),
            RhiFormat.R32G32B32A32_SFloat => (4, VertexAttribType.Float, false, false),
            RhiFormat.R16G16_SFloat => (2, VertexAttribType.HalfFloat, false, false),
            RhiFormat.R16G16B16A16_SFloat => (4, VertexAttribType.HalfFloat, false, false),

            RhiFormat.R8G8B8A8_UNorm => (4, VertexAttribType.UnsignedByte, true, false),
            RhiFormat.R16G16_UNorm => (2, VertexAttribType.UnsignedShort, true, false),
            RhiFormat.R16G16B16A16_UNorm => (4, VertexAttribType.UnsignedShort, true, false),
            RhiFormat.R16G16_SNorm => (2, VertexAttribType.Short, true, false),
            RhiFormat.R8G8B8A8_SNorm => (4, VertexAttribType.Byte, true, false),

            RhiFormat.R32_UInt => (1, VertexAttribType.UnsignedInt, false, true),
            RhiFormat.R32G32_UInt => (2, VertexAttribType.UnsignedInt, false, true),
            RhiFormat.R32G32B32A32_UInt => (4, VertexAttribType.UnsignedInt, false, true),
            RhiFormat.R32_SInt => (1, VertexAttribType.Int, false, true),
            RhiFormat.R8G8B8A8_UInt => (4, VertexAttribType.UnsignedByte, false, true),
            RhiFormat.R16G16B16A16_UInt => (4, VertexAttribType.UnsignedShort, false, true),
            RhiFormat.R16G16_SInt => (2, VertexAttribType.Short, false, true),
            RhiFormat.R16G16B16A16_SInt => (4, VertexAttribType.Short, false, true),
            RhiFormat.R32G32B32A32_SInt => (4, VertexAttribType.Int, false, true),

            _ => throw new NotSupportedException($"Vertex attribute format {format} has no OpenGL vertex attribute mapping (location {location})."),
        };

        if (integer)
        {
            GLApi.VertexArrayAttribIFormat(vao, location, count, (VertexAttribIType)type, offsetInBytes);
            return;
        }

        GLApi.VertexArrayAttribFormat(vao, location, count, type, normalized, offsetInBytes);
    }

    private void ReapplyBufferBindings()
    {
        for (var i = 0; i < vertexBindings.Length; i++)
        {
            if (vertexBindings[i].Valid)
            {
                ApplyVertexBinding(i);
            }
        }

        if (indexBinding.Valid)
        {
            GLApi.VertexArrayElementBuffer(boundVertexArray, indexBinding.Handle);
        }
    }

    private void ApplyVertexBinding(int binding)
    {
        var stride = StrideFor(binding);
        var value = vertexBindings[binding];

        GLApi.VertexArrayVertexBuffer(boundVertexArray, binding, value.Handle, (IntPtr)value.OffsetInBytes, stride);
    }

    private int StrideFor(int binding)
    {
        foreach (var descriptor in graphicsPipeline?.Description.VertexInput.Bindings ?? [])
        {
            if (descriptor.Binding == binding)
            {
                return descriptor.StrideInBytes;
            }
        }

        return 0;
    }

    // ---- transient uniform ring ----

    private (GLBuffer Block, int Offset) AllocateTransient(int sizeInBytes)
    {
        if (transientAlignment == 0)
        {
            transientAlignment = Math.Max(1, GLApi.GetInteger(GetPName.UniformBufferOffsetAlignment));
        }

        var aligned = (transientOffset + transientAlignment - 1) / transientAlignment * transientAlignment;
        var blockSize = Math.Max(TransientBlockSize, sizeInBytes);

        if (transientBlockIndex >= transientBlocks.Count || aligned + sizeInBytes > transientBlocks[transientBlockIndex].SizeInBytes)
        {
            // A new block rather than a bigger one: ranges already bound out of the current block stay
            // valid, which they would not if it were reallocated.
            if (transientBlocks.Count > 0 && transientBlockIndex < transientBlocks.Count)
            {
                transientBlockIndex++;
            }

            while (transientBlockIndex >= transientBlocks.Count)
            {
                transientBlocks.Add(new GLBuffer(new BufferDesc(blockSize, BufferUsage.Uniform, BufferMemory.HostUpload, $"{name} transient uniforms {transientBlocks.Count}")));
            }

            aligned = 0;
        }

        transientOffset = aligned + sizeInBytes;
        return (transientBlocks[transientBlockIndex], aligned);
    }

    // ---- helpers ----

    private PrimitiveType TopologyForDraw()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureInRenderPass();

        if (graphicsPipeline is null)
        {
            throw new InvalidOperationException("A draw needs a graphics pipeline bound, which supplies its topology and vertex input.");
        }

        return graphicsPipeline.Topology;
    }

    private (DrawElementsType Type, int ElementSize) IndexFormat()
    {
        if (!indexBinding.Valid)
        {
            throw new InvalidOperationException($"An indexed draw needs an index buffer bound through {nameof(BindIndexBuffer)}.");
        }

        return indexBinding.IndexType == IndexType.UInt16
            ? (DrawElementsType.UnsignedShort, sizeof(ushort))
            : (DrawElementsType.UnsignedInt, sizeof(uint));
    }

    private static void BindIndirectBuffer(IBuffer argumentBuffer)
        => GLApi.BindBuffer(BufferTarget.DrawIndirectBuffer, HandleOf(argumentBuffer));

    private void EnsureInRenderPass()
    {
        if (!inRenderPass)
        {
            throw new InvalidOperationException($"This command is only valid inside a render pass. Open one with {nameof(BeginRenderPass)}.");
        }
    }

    private void EnsureOutsideRenderPass()
    {
        if (inRenderPass)
        {
            throw new InvalidOperationException($"Dispatch is not valid inside a render pass. Close it with {nameof(EndRenderPass)} first.");
        }
    }

    private static int HandleOf(IBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return buffer is GLBuffer glBuffer
            ? glBuffer.Handle
            : throw new ArgumentException($"Expected a {nameof(GLBuffer)}, got {buffer.GetType().Name}.", nameof(buffer));
    }

    private static int HandleOf(ITexture texture) => AsGLTexture(texture).Handle;

    private static GLTexture AsGLTexture(ITexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);

        return texture is GLTexture glTexture
            ? glTexture
            : throw new ArgumentException($"Expected a {nameof(GLTexture)}, got {texture.GetType().Name}.", nameof(texture));
    }

    private enum ClearKind
    {
        Float,
        SignedInteger,
        UnsignedInteger,
    }

    private static ClearKind ClearKindOf(RhiFormat format) => format switch
    {
        RhiFormat.R8_UInt or RhiFormat.R16_UInt or RhiFormat.R32_UInt or RhiFormat.R32G32_UInt
            or RhiFormat.R32G32B32A32_UInt or RhiFormat.R8G8B8A8_UInt or RhiFormat.R16G16B16A16_UInt
            => ClearKind.UnsignedInteger,

        RhiFormat.R32_SInt or RhiFormat.R16G16_SInt or RhiFormat.R16G16B16A16_SInt or RhiFormat.R32G32B32A32_SInt
            => ClearKind.SignedInteger,

        _ => ClearKind.Float,
    };

    private readonly record struct AttachmentKey(int Handle, int MipLevel, int ArrayLayer);

    private readonly record struct VertexBinding(int Handle, int OffsetInBytes, bool Valid);

    private readonly record struct IndexBinding(int Handle, IndexType IndexType, int OffsetInBytes, bool Valid);

    private sealed class CachedFramebuffer
    {
        private readonly AttachmentKey[] colors;
        private readonly AttachmentKey? depth;
        private readonly bool isTransfer;
        private readonly bool transferIsDepth;

        public int Framebuffer { get; }

        public CachedFramebuffer(int framebuffer, AttachmentKey[] colors, AttachmentKey? depth)
        {
            Framebuffer = framebuffer;
            this.colors = colors;
            this.depth = depth;
        }

        private CachedFramebuffer(int framebuffer, AttachmentKey key, bool isDepth)
        {
            Framebuffer = framebuffer;
            colors = isDepth ? [] : [key];
            depth = isDepth ? key : null;
            isTransfer = true;
            transferIsDepth = isDepth;
        }

        public static CachedFramebuffer Transfer(int framebuffer, AttachmentKey key, bool isDepth)
            => new(framebuffer, key, isDepth);

        public bool Matches(ReadOnlySpan<AttachmentKey> otherColors, AttachmentKey? otherDepth)
        {
            if (isTransfer || colors.Length != otherColors.Length || depth != otherDepth)
            {
                return false;
            }

            for (var i = 0; i < colors.Length; i++)
            {
                if (colors[i] != otherColors[i])
                {
                    return false;
                }
            }

            return true;
        }

        public bool IsTransferFor(AttachmentKey key, bool isDepth)
            => isTransfer && transferIsDepth == isDepth && (isDepth ? depth == key : colors.Length == 1 && colors[0] == key);
    }

    private sealed class DebugGroup : IDisposable
    {
        private bool closed;

        public DebugGroup(string name)
        {
            ArgumentNullException.ThrowIfNull(name);

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

    /// <summary>Releases the framebuffers, vertex arrays and transient buffers this command list cached.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        foreach (var cached in framebuffers)
        {
            GLApi.DeleteFramebuffer(cached.Framebuffer);
        }

        framebuffers.Clear();

        foreach (var vao in vertexArrays.Values)
        {
            GLApi.DeleteVertexArray(vao);
        }

        vertexArrays.Clear();

        foreach (var block in transientBlocks)
        {
            block.Dispose();
        }

        transientBlocks.Clear();
    }
}
