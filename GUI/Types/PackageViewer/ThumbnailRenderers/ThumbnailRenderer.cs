using System.Diagnostics;
using System.Drawing;
using System.Threading;
using GUI.Utils;
using Microsoft.Extensions.Logging;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.SceneEnvironment;
using GLRecordingDevice = ValveResourceFormat.Renderer.RHI.OpenGL.GLRecordingDevice;

namespace GUI.Types.PackageViewer.ThumbnailRenderers;

internal enum ThumbnailSizes : int
{
    Tiny = 24,
    Small = 64,
    Medium = 128,
    Big = 192,
    Huge = 256,
}

internal abstract class ThumbnailRenderer : IDisposable
{
    protected Renderer? SceneRenderer;
    private Framebuffer? framebuffer;

    /// <summary>
    /// Where the post-process chain writes the display-ready image that gets read back.
    ///
    /// An offscreen colour target rather than framebuffer 0. A thumbnail is never presented, so bouncing
    /// through the window's default framebuffer only coupled this path to a surface that has no
    /// <see cref="ITexture"/> on OpenGL and no equivalent at all on a headless Vulkan device. Rendering
    /// into a target we own is also what makes the capture independent of the window's size.
    /// </summary>
    private Framebuffer? captureFramebuffer;

    private TextRenderer? textRenderer;
    private RendererContext? RendererContext;
    private NativeWindow? NativeWindow;
    private bool disposed;

    /// <summary>
    /// The graphics device thumbnails render through, created in <see cref="Load"/> once the GL context
    /// is current and published on the renderer context. Null until then.
    /// </summary>
    protected IDevice? Device { get; private set; }

    public bool Loaded { get; private set; }

    public virtual void SetResource(Resource resource)
    {

    }

    public void Load(VrfGuiContext context)
    {
        Loaded = true;

        var nativeWindowSettings = new NativeWindowSettings()
        {
            APIVersion = GLEnvironment.RequiredVersion,
            Vsync = VSyncMode.Adaptive,
            WindowBorder = WindowBorder.Hidden,
            WindowState = WindowState.Normal,
            Title = "Thumbnail Renderer",
            Flags = ContextFlags.ForwardCompatible,
            Profile = ContextProfile.Core,
            StartVisible = false,
            StartFocused = false,
        };

        NativeWindow = GLViewers.NativeWindowFactory.Create(nativeWindowSettings);
        RendererContext = new RendererContext(context, VrfGuiContext.Logger);

        NativeWindow.MakeCurrent();

        // Thumbnails render on a background thread, so diagnostics are logged rather than broken on.
        Device = new GLRecordingDevice(RendererContext, OnRhiMessage);
        RendererContext.Device = Device;

        GLEnvironment.Initialize(RendererContext.Logger);
        GLEnvironment.SetDefaultRenderState(RendererContext);

        SceneRenderer = new Renderer(RendererContext);

        SceneRenderer.Camera.SetFromQAngle(new Vector3(20f, 225f, 0f));

        RendererContext.Logger.LogInformation("Loading scene...");

        // Create TextRenderer (needed for Scene.Update)
        textRenderer = new TextRenderer(RendererContext, SceneRenderer.Camera);
        textRenderer.Load();

        SceneRenderer.Postprocess.Load(4);

        framebuffer = Framebuffer.Prepare("MainFramebuffer", 4, 4, 4,
            new(PixelInternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.HalfFloat),
            Framebuffer.DepthAttachmentFormat.Depth16);
        framebuffer.Initialize();

        // Display-ready sRGB-encoded bytes, matching what the golden image harness captures. Reading back
        // the Rgba16f scene target instead would capture pre-tonemap values.
        captureFramebuffer = Framebuffer.Prepare("ThumbnailCapture", 4, 4, 0,
            new(PixelInternalFormat.Rgba8, PixelFormat.Bgra, PixelType.UnsignedByte),
            null);
        captureFramebuffer.ClearColor = new OpenTK.Mathematics.Color4(0f, 128f / 255f, 0f, 1f);
        captureFramebuffer.ClearMask = ClearBufferMask.ColorBufferBit;
        captureFramebuffer.Initialize();

        SceneRenderer.Initialize();
        SceneRenderer.MainFramebuffer = framebuffer;

        SceneRenderer.LoadRendererResources();

        using var stream = Program.Assembly.GetManifestResourceStream("GUI.Utils.industrial_sunset_puresky.vtex_c");
        Debug.Assert(stream != null);

        using var resource = new Resource()
        {
            FileName = "vrf_default_cubemap.vtex_c"
        };
        resource.Read(stream);

        Renderer.LoadDefaultLighting(SceneRenderer.Scene, resource);

        var post = new ScenePostProcessVolume(SceneRenderer.Scene)
        {
            HasBloom = true,
            IsMaster = true,
            BloomSettings = new BloomSettings
            {
                BlendMode = BloomBlendType.BLOOM_BLEND_SCREEN,
                BloomStartValue = 1,
                ScreenBloomStrength = 0.584f,
                BloomThreshold = 1.972f,
                BloomThresholdWidth = 2.364f
            }
        };

        SceneRenderer.Scene.PostProcessInfo.AddPostProcessVolume(post);
    }

    private static void OnRhiMessage(RhiMessageSeverity severity, string message)
    {
        switch (severity)
        {
            case RhiMessageSeverity.Error: Log.Error(nameof(ThumbnailRenderer), message); break;
            case RhiMessageSeverity.Warning: Log.Warn(nameof(ThumbnailRenderer), message); break;
            default: Log.Debug(nameof(ThumbnailRenderer), message); break;
        }
    }

    public Bitmap? ReadPixelsToBitmap()
    {
        if (captureFramebuffer is null)
        {
            return null;
        }

        var currentSize = captureFramebuffer.Width;

        NativeWindow?.MakeCurrent();
        using var bitmap = new SkiaSharp.SKBitmap(currentSize, currentSize, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque);
        var pixels = bitmap.GetPixels(out var length);

        captureFramebuffer.Bind(FramebufferTarget.ReadFramebuffer);
        GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        GL.ReadPixels(0, 0, currentSize, currentSize, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);

        // Flip y
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Scale(1, -1, 0, bitmap.Height / 2f);
        canvas.DrawBitmap(bitmap, new SkiaSharp.SKPoint(), SkiaSharp.SKSamplingOptions.Default);

        return bitmap.ToBitmap();
    }

    public static Resource? LoadResourceFromPackageEntry(VrfGuiContext context, PackageEntry entry)
    {
        var stream = GameFileLoader.GetPackageEntryStream(context.CurrentPackage!, entry);

        if (stream == null)
        {
            return null;
        }

        var resource = new Resource { FileName = entry.GetFullPath() };
        resource.Read(stream);

        return resource;
    }

    public virtual Bitmap? Render(PackageEntry entry, VrfGuiContext context, ThumbnailSizes thumbnailSize, CancellationToken cancellationToken)
    {
        Debug.Assert(SceneRenderer != null);

        using var resource = LoadResourceFromPackageEntry(context, entry);

        if (resource == null)
        {
            return null;
        }

        Debug.Assert(NativeWindow != null, "NativeWindow is not created.");
        Debug.Assert(RendererContext != null, "RendererContext is not created.");
        Debug.Assert(SceneRenderer != null, "SceneRenderer is not loaded.");
        Debug.Assert(framebuffer is not null, "Framebuffer is not created.");
        Debug.Assert(captureFramebuffer is not null, "Capture framebuffer is not created.");
        Debug.Assert(textRenderer != null, "TextRenderer is not created.");

        NativeWindow.MakeCurrent();

        SceneRenderer.Scene.Clear();

        var size = (int)thumbnailSize;

        // Before the resource is set, because setting it frames the camera on what it loaded, and framing
        // it against the previous thumbnail's aspect ratio picks the wrong distance
        SceneRenderer.Camera.SetViewportSize(size, size);

        SetResource(resource);

        // Initialize scene (creates lighting buffers, octrees, etc.)
        SceneRenderer.Scene.Initialize();

        RendererContext.MaxTextureSize = size;
        NativeWindow.ClientSize = new(size);
        NativeWindow.Size = new OpenTK.Mathematics.Vector2i(size, size);
        framebuffer.Resize(size, size);
        captureFramebuffer.Resize(size, size);

        NativeWindow.MakeCurrent();

        var updateContext = new Scene.UpdateContext
        {
            Camera = SceneRenderer!.Camera,
            TextRenderer = textRenderer!,
            Timestep = 0,
        };

        SceneRenderer.Update(updateContext);

        // Green, so a thumbnail whose post-process produced nothing is obviously wrong rather than black.
        captureFramebuffer.BindAndClear();

        SceneRenderer.Render(framebuffer);
        SceneRenderer.PostprocessRender(framebuffer, captureFramebuffer, flipY: false);

        // Overlay text goes over the tonemapped image, in the target the post-process just wrote.
        captureFramebuffer.Bind(FramebufferTarget.Framebuffer);
        GL.Viewport(0, 0, size, size);
        textRenderer.Render(SceneRenderer.Camera);

        // Nothing is presented: the frame exists only to be read back, so there is no swap.

        // The contract's sanctioned use of WaitIdle: drain before reading back.
        Debug.Assert(Device is not null, "Device is not created.");
        Device.WaitIdle();

        return ReadPixelsToBitmap();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposed)
        {
            return;
        }

        if (disposing)
        {
            // Before the native window, which owns the GL context the device's resources live in.
            if (RendererContext is not null)
            {
                RendererContext.Device = null;
            }

            Device?.Dispose();
            Device = null;

            framebuffer?.Delete();
            captureFramebuffer?.Delete();

            RendererContext?.Dispose();
            SceneRenderer?.Dispose();
            GLViewers.NativeWindowFactory.Destroy(NativeWindow);
        }

        Loaded = false;
        disposed = true;
    }
}
