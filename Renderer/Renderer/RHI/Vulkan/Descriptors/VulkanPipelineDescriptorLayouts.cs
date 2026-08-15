using System.Collections.Immutable;
using System.Globalization;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.Shaders.Spirv;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Descriptors;

/// <summary>
/// The descriptor set layouts one pipeline layout declares, derived from the SPIR-V its stages
/// were compiled to.
/// </summary>
/// <remarks>
/// <para>
/// This is the hand-off to the pipeline layer: <c>vkCreatePipelineLayout</c> wants an array of
/// <see cref="DescriptorSets.Count"/> <c>VkDescriptorSetLayout</c> handles in set order, and
/// <see cref="CopyHandlesTo"/> or <see cref="ToHandles"/> fills it. Nothing here creates a
/// <c>VkPipelineLayout</c>; that object, its cache and the push constant range on it belong to the
/// pipeline layer.
/// </para>
/// <para>
/// Sets 0, 1, 2 and 4 normally come back as the shared canonical layouts, so every pipeline built this
/// way is layout-compatible for them and the renderer can bind the globals once per pass. A set falls
/// back to a reflected layout only when the shader declares something the canonical table cannot
/// express, and <see cref="Diagnostics"/> says which and why. See
/// <see cref="StorageImagesDisplaceSet2"/> for the one case that occurs today.
/// </para>
/// </remarks>
public sealed class VulkanPipelineDescriptorLayouts
{
    /// <summary>
    /// Whether set 2 fell back from its shared canonical layout because the shader put a storage image
    /// in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Transitional, and expected to be true until shader emission catches up.</b> Storage images now
    /// have <see cref="DescriptorSets.StorageImages"/> of their own, which is what this flag being true
    /// on the old layout argued for. But <c>ShaderParser</c> still decorates them
    /// <c>layout(set = 2, binding = n)</c>, so reflection still reports set 2 for an image, and a
    /// pipeline layout <i>must</i> declare what the module decorates &#8212; relocating the binding to
    /// set 4 here would leave the shader reading set 2 while the layout described set 4, which is the
    /// silently-wrong-resource failure the whole scheme exists to prevent. So the fallback stays: set 2
    /// is reflected for those pipelines, they lose compatibility with the shared set 2 and must rebind
    /// it.
    /// </para>
    /// <para>
    /// Nothing here needs changing when emission moves. An image decorated into set 4 fits the canonical
    /// set 4 layout, this flag stops being set, and set 2 goes back to being shared, all by the existing
    /// rule.
    /// </para>
    /// </remarks>
    public bool StorageImagesDisplaceSet2 { get; }

    /// <summary>Gets the layouts in set order. Always <see cref="DescriptorSets.Count"/> long.</summary>
    public ImmutableArray<VulkanDescriptorSetLayout> Sets { get; }

    /// <summary>Gets one message per contract violation or canonical fallback. Empty when the shader
    /// conforms and every set is shared.</summary>
    public ImmutableArray<string> Diagnostics { get; }

    /// <summary>
    /// Gets how many of <see cref="Diagnostics"/> are conformance violations reported by
    /// <see cref="SpirvReflection.ValidateDescriptorSets"/>, as opposed to notes about a set falling
    /// back to a reflected layout.
    /// </summary>
    /// <remarks>They are worth separating because they mean different things: a violation is a shader
    /// that disagrees with the contract and needs fixing, while a fallback is this layer handling a
    /// disagreement correctly. The violations lead the list.</remarks>
    public int ConformanceViolationCount { get; }

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
        int conformanceViolationCount,
        PushConstantRange? pushConstants,
        bool storageImagesDisplaceSet2)
    {
        Sets = sets;
        Diagnostics = diagnostics;
        ConformanceViolationCount = conformanceViolationCount;
        PushConstants = pushConstants;
        StorageImagesDisplaceSet2 = storageImagesDisplaceSet2;
    }

    /// <summary>Gets the layout handles as a fresh array, in set order.</summary>
    /// <returns>The handles, ready for <c>vkCreatePipelineLayout</c>.</returns>
    /// <remarks>For a caller that needs an array it can keep, such as
    /// <see cref="VulkanPipelineLayout"/>. Prefer <see cref="CopyHandlesTo"/> where a span will do.</remarks>
    public DescriptorSetLayout[] ToHandles()
    {
        var handles = new DescriptorSetLayout[Sets.Length];
        CopyHandlesTo(handles);
        return handles;
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
    /// <returns>The layouts and what was noticed while building them.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public static VulkanPipelineDescriptorLayouts Build(
        VulkanDescriptorLayoutCache cache,
        string name,
        params SpirvReflectionResult[] stages)
        => Build(cache, name, (IReadOnlyList<SpirvReflectionResult>)stages);

    /// <summary>
    /// Builds the set layouts for a pipeline from the reflection of every stage it uses.
    /// </summary>
    /// <param name="cache">Where the layouts come from and where they live.</param>
    /// <param name="name">Debug name for any layout this has to create.</param>
    /// <param name="stages">The reflected modules, in any order.</param>
    /// <returns>The layouts and what was noticed while building them.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <remarks>The overload the pipeline layer calls, which already holds its stages as a list.</remarks>
    public static VulkanPipelineDescriptorLayouts Build(
        VulkanDescriptorLayoutCache cache,
        string name,
        IReadOnlyList<SpirvReflectionResult> stages)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(stages);

        var diagnostics = ImmutableArray.CreateBuilder<string>();
        var merged = MergeStages(stages, diagnostics, out var conformanceViolations);

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

                var storageImageInSet2 = set == DescriptorSets.ReservedTextures && HasStorageImage(declared);
                displaced |= storageImageInSet2;

                var note = storageImageInSet2
                    ? $" This is the transitional case: storage images have set {DescriptorSets.StorageImages} of their own now, and this shader will stop needing a reflected set 2 once shader emission decorates them there."
                    : string.Empty;

                diagnostics.Add(string.Create(CultureInfo.InvariantCulture,
                    $"'{name}' cannot use the shared set {set} layout: {reason} Its set {set} is reflected instead, so it is not layout-compatible with pipelines that use the shared one and must rebind it.{note}"));
            }

            sets.Add(cache.GetOrCreate(
                set,
                [.. declared.Values],
                string.Create(CultureInfo.InvariantCulture, $"{name} set {set}")));
        }

        return new VulkanPipelineDescriptorLayouts(
            sets.MoveToImmutable(),
            diagnostics.ToImmutable(),
            conformanceViolations,
            MergePushConstants(stages),
            displaced);
    }

    private static bool HasStorageImage(Dictionary<int, VulkanDescriptorBinding> declared)
    {
        foreach (var binding in declared.Values)
        {
            if (binding.Type == DescriptorType.StorageImage)
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<int, VulkanDescriptorBinding>[] MergeStages(
        IReadOnlyList<SpirvReflectionResult> stages,
        ImmutableArray<string>.Builder diagnostics,
        out int conformanceViolations)
    {
        var merged = new Dictionary<int, VulkanDescriptorBinding>[DescriptorSets.Count];

        for (var set = 0; set < merged.Length; set++)
        {
            merged[set] = [];
        }

        conformanceViolations = 0;

        foreach (var stage in stages)
        {
            if (stage is null)
            {
                continue;
            }

            // Run the contract conformance check the reflector already provides, so a shader that
            // decorates a storage buffer into set 0 is reported here rather than binding the wrong
            // buffer silently at draw time.
            var violations = SpirvReflection.ValidateDescriptorSets(stage);
            diagnostics.AddRange(violations);
            conformanceViolations += violations.Length;

            var stageFlags = VulkanShaderModule.ToVkStages(stage.Stage);

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

    private static PushConstantRange? MergePushConstants(IReadOnlyList<SpirvReflectionResult> stages)
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
