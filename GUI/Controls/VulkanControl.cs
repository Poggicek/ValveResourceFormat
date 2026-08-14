using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using GUI.Utils;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using ImageLayout = Silk.NET.Vulkan.ImageLayout;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace GUI.Controls;

/// <summary>
/// The Vulkan objects a <see cref="VulkanControl"/> needs in order to own a swapchain.
/// </summary>
/// <remarks>
/// This is the seam the real device is injected through. <see cref="TemporaryVulkanDevice"/> implements
/// it today; the renderer's own Vulkan device implements it later without this control changing.
/// Everything here is shared between controls, so several tabs can each own a swapchain on one device.
/// </remarks>
public interface IVulkanPresentDevice
{
    /// <summary>Core Vulkan entry points.</summary>
    Vk Api { get; }

    /// <summary>Instance the presentation surfaces are created from.</summary>
    Instance Instance { get; }

    /// <summary>Physical device backing <see cref="Device"/>.</summary>
    PhysicalDevice PhysicalDevice { get; }

    /// <summary>Logical device the swapchains belong to.</summary>
    Device Device { get; }

    /// <summary>Index of the queue family used for both rendering and presentation.</summary>
    uint PresentQueueFamily { get; }

    /// <summary>Queue used for both rendering and presentation.</summary>
    Queue PresentQueue { get; }

    /// <summary>
    /// Guards <see cref="PresentQueue"/> and device-wide waits. Vulkan queues are not internally
    /// synchronized and every control shares this one queue.
    /// </summary>
    Lock QueueLock { get; }

    /// <summary><c>VK_KHR_surface</c> entry points.</summary>
    KhrSurface SurfaceApi { get; }

    /// <summary><c>VK_KHR_win32_surface</c> entry points.</summary>
    KhrWin32Surface Win32SurfaceApi { get; }

    /// <summary><c>VK_KHR_swapchain</c> entry points.</summary>
    KhrSwapchain SwapchainApi { get; }
}

/// <summary>
/// Scope returned by <see cref="VulkanControl.MakeCurrent"/>, the Vulkan counterpart of the OpenGL
/// path's context scope.
/// </summary>
/// <remarks>
/// Vulkan has no current-context concept, so this only takes the control's render lock. The shape is
/// deliberately identical to the OpenGL one so the shared render thread's call sites are unchanged.
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
/// </remarks>
public sealed partial class VulkanControl : Control
{
    /// <summary>Frames the CPU may run ahead of the GPU.</summary>
    private const int MaxFramesInFlight = 2;

    private const string ChildWindowClassName = "Source2ViewerVulkanSurface";

    private static readonly Lock WindowClassLock = new();
    private static bool windowClassRegistered;

    private readonly Lock renderLock;
    private readonly IVulkanPresentDevice? injectedDevice;

    private IVulkanPresentDevice? device;
    private bool ownsTemporaryDevice;

    private HWND childWindow;
    private SurfaceKHR surface;
    private SwapchainKHR swapchain;

    private Image[] swapchainImages = [];
    private VkSemaphore[] renderFinishedSemaphores = [];
    private Fence[] imagesInFlight = [];

    private readonly VkSemaphore[] imageAvailableSemaphores = new VkSemaphore[MaxFramesInFlight];
    private readonly Fence[] inFlightFences = new Fence[MaxFramesInFlight];
    private readonly CommandBuffer[] commandBuffers = new CommandBuffer[MaxFramesInFlight];
    private CommandPool commandPool;

    private Extent2D swapchainExtent;
    private int frameIndex;
    private bool swapchainDirty;
    private bool perFrameObjectsCreated;
    private bool torndown;

    // Written by the UI thread on resize, read by the render thread. Only a fallback: the surface's
    // reported current extent is authoritative on Win32, which is what makes DPI changes free.
    private volatile int lastClientWidth;
    private volatile int lastClientHeight;

    /// <summary>
    /// Colour the swapchain image is cleared to each frame. Distinct values make it obvious which tab
    /// a presented image belongs to.
    /// </summary>
    public ClearColorValue ClearColor { get; set; } = new(0.1f, 0.1f, 0.12f, 1f);

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

    /// <summary>Whether a swapchain currently exists and can be presented to.</summary>
    public bool HasSwapchain => swapchain.Handle != 0;

    /// <summary>Format of the current swapchain images; whatever renders into them must match it.</summary>
    public Format SwapchainFormat { get; private set; }

    /// <summary>Size of the current swapchain images, in pixels.</summary>
    public Extent2D SwapchainExtent => swapchainExtent;

    /// <summary>Number of frames successfully presented, for tests and diagnostics.</summary>
    public long PresentedFrameCount { get; private set; }

    /// <summary>Number of times the swapchain was rebuilt, for tests and diagnostics.</summary>
    public long SwapchainRecreateCount { get; private set; }

    /// <summary>
    /// Number of times acquire or present reported the swapchain out of date or suboptimal, for tests
    /// and diagnostics.
    /// </summary>
    public long OutOfDateCount { get; private set; }

    /// <summary>
    /// Constructs a control that presents onto its own child window.
    /// </summary>
    /// <param name="renderLock">
    /// Lock serialising this control's Vulkan work, held by the shared render thread while drawing and
    /// by the UI thread while creating or destroying the surface.
    /// </param>
    /// <param name="device">
    /// Device to present with. When <see langword="null"/> the temporary process-wide device is used;
    /// pass the renderer's device once it exists.
    /// </param>
    public VulkanControl(Lock renderLock, IVulkanPresentDevice? device = null)
    {
        // Not ArgumentNullException.ThrowIfNull: passing a Lock as object trips CS9216.
        this.renderLock = renderLock ?? throw new ArgumentNullException(nameof(renderLock));
        injectedDevice = device;

        SetStyle(ControlStyles.Opaque, true);
        SetStyle(ControlStyles.UserPaint, true);
        SetStyle(ControlStyles.AllPaintingInWmPaint, true);
        DoubleBuffered = false;
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

        device = injectedDevice ?? TemporaryVulkanDevice.Acquire();
        ownsTemporaryDevice = injectedDevice is null;

        var createInfo = new Win32SurfaceCreateInfoKHR
        {
            SType = StructureType.Win32SurfaceCreateInfoKhr,
            Hinstance = moduleHandle,
            Hwnd = childWindow,
        };

        var result = device.Win32SurfaceApi.CreateWin32Surface(device.Instance, in createInfo, null, out surface);

        if (result != Result.Success)
        {
            Log.Error(nameof(VulkanControl), $"vkCreateWin32SurfaceKHR failed: {result}");
            surface = default;
            return;
        }

        var supportResult = device.SurfaceApi.GetPhysicalDeviceSurfaceSupport(
            device.PhysicalDevice, device.PresentQueueFamily, surface, out var supported);

        if (supportResult != Result.Success || !supported)
        {
            Log.Error(nameof(VulkanControl), "The present queue family cannot present to this surface.");
            DestroySurface();
            return;
        }

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
    /// Acquires, clears and presents one swapchain image. Called by the shared render thread.
    /// </summary>
    /// <returns><see langword="true"/> when an image was presented this call.</returns>
    public unsafe bool DrawFrame()
    {
        using var _ = renderLock.EnterScope();

        if (torndown || device is null || surface.Handle == 0)
        {
            return false;
        }

        if (!EnsureSwapchain())
        {
            // Minimised, or the swapchain could not be built at the current size.
            return false;
        }

        var vk = device.Api;
        var logicalDevice = device.Device;
        var fence = inFlightFences[frameIndex];

        vk.WaitForFences(logicalDevice, 1, in fence, true, ulong.MaxValue);

        uint imageIndex;
        var acquire = device.SwapchainApi.AcquireNextImage(
            logicalDevice, swapchain, ulong.MaxValue, imageAvailableSemaphores[frameIndex], default, &imageIndex);

        if (acquire is Result.ErrorOutOfDateKhr)
        {
            // The fence is still signalled and the semaphore was not touched, so the next frame is
            // free to start over on a rebuilt swapchain.
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

        // The previous user of this image may still be in flight in another frame slot.
        var previousFence = imagesInFlight[imageIndex];

        if (previousFence.Handle != 0 && previousFence.Handle != fence.Handle)
        {
            vk.WaitForFences(logicalDevice, 1, in previousFence, true, ulong.MaxValue);
        }

        imagesInFlight[imageIndex] = fence;

        RecordClear(commandBuffers[frameIndex], swapchainImages[imageIndex]);

        vk.ResetFences(logicalDevice, 1, in fence);

        var waitSemaphore = imageAvailableSemaphores[frameIndex];
        var signalSemaphore = renderFinishedSemaphores[imageIndex];
        var waitStage = PipelineStageFlags.TransferBit;
        var commandBuffer = commandBuffers[frameIndex];

        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore,
        };

        Result present;
        var localSwapchain = swapchain;

        using (device.QueueLock.EnterScope())
        {
            var submitted = vk.QueueSubmit(device.PresentQueue, 1, in submit, fence);

            if (submitted != Result.Success)
            {
                Log.Error(nameof(VulkanControl), $"vkQueueSubmit failed: {submitted}");
                return false;
            }

            var presentInfo = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &signalSemaphore,
                SwapchainCount = 1,
                PSwapchains = &localSwapchain,
                PImageIndices = &imageIndex,
            };

            present = device.SwapchainApi.QueuePresent(device.PresentQueue, in presentInfo);
        }

        if (present is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
        {
            swapchainDirty = true;
            OutOfDateCount++;
        }
        else if (present is not Result.Success)
        {
            Log.Error(nameof(VulkanControl), $"vkQueuePresentKHR failed: {present}");
            swapchainDirty = true;
        }

        frameIndex = (frameIndex + 1) % MaxFramesInFlight;
        PresentedFrameCount++;

        return true;
    }

    private unsafe void RecordClear(CommandBuffer commandBuffer, Image image)
    {
        Debug.Assert(device is not null);

        var vk = device.Api;

        vk.ResetCommandBuffer(commandBuffer, CommandBufferResetFlags.None);

        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        vk.BeginCommandBuffer(commandBuffer, in begin);

        var range = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };

        // Undefined -> TransferDst. The transition is placed in the transfer stage, which is the stage
        // the acquire semaphore is waited at, so it cannot run ahead of the presentation engine.
        var toTransferDst = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
            SrcAccessMask = AccessFlags.None,
            DstAccessMask = AccessFlags.TransferWriteBit,
        };

        vk.CmdPipelineBarrier(commandBuffer,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
            DependencyFlags.None, 0, null, 0, null, 1, in toTransferDst);

        var clearColor = ClearColor;
        vk.CmdClearColorImage(commandBuffer, image, ImageLayout.TransferDstOptimal, in clearColor, 1, in range);

        var toPresent = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.PresentSrcKhr,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.None,
        };

        vk.CmdPipelineBarrier(commandBuffer,
            PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit,
            DependencyFlags.None, 0, null, 0, null, 1, in toPresent);

        vk.EndCommandBuffer(commandBuffer);
    }

    /// <summary>
    /// Brings the swapchain up to date with the surface, rebuilding it when the window resized, the
    /// DPI changed, or a previous acquire or present reported it out of date.
    /// </summary>
    /// <returns><see langword="false"/> when there is nothing to draw into.</returns>
    private bool EnsureSwapchain()
    {
        Debug.Assert(device is not null);

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
        Debug.Assert(device is not null);

        var result = device.SurfaceApi.GetPhysicalDeviceSurfaceCapabilities(device.PhysicalDevice, surface, out var caps);

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
        Debug.Assert(device is not null);

        var vk = device.Api;
        var logicalDevice = device.Device;

        const ImageUsageFlags requiredUsage = ImageUsageFlags.TransferDstBit;

        if ((caps.SupportedUsageFlags & requiredUsage) != requiredUsage)
        {
            Log.Error(nameof(VulkanControl), "The surface does not support being a transfer destination.");
            return false;
        }

        var format = ChooseSurfaceFormat();
        var presentMode = ChoosePresentMode();

        var imageCount = caps.MinImageCount + 1;

        if (caps.MaxImageCount > 0 && imageCount > caps.MaxImageCount)
        {
            imageCount = caps.MaxImageCount;
        }

        // Everything still referencing the old swapchain has to finish first. A device-wide wait is
        // heavier than waiting on this control's fences, but fences do not cover queued presents and
        // this only happens on resize.
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
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
            OldSwapchain = oldSwapchain,
        };

        var result = device.SwapchainApi.CreateSwapchain(logicalDevice, in createInfo, null, out var newSwapchain);

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
        swapchainDirty = false;
        SwapchainRecreateCount++;

        uint actualImageCount = 0;
        device.SwapchainApi.GetSwapchainImages(logicalDevice, swapchain, ref actualImageCount, null);

        swapchainImages = new Image[actualImageCount];

        fixed (Image* images = swapchainImages)
        {
            device.SwapchainApi.GetSwapchainImages(logicalDevice, swapchain, ref actualImageCount, images);
        }

        // One signalling semaphore per image, not per frame in flight: present waits on it and there
        // is no way to know it has been unsignalled before the image comes round again.
        renderFinishedSemaphores = new VkSemaphore[actualImageCount];

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };

        for (var i = 0; i < actualImageCount; i++)
        {
            vk.CreateSemaphore(logicalDevice, in semaphoreInfo, null, out renderFinishedSemaphores[i]);
        }

        imagesInFlight = new Fence[actualImageCount];
        frameIndex = 0;

        EnsurePerFrameObjects();

        return true;
    }

    private SurfaceFormatKHR ChooseSurfaceFormat()
    {
        Debug.Assert(device is not null);

        var formats = GetSurfaceFormats();

        if (formats.Length == 0)
        {
            return new SurfaceFormatKHR(Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr);
        }

        foreach (var candidate in formats)
        {
            if (candidate.Format is Format.B8G8R8A8Unorm && candidate.ColorSpace is ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                return candidate;
            }
        }

        foreach (var candidate in formats)
        {
            if (candidate.Format is Format.R8G8B8A8Unorm && candidate.ColorSpace is ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                return candidate;
            }
        }

        // A single entry of VK_FORMAT_UNDEFINED means anything goes.
        if (formats.Length == 1 && formats[0].Format is Format.Undefined)
        {
            return new SurfaceFormatKHR(Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr);
        }

        return formats[0];
    }

    private unsafe SurfaceFormatKHR[] GetSurfaceFormats()
    {
        Debug.Assert(device is not null);

        uint count = 0;
        device.SurfaceApi.GetPhysicalDeviceSurfaceFormats(device.PhysicalDevice, surface, ref count, null);

        if (count == 0)
        {
            return [];
        }

        var formats = new SurfaceFormatKHR[count];

        fixed (SurfaceFormatKHR* pointer = formats)
        {
            device.SurfaceApi.GetPhysicalDeviceSurfaceFormats(device.PhysicalDevice, surface, ref count, pointer);
        }

        return formats;
    }

    private unsafe PresentModeKHR ChoosePresentMode()
    {
        Debug.Assert(device is not null);

        // FIFO is the only mode guaranteed to exist, and is what vsync means here.
        if (Vsync)
        {
            return PresentModeKHR.FifoKhr;
        }

        uint count = 0;
        device.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(device.PhysicalDevice, surface, ref count, null);

        if (count == 0)
        {
            return PresentModeKHR.FifoKhr;
        }

        var modes = new PresentModeKHR[count];

        fixed (PresentModeKHR* pointer = modes)
        {
            device.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(device.PhysicalDevice, surface, ref count, pointer);
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
        Debug.Assert(device is not null);

        if (perFrameObjectsCreated)
        {
            return;
        }

        var vk = device.Api;
        var logicalDevice = device.Device;

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = device.PresentQueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };

        vk.CreateCommandPool(logicalDevice, in poolInfo, null, out commandPool);

        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = MaxFramesInFlight,
        };

        fixed (CommandBuffer* buffers = commandBuffers)
        {
            vk.AllocateCommandBuffers(logicalDevice, in allocateInfo, buffers);
        }

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };

        // Signalled, so the very first frame does not wait on a fence nothing will ever signal.
        var fenceInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit,
        };

        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            vk.CreateSemaphore(logicalDevice, in semaphoreInfo, null, out imageAvailableSemaphores[i]);
            vk.CreateFence(logicalDevice, in fenceInfo, null, out inFlightFences[i]);
        }

        perFrameObjectsCreated = true;
    }

    private void WaitDeviceIdle()
    {
        Debug.Assert(device is not null);

        using var _ = device.QueueLock.EnterScope();
        device.Api.DeviceWaitIdle(device.Device);
    }

    private unsafe void DestroySwapchainObjects(SwapchainKHR target)
    {
        Debug.Assert(device is not null);

        var vk = device.Api;
        var logicalDevice = device.Device;

        if (target.Handle != 0)
        {
            device.SwapchainApi.DestroySwapchain(logicalDevice, target, null);
        }

        // Destroyed only after the swapchain that presented with them, so no present is left waiting.
        foreach (var semaphore in renderFinishedSemaphores)
        {
            if (semaphore.Handle != 0)
            {
                vk.DestroySemaphore(logicalDevice, semaphore, null);
            }
        }

        renderFinishedSemaphores = [];
        imagesInFlight = [];
        swapchainImages = [];
    }

    private unsafe void Teardown()
    {
        using var _ = renderLock.EnterScope();

        if (torndown)
        {
            return;
        }

        torndown = true;

        if (device is not null)
        {
            var vk = device.Api;
            var logicalDevice = device.Device;

            WaitDeviceIdle();

            DestroySwapchainObjects(swapchain);
            swapchain = default;

            if (perFrameObjectsCreated)
            {
                for (var i = 0; i < MaxFramesInFlight; i++)
                {
                    if (imageAvailableSemaphores[i].Handle != 0)
                    {
                        vk.DestroySemaphore(logicalDevice, imageAvailableSemaphores[i], null);
                        imageAvailableSemaphores[i] = default;
                    }

                    if (inFlightFences[i].Handle != 0)
                    {
                        vk.DestroyFence(logicalDevice, inFlightFences[i], null);
                        inFlightFences[i] = default;
                    }
                }

                if (commandPool.Handle != 0)
                {
                    vk.DestroyCommandPool(logicalDevice, commandPool, null);
                    commandPool = default;
                }

                perFrameObjectsCreated = false;
            }

            DestroySurface();

            device = null;
        }

        if (!childWindow.IsNull)
        {
            PInvoke.DestroyWindow(childWindow);
            childWindow = HWND.Null;
        }

        if (ownsTemporaryDevice)
        {
            ownsTemporaryDevice = false;
            TemporaryVulkanDevice.Release();
        }
    }

    private unsafe void DestroySurface()
    {
        if (device is null || surface.Handle == 0)
        {
            return;
        }

        device.SurfaceApi.DestroySurface(device.Instance, surface, null);
        surface = default;
    }

    // SetWindowPos is not in the project's NativeMethods.txt, so it is imported directly rather than
    // through CsWin32 like the other window calls here.
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}

/// <summary>
/// TEMPORARY. A minimal shared <c>VkInstance</c> and <c>VkDevice</c> that exists only so the swapchain
/// has something to be created from.
/// </summary>
/// <remarks>
/// <para>
/// This is scaffolding, not the renderer's device. It picks one queue family that can both render and
/// present, enables nothing beyond <c>VK_KHR_swapchain</c>, and exposes no resource creation at all.
/// Delete it once the real device implements <see cref="IVulkanPresentDevice"/>: pass that device to
/// the <see cref="VulkanControl"/> constructor and every reference here disappears with it.
/// </para>
/// <para>
/// Reference counted, so all tabs share one instance and device, and the last tab to close tears them
/// down.
/// </para>
/// </remarks>
internal sealed class TemporaryVulkanDevice : IVulkanPresentDevice, IDisposable
{
    private const string ValidationLayerName = "VK_LAYER_KHRONOS_validation";

    private static readonly Lock SharedLock = new();
    private static TemporaryVulkanDevice? shared;
    private static int referenceCount;

    // The Vk object owns the loaded vulkan-1 module and is shared by every device this process
    // creates. Disposing it unloads that module, which breaks any device created afterwards, so it is
    // created once and deliberately never disposed.
    private static Vk? sharedApi;

    /// <summary>Validation messages seen since process start, for tests to assert on.</summary>
    internal static int ValidationMessageCount;

    /// <summary>
    /// Whether the validation layer was found and enabled on the most recently created device. Without
    /// this, a run reporting no validation messages proves nothing.
    /// </summary>
    internal static bool ValidationLayerActive;

    private DebugUtilsMessengerEXT debugMessenger;
    private ExtDebugUtils? debugUtils;

    public Vk Api { get; }
    public Instance Instance { get; private set; }
    public PhysicalDevice PhysicalDevice { get; private set; }
    public Device Device { get; private set; }
    public uint PresentQueueFamily { get; private set; }
    public Queue PresentQueue { get; private set; }
    public Lock QueueLock { get; } = new();
    public KhrSurface SurfaceApi { get; private set; } = null!;
    public KhrWin32Surface Win32SurfaceApi { get; private set; } = null!;
    public KhrSwapchain SwapchainApi { get; private set; } = null!;

    private TemporaryVulkanDevice(Vk api)
    {
        Api = api;
    }

    /// <summary>Returns the shared temporary device, creating it on first use.</summary>
    internal static IVulkanPresentDevice Acquire()
    {
        using var _ = SharedLock.EnterScope();

        if (shared is null)
        {
            sharedApi ??= Vk.GetApi();

            var created = new TemporaryVulkanDevice(sharedApi);
            created.Initialize();
            shared = created;
        }

        referenceCount++;

        return shared;
    }

    /// <summary>
    /// Drops a control's reference. The instance and device deliberately outlive the last control:
    /// Silk.NET's <see cref="Vk"/> caches instance-level entry points, so destroying and recreating the
    /// instance underneath it hands out physical devices the loader then rejects. A real device is
    /// process-lifetime anyway; <see cref="Shutdown"/> is the one place it goes away.
    /// </summary>
    internal static void Release()
    {
        using var _ = SharedLock.EnterScope();

        if (referenceCount > 0)
        {
            referenceCount--;
        }
    }

    /// <summary>
    /// Destroys the temporary instance and device. Call once while shutting the application down, after
    /// every <see cref="VulkanControl"/> is disposed; the validation layer reports anything still alive
    /// as a leak at this point.
    /// </summary>
    internal static void Shutdown()
    {
        using var _ = SharedLock.EnterScope();

        if (shared is null)
        {
            return;
        }

        if (referenceCount != 0)
        {
            Log.Warn(nameof(TemporaryVulkanDevice), $"Shutting down with {referenceCount} live controls.");
        }

        shared.Dispose();
        shared = null;
        referenceCount = 0;

        // A new Vk is needed for any future instance, for the caching reason described in Release.
        sharedApi = null;
    }

    private unsafe void Initialize()
    {
        var useValidation = ShouldUseValidation() && HasValidationLayer();
        var hasDebugUtils = HasInstanceExtension(ExtDebugUtils.ExtensionName);

        var extensions = new List<string>
        {
            KhrSurface.ExtensionName,
            KhrWin32Surface.ExtensionName,
        };

        if (useValidation && hasDebugUtils)
        {
            extensions.Add(ExtDebugUtils.ExtensionName);
        }

        var applicationName = SilkMarshal.StringToPtr("Source 2 Viewer");
        var engineName = SilkMarshal.StringToPtr("Source 2 Viewer");
        var extensionNames = SilkMarshal.StringArrayToPtr(extensions);
        var layerNames = useValidation ? SilkMarshal.StringArrayToPtr(new[] { ValidationLayerName }) : 0;

        try
        {
            var applicationInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)applicationName,
                ApplicationVersion = Vk.MakeVersion(1, 0, 0),
                PEngineName = (byte*)engineName,
                EngineVersion = Vk.MakeVersion(1, 0, 0),
                ApiVersion = Vk.Version13,
            };

            var instanceInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &applicationInfo,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = (byte**)extensionNames,
                EnabledLayerCount = useValidation ? 1u : 0u,
                PpEnabledLayerNames = useValidation ? (byte**)layerNames : null,
            };

            var result = Api.CreateInstance(in instanceInfo, null, out var instance);

            if (result != Result.Success)
            {
                throw new InvalidOperationException($"vkCreateInstance failed: {result}");
            }

            Instance = instance;
            ValidationLayerActive = useValidation;
        }
        finally
        {
            SilkMarshal.Free(applicationName);
            SilkMarshal.Free(engineName);
            SilkMarshal.Free(extensionNames);

            if (layerNames != 0)
            {
                SilkMarshal.Free(layerNames);
            }
        }

        if (!Api.TryGetInstanceExtension(Instance, out KhrSurface surfaceApi))
        {
            throw new InvalidOperationException("VK_KHR_surface is unavailable.");
        }

        SurfaceApi = surfaceApi;

        if (!Api.TryGetInstanceExtension(Instance, out KhrWin32Surface win32SurfaceApi))
        {
            throw new InvalidOperationException("VK_KHR_win32_surface is unavailable.");
        }

        Win32SurfaceApi = win32SurfaceApi;

        if (useValidation && hasDebugUtils && Api.TryGetInstanceExtension(Instance, out ExtDebugUtils utils))
        {
            debugUtils = utils;
            CreateDebugMessenger();
        }

        SelectPhysicalDevice();
        CreateLogicalDevice();
    }

    private static bool ShouldUseValidation()
    {
#if DEBUG
        return true;
#else
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VRF_VULKAN_VALIDATION"));
#endif
    }

    private unsafe bool HasValidationLayer()
    {
        uint count = 0;
        Api.EnumerateInstanceLayerProperties(ref count, null);

        if (count == 0)
        {
            return false;
        }

        var layers = new LayerProperties[count];

        fixed (LayerProperties* pointer = layers)
        {
            Api.EnumerateInstanceLayerProperties(ref count, pointer);

            for (var i = 0; i < count; i++)
            {
                if (SilkMarshal.PtrToString((nint)pointer[i].LayerName) == ValidationLayerName)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private unsafe bool HasInstanceExtension(string name)
    {
        uint count = 0;
        Api.EnumerateInstanceExtensionProperties((byte*)null, ref count, null);

        if (count == 0)
        {
            return false;
        }

        var extensions = new ExtensionProperties[count];

        fixed (ExtensionProperties* pointer = extensions)
        {
            Api.EnumerateInstanceExtensionProperties((byte*)null, ref count, pointer);

            for (var i = 0; i < count; i++)
            {
                if (SilkMarshal.PtrToString((nint)pointer[i].ExtensionName) == name)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private unsafe void CreateDebugMessenger()
    {
        Debug.Assert(debugUtils is not null);

        var info = new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(DebugCallback),
        };

        debugUtils.CreateDebugUtilsMessenger(Instance, in info, null, out debugMessenger);
    }

    private static unsafe uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT messageType,
        DebugUtilsMessengerCallbackDataEXT* callbackData,
        void* userData)
    {
        var message = SilkMarshal.PtrToString((nint)callbackData->PMessage) ?? string.Empty;

        Interlocked.Increment(ref ValidationMessageCount);

        if ((severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0)
        {
            Log.Error("Vulkan", message);
        }
        else
        {
            Log.Warn("Vulkan", message);
        }

        return Vk.False;
    }

    private unsafe void SelectPhysicalDevice()
    {
        var devices = Api.GetPhysicalDevices(Instance);
        PhysicalDevice best = default;
        uint bestFamily = 0;
        var bestScore = -1;

        foreach (var candidate in devices)
        {
            if (!TryFindPresentQueueFamily(candidate, out var family))
            {
                continue;
            }

            if (!HasSwapchainExtension(candidate))
            {
                continue;
            }

            Api.GetPhysicalDeviceProperties(candidate, out var properties);

            var score = properties.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 3,
                PhysicalDeviceType.IntegratedGpu => 2,
                PhysicalDeviceType.VirtualGpu => 1,
                _ => 0,
            };

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
                bestFamily = family;
            }
        }

        if (bestScore < 0)
        {
            throw new InvalidOperationException("No Vulkan device that can present to a window was found.");
        }

        PhysicalDevice = best;
        PresentQueueFamily = bestFamily;
    }

    private unsafe bool TryFindPresentQueueFamily(PhysicalDevice candidate, out uint family)
    {
        uint count = 0;
        Api.GetPhysicalDeviceQueueFamilyProperties(candidate, ref count, null);

        var families = new QueueFamilyProperties[count];

        fixed (QueueFamilyProperties* pointer = families)
        {
            Api.GetPhysicalDeviceQueueFamilyProperties(candidate, ref count, pointer);
        }

        for (var i = 0u; i < count; i++)
        {
            if ((families[i].QueueFlags & QueueFlags.GraphicsBit) == 0)
            {
                continue;
            }

            // Checked without a surface, which is exactly why this can run before any control exists.
            if (Win32SurfaceApi.GetPhysicalDeviceWin32PresentationSupport(candidate, i))
            {
                family = i;
                return true;
            }
        }

        family = 0;
        return false;
    }

    private unsafe bool HasSwapchainExtension(PhysicalDevice candidate)
    {
        uint count = 0;
        Api.EnumerateDeviceExtensionProperties(candidate, (byte*)null, ref count, null);

        if (count == 0)
        {
            return false;
        }

        var extensions = new ExtensionProperties[count];

        fixed (ExtensionProperties* pointer = extensions)
        {
            Api.EnumerateDeviceExtensionProperties(candidate, (byte*)null, ref count, pointer);

            for (var i = 0; i < count; i++)
            {
                if (SilkMarshal.PtrToString((nint)pointer[i].ExtensionName) == KhrSwapchain.ExtensionName)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private unsafe void CreateLogicalDevice()
    {
        var priority = 1f;

        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = PresentQueueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        var extensionNames = SilkMarshal.StringArrayToPtr(new[] { KhrSwapchain.ExtensionName });

        try
        {
            Api.GetPhysicalDeviceFeatures(PhysicalDevice, out var supported);

            // Overlay layers (Steam, OBS, RTSS and friends) inject themselves into whatever device the
            // application creates and render with it. Several of them create anisotropic samplers
            // without checking, which the validation layer then reports against us. Enabling the
            // feature when the device has it costs nothing and keeps that output clean.
            var features = new PhysicalDeviceFeatures
            {
                SamplerAnisotropy = supported.SamplerAnisotropy,
            };

            var deviceInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueInfo,
                EnabledExtensionCount = 1,
                PpEnabledExtensionNames = (byte**)extensionNames,
                PEnabledFeatures = &features,
            };

            var result = Api.CreateDevice(PhysicalDevice, in deviceInfo, null, out var logicalDevice);

            if (result != Result.Success)
            {
                throw new InvalidOperationException($"vkCreateDevice failed: {result}");
            }

            Device = logicalDevice;
        }
        finally
        {
            SilkMarshal.Free(extensionNames);
        }

        if (!Api.TryGetDeviceExtension(Instance, Device, out KhrSwapchain swapchainApi))
        {
            throw new InvalidOperationException("VK_KHR_swapchain is unavailable.");
        }

        SwapchainApi = swapchainApi;

        Api.GetDeviceQueue(Device, PresentQueueFamily, 0, out var queue);
        PresentQueue = queue;
    }

    public unsafe void Dispose()
    {
        if (Device.Handle != 0)
        {
            Api.DeviceWaitIdle(Device);
            Api.DestroyDevice(Device, null);
            Device = default;
        }

        if (debugMessenger.Handle != 0 && debugUtils is not null)
        {
            debugUtils.DestroyDebugUtilsMessenger(Instance, debugMessenger, null);
            debugMessenger = default;
        }

        if (Instance.Handle != 0)
        {
            // Any object still alive here is reported as a leak by the validation layer.
            Api.DestroyInstance(Instance, null);
            Instance = default;
        }

        // The extension wrappers and the Vk object are not disposed: they all share the loaded
        // vulkan-1 module, and unloading it makes the next device this process creates crash.
        debugUtils = null;
        SwapchainApi = null!;
        Win32SurfaceApi = null!;
        SurfaceApi = null!;
    }
}
