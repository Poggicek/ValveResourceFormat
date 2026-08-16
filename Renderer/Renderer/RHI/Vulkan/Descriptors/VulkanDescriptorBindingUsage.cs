using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using ValveResourceFormat.Renderer.Shaders.Spirv;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Descriptors;

/// <summary>
/// Which <i>bindings</i> inside each descriptor set a pipeline's shaders declare, carried as one bit per
/// binding number per set, and the record-time check that refuses a draw or dispatch while one of them
/// has nothing written to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the third fuse, and it stands where the other two do not.</b>
/// <see cref="VulkanDescriptorSetUsage.EnsureBound"/> refuses a draw whose pipeline uses a set nothing
/// bound, which is set granularity: it passes as soon as <i>anything</i> was bound into the set.
/// <see cref="VulkanDescriptorBinder"/>'s own <c>EnsureDeclaredBindingsBound</c> is binding granular but
/// deliberately runs only for <i>reflected</i> set layouts, because a canonical layout declares a whole
/// reserved range whether a shader reads it or not and nothing can be concluded from an empty slot in
/// one. Between them sits the case that has actually hurt: a canonical set — set 0's uniform buffers,
/// set 2's reserved textures — that was bound, with one binding inside it that no shader-visible call
/// ever filled. Reading it is undefined behaviour that reaches the GPU, and on an AMD driver it is a
/// device hang rather than a wrong pixel.
/// </para>
/// <para>
/// <b>What closes the gap is the pipeline, not the layout.</b> A canonical layout cannot say which of
/// its slots this draw reads, but the pipeline's own SPIR-V can, and
/// <see cref="VulkanPipelineDescriptorLayouts"/> already merges exactly that per set when it decides
/// whether a set fits the canonical table. Intersecting the two — what the pipeline declares against
/// what the binder holds — is one <c>and</c> and one compare per set per draw, five of each, against
/// integers computed once when the pipeline was created.
/// </para>
/// <para>
/// <b>Declared, not statically accessed &#8212; the same choice as
/// <see cref="VulkanDescriptorSetUsage"/>, made for the same reason and with the same evidence.</b>
/// <see cref="SpirvReflectionResult.DescriptorBindings"/> lists every module-scope variable carrying a
/// binding decoration. Vulkan's own requirement is narrower: only descriptors a pipeline <i>statically
/// uses</i> must be written. Nothing in this tree computes static use — the reflector walks
/// <c>OpVariable</c> and decorations and never enters a function body — so declaration is not merely the
/// convenient predicate, it is the only one available, and a guard built on it can refuse a draw Vulkan
/// would have allowed. That is the safe direction: being too strict costs an exception naming the
/// resource to bind, being too lax costs the hang. It is also the predicate the renderer above already
/// commits to. <c>MeshBatchRenderer.BindReservedTextures</c> binds a white texel into
/// <see cref="Materials.ReservedTextureSlots.MorphCompositeTexture"/> for every draw, precisely because
/// every shader compiled with <c>F_MORPH_SUPPORTED</c> declares <c>morphCompositeTexture</c> while only
/// a mesh with a composite has one to bind; its comment says the descriptor "is checked against what a
/// stage declares and not against what it reaches". This guard is that sentence enforced.
/// </para>
/// <para>
/// <b>Bindings at or past <see cref="MaskWidth"/> are left out of the mask</b>, the same concession
/// <see cref="IVulkanPipeline.UsedVertexBindings"/> makes. It cannot lose coverage where coverage is
/// scarce: every canonical set is narrower than that by construction — set 0 is
/// <see cref="Buffers.ReservedBufferSlots.Max"/> wide, set 1 sixteen, set 2
/// <see cref="Materials.ReservedTextureSlots.Last"/> plus one, set 4 eight, and
/// <see cref="EnsureCanonicalSetsFitTheMask"/> refuses to let that stop being true. Only set 3 can ever
/// reach one: its numbering is assigned per shader in declaration order, so it is bounded by how many
/// textures a single material shader declares rather than by a table — eleven is the widest in the tree
/// today, but nothing holds it there. Set 3 is also the one set that is always reflected, and therefore
/// already checked exactly, binding by binding, by <see cref="VulkanDescriptorBinder"/>, so a slot that
/// falls out of the mask does not fall out of the guards.
/// </para>
/// </remarks>
public sealed class VulkanDescriptorBindingUsage
{
    /// <summary>How many binding numbers one set's mask can hold.</summary>
    public const int MaskWidth = 32;

    private readonly uint[] MaskBySet;
    private readonly FrozenDictionary<int, string> NameByKey;

    /// <summary>Gets a usage declaring no binding at all, for a pipeline built without reflection.</summary>
    public static VulkanDescriptorBindingUsage None { get; } =
        new(new uint[DescriptorSets.Count], FrozenDictionary<int, string>.Empty);

    private VulkanDescriptorBindingUsage(uint[] maskBySet, FrozenDictionary<int, string> nameByKey)
    {
        MaskBySet = maskBySet;
        NameByKey = nameByKey;
    }

    /// <summary>Gets the declared bindings of each set, one bit per binding number, in set order.</summary>
    /// <remarks>A span rather than an array property so the caller cannot write through it and the
    /// per-draw read costs nothing.</remarks>
    public ReadOnlySpan<uint> Masks => MaskBySet;

    /// <summary>
    /// Builds the per-set binding masks for a pipeline from the reflection of every stage it uses.
    /// </summary>
    /// <param name="stages">The reflected modules. A graphics pipeline passes its vertex and fragment
    /// modules, a compute pipeline its single one. Null entries are skipped.</param>
    /// <returns>The masks, and the names to report a missing one by.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stages"/> is <see langword="null"/>.</exception>
    /// <remarks>Called once, when the pipeline is created, from the same reflections
    /// <see cref="VulkanDescriptorSetUsage.MaskFor(IReadOnlyList{SpirvReflectionResult})"/> and the
    /// pipeline's set layouts are built from. A draw then compares integers.</remarks>
    public static VulkanDescriptorBindingUsage For(IReadOnlyList<SpirvReflectionResult> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);

        var masks = new uint[DescriptorSets.Count];
        var names = new Dictionary<int, string>();

        for (var i = 0; i < stages.Count; i++)
        {
            Accumulate(stages[i], masks, names);
        }

        return names.Count == 0 && AllZero(masks)
            ? None
            : new VulkanDescriptorBindingUsage(masks, names.ToFrozenDictionary());
    }

    /// <summary>
    /// Builds the per-set binding masks for a pipeline with one stage.
    /// </summary>
    /// <param name="stage">The reflected module, or <see langword="null"/> for a stage that is absent.</param>
    /// <returns>The masks, and the names to report a missing one by.</returns>
    public static VulkanDescriptorBindingUsage For(SpirvReflectionResult? stage)
    {
        var masks = new uint[DescriptorSets.Count];
        var names = new Dictionary<int, string>();

        Accumulate(stage, masks, names);

        return names.Count == 0 && AllZero(masks)
            ? None
            : new VulkanDescriptorBindingUsage(masks, names.ToFrozenDictionary());
    }

    private static void Accumulate(SpirvReflectionResult? stage, uint[] masks, Dictionary<int, string> names)
    {
        if (stage is null)
        {
            return;
        }

        foreach (var binding in stage.DescriptorBindings)
        {
            // A set outside the contract's scheme has already been reported by
            // SpirvReflection.ValidateDescriptorSets, no layout declares it and nothing could bind it, so
            // counting it here would make the guard fire forever. Same rule as VulkanDescriptorSetUsage.
            if (binding.Set < 0 || binding.Set >= DescriptorSets.Count)
            {
                continue;
            }

            if (binding.Binding < 0 || binding.Binding >= MaskWidth)
            {
                continue;
            }

            masks[binding.Set] |= 1u << binding.Binding;

            if (binding.Name.Length > 0)
            {
                names.TryAdd(Key(binding.Set, binding.Binding), binding.Name);
            }
        }
    }

    private static bool AllZero(uint[] masks)
    {
        foreach (var mask in masks)
        {
            if (mask != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int Key(int set, int binding) => (set * MaskWidth) + binding;

    /// <summary>Gets the name the pipeline's own shaders give a binding, or an empty string.</summary>
    /// <param name="set">The set index.</param>
    /// <param name="binding">The binding number.</param>
    /// <returns>The declared name, such as <c>LightingConstants</c> or <c>morphCompositeTexture</c>.</returns>
    /// <remarks>Straight from the SPIR-V, so the message names the identifier a reader can search the
    /// shader tree for rather than a number this layer reverse-mapped.</remarks>
    public string NameOf(int set, int binding) => NameByKey.GetValueOrDefault(Key(set, binding), string.Empty);

    /// <summary>
    /// Refuses to let a draw or dispatch be recorded while a binding the pipeline declares has nothing
    /// written to it.
    /// </summary>
    /// <param name="pipelineName">The bound pipeline, named in the message.</param>
    /// <param name="declared">The masks the pipeline carries, from <see cref="For(IReadOnlyList{SpirvReflectionResult})"/>.</param>
    /// <param name="bound">What the descriptor binder holds for each set, one bit per binding number, in
    /// set order. Shorter than <see cref="DescriptorSets.Count"/> means the rest are empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="declared"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A declared binding was never bound. The message names
    /// the pipeline, every missing set and binding, what each holds and the call that fills it.</exception>
    /// <remarks>Five <c>and</c>s and five compares against precomputed integers in the passing case, which
    /// is why this is always on rather than behind a <c>DEBUG</c> guard. See the type's remarks for what
    /// it is standing in front of.</remarks>
    public static void EnsureBound(string pipelineName, VulkanDescriptorBindingUsage declared, ReadOnlySpan<uint> bound)
    {
        ArgumentNullException.ThrowIfNull(declared);

        var masks = declared.MaskBySet;

        for (var set = 0; set < masks.Length; set++)
        {
            var missing = masks[set] & ~(set < bound.Length ? bound[set] : 0u);

            if (missing != 0)
            {
                Throw(pipelineName, declared, bound);
                return;
            }
        }
    }

    /// <summary>Builds and throws the message, kept out of <see cref="EnsureBound"/> so the passing path
    /// stays small enough to inline.</summary>
    private static void Throw(string pipelineName, VulkanDescriptorBindingUsage declared, ReadOnlySpan<uint> bound)
    {
        var masks = declared.MaskBySet;
        var message = new StringBuilder();
        var total = 0;

        for (var set = 0; set < masks.Length; set++)
        {
            var missing = masks[set] & ~(set < bound.Length ? bound[set] : 0u);

            while (missing != 0)
            {
                var binding = BitOperations.TrailingZeroCount(missing);
                missing &= missing - 1;
                total++;

                var name = declared.NameOf(set, binding);
                var named = name.Length == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" ('{name}')");

                message.Append(CultureInfo.InvariantCulture,
                    $" Set {set} binding {binding}{named} holds {BoundBy(set, binding)}.");
            }
        }

        var lead = string.Create(CultureInfo.InvariantCulture,
            $"Pipeline '{pipelineName}' declares {total} descriptor {(total == 1 ? "binding" : "bindings")} that nothing wrote on this command list.");

        throw new InvalidOperationException(string.Concat(
            lead,
            message.ToString(),
            " The set itself was bound, so the set-granularity guard passed and the layout is a shared canonical one that declares the whole reserved range, so the binder's per-layout check says nothing about it either. Reading a descriptor nothing wrote is undefined behaviour that reaches the GPU: the validation layer reports it and the submission still happens, which on a real device hangs it."));
    }

    /// <summary>
    /// Says what one binding holds and which contract call fills it, so whoever hits the guard knows what
    /// to bind rather than only that something is missing.
    /// </summary>
    /// <param name="set">The set index.</param>
    /// <param name="binding">The binding number.</param>
    /// <returns>A sentence fragment naming the contents and the call.</returns>
    /// <remarks>The slot names come from the two enums the contract numbers these sets by, so a message
    /// says <c>ReservedTextureSlots.MorphCompositeTexture</c> rather than <c>binding 17</c> alone.</remarks>
    public static string BoundBy(int set, int binding) => set switch
    {
        DescriptorSets.UniformBuffers =>
            $"{UniformSlotName(binding)}, filled by {nameof(ICommandList)}.{nameof(ICommandList.BindUniformBuffer)}({binding}, ...)",
        DescriptorSets.StorageBuffers =>
            $"{StorageSlotName(binding)}, filled by {nameof(ICommandList)}.{nameof(ICommandList.BindStorageBuffer)}({binding}, ...)",
        DescriptorSets.ReservedTextures =>
            $"{TextureSlotName(binding)}, filled by {nameof(ICommandList)}.{nameof(ICommandList.BindTexture)}({nameof(DescriptorSets)}.{nameof(DescriptorSets.ReservedTextures)}, {binding}, ...)",
        DescriptorSets.MaterialTextures =>
            $"a per-material texture, filled by {nameof(ICommandList)}.{nameof(ICommandList.BindTexture)}({nameof(DescriptorSets)}.{nameof(DescriptorSets.MaterialTextures)}, {binding}, ...)",
        DescriptorSets.StorageImages =>
            $"storage image unit {binding}, filled by {nameof(ICommandList)}.{nameof(ICommandList.BindStorageTexture)}({binding}, ...)",
        _ => "a set outside the contract's scheme, which nothing can bind",
    };

    private static string UniformSlotName(int binding)
        => UniformSlots.TryGetValue(binding, out var name)
            ? $"{nameof(Buffers.ReservedBufferSlots)}.{name}"
            : "an unreserved uniform buffer slot";

    private static string StorageSlotName(int binding)
        => StorageSlots.TryGetValue(binding, out var name)
            ? $"{nameof(Buffers.ReservedBufferSlots)}.{name}"
            : "an unreserved storage buffer slot";

    private static string TextureSlotName(int binding)
        => TextureSlots.TryGetValue(binding, out var name)
            ? $"{nameof(Materials.ReservedTextureSlots)}.{name}"
            : "an unreserved global texture slot";

    /// <remarks><see cref="Buffers.ReservedBufferSlots"/> overlaps its uniform and storage ranges on
    /// purpose, so the two directions cannot share one reverse map and neither can be built by asking the
    /// enum for a name. The block tables are the disambiguation the enum itself lacks.</remarks>
    private static FrozenDictionary<int, string> UniformSlots { get; } = ReverseBufferSlots(storage: false);

    private static FrozenDictionary<int, string> StorageSlots { get; } = ReverseBufferSlots(storage: true);

    private static FrozenDictionary<int, string> TextureSlots { get; } = ReverseTextureSlots();

    private static FrozenDictionary<int, string> ReverseBufferSlots(bool storage)
    {
        var blocks = storage ? Buffers.ReservedBufferBlocks.StorageBlocks : Buffers.ReservedBufferBlocks.UniformBlocks;
        var reversed = new Dictionary<int, string>();

        foreach (var (_, slot) in blocks)
        {
            // Two names share ReservedBufferSlots.CullBits, which is one buffer read and written under
            // different block names; the slot's own name is the same either way.
            reversed.TryAdd((int)slot, slot.ToString());
        }

        return reversed.ToFrozenDictionary();
    }

    private static FrozenDictionary<int, string> ReverseTextureSlots()
    {
        var reversed = new Dictionary<int, string>();

        foreach (var slot in Enum.GetValues<Materials.ReservedTextureSlots>())
        {
            // ReservedTextureSlots.Last aliases the highest real slot. Enum.GetValues collapses aliases to
            // one value, and ToString picks whichever name was declared first, which is the real one.
            reversed.TryAdd((int)slot, slot.ToString());
        }

        return reversed.ToFrozenDictionary();
    }

    /// <summary>
    /// Checks that every canonical set is narrow enough for one <see cref="MaskWidth"/> bit mask to
    /// describe it completely.
    /// </summary>
    /// <returns>A message naming the set that outgrew the mask, or <see langword="null"/> when they all
    /// fit.</returns>
    /// <remarks>
    /// The one assumption this guard rests on that a future edit could quietly break: the canonical layouts
    /// widen automatically when a slot is added to <see cref="Buffers.ReservedBufferSlots"/> or
    /// <see cref="Materials.ReservedTextureSlots"/>, and the mask does not. Silently dropping the new slot
    /// would leave exactly the hole this type exists to close, so it is reported instead. Set 3 is not
    /// checked: its numbering starts above the last reserved texture slot and is expected to pass 32, and
    /// it is the set <see cref="VulkanDescriptorBinder"/> already checks exactly.
    /// </remarks>
    public static string? EnsureCanonicalSetsFitTheMask()
    {
        var widths = new (int Set, int Width, string Source)[]
        {
            (DescriptorSets.UniformBuffers, VulkanDescriptorTypes.UniformBufferSlotCount, nameof(VulkanDescriptorTypes.UniformBufferSlotCount)),
            (DescriptorSets.StorageBuffers, VulkanDescriptorTypes.StorageBufferSlotCount, nameof(VulkanDescriptorTypes.StorageBufferSlotCount)),
            (DescriptorSets.ReservedTextures, VulkanDescriptorTypes.ReservedTextureSlotCount, nameof(VulkanDescriptorTypes.ReservedTextureSlotCount)),
            (DescriptorSets.StorageImages, VulkanDescriptorTypes.StorageImageSlotCount, nameof(VulkanDescriptorTypes.StorageImageSlotCount)),
        };

        foreach (var (set, width, source) in widths)
        {
            if (width > MaskWidth)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"Canonical set {set} declares {width} bindings ({source}), past the {MaskWidth} a {nameof(VulkanDescriptorBindingUsage)} mask holds. Bindings at or past {MaskWidth} would drop out of the guard silently, which is the unwritten-descriptor hole it exists to close. Widen the mask to a 64 bit one before adding the slot.");
            }
        }

        return null;
    }
}
