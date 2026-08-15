using System.Collections.Immutable;
using System.Globalization;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.Shaders.Spirv;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Descriptors;

/// <summary>
/// The four descriptor set layouts one pipeline layout declares, derived from the SPIR-V its stages
/// were compiled to.
/// </summary>
/// <remarks>
/// <para>
/// This is the hand-off to the pipeline layer: <c>vkCreatePipelineLayout</c> wants an array of four
/// <c>VkDescriptorSetLayout</c> handles in set order, and <see cref="CopyHandlesTo"/> fills it. Nothing
/// here creates a <c>VkPipelineLayout</c>; that object, its cache and the push constant range on it
/// belong to the pipeline layer.
/// </para>
/// <para>
/// Sets 0, 1 and 2 normally come back as the shared canonical layouts, so every pipeline built this way
/// is layout-compatible for them and the renderer can bind the globals once per pass. A set falls back
/// to a reflected layout only when the shader declares something the canonical table cannot express,
/// and <see cref="Diagnostics"/> says which and why. See <see cref="StorageImagesDisplaceSet2"/> for the
/// one case that actually occurs.
/// </para>
/// </remarks>
public sealed class VulkanPipelineDescriptorLayouts
{
    /// <summary>
    /// Whether any set fell back from its shared canonical layout to a reflected one.
    /// </summary>
    /// <remarks>
    /// True today only for the compute passes that declare storage images. Those bind at
    /// <c>layout(binding = 0..4)</c> in OpenGL's <i>image unit</i> namespace, which is a third index
    /// space alongside texture units and buffer binding points, and the contract's four-set table has no
    /// room for it: set 2 binding 0 is already <c>g_tBRDFLookup</c> as a combined image sampler. This is
    /// the same collision the UBO and SSBO ranges have, one namespace further on, and it has no
    /// equivalent of splitting across sets 0 and 1 available to it. Reflecting set 2 for those pipelines
    /// is correct but costs them compatibility with the shared set 2, so they must rebind it.
    /// </remarks>
    public bool StorageImagesDisplaceSet2 { get; }

    /// <summary>Gets the layouts in set order. Always <see cref="DescriptorSets.Count"/> long.</summary>
    public ImmutableArray<VulkanDescriptorSetLayout> Sets { get; }

    /// <summary>Gets one message per contract violation or canonical fallback. Empty when the shader
    /// conforms and every set is shared.</summary>
    public ImmutableArray<string> Diagnostics { get; }

    /// <summary>
    /// Gets the push constant range the stages declare, or <see langword="null"/> when none does.
    /// </summary>
    /// <remarks>Offered because building it means walking the same reflections. The contract's per-draw
    /// block is 92 bytes and this reports what the module actually contains, which is the derivation
    /// <c>CONTRACT.md</c> asks for rather than a hardcoded size.</remarks>
    public PushConstantRange? PushConstants { get; }

    private VulkanPipelineDescriptorLayouts(
        ImmutableArray<VulkanDescriptorSetLayout> sets,
        ImmutableArray<string> diagnostics,
        PushConstantRange? pushConstants,
        bool storageImagesDisplaceSet2)
    {
        Sets = sets;
        Diagnostics = diagnostics;
        PushConstants = pushConstants;
        StorageImagesDisplaceSet2 = storageImagesDisplaceSet2;
    }

    /// <summary>Copies the layout handles into a span, in set order.</summary>
    /// <param name="destination">Receives the handles. Must be at least
    /// <see cref="DescriptorSets.Count"/> long.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    public void CopyHandlesTo(Span<DescriptorSetLayout> destination)
    {
        if (destination.Length < Sets.Length)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"Need room for {Sets.Length} set layouts, got {destination.Length}."), nameof(destination));
        }

        for (var i = 0; i < Sets.Length; i++)
        {
            destination[i] = Sets[i].Handle;
        }
    }

    /// <summary>
    /// Builds the set layouts for a pipeline from the reflection of every stage it uses.
    /// </summary>
    /// <param name="cache">Where the layouts come from and where they live.</param>
    /// <param name="name">Debug name for any layout this has to create.</param>
    /// <param name="stages">The reflected modules. A graphics pipeline passes its vertex and fragment
    /// modules, a compute pipeline its single one.</param>
    /// <returns>The four layouts and what was noticed while building them.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public static VulkanPipelineDescriptorLayouts Build(
        VulkanDescriptorLayoutCache cache,
        string name,
        params SpirvReflectionResult[] stages)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(stages);

        var diagnostics = ImmutableArray.CreateBuilder<string>();
        var merged = MergeStages(stages, diagnostics);

        var sets = ImmutableArray.CreateBuilder<VulkanDescriptorSetLayout>(DescriptorSets.Count);
        var displaced = false;

        for (var set = 0; set < DescriptorSets.Count; set++)
        {
            var declared = merged[set];
            var canonical = cache.Canonical(set);

            if (canonical is not null)
            {
                if (FitsCanonical(declared, canonical, set, out var reason))
                {
                    sets.Add(canonical);
                    continue;
                }

                displaced |= set == DescriptorSets.ReservedTextures;

                diagnostics.Add(string.Create(CultureInfo.InvariantCulture,
                    $"'{name}' cannot use the shared set {set} layout: {reason} Its set {set} is reflected instead, so it is not layout-compatible with pipelines that use the shared one and must rebind it."));
            }

            sets.Add(cache.GetOrCreate(
                set,
                [.. declared.Values],
                string.Create(CultureInfo.InvariantCulture, $"{name} set {set}")));
        }

        return new VulkanPipelineDescriptorLayouts(
            sets.MoveToImmutable(),
            diagnostics.ToImmutable(),
            MergePushConstants(stages),
            displaced);
    }

    private static Dictionary<int, VulkanDescriptorBinding>[] MergeStages(
        SpirvReflectionResult[] stages,
        ImmutableArray<string>.Builder diagnostics)
    {
        var merged = new Dictionary<int, VulkanDescriptorBinding>[DescriptorSets.Count];

        for (var set = 0; set < merged.Length; set++)
        {
            merged[set] = [];
        }

        foreach (var stage in stages)
        {
            if (stage is null)
            {
                continue;
            }

            // Run the contract conformance check the reflector already provides, so a shader that
            // decorates a storage buffer into set 0 is reported here rather than binding the wrong
            // buffer silently at draw time.
            diagnostics.AddRange(SpirvReflection.ValidateDescriptorSets(stage));

            var stageFlags = VulkanDescriptorTypes.ToStageFlags(stage.Stage);

            foreach (var binding in stage.DescriptorBindings)
            {
                if (binding.Set < 0 || binding.Set >= DescriptorSets.Count)
                {
                    // Already reported by ValidateDescriptorSets; there is no set to put it in.
                    continue;
                }

                DescriptorType type;

                try
                {
                    type = VulkanDescriptorTypes.ToDescriptorType(binding.Kind);
                }
                catch (ArgumentOutOfRangeException)
                {
                    diagnostics.Add(string.Create(CultureInfo.InvariantCulture,
                        $"'{binding.Name}' (set={binding.Set}, binding={binding.Binding}) could not be classified, so it is left out of the layout."));
                    continue;
                }

                // A runtime sized array reflects as 0. Without descriptor indexing the layout needs a
                // real count, and 1 is what a non-indexed shader can actually address.
                var count = Math.Max(binding.Count, 1);
                var table = merged[binding.Set];

                if (table.TryGetValue(binding.Binding, out var existing))
                {
                    if (existing.Type != type || existing.Count != count)
                    {
                        diagnostics.Add(string.Create(CultureInfo.InvariantCulture,
                            $"Set {binding.Set} binding {binding.Binding} is declared as {existing.Type}[{existing.Count}] by one stage and {type}[{count}] by another. Keeping the first."));
                        continue;
                    }

                    table[binding.Binding] = existing with { Stages = existing.Stages | stageFlags };
                    continue;
                }

                table[binding.Binding] = new VulkanDescriptorBinding(binding.Binding, type, count, stageFlags);
            }
        }

        return merged;
    }

    /// <summary>
    /// Whether everything the shader declares in a set is already declared the same way by the shared
    /// canonical layout, which is what lets the pipeline use the shared object.
    /// </summary>
    private static bool FitsCanonical(
        Dictionary<int, VulkanDescriptorBinding> declared,
        VulkanDescriptorSetLayout canonical,
        int set,
        out string reason)
    {
        foreach (var (binding, wanted) in declared)
        {
            if (!canonical.TryGetBinding(binding, out var available))
            {
                reason = string.Create(CultureInfo.InvariantCulture,
                    $"it declares binding {binding}, which is past the last reserved slot of set {set}.");
                return false;
            }

            if (available.Type != wanted.Type)
            {
                reason = string.Create(CultureInfo.InvariantCulture,
                    $"binding {binding} is a {wanted.Type} there, and the reserved table types that slot as a {available.Type}.");
                return false;
            }

            if (available.Count != wanted.Count)
            {
                reason = string.Create(CultureInfo.InvariantCulture,
                    $"binding {binding} is an array of {wanted.Count}, and the reserved table declares {available.Count}.");
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static PushConstantRange? MergePushConstants(SpirvReflectionResult[] stages)
    {
        var size = 0;
        var stageMask = ShaderStage.None;

        foreach (var stage in stages)
        {
            if (stage is null || stage.PushConstantSizeInBytes <= 0)
            {
                continue;
            }

            size = Math.Max(size, stage.PushConstantSizeInBytes);
            stageMask |= stage.Stage;
        }

        return size == 0 ? null : new PushConstantRange(0, size, stageMask);
    }
}
