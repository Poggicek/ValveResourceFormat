using System.Threading;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Present;

/// <summary>
/// Which backend a viewer window should be brought up on, and which one it actually came up on.
/// </summary>
/// <remarks>
/// <para>
/// <b>OpenGL is the default and neither backend is temporary.</b> The contract says both ship
/// permanently: OpenGL is the parity oracle the golden image suite diffs Vulkan against, so it is not
/// scaffolding waiting to be deleted, and this is not a migration flag that eventually flips and gets
/// removed. It is a permanent choice, defaulting to the backend with years of shipped behaviour behind
/// it.
/// </para>
/// <para>
/// <b><see cref="Requested"/> and <see cref="Active"/> are deliberately separate.</b> Requesting Vulkan
/// on a machine whose driver is too old, or whose adapter cannot present, must not be a failure to
/// start &#8212; it must be a fall back to OpenGL with a reason recorded. Collapsing the two into one value
/// would make "the user asked for Vulkan" and "Vulkan is running" indistinguishable, and the difference
/// between them is exactly what a bug report needs to say.
/// </para>
/// <para>
/// This lives in the presentation layer because choosing a backend is choosing which window and
/// swapchain to create, which is presentation's job and no part of the RHI. Nothing here is Vulkan
/// specific beyond that.
/// </para>
/// </remarks>
public static class RhiBackendSelection
{
    /// <summary>The environment variable read at first use. <c>opengl</c>, <c>gl</c>, <c>vulkan</c> or
    /// <c>vk</c>, case insensitive; anything else is ignored.</summary>
    public const string EnvironmentVariableName = "VRF_RHI_BACKEND";

    private static readonly Lock StateLock = new();
    private static RhiBackend RequestedValue = ReadEnvironment();
    private static RhiBackend ActiveValue = RhiBackend.OpenGL;
    private static string? FallbackReasonValue;

    /// <summary>Gets the backend used when nothing selects one.</summary>
    public static RhiBackend Default => RhiBackend.OpenGL;

    /// <summary>Gets or sets the backend the next window should try to come up on.</summary>
    /// <remarks>Initialised from <see cref="EnvironmentVariableName"/>, and settable at runtime so a
    /// settings screen can change it. Existing windows keep the backend they were created with; a
    /// device and its resources cannot be moved between backends, so switching takes effect when a
    /// viewer is next opened.</remarks>
    public static RhiBackend Requested
    {
        get
        {
            using var _ = StateLock.EnterScope();
            return RequestedValue;
        }

        set
        {
            using var _ = StateLock.EnterScope();
            RequestedValue = value;
        }
    }

    /// <summary>Gets the backend that most recently came up successfully.</summary>
    public static RhiBackend Active
    {
        get
        {
            using var _ = StateLock.EnterScope();
            return ActiveValue;
        }
    }

    /// <summary>Gets why the last attempt at <see cref="Requested"/> fell back, or
    /// <see langword="null"/> when nothing has fallen back.</summary>
    public static string? FallbackReason
    {
        get
        {
            using var _ = StateLock.EnterScope();
            return FallbackReasonValue;
        }
    }

    /// <summary>Records that a window came up on a backend.</summary>
    /// <param name="backend">The backend that is now running.</param>
    public static void MarkActive(RhiBackend backend)
    {
        using var _ = StateLock.EnterScope();
        ActiveValue = backend;

        if (backend == RequestedValue)
        {
            FallbackReasonValue = null;
        }
    }

    /// <summary>Records that the requested backend could not be brought up and OpenGL was used instead.</summary>
    /// <param name="reason">Why it failed, for the log and for bug reports.</param>
    /// <returns><see cref="RhiBackend.OpenGL"/>, so a call site can <c>return</c> it directly.</returns>
    public static RhiBackend FallBackToOpenGL(string reason)
    {
        using var _ = StateLock.EnterScope();
        ActiveValue = RhiBackend.OpenGL;
        FallbackReasonValue = reason;
        return RhiBackend.OpenGL;
    }

    /// <summary>Parses a backend name.</summary>
    /// <param name="text">The name to parse.</param>
    /// <param name="backend">The parsed backend.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> named a backend.</returns>
    public static bool TryParse(string? text, out RhiBackend backend)
    {
        switch (text?.Trim().ToUpperInvariant())
        {
            case "OPENGL" or "GL":
                backend = RhiBackend.OpenGL;
                return true;

            case "VULKAN" or "VK":
                backend = RhiBackend.Vulkan;
                return true;

            default:
                backend = RhiBackend.OpenGL;
                return false;
        }
    }

    private static RhiBackend ReadEnvironment()
        => TryParse(Environment.GetEnvironmentVariable(EnvironmentVariableName), out var backend)
            ? backend
            : RhiBackend.OpenGL;
}
