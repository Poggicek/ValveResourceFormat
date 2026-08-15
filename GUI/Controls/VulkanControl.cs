using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using GUI.Utils;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;
using ValveResourceFormat.Renderer.RHI.Vulkan.Present;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using ImageLayout = Silk.NET.Vulkan.ImageLayout;
using VkDevice = Silk.NET.Vulkan.Device;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace GUI.Controls;

/// <summary>
/// The shared Vulkan device every <see cref="VulkanControl"/> presents through, plus the
/// <c>VK_KHR_*</c> surface and swapchain entry points that only the presentation layer needs.
/// </summary>
/// <remarks>
/// <para>
/// The device itself is <see cref="VulkanPresentDevice"/>, which lives in the renderer next to the rest
/// of the backend. This class exists only because the swapchain extensions do not: <c>Renderer</c>
/// references <c>Silk.NET.Vulkan</c> and its <c>Extensions.EXT</c>, while <c>KhrSurface</c>,
/// <c>KhrWin32Surface</c> and <c>KhrSwapchain</c> come from <c>Extensions.KHR</c>, which only this
/// project references. Everything platform- and swapchain-specific therefore stays on this side of the
/// boundary, which is also what the contract asks for: swapchain creation is owned by the presentation
/// layer and is no part of the RHI.
/// </para>
/// <para>
/// Reference counted, so every tab shares one instance, device and queue. Nothing is destroyed when the
/// last tab closes: the extension wrappers and the <see cref="Vk"/> under them share the loaded
/// <c>vulkan-1</c> module, and disposing them unloads it, which makes the next device this process
/// creates access-violate inside the loader. <see cref="Shutdown"/> is the one place it all goes away.
/// </para>
/// </remarks>
public sealed class VulkanPresentSession
{
    private static readonly Lock SharedLock = new();
    private static VulkanPresentSession? shared;

    /// <summary>Gets the device rendering and presentation go through.</summary>
    public VulkanPresentDevice PresentDevice { get; }

    /// <summary>Gets the core Vulkan entry points.</summary>
    public Vk Api => PresentDevice.Core.Api;

    /// <summary>Gets the instance the presentation surfaces are created from.</summary>
    public Instance Instance => PresentDevice.Core.Instance.Handle;

    /// <summary>Gets the physical device that was selected.</summary>
    public PhysicalDevice PhysicalDevice => PresentDevice.Core.Adapter.Handle;

    /// <summary>Gets the logical device the swapchains belong to.</summary>
    public VkDevice LogicalDevice => PresentDevice.Core.Handle;

    /// <summary>Gets the index of the queue family used for both rendering and presentation.</summary>
    public uint PresentQueueFamily => PresentDevice.Core.GraphicsQueueFamily;

    /// <summary>Gets the queue used for both rendering and presentation.</summary>
    public Queue PresentQueue => PresentDevice.Core.GraphicsQueue;

    /// <summary>Gets the lock guarding the queue, presentation and the frame ring. Reentrant.</summary>
    public Lock QueueLock => PresentDevice.SubmissionLock;

    /// <summary>Gets the object naming helper, so swapchain images show up named in a capture.</summary>
    public VulkanDebugNames DebugNames => PresentDevice.Core.DebugNames;

    /// <summary>Gets the <c>VK_KHR_surface</c> entry points.</summary>
    public KhrSurface SurfaceApi { get; }

    /// <summary>Gets the <c>VK_KHR_win32_surface</c> entry points.</summary>
    public KhrWin32Surface Win32SurfaceApi { get; }

    /// <summary>Gets the <c>VK_KHR_swapchain</c> entry points.</summary>
    public KhrSwapchain SwapchainApi { get; }

    private VulkanPresentSession(VulkanPresentDevice device)
    {
        PresentDevice = device;

        if (!device.Core.Api.TryGetInstanceExtension(Instance, out KhrSurface surfaceApi))
        {
            throw new VulkanException($"{VulkanPresentDevice.SurfaceExtensionName} is unavailable.");
        }

        if (!device.Core.Api.TryGetInstanceExtension(Instance, out KhrWin32Surface win32SurfaceApi))
        {
            throw new VulkanException($"{VulkanPresentDevice.Win32SurfaceExtensionName} is unavailable.");
        }

        if (!device.Core.Api.TryGetDeviceExtension(Instance, LogicalDevice, out KhrSwapchain swapchainApi))
        {
            throw new VulkanException($"{VulkanPresentDevice.SwapchainExtensionName} is unavailable.");
        }

        SurfaceApi = surfaceApi;
        Win32SurfaceApi = win32SurfaceApi;
        SwapchainApi = swapchainApi;
    }

    /// <summary>Returns the shared session, creating the device on first use.</summary>
    /// <returns>The shared session.</returns>
    /// <exception cref="VulkanException">No suitable device exists, or creation failed.</exception>
    public static VulkanPresentSession Acquire()
    {
        using var _ = SharedLock.EnterScope();

        if (shared is null)
        {
            var options = new VulkanCoreOptions
            {
                ApplicationName = "Source 2 Viewer",
                EnableValidation = ShouldUseValidation(),
                EnableSynchronizationValidation = ShouldUseValidation(),
            };

            // The viewer reports an interface problem rather than failing the pipeline: a shader whose
            // declarations disagree with the layout is a bug worth logging, but not one that should stop
            // a user opening a file. The golden harness turns it into a hard failure instead.
            var pipelineOptions = new VulkanPipelineOptions
            {
                MessageCallback = OnRhiMessage,
                TreatInterfaceProblemsAsErrors = false,
            };

            var device = VulkanPresentDevice.Acquire(OnRhiMessage, options, pipelineOptions);

            try
            {
                shared = new VulkanPresentSession(device);
            }
            catch
            {
                VulkanPresentDevice.Release();
                throw;
            }

            Log.Info(nameof(VulkanPresentSession),
                $"Vulkan device: {device.Core.Adapter.Name}, validation {(device.ValidationEnabled ? "on" : "off")}.");

            return shared;
        }

        VulkanPresentDevice.Acquire();

        return shared;
    }

    /// <summary>Drops a control's reference. Destroys nothing; see the remarks on the class.</summary>
    public static void Release() => VulkanPresentDevice.Release();

    /// <summary>
    /// Destroys the shared instance and device. Call once while shutting the application down, after
    /// every <see cref="VulkanControl"/> is disposed; the validation layer reports anything still alive
    /// at this point as a leak.
    /// </summary>
    public static void Shutdown()
    {
        using var _ = SharedLock.EnterScope();

        if (shared is null)
        {
            return;
        }

        var outstanding = VulkanPresentDevice.Shutdown();

        if (outstanding != 0)
        {
            Log.Warn(nameof(VulkanPresentSession), $"Shutting down with {outstanding} live controls.");
        }

        shared = null;
    }

    private static bool ShouldUseValidation()
    {
#if DEBUG
        return true;
#else
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VRF_VULKAN_VALIDATION"));
#endif
    }

    private static void OnRhiMessage(RhiMessageSeverity severity, string message)
    {
        switch (severity)
        {
            case RhiMessageSeverity.Error: Log.Error("Vulkan", message); break;
            case RhiMessageSeverity.Warning: Log.Warn("Vulkan", message); break;
            default: Log.Debug("Vulkan", message); break;
        }
    }
}

/// <summary>
/// Scope returned by <see cref="VulkanControl.MakeCurrent"/>, the Vulkan counterpart of the OpenGL
/// path's context scope.
/// </summary>
/// <remarks>
/// Vulkan has no current-context concept, so this only takes the control's render lock. The shape is
/// deliberately identical to the OpenGL one so the shared render thread's call sites are unchanged.
/// Simplifying it away is tempting and wrong: changing the threading model and the graphics API in the
/// same step is how a port like this goes wrong.
/// </remarks>
public readonly ref struct VulkanLockScope
{
#pragma warning disable CA2213 // Ref structs implicitly have Dispose and do not implement IDisposable
    private readonly Lock.Scope lockScope;
#pragma warning restore CA2213

    /// <summary>Enters <paramref name="renderLock"/> for the duration of the scope.</summary>
    /// <param name="renderLock">The control's render lock.</param>
    public VulkanLockScope(Lock renderLock)
    {
        // Not ArgumentNullException.ThrowIfNull: passing a Lock as object trips CS9216.
        lockScope = (renderLock ?? throw new ArgumentNullException(nameof(renderLock))).EnterScope();
    }

    /// <summary>Leaves the render lock.</summary>
    public readonly void Dispose() => lockScope.Dispose();
}

/// <summary>
/// Vulkan-capable WinForms control. Owns a plain Win32 child window, the <c>VkSurfaceKHR</c> created
/// from it, and the swapchain presented into it.
/// </summary>
/// <remarks>
/// <para>
/// The OpenGL control reparents a GLFW <c>NativeWindow</c> because a WGL context needs a window GLFW
/// created. <c>VK_KHR_win32_surface</c> takes any HWND, so this control creates a bare child window
/// instead and GLFW is not involved on this path at all.
/// </para>
/// <para>
/// Threading matches the OpenGL path exactly: the shared render thread calls <see cref="DrawFrame"/>,
/// which takes the per-control render lock; the UI thread takes the same lock whenever it creates or
/// destroys the child window. Vulkan needs no current context, but the lock and its scope are kept so
/// that swapping backends does not also swap threading models.
/// </para>
/// <para>
/// <b>Frame pacing is the device's, not this control's.</b> The swapchain contributes the acquire and
/// the present; everything between them &#8212; how far the CPU may run ahead, which command pool a frame
/// records into, when a retired frame's resources are recycled &#8212; belongs to
/// <see cref="VulkanPresentDevice"/> and its frame ring. So a frame here is
/// <c>BeginFrame, acquire, record, submit, present, EndFrame</c>, and this control owns no fences and
/// no command pool of its own. Keeping a second, parallel notion of "frames in flight" next to the
/// device's is how the two drift apart.
/// </para>
/// </remarks>
public sealed partial class VulkanControl : Control
{
    private const string ChildWindowClassName = "Source2ViewerVulkanSurface";

    private static readonly Lock WindowClassLock = new();
    private static bool windowClassRegistered;

    private readonly Lock renderLock;
    private readonly VulkanPresentSession? injectedSession;

    private VulkanPresentSession? session;
    private bool ownsSessionReference;

    private HWND childWindow;
    private SurfaceKHR surface;
    private SwapchainKHR swapchain;

    private VulkanSwapchainTexture[] swapchainTextures = [];
    private VkSemaphore[] renderFinishedSemaphores = [];
    private ulong[] imageFrameSerials = [];

    private VkSemaphore[] imageAvailableSemaphores = [];

    private Extent2D swapchainExtent;
    private bool swapchainDirty;
    private bool torndown;

    // Written by the UI thread on resize, read by the render thread. Only a fallback: the surface's
    // reported current extent is authoritative on Win32, which is what makes DPI changes free.
    private volatile int lastClientWidth;
    private volatile int lastClientHeight;

    /// <summary>
    /// Colour the swapchain image is cleared to when no <see cref="RenderFrame"/> callback is set.
    /// Distinct values make it obvious which tab a presented image belongs to.
    /// </summary>
    public ClearColorValue ClearColor { get; set; } = new(0.1f, 0.1f, 0.12f, 1f);

    /// <summary>
    /// Draws the frame into the acquired swapchain image, or <see langword="null"/> to clear it to
    /// <see cref="ClearColor"/>.
    /// </summary>
    /// <remarks>This is the seam the renderer attaches to. The callback receives the acquired image and
    /// the device the frame is open on, and records through command lists it begins and submits itself;
    /// <see cref="VulkanPresentFrame"/> says why it owns them rather than being handed one, and why
    /// reaching the backbuffer is still a copy rather than a render pass that names it.</remarks>
    public VulkanPresentFrameCallback? RenderFrame { get; set; }

    /// <summary>
    /// Whether presentation waits for vertical blank. <see langword="true"/> selects FIFO, which every
    /// implementation supports; <see langword="false"/> prefers mailbox, then immediate.
    /// </summary>
    public bool Vsync
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                swapchainDirty = true;
            }
        }
    } = true;

    /// <summary>Gets a value indicating whether a swapchain currently exists and can be presented to.</summary>
    public bool HasSwapchain => swapchain.Handle != 0;

    /// <summary>Gets the format of the current swapchain images; whatever renders into them must match it.</summary>
    public Format SwapchainFormat { get; private set; }

    /// <summary>Gets the contract format of the current swapchain images.</summary>
    public RhiFormat SwapchainRhiFormat { get; private set; }

    /// <summary>Gets the size of the current swapchain images, in pixels.</summary>
    public Extent2D SwapchainExtent => swapchainExtent;

    /// <summary>Gets the device this control presents through, or <see langword="null"/> before the
    /// surface exists or after teardown.</summary>
    public VulkanPresentDevice? PresentDevice => session?.PresentDevice;

    /// <summary>Gets the number of frames successfully presented, for tests and diagnostics.</summary>
    public long PresentedFrameCount { get; private set; }

    /// <summary>Gets the number of times the swapchain was rebuilt, for tests and diagnostics.</summary>
    public long SwapchainRecreateCount { get; private set; }

    /// <summary>
    /// Gets the number of times acquire or present reported the swapchain out of date or suboptimal,
    /// for tests and diagnostics.
    /// </summary>
    public long OutOfDateCount { get; private set; }

    /// <summary>
    /// Gets the number of frames skipped because the surface had no pixels, for tests and diagnostics.
    /// Minimising a window drives this and nothing else does.
    /// </summary>
    public long ZeroExtentSkipCount { get; private set; }

    /// <summary>
    /// Constructs a control that presents onto its own child window.
    /// </summary>
    /// <param name="renderLock">
    /// Lock serialising this control's Vulkan work, held by the shared render thread while drawing and
    /// by the UI thread while creating or destroying the surface.
    /// </param>
    /// <param name="session">
    /// Session to present with, or <see langword="null"/> to take a reference on the shared one.
    /// </param>
    public VulkanControl(Lock renderLock, VulkanPresentSession? session = null)
    {
        // Not ArgumentNullException.ThrowIfNull: passing a Lock as object trips CS9216.
        this.renderLock = renderLock ?? throw new ArgumentNullException(nameof(renderLock));
        injectedSession = session;

        SetStyle(ControlStyles.Opaque, true);
        SetStyle(ControlStyles.UserPaint, true);
        SetStyle(ControlStyles.AllPaintingInWmPaint, true);

        // Keyboard messages go to the focused window, and this control must never be it. Hit testing
        // (see WndProc) already stops a click from focusing it, but tab navigation does not go through
        // hit testing, so the control is taken out of the tab order as well. A Vulkan surface that held
        // the focus would send every keystroke somewhere the viewer's input filter does not look.
        SetStyle(ControlStyles.Selectable, false);

        DoubleBuffered = false;
    }

    private const int WM_NCHITTEST = 0x0084;

    /// <summary>Hit-test result meaning "not me, keep looking underneath".</summary>
    private const int HTTRANSPARENT = -1;

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>This control is invisible to the mouse on purpose.</b> A Vulkan viewer stacks this control in
    /// front of the <see cref="GLControl"/> that the viewer's input plumbing is built around: the raw
    /// message filter keys on <c>GLControl.Handle</c>, and mouse down, mouse up, mouse enter, mouse leave
    /// and lost focus are wired as WinForms events on that control. Being in front made this control the
    /// window every mouse message was delivered to, so the filter rejected all of them and none of those
    /// events ever fired &#8212; a Vulkan viewer whose camera could not be moved at all.
    /// </para>
    /// <para>
    /// Answering <c>HTTRANSPARENT</c> makes the window manager skip this window and carry on hit testing
    /// the siblings underneath it in the same thread, which is the GL control. So the messages arrive at
    /// exactly the window they arrive at on OpenGL, carrying that window's client coordinates, and the
    /// whole input path &#8212; filter, events, focus, capture, hover tracking, picking &#8212; is the one
    /// OpenGL already uses. Nothing about it is backend specific, which is the point: routing input
    /// through a second window would have meant a second coordinate space and a second focus story, and
    /// an origin offset there would move the camera <i>wrongly</i> rather than not at all.
    /// </para>
    /// <para>
    /// The plain Win32 child window this control owns is already <c>WS_DISABLED</c> and is skipped by hit
    /// testing for the same reason, so the two windows this control puts on screen are both transparent
    /// to input. Nothing here is interactive; it is a surface the swapchain presents into.
    /// </para>
    /// </remarks>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            m.Result = HTTRANSPARENT;
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// Takes the render lock. Named after the OpenGL path's context scope so the shared render thread
    /// keeps one shape for both backends; Vulkan itself has nothing to make current.
    /// </summary>
    /// <returns>A scope that releases the lock when disposed.</returns>
    public VulkanLockScope MakeCurrent() => new(renderLock);

    /// <inheritdoc/>
    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_VREDRAW = 0x1;
            const int CS_HREDRAW = 0x2;

            var cp = base.CreateParams;
            cp.ClassStyle |= CS_VREDRAW | CS_HREDRAW;
            return cp;
        }
    }

    /// <inheritdoc/>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (DesignMode)
        {
            return;
        }

        CreateChildWindowAndSurface();
    }

    /// <inheritdoc/>
    protected override void OnHandleDestroyed(EventArgs e)
    {
        // Runs before the HWND goes away, which is the only point the child window can still be
        // destroyed by its owning (UI) thread.
        Teardown();

        base.OnHandleDestroyed(e);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Teardown();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ResizeChildWindow();
    }

    /// <inheritdoc/>
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);

        // The client size is in physical pixels, so a DPI change is just another resize. The swapchain
        // still rebuilds from the surface's reported extent rather than from anything measured here.
        ResizeChildWindow();
        swapchainDirty = true;
    }

    /// <inheritdoc/>
    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        ResizeChildWindow();
    }

    private void ResizeChildWindow()
    {
        if (childWindow.IsNull || !IsHandleCreated || DesignMode)
        {
            return;
        }

        var size = ClientSize;
        var width = Math.Max(0, size.Width);
        var height = Math.Max(0, size.Height);

        lastClientWidth = width;
        lastClientHeight = height;

        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_NOOWNERZORDER = 0x0200;

        SetWindowPos(childWindow, nint.Zero, 0, 0, width, height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    private unsafe void CreateChildWindowAndSurface()
    {
        using var _ = renderLock.EnterScope();

        if (torndown || !childWindow.IsNull)
        {
            return;
        }

        EnsureWindowClassRegistered();

        var size = ClientSize;
        lastClientWidth = Math.Max(0, size.Width);
        lastClientHeight = Math.Max(0, size.Height);

        var moduleHandle = PInvoke.GetModuleHandle(default(PCWSTR));

        // WS_DISABLED keeps every mouse and keyboard message on the WinForms control, exactly like the
        // GLFW child window it replaces, so input handling is unaffected by the backend.
        fixed (char* className = ChildWindowClassName)
        {
            childWindow = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
                new PCWSTR(className),
                default,
                WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_VISIBLE | WINDOW_STYLE.WS_DISABLED | WINDOW_STYLE.WS_CLIPSIBLINGS,
                0, 0, lastClientWidth, lastClientHeight,
                (HWND)Handle,
                default,
                moduleHandle,
                null);
        }

        if (childWindow.IsNull)
        {
            Log.Error(nameof(VulkanControl), $"Failed to create the Vulkan child window: {Marshal.GetLastWin32Error()}");
            return;
        }

        if (injectedSession is not null)
        {
            session = injectedSession;
        }
        else
        {
            try
            {
                session = VulkanPresentSession.Acquire();
                ownsSessionReference = true;
            }
            catch (VulkanException exception)
            {
                // Not fatal. Both backends ship, and OpenGL is the one that always works.
                Log.Error(nameof(VulkanControl), $"Vulkan is unavailable: {exception.Message}");
                RhiBackendSelection.FallBackToOpenGL(exception.Message);
                DestroyChildWindow();
                return;
            }
        }

        var createInfo = new Win32SurfaceCreateInfoKHR
        {
            SType = StructureType.Win32SurfaceCreateInfoKhr,
            Hinstance = moduleHandle,
            Hwnd = childWindow,
        };

        var result = session.Win32SurfaceApi.CreateWin32Surface(session.Instance, in createInfo, null, out surface);

        if (result != Result.Success)
        {
            Log.Error(nameof(VulkanControl), $"vkCreateWin32SurfaceKHR failed: {result}");
            surface = default;
            return;
        }

        var supportResult = session.SurfaceApi.GetPhysicalDeviceSurfaceSupport(
            session.PhysicalDevice, session.PresentQueueFamily, surface, out var supported);

        if (supportResult != Result.Success || !supported)
        {
            // The authoritative "can this adapter present here" answer, which cannot be asked before a
            // surface exists and therefore cannot gate physical device selection.
            Log.Error(nameof(VulkanControl), "The present queue family cannot present to this surface.");
            RhiBackendSelection.FallBackToOpenGL("the selected adapter cannot present to a window surface");
            DestroySurface();
            return;
        }

        RhiBackendSelection.MarkActive(RhiBackend.Vulkan);
        swapchainDirty = true;
    }

    private static unsafe void EnsureWindowClassRegistered()
    {
        using var _ = WindowClassLock.EnterScope();

        if (windowClassRegistered)
        {
            return;
        }

        fixed (char* className = ChildWindowClassName)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = ChildWindowProcedureDelegate,
                hInstance = PInvoke.GetModuleHandle(default(PCWSTR)),
                lpszClassName = new PCWSTR(className),
                // No background brush: the swapchain owns every pixel, and erasing would flicker.
                hbrBackground = default,
            };

            if (PInvoke.RegisterClassEx(in windowClass) == 0)
            {
                var error = Marshal.GetLastWin32Error();

                // 1410 is ERROR_CLASS_ALREADY_EXISTS, which is benign.
                if (error != 1410)
                {
                    throw new Win32Exception(error, "Failed to register the Vulkan child window class.");
                }
            }
        }

        windowClassRegistered = true;
    }

    // Held in a static field so the delegate outlives every window registered against the class.
    private static readonly WNDPROC ChildWindowProcedureDelegate = ChildWindowProcedure;

    private static LRESULT ChildWindowProcedure(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
        => PInvoke.DefWindowProc(hwnd, message, wParam, lParam);

    /// <summary>
    /// Runs one frame: acquire, render into the acquired image, present. Called by the shared render
    /// thread.
    /// </summary>
    /// <returns><see langword="true"/> when an image was presented this call.</returns>
    public bool DrawFrame()
    {
        using var _ = renderLock.EnterScope();

        if (torndown || session is null || surface.Handle == 0)
        {
            return false;
        }

        if (!EnsureSwapchain())
        {
            // Minimised, or the swapchain could not be built at the current size.
            return false;
        }

        var device = session.PresentDevice;

        // Held across the whole frame. The frame ring is device-wide and every window shares one
        // queue, so a frame is not a thing two windows may be inside at once.
        using var submission = device.SubmissionLock.EnterScope();

        device.BeginFrame();

        uint imageIndex;
        bool recorded;

        try
        {
            recorded = AcquireAndRecord(device, out imageIndex);
        }
        finally
        {
            // Unconditional, and this is where the frame reaches the queue: EndFrame submits the whole
            // frame as one batch carrying the acquire wait and the present signal. A frame that opened
            // and recorded nothing leaves the ring waiting on a serial nothing would ever reach, which
            // is what the base class's empty signalling submission is for.
            device.EndFrame();
        }

        // Only now, because vkQueuePresentKHR waits on a binary semaphore and a wait may not be
        // submitted before the submission that signals it. Presenting inside the recording step would
        // queue a wait on a semaphore whose signalling batch had not been handed over yet.
        return recorded && Present(imageIndex);
    }

    private unsafe bool AcquireAndRecord(VulkanPresentDevice device, out uint acquiredImageIndex)
    {
        acquiredImageIndex = 0;

        Debug.Assert(session is not null);

        var frameIndex = device.FrameIndex;
        var acquireSemaphore = imageAvailableSemaphores[frameIndex];

        uint imageIndex;
        var acquire = session.SwapchainApi.AcquireNextImage(
            session.LogicalDevice, swapchain, ulong.MaxValue, acquireSemaphore, default, &imageIndex);

        if (acquire is Result.ErrorOutOfDateKhr)
        {
            // The semaphore was not touched, so the next frame is free to start over on a rebuilt
            // swapchain without an already-signalled semaphore handed to vkAcquireNextImageKHR.
            swapchainDirty = true;
            OutOfDateCount++;
            return false;
        }

        if (acquire is Result.SuboptimalKhr)
        {
            // Suboptimal is a success code: the semaphore *was* signalled, so this frame must be
            // submitted and presented or the semaphore stays signalled with nothing to wait on it.
            swapchainDirty = true;
            OutOfDateCount++;
        }
        else if (acquire is not Result.Success)
        {
            Log.Error(nameof(VulkanControl), $"vkAcquireNextImageKHR failed: {acquire}");
            swapchainDirty = true;
            return false;
        }

        // With more swapchain images than frames in flight, the slot the ring just recycled says
        // nothing about whether *this image* is still being rendered into by an older frame.
        device.WaitForFrameSerial(imageFrameSerials[imageIndex]);

        var backbuffer = swapchainTextures[imageIndex];

        // Acquiring guarantees nothing about the image's layout or its contents, so the frame starts
        // from Undefined and the first barrier discards whatever the presentation engine left.
        backbuffer.OverrideTrackedState(ResourceState.Undefined);

        var callback = RenderFrame;

        if (callback is not null)
        {
            var frame = new VulkanPresentFrame(backbuffer, device, frameIndex);

            // A throwing callback must not take the frame with it. The acquire semaphore has already
            // been signalled by the presentation engine, and the only thing that can unsignal it is a
            // submission waiting on it; abandoning the frame here would hand an already-signalled
            // semaphore to the next vkAcquireNextImageKHR on this slot. So whatever the callback
            // managed to record is transitioned, submitted and presented anyway, and the failure is
            // reported rather than compounded.
            try
            {
                callback(in frame);
            }
            catch (Exception exception)
            {
                Log.Error(nameof(VulkanControl), $"The frame callback threw, presenting the partial frame: {exception}");
            }
        }

        // A real command list from the device, not a raw command buffer off the pool. That is what
        // makes a windowed frame the same kind of frame the offscreen path records: whatever draws
        // here gets the device's recorder, its descriptor binder and its pipelines.
        //
        // Begun after the callback and not before it. This device reuses one command list and refuses
        // to begin a second while the first is unsubmitted, and a callback that draws a scene needs
        // several -- so a list opened here first would refuse every one of them. What is left for this
        // one is the transition into Present, which is the presentation layer's own work and belongs at
        // the end of the frame's batch anyway.
        var commands = device.BeginCommandList($"{nameof(VulkanControl)} present {device.CurrentFrameSerial}");
        var commandBuffer = ((VulkanCommandList)commands).Handle;

        if (callback is null)
        {
            RecordClear(commandBuffer, backbuffer);
        }

        backbuffer.TransitionTo(commandBuffer, ResourceState.Present);

        var signalSemaphore = renderFinishedSemaphores[imageIndex];

        // Named before the submit, so the frame's one batch carries the acquire wait and the present
        // signal rather than needing a second submission beside it.
        device.SetPresentSemaphores(acquireSemaphore, signalSemaphore);
        device.Submit(commands);

        imageFrameSerials[imageIndex] = device.CurrentFrameSerial;
        acquiredImageIndex = imageIndex;

        return true;
    }

    private unsafe bool Present(uint imageIndex)
    {
        Debug.Assert(session is not null);

        var signalSemaphore = renderFinishedSemaphores[imageIndex];
        var localSwapchain = swapchain;
        var index = imageIndex;

        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &localSwapchain,
            PImageIndices = &index,
        };

        var present = session.SwapchainApi.QueuePresent(session.PresentQueue, in presentInfo);

        if (present is Result.SuboptimalKhr)
        {
            swapchainDirty = true;
            OutOfDateCount++;
        }
        else if (present is Result.ErrorOutOfDateKhr)
        {
            swapchainDirty = true;
            OutOfDateCount++;
            return false;
        }
        else if (present is not Result.Success)
        {
            Log.Error(nameof(VulkanControl), $"vkQueuePresentKHR failed: {present}");
            swapchainDirty = true;
            return false;
        }

        PresentedFrameCount++;

        return true;
    }

    private unsafe void RecordClear(CommandBuffer commandBuffer, VulkanSwapchainTexture backbuffer)
    {
        Debug.Assert(session is not null);

        backbuffer.TransitionTo(commandBuffer, ResourceState.CopyDestination);

        var range = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };

        var clearColor = ClearColor;

        session.Api.CmdClearColorImage(
            commandBuffer, backbuffer.Handle, ImageLayout.TransferDstOptimal, in clearColor, 1, in range);
    }

    /// <summary>
    /// Brings the swapchain up to date with the surface, rebuilding it when the window resized, the
    /// DPI changed, or a previous acquire or present reported it out of date.
    /// </summary>
    /// <returns><see langword="false"/> when there is nothing to draw into.</returns>
    private bool EnsureSwapchain()
    {
        Debug.Assert(session is not null);

        var caps = QuerySurfaceCapabilities();

        if (caps is null)
        {
            return false;
        }

        var extent = ResolveExtent(caps.Value);

        // Minimised, or collapsed to nothing mid drag. Creating a zero-extent swapchain is invalid, so
        // hold on to whatever exists and simply do not draw until there are pixels again.
        if (extent.Width == 0 || extent.Height == 0)
        {
            ZeroExtentSkipCount++;
            return false;
        }

        if (HasSwapchain && !swapchainDirty && extent.Width == swapchainExtent.Width && extent.Height == swapchainExtent.Height)
        {
            return true;
        }

        return RecreateSwapchain(caps.Value, extent);
    }

    private SurfaceCapabilitiesKHR? QuerySurfaceCapabilities()
    {
        Debug.Assert(session is not null);

        var result = session.SurfaceApi.GetPhysicalDeviceSurfaceCapabilities(session.PhysicalDevice, surface, out var caps);

        if (result != Result.Success)
        {
            // The window can disappear between the render thread's checks; that is not an error worth
            // reporting every frame, the next teardown will clean up.
            if (result is not Result.ErrorSurfaceLostKhr)
            {
                Log.Error(nameof(VulkanControl), $"vkGetPhysicalDeviceSurfaceCapabilitiesKHR failed: {result}");
            }

            return null;
        }

        return caps;
    }

    private Extent2D ResolveExtent(SurfaceCapabilitiesKHR caps)
    {
        // Win32 surfaces always report the real client size, which is why nothing here has to know
        // about DPI: a monitor switch changes the client size in pixels and this picks it up.
        if (caps.CurrentExtent.Width != uint.MaxValue)
        {
            return caps.CurrentExtent;
        }

        return new Extent2D(
            Math.Clamp((uint)lastClientWidth, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
            Math.Clamp((uint)lastClientHeight, caps.MinImageExtent.Height, caps.MaxImageExtent.Height));
    }

    private unsafe bool RecreateSwapchain(SurfaceCapabilitiesKHR caps, Extent2D extent)
    {
        Debug.Assert(session is not null);

        var logicalDevice = session.LogicalDevice;

        // The frame renders straight into the swapchain image, so it must be a colour attachment; the
        // fallback clear path also needs it as a transfer destination.
        const ImageUsageFlags RequiredUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit;

        if ((caps.SupportedUsageFlags & RequiredUsage) != RequiredUsage)
        {
            Log.Error(nameof(VulkanControl), $"The surface supports {caps.SupportedUsageFlags}, which does not cover {RequiredUsage}.");
            return false;
        }

        if (!TryChooseSurfaceFormat(out var format, out var rhiFormat))
        {
            Log.Error(nameof(VulkanControl), "The surface reports no format the renderer can describe a render pass against.");
            return false;
        }

        var presentMode = ChoosePresentMode();

        var imageCount = caps.MinImageCount + 1;

        if (caps.MaxImageCount > 0 && imageCount > caps.MaxImageCount)
        {
            imageCount = caps.MaxImageCount;
        }

        // Everything still referencing the old swapchain has to finish first. A device-wide wait is
        // heavier than waiting on the frame timeline, but the timeline does not cover queued presents
        // and this only happens on resize.
        WaitDeviceIdle();

        var oldSwapchain = swapchain;

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = surface,
            MinImageCount = imageCount,
            ImageFormat = format.Format,
            ImageColorSpace = format.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = RequiredUsage,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
            OldSwapchain = oldSwapchain,
        };

        var result = session.SwapchainApi.CreateSwapchain(logicalDevice, in createInfo, null, out var newSwapchain);

        // The old swapchain is retired whether or not creation succeeded, so it is destroyed either
        // way, and only after the new one is built so the driver can hand over its resources.
        DestroySwapchainObjects(oldSwapchain);
        swapchain = default;

        if (result != Result.Success)
        {
            Log.Error(nameof(VulkanControl), $"vkCreateSwapchainKHR failed: {result}");
            return false;
        }

        swapchain = newSwapchain;
        swapchainExtent = extent;
        SwapchainFormat = format.Format;
        SwapchainRhiFormat = rhiFormat;
        swapchainDirty = false;
        SwapchainRecreateCount++;

        uint actualImageCount = 0;
        session.SwapchainApi.GetSwapchainImages(logicalDevice, swapchain, ref actualImageCount, null);

        var images = new Image[actualImageCount];

        fixed (Image* pointer = images)
        {
            session.SwapchainApi.GetSwapchainImages(logicalDevice, swapchain, ref actualImageCount, pointer);
        }

        swapchainTextures = new VulkanSwapchainTexture[actualImageCount];

        for (var i = 0; i < actualImageCount; i++)
        {
            swapchainTextures[i] = new VulkanSwapchainTexture(
                session.Api,
                logicalDevice,
                session.DebugNames,
                images[i],
                rhiFormat,
                (int)extent.Width,
                (int)extent.Height,
                TextureUsage.ColorTarget | TextureUsage.CopyDestination,
                $"Swapchain image {i}");
        }

        // One signalling semaphore per image, not per frame in flight: present waits on it and there
        // is no way to know it has been unsignalled before the image comes round again.
        renderFinishedSemaphores = new VkSemaphore[actualImageCount];

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };

        for (var i = 0; i < actualImageCount; i++)
        {
            session.Api.CreateSemaphore(logicalDevice, in semaphoreInfo, null, out renderFinishedSemaphores[i]);
            session.DebugNames.SetName(renderFinishedSemaphores[i], $"Render finished {i}");
        }

        imageFrameSerials = new ulong[actualImageCount];

        EnsurePerFrameObjects();

        return true;
    }

    private bool TryChooseSurfaceFormat(out SurfaceFormatKHR format, out RhiFormat rhiFormat)
    {
        Debug.Assert(session is not null);

        var formats = GetSurfaceFormats();

        // A single entry of VK_FORMAT_UNDEFINED is the legacy "anything goes" answer.
        if (formats.Length == 0 || (formats.Length == 1 && formats[0].Format is Format.Undefined))
        {
            format = new SurfaceFormatKHR(Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr);
            rhiFormat = RhiFormat.B8G8R8A8_UNorm;
            return true;
        }

        foreach (var preferred in (ReadOnlySpan<Format>)[Format.B8G8R8A8Unorm, Format.R8G8B8A8Unorm])
        {
            foreach (var candidate in formats)
            {
                if (candidate.Format == preferred
                    && candidate.ColorSpace is ColorSpaceKHR.SpaceSrgbNonlinearKhr
                    && VulkanSwapchainTexture.TryToRhiFormat(candidate.Format, out rhiFormat))
                {
                    format = candidate;
                    return true;
                }
            }
        }

        // Anything else the contract can name. A surface format with no RhiFormat could never be a
        // render pass attachment, so it is rejected here rather than at the first draw.
        foreach (var candidate in formats)
        {
            if (VulkanSwapchainTexture.TryToRhiFormat(candidate.Format, out rhiFormat))
            {
                format = candidate;
                return true;
            }
        }

        format = default;
        rhiFormat = RhiFormat.Undefined;
        return false;
    }

    private unsafe SurfaceFormatKHR[] GetSurfaceFormats()
    {
        Debug.Assert(session is not null);

        uint count = 0;
        session.SurfaceApi.GetPhysicalDeviceSurfaceFormats(session.PhysicalDevice, surface, ref count, null);

        if (count == 0)
        {
            return [];
        }

        var formats = new SurfaceFormatKHR[count];

        fixed (SurfaceFormatKHR* pointer = formats)
        {
            session.SurfaceApi.GetPhysicalDeviceSurfaceFormats(session.PhysicalDevice, surface, ref count, pointer);
        }

        return formats;
    }

    private unsafe PresentModeKHR ChoosePresentMode()
    {
        Debug.Assert(session is not null);

        // FIFO is the only mode guaranteed to exist, and is what vsync means here.
        if (Vsync)
        {
            return PresentModeKHR.FifoKhr;
        }

        uint count = 0;
        session.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(session.PhysicalDevice, surface, ref count, null);

        if (count == 0)
        {
            return PresentModeKHR.FifoKhr;
        }

        var modes = new PresentModeKHR[count];

        fixed (PresentModeKHR* pointer = modes)
        {
            session.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(session.PhysicalDevice, surface, ref count, pointer);
        }

        if (Array.IndexOf(modes, PresentModeKHR.MailboxKhr) >= 0)
        {
            return PresentModeKHR.MailboxKhr;
        }

        if (Array.IndexOf(modes, PresentModeKHR.ImmediateKhr) >= 0)
        {
            return PresentModeKHR.ImmediateKhr;
        }

        return PresentModeKHR.FifoKhr;
    }

    private unsafe void EnsurePerFrameObjects()
    {
        Debug.Assert(session is not null);

        var framesInFlight = session.PresentDevice.FramesInFlight;

        if (imageAvailableSemaphores.Length == framesInFlight)
        {
            return;
        }

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };

        imageAvailableSemaphores = new VkSemaphore[framesInFlight];

        for (var i = 0; i < framesInFlight; i++)
        {
            session.Api.CreateSemaphore(session.LogicalDevice, in semaphoreInfo, null, out imageAvailableSemaphores[i]);
            session.DebugNames.SetName(imageAvailableSemaphores[i], $"Image available {i}");
        }
    }

    private void WaitDeviceIdle()
    {
        Debug.Assert(session is not null);

        using var _ = session.QueueLock.EnterScope();
        session.PresentDevice.WaitIdle();
    }

    private unsafe void DestroySwapchainObjects(SwapchainKHR target)
    {
        Debug.Assert(session is not null);

        var logicalDevice = session.LogicalDevice;

        foreach (var texture in swapchainTextures)
        {
            // Only the view; the images belong to the swapchain and go with it.
            texture.Dispose();
        }

        swapchainTextures = [];

        if (target.Handle != 0)
        {
            session.SwapchainApi.DestroySwapchain(logicalDevice, target, null);
        }

        // Destroyed only after the swapchain that presented with them, so no present is left waiting.
        foreach (var semaphore in renderFinishedSemaphores)
        {
            if (semaphore.Handle != 0)
            {
                session.Api.DestroySemaphore(logicalDevice, semaphore, null);
            }
        }

        renderFinishedSemaphores = [];
        imageFrameSerials = [];
    }

    private unsafe void Teardown()
    {
        using var _ = renderLock.EnterScope();

        if (torndown)
        {
            return;
        }

        torndown = true;

        if (session is not null)
        {
            WaitDeviceIdle();

            DestroySwapchainObjects(swapchain);
            swapchain = default;

            foreach (var semaphore in imageAvailableSemaphores)
            {
                if (semaphore.Handle != 0)
                {
                    session.Api.DestroySemaphore(session.LogicalDevice, semaphore, null);
                }
            }

            imageAvailableSemaphores = [];

            DestroySurface();

            session = null;
        }

        DestroyChildWindow();

        if (ownsSessionReference)
        {
            ownsSessionReference = false;
            VulkanPresentSession.Release();
        }
    }

    private void DestroyChildWindow()
    {
        if (!childWindow.IsNull)
        {
            PInvoke.DestroyWindow(childWindow);
            childWindow = HWND.Null;
        }
    }

    private unsafe void DestroySurface()
    {
        if (session is null || surface.Handle == 0)
        {
            return;
        }

        session.SurfaceApi.DestroySurface(session.Instance, surface, null);
        surface = default;
    }

    // SetWindowPos is not in the project's NativeMethods.txt, so it is imported directly rather than
    // through CsWin32 like the other window calls here.
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
