using System.Globalization;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Descriptors;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// <see cref="ICommandList"/> on Vulkan: records into a <c>VkCommandBuffer</c> taken from the frame
/// ring's pool, using dynamic rendering and synchronization2 throughout.
/// </summary>
/// <remarks>
/// <para>
/// <b>No render pass or framebuffer object exists.</b> <see cref="BeginRenderPass"/> is
/// <c>vkCmdBeginRendering</c>, which takes the attachments and their load and store operations
/// directly, so there is nothing to cache or invalidate when a target is resized. The only per-pass
/// object is the single-mip image view each attachment needs, which
/// <see cref="VulkanAttachmentViewCache"/> keeps.
/// </para>
/// <para>
/// <b>Y is flipped by a negative viewport height, never in shaders.</b> The renderer's call sites hand
/// this class the same numbers they hand <c>glViewport</c>, whose origin is the bottom left; Vulkan's
/// is the top left. Reconciling that in the viewport keeps every screen-space derivative, every
/// <c>gl_FragCoord</c> read and the whole post-process chain correct, and it keeps one shader source
/// feeding both backends. Depth needs no such treatment: the renderer is already reverse-Z through
/// <c>ClipControl</c>, which is Vulkan's native zero-to-one convention. See <see cref="SetViewport"/>.
/// </para>
/// <para>
/// <b>MSAA is resolved by the render pass, never by a blit.</b> A resolve is
/// <see cref="ColorAttachmentDesc.ResolveTexture"/>, which becomes <c>pResolveAttachments</c> here.
/// <see cref="BlitTexture"/> rejects a multisampled source, as the OpenGL backend deliberately does,
/// because <c>glBlitFramebuffer</c> would resolve one implicitly and let a resolve-by-blit pass on the
/// parity oracle while failing outright on <c>vkCmdBlitImage</c>.
/// </para>
/// <para>
/// <b>Layout is never guessed.</b> Every image barrier goes through
/// <see cref="VulkanTexture.TransitionTo"/>, which reads the image's tracked state for its
/// <c>oldLayout</c>, and every descriptor names the layout that same tracking reports. A texture whose
/// tracked state cannot be bound the way a call asks &#8212; the one that arrives from
/// <see cref="IDevice.UploadTexture"/> still in <see cref="ResourceState.CopyDestination"/> being the
/// common case &#8212; is refused by name rather than bound in a layout the descriptor is not allowed
/// to see.
/// </para>
/// <para>
/// Reuse one across frames: <see cref="Begin"/> rebinds it to the frame's command buffer and drops the
/// per-frame state, and the image view cache survives, which is the only thing worth keeping.
/// </para>
/// </remarks>
public sealed unsafe class VulkanCommandList : ICommandList
{
    // Tightly packed strides for the indirect argument structures, used when a caller passes 0. Vulkan
    // has no "packed" sentinel and rejects a zero stride for a multi-draw, where OpenGL takes it.
    private const int DrawIndirectStride = 16;
    private const int DrawIndexedIndirectStride = 20;

    private readonly VulkanDevice Owner;
    private readonly Vk Api;
    private readonly IVulkanDescriptorBinder? Binder;
    private readonly VulkanAttachmentViewCache Views = new();
    private readonly string ListName;

    private CommandBuffer Command;
    private IVulkanPipeline? Pipeline;
    private bool PipelineIsGraphics;

    // A flag rather than the buffer: nothing here reads it back, the index type and offset live in the
    // command buffer once bound, and holding a reference to a resource this list does not own would
    // outlive its deferred destruction.
    private bool IndexBufferBound;

    // One bit per vertex buffer binding filled on the current command buffer, for the same reason and
    // held the same way. Vertex bindings survive a pipeline change, so this is reset per command buffer
    // rather than per pipeline.
    private uint VertexBuffersBound;

    private bool Recording;
    private bool InRenderPass;
    private int PassHeight;
    private bool Disposed;

    /// <summary>The images the open render pass writes, so a bind can tell it would be a feedback loop.</summary>
    /// <remarks>Roots rather than views: an attachment and a texture sampled through a view of it are the
    /// same image, and it is the image that has one layout.</remarks>
    private readonly List<VulkanTexture> PassAttachments = [];

    /// <inheritdoc/>
    public IDevice Device => Owner;

    /// <summary>Gets the command buffer being recorded into.</summary>
    public CommandBuffer Handle => Command;

    /// <summary>Gets a value indicating whether a command buffer is currently bound for recording.</summary>
    public bool IsRecording => Recording;

    /// <summary>Gets the descriptor binder this list writes bindings through, or
    /// <see langword="null"/> when it was created without one.</summary>
    /// <remarks>Null is a usable state on purpose. Transfers, barriers, clears, render passes and debug
    /// labels need no descriptors, and they are the half of this surface that can be exercised on real
    /// hardware before the descriptor layer exists; the binding calls are the ones that refuse.</remarks>
    public IVulkanDescriptorBinder? DescriptorBinder => Binder;

    /// <summary>Creates a command list.</summary>
    /// <param name="device">The device that produced this command list.</param>
    /// <param name="binder">Where descriptor bindings are accumulated, or <see langword="null"/> when
    /// the descriptor layer is not present and the binding calls should refuse.</param>
    /// <param name="name">Debug label for the recorded work.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    /// <remarks>Public so that any device can hand one out, exactly as <c>GLCommandList</c> is:
    /// <see cref="VulkanDevice.BeginCommandList"/> throws by design and the device that overrides it
    /// need not be this class's friend.</remarks>
    public VulkanCommandList(VulkanDevice device, IVulkanDescriptorBinder? binder, string name)
    {
        ArgumentNullException.ThrowIfNull(device);

        Owner = device;
        Api = device.Core.Api;
        Binder = binder;
        ListName = name ?? string.Empty;
    }

    /// <summary>Binds this list to a command buffer and drops the state belonging to the last one.</summary>
    /// <param name="commandBuffer">A command buffer already in the recording state, normally from
    /// <see cref="Core.VulkanCommandPool.Acquire"/>.</param>
    /// <remarks>The image view cache deliberately survives: its entries are keyed on textures, cost a
    /// driver allocation each, and stay valid for as long as those textures do.</remarks>
    public void Begin(CommandBuffer commandBuffer)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        Command = commandBuffer;
        Recording = true;

        Pipeline = null;
        PipelineIsGraphics = false;
        IndexBufferBound = false;
        VertexBuffersBound = 0;

        InRenderPass = false;
        PassHeight = 0;

        Binder?.Reset(commandBuffer);
    }

    /// <summary>Stops accepting commands, leaving the command buffer ready to be ended and submitted.</summary>
    /// <exception cref="InvalidOperationException">A render pass is still open.</exception>
    /// <remarks>Ending the command buffer itself belongs to whoever submits it, which is the device.</remarks>
    public void End()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        if (InRenderPass)
        {
            throw new InvalidOperationException($"Command list '{ListName}' still has a render pass open. Close it with {nameof(EndRenderPass)} before submitting.");
        }

        Recording = false;
    }

    /// <summary>
    /// Drops whatever was being recorded, without ending the command buffer or requiring a render pass
    /// to have been closed.
    /// </summary>
    /// <remarks>
    /// For a frame that ended while this list was still open: the frame ring resets the slot's command
    /// pool on the way into the next frame, which invalidates every buffer it handed out, so the
    /// recording is already gone and only this object's opinion of it remains. Deliberately not a way to
    /// discard work mid-frame &#8212; that is what <see cref="VulkanRecordingDevice.BeginCommandList"/>
    /// refuses, and this is what keeps the refusal from outliving the frame that earned it.
    /// </remarks>
    public void Abandon()
    {
        if (Disposed)
        {
            return;
        }

        Command = default;
        Recording = false;
        InRenderPass = false;
        PassHeight = 0;

        Pipeline = null;
        PipelineIsGraphics = false;
        IndexBufferBound = false;
        VertexBuffersBound = 0;
    }

    // ---- render passes ----

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">The pass has no attachments.</exception>
    /// <exception cref="InvalidOperationException">A pass is already open, an attachment lacks the
    /// usage it is being put to, or a resolve was asked for from a single-sampled attachment.</exception>
    /// <remarks>
    /// <para>
    /// <b>Barriers inserted here, and why.</b> Every attachment is transitioned into its attachment
    /// state before <c>vkCmdBeginRendering</c>: colour and resolve targets to
    /// <see cref="ResourceState.ColorTarget"/>, depth to <see cref="ResourceState.DepthWrite"/> or, when
    /// the attachment is read-only, <see cref="ResourceState.DepthRead"/>. This is not a convenience the
    /// caller could have provided instead: dynamic rendering forbids layout transitions inside a
    /// rendering instance, so the last moment an attachment can reach its layout is here, and the
    /// texture that a pass renders into has almost always just been sampled by the pass before it. The
    /// transition is skipped when the image is already in the state, so a pass that reopens over the
    /// same targets costs nothing.
    /// </para>
    /// <para>
    /// <see cref="LoadOp.Load"/> keeps the contents across the transition, which a layout change
    /// preserves; only a transition out of <see cref="ResourceState.Undefined"/> discards, and an
    /// attachment loaded from that state was already undefined.
    /// </para>
    /// </remarks>
    public void BeginRenderPass(in RenderPassDesc desc)
    {
        EnsureRecording();

        if (InRenderPass)
        {
            throw new InvalidOperationException($"A render pass is already open on command list '{ListName}'. Close it with {nameof(EndRenderPass)} first.");
        }

        var colors = desc.ColorAttachments ?? [];

        if (colors.Length == 0 && desc.DepthAttachment is null)
        {
            throw new ArgumentException("A render pass needs at least one attachment.", nameof(desc));
        }

        var colorInfos = new RenderingAttachmentInfo[colors.Length];
        var width = 0;
        var height = 0;

        PassAttachments.Clear();

        for (var i = 0; i < colors.Length; i++)
        {
            colorInfos[i] = BuildColorAttachment(in colors[i], ref width, ref height);
        }

        var depthInfo = default(RenderingAttachmentInfo);
        var stencilInfo = default(RenderingAttachmentInfo);
        var hasDepth = false;
        var hasStencil = false;

        if (desc.DepthAttachment is { } depthAttachment)
        {
            (depthInfo, hasStencil) = BuildDepthAttachment(in depthAttachment, ref width, ref height);
            stencilInfo = depthInfo with
            {
                LoadOp = VulkanAttachments.ToVk(depthAttachment.StencilLoadOp),
                StoreOp = VulkanAttachments.ToVk(depthAttachment.StencilStoreOp),
            };

            hasDepth = true;
        }

        PassHeight = height;

        fixed (RenderingAttachmentInfo* colorPointer = colorInfos)
        {
            var info = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D(default, new Extent2D((uint)width, (uint)height)),
                LayerCount = 1,
                ViewMask = 0,
                ColorAttachmentCount = (uint)colors.Length,
                PColorAttachments = colors.Length == 0 ? null : colorPointer,
                PDepthAttachment = hasDepth ? &depthInfo : null,
                PStencilAttachment = hasStencil ? &stencilInfo : null,
            };

            Api.CmdBeginRendering(Command, &info);
        }

        InRenderPass = true;

        // The contract says a pass opens with viewport and scissor covering the whole attachment. Both
        // are dynamic state on every pipeline this backend creates, so they have to be set explicitly.
        SetViewport(0, 0, width, height);
        SetScissor(0, 0, width, height);
    }

    private RenderingAttachmentInfo BuildColorAttachment(in ColorAttachmentDesc attachment, ref int width, ref int height)
    {
        var texture = VulkanBarrierTranslation.AsVulkanTexture(attachment.Texture);
        texture.RequireUsage(TextureUsage.ColorTarget, "Rendering into a colour attachment");

        PassAttachments.Add(texture.Root);

        var view = Views.Subresource(texture, attachment.MipLevel, attachment.ArrayLayer);
        view.TransitionTo(Command, ResourceState.ColorTarget);

        TakeExtent(view, ref width, ref height);

        var info = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = view.View,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = VulkanAttachments.ToVk(attachment.LoadOp),
            StoreOp = VulkanAttachments.ToVk(attachment.StoreOp),
            ClearValue = VulkanAttachments.ColorClear(texture.Format, attachment.ClearColor),
            ResolveMode = ResolveModeFlags.None,
        };

        if (attachment.ResolveTexture is not { } resolveTexture)
        {
            return info;
        }

        if (texture.SampleCount <= 1)
        {
            throw new InvalidOperationException(
                $"Attachment '{texture.Name}' is single-sampled, so there is nothing for {nameof(ColorAttachmentDesc.ResolveTexture)} to resolve. Copy it with {nameof(CopyTexture)} or {nameof(BlitTexture)} instead.");
        }

        var resolve = VulkanBarrierTranslation.AsVulkanTexture(resolveTexture);
        resolve.RequireUsage(TextureUsage.ColorTarget, "Resolving a multisampled attachment into a texture");

        var resolveView = Views.Subresource(resolve, 0, 0);

        // The resolve target is written by the colour attachment output stage at the end of the pass,
        // exactly like the attachment itself, so it needs the same state and the same barrier.
        resolveView.TransitionTo(Command, ResourceState.ColorTarget);

        return info with
        {
            ResolveMode = ResolveModeFlags.AverageBit,
            ResolveImageView = resolveView.View,
            ResolveImageLayout = ImageLayout.ColorAttachmentOptimal,
        };
    }

    private (RenderingAttachmentInfo Info, bool HasStencil) BuildDepthAttachment(in DepthAttachmentDesc attachment, ref int width, ref int height)
    {
        var texture = VulkanBarrierTranslation.AsVulkanTexture(attachment.Texture);
        texture.RequireUsage(TextureUsage.DepthStencilTarget, "Rendering into a depth attachment");

        var view = Views.Subresource(texture, attachment.MipLevel, attachment.ArrayLayer);

        // ReadOnly is what permits the attachment to be sampled while it is still bound, and the layout
        // is the only thing that makes that legal, so the two are decided together.
        var state = attachment.ReadOnly ? ResourceState.DepthRead : ResourceState.DepthWrite;
        view.TransitionTo(Command, state);

        if (!attachment.ReadOnly)
        {
            // Only a written attachment is a hazard to sample. A read-only one is bound precisely so it
            // can be, which is what DepthRead means.
            PassAttachments.Add(texture.Root);
        }

        TakeExtent(view, ref width, ref height);

        var info = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = view.View,
            ImageLayout = attachment.ReadOnly
                ? ImageLayout.DepthStencilReadOnlyOptimal
                : ImageLayout.DepthStencilAttachmentOptimal,
            LoadOp = VulkanAttachments.ToVk(attachment.DepthLoadOp),
            StoreOp = VulkanAttachments.ToVk(attachment.DepthStoreOp),
            ClearValue = VulkanAttachments.DepthStencilClear(attachment.ClearDepth, attachment.ClearStencil),
            ResolveMode = ResolveModeFlags.None,
        };

        return (info, RhiFormatInfo.IsStencil(texture.Format));
    }

    private static void TakeExtent(VulkanTexture view, ref int width, ref int height)
    {
        // The view already addresses a single mip level, so its own extent is the pass extent.
        width = width == 0 ? view.Width : Math.Min(width, view.Width);
        height = height == 0 ? view.Height : Math.Min(height, view.Height);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">No pass is open.</exception>
    /// <remarks>Nothing is transitioned here. The attachments stay in their attachment layouts, which
    /// is what the tracking table already says, so a pass that reopens over the same targets needs no
    /// barrier and one that samples them next issues the transition it was always going to issue.</remarks>
    /// <summary>
    /// Returns <see langword="true"/> when a texture is written by the render pass that is open.
    /// </summary>
    /// <param name="texture">The texture about to be bound for sampling.</param>
    /// <remarks>
    /// <para>
    /// Sampling an image the open pass writes is a feedback loop with no defined ordering between the
    /// write and the read. Vulkan cannot express it at all: an image holds one layout, and it cannot be
    /// a depth target and a sampled texture at once. OpenGL tolerates the same sequence, which is why
    /// this went unnoticed until a Vulkan device looked at it.
    /// </para>
    /// <para>
    /// The renderer reaches this through the shadow passes:
    /// <see cref="MeshBatchRenderer.Render"/> binds every reserved texture at the top of each pass, and
    /// the shadow atlases are reserved textures, so the sun shadow pass binds the attachment it is
    /// writing. Nothing samples it, since the depth-only shader has no use for a shadow map, so the bind
    /// is dropped rather than made an error. Later passes sample the atlas legitimately, once it is no
    /// longer an attachment and has been transitioned to <see cref="ResourceState.ShaderRead"/>.
    /// </para>
    /// <para>
    /// A read-only depth attachment is deliberately absent from the list it checks: being sampled while
    /// bound is exactly what <see cref="ResourceState.DepthRead"/> is for.
    /// </para>
    /// </remarks>
    private bool IsAttachmentOfOpenPass(VulkanTexture texture)
    {
        if (!InRenderPass)
        {
            return false;
        }

        var root = texture.Root;

        foreach (var attachment in PassAttachments)
        {
            if (ReferenceEquals(attachment, root))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public void EndRenderPass()
    {
        EnsureRecording();
        EnsureInRenderPass();

        Api.CmdEndRendering(Command);

        InRenderPass = false;
        PassHeight = 0;
        PassAttachments.Clear();
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">No render pass is open.</exception>
    /// <remarks>
    /// <para>
    /// <b>The y coordinate counts from the bottom, and the height is submitted negative.</b> Every call
    /// site hands this method the same numbers it hands <c>glViewport</c> &#8212; the barn light atlas
    /// passes a shadow caster's atlas region to both in the same breath &#8212; and OpenGL's viewport
    /// origin is the bottom left corner of the target. Vulkan's is the top left, so the row range
    /// <c>[y, y + height)</c> counted upwards becomes <c>[targetHeight - y - height, targetHeight - y)</c>
    /// counted downwards, and <c>VkViewport.y</c> is that range's far end because the height is negative.
    /// </para>
    /// <para>
    /// The negative height is <c>VK_KHR_maintenance1</c>, core since Vulkan 1.1, and it is the whole
    /// mechanism: pushing the flip into shaders instead would invert every screen-space derivative,
    /// break the post-process chain, and force the shader emission to produce two variants of every
    /// source file. Depth is untouched, the renderer being reverse-Z already.
    /// </para>
    /// <para>
    /// This is why it needs an open pass: the flip is relative to the target's height, and outside a
    /// pass there is no target to be relative to.
    /// </para>
    /// </remarks>
    public void SetViewport(int x, int y, int width, int height, float minDepth = 0f, float maxDepth = 1f)
    {
        EnsureRecording();
        EnsureInRenderPass();

        var viewport = FlipViewport(x, y, width, height, PassHeight, minDepth, maxDepth);

        Api.CmdSetViewport(Command, 0, 1, &viewport);
    }

    /// <summary>
    /// Converts a bottom-left-origin viewport rectangle into the top-left-origin, negative-height
    /// <c>VkViewport</c> that renders it the same way OpenGL would.
    /// </summary>
    /// <param name="x">Left edge in pixels.</param>
    /// <param name="y">Bottom edge in pixels, counted upwards, as <c>glViewport</c> takes it.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="targetHeight">Height of the attachment the viewport is relative to.</param>
    /// <param name="minDepth">Minimum depth.</param>
    /// <param name="maxDepth">Maximum depth.</param>
    /// <returns>The viewport to submit.</returns>
    /// <remarks>Separate and pure so the arithmetic can be asserted directly. It is load bearing for
    /// every pixel the backend produces and there is no way to read a submitted viewport back off a
    /// command buffer, so a test that cannot see this in isolation cannot see it at all.</remarks>
    public static Viewport FlipViewport(int x, int y, int width, int height, int targetHeight, float minDepth = 0f, float maxDepth = 1f) => new()
    {
        X = x,
        Y = targetHeight - y,
        Width = width,
        Height = -height,
        MinDepth = minDepth,
        MaxDepth = maxDepth,
    };

    /// <summary>Converts a bottom-left-origin scissor rectangle into a top-left-origin
    /// <c>VkRect2D</c>.</summary>
    /// <param name="x">Left edge in pixels.</param>
    /// <param name="y">Bottom edge in pixels, counted upwards.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="targetHeight">Height of the attachment the rectangle is relative to.</param>
    /// <returns>The scissor rectangle to submit.</returns>
    /// <remarks>A rectangle has no sign to carry the flip, so unlike <see cref="FlipViewport"/> the
    /// offset moves to the far edge: the same rows, addressed from the other end.</remarks>
    public static Rect2D FlipScissor(int x, int y, int width, int height, int targetHeight)
        => new(new Offset2D(x, targetHeight - y - height), new Extent2D((uint)width, (uint)height));

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">No render pass is open.</exception>
    /// <remarks>Flipped the same way <see cref="SetViewport"/> is, and for the same reason. There is no
    /// enable to match OpenGL's: a Vulkan scissor is always on, and a pass opens with one covering the
    /// whole attachment, which is what disabling the test means.</remarks>
    public void SetScissor(int x, int y, int width, int height)
    {
        EnsureRecording();
        EnsureInRenderPass();

        var scissor = FlipScissor(x, y, width, height, PassHeight);

        Api.CmdSetScissor(Command, 0, 1, &scissor);
    }

    // ---- binding ----

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="pipeline"/> does not implement
    /// <see cref="IVulkanPipeline"/> or is not a graphics pipeline.</exception>
    public void BindPipeline(IGraphicsPipeline pipeline)
    {
        EnsureRecording();
        ArgumentNullException.ThrowIfNull(pipeline);

        var vulkan = AsVulkanPipeline(pipeline, PipelineBindPoint.Graphics, nameof(pipeline));

        Api.CmdBindPipeline(Command, PipelineBindPoint.Graphics, vulkan.Handle);

        Pipeline = vulkan;
        PipelineIsGraphics = true;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="pipeline"/> does not implement
    /// <see cref="IVulkanPipeline"/> or is not a compute pipeline.</exception>
    public void BindPipeline(IComputePipeline pipeline)
    {
        EnsureRecording();
        ArgumentNullException.ThrowIfNull(pipeline);

        var vulkan = AsVulkanPipeline(pipeline, PipelineBindPoint.Compute, nameof(pipeline));

        Api.CmdBindPipeline(Command, PipelineBindPoint.Compute, vulkan.Handle);

        Pipeline = vulkan;
        PipelineIsGraphics = false;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The buffer was not created with
    /// <see cref="BufferUsage.Vertex"/>.</exception>
    public void BindVertexBuffer(int binding, IBuffer buffer, int offsetInBytes = 0)
    {
        EnsureRecording();
        ArgumentOutOfRangeException.ThrowIfNegative(binding);
        ArgumentOutOfRangeException.ThrowIfNegative(offsetInBytes);

        var vulkan = VulkanBarrierTranslation.AsVulkanBuffer(buffer);
        vulkan.RequireUsage(BufferUsage.Vertex, "Binding a vertex buffer");

        var handle = vulkan.Handle;
        var offset = (ulong)offsetInBytes;

        Api.CmdBindVertexBuffers(Command, (uint)binding, 1, &handle, &offset);

        if (binding < 32)
        {
            VertexBuffersBound |= 1u << binding;
        }
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The buffer was not created with
    /// <see cref="BufferUsage.Index"/>.</exception>
    /// <remarks>The <c>firstIndex</c> of a later draw counts elements from
    /// <paramref name="offsetInBytes"/>, matching what the OpenGL backend computes by hand from the
    /// bound offset and the element size.</remarks>
    public void BindIndexBuffer(IBuffer buffer, IndexType indexType, int offsetInBytes = 0)
    {
        EnsureRecording();
        ArgumentOutOfRangeException.ThrowIfNegative(offsetInBytes);

        var vulkan = VulkanBarrierTranslation.AsVulkanBuffer(buffer);
        vulkan.RequireUsage(BufferUsage.Index, "Binding an index buffer");

        Api.CmdBindIndexBuffer(
            Command,
            vulkan.Handle,
            (ulong)offsetInBytes,
            indexType == IndexType.UInt16 ? Silk.NET.Vulkan.IndexType.Uint16 : Silk.NET.Vulkan.IndexType.Uint32);

        IndexBufferBound = true;
    }

    /// <inheritdoc/>
    /// <remarks>Set 0 of the contract's table. Uniform and storage buffers are split across sets 0 and
    /// 1 precisely because <c>ReservedBufferSlots</c> overlaps their index spaces and Vulkan, unlike
    /// OpenGL, gives them one namespace.</remarks>
    public void BindUniformBuffer(int binding, IBuffer buffer, int offsetInBytes = 0, int sizeInBytes = -1)
    {
        EnsureRecording();

        var vulkan = VulkanBarrierTranslation.AsVulkanBuffer(buffer);
        vulkan.RequireUsage(BufferUsage.Uniform, "Binding a uniform buffer");

        var (offset, size) = Range(vulkan, offsetInBytes, sizeInBytes);

        RequireBinder(nameof(BindUniformBuffer)).BindUniformBuffer(binding, vulkan.Handle, offset, size);
    }

    /// <inheritdoc/>
    /// <remarks>Set 1 of the contract's table. See <see cref="BindUniformBuffer"/>.</remarks>
    public void BindStorageBuffer(int binding, IBuffer buffer, int offsetInBytes = 0, int sizeInBytes = -1)
    {
        EnsureRecording();

        var vulkan = VulkanBarrierTranslation.AsVulkanBuffer(buffer);
        vulkan.RequireUsage(BufferUsage.Storage, "Binding a storage buffer");

        var (offset, size) = Range(vulkan, offsetInBytes, sizeInBytes);

        RequireBinder(nameof(BindStorageBuffer)).BindStorageBuffer(binding, vulkan.Handle, offset, size);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="descriptorSet"/> is not a texture set.</exception>
    /// <exception cref="InvalidOperationException">The texture is not in a state a sampled descriptor
    /// may name.</exception>
    /// <remarks>
    /// <para>
    /// A null sampler means the device's real default, unlike OpenGL where sampler object 0 defers to
    /// the texture's own parameters. A <c>VkImage</c> carries no sampling state for it to defer to.
    /// </para>
    /// <para>
    /// <b>The layout comes from the image's tracking, and a texture that is not ready is refused.</b>
    /// <see cref="IDevice.UploadTexture"/> leaves a texture in <see cref="ResourceState.CopyDestination"/>
    /// on purpose &#8212; guessing <see cref="ResourceState.ShaderRead"/> there would be wrong for every
    /// storage image and every render target &#8212; so the transition before the first sample belongs
    /// to the caller. Binding one anyway would write a descriptor naming <c>TRANSFER_DST_OPTIMAL</c>,
    /// which the layer reports against the draw rather than the bind. Naming it here points at the
    /// missing barrier instead.
    /// </para>
    /// </remarks>
    public void BindTexture(int descriptorSet, int binding, ITexture texture, ISampler? sampler = null)
    {
        EnsureRecording();

        if (descriptorSet is not (DescriptorSets.ReservedTextures or DescriptorSets.MaterialTextures))
        {
            throw new ArgumentOutOfRangeException(nameof(descriptorSet), descriptorSet,
                $"Only sets {DescriptorSets.ReservedTextures} and {DescriptorSets.MaterialTextures} hold textures.");
        }

        var vulkan = VulkanBarrierTranslation.AsVulkanTexture(texture);

        if (IsAttachmentOfOpenPass(vulkan))
        {
            return;
        }

        vulkan.RequireUsage(TextureUsage.Sampled, "Binding a texture for sampling");

        var state = vulkan.StateOf(0, 0);

        if (state is not (ResourceState.ShaderRead or ResourceState.DepthRead or ResourceState.ShaderReadWrite))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Texture '{vulkan.Name}' is in {state} and cannot be sampled from there, at descriptor set {descriptorSet} binding {binding}. Transition it to {ResourceState.ShaderRead} with a barrier first; {nameof(IDevice.UploadTexture)} deliberately leaves textures in {ResourceState.CopyDestination} rather than guessing what they are for."));
        }

        var vulkanSampler = sampler switch
        {
            null => Owner.DefaultSampler,
            VulkanSampler existing => existing,
            _ => throw new ArgumentException($"Expected a {nameof(VulkanSampler)}, got {sampler.GetType().Name}.", nameof(sampler)),
        };

        RequireBinder(nameof(BindTexture)).BindSampledImage(
            descriptorSet,
            binding,
            vulkan.View,
            vulkanSampler.Handle,
            VulkanBarrierTranslation.LayoutOf(vulkan));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The allocation comes from <see cref="Core.VulkanUploadRing"/>, whose slot is reset when the frame
    /// retires, which is exactly the lifetime the contract promises. Nothing needs flushing: the ring's
    /// slots are host visible and persistently mapped, and the allocator gives them coherent memory.
    /// </remarks>
    public void BindTransientUniform<T>(int binding, in T data) where T : unmanaged
    {
        EnsureRecording();

        var binder = RequireBinder(nameof(BindTransientUniform));
        var allocation = Owner.Core.UploadRing.Write(in data);

        binder.BindUniformBuffer(binding, allocation.Buffer, allocation.Offset, allocation.Size);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The texture is not in a state a storage image
    /// descriptor may name.</exception>
    /// <remarks>
    /// <para>
    /// A storage image is only readable and writable in <see cref="ImageLayout.General"/>, which is what
    /// <see cref="ResourceState.ShaderWrite"/> and <see cref="ResourceState.ShaderReadWrite"/> expand to.
    /// There is no write-only image layout, so a texture in any other state is refused rather than bound
    /// in one the descriptor is not allowed to see.
    /// </para>
    /// <para>
    /// A layered texture is bound with a view spanning every layer, matching the OpenGL backend's
    /// <c>layered</c> argument to <c>glBindImageTexture</c>, so a shader indexes across faces or slices
    /// the same way on both.
    /// </para>
    /// </remarks>
    public void BindStorageTexture(int binding, ITexture texture, int mipLevel = 0)
    {
        EnsureRecording();

        var vulkan = VulkanBarrierTranslation.AsVulkanTexture(texture);
        vulkan.RequireUsage(TextureUsage.Storage, "Binding a texture as a storage image");

        var state = vulkan.StateOf(mipLevel, 0);

        if (state is not (ResourceState.ShaderWrite or ResourceState.ShaderReadWrite))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Texture '{vulkan.Name}' is in {state}, and a storage image descriptor may only name {ImageLayout.General}. Transition it to {ResourceState.ShaderReadWrite} with a barrier first."));
        }

        var view = Views.Subresource(vulkan, mipLevel, 0, vulkan.LayerCount);

        RequireBinder(nameof(BindStorageTexture)).BindStorageImage(binding, view.View, ImageLayout.General);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">No pipeline is bound, or the bound one declares no
    /// push constants.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The write falls outside the declared range.</exception>
    public void SetPushConstants<T>(in T data, int offsetInBytes = 0) where T : unmanaged
    {
        EnsureRecording();
        ArgumentOutOfRangeException.ThrowIfNegative(offsetInBytes);

        var pipeline = Pipeline
            ?? throw new InvalidOperationException("Push constants are written against the bound pipeline's layout, and no pipeline is bound.");

        var range = pipeline.PushConstants
            ?? throw new InvalidOperationException("The bound pipeline declares no push constant range. Writing outside a declared range is undefined, not a no-op.");

        var size = Unsafe.SizeOf<T>();

        if (offsetInBytes < range.OffsetInBytes || offsetInBytes + size > range.OffsetInBytes + range.SizeInBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(offsetInBytes), offsetInBytes, string.Create(CultureInfo.InvariantCulture,
                $"Writing {size} bytes at {offsetInBytes} falls outside the pipeline's declared push constant range of {range.SizeInBytes} bytes at {range.OffsetInBytes}."));
        }

        var value = data;

        Api.CmdPushConstants(
            Command,
            pipeline.Layout,
            VulkanShaderModule.ToVkStages(range.Stages),
            (uint)offsetInBytes,
            (uint)size,
            &value);
    }

    // ---- draws ----

    /// <inheritdoc/>
    /// <remarks><paramref name="firstInstance"/> reaches the shader as <c>gl_BaseInstance</c>, which is
    /// how the renderer passes the scene node id into a draw.</remarks>
    public void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0)
    {
        BeginDraw();

        Api.CmdDraw(Command, (uint)vertexCount, (uint)instanceCount, (uint)firstVertex, (uint)firstInstance);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">No index buffer is bound.</exception>
    /// <remarks>See <see cref="Draw"/> on <paramref name="firstInstance"/>.</remarks>
    public void DrawIndexed(int indexCount, int instanceCount = 1, int firstIndex = 0, int baseVertex = 0, int firstInstance = 0)
    {
        BeginDraw();
        EnsureIndexBuffer();

        Api.CmdDrawIndexed(Command, (uint)indexCount, (uint)instanceCount, (uint)firstIndex, baseVertex, (uint)firstInstance);
    }

    /// <inheritdoc/>
    /// <remarks>A stride of 0 becomes the tightly packed size of <c>VkDrawIndirectCommand</c>. OpenGL
    /// reads 0 as "packed"; Vulkan rejects it for a multi-draw, so the sentinel is expanded here rather
    /// than at the call sites.</remarks>
    public void DrawIndirect(IBuffer argumentBuffer, int offsetInBytes, int drawCount, int strideInBytes = 0)
    {
        BeginDraw();

        var buffer = IndirectArguments(argumentBuffer, offsetInBytes);
        var stride = strideInBytes == 0 ? DrawIndirectStride : strideInBytes;

        Api.CmdDrawIndirect(Command, buffer.Handle, (ulong)offsetInBytes, (uint)drawCount, (uint)stride);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">No index buffer is bound.</exception>
    /// <remarks>A stride of 0 becomes the tightly packed size of <c>VkDrawIndexedIndirectCommand</c>,
    /// which is 20 bytes rather than the non-indexed form's 16.</remarks>
    public void DrawIndexedIndirect(IBuffer argumentBuffer, int offsetInBytes, int drawCount, int strideInBytes = 0)
    {
        BeginDraw();
        EnsureIndexBuffer();

        var buffer = IndirectArguments(argumentBuffer, offsetInBytes);
        var stride = strideInBytes == 0 ? DrawIndexedIndirectStride : strideInBytes;

        Api.CmdDrawIndexedIndirect(Command, buffer.Handle, (ulong)offsetInBytes, (uint)drawCount, (uint)stride);
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">The device cannot read a draw count from a buffer.</exception>
    /// <exception cref="InvalidOperationException">No index buffer is bound.</exception>
    /// <remarks>Backs the renderer's <c>MultiDrawElementsIndirectCount</c> path. The count buffer must
    /// carry <see cref="BufferUsage.Indirect"/> as well as the argument buffer: the count is an indirect
    /// read too, and it is the flag call sites forget.</remarks>
    public void DrawIndexedIndirectCount(
        IBuffer argumentBuffer,
        int argumentOffsetInBytes,
        IBuffer countBuffer,
        int countOffsetInBytes,
        int maxDrawCount,
        int strideInBytes = 0)
    {
        BeginDraw();
        EnsureIndexBuffer();

        if (!Owner.Limits.SupportsDrawIndirectCount)
        {
            throw new NotSupportedException($"This device cannot take an indirect draw count from a buffer. Guard the call site with {nameof(IDeviceLimits)}.{nameof(IDeviceLimits.SupportsDrawIndirectCount)}.");
        }

        var arguments = IndirectArguments(argumentBuffer, argumentOffsetInBytes);
        var counts = IndirectArguments(countBuffer, countOffsetInBytes);
        var stride = strideInBytes == 0 ? DrawIndexedIndirectStride : strideInBytes;

        Api.CmdDrawIndexedIndirectCount(
            Command,
            arguments.Handle,
            (ulong)argumentOffsetInBytes,
            counts.Handle,
            (ulong)countOffsetInBytes,
            (uint)maxDrawCount,
            (uint)stride);
    }

    // ---- compute ----

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">A render pass is open, or no compute pipeline is bound.</exception>
    public void Dispatch(int groupCountX, int groupCountY = 1, int groupCountZ = 1)
    {
        BeginDispatch();

        Api.CmdDispatch(Command, (uint)groupCountX, (uint)groupCountY, (uint)groupCountZ);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">A render pass is open, or no compute pipeline is bound.</exception>
    public void DispatchIndirect(IBuffer argumentBuffer, int offsetInBytes)
    {
        BeginDispatch();

        var buffer = IndirectArguments(argumentBuffer, offsetInBytes);

        Api.CmdDispatchIndirect(Command, buffer.Handle, (ulong)offsetInBytes);
    }

    // ---- synchronisation ----

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">A render pass is open.</exception>
    /// <remarks>See <see cref="VulkanBarrierTranslation"/> for what each transition becomes and why the
    /// batch is not merged into a single <c>vkCmdPipelineBarrier2</c>.</remarks>
    public void Barrier(ReadOnlySpan<BufferBarrier> bufferBarriers, ReadOnlySpan<TextureBarrier> textureBarriers)
    {
        EnsureRecording();
        EnsureBarrierIsLegalHere();

        VulkanBarrierTranslation.Record(Command, bufferBarriers, textureBarriers);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">A render pass is open.</exception>
    public void Barrier(in TextureBarrier barrier)
    {
        EnsureRecording();
        EnsureBarrierIsLegalHere();

        VulkanBarrierTranslation.Record(Command, in barrier);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">A render pass is open.</exception>
    public void Barrier(in BufferBarrier barrier)
    {
        EnsureRecording();
        EnsureBarrierIsLegalHere();

        VulkanBarrierTranslation.Record(Command, in barrier);
    }

    // ---- transfers ----

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">A render pass is open, or a buffer lacks the copy
    /// usage it is being put to.</exception>
    /// <remarks>The states either buffer is in are the caller's business: a buffer has no layout, so
    /// there is nothing this call could infer. Barrier the source out of whatever wrote it and the
    /// destination out of whatever read it.</remarks>
    public void CopyBuffer(IBuffer source, int sourceOffsetInBytes, IBuffer destination, int destinationOffsetInBytes, int sizeInBytes)
    {
        EnsureRecording();
        EnsureOutsideRenderPass(nameof(CopyBuffer));
        ArgumentOutOfRangeException.ThrowIfNegative(sizeInBytes);

        var from = VulkanBarrierTranslation.AsVulkanBuffer(source);
        var to = VulkanBarrierTranslation.AsVulkanBuffer(destination);

        from.RequireUsage(BufferUsage.CopySource, "Copying out of a buffer");
        to.RequireUsage(BufferUsage.CopyDestination, "Copying into a buffer");

        var copy = new BufferCopy
        {
            SrcOffset = (ulong)sourceOffsetInBytes,
            DstOffset = (ulong)destinationOffsetInBytes,
            Size = (ulong)sizeInBytes,
        };

        Api.CmdCopyBuffer(Command, from.Handle, to.Handle, 1, &copy);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">A render pass is open, or a texture lacks the copy
    /// usage it is being put to.</exception>
    /// <remarks>
    /// <b>Both images are transitioned for the copy.</b> A transfer has layout requirements the caller
    /// cannot express through this signature &#8212; there is no state parameter &#8212; so the source
    /// is moved to <see cref="ResourceState.CopySource"/> and the destination to
    /// <see cref="ResourceState.CopyDestination"/>, and both are left there. That is two barriers where
    /// the contract asks for one batch, which is the price of routing every image barrier through the
    /// tracking that owns <c>oldLayout</c>; a copy is not a hot path. Transition them onwards
    /// afterwards, the destination especially.
    /// </remarks>
    public void CopyTexture(ITexture source, ITexture destination, int mipLevel = 0)
    {
        EnsureRecording();
        EnsureOutsideRenderPass(nameof(CopyTexture));

        var from = VulkanBarrierTranslation.AsVulkanTexture(source);
        var to = VulkanBarrierTranslation.AsVulkanTexture(destination);

        from.RequireUsage(TextureUsage.CopySource, "Copying out of a texture");
        to.RequireUsage(TextureUsage.CopyDestination, "Copying into a texture");

        from.TransitionTo(Command, ResourceState.CopySource);
        to.TransitionTo(Command, ResourceState.CopyDestination);

        var (width, height, depth) = from.MipExtent(mipLevel);
        var layers = from.Dimension == TextureDimension.Texture3D ? 1 : from.LayerCount;

        var region = new ImageCopy
        {
            SrcSubresource = Layers(from, mipLevel, 0, layers),
            DstSubresource = Layers(to, mipLevel, 0, layers),
            Extent = new Extent3D((uint)width, (uint)height, (uint)depth),
        };

        Api.CmdCopyImage(
            Command,
            from.Handle,
            ImageLayout.TransferSrcOptimal,
            to.Handle,
            ImageLayout.TransferDstOptimal,
            1,
            &region);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">A render pass is open, or a resource lacks the copy
    /// usage it is being put to.</exception>
    /// <remarks>The source is transitioned to <see cref="ResourceState.CopySource"/>, which the contract
    /// says it should already be in, so the call costs nothing when the caller kept its word and works
    /// when it did not. The destination buffer is not barriered: nothing here knows what wrote it, and
    /// the read that follows is a host read after <see cref="IDevice.WaitIdle"/>.</remarks>
    public void CopyTextureToBuffer(ITexture source, int mipLevel, int arrayLayer, IBuffer destination, int destinationOffsetInBytes = 0)
    {
        EnsureRecording();
        EnsureOutsideRenderPass(nameof(CopyTextureToBuffer));

        var from = VulkanBarrierTranslation.AsVulkanTexture(source);
        var to = VulkanBarrierTranslation.AsVulkanBuffer(destination);

        from.RequireUsage(TextureUsage.CopySource, "Reading a texture back into a buffer");
        to.RequireUsage(BufferUsage.CopyDestination, "Receiving a texture readback");

        from.TransitionTo(Command, ResourceState.CopySource);

        var (width, height, depth) = from.MipExtent(mipLevel);

        var copy = new BufferImageCopy
        {
            BufferOffset = (ulong)destinationOffsetInBytes,

            // Zero is tightly packed to the image extent, which is what every readback consumer expects
            // and what MipSizeInBytes measures.
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = Layers(from, mipLevel, from.Dimension == TextureDimension.Texture3D ? 0 : arrayLayer, 1),
            ImageOffset = default,
            ImageExtent = new Extent3D((uint)width, (uint)height, (uint)depth),
        };

        Api.CmdCopyImageToBuffer(Command, from.Handle, ImageLayout.TransferSrcOptimal, to.Handle, 1, &copy);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">A render pass is open, <paramref name="source"/> is
    /// multisampled, or a texture lacks the copy usage it is being put to.</exception>
    /// <remarks>
    /// <b>Rejects a multisampled source, exactly as the OpenGL backend does.</b> There it is a
    /// deliberate refusal of something the API would have done: <c>glBlitFramebuffer</c> resolves
    /// implicitly, so a resolve written as a blit passes on the parity oracle. Here it is what the API
    /// does anyway, since <c>vkCmdBlitImage</c> rejects a multisampled source outright. Resolve through
    /// <see cref="ColorAttachmentDesc.ResolveTexture"/>. Both images are transitioned for the transfer,
    /// as in <see cref="CopyTexture"/>.
    /// </remarks>
    public void BlitTexture(ITexture source, ITexture destination, FilterMode filter = FilterMode.Linear)
    {
        EnsureRecording();
        EnsureOutsideRenderPass(nameof(BlitTexture));

        var from = VulkanBarrierTranslation.AsVulkanTexture(source);
        var to = VulkanBarrierTranslation.AsVulkanTexture(destination);

        if (from.SampleCount > 1)
        {
            throw new InvalidOperationException(
                $"Texture '{from.Name}' is multisampled and cannot be blitted. Resolve it with {nameof(ColorAttachmentDesc)}.{nameof(ColorAttachmentDesc.ResolveTexture)} instead: a blit would resolve it on OpenGL and fail here.");
        }

        from.RequireUsage(TextureUsage.CopySource, "Blitting out of a texture");
        to.RequireUsage(TextureUsage.CopyDestination, "Blitting into a texture");

        from.TransitionTo(Command, ResourceState.CopySource);
        to.TransitionTo(Command, ResourceState.CopyDestination);

        var isDepth = RhiFormatInfo.IsDepth(from.Format);

        var region = new ImageBlit
        {
            SrcSubresource = Layers(from, 0, 0, 1),
            DstSubresource = Layers(to, 0, 0, 1),
        };

        region.SrcOffsets[1] = new Offset3D(from.Width, from.Height, 1);
        region.DstOffsets[1] = new Offset3D(to.Width, to.Height, 1);

        // A depth or stencil blit must be unfiltered; Vulkan rejects anything else, as OpenGL does.
        var vkFilter = isDepth || filter == FilterMode.Nearest ? Filter.Nearest : Filter.Linear;

        Api.CmdBlitImage(
            Command,
            from.Handle,
            ImageLayout.TransferSrcOptimal,
            to.Handle,
            ImageLayout.TransferDstOptimal,
            1,
            &region,
            vkFilter);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">A render pass is open, or the buffer was not created
    /// with <see cref="BufferUsage.CopyDestination"/>.</exception>
    /// <remarks>Backs the cull passes resetting their counters. The offset and size must be multiples of
    /// four, which <c>vkCmdFillBuffer</c> requires and a 32 bit fill implies anyway.</remarks>
    public void FillBuffer(IBuffer buffer, int offsetInBytes, int sizeInBytes, uint value)
    {
        EnsureRecording();
        EnsureOutsideRenderPass(nameof(FillBuffer));
        ArgumentOutOfRangeException.ThrowIfNegative(offsetInBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeInBytes);

        var vulkan = VulkanBarrierTranslation.AsVulkanBuffer(buffer);
        vulkan.RequireUsage(BufferUsage.CopyDestination, "Filling a buffer");

        Api.CmdFillBuffer(Command, vulkan.Handle, (ulong)offsetInBytes, (ulong)sizeInBytes, value);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">A render pass is open, or the texture was not created
    /// with <see cref="TextureUsage.CopyDestination"/>.</exception>
    /// <remarks>
    /// A clear is a transfer operation in Vulkan, so the texture is transitioned to
    /// <see cref="ResourceState.CopyDestination"/> and left there; a storage image being cleared for the
    /// next dispatch needs a barrier back to <see cref="ResourceState.ShaderReadWrite"/> afterwards, and
    /// <see cref="BindStorageTexture"/> refuses it until that happens.
    /// </remarks>
    public void ClearTexture(ITexture texture, int mipLevel, uint value)
    {
        EnsureRecording();
        EnsureOutsideRenderPass(nameof(ClearTexture));

        var vulkan = VulkanBarrierTranslation.AsVulkanTexture(texture);
        vulkan.RequireUsage(TextureUsage.CopyDestination, "Clearing a texture");

        vulkan.TransitionTo(Command, ResourceState.CopyDestination);

        var range = new ImageSubresourceRange
        {
            AspectMask = vulkan.Aspect,
            BaseMipLevel = (uint)mipLevel,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = (uint)vulkan.LayerCount,
        };

        if (RhiFormatInfo.IsDepth(vulkan.Format))
        {
            // The same reinterpretation the colour path makes: the raw bits are the depth value, which is
            // how glClearTexImage reads them through the format's client type.
            var depth = new ClearDepthStencilValue(BitConverter.UInt32BitsToSingle(value), value);

            Api.CmdClearDepthStencilImage(Command, vulkan.Handle, ImageLayout.TransferDstOptimal, &depth, 1, &range);
            return;
        }

        var color = VulkanAttachments.RawClear(vulkan.Format, value);

        Api.CmdClearColorImage(Command, vulkan.Handle, ImageLayout.TransferDstOptimal, &color, 1, &range);
    }

    // ---- debugging ----

    /// <inheritdoc/>
    public IDisposable DebugScope(string name)
    {
        EnsureRecording();
        ArgumentNullException.ThrowIfNull(name);

        return Owner.Core.DebugNames.Scope(Command, name);
    }

    /// <inheritdoc/>
    public void DebugMarker(string name)
    {
        EnsureRecording();
        ArgumentNullException.ThrowIfNull(name);

        Owner.Core.DebugNames.Marker(Command, name);
    }

    // ---- helpers ----

    private static ImageSubresourceLayers Layers(VulkanTexture texture, int mipLevel, int baseArrayLayer, int layerCount) => new()
    {
        AspectMask = texture.Aspect,
        MipLevel = (uint)mipLevel,
        BaseArrayLayer = (uint)baseArrayLayer,
        LayerCount = (uint)layerCount,
    };

    private static (ulong Offset, ulong Size) Range(VulkanBuffer buffer, int offsetInBytes, int sizeInBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offsetInBytes);

        var size = sizeInBytes < 0 ? buffer.SizeInBytes - offsetInBytes : sizeInBytes;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size, nameof(sizeInBytes));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offsetInBytes + size, buffer.SizeInBytes, nameof(sizeInBytes));

        return ((ulong)offsetInBytes, (ulong)size);
    }

    private static VulkanBuffer IndirectArguments(IBuffer buffer, int offsetInBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offsetInBytes);

        var vulkan = VulkanBarrierTranslation.AsVulkanBuffer(buffer);
        vulkan.RequireUsage(BufferUsage.Indirect, "Reading indirect arguments");

        return vulkan;
    }

    private IVulkanDescriptorBinder RequireBinder(string operation)
        => Binder ?? throw new NotSupportedException(
            $"Command list '{ListName}' was created without an {nameof(IVulkanDescriptorBinder)}, so {operation} has nowhere to write a descriptor. Construct it with the descriptor layer's binder.");

    private static IVulkanPipeline AsVulkanPipeline(IRhiResource pipeline, PipelineBindPoint expected, string parameterName)
    {
        if (pipeline is not IVulkanPipeline vulkan)
        {
            throw new ArgumentException(
                $"Expected a pipeline implementing {nameof(IVulkanPipeline)}, got {pipeline.GetType().Name}.", parameterName);
        }

        if (vulkan.BindPoint != expected)
        {
            throw new ArgumentException(
                $"Pipeline '{pipeline.Name}' binds to {vulkan.BindPoint}, not {expected}.", parameterName);
        }

        return vulkan;
    }

    private void BeginDraw()
    {
        EnsureRecording();
        EnsureInRenderPass();

        if (Pipeline is null || !PipelineIsGraphics)
        {
            throw new InvalidOperationException("A draw needs a graphics pipeline bound, which supplies its topology and vertex input.");
        }

        Binder?.Flush(Command, PipelineBindPoint.Graphics, Pipeline.Layout);

        EnsureCompletelyBound();
        EnsureVertexBuffers();
    }

    private void BeginDispatch()
    {
        EnsureRecording();
        EnsureOutsideRenderPass("Dispatch");

        if (Pipeline is null || PipelineIsGraphics)
        {
            throw new InvalidOperationException("A dispatch needs a compute pipeline bound.");
        }

        Binder?.Flush(Command, PipelineBindPoint.Compute, Pipeline.Layout);

        EnsureCompletelyBound();
    }

    /// <summary>
    /// Refuses to record a draw or dispatch whose pipeline uses a descriptor set nothing has bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Here rather than at <see cref="BindPipeline(IGraphicsPipeline)"/>, because a per-bind check
    /// would be wrong and not merely cheaper.</b> The contract's binding calls are immediate-mode and
    /// legitimately follow the pipeline they belong to &#8212; bind pipeline, bind this material's
    /// textures, draw &#8212; so a pipeline whose sets are unbound at bind time is the normal case, not a
    /// fault. Only at the draw is the answer knowable.
    /// </para>
    /// <para>
    /// <b>And it costs nothing to be here.</b> Both masks are precomputed integers: the pipeline's is
    /// built once at creation from reflection, the binder's is accumulated by the flush that just ran.
    /// The check is an <c>and</c> against a constant-time value, which is not what a 36-scene suite would
    /// notice.
    /// </para>
    /// <para>
    /// A list with no binder reports nothing bound, which is the truth: its binding calls refuse, so a
    /// pipeline that uses any set can never have been satisfied on it.
    /// </para>
    /// </remarks>
    private void EnsureCompletelyBound()
        => VulkanDescriptorSetUsage.EnsureBound(
            PipelineName,
            Pipeline!.UsedDescriptorSets,
            Binder?.BoundDescriptorSets ?? VulkanDescriptorSetUsage.NoSets);

    /// <summary>
    /// Refuses to record a draw whose pipeline fetches from a vertex binding nothing filled.
    /// </summary>
    /// <remarks>The neighbouring hazard, taken at the same seam because it is the same shape: fetching
    /// from a binding with no buffer is undefined the way an unbound descriptor set is, and it costs one
    /// more integer compare to catch. See <see cref="IVulkanPipeline.UsedVertexBindings"/> for why only
    /// bindings an attribute reads are counted.</remarks>
    private void EnsureVertexBuffers()
    {
        var missing = Pipeline!.UsedVertexBindings & ~VertexBuffersBound;

        if (missing == 0)
        {
            return;
        }

        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
            $"Pipeline '{PipelineName}' fetches vertex attributes from binding {BitOperations.TrailingZeroCount(missing)}, which no {nameof(BindVertexBuffer)} filled on this command list. Drawing would fetch from a null buffer, which is undefined behaviour that reaches the GPU."));
    }

    /// <summary>The bound pipeline's debug name, or a placeholder when it carries none.</summary>
    private string PipelineName => Pipeline switch
    {
        IRhiResource named when !string.IsNullOrEmpty(named.Name) => named.Name,
        _ => "<unnamed>",
    };

    private void EnsureIndexBuffer()
    {
        if (!IndexBufferBound)
        {
            throw new InvalidOperationException($"An indexed draw needs an index buffer bound through {nameof(BindIndexBuffer)}.");
        }
    }

    private void EnsureRecording()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        if (!Recording)
        {
            throw new InvalidOperationException($"Command list '{ListName}' is not recording. Obtain it from {nameof(IDevice)}.{nameof(IDevice.BeginCommandList)}, which binds it to this frame's command buffer.");
        }
    }

    private void EnsureInRenderPass()
    {
        if (!InRenderPass)
        {
            throw new InvalidOperationException($"This command is only valid inside a render pass. Open one with {nameof(BeginRenderPass)}.");
        }
    }

    private void EnsureOutsideRenderPass(string operation)
    {
        if (InRenderPass)
        {
            throw new InvalidOperationException($"{operation} is not valid inside a render pass. Close it with {nameof(EndRenderPass)} first.");
        }
    }

    /// <summary>
    /// Refuses a barrier recorded inside a render pass, which dynamic rendering does not permit.
    /// </summary>
    /// <remarks>
    /// Not a conservative restriction: a <c>vkCmdPipelineBarrier2</c> inside a rendering instance may
    /// only express a self-dependency, which means no buffer barriers at all and image barriers whose
    /// old and new layouts are equal and whose image is an attachment of the running pass. Every
    /// transition this backend records is a real layout change, so none of them is legal there. The
    /// OpenGL backend accepts one happily, which is precisely why this needs to be loud: a call site
    /// that barriers mid-pass works on the parity oracle and would be undefined here.
    /// </remarks>
    private void EnsureBarrierIsLegalHere()
    {
        if (InRenderPass)
        {
            throw new InvalidOperationException(
                $"A barrier cannot be recorded inside a render pass: dynamic rendering permits only self-dependencies there, which no state transition is. Split the pass around it with {nameof(EndRenderPass)} and {nameof(BeginRenderPass)}.");
        }
    }

    /// <summary>Destroys the image views this command list cached.</summary>
    /// <remarks>The command buffers belong to the frame ring's pools, which recycle them, so there is
    /// nothing else to release.</remarks>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;
        Recording = false;

        Views.Dispose();
    }
}
