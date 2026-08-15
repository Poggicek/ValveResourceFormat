using System.Globalization;
using System.Text;
using ValveResourceFormat.Renderer.Shaders.Spirv;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Descriptors;

/// <summary>
/// Which descriptor sets a pipeline's shaders declare, carried as one bit per set, and the record-time
/// check that refuses a draw or dispatch whose sets have not all been bound.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a fuse, not a diagnostic.</b> Recording a draw whose pipeline uses a set nothing has bound
/// is undefined behaviour that reaches the GPU. The validation layer does report it
/// (<c>VkPipeline [name] uses set 3 but that set is not bound</c>) but reporting does not stop the
/// submission &#8212; it narrates the fault on the way down, and on an AMD driver the fault is a device
/// hang that can bugcheck the machine. Throwing here turns a reboot into a stack trace.
/// </para>
/// <para>
/// <b>The mask belongs to the pipeline, not to its layout.</b> Every pipeline layout this backend builds
/// declares all <see cref="DescriptorSets.Count"/> sets, canonical ones included, and
/// <see cref="VulkanPipelineLayoutCache"/> deduplicates on the resulting handles: a shader using only
/// set 0 and a shader using sets 0 and 3 land on the <i>same</i> <c>VkPipelineLayout</c>. Reading usage
/// off the layout would therefore report the union of every shader that ever shared it. Only the
/// reflection of a pipeline's own stages says which sets that pipeline uses.
/// </para>
/// <para>
/// <b>Declared, not statically accessed.</b> <see cref="SpirvReflectionResult.DescriptorBindings"/>
/// lists what the module declares with a binding decoration, which is the same list
/// <see cref="VulkanPipelineDescriptorLayouts"/> builds the layout from. Vulkan's own requirement is
/// narrower &#8212; sets the pipeline <i>statically uses</i> &#8212; so a module that kept a descriptor
/// it never reads would be refused here where Vulkan would have allowed it. That is the safe direction
/// to be wrong in: the cost of being too strict is an exception naming the resource to bind, and the
/// cost of being too lax is the hang this exists to prevent.
/// </para>
/// </remarks>
public static class VulkanDescriptorSetUsage
{
    /// <summary>A mask naming no descriptor set.</summary>
    public const int NoSets = 0;

    /// <summary>A mask naming every set the contract declares.</summary>
    public const int AllSets = (1 << DescriptorSets.Count) - 1;

    /// <summary>
    /// Gets the mask of descriptor sets a pipeline's stages declare.
    /// </summary>
    /// <param name="stages">The reflected stages. A graphics pipeline passes its vertex and fragment
    /// modules, a compute pipeline its single one. Null entries are skipped.</param>
    /// <returns>One bit per set index, or <see cref="NoSets"/> when the stages declare no descriptors.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stages"/> is <see langword="null"/>.</exception>
    /// <remarks>Meant to be called once, when the pipeline is created. A draw compares two integers.</remarks>
    public static int MaskFor(IReadOnlyList<SpirvReflectionResult> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);

        var mask = NoSets;

        for (var i = 0; i < stages.Count; i++)
        {
            mask |= MaskFor(stages[i]);
        }

        return mask;
    }

    /// <summary>
    /// Gets the mask of descriptor sets one reflected stage declares.
    /// </summary>
    /// <param name="stage">The reflected module, or <see langword="null"/> for a stage that is absent.</param>
    /// <returns>One bit per set index.</returns>
    /// <remarks>
    /// The set number is taken from the binding's own <see cref="SpirvDescriptorBinding.Set"/> &#8212; what
    /// the module decorates &#8212; and never re-derived from what kind of resource it is. Those two
    /// disagree today: <c>depth_pyramid.comp</c> has its storage images decorated into set 2 rather than
    /// set 4 because shader emission has not moved yet, and a pipeline layout must declare what the module
    /// actually reads. Deriving the set from the kind would have this guard demand a set the shader never
    /// touches while ignoring the one it does.
    /// <para>
    /// A binding outside the contract's set range is skipped rather than counted. It has already been
    /// reported by <see cref="SpirvReflection.ValidateDescriptorSets"/>, no layout declares it, and
    /// nothing could ever bind it, so counting it would make the guard fire forever.
    /// </para>
    /// </remarks>
    public static int MaskFor(SpirvReflectionResult? stage)
    {
        if (stage is null)
        {
            return NoSets;
        }

        var mask = NoSets;

        foreach (var binding in stage.DescriptorBindings)
        {
            if (binding.Set >= 0 && binding.Set < DescriptorSets.Count)
            {
                mask |= 1 << binding.Set;
            }
        }

        return mask;
    }

    /// <summary>Gets whether a mask names a set.</summary>
    /// <param name="mask">The mask.</param>
    /// <param name="set">The set index.</param>
    /// <returns><see langword="true"/> when the set is in the mask.</returns>
    public static bool Uses(int mask, int set)
        => set >= 0 && set < DescriptorSets.Count && (mask & (1 << set)) != 0;

    /// <summary>Renders a mask as its set numbers, lowest first.</summary>
    /// <param name="mask">The mask.</param>
    /// <returns>Something like <c>0, 1 and 3</c>, or <c>none</c> for an empty mask.</returns>
    public static string Describe(int mask)
    {
        var sets = new List<int>(DescriptorSets.Count);

        for (var set = 0; set < DescriptorSets.Count; set++)
        {
            if ((mask & (1 << set)) != 0)
            {
                sets.Add(set);
            }
        }

        if (sets.Count == 0)
        {
            return "none";
        }

        var text = new StringBuilder();

        for (var i = 0; i < sets.Count; i++)
        {
            if (i > 0)
            {
                text.Append(i == sets.Count - 1 ? " and " : ", ");
            }

            text.Append(CultureInfo.InvariantCulture, $"{sets[i]}");
        }

        return text.ToString();
    }

    /// <summary>
    /// Says what a set holds and which contract call fills it, so whoever hits the guard knows what to
    /// bind rather than only that something is missing.
    /// </summary>
    /// <param name="set">The set index.</param>
    /// <returns>A sentence fragment naming the contents and the call.</returns>
    public static string BoundBy(int set) => set switch
    {
        DescriptorSets.UniformBuffers =>
            $"the uniform buffers, filled by {nameof(ICommandList)}.{nameof(ICommandList.BindUniformBuffer)}",
        DescriptorSets.StorageBuffers =>
            $"the storage buffers, filled by {nameof(ICommandList)}.{nameof(ICommandList.BindStorageBuffer)}",
        DescriptorSets.ReservedTextures =>
            $"the globally reserved textures, filled by {nameof(ICommandList)}.{nameof(ICommandList.BindTexture)}({nameof(DescriptorSets)}.{nameof(DescriptorSets.ReservedTextures)}, ...)",
        DescriptorSets.MaterialTextures =>
            $"the per-material textures, filled by {nameof(ICommandList)}.{nameof(ICommandList.BindTexture)}({nameof(DescriptorSets)}.{nameof(DescriptorSets.MaterialTextures)}, ...)",
        DescriptorSets.StorageImages =>
            $"the storage images, filled by {nameof(ICommandList)}.{nameof(ICommandList.BindStorageTexture)}",
        _ => "a set outside the contract's scheme, which nothing can bind",
    };

    /// <summary>
    /// Refuses to let a draw or dispatch be recorded while a set the pipeline uses is unbound.
    /// </summary>
    /// <param name="pipelineName">The bound pipeline, named in the message.</param>
    /// <param name="usedSets">The mask the pipeline carries, from <see cref="MaskFor(IReadOnlyList{SpirvReflectionResult})"/>.</param>
    /// <param name="boundSets">The mask the descriptor binder reports as bound on this command buffer.</param>
    /// <exception cref="InvalidOperationException">A set the pipeline uses is not bound. The message names
    /// the pipeline, every missing set, and the call that should have bound each one.</exception>
    /// <remarks>
    /// Two integer reads and an <c>and</c> in the common case, which is why this is always on rather than
    /// behind a <c>DEBUG</c> guard. See the type's remarks for what it is standing in front of.
    /// </remarks>
    public static void EnsureBound(string pipelineName, int usedSets, int boundSets)
    {
        var missing = usedSets & ~boundSets;

        if (missing == NoSets)
        {
            return;
        }

        Throw(pipelineName, usedSets, missing);
    }

    /// <summary>Builds and throws the message, kept out of <see cref="EnsureBound"/> so the passing path
    /// stays small enough to inline.</summary>
    private static void Throw(string pipelineName, int usedSets, int missing)
    {
        var count = BitOperations.PopCount((uint)missing);
        var usedCount = BitOperations.PopCount((uint)usedSets);

        var message = new StringBuilder();

        message.Append(CultureInfo.InvariantCulture,
            $"Pipeline '{pipelineName}' uses descriptor {(usedCount == 1 ? "set" : "sets")} {Describe(usedSets)}, ");
        message.Append(CultureInfo.InvariantCulture,
            $"but {(count == 1 ? "set" : "sets")} {Describe(missing)} {(count == 1 ? "was" : "were")} never bound on this command list.");

        for (var set = 0; set < DescriptorSets.Count; set++)
        {
            if ((missing & (1 << set)) != 0)
            {
                message.Append(CultureInfo.InvariantCulture, $" Set {set} holds {BoundBy(set)}.");
            }
        }

        message.Append(
            " Recording the draw anyway is undefined behaviour that reaches the GPU: the validation layer reports it and the submission still happens, which on a real device hangs it.");

        throw new InvalidOperationException(message.ToString());
    }
}
