using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>Why a pipeline cache file was not reused.</summary>
public enum VulkanPipelineCacheRejection
{
    /// <summary>The file was accepted.</summary>
    None,

    /// <summary>No file was found at the path.</summary>
    Missing,

    /// <summary>The file could not be read.</summary>
    Unreadable,

    /// <summary>The file is not one of these, or is shorter than its own container header.</summary>
    NotOurFormat,

    /// <summary>The payload does not match the checksum recorded with it, so the file is truncated or corrupt.</summary>
    Corrupt,

    /// <summary>The graphics driver has been updated since the file was written.</summary>
    DriverChanged,

    /// <summary>The file was written by a different physical device.</summary>
    DeviceChanged,

    /// <summary>The driver's own cache header says the blob is not for this implementation.</summary>
    IncompatibleBlob,
}

/// <summary>
/// A <c>VkPipelineCache</c> that survives the process, so a second run does not recompile every
/// pipeline the first one already did.
/// </summary>
/// <remarks>
/// <para>
/// The driver keeps a compiled-shader cache behind the handle; handing it back the blob it produced last
/// run is what turns a cold first frame into a warm one. Vulkan asks that the blob be validated before
/// it is handed over, and the header the specification puts at the front of it says exactly what it is
/// for: the vendor and device it was compiled on and a <c>pipelineCacheUUID</c> the driver changes
/// whenever its compiled output stops being interchangeable. Feeding a blob from another device or an
/// older driver back to it is at best ignored and at worst a crash inside the driver.
/// </para>
/// <para>
/// Around the driver's header this writes a container of its own: a magic number, the driver version,
/// the payload length and an FNV-1a checksum of the payload. The specification's header cannot detect a
/// truncated file &#8212; a half-written cache from a process killed mid-save has a perfectly valid
/// header &#8212; and <c>driverVersion</c> is not in it at all, so a driver update that keeps the same
/// UUID would otherwise go unnoticed.
/// </para>
/// <para>
/// FNV-1a again rather than <see cref="HashCode"/>, for the reason given on
/// <see cref="VulkanPipelineKey"/>: a per-process seed would reject the file on every subsequent run.
/// </para>
/// </remarks>
public sealed unsafe class VulkanPipelineCache : IDisposable
{
    /// <summary>The container magic, and its version. Bump the trailing digits to invalidate every file.</summary>
    public const string ContainerMagic = "VRFPLC01";

    /// <summary>Bytes of container header before the driver's blob: magic, driver version, length, checksum.</summary>
    public const int ContainerHeaderSize = 8 + 4 + 4 + 8;

    /// <summary>Bytes of the header the Vulkan specification puts at the front of the blob itself.</summary>
    public const int BlobHeaderSize = 16 + UuidSize;

    private const int UuidSize = 16;
    private const uint HeaderVersionOne = 1;

    private readonly Vk Api;
    private readonly Device Device;
    private readonly VulkanAdapter Adapter;
    private readonly VulkanPipelineStats Stats;
    private readonly RhiMessageCallback? MessageCallback;

    private PipelineCache CacheHandle;
    private bool Disposed;

    /// <summary>Gets the cache handle, for passing to <c>vkCreate*Pipelines</c>.</summary>
    public PipelineCache Handle => CacheHandle;

    /// <summary>Gets the file this cache loads from and saves to, or <see langword="null"/> when it is
    /// memory only.</summary>
    public string? FilePath { get; }

    /// <summary>Gets why the file on disk was not reused, or <see cref="VulkanPipelineCacheRejection.None"/>
    /// when it was.</summary>
    public VulkanPipelineCacheRejection Rejection { get; }

    /// <summary>Gets a value indicating whether the cache started warm, from a file this device wrote.</summary>
    public bool LoadedFromDisk => Rejection == VulkanPipelineCacheRejection.None && FilePath is not null;

    /// <summary>Creates the cache, warming it from <paramref name="filePath"/> when that file is valid
    /// for this device and driver.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="adapter">The physical device, whose identity the file is validated against.</param>
    /// <param name="debugNames">Used to name the cache object.</param>
    /// <param name="stats">Counters to raise.</param>
    /// <param name="filePath">Where to load from and save to, or <see langword="null"/> for a memory
    /// only cache.</param>
    /// <param name="messageCallback">Where to report a rejected file, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="VulkanException">Creation failed.</exception>
    public VulkanPipelineCache(
        Vk api,
        Device device,
        VulkanAdapter adapter,
        VulkanDebugNames debugNames,
        VulkanPipelineStats stats,
        string? filePath,
        RhiMessageCallback? messageCallback = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(debugNames);
        ArgumentNullException.ThrowIfNull(stats);

        Api = api;
        Device = device;
        Adapter = adapter;
        Stats = stats;
        FilePath = filePath;
        MessageCallback = messageCallback;

        byte[]? initial = null;
        var rejection = VulkanPipelineCacheRejection.Missing;

        if (filePath is not null)
        {
            initial = Read(filePath, ref rejection);
        }

        Rejection = rejection;

        if (initial is not null)
        {
            Stats.Count(VulkanPipelineCounter.DiskCacheBytesLoaded, initial.Length);
        }
        else if (filePath is not null && rejection != VulkanPipelineCacheRejection.Missing)
        {
            messageCallback?.Invoke(RhiMessageSeverity.Info, string.Create(CultureInfo.InvariantCulture,
                $"Vulkan pipeline cache '{filePath}' was not reused: {rejection}. Pipelines will be compiled from scratch this run."));
        }

        fixed (byte* p = initial)
        {
            var info = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = (nuint)(initial?.Length ?? 0),
                PInitialData = initial is null ? null : p,
            };

            Api.CreatePipelineCache(Device, &info, null, out CacheHandle).Check("vkCreatePipelineCache");
        }

        debugNames.SetName(ObjectType.PipelineCache, CacheHandle.Handle, "VulkanPipelineCache");
    }

    /// <summary>
    /// Builds the default cache path for a device, under the same folder the viewer keeps its settings in.
    /// </summary>
    /// <param name="adapter">The physical device the cache is for.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="adapter"/> is <see langword="null"/>.</exception>
    /// <remarks>The vendor and device identifiers are in the file <i>name</i> as well as being validated
    /// from its contents, so a machine with two GPUs keeps two warm caches instead of one that is
    /// rejected every other launch.</remarks>
    public static string DefaultPathFor(VulkanAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Source2Viewer");

        var name = string.Create(CultureInfo.InvariantCulture,
            $"vulkan_pipelines_{adapter.Properties.VendorID:x4}_{adapter.Properties.DeviceID:x4}.bin");

        return Path.Combine(folder, name);
    }

    private byte[]? Read(string path, ref VulkanPipelineCacheRejection rejection)
    {
        byte[] file;

        try
        {
            if (!File.Exists(path))
            {
                rejection = VulkanPipelineCacheRejection.Missing;
                return null;
            }

            file = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            rejection = VulkanPipelineCacheRejection.Unreadable;
            return null;
        }

        if (file.Length < ContainerHeaderSize + BlobHeaderSize
            || !Encoding.ASCII.GetString(file, 0, ContainerMagic.Length).Equals(ContainerMagic, StringComparison.Ordinal))
        {
            rejection = VulkanPipelineCacheRejection.NotOurFormat;
            return null;
        }

        var driverVersion = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(12));
        var checksum = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(16));

        if (payloadLength <= 0 || ContainerHeaderSize + (long)payloadLength != file.Length)
        {
            rejection = VulkanPipelineCacheRejection.Corrupt;
            return null;
        }

        var payload = file.AsSpan(ContainerHeaderSize, payloadLength);

        if (VulkanPipelineKey.Fnv1a(payload) != checksum)
        {
            rejection = VulkanPipelineCacheRejection.Corrupt;
            return null;
        }

        if (driverVersion != Adapter.Properties.DriverVersion)
        {
            rejection = VulkanPipelineCacheRejection.DriverChanged;
            return null;
        }

        rejection = ClassifyBlob(payload);

        return rejection == VulkanPipelineCacheRejection.None ? payload.ToArray() : null;
    }

    /// <summary>
    /// Checks the header the specification requires at the front of a pipeline cache blob against this
    /// physical device.
    /// </summary>
    /// <param name="payload">The blob, starting at its own header.</param>
    /// <returns><see cref="VulkanPipelineCacheRejection.None"/> when the blob is for this device.</returns>
    private VulkanPipelineCacheRejection ClassifyBlob(ReadOnlySpan<byte> payload)
    {
        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var headerVersion = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        var vendorId = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        var deviceId = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]);

        if (headerVersion != HeaderVersionOne || headerLength < BlobHeaderSize || headerLength > payload.Length)
        {
            return VulkanPipelineCacheRejection.IncompatibleBlob;
        }

        if (vendorId != Adapter.Properties.VendorID || deviceId != Adapter.Properties.DeviceID)
        {
            return VulkanPipelineCacheRejection.DeviceChanged;
        }

        var uuid = payload.Slice(16, UuidSize);
        var properties = Adapter.Properties;

        for (var i = 0; i < UuidSize; i++)
        {
            if (uuid[i] != properties.PipelineCacheUuid[i])
            {
                // The driver changes this whenever its compiled output stops being interchangeable,
                // which is the signal a driver update has happened even when the version did not move.
                return VulkanPipelineCacheRejection.DriverChanged;
            }
        }

        return VulkanPipelineCacheRejection.None;
    }

    /// <summary>Reads the driver's current cache blob.</summary>
    /// <returns>The blob, which already carries the specification's header.</returns>
    /// <exception cref="VulkanException">The read failed.</exception>
    public byte[] GetData()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        nuint size = 0;
        Api.GetPipelineCacheData(Device, CacheHandle, ref size, null).Check("vkGetPipelineCacheData");

        if (size == 0)
        {
            return [];
        }

        var data = new byte[(int)size];

        fixed (byte* p = data)
        {
            Api.GetPipelineCacheData(Device, CacheHandle, ref size, p).Check("vkGetPipelineCacheData");
        }

        return data;
    }

    /// <summary>Writes the cache to <see cref="FilePath"/>.</summary>
    /// <returns><see langword="true"/> when a file was written.</returns>
    /// <remarks>Written to a temporary file and moved into place, so a process killed mid-save leaves the
    /// previous cache intact rather than a truncated one. A failure to write is not worth failing over
    /// &#8212; the only consequence is a cold start next run &#8212; so it is reported and swallowed.</remarks>
    public bool Save()
    {
        if (Disposed || FilePath is null)
        {
            return false;
        }

        byte[] payload;

        try
        {
            payload = GetData();
        }
        catch (VulkanException e)
        {
            MessageCallback?.Invoke(RhiMessageSeverity.Warning, $"Reading the Vulkan pipeline cache failed: {e.Message}");
            return false;
        }

        if (payload.Length < BlobHeaderSize)
        {
            return false;
        }

        var file = new byte[ContainerHeaderSize + payload.Length];

        Encoding.ASCII.GetBytes(ContainerMagic).CopyTo(file, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), Adapter.Properties.DriverVersion);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(12), payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(16), VulkanPipelineKey.Fnv1a(payload));
        payload.CopyTo(file, ContainerHeaderSize);

        var temporary = FilePath + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(FilePath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(temporary, file);
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            MessageCallback?.Invoke(RhiMessageSeverity.Warning,
                $"Saving the Vulkan pipeline cache to '{FilePath}' failed: {e.Message}");
            return false;
        }

        Stats.Count(VulkanPipelineCounter.DiskCacheBytesWritten, file.Length);
        return true;
    }

    /// <summary>Saves the cache and destroys the handle.</summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Save();
        Disposed = true;

        if (CacheHandle.Handle != 0)
        {
            Api.DestroyPipelineCache(Device, CacheHandle, null);
            CacheHandle = default;
        }
    }
}
