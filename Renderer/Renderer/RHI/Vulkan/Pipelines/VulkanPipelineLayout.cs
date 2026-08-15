using System.Globalization;
using System.Threading;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;
using ValveResourceFormat.Renderer.Shaders.Spirv;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// A <c>VkPipelineLayout</c>: the four descriptor set layouts of the contract's scheme, plus the push
/// constant range the shaders declare.
/// </summary>
/// <remarks>
/// <para>
/// The layout does not own its set layouts. <see cref="VulkanDescriptorSetLayouts"/> does, and shares
/// them between every layout whose bindings match, so disposing a pipeline layout must not destroy them.
/// </para>
/// <para>
/// The push constant range comes from SPIR-V reflection rather than from a constant, which is what the
/// contract asks for and is worth the extra step. The block's 92 bytes are load bearing and depend on
/// the member <i>order</i>: an earlier revision of the contract listed the members in declaration order,
/// which packs to 96, and the two agents who caught it did so from opposite ends. Reading the size back
/// out of the compiled module is the only check that cannot be fooled by a table that has drifted.
/// </para>
/// </remarks>
public sealed unsafe class VulkanPipelineLayout : IDisposable
{
    private readonly Vk Api;
    private readonly Device Device;

    private PipelineLayout LayoutHandle;
    private bool Disposed;

    /// <summary>Gets the layout handle, or a null handle once disposed.</summary>
    public PipelineLayout Handle => LayoutHandle;

    /// <summary>Gets the debug name.</summary>
    public string Name { get; }

    /// <summary>Gets the descriptor set layouts, indexed by descriptor set number.</summary>
    public IReadOnlyList<DescriptorSetLayout> SetLayouts { get; }

    /// <summary>Gets the push constant range, or <see langword="null"/> when no stage declares one.</summary>
    public PushConstantRange? PushConstants { get; }

    /// <summary>Gets one message per contract violation or unrepresentable declaration found while
    /// building this layout. Empty when the shaders conform.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Creates a pipeline layout.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="debugNames">Used to name the layout.</param>
    /// <param name="setLayouts">The four descriptor set layouts, indexed by set number.</param>
    /// <param name="pushConstants">The push constant range, or <see langword="null"/> for none.</param>
    /// <param name="problems">Diagnostics gathered while the set layouts were built.</param>
    /// <param name="name">Debug name.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="VulkanException">Creation failed.</exception>
    public VulkanPipelineLayout(
        Vk api,
        Device device,
        VulkanDebugNames debugNames,
        DescriptorSetLayout[] setLayouts,
        PushConstantRange? pushConstants,
        IReadOnlyList<string> problems,
        string name)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(debugNames);
        ArgumentNullException.ThrowIfNull(setLayouts);
        ArgumentNullException.ThrowIfNull(problems);

        Api = api;
        Device = device;
        Name = name ?? string.Empty;
        SetLayouts = setLayouts;
        PushConstants = pushConstants;
        Problems = problems;

        var range = default(Silk.NET.Vulkan.PushConstantRange);

        if (pushConstants is { } declared)
        {
            range = new Silk.NET.Vulkan.PushConstantRange
            {
                StageFlags = VulkanShaderModule.ToVkStages(declared.Stages),
                Offset = (uint)declared.OffsetInBytes,
                Size = (uint)declared.SizeInBytes,
            };
        }

        fixed (DescriptorSetLayout* p = setLayouts)
        {
            var info = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)setLayouts.Length,
                PSetLayouts = setLayouts.Length == 0 ? null : p,
                PushConstantRangeCount = pushConstants is null ? 0u : 1u,
                PPushConstantRanges = pushConstants is null ? null : &range,
            };

            Api.CreatePipelineLayout(Device, &info, null, out LayoutHandle).Check("vkCreatePipelineLayout");
        }

        debugNames.SetName(ObjectType.PipelineLayout, LayoutHandle.Handle, Name);
    }

    /// <summary>
    /// Derives the push constant range a set of stages needs, from what their SPIR-V declares.
    /// </summary>
    /// <param name="reflections">The reflected stages.</param>
    /// <param name="maxPushConstantSize">The device's <see cref="IDeviceLimits.MaxPushConstantSize"/>.</param>
    /// <param name="pipelineName">The pipeline being built, used in the exception message.</param>
    /// <returns>The range, or <see langword="null"/> when no stage declares a block.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reflections"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The declared block does not fit the device limit.</exception>
    /// <remarks>
    /// One range covering every stage that declares a block, rather than one range per stage. The
    /// renderer's stages share a single block, so a shared range is both what Vulkan wants and what makes
    /// <c>vkCmdPushConstants</c> a single call.
    /// </remarks>
    public static PushConstantRange? DerivePushConstants(
        IReadOnlyList<SpirvReflectionResult> reflections,
        int maxPushConstantSize,
        string pipelineName)
    {
        ArgumentNullException.ThrowIfNull(reflections);

        var size = 0;
        var stages = ShaderStage.None;

        foreach (var reflection in reflections)
        {
            if (reflection is null || reflection.PushConstantSizeInBytes <= 0)
            {
                continue;
            }

            size = Math.Max(size, reflection.PushConstantSizeInBytes);
            stages |= reflection.Stage;
        }

        if (size == 0)
        {
            return null;
        }

        if (size > maxPushConstantSize)
        {
            throw new ArgumentOutOfRangeException(nameof(reflections), size, string.Create(CultureInfo.InvariantCulture,
                $"Pipeline '{pipelineName}' declares a {size} byte push constant block, which does not fit this device's {maxPushConstantSize} byte limit."));
        }

        return new PushConstantRange(0, size, stages);
    }

    /// <summary>Destroys the layout. Its set layouts belong to the set layout cache and are untouched.</summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        if (LayoutHandle.Handle != 0)
        {
            Api.DestroyPipelineLayout(Device, LayoutHandle, null);
            LayoutHandle = default;
        }
    }
}

/// <summary>
/// Caches pipeline layouts, so the many pipelines a shader produces share one.
/// </summary>
/// <remarks>
/// Worth caching separately from the pipelines themselves for the same reason the descriptor set layouts
/// are: a shader drawn under twenty render states is twenty pipelines but one layout, and two pipelines
/// are only layout-compatible &#8212; which is what lets a bound descriptor set survive a pipeline change
/// &#8212; if they were created from the same layout object.
/// </remarks>
public sealed class VulkanPipelineLayoutCache : IDisposable
{
    private readonly Vk Api;
    private readonly Device Device;
    private readonly VulkanDebugNames DebugNames;
    private readonly VulkanPipelineStats Stats;
    private readonly VulkanDescriptorSetLayouts SetLayouts;
    private readonly Dictionary<ulong, VulkanPipelineLayout> Cache = [];
    private readonly Lock Gate = new();

    private bool Disposed;

    /// <summary>Gets the number of distinct pipeline layouts created.</summary>
    public int LayoutCount
    {
        get
        {
            lock (Gate)
            {
                return Cache.Count;
            }
        }
    }

    /// <summary>Gets the descriptor set layout cache the layouts are built from.</summary>
    public VulkanDescriptorSetLayouts DescriptorSetLayouts => SetLayouts;

    /// <summary>Initializes the cache.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="debugNames">Used to name the layouts.</param>
    /// <param name="stats">Counters to raise.</param>
    /// <param name="setLayouts">The descriptor set layout cache.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public VulkanPipelineLayoutCache(
        Vk api,
        Device device,
        VulkanDebugNames debugNames,
        VulkanPipelineStats stats,
        VulkanDescriptorSetLayouts setLayouts)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(debugNames);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(setLayouts);

        Api = api;
        Device = device;
        DebugNames = debugNames;
        Stats = stats;
        SetLayouts = setLayouts;
    }

    /// <summary>Gets the layout a set of reflected stages needs, creating it on first request.</summary>
    /// <param name="reflections">The reflected stages of the pipeline.</param>
    /// <param name="maxPushConstantSize">The device's push constant limit.</param>
    /// <param name="name">Debug name.</param>
    /// <returns>The layout, shared with every other pipeline whose interface matches.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reflections"/> is <see langword="null"/>.</exception>
    /// <exception cref="VulkanException">Creation failed.</exception>
    public VulkanPipelineLayout GetOrCreate(
        IReadOnlyList<SpirvReflectionResult> reflections,
        int maxPushConstantSize,
        string name)
    {
        ArgumentNullException.ThrowIfNull(reflections);
        ObjectDisposedException.ThrowIf(Disposed, this);

        var setLayouts = SetLayouts.Build(reflections, name, out var problems);
        var pushConstants = VulkanPipelineLayout.DerivePushConstants(reflections, maxPushConstantSize, name);

        var hash = VulkanPipelineKey.OffsetBasis;

        foreach (var layout in setLayouts)
        {
            hash = VulkanPipelineKey.HashValue(hash, layout.Handle);
        }

        if (pushConstants is { } range)
        {
            hash = VulkanPipelineKey.HashValue(hash, (ulong)range.OffsetInBytes);
            hash = VulkanPipelineKey.HashValue(hash, (ulong)range.SizeInBytes);
            hash = VulkanPipelineKey.HashValue(hash, (ulong)range.Stages);
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(hash, out var cached))
            {
                Stats.Count(VulkanPipelineCounter.PipelineLayoutCacheHits);
                return cached;
            }

            var created = new VulkanPipelineLayout(Api, Device, DebugNames, setLayouts, pushConstants, problems, name);
            Cache[hash] = created;
            Stats.Count(VulkanPipelineCounter.PipelineLayoutsCreated);

            return created;
        }
    }

    /// <summary>Destroys every layout this cache created.</summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        lock (Gate)
        {
            foreach (var layout in Cache.Values)
            {
                layout.Dispose();
            }

            Cache.Clear();
        }
    }
}
