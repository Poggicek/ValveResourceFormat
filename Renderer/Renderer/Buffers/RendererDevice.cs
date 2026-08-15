using ValveResourceFormat.Renderer.RHI;

namespace ValveResourceFormat.Renderer;

/// <summary>
/// The <see cref="IDevice"/> that renderer-owned resources allocate through when their call site cannot
/// name one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than a parameter.</b> <see cref="RenderTexture"/> and
/// <see cref="Buffers.Buffer"/> are constructed from roughly twenty call sites spread across the
/// renderer, the post-process chain and the GUI. Threading a device through every one of them is the
/// right end state and is where this is going, but those signatures cannot all change at once. This is
/// the seam that lets allocation move onto the device now, per resource type, without a flag day.
/// </para>
/// <para>
/// <b>It is a fallback, not the interface.</b> Every allocating API that reads this also takes an
/// explicit <see cref="IDevice"/>, and passing one always wins. A call site that can name its device
/// should; this only answers for the ones that cannot yet.
/// </para>
/// <para>
/// <b>Why a single static is safe enough on OpenGL and not on Vulkan.</b> The GUI can hold several
/// viewers, each with its own <see cref="RendererContext"/> and its own device, and this remembers only
/// the most recently constructed one. On OpenGL that is harmless: every GL entry point acts on whichever
/// context is current on the calling thread, so holding the "wrong" <c>GLDevice</c> still allocates in
/// the right place. On Vulkan it would not be harmless, which is the concrete reason the explicit
/// parameter has to win the race against multi-viewer Vulkan. <see cref="RendererDevice.Current"/> is
/// read at each allocation rather than captured, so a resource never outlives its lookup.
/// </para>
/// </remarks>
public static class RendererDevice
{
    /// <summary>Follows a chain of <see cref="IDeviceDecorator"/> wrappers to the device underneath.</summary>
    /// <param name="device">The device to unwrap, which may be <see langword="null"/> or not a decorator.</param>
    /// <returns>The innermost device, or <see langword="null"/> when <paramref name="device"/> was.</returns>
    /// <remarks>Loops rather than recursing once, so a device wrapped twice &#8212; a census over a
    /// recorder, say &#8212; still resolves.</remarks>
    public static IDevice? Unwrap(IDevice? device)
    {
        while (device is IDeviceDecorator decorator)
        {
            device = decorator.Inner;
        }

        return device;
    }

    // Weak, because this must not be what keeps a closed viewer's context alive. Most recent last.
    private static readonly List<WeakReference<RendererContext>> Published = [];

    /// <summary>
    /// Records the context whose device answers <see cref="Current"/>.
    /// </summary>
    /// <param name="context">The context being constructed.</param>
    /// <remarks>Called while <paramref name="context"/> is still being constructed, so its
    /// <see cref="RendererContext.Device"/> is not assigned yet. The context is held rather than the
    /// device for exactly that reason: the device is read later, at allocation time, once the
    /// presentation layer has supplied it.</remarks>
    public static void Publish(RendererContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (Published)
        {
            Published.RemoveAll(static entry => !entry.TryGetTarget(out _));
            Published.Add(new WeakReference<RendererContext>(context));
        }
    }

    /// <summary>Gets the device to allocate through, or <see langword="null"/> when none has been
    /// assigned yet.</summary>
    /// <remarks>
    /// <para>
    /// <see langword="null"/> means the legacy direct-OpenGL path. It is a real state and not an error:
    /// resources are created during startup before the presentation layer has brought a device up, and
    /// the tools that use the renderer without a device at all depend on it.
    /// </para>
    /// <para>
    /// The most recently published context wins, except that a context running the backend the process
    /// was asked for beats one that is not. That tie-break matters whenever two contexts are live at
    /// once: a resource allocated on the wrong backend's device is not a near miss, it is unusable. It is
    /// still only a tie-break between ambiguous answers, and the way to not be ambiguous is for the call
    /// site to name its device.
    /// </para>
    /// </remarks>
    public static IDevice? Current
    {
        get
        {
            IDevice? mostRecent = null;

            lock (Published)
            {
                for (var i = Published.Count - 1; i >= 0; i--)
                {
                    if (!Published[i].TryGetTarget(out var context) || context.Device is not { } device)
                    {
                        continue;
                    }

                    if (device.Backend == RHI.Vulkan.Present.RhiBackendSelection.Requested)
                    {
                        return device;
                    }

                    mostRecent ??= device;
                }
            }

            return mostRecent;
        }
    }

    /// <summary>Gets the device to allocate through, preferring an explicitly supplied one.</summary>
    /// <param name="device">The device the call site named, or <see langword="null"/>.</param>
    /// <returns><paramref name="device"/> when it is not <see langword="null"/>, otherwise
    /// <see cref="Current"/>.</returns>
    public static IDevice? Resolve(IDevice? device) => device ?? Current;

    /// <summary>
    /// Gets a value indicating whether direct OpenGL calls are legal right now.
    /// </summary>
    /// <param name="device">The device the call site named, or <see langword="null"/> to use
    /// <see cref="Current"/>.</param>
    /// <returns><see langword="true"/> on an OpenGL device and when there is no device at all.</returns>
    /// <remarks>The no-device case answers <see langword="true"/> because the legacy path is OpenGL: a
    /// renderer running before a device exists is running on the GL context it was always running on.</remarks>
    public static bool IsOpenGL(IDevice? device = null)
    {
        var resolved = Resolve(device);

        return resolved is null || resolved.Backend == RhiBackend.OpenGL;
    }
}

/// <summary>
/// Implemented by an <see cref="IDevice"/> that wraps another one and forwards to it.
/// </summary>
/// <remarks>
/// <para>
/// Almost nothing needs this: the RHI is an interface precisely so callers do not care which device is
/// underneath. The exception is work that has no expression in the contract and must reach the concrete
/// backend &#8212; publishing an uploaded texture for sampling is the one that exists today, because the
/// barrier has to ride the backend's own load-time command buffer rather than a frame's command list.
/// </para>
/// <para>
/// A decorator that does not implement this is invisible to <see cref="RendererDevice.Unwrap"/>, and
/// anything looking for the concrete device behind it will quietly decide there isn't one. That failure
/// is silent, which is why the interface is a single property with no behaviour: implementing it should
/// never be a decision.
/// </para>
/// </remarks>
public interface IDeviceDecorator
{
    /// <summary>Gets the device this one forwards to.</summary>
    IDevice Inner { get; }
}
