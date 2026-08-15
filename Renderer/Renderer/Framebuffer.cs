using System.Diagnostics;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using ValveResourceFormat.Renderer.Buffers;
using ValveResourceFormat.Renderer.RHI;

namespace ValveResourceFormat.Renderer;

/// <summary>
/// OpenGL framebuffer object with color and depth attachments.
/// </summary>
public class Framebuffer
{
    /// <summary>
    /// OpenGL framebuffer object handle.
    /// </summary>
    public int FboHandle { get; }

    /// <summary>
    /// Width of the framebuffer in pixels.
    /// </summary>
    public int Width { get; protected set; }

    /// <summary>
    /// Height of the framebuffer in pixels.
    /// </summary>
    public int Height { get; protected set; }

    /// <summary>
    /// Returns <see langword="true"/> if both <see cref="Width"/> and <see cref="Height"/> are greater than zero.
    /// </summary>
    public bool HasValidDimensions() => Width > 0 && Height > 0;

    /// <summary>
    /// Number of mip levels for color attachments.
    /// </summary>
    public int NumMips { get; set; } = 1;

    /// <summary>
    /// Number of MSAA samples; 0 means no multisampling.
    /// </summary>
    public int NumSamples { get; set; }

    /// <summary>
    /// Texture target used for attachments (<see cref="TextureTarget.Texture2D"/> or <see cref="TextureTarget.Texture2DMultisample"/>).
    /// </summary>
    public TextureTarget Target { get; protected set; }

    /// <summary>
    /// Color attachment texture, or <see langword="null"/> if none.
    /// </summary>
    public RenderTexture? Color { get; protected set; }

    /// <summary>
    /// Depth attachment texture, or <see langword="null"/> if none.
    /// </summary>
    public RenderTexture? Depth { get; protected set; }

    /// <summary>
    /// Stencil view texture, or <see langword="null"/> if none.
    /// </summary>
    public RenderTexture? Stencil { get; protected set; }

    // Maybe these can be in texture
    /// <summary>
    /// Pixel format specification for the color attachment.
    /// </summary>
    public AttachmentFormat? ColorFormat { get; protected set; }

    /// <summary>
    /// Pixel format specification for the depth attachment.
    /// </summary>
    public DepthAttachmentFormat? DepthFormat { get; protected set; }

    /// <summary>
    /// Framebuffer completeness status set after <see cref="Initialize"/> is called.
    /// </summary>
    public FramebufferErrorCode InitialStatus { get; private set; } = FramebufferErrorCode.FramebufferUndefined;

    /// <summary>
    /// The framebuffer target this object was last bound to.
    /// </summary>
    public FramebufferTarget TargetState { get; set; } = FramebufferTarget.Framebuffer;

    /// <summary>
    /// Binds this framebuffer to the specified target.
    /// </summary>
    public void Bind(FramebufferTarget targetState)
    {
        TargetState = targetState;

        if (!RendererDevice.IsOpenGL())
        {
            // There is no framebuffer object to make current. The equivalent is beginning a render pass
            // over this framebuffer's attachments, which <see cref="RenderPass"/> describes.
            return;
        }

        GL.BindFramebuffer(targetState, FboHandle);
    }

    #region Render state
    /// <summary>
    /// Color used to clear the color attachment.
    /// </summary>
    public Color4 ClearColor { get; set; } = Color4.Black; // https://gpuopen.com/learn/rdna-performance-guide/#clears

    /// <summary>
    /// Buffer bits cleared when <see cref="BindAndClear"/> is called.
    /// </summary>
    public ClearBufferMask ClearMask { get; set; } = ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit;
    #endregion

    /// <summary>
    /// Binds this framebuffer and clears it with <see cref="ClearColor"/> and <see cref="ClearMask"/>.
    /// </summary>
    public void BindAndClear(FramebufferTarget targetState = FramebufferTarget.Framebuffer)
    {
        Bind(targetState);
        GL.ClearColor(ClearColor);
        GL.Clear(ClearMask);
    }

    /// <summary>
    /// Describes this framebuffer as a render pass, so it can be handed to
    /// <see cref="ICommandList.BeginRenderPass"/>.
    /// </summary>
    /// <param name="name">Debug label for the pass, or <see langword="null"/> to reuse the framebuffer's.</param>
    /// <param name="colorMipLevel">Mip level of the colour attachment to render into. The RHI equivalent
    /// of <see cref="AttachColorMipLevel"/>, which the bloom chain uses.</param>
    /// <param name="resolveColorTo">A single-sampled texture to resolve colour into when the pass ends,
    /// or <see langword="null"/> for no resolve. The only correct way to resolve MSAA: a blit resolves
    /// implicitly on OpenGL and fails on Vulkan.</param>
    /// <returns>The pass descriptor.</returns>
    /// <remarks>
    /// <para>
    /// The load operations come from <see cref="ClearMask"/> and the clear values from
    /// <see cref="ClearColor"/>, so a pass begun from this descriptor clears exactly what
    /// <see cref="BindAndClear"/> would. Every attachment stores: nothing in the renderer discards its
    /// results today, and <see cref="StoreOp.DontCare"/> here would change what later passes read.
    /// </para>
    /// <para>
    /// Prefer this over <see cref="BindAndClear"/> in ported code. Clears obey the colour and stencil
    /// write masks, and <see cref="BindAndClear"/> leaves that to its caller &#8212; only
    /// <see cref="Renderer"/>'s frame entry actually opens a scope for it. The render pass path forces
    /// the masks open around the clear itself, so it cannot inherit a restrictive mask from whatever
    /// drew last.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The framebuffer has no attachments to describe.</exception>
    public RenderPassDesc RenderPass(string? name = null, int colorMipLevel = 0, ITexture? resolveColorTo = null)
    {
        if (Color == null && Depth == null)
        {
            throw new InvalidOperationException("Framebuffer has no attachments to describe as a render pass.");
        }

        var clearsColor = (ClearMask & ClearBufferMask.ColorBufferBit) != 0;
        var clearsDepth = (ClearMask & ClearBufferMask.DepthBufferBit) != 0;

        var colors = Color == null
            ? []
            : new[]
            {
                new ColorAttachmentDesc(
                    Color.RhiTexture,
                    clearsColor ? LoadOp.Clear : LoadOp.Load,
                    StoreOp.Store,
                    // Qualified: this file also has OpenTK's Vector4 in scope.
                    new System.Numerics.Vector4(ClearColor.R, ClearColor.G, ClearColor.B, ClearColor.A),
                    colorMipLevel,
                    ArrayLayer: 0,
                    resolveColorTo),
            };

        DepthAttachmentDesc? depth = null;

        if (Depth != null)
        {
            // Only a format that has a stencil aspect may be told to clear one; asking for a stencil
            // clear on a depth-only attachment is an OpenGL error rather than a no-op.
            var hasStencil = RhiFormatInfo.IsStencil(Depth.RhiFormat);
            var clearsStencil = hasStencil && (ClearMask & ClearBufferMask.StencilBufferBit) != 0;

            // The depth clear value is 0: the renderer is reverse-Z, so 0 is the far plane, and that is
            // also what GLEnvironment sets glClearDepth to for the path this replaces.
            depth = new DepthAttachmentDesc(
                Depth.RhiTexture,
                clearsDepth ? LoadOp.Clear : LoadOp.Load,
                StoreOp.Store,
                ClearDepth: 0f,
                hasStencil ? (clearsStencil ? LoadOp.Clear : LoadOp.Load) : LoadOp.DontCare,
                hasStencil ? StoreOp.Store : StoreOp.DontCare,
                ClearStencil: 0);
        }

        return new RenderPassDesc(colors, depth, name ?? DebugName);
    }

    /// <summary>Gets the debug label this framebuffer was created with.</summary>
    public string DebugName { get; } = string.Empty;

    /// <summary>
    /// Creates a new named OpenGL framebuffer object.
    /// </summary>
    /// <param name="name">Debug label applied to the framebuffer object.</param>
    public Framebuffer(string name)
    {
        DebugName = name;

        if (!RendererDevice.IsOpenGL())
        {
            // Nothing to create. Under dynamic rendering a pass names its attachments directly, so this
            // object is a description of them rather than a handle; see <see cref="RenderPass"/>.
            return;
        }

        GL.CreateFramebuffers(1, out int handle);
        GL.ObjectLabel(ObjectLabelIdentifier.Framebuffer, handle, name.Length, name);
        FboHandle = handle;
    }

    #region Default OpenGL Framebuffer instance, and equality checks
    Framebuffer(int fboHandle)
    {
        FboHandle = fboHandle;
        InitialStatus = FramebufferErrorCode.FramebufferComplete;
    }
    /// <summary>
    /// Creates a <see cref="Framebuffer"/> instance wrapping the default OpenGL framebuffer (handle 0).
    /// </summary>
    public static Framebuffer GLDefaultFramebuffer => new(fboHandle: 0);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Framebuffer other && other.FboHandle == FboHandle;

    /// <inheritdoc/>
    public override int GetHashCode() => FboHandle.GetHashCode();

    /// <summary>
    /// Returns <see langword="true"/> if both framebuffers wrap the same OpenGL handle.
    /// </summary>
    public static bool operator ==(Framebuffer? left, Framebuffer? right)
    {
        if (left is null)
        {
            return right is null;
        }

        return left.Equals(right);
    }

    /// <summary>
    /// Returns <see langword="true"/> if the framebuffers wrap different OpenGL handles.
    /// </summary>
    public static bool operator !=(Framebuffer? left, Framebuffer? right) => !(left == right);

    #endregion

    /// <summary>
    /// Color attachment pixel format and type specification.
    /// </summary>
    public record class AttachmentFormat(PixelInternalFormat InternalFormat, PixelFormat PixelFormat, PixelType PixelType);

    /// <summary>
    /// Depth attachment pixel format and type specification.
    /// </summary>
    public record class DepthAttachmentFormat(PixelInternalFormat InternalFormat, PixelType PixelType)
    {
        /// <summary>
        /// 16-bit unsigned integer depth format.
        /// </summary>
        public static readonly DepthAttachmentFormat Depth16 = new(PixelInternalFormat.DepthComponent16, PixelType.UnsignedShort);

        /// <summary>
        /// 32-bit floating-point depth format.
        /// </summary>
        public static readonly DepthAttachmentFormat Depth32F = new(PixelInternalFormat.DepthComponent32f, PixelType.Float);

        /// <summary>
        /// 32-bit floating-point depth with 8-bit stencil format.
        /// </summary>
        public static readonly DepthAttachmentFormat Depth32FStencil8 = new(PixelInternalFormat.Depth32fStencil8, PixelType.Float32UnsignedInt248Rev);

        /// <summary>
        /// Implicitly converts this depth format to a generic <see cref="AttachmentFormat"/>.
        /// </summary>
        public static implicit operator AttachmentFormat(DepthAttachmentFormat depthFormat) => depthFormat.ToAttachmentFormat();

        /// <summary>
        /// Converts this depth format to a generic <see cref="AttachmentFormat"/>.
        /// </summary>
        public AttachmentFormat ToAttachmentFormat()
        {
            return new(InternalFormat, PixelFormat.DepthComponent, PixelType);
        }
    }

    /// <summary>
    /// Creates and configures a framebuffer without allocating GPU attachments; call <see cref="Initialize"/> to allocate.
    /// </summary>
    /// <param name="name">Debug label for the framebuffer.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="msaa">Number of MSAA samples; 0 disables multisampling.</param>
    /// <param name="colorFormat">Color attachment format, or <see langword="null"/> for depth-only.</param>
    /// <param name="depthFormat">Depth attachment format, or <see langword="null"/> for color-only.</param>
    public static Framebuffer Prepare(string name, int width, int height, int msaa, AttachmentFormat? colorFormat, DepthAttachmentFormat? depthFormat)
    {
        var fbo = new Framebuffer(name)
        {
            NumSamples = msaa,
            Target = msaa > 0 ? TextureTarget.Texture2DMultisample : TextureTarget.Texture2D,
            ColorFormat = colorFormat,
            DepthFormat = depthFormat,
            Width = width,
            Height = height,
        };

        return fbo;
    }

    /// <summary>
    /// Allocates GPU textures for all attachments and checks framebuffer completeness.
    /// </summary>
    /// <returns>The OpenGL framebuffer completeness status code.</returns>
    public FramebufferErrorCode Initialize()
    {
        if (Target == 0)
        {
            throw new InvalidOperationException("Framebuffer target is not set");
        }

        if (ColorFormat == null && DepthFormat == null)
        {
            throw new InvalidOperationException("Framebuffer has no attachments");
        }

        if (!HasValidDimensions())
        {
            throw new InvalidOperationException("Framebuffer has invalid sizes: " + Width + "x" + Height);
        }

        if (InitialStatus != FramebufferErrorCode.FramebufferUndefined)
        {
            throw new InvalidOperationException("Framebuffer has already been initialized");
        }

        CreateAttachments();

        if (!RendererDevice.IsOpenGL())
        {
            // Completeness is a property of a framebuffer object, and there is none. The attachments were
            // allocated through the device, which throws on a format or extent it cannot honour, so the
            // check OpenGL performs here has already happened by construction.
            InitialStatus = FramebufferErrorCode.FramebufferComplete;
            return InitialStatus;
        }

        var fboTarget = FramebufferTarget.Framebuffer;
        Bind(fboTarget);

        InitialStatus = GL.CheckFramebufferStatus(fboTarget);
        return InitialStatus;
    }

    /// <summary>
    /// Updates the MSAA sample count and resizes the framebuffer; attachments are recreated only when the width or height changes.
    /// </summary>
    public void Resize(int width, int height, int msaa)
    {
        if (width == Width && height == Height && msaa == NumSamples)
        {
            return;
        }

        NumSamples = msaa;
        Resize(width, height);
    }

    /// <summary>
    /// Resizes the framebuffer, recreating attachments if dimensions changed.
    /// </summary>
    /// <returns><see langword="true"/> if the dimensions changed and attachments were recreated.</returns>
    public bool Resize(int width, int height)
    {
        if (width == Width && height == Height)
        {
            return false;
        }

        Width = width;
        Height = height;
        CreateAttachments();
        return true;
    }

    private void CreateAttachments()
    {
        // The stencil view aliases the depth attachment's storage, so it is released before its parent.
        Stencil?.Delete();
        Depth?.Delete();
        Color?.Delete();

        var (width, height) = (Width, Height);

        if (ColorFormat != null)
        {
            Color = CreateAttachment(
                ColorFormat,
                width,
                height,
                NumMips,
                TextureUsage.ColorTarget | TextureUsage.Sampled | TextureUsage.CopySource | TextureUsage.CopyDestination,
                "FramebufferColor");

            Color.SetLabel("FramebufferColor");

            // Inert on a device with no framebuffer object; the attachment is named by RenderPass instead.
            Color.AttachToFramebuffer(this, FramebufferAttachment.ColorAttachment0, 0);
        }

        if (DepthFormat != null)
        {
            Depth = CreateAttachment(
                DepthFormat,
                width,
                height,
                1,
                TextureUsage.DepthStencilTarget | TextureUsage.Sampled | TextureUsage.CopySource,
                "FramebufferDepth");

            Depth.SetLabel("FramebufferDepth");
            Depth.AttachToFramebuffer(this, FramebufferAttachment.DepthAttachment, 0);

            if (DepthFormat == DepthAttachmentFormat.Depth32FStencil8)
            {
                Depth.AttachToFramebuffer(this, FramebufferAttachment.DepthStencilAttachment, 0);

                // The stencil aspect travels on the view rather than as a texture parameter. Only OpenGL
                // lets a texture object carry DepthStencilTextureMode, and the view has one mip, so the
                // base and max level this used to clamp are already the only ones it has.
                if (DeviceCanAllocate && Depth.RhiFormat != RhiFormat.Undefined)
                {
                    // The stencil aspect travels on the view rather than as a texture parameter. Only
                    // OpenGL lets a texture object carry DepthStencilTextureMode, and the view has one
                    // mip, so the base and max level this used to clamp are already the only ones it has.
                    Stencil = Depth.CreateView(0, 1, 0, 1, Depth.RhiFormat, TextureAspect.Stencil);
                    Stencil.SetLabel("FramebufferStencil");
                }
                else
                {
                    // The depth attachment was allocated outside the device, so its RHI description would
                    // report the wrong sample count and glTextureView would reject the view. See
                    // DeviceCanAllocate.
                    Stencil = Depth.CreateView(DepthFormat.InternalFormat);
                    Stencil.SetLabel("FramebufferStencil");
                    Stencil.SetBaseMaxLevel(0, 0);
                    Stencil.RhiFormat = Depth.RhiFormat;
                    GL.TextureParameter(Stencil.Handle, TextureParameterName.DepthStencilTextureMode, (int)DepthStencilTextureMode.StencilIndex);
                }
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether this framebuffer's attachments can be described to the device.
    /// </summary>
    /// <remarks>
    /// False for exactly one shape: a multisample target carrying a single sample. A
    /// <see cref="TextureDesc"/> decides multisample-ness from <see cref="TextureDesc.SampleCount"/>
    /// being greater than one, so a one-sample multisample texture cannot be described at all, and asking
    /// for it yields a plain 2D texture that a <c>sampler2DMS</c> then reads as black. The renderer uses
    /// this shape deliberately &#8212; it exercises the post-process chain's multisample resolve without
    /// depending on any driver's sample pattern &#8212; so those attachments keep the OpenGL allocation
    /// rather than being silently allocated as something else.
    /// </remarks>
    private bool DeviceCanAllocate => Target != TextureTarget.Texture2DMultisample || NumSamples > 1;

    /// <summary>
    /// Allocates one attachment, through the device when there is one that can express its format.
    /// </summary>
    /// <remarks>
    /// Not <c>new RenderTexture(target, format, ...)</c>, because that constructor always describes its
    /// storage as single-sampled and this is the one place in the renderer that allocates multisampled
    /// storage. The texture is created from a descriptor carrying the real sample count and wrapped.
    /// </remarks>
    private RenderTexture CreateAttachment(AttachmentFormat format, int width, int height, int numMips, TextureUsage usage, string name)
    {
        var mipCount = Math.Min(RenderTexture.MaxMipCount(width, height), numMips);
        var multisampled = Target == TextureTarget.Texture2DMultisample;
        var sampleCount = multisampled ? Math.Max(1, NumSamples) : 1;

        if (multisampled && mipCount > 1)
        {
            throw new InvalidOperationException("Multisample textures do not support mipmaps");
        }

        if (multisampled)
        {
            mipCount = 1;
        }

        var rhiFormat = ToRhiFormat(format.InternalFormat);
        var device = RendererDevice.Current;

        if (device is not null && rhiFormat != RhiFormat.Undefined && DeviceCanAllocate)
        {
            var texture = device.CreateTexture(new TextureDesc(
                width,
                height,
                rhiFormat,
                usage,
                name,
                Depth: 1,
                MipLevels: mipCount,
                SampleCount: sampleCount,
                Dimension: TextureDimension.Texture2D));

            var allocated = new RenderTexture(texture, Target);

            // Sampler state that only an OpenGL texture object carries. A no-op elsewhere, and the mip
            // clamp is what stops a sampler reading levels this attachment never allocated.
            allocated.SetBaseMaxLevel(0, mipCount - 1);
            return allocated;
        }

        if (device is not null && device.Backend != RhiBackend.OpenGL)
        {
            throw new InvalidOperationException(DeviceCanAllocate
                ? $"Attachment format {format.InternalFormat} has no {nameof(RhiFormat)} member, so it cannot be allocated on a {device.Backend} device. Add it to {nameof(ToRhiFormat)}, or give the call site a format the contract carries."
                : $"Framebuffer '{DebugName}' asks for a multisample target with {NumSamples} sample(s), which no {nameof(TextureDesc)} can describe: multisample-ness is decided by {nameof(TextureDesc.SampleCount)} being greater than one. Use a real sample count, or a non-multisample target.");
        }

        // No device at all: the direct OpenGL allocation this replaces, kept for the tools that use the
        // renderer without a presentation layer.
        var attachment = new RenderTexture(Target, width, height, 1, numMips);

        if (multisampled)
        {
            GL.TextureStorage2DMultisample(attachment.Handle, NumSamples, (SizedInternalFormat)format.InternalFormat, width, height, fixedsamplelocations: true);
        }
        else
        {
            GL.TextureStorage2D(attachment.Handle, mipCount, (SizedInternalFormat)format.InternalFormat, width, height);
        }

        attachment.SetBaseMaxLevel(0, mipCount - 1);
        attachment.RhiFormat = rhiFormat;
        return attachment;
    }

    /// <summary>
    /// Maps an attachment's OpenGL internal format back to the <see cref="RhiFormat"/> that describes it,
    /// so <see cref="RenderTexture.RhiTexture"/> can carry it into a <see cref="RenderPassDesc"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately partial, and answers <see cref="RhiFormat.Undefined"/> rather than throwing for a
    /// format it does not know. Colour attachments are not all statically known:
    /// <c>GLTextureDecoder</c> asks the driver for its preferred export format, so the value depends on
    /// the GPU. An undefined format still binds, samples and blits; it only costs the ability to upload
    /// to or create a view of the texture through the RHI.
    /// </para>
    /// <para>
    /// The depth formats are all mapped, and that is the part that has to be exact. The attachment point
    /// a render pass picks turns on whether the format has a stencil aspect, so a depth-stencil buffer
    /// that answered <see cref="RhiFormat.Undefined"/> would be attached as depth only and lose its
    /// stencil silently. The one integer colour target the renderer has, the picking buffer, is mapped
    /// for the same reason: its clear has to be an integer clear.
    /// </para>
    /// </remarks>
    private static RhiFormat ToRhiFormat(PixelInternalFormat internalFormat) => internalFormat switch
    {
        PixelInternalFormat.Rgba8 => RhiFormat.R8G8B8A8_UNorm,
        PixelInternalFormat.Srgb8Alpha8 => RhiFormat.R8G8B8A8_SRgb,
        PixelInternalFormat.Rgba16f => RhiFormat.R16G16B16A16_SFloat,
        PixelInternalFormat.Rgba16 => RhiFormat.R16G16B16A16_UNorm,
        PixelInternalFormat.Rgba32f => RhiFormat.R32G32B32A32_SFloat,
        PixelInternalFormat.R11fG11fB10f => RhiFormat.B10G11R11_UFloat,
        PixelInternalFormat.Rgb10A2 => RhiFormat.R10G10B10A2_UNorm,
        PixelInternalFormat.R8 => RhiFormat.R8_UNorm,
        PixelInternalFormat.R16f => RhiFormat.R16_SFloat,
        PixelInternalFormat.R32f => RhiFormat.R32_SFloat,
        PixelInternalFormat.Rg16f => RhiFormat.R16G16_SFloat,

        // The picking buffer. An integer target has to clear through the integer entry point.
        PixelInternalFormat.Rgba32ui => RhiFormat.R32G32B32A32_UInt,
        PixelInternalFormat.R32ui => RhiFormat.R32_UInt,

        PixelInternalFormat.DepthComponent16 => RhiFormat.D16_UNorm,
        PixelInternalFormat.DepthComponent32f => RhiFormat.D32_SFloat,
        PixelInternalFormat.Depth24Stencil8 => RhiFormat.D24_UNorm_S8_UInt,
        PixelInternalFormat.Depth32fStencil8 => RhiFormat.D32_SFloat_S8_UInt,

        _ => RhiFormat.Undefined,
    };

    /// <summary>
    /// Changes the attachment formats and recreates the GPU attachments at the current dimensions.
    /// </summary>
    public void ChangeFormat(AttachmentFormat? colorFormat, DepthAttachmentFormat? depthFormat, FramebufferAttachment? framebufferAttachment = null)
    {
        ColorFormat = colorFormat;
        DepthFormat = depthFormat;

        CreateAttachments();
    }

    /// <summary>
    /// Throws an <see cref="InvalidOperationException"/> if the framebuffer is not complete.
    /// </summary>
    public void CheckStatus_ThrowIfIncomplete(string name = "")
    {
        if (InitialStatus != FramebufferErrorCode.FramebufferComplete)
        {
            throw new InvalidOperationException($"Fbo '{name}' failed to initialize with error: {InitialStatus}");
        }
    }

    /// <summary>
    /// Attaches a specific mip level of the color texture to the color attachment point.
    /// </summary>
    /// <param name="mipLevel">Zero-based mip level to attach.</param>
    public void AttachColorMipLevel(int mipLevel)
    {
        Debug.Assert(Color != null, "Color attachment is null");

        Color.AttachToFramebuffer(this, FramebufferAttachment.ColorAttachment0, mipLevel);
    }

    /// <summary>
    /// Returns the pixel dimensions of the framebuffer at the given mip level.
    /// </summary>
    /// <param name="level">Zero-based mip level.</param>
    public Vector2i GetMipSize(int level)
    {
        var mipWidth = Math.Max(1, Width >> level);
        var mipHeight = Math.Max(1, Height >> level);

        return new(mipWidth, mipHeight);
    }

    /// <summary>
    /// Deletes the framebuffer object and all its attached textures.
    /// </summary>
    public void Delete()
    {
        if (FboHandle != 0)
        {
            GL.DeleteFramebuffer(FboHandle);
        }

        // Through the textures rather than glDeleteTexture, so device-allocated storage is retired by the
        // device. Destroying one directly while a frame still references it is undefined on Vulkan.
        // The stencil view aliases the depth attachment's storage, so it goes first.
        Stencil?.Delete();
        Depth?.Delete();
        Color?.Delete();

        Stencil = null;
        Depth = null;
        Color = null;
    }

    /// <summary>
    /// Configures depth comparison sampling on the depth attachment for shadow map reads.
    /// </summary>
    /// <param name="lEqualCompare">When <see langword="true"/>, sets a less-or-equal compare function; otherwise keeps the default.</param>
    public void SetShadowDepthSamplerState(bool lEqualCompare = false)
    {
        if (Depth != null)
        {
            Depth.SetParameter(TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRToTexture);

            if (lEqualCompare)
            {
                Depth.SetParameter(TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
            }

            Depth.SetFiltering(TextureMinFilter.Linear, TextureMagFilter.Linear);
            Depth.SetWrapMode(TextureWrapMode.ClampToEdge);
        }
    }
}
