using System.Globalization;
using System.Linq;
using System.Threading;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.Buffers;
using ValveResourceFormat.Renderer.Materials;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;
using ValveResourceFormat.Renderer.Shaders.Spirv;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>One binding of a descriptor set layout.</summary>
/// <param name="Binding">The binding number within the set. These are the existing
/// <see cref="ReservedBufferSlots"/> and <see cref="ReservedTextureSlots"/> values.</param>
/// <param name="Type">What the binding holds.</param>
/// <param name="Count">Array length, 1 for a plain declaration.</param>
/// <param name="Stages">Which stages may read it.</param>
public readonly record struct VulkanDescriptorBinding(int Binding, DescriptorType Type, int Count, ShaderStageFlags Stages);

/// <summary>
/// Builds and caches the four <c>VkDescriptorSetLayout</c>s of the descriptor set scheme in
/// <c>RHI/CONTRACT.md</c>, merging what a shader's SPIR-V actually declares over the reserved slot
/// tables the renderer already numbers by.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coordination note.</b> A descriptor set layout type is also being built by the agent who owns
/// descriptor set allocation and updates. This class exists because a pipeline layout cannot be created
/// without set layouts and the pipeline layer had to be able to build and test one; it is intended to be
/// replaced by, or folded into, that agent's type. The seam is <see cref="VulkanPipelineLayoutCache"/>,
/// which is the only consumer, and <see cref="Build"/>, which is the only entry point. Nothing outside
/// this file assumes the layouts come from here.
/// </para>
/// <para>
/// <b>The reserved template is why the layouts are mostly shared.</b> Sets 0, 1 and 2 hold slots whose
/// numbering is fixed for every shader in the renderer, so by default every pipeline declares the whole
/// reserved range for them rather than only the subset its own SPIR-V happens to touch. Two pipelines
/// then get the identical set layout object, which is what makes them layout-compatible for those sets
/// and lets a bound global set survive a pipeline change. Reflection still wins where it disagrees: a
/// shader that declares a storage image at a reserved texture slot gets a storage image binding, because
/// a layout that disagrees with the shader is a validation error at pipeline creation and, without the
/// layer, a silently wrong binding.
/// </para>
/// <para>
/// Unused bindings still count against <c>maxPerStageDescriptor*</c>, so the template is checked against
/// the device's real limits and dropped, with a diagnostic, on a device too small for it. Set 3 is
/// always reflection-only: its numbering is assigned per shader by the material, so there is no template
/// to have.
/// </para>
/// </remarks>
public sealed unsafe class VulkanDescriptorSetLayouts : IDisposable
{
    private readonly Vk Api;
    private readonly Device Device;
    private readonly VulkanDebugNames DebugNames;
    private readonly VulkanPipelineStats Stats;
    private readonly Dictionary<ulong, DescriptorSetLayout> Cache = [];
    private readonly Lock Gate = new();

    private bool Disposed;

    /// <summary>Gets a value indicating whether the reserved template is being applied to sets 0 to 2.</summary>
    public bool UseReservedTemplate { get; }

    /// <summary>Gets the number of distinct set layouts created.</summary>
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

    /// <summary>Initializes the layout cache.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="debugNames">Used to name the layouts.</param>
    /// <param name="stats">Counters to raise.</param>
    /// <param name="useReservedTemplate">Whether to declare the whole reserved range for sets 0 to 2.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public VulkanDescriptorSetLayouts(
        Vk api,
        Device device,
        VulkanDebugNames debugNames,
        VulkanPipelineStats stats,
        bool useReservedTemplate)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(debugNames);
        ArgumentNullException.ThrowIfNull(stats);

        Api = api;
        Device = device;
        DebugNames = debugNames;
        Stats = stats;
        UseReservedTemplate = useReservedTemplate;
    }

    /// <summary>
    /// Reports whether a device's limits leave room for the reserved template on every stage at once.
    /// </summary>
    /// <param name="limits">The physical device limits.</param>
    /// <returns><see langword="true"/> when the template fits.</returns>
    /// <remarks>The floors the Vulkan specification guarantees are far below what the template declares
    /// &#8212; four storage buffers per stage against the sixteen <see cref="ReservedBufferSlots"/>
    /// numbers &#8212; while every desktop driver reports orders of magnitude more. Rather than assume
    /// either, this asks.</remarks>
    public static bool TemplateFits(in PhysicalDeviceLimits limits)
        => limits.MaxBoundDescriptorSets >= DescriptorSets.Count
        && limits.MaxPerStageDescriptorUniformBuffers >= (uint)UniformBufferSlotCount
        && limits.MaxPerStageDescriptorStorageBuffers >= (uint)StorageBufferSlotCount
        && limits.MaxPerStageDescriptorSampledImages >= (uint)ReservedTextureSlotCount;

    /// <summary>Gets the number of uniform buffer slots the reserved template declares in set 0.</summary>
    /// <remarks><see cref="ReservedBufferSlots.Max"/> is the guaranteed binding point count, and the
    /// uniform half of the enum runs from zero to one below it.</remarks>
    public static int UniformBufferSlotCount => (int)ReservedBufferSlots.Max;

    /// <summary>Gets the number of storage buffer slots the reserved template declares in set 1.</summary>
    /// <remarks>The storage half of <see cref="ReservedBufferSlots"/> runs past the uniform half; the
    /// last member is <see cref="ReservedBufferSlots.CullPlanes"/>.</remarks>
    public static int StorageBufferSlotCount => (int)ReservedBufferSlots.CullPlanes + 1;

    /// <summary>Gets the number of global texture slots the reserved template declares in set 2.</summary>
    public static int ReservedTextureSlotCount => (int)ReservedTextureSlots.Last + 1;

    /// <summary>
    /// Builds the four set layouts a pipeline over these stages needs.
    /// </summary>
    /// <param name="reflections">The reflected interface of every stage in the pipeline.</param>
    /// <param name="pipelineName">Debug name, used to name the layouts and to word diagnostics.</param>
    /// <param name="problems">Receives one message per contract violation or unrepresentable declaration.</param>
    /// <returns>The four layouts, indexed by descriptor set number.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reflections"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A stage declares a descriptor in a set the contract
    /// does not have, or of a kind with no Vulkan descriptor type.</exception>
    public DescriptorSetLayout[] Build(
        IReadOnlyList<SpirvReflectionResult> reflections,
        string pipelineName,
        out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(reflections);

        var found = new List<string>();
        var sets = new Dictionary<int, VulkanDescriptorBinding>[DescriptorSets.Count];

        for (var set = 0; set < sets.Length; set++)
        {
            sets[set] = [];
        }

        if (UseReservedTemplate)
        {
            ApplyReservedTemplate(sets);
        }

        foreach (var reflection in reflections)
        {
            if (reflection is null)
            {
                continue;
            }

            foreach (var violation in SpirvReflection.ValidateDescriptorSets(reflection))
            {
                found.Add(string.Create(CultureInfo.InvariantCulture, $"'{pipelineName}': {violation}"));
                Stats.Count(VulkanPipelineCounter.DescriptorConformanceViolations);
            }

            var stage = VulkanShaderModule.ToVkStage(reflection.Stage);

            foreach (var binding in reflection.DescriptorBindings)
            {
                if (binding.Set < 0 || binding.Set >= DescriptorSets.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(reflections), binding.Set,
                        $"Pipeline '{pipelineName}' declares '{binding.Name}' in descriptor set {binding.Set}, but the contract has {DescriptorSets.Count} sets.");
                }

                var type = ToDescriptorType(binding.Kind, binding.Name, pipelineName);

                // A runtime sized descriptor array needs descriptor indexing to express; the renderer
                // has none, so one entry is the honest reading of an unsized declaration.
                var count = Math.Max(1, binding.Count);

                if (sets[binding.Set].TryGetValue(binding.Binding, out var existing))
                {
                    if (existing.Type != type)
                    {
                        found.Add(string.Create(CultureInfo.InvariantCulture,
                            $"'{pipelineName}': set {binding.Set} binding {binding.Binding} is declared as {existing.Type} and as {type}; the shader's type wins."));
                    }

                    sets[binding.Set][binding.Binding] = existing with
                    {
                        Type = type,
                        Count = Math.Max(existing.Count, count),
                        Stages = existing.Stages | stage,
                    };

                    continue;
                }

                sets[binding.Set][binding.Binding] = new VulkanDescriptorBinding(binding.Binding, type, count, stage);
            }
        }

        problems = found;

        var layouts = new DescriptorSetLayout[DescriptorSets.Count];

        for (var set = 0; set < layouts.Length; set++)
        {
            var ordered = sets[set].Values.OrderBy(static b => b.Binding).ToArray();
            layouts[set] = GetOrCreate(ordered, string.Create(CultureInfo.InvariantCulture, $"{pipelineName} set {set}"));
        }

        return layouts;
    }

    private static void ApplyReservedTemplate(Dictionary<int, VulkanDescriptorBinding>[] sets)
    {
        // ShaderStageFlags.All rather than the union of the pipeline's stages: the template's whole point
        // is that two pipelines get the identical layout object, and a per-pipeline stage mask would
        // make it depend on which stages the pipeline happens to have.
        for (var binding = 0; binding < UniformBufferSlotCount; binding++)
        {
            sets[DescriptorSets.UniformBuffers][binding] =
                new VulkanDescriptorBinding(binding, DescriptorType.UniformBuffer, 1, ShaderStageFlags.All);
        }

        for (var binding = 0; binding < StorageBufferSlotCount; binding++)
        {
            sets[DescriptorSets.StorageBuffers][binding] =
                new VulkanDescriptorBinding(binding, DescriptorType.StorageBuffer, 1, ShaderStageFlags.All);
        }

        for (var binding = 0; binding < ReservedTextureSlotCount; binding++)
        {
            sets[DescriptorSets.ReservedTextures][binding] =
                new VulkanDescriptorBinding(binding, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.All);
        }
    }

    /// <summary>Maps a reflected resource kind to its Vulkan descriptor type.</summary>
    /// <param name="kind">What the binding holds.</param>
    /// <param name="bindingName">The declared name, for the exception message.</param>
    /// <param name="pipelineName">The pipeline being built, for the exception message.</param>
    /// <returns>The descriptor type.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> has no descriptor type.</exception>
    public static DescriptorType ToDescriptorType(SpirvResourceKind kind, string bindingName, string pipelineName)
        => kind switch
        {
            SpirvResourceKind.UniformBuffer => DescriptorType.UniformBuffer,
            SpirvResourceKind.StorageBuffer => DescriptorType.StorageBuffer,
            SpirvResourceKind.CombinedImageSampler => DescriptorType.CombinedImageSampler,
            SpirvResourceKind.SampledImage => DescriptorType.SampledImage,
            SpirvResourceKind.StorageImage => DescriptorType.StorageImage,
            SpirvResourceKind.Sampler => DescriptorType.Sampler,
            SpirvResourceKind.UniformTexelBuffer => DescriptorType.UniformTexelBuffer,
            SpirvResourceKind.StorageTexelBuffer => DescriptorType.StorageTexelBuffer,
            SpirvResourceKind.InputAttachment => DescriptorType.InputAttachment,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
                $"Pipeline '{pipelineName}' declares '{bindingName}' as {kind}, which has no Vulkan descriptor type. The reflector could not classify it."),
        };

    /// <summary>Gets the layout for a set of bindings, creating it on first request.</summary>
    /// <param name="bindings">The bindings, in ascending binding order.</param>
    /// <param name="name">Debug name.</param>
    /// <returns>The layout, shared with every other request for the same bindings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bindings"/> is <see langword="null"/>.</exception>
    /// <exception cref="VulkanException">Creation failed.</exception>
    public DescriptorSetLayout GetOrCreate(IReadOnlyList<VulkanDescriptorBinding> bindings, string name)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ObjectDisposedException.ThrowIf(Disposed, this);

        var hash = HashBindings(bindings);

        lock (Gate)
        {
            if (Cache.TryGetValue(hash, out var cached))
            {
                Stats.Count(VulkanPipelineCounter.DescriptorSetLayoutCacheHits);
                return cached;
            }

            var native = new DescriptorSetLayoutBinding[bindings.Count];

            for (var i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];

                native[i] = new DescriptorSetLayoutBinding
                {
                    Binding = (uint)binding.Binding,
                    DescriptorType = binding.Type,
                    DescriptorCount = (uint)binding.Count,
                    StageFlags = binding.Stages,
                    PImmutableSamplers = null,
                };
            }

            DescriptorSetLayout layout;

            fixed (DescriptorSetLayoutBinding* p = native)
            {
                var info = new DescriptorSetLayoutCreateInfo
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    BindingCount = (uint)native.Length,
                    PBindings = native.Length == 0 ? null : p,
                };

                Api.CreateDescriptorSetLayout(Device, &info, null, out layout).Check("vkCreateDescriptorSetLayout");
            }

            DebugNames.SetName(ObjectType.DescriptorSetLayout, layout.Handle, name);
            Cache[hash] = layout;
            Stats.Count(VulkanPipelineCounter.DescriptorSetLayoutsCreated);

            return layout;
        }
    }

    /// <summary>Hashes a binding list, reproducibly across runs.</summary>
    /// <param name="bindings">The bindings to hash.</param>
    /// <returns>The hash.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bindings"/> is <see langword="null"/>.</exception>
    public static ulong HashBindings(IReadOnlyList<VulkanDescriptorBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        var hash = VulkanPipelineKey.OffsetBasis;

        foreach (var binding in bindings)
        {
            hash = VulkanPipelineKey.HashValue(hash, (ulong)binding.Binding);
            hash = VulkanPipelineKey.HashValue(hash, (ulong)binding.Type);
            hash = VulkanPipelineKey.HashValue(hash, (ulong)binding.Count);
            hash = VulkanPipelineKey.HashValue(hash, (ulong)binding.Stages);
        }

        return hash;
    }

    /// <summary>Destroys every layout this cache created.</summary>
    /// <remarks>Only legal once no pipeline layout still references them, which for this backend means
    /// after the device is idle.</remarks>
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
                Api.DestroyDescriptorSetLayout(Device, layout, null);
            }

            Cache.Clear();
        }
    }
}
