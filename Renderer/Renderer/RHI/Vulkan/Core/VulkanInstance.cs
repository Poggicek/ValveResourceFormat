using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Core;

/// <summary>
/// A Vulkan instance, its optional validation layer, and the <c>VK_EXT_debug_utils</c> messenger that
/// routes diagnostics to an <see cref="RhiMessageCallback"/>.
/// </summary>
/// <remarks>
/// The messenger is chained into <c>VkInstanceCreateInfo.pNext</c> as well as being created as a
/// standalone object. Without that, messages emitted during instance and device creation are lost,
/// and those are the ones that explain why creation failed.
/// </remarks>
public sealed unsafe class VulkanInstance : IDisposable
{
    private const string ValidationLayerName = "VK_LAYER_KHRONOS_validation";
    private const string DebugUtilsExtensionName = "VK_EXT_debug_utils";
    private const string ValidationFeaturesExtensionName = "VK_EXT_validation_features";

    private readonly RhiMessageCallback? MessageCallback;
    private readonly DebugUtilsMessengerCallbackFunctionEXT? CallbackDelegate;
    private DebugUtilsMessengerEXT Messenger;
    private int ErrorCountValue;
    private int WarningCountValue;
    private int ValidationErrorCountValue;
    private int ValidationWarningCountValue;
    private bool Disposed;

    /// <summary>Gets the Vulkan entry points.</summary>
    public Vk Api { get; }

    /// <summary>Gets the instance handle.</summary>
    public Instance Handle { get; }

    /// <summary>Gets the <c>VK_EXT_debug_utils</c> entry points, or <see langword="null"/> when the
    /// extension is not available.</summary>
    public ExtDebugUtils? DebugUtils { get; }

    /// <summary>Gets a value indicating whether the validation layer is actually loaded. False when
    /// it was requested but is not installed, which is the normal case outside a development machine.</summary>
    public bool ValidationEnabled { get; }

    /// <summary>Gets the number of error-severity messages seen so far, from every source.</summary>
    public int ErrorCount => Volatile.Read(ref ErrorCountValue);

    /// <summary>Gets the number of warning-severity messages seen so far, from every source.</summary>
    public int WarningCount => Volatile.Read(ref WarningCountValue);

    /// <summary>Gets the number of error-severity messages attributable to this application's use of
    /// the API, rather than to the loader's view of the machine.</summary>
    /// <remarks>
    /// The distinction matters in practice. A consumer machine routinely carries a dozen third-party
    /// overlay layers from capture software, storefronts and recording tools, and the loader reports
    /// their stale manifests and version mismatches at error and warning severity through the same
    /// messenger. Those messages carry the <c>General</c> type; anything the renderer itself did wrong
    /// carries <c>Validation</c> or <c>Performance</c>. Only the latter is a defect in this code, so
    /// only the latter is what a test may hold to zero. Counting all of them instead would make the
    /// suite pass or fail based on what the developer happens to have installed.
    /// </remarks>
    public int ValidationErrorCount => Volatile.Read(ref ValidationErrorCountValue);

    /// <summary>Gets the number of warning-severity messages attributable to this application's use of
    /// the API. See <see cref="ValidationErrorCount"/>.</summary>
    public int ValidationWarningCount => Volatile.Read(ref ValidationWarningCountValue);

    /// <summary>Creates an instance.</summary>
    /// <param name="options">Creation parameters.</param>
    /// <exception cref="VulkanException">The instance could not be created.</exception>
    public VulkanInstance(VulkanCoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        MessageCallback = options.MessageCallback;
        Api = Vk.GetApi();

        var availableLayers = EnumerateLayers();
        var availableExtensions = EnumerateExtensions();

        var wantValidation = options.EnableValidation && availableLayers.Contains(ValidationLayerName);
        ValidationEnabled = wantValidation;

        var extensions = new List<string>(options.InstanceExtensions);

        var hasDebugUtils = availableExtensions.Contains(DebugUtilsExtensionName);

        if (hasDebugUtils && !extensions.Contains(DebugUtilsExtensionName))
        {
            extensions.Add(DebugUtilsExtensionName);
        }

        var wantSyncValidation = wantValidation
            && options.EnableSynchronizationValidation
            && availableExtensions.Contains(ValidationFeaturesExtensionName);

        if (wantSyncValidation && !extensions.Contains(ValidationFeaturesExtensionName))
        {
            extensions.Add(ValidationFeaturesExtensionName);
        }

        foreach (var required in options.InstanceExtensions)
        {
            if (!availableExtensions.Contains(required))
            {
                throw new VulkanException($"Required Vulkan instance extension '{required}' is not available.");
            }
        }

        if (hasDebugUtils && MessageCallback is not null)
        {
            CallbackDelegate = OnDebugMessage;
        }

        var appName = SilkMarshal.StringToPtr(options.ApplicationName);
        var engineName = SilkMarshal.StringToPtr("Source 2 Viewer Renderer");
        var extensionNames = SilkMarshal.StringArrayToPtr(extensions);
        var layerNames = wantValidation ? SilkMarshal.StringArrayToPtr(new[] { ValidationLayerName }) : 0;

        try
        {
            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)appName,
                ApplicationVersion = Vk.MakeVersion(1, 0, 0),
                PEngineName = (byte*)engineName,
                EngineVersion = Vk.MakeVersion(1, 0, 0),
                ApiVersion = Vk.Version13,
            };

            var createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = (byte**)extensionNames,
                EnabledLayerCount = wantValidation ? 1u : 0u,
                PpEnabledLayerNames = (byte**)layerNames,
            };

            // Chained so that messages raised while the instance itself is being created are delivered.
            var messengerInfo = BuildMessengerInfo(options.VerboseValidation);

            if (CallbackDelegate is not null)
            {
                createInfo.PNext = &messengerInfo;
            }

            var syncFeature = ValidationFeatureEnableEXT.SynchronizationValidationExt;
            var validationFeatures = new ValidationFeaturesEXT
            {
                SType = StructureType.ValidationFeaturesExt,
                EnabledValidationFeatureCount = 1,
                PEnabledValidationFeatures = &syncFeature,
            };

            if (wantSyncValidation)
            {
                validationFeatures.PNext = createInfo.PNext;
                createInfo.PNext = &validationFeatures;
            }

            Api.CreateInstance(&createInfo, null, out var instance).Check("vkCreateInstance");
            Handle = instance;
        }
        finally
        {
            SilkMarshal.Free(appName);
            SilkMarshal.Free(engineName);
            SilkMarshal.Free(extensionNames);

            if (layerNames != 0)
            {
                SilkMarshal.Free(layerNames);
            }
        }

        if (Api.TryGetInstanceExtension<ExtDebugUtils>(Handle, out var debugUtils))
        {
            DebugUtils = debugUtils;

            if (CallbackDelegate is not null)
            {
                var messengerInfo = BuildMessengerInfo(options.VerboseValidation);
                DebugUtils.CreateDebugUtilsMessenger(Handle, &messengerInfo, null, out Messenger)
                    .Check("vkCreateDebugUtilsMessengerEXT");
            }
        }
    }

    private DebugUtilsMessengerCreateInfoEXT BuildMessengerInfo(bool verbose)
    {
        var severity = DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt
            | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt;

        if (verbose)
        {
            severity |= DebugUtilsMessageSeverityFlagsEXT.InfoBitExt
                | DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt;
        }

        return new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = severity,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = CallbackDelegate is null ? default : new PfnDebugUtilsMessengerCallbackEXT(CallbackDelegate),
        };
    }

    private uint OnDebugMessage(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT types,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        var ourFault = (types & (DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
            | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt)) != 0;

        if ((severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0)
        {
            Interlocked.Increment(ref ErrorCountValue);

            if (ourFault)
            {
                Interlocked.Increment(ref ValidationErrorCountValue);
            }
        }
        else if ((severity & DebugUtilsMessageSeverityFlagsEXT.WarningBitExt) != 0)
        {
            Interlocked.Increment(ref WarningCountValue);

            if (ourFault)
            {
                Interlocked.Increment(ref ValidationWarningCountValue);
            }
        }

        var callback = MessageCallback;

        if (callback is null || data is null)
        {
            return Vk.False;
        }

        var mapped = (severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0
            ? RhiMessageSeverity.Error
            : (severity & DebugUtilsMessageSeverityFlagsEXT.WarningBitExt) != 0
                ? RhiMessageSeverity.Warning
                : RhiMessageSeverity.Info;

        callback(mapped, Describe(severity, types, data));

        // Never abort the provoking call: the GL path logs and continues too.
        return Vk.False;
    }

    private static string Describe(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT types,
        DebugUtilsMessengerCallbackDataEXT* data)
    {
        var severityText = (severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0 ? "High"
            : (severity & DebugUtilsMessageSeverityFlagsEXT.WarningBitExt) != 0 ? "Medium"
            : (severity & DebugUtilsMessageSeverityFlagsEXT.InfoBitExt) != 0 ? "Low"
            : "Notification";

        var typeText = (types & DebugUtilsMessageTypeFlagsEXT.ValidationBitExt) != 0 ? "Error"
            : (types & DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt) != 0 ? "Performance"
            : "Other";

        // Mirrors the "[{severity} {source} {type}] {message}" shape the GL callback already logs.
        var builder = new StringBuilder(256);
        builder.Append('[').Append(severityText).Append(" Vulkan ").Append(typeText).Append("] ");
        builder.Append(Marshal.PtrToStringUTF8((nint)data->PMessage) ?? string.Empty);

        // The GL callback annotates messages with the current program and framebuffer labels. The
        // Vulkan equivalent is the set of labelled objects the layer already attached to the message.
        if (data->ObjectCount > 0 && data->PObjects is not null)
        {
            builder.Append(" (");

            for (var i = 0u; i < data->ObjectCount; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                ref var obj = ref data->PObjects[i];
                var name = obj.PObjectName is null ? null : Marshal.PtrToStringUTF8((nint)obj.PObjectName);

                builder.Append(obj.ObjectType);

                if (!string.IsNullOrEmpty(name))
                {
                    builder.Append(" '").Append(name).Append('\'');
                }
                else
                {
                    builder.Append(' ').Append(obj.ObjectHandle);
                }
            }

            builder.Append(')');
        }

        return builder.ToString();
    }

    /// <summary>Resets the error and warning counters.</summary>
    /// <remarks>The smoke test brackets each stage with this so a failure names the stage that
    /// produced the message.</remarks>
    public void ResetMessageCounts()
    {
        Volatile.Write(ref ErrorCountValue, 0);
        Volatile.Write(ref WarningCountValue, 0);
        Volatile.Write(ref ValidationErrorCountValue, 0);
        Volatile.Write(ref ValidationWarningCountValue, 0);
    }

    private HashSet<string> EnumerateLayers()
    {
        uint count = 0;
        Api.EnumerateInstanceLayerProperties(ref count, null).Check("vkEnumerateInstanceLayerProperties");

        var properties = new LayerProperties[count];
        var result = new HashSet<string>(StringComparer.Ordinal);

        fixed (LayerProperties* p = properties)
        {
            Api.EnumerateInstanceLayerProperties(ref count, p).Check("vkEnumerateInstanceLayerProperties");

            for (var i = 0; i < count; i++)
            {
                var name = Marshal.PtrToStringUTF8((nint)p[i].LayerName);

                if (name is not null)
                {
                    result.Add(name);
                }
            }
        }

        return result;
    }

    private HashSet<string> EnumerateExtensions()
    {
        uint count = 0;
        Api.EnumerateInstanceExtensionProperties((byte*)null, ref count, null)
            .Check("vkEnumerateInstanceExtensionProperties");

        var properties = new ExtensionProperties[count];
        var result = new HashSet<string>(StringComparer.Ordinal);

        fixed (ExtensionProperties* p = properties)
        {
            Api.EnumerateInstanceExtensionProperties((byte*)null, ref count, p)
                .Check("vkEnumerateInstanceExtensionProperties");

            for (var i = 0; i < count; i++)
            {
                var name = Marshal.PtrToStringUTF8((nint)p[i].ExtensionName);

                if (name is not null)
                {
                    result.Add(name);
                }
            }
        }

        return result;
    }

    /// <summary>Destroys the messenger and the instance.</summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        if (Messenger.Handle != 0 && DebugUtils is not null)
        {
            DebugUtils.DestroyDebugUtilsMessenger(Handle, Messenger, null);
            Messenger = default;
        }

        DebugUtils?.Dispose();

        if (Handle.Handle != 0)
        {
            Api.DestroyInstance(Handle, null);
        }

        Api.Dispose();
    }
}
