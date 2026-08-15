using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// <see cref="ISampler"/> on Vulkan: one <c>VkSampler</c>.
/// </summary>
/// <remarks>
/// <para>
/// Sampler objects are already how <see cref="Materials.MaterialLoader"/> supplies filtering and wrap
/// state on OpenGL, so nothing about the renderer's model changes shape here. What does change is that
/// there is no fallback: <c>GLDevice.DefaultSamplerHandle</c> is zero, meaning "use the parameters on
/// the texture object", and Vulkan has no such thing because a <c>VkImage</c> carries no sampling
/// state at all. Every bind needs a real sampler, which is why
/// <see cref="VulkanDevice.DefaultSampler"/> exists.
/// </para>
/// <para>
/// <see cref="SamplerDesc"/> carries no border colour, so <see cref="AddressMode.ClampToBorder"/>
/// resolves to transparent black. That is OpenGL's default <c>GL_TEXTURE_BORDER_COLOR</c> and
/// therefore what the renderer's existing clamp-to-border call sites already get.
/// </para>
/// </remarks>
public sealed unsafe class VulkanSampler : ISampler
{
    private readonly Vk Api;
    private readonly Device Device;

    private Sampler SamplerHandle;
    private bool Disposed;

    /// <summary>Gets the sampler handle, or a null handle once disposed.</summary>
    public Sampler Handle => SamplerHandle;

    /// <inheritdoc/>
    public string Name { get; }

    /// <summary>Gets the parameters this sampler was created from.</summary>
    public SamplerDesc Description { get; }

    /// <summary>Creates a sampler.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="debugNames">Used to name the sampler.</param>
    /// <param name="desc">Creation parameters. Anisotropy must already be clamped to the device limit.</param>
    /// <param name="name">Debug name.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public VulkanSampler(Vk api, Device device, VulkanDebugNames debugNames, in SamplerDesc desc, string name)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(debugNames);

        Api = api;
        Device = device;
        Name = name ?? string.Empty;
        Description = desc;

        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MinFilter = ToVkFilter(desc.MinFilter),
            MagFilter = ToVkFilter(desc.MagFilter),
            MipmapMode = desc.MipFilter == MipFilterMode.Linear
                ? SamplerMipmapMode.Linear
                : SamplerMipmapMode.Nearest,
            AddressModeU = ToVkAddress(desc.AddressU),
            AddressModeV = ToVkAddress(desc.AddressV),
            AddressModeW = ToVkAddress(desc.AddressW),
            MipLodBias = 0f,
            AnisotropyEnable = desc.MaxAnisotropy > 1f,
            MaxAnisotropy = desc.MaxAnisotropy,
            CompareEnable = desc.CompareOp is not null,
            CompareOp = desc.CompareOp is { } compare ? ToVkCompare(compare) : CompareOp.Never,
            MinLod = 0f,

            // MipFilterMode.None means sample level zero only, which Vulkan expresses as a zero LOD
            // clamp rather than as a mipmap mode; SamplerMipmapMode has no "none" member.
            MaxLod = desc.MipFilter == MipFilterMode.None ? 0f : Vk.LodClampNone,

            BorderColor = BorderColor.FloatTransparentBlack,
            UnnormalizedCoordinates = false,
        };

        Api.CreateSampler(Device, &info, null, out SamplerHandle).Check("vkCreateSampler");
        debugNames.SetName(ObjectType.Sampler, SamplerHandle.Handle, Name);
    }

    private static Filter ToVkFilter(FilterMode filter) => filter switch
    {
        FilterMode.Nearest => Filter.Nearest,
        FilterMode.Linear => Filter.Linear,
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "Unknown filter mode."),
    };

    private static SamplerAddressMode ToVkAddress(AddressMode address) => address switch
    {
        AddressMode.Repeat => SamplerAddressMode.Repeat,
        AddressMode.MirroredRepeat => SamplerAddressMode.MirroredRepeat,
        AddressMode.ClampToEdge => SamplerAddressMode.ClampToEdge,
        AddressMode.ClampToBorder => SamplerAddressMode.ClampToBorder,
        _ => throw new ArgumentOutOfRangeException(nameof(address), address, "Unknown address mode."),
    };

    // The same table GLSampler applies, so a shadow comparison follows the renderer's reverse-Z
    // convention identically on both backends rather than restating it.
    private static CompareOp ToVkCompare(Comparison comparison) => comparison switch
    {
        Comparison.Never => CompareOp.Never,
        Comparison.Less => CompareOp.Less,
        Comparison.Equal => CompareOp.Equal,
        Comparison.LessEqual => CompareOp.LessOrEqual,
        Comparison.Greater => CompareOp.Greater,
        Comparison.NotEqual => CompareOp.NotEqual,
        Comparison.GreaterEqual => CompareOp.GreaterOrEqual,
        Comparison.Always => CompareOp.Always,
        Comparison.Closer => CompareOp.Greater,
        Comparison.CloserEqual => CompareOp.GreaterOrEqual,
        Comparison.Farther => CompareOp.Less,
        Comparison.FartherEqual => CompareOp.LessOrEqual,
        _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison, "Unknown comparison."),
    };

    /// <summary>Queues this sampler on a deletion queue.</summary>
    /// <param name="deletionQueue">The queue to enqueue on.</param>
    /// <param name="frameSerial">The serial of the frame during which destruction was requested.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deletionQueue"/> is <see langword="null"/>.</exception>
    public void EnqueueDestroy(VulkanDeletionQueue deletionQueue, ulong frameSerial)
    {
        ArgumentNullException.ThrowIfNull(deletionQueue);

        if (Disposed || SamplerHandle.Handle == 0)
        {
            return;
        }

        Disposed = true;
        deletionQueue.Enqueue(frameSerial, SamplerHandle);
        SamplerHandle = default;
    }

    /// <summary>Destroys the sampler immediately.</summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        if (SamplerHandle.Handle != 0)
        {
            Api.DestroySampler(Device, SamplerHandle, null);
            SamplerHandle = default;
        }
    }
}
