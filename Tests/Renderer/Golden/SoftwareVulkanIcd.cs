using System.IO;
using System.Runtime.CompilerServices;

namespace Tests.Renderer.Golden
{
    /// <summary>
    /// Points the Vulkan loader at the vendored CPU driver, and records what it did so the run can say so
    /// out loud.
    ///
    /// <para><b>This exists because a Vulkan golden run bugchecked the machine it was run on.</b> The
    /// renderer currently records draws whose pipelines read descriptor sets that were never bound. That is
    /// undefined behaviour, and a kernel-mode display driver is within its rights to answer undefined
    /// behaviour by hanging; the recovery timeout took the box with it. None of that is fixed here — the
    /// port is what fixes it — so this removes the display driver from the blast radius instead. A CPU
    /// implementation that meets the same contract can throw, crash the test process, or draw the wrong
    /// pixels, and every one of those is a bug report rather than a reboot.</para>
    ///
    /// <para><b>Software is the default, and hardware is the thing you have to ask for.</b> That is the
    /// inversion the incident earns. <c>VRF_RHI_SOFTWARE=0</c> is the only route back to the real GPU and
    /// has to be typed on purpose; anything else — unset, <c>1</c>, <c>true</c> — keeps the run on the CPU
    /// device.</para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two mechanisms, not one.</b> Setting <c>VK_DRIVER_FILES</c> asks the loader nicely; it is how the
    /// CPU device is reached, but it is not what makes hardware unreachable. What makes hardware
    /// unreachable is <see cref="RequireCpuDevice"/>: after the device is created,
    /// <see cref="HeadlessVulkan"/> reads the adapter's own <c>VkPhysicalDeviceProperties.deviceType</c>
    /// back and tears the device down before a single command is submitted if it is anything but
    /// <c>CPU</c>. A loader that ignored the variable, a manifest that silently failed to parse, a future
    /// loader release that changes its precedence rules — all of them land on that check rather than on the
    /// display driver. The variable is a preference; the check is the guarantee.
    /// </para>
    /// <para>
    /// <b>An already-set <c>VK_DRIVER_FILES</c> is left alone.</b> Someone who set it meant it, and
    /// overwriting it would make the one probe that matters — point it at a file that does not exist and
    /// confirm the run dies rather than quietly finding the GPU — impossible to perform. The CPU check
    /// still applies in that case, so honouring the variable cannot be used to sneak onto hardware.
    /// </para>
    /// <para>
    /// <b>Implicit layers are switched off.</b> This machine has nine of them — capture hooks, two
    /// overlays, a frame counter, an AMD switchable-graphics shim — injected into every Vulkan process on
    /// the system. None of them has any business in a golden run, several are closed-source code that
    /// reaches for the display adapter, and any of them could put a message in the validation stream that
    /// the report would then attribute to the renderer.
    /// </para>
    /// <para>
    /// <b>Setting the variables from inside the process works.</b> The Windows Vulkan loader reads them
    /// with <c>GetEnvironmentVariableW</c> at instance creation, not from a CRT copy captured at startup,
    /// and .NET's <see cref="Environment.SetEnvironmentVariable(string, string)"/> writes through to the
    /// Win32 environment block. The proof is not the reasoning, though: it is the device name the run
    /// prints, which is checked against the type before any work is submitted.
    /// </para>
    /// </remarks>
    internal static class SoftwareVulkanIcd
    {
        /// <summary>Selects the software driver. Unset means the same as <c>1</c>; only <c>0</c> or
        /// <c>false</c> permits the hardware GPU.</summary>
        public const string EnvironmentVariable = "VRF_RHI_SOFTWARE";

        /// <summary>The loader variable naming the ICD manifests to use, to the exclusion of every
        /// registered driver.</summary>
        public const string DriverFilesVariable = "VK_DRIVER_FILES";

        /// <summary>The pre-1.3.207 spelling of <see cref="DriverFilesVariable"/>, set as well so an older
        /// loader on the machine behaves the same way.</summary>
        public const string LegacyDriverFilesVariable = "VK_ICD_FILENAMES";

        private const string LayersDisableVariable = "VK_LOADER_LAYERS_DISABLE";
        private const string InstanceLayersVariable = "VK_INSTANCE_LAYERS";

        /// <summary>The vendored driver directory. Deleting it is how this is uninstalled.</summary>
        public static string DirectoryPath { get; }
            = Path.GetFullPath(Path.Combine(SourceDirectory(), "..", "..", "SoftwareVulkan"));

        /// <summary>The ICD manifest the loader is pointed at.</summary>
        public static string ManifestPath { get; } = Path.Combine(DirectoryPath, "lvp_icd.x86_64.json");

        /// <summary>The driver the manifest names, checked as well as the manifest: a manifest without its
        /// library beside it is a broken drop, not a usable one.</summary>
        public static string DriverPath { get; } = Path.Combine(DirectoryPath, "vulkan_lvp.dll");

        /// <summary>Whether the driver has been fetched into <see cref="DirectoryPath"/>.</summary>
        public static bool IsVendored => File.Exists(ManifestPath) && File.Exists(DriverPath);

        /// <summary>
        /// Whether the run must abort unless the adapter reports itself as a CPU device. This is the
        /// guarantee that the hardware GPU cannot be reached, and it does not depend on the loader having
        /// honoured anything.
        /// </summary>
        public static bool RequireCpuDevice { get; private set; }

        /// <summary>What <see cref="Configure"/> did, for the run banner.</summary>
        public static string Status { get; private set; } = "not configured";

        /// <summary>
        /// Why the run cannot start at all, or <see langword="null"/>. Distinct from a device that failed to
        /// come up: this is a mistake in how the run was set up, and a run that skips its scenes over one
        /// would look green while checking nothing.
        /// </summary>
        public static string? ConfigurationError { get; private set; }

        /// <summary>
        /// Decides which Vulkan driver this process may use, and sets the loader's environment to match.
        /// Must run before the first Vulkan call.
        /// </summary>
        public static void Configure()
        {
            if (Environment.GetEnvironmentVariable(EnvironmentVariable) is "0" or "false")
            {
                RequireCpuDevice = false;
                Status = $"{EnvironmentVariable}=0, so the hardware GPU is permitted and no driver "
                    + "restriction was applied. This is the configuration that bugchecked a machine.";
                return;
            }

            // Everything below this line is a run that will not touch the display driver, whatever else
            // happens: the flag is set before any of the ways of getting there can fail.
            RequireCpuDevice = true;

            var preset = Environment.GetEnvironmentVariable(DriverFilesVariable)
                ?? Environment.GetEnvironmentVariable(LegacyDriverFilesVariable);

            if (!string.IsNullOrEmpty(preset))
            {
                Status = $"{DriverFilesVariable} was already set to '{preset}' and was left as it is; "
                    + "the adapter must still report itself as a CPU device.";
                return;
            }

            if (!IsVendored)
            {
                ConfigurationError =
                    $"No software Vulkan driver is vendored. '{ManifestPath}' and '{DriverPath}' must both exist, "
                    + $"and running Tests/SoftwareVulkan/fetch-lavapipe.ps1 puts them there. "
                    + $"The Vulkan golden run does not fall back to the hardware GPU, because doing that on this "
                    + $"renderer hung a display driver and bugchecked the machine; set {EnvironmentVariable}=0 if "
                    + "you accept that risk on yours.";

                Status = ConfigurationError;
                return;
            }

            Environment.SetEnvironmentVariable(DriverFilesVariable, ManifestPath);
            Environment.SetEnvironmentVariable(LegacyDriverFilesVariable, ManifestPath);

            // System-wide capture hooks and overlays have no place in a golden run, and one of them is an
            // AMD shim whose whole job is reaching for the display adapter.
            Environment.SetEnvironmentVariable(LayersDisableVariable, "~implicit~");
            Environment.SetEnvironmentVariable(InstanceLayersVariable, null);

            Status = $"the Vulkan loader was restricted to the vendored software driver at '{ManifestPath}', "
                + "implicit layers were disabled, and the adapter must report itself as a CPU device.";
        }

        private static string SourceDirectory([CallerFilePath] string sourceFilePath = "")
            => Path.GetDirectoryName(sourceFilePath)!;
    }
}
