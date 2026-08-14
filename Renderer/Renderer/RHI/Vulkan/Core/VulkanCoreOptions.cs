using Silk.NET.Vulkan;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Core;

/// <summary>
/// Creation parameters for a <see cref="VulkanCoreDevice"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MessageCallback"/> is a creation parameter rather than something attached afterwards
/// because <c>VK_EXT_debug_utils</c> installs its messenger as part of instance creation. There is no
/// supported way to add one later and see the messages the instance emitted while it was being built,
/// which is exactly when the most useful validation errors occur.
/// </para>
/// </remarks>
public sealed record VulkanCoreOptions
{
    /// <summary>Gets the application name reported to the driver and to graphics debuggers.</summary>
    public string ApplicationName { get; init; } = "Source 2 Viewer";

    /// <summary>Gets a value indicating whether <c>VK_LAYER_KHRONOS_validation</c> is requested.
    /// Ignored when the layer is not installed, which is the normal case on end user machines.</summary>
    public bool EnableValidation { get; init; }

    /// <summary>Gets a value indicating whether synchronization validation is requested on top of core
    /// validation. Debug builds only; it is expensive.</summary>
    /// <remarks>This is the option that pays for itself. The contract states that a barrier omitted
    /// while porting is a race that reproduces on one vendor's driver and not another's; synchronization
    /// validation is the only thing that reports the missing barrier deterministically, on any vendor,
    /// at the call that needed it. There is no equivalent to <c>DebugOutputSynchronous</c> to request:
    /// Vulkan invokes the messenger on the thread and inside the call that provoked the message already.</remarks>
    public bool EnableSynchronizationValidation { get; init; }

    /// <summary>Gets a value indicating whether informational validation messages are delivered.
    /// Off by default: the loader reports every layer and extension it considers at this level.</summary>
    public bool VerboseValidation { get; init; }

    /// <summary>Gets the number of frames that may be recorded before the first must have retired.
    /// Two is the standard latency and throughput compromise.</summary>
    public int FramesInFlight { get; init; } = 2;

    /// <summary>Gets extra instance extensions to enable, such as the platform surface extension the
    /// presentation layer needs. <c>VK_EXT_debug_utils</c> is added automatically.</summary>
    public IReadOnlyList<string> InstanceExtensions { get; init; } = [];

    /// <summary>Gets extra device extensions to enable, such as <c>VK_KHR_swapchain</c>. The core
    /// requires none of its own: everything it uses is Vulkan 1.3 core.</summary>
    public IReadOnlyList<string> DeviceExtensions { get; init; } = [];

    /// <summary>Gets the callback diagnostics are routed to, or <see langword="null"/> to drop them.</summary>
    public RhiMessageCallback? MessageCallback { get; init; }

    /// <summary>Gets a predicate that picks the physical device, or <see langword="null"/> to use the
    /// default scoring, which prefers a discrete GPU. The presentation layer overrides this when a
    /// device must also be able to present to a particular surface.</summary>
    public Func<PhysicalDevice, bool>? DeviceFilter { get; init; }
}

/// <summary>
/// Thrown when a Vulkan call fails or the machine cannot meet the renderer's requirements.
/// </summary>
public sealed class VulkanException : Exception
{
    /// <summary>Gets the result code that caused the failure, or <see langword="null"/> when the
    /// failure was a missing capability rather than a failed call.</summary>
    public Result? Result { get; }

    /// <summary>Initializes a new instance of the <see cref="VulkanException"/> class.</summary>
    public VulkanException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="VulkanException"/> class.</summary>
    /// <param name="message">The message.</param>
    public VulkanException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="VulkanException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public VulkanException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="VulkanException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="result">The failing result code.</param>
    public VulkanException(string message, Result result) : base($"{message}: {result}")
    {
        Result = result;
    }
}

/// <summary>
/// Turns Vulkan result codes into exceptions.
/// </summary>
public static class VulkanResultExtensions
{
    /// <summary>Throws when <paramref name="result"/> is not a success code.</summary>
    /// <param name="result">The result to check.</param>
    /// <param name="what">What was being attempted, used in the message.</param>
    /// <exception cref="VulkanException">The call failed.</exception>
    public static void Check(this Result result, string what)
    {
        if (result is not Silk.NET.Vulkan.Result.Success and not Silk.NET.Vulkan.Result.SuboptimalKhr)
        {
            throw new VulkanException(what, result);
        }
    }
}
