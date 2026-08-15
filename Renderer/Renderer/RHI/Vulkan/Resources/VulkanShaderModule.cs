using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// <see cref="IShaderModule"/> on Vulkan: one <c>VkShaderModule</c> built from SPIR-V.
/// </summary>
/// <remarks>
/// <para>
/// The contract's <see cref="IDevice.CreateShaderModule"/> takes already compiled code, so nothing here
/// compiles anything: producing the SPIR-V is <see cref="ShaderLoader"/>'s job. The bytes must be
/// SPIR-V words, which is why the length is required to be a multiple of four &#8212; handing the
/// driver GLSL source, which is what the same call means on the OpenGL backend, otherwise fails deep
/// inside the driver rather than here.
/// </para>
/// <para>
/// <see cref="ContentHash"/> is FNV-1a over the SPIR-V, matching <c>GLShaderModule</c>. It is
/// deliberately not <see cref="HashCode"/>: that is seeded per process, and a pipeline cache which
/// outlives a run would miss every entry.
/// </para>
/// </remarks>
public sealed unsafe class VulkanShaderModule : IShaderModule
{
    private const ulong FnvOffsetBasis = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    private readonly Vk Api;
    private readonly Device Device;

    private ShaderModule ModuleHandle;
    private bool Disposed;

    /// <summary>Gets the module handle, or a null handle once disposed.</summary>
    public ShaderModule Handle => ModuleHandle;

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public ShaderStage Stage { get; }

    /// <inheritdoc/>
    public ulong ContentHash { get; }

    /// <summary>Gets the Vulkan stage flag this module was compiled for.</summary>
    public ShaderStageFlags StageFlags { get; }

    /// <summary>Creates a shader module from SPIR-V.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="debugNames">Used to name the module.</param>
    /// <param name="code">The SPIR-V words.</param>
    /// <param name="stage">The stage the code was compiled for.</param>
    /// <param name="name">Debug name.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="code"/> is empty or not a whole number of
    /// SPIR-V words.</exception>
    public VulkanShaderModule(
        Vk api,
        Device device,
        VulkanDebugNames debugNames,
        ReadOnlySpan<byte> code,
        ShaderStage stage,
        string name)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(debugNames);

        if (code.IsEmpty)
        {
            throw new ArgumentException($"Shader module '{name}' was given no code.", nameof(code));
        }

        if (code.Length % 4 != 0)
        {
            throw new ArgumentException($"Shader module '{name}' is {code.Length} bytes, which is not a whole number of SPIR-V words. This backend takes SPIR-V, not GLSL source.", nameof(code));
        }

        Api = api;
        Device = device;
        Name = name ?? string.Empty;
        Stage = stage;
        StageFlags = ToVkStage(stage);
        ContentHash = Fnv1a(code);

        fixed (byte* p = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)p,
            };

            Api.CreateShaderModule(Device, &info, null, out ModuleHandle).Check("vkCreateShaderModule");
        }

        debugNames.SetName(ObjectType.ShaderModule, ModuleHandle.Handle, Name);
    }

    /// <summary>Translates a contract stage to its Vulkan flag.</summary>
    /// <param name="stage">The stage to translate.</param>
    /// <returns>The matching <see cref="ShaderStageFlags"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stage"/> is not a single stage.</exception>
    public static ShaderStageFlags ToVkStage(ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => ShaderStageFlags.VertexBit,
        ShaderStage.Fragment => ShaderStageFlags.FragmentBit,
        ShaderStage.Compute => ShaderStageFlags.ComputeBit,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "A shader module is one stage; AllGraphics and None cannot be compiled."),
    };

    /// <summary>Translates a set of contract stages to Vulkan flags, for a push constant range.</summary>
    /// <param name="stages">The stages to translate.</param>
    /// <returns>The matching <see cref="ShaderStageFlags"/>.</returns>
    public static ShaderStageFlags ToVkStages(ShaderStage stages)
    {
        var flags = ShaderStageFlags.None;

        if (stages.HasFlag(ShaderStage.Vertex))
        {
            flags |= ShaderStageFlags.VertexBit;
        }

        if (stages.HasFlag(ShaderStage.Fragment))
        {
            flags |= ShaderStageFlags.FragmentBit;
        }

        if (stages.HasFlag(ShaderStage.Compute))
        {
            flags |= ShaderStageFlags.ComputeBit;
        }

        return flags;
    }

    private static ulong Fnv1a(ReadOnlySpan<byte> data)
    {
        var hash = FnvOffsetBasis;

        foreach (var b in data)
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        return hash;
    }

    /// <summary>Queues this module on a deletion queue.</summary>
    /// <param name="deletionQueue">The queue to enqueue on.</param>
    /// <param name="frameSerial">The serial of the frame during which destruction was requested.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deletionQueue"/> is <see langword="null"/>.</exception>
    /// <remarks>A module may be destroyed as soon as every pipeline using it has been created, but the
    /// contract routes every resource through the same deferral, so this does too.</remarks>
    public void EnqueueDestroy(VulkanDeletionQueue deletionQueue, ulong frameSerial)
    {
        ArgumentNullException.ThrowIfNull(deletionQueue);

        if (Disposed || ModuleHandle.Handle == 0)
        {
            return;
        }

        Disposed = true;

        var api = Api;
        var device = Device;
        var handle = ModuleHandle;

        deletionQueue.Enqueue(frameSerial, () => api.DestroyShaderModule(device, handle, null));
        ModuleHandle = default;
    }

    /// <summary>Destroys the module immediately.</summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        if (ModuleHandle.Handle != 0)
        {
            Api.DestroyShaderModule(Device, ModuleHandle, null);
            ModuleHandle = default;
        }
    }
}
