using System.Globalization;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Descriptors;

/// <summary>What the binder has been doing, for a diagnostics overlay or a smoke test.</summary>
/// <param name="Flushes">How many times <see cref="VulkanDescriptorBinder.Flush"/> was called, which is
/// once per draw and dispatch.</param>
/// <param name="NoOpFlushes">How many of those issued no Vulkan call at all: either nothing changed, or
/// nothing that changed was declared by the bound pipeline.</param>
/// <param name="SetsAllocated">Descriptor sets taken from the frame chain.</param>
/// <param name="DescriptorsWritten">Individual descriptor writes.</param>
/// <param name="UpdateCalls">Calls to <c>vkUpdateDescriptorSets</c>. The ratio of
/// <paramref name="DescriptorsWritten"/> to this is what the writer's batching buys.</param>
/// <param name="BindCalls">Calls to <c>vkCmdBindDescriptorSets</c>.</param>
/// <param name="LayoutChanges">How many flushes saw a different pipeline layout than the one before and
/// had to rebind every set.</param>
/// <param name="FilteredWrites">Accumulated bindings that were <i>not</i> written, because the set layout
/// the bound pipeline declares at that index declares no such binding. Never zero for long in a scene
/// that draws several materials: see the type remarks on why a binding outlives the pipeline it was
/// recorded under. A count that grows without a material or shader change is the shape of a caller
/// binding to the wrong slot.</param>
public readonly record struct VulkanDescriptorBinderStatistics(
    long Flushes,
    long NoOpFlushes,
    long SetsAllocated,
    long DescriptorsWritten,
    long UpdateCalls,
    long BindCalls,
    long LayoutChanges,
    long FilteredWrites);

/// <summary>
/// Turns the contract's immediate-mode binding calls into descriptor sets, and is the piece that joins
/// <see cref="VulkanDescriptorAllocator"/>, <see cref="VulkanDescriptorWriter"/> and
/// <see cref="VulkanDescriptorLayoutCache"/> to <see cref="VulkanCommandList"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The strategy is allocate-and-write per change.</b> Bindings accumulate as they arrive; at each
/// flush, any set whose contents changed gets a <i>fresh</i> descriptor set from the frame chain, has
/// every one of its accumulated bindings written into it, and is bound. Rewriting a set a recorded draw
/// already references would change what that draw reads, so a changed set is never reused &#8212; this
/// is the single rule the whole design turns on. Push descriptors would avoid the allocation entirely,
/// but <c>VK_KHR_push_descriptor</c> is not among the extensions
/// <see cref="Core.VulkanCoreDevice"/> enables, so that is a change to make deliberately rather than a
/// strategy to assume.
/// </para>
/// <para>
/// <b>A recorded binding is sticky command-list state, and it is scoped to nothing.</b> The contract's
/// binding calls are OpenGL's, where <c>glBindTextureUnit</c> settles a unit until something else
/// settles it and each program reads only the units it declares a sampler for. Bindings therefore
/// outlive the pipeline that was bound when they were recorded, deliberately: the renderer binds the
/// reserved globals once per pass, before any pipeline exists, and every draw in that pass reads them.
/// Scoping a binding to the pipeline it was recorded under, or clearing the pending set when the
/// pipeline changes, would discard exactly those pass-wide binds and fail the following draw.
/// </para>
/// <para>
/// <b>What a pipeline reads is decided when the set is materialised, not when the binding is
/// recorded.</b> A set is written against the set layout the <i>bound</i> pipeline declares at that
/// index, and only the bindings that layout declares are written; the rest are counted in
/// <see cref="VulkanDescriptorBinderStatistics.FilteredWrites"/> and dropped. That is not a guess. A
/// set layout is built by <see cref="VulkanPipelineDescriptorLayouts"/> from the SPIR-V reflection of
/// that pipeline's own stages, so a binding it does not declare is one no stage of the draw can
/// address &#8212; unreadable by construction rather than merely unread, and inert on the OpenGL oracle
/// for the same reason. Writing it anyway is what made a material's set 3 slot blow up on the next
/// material's draw.
/// </para>
/// <para>
/// <b>The dangerous direction stays loud, and got louder.</b> The failure worth catching is the
/// opposite one &#8212; a descriptor the shader reads that nothing wrote, which is the undefined read
/// that hangs a device. At set granularity <see cref="VulkanDescriptorSetUsage.EnsureBound"/> catches
/// it at the draw. At binding granularity <see cref="EnsureDeclaredBindingsBound"/> catches it here,
/// for every set whose layout is <i>reflected</i> rather than canonical: a reflected layout is literally
/// the declaration of the pipeline's stages, so every binding in it is read by the draw and every one
/// must have been bound. A canonical layout declares a whole reserved range whether a shader reads it
/// or not, so no such conclusion can be drawn from it and none is.
/// </para>
/// <para>
/// <b>Three kinds of staleness, tracked separately, because they cost differently.</b> A set whose
/// contents changed needs a new set and a write. A set materialised against a different set layout
/// needs the same, because the fresh set is allocated from that layout and the filter's answer depends
/// on it &#8212; and because binding a set allocated from one layout at an index the pipeline layout
/// declares as another is not legal Vulkan. A set that is merely no longer bound, because the pipeline
/// layout changed under it while its own set layout did not, needs only a rebind of the set it already
/// has. Collapsing the last into the first two would allocate on every pipeline change, which for a
/// scene sorted by material is most draws.
/// </para>
/// <para>
/// <b>A layout change invalidates conservatively.</b> Vulkan invalidates bound sets from the first index
/// at which two pipeline layouts stop being compatible; working out that index exactly would mean
/// comparing set layouts pairwise, so this rebinds every set instead. It is cheap because it is rare:
/// the canonical sets exist precisely so that pipelines share layouts, and the pipeline smoke test sees
/// 49 pipelines resolve to 3 layouts.
/// </para>
/// <para>
/// <b>Not thread safe</b>, matching the command list it serves. The layout caches underneath it are.
/// </para>
/// </remarks>
public sealed unsafe class VulkanDescriptorBinder : IVulkanDescriptorBinder, IDisposable
{
    private readonly Vk Api;
    private readonly VulkanDescriptorLayoutCache Layouts;
    private readonly VulkanPipelineLayoutCache PipelineLayouts;
    private readonly VulkanDescriptorAllocator Allocator;
    private readonly VulkanDescriptorWriter Writer;
    private readonly bool OwnsAllocator;

    private readonly SetState[] Sets = new SetState[DescriptorSets.Count];
    private readonly Dictionary<ulong, LayoutPlan> ResolvedLayouts = [];

    private PipelineLayout LastLayout;
    private PipelineBindPoint LastBindPoint;
    private int BoundSets;
    private bool Disposed;

    private long FlushCount;
    private long NoOpFlushCount;
    private long SetsAllocatedCount;
    private long DescriptorsWrittenCount;
    private long UpdateCallCount;
    private long BindCallCount;
    private long LayoutChangeCount;
    private long FilteredWriteCount;

    /// <summary>Gets what the binder has been doing.</summary>
    public VulkanDescriptorBinderStatistics Statistics => new(
        FlushCount,
        NoOpFlushCount,
        SetsAllocatedCount,
        DescriptorsWrittenCount,
        UpdateCallCount,
        BindCallCount,
        LayoutChangeCount,
        FilteredWriteCount);

    /// <summary>Gets the allocator the per-draw sets come from.</summary>
    public VulkanDescriptorAllocator DescriptorAllocator => Allocator;

    /// <inheritdoc/>
    /// <remarks>Accumulated by <see cref="Flush"/> in the same pass that decides what to bind, so it is
    /// the loop's own answer rather than a second opinion about it.</remarks>
    public int BoundDescriptorSets => BoundSets;

    /// <summary>Creates a binder that owns its allocator.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="debugNames">Used to name the pools.</param>
    /// <param name="frameRing">The ring the per-frame chains follow.</param>
    /// <param name="layouts">The descriptor set layout cache, used to resolve a raw set layout handle
    /// back to its binding table.</param>
    /// <param name="pipelineLayouts">The pipeline layout cache, used to resolve the bare
    /// <c>VkPipelineLayout</c> that <see cref="Flush"/> is given.</param>
    /// <param name="sizes">Pool sizing, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public VulkanDescriptorBinder(
        Vk api,
        Device device,
        VulkanDebugNames debugNames,
        VulkanFrameRing frameRing,
        VulkanDescriptorLayoutCache layouts,
        VulkanPipelineLayoutCache pipelineLayouts,
        VulkanDescriptorPoolSizes? sizes = null)
        : this(
            api,
            layouts,
            pipelineLayouts,
            new VulkanDescriptorAllocator(api, device, debugNames, frameRing, layouts, sizes),
            ownsAllocator: true)
    {
    }

    /// <summary>Creates a binder over an allocator the caller already owns.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="layouts">The descriptor set layout cache.</param>
    /// <param name="pipelineLayouts">The pipeline layout cache.</param>
    /// <param name="allocator">Where per-draw sets come from.</param>
    /// <param name="ownsAllocator">Whether disposing this binder disposes <paramref name="allocator"/>.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public VulkanDescriptorBinder(
        Vk api,
        VulkanDescriptorLayoutCache layouts,
        VulkanPipelineLayoutCache pipelineLayouts,
        VulkanDescriptorAllocator allocator,
        bool ownsAllocator = false)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(layouts);
        ArgumentNullException.ThrowIfNull(pipelineLayouts);
        ArgumentNullException.ThrowIfNull(allocator);

        Api = api;
        Layouts = layouts;
        PipelineLayouts = pipelineLayouts;
        Allocator = allocator;
        OwnsAllocator = ownsAllocator;
        Writer = new VulkanDescriptorWriter(api, allocator.Device);

        for (var set = 0; set < Sets.Length; set++)
        {
            Sets[set] = new SetState();
        }
    }

    /// <inheritdoc/>
    /// <remarks>The command buffer is not retained. It is taken so that a binder which one day records
    /// something at reset time has it, and so the seam does not have to change when one does.</remarks>
    public void Reset(CommandBuffer commandBuffer)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        foreach (var state in Sets)
        {
            state.Clear();
        }

        // Nothing is bound on a command buffer that has just begun, whatever was bound on the last one.
        LastLayout = default;
        LastBindPoint = default;
        BoundSets = 0;
        Writer.Discard();
    }

    /// <inheritdoc/>
    public void BindUniformBuffer(int binding, Silk.NET.Vulkan.Buffer buffer, ulong offsetInBytes, ulong sizeInBytes)
        => Record(DescriptorSets.UniformBuffers, binding, Pending.ForBuffer(buffer, offsetInBytes, sizeInBytes));

    /// <inheritdoc/>
    public void BindStorageBuffer(int binding, Silk.NET.Vulkan.Buffer buffer, ulong offsetInBytes, ulong sizeInBytes)
        => Record(DescriptorSets.StorageBuffers, binding, Pending.ForBuffer(buffer, offsetInBytes, sizeInBytes));

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="descriptorSet"/> is not a texture set.</exception>
    public void BindSampledImage(int descriptorSet, int binding, ImageView view, Sampler sampler, ImageLayout layout)
    {
        if (descriptorSet is not (DescriptorSets.ReservedTextures or DescriptorSets.MaterialTextures))
        {
            throw new ArgumentOutOfRangeException(nameof(descriptorSet), descriptorSet,
                $"Only sets {DescriptorSets.ReservedTextures} and {DescriptorSets.MaterialTextures} hold sampled textures.");
        }

        Record(descriptorSet, binding, Pending.ForImage(view, sampler, layout));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Routed to <see cref="DescriptorSets.StorageImages"/>, not to the reserved texture set the
    /// interface's own summary still names. That set is why set 4 exists: an image unit is a third index
    /// space OpenGL keeps apart from texture units, and images at 0 to 3 land squarely on the BRDF
    /// lookup, blue noise, fog cube and first lightmap slots.
    /// </remarks>
    public void BindStorageImage(int binding, ImageView view, ImageLayout layout)
        => Record(DescriptorSets.StorageImages, binding, Pending.ForImage(view, default, layout));

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The pipeline layout did not come from the pipeline
    /// layout cache, a bound binding is typed differently by the shader that declares it, or a reflected
    /// set layout declares a binding nothing bound. A binding the bound pipeline does not declare at all
    /// is <i>not</i> an error: it is filtered out, for the reasons on this type.</exception>
    public void Flush(CommandBuffer commandBuffer, PipelineBindPoint bindPoint, PipelineLayout layout)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        FlushCount++;

        // Descriptor set bindings are per bind point, and a layout change invalidates them from the
        // first incompatible index, so either one means nothing can be assumed still bound.
        if (layout.Handle != LastLayout.Handle || bindPoint != LastBindPoint)
        {
            if (LastLayout.Handle != 0)
            {
                LayoutChangeCount++;
            }

            foreach (var state in Sets)
            {
                state.Bound = false;
            }

            LastLayout = layout;
            LastBindPoint = bindPoint;
        }

        var plan = ResolveFor(layout);
        var written = 0;
        var toBind = 0;
        var bound = 0;

        Span<DescriptorSet> handles = stackalloc DescriptorSet[DescriptorSets.Count];
        Span<int> indices = stackalloc int[DescriptorSets.Count];

        for (var set = 0; set < Sets.Length; set++)
        {
            var state = Sets[set];

            if (state.Bindings.Count == 0)
            {
                continue;
            }

            var setLayout = plan.SetLayouts[set]
                ?? throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                    $"A binding was recorded into descriptor set {set}, but the bound pipeline layout declares nothing this layer created there. Its set layout at that index did not come from this binder's layout cache, so what the shader declares there cannot be established and the bindings cannot be filtered against it."));

            // Contents changing and the set layout changing are separate reasons to build a new set, and
            // the second is not the same as the pipeline layout changing: two pipeline layouts routinely
            // share a canonical set layout at an index, and then the set already built is still valid
            // there and only needs rebinding.
            if (state.ContentsDirty || !ReferenceEquals(state.MaterializedFor, setLayout))
            {
                written += Materialize(state, setLayout, plan.Reflected[set]);
            }

            // Nothing the bound pipeline declares was among the accumulated bindings, so there is no set
            // to bind and this index stays out of the bound mask. A pipeline that does read the set then
            // fails the draw-time guard naming it, which is the right error rather than this being one.
            if (!state.HasSet)
            {
                continue;
            }

            if (!state.Bound)
            {
                handles[toBind] = state.Current;
                indices[toBind] = set;
                toBind++;
                state.Bound = true;
            }

            // Every set that reaches here holds bindings and is now bound for this layout and bind point,
            // which is exactly what the draw-time guard needs to compare a pipeline's used sets against.
            bound |= 1 << set;
        }

        BoundSets = bound;

        if (written == 0 && toBind == 0)
        {
            NoOpFlushCount++;
            return;
        }

        if (written > 0)
        {
            // One call for every set this flush touched, which is the whole reason the writer batches.
            Writer.Flush();
            DescriptorsWrittenCount += written;
            UpdateCallCount++;
        }

        BindRuns(commandBuffer, bindPoint, layout, handles[..toBind], indices[..toBind]);
    }

    /// <summary>
    /// Issues <c>vkCmdBindDescriptorSets</c> over runs of consecutive set indices.
    /// </summary>
    /// <remarks>The call takes a first set and an array, so sets 0, 1 and 2 are one call while 0 and 4
    /// are two. Worth grouping because the common frame binds the buffer sets together.</remarks>
    private void BindRuns(
        CommandBuffer commandBuffer,
        PipelineBindPoint bindPoint,
        PipelineLayout layout,
        ReadOnlySpan<DescriptorSet> handles,
        ReadOnlySpan<int> indices)
    {
        var start = 0;

        while (start < indices.Length)
        {
            var end = start + 1;

            while (end < indices.Length && indices[end] == indices[end - 1] + 1)
            {
                end++;
            }

            fixed (DescriptorSet* p = handles[start..end])
            {
                Api.CmdBindDescriptorSets(
                    commandBuffer,
                    bindPoint,
                    layout,
                    (uint)indices[start],
                    (uint)(end - start),
                    p,
                    0,
                    null);
            }

            BindCallCount++;
            start = end;
        }
    }

    /// <summary>
    /// Builds a fresh descriptor set for one index and writes the accumulated bindings the given set
    /// layout actually declares into it.
    /// </summary>
    /// <param name="state">The set's accumulated bindings and current materialisation.</param>
    /// <param name="setLayout">The layout the bound pipeline declares at this index.</param>
    /// <param name="reflected">Whether that layout is the pipeline's own reflected declaration rather
    /// than a shared canonical one, which is what makes the completeness check meaningful.</param>
    /// <returns>How many descriptors were queued on the writer.</returns>
    /// <remarks>The set is allocated lazily, on the first binding that survives the filter, so a set
    /// whose accumulated bindings are all foreign to this pipeline costs no allocation at all.</remarks>
    private int Materialize(SetState state, VulkanDescriptorSetLayout setLayout, bool reflected)
    {
        if (reflected)
        {
            // Before anything is queued, so a throw does not leave half a set on the writer.
            EnsureDeclaredBindingsBound(state, setLayout);
        }

        state.MaterializedFor = setLayout;
        state.ContentsDirty = false;
        state.Bound = false;
        state.HasSet = false;
        state.Current = default;

        var written = 0;

        foreach (var (binding, pending) in state.Bindings)
        {
            if (!setLayout.TryGetBinding(binding, out _))
            {
                FilteredWriteCount++;
                continue;
            }

            if (!state.HasSet)
            {
                state.Current = Allocator.AllocateForFrame(setLayout);
                state.HasSet = true;
                SetsAllocatedCount++;
            }

            if (pending.IsImage)
            {
                Writer.WriteImageHandle(state.Current, setLayout, binding, pending.View, pending.Sampler, pending.ImageLayout);
            }
            else
            {
                Writer.WriteBufferHandle(state.Current, setLayout, binding, pending.Buffer, pending.Offset, pending.Size);
            }

            written++;
        }

        return written;
    }

    /// <summary>
    /// Refuses to build a set from a reflected layout while a binding that layout declares has nothing
    /// bound to it.
    /// </summary>
    /// <param name="state">The set's accumulated bindings.</param>
    /// <param name="setLayout">The reflected layout, which is the declaration of the bound pipeline's own
    /// stages.</param>
    /// <exception cref="InvalidOperationException">A declared binding was never bound.</exception>
    /// <remarks>
    /// <para>
    /// The companion to the filter, and the reason filtering is not a loosening. A reflected set layout
    /// is built from nothing but the SPIR-V of the pipeline's stages, so every binding in it is one the
    /// draw reads. Leaving one unwritten is a descriptor read of undefined contents &#8212; the failure
    /// class that hangs a device rather than the one that renders a wrong pixel &#8212; and it is exactly
    /// what a caller binding a material texture to the wrong slot produces once the wrong slot is
    /// silently filtered away.
    /// </para>
    /// <para>
    /// Only for reflected layouts. A canonical layout declares a whole reserved range whether any shader
    /// reads it or not, so an unbound binding there says nothing at all; that case is left to the
    /// set-granularity guard at the draw.
    /// </para>
    /// <para>
    /// Not reached for a set nothing has bound anything into, because the flush skips those before it
    /// looks at a layout. <see cref="VulkanDescriptorSetUsage.EnsureBound"/> is what names that one.
    /// </para>
    /// <para>
    /// <b>It asks whether a binding is bound, not whether it was bound for this pipeline</b>, and that is
    /// the one hole left. A slot the previous material set and this one did not would satisfy this check
    /// while writing the previous material's texture. Closing it needs a binding to carry which pipeline
    /// recorded it, which is the scoping this type's remarks reject for the pass-wide globals, so it is
    /// left open deliberately: <c>MeshBatchRenderer.BindMaterialTextures</c> restates every slot the
    /// shader declares on every material change, so the case does not arise for the path that has one.
    /// </para>
    /// </remarks>
    private static void EnsureDeclaredBindingsBound(SetState state, VulkanDescriptorSetLayout setLayout)
    {
        foreach (var declared in setLayout.Bindings)
        {
            if (state.Bindings.ContainsKey(declared.Binding))
            {
                continue;
            }

            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Set {setLayout.SetIndex} of '{setLayout.Name}' declares binding {declared.Binding} as a {declared.Type}, and nothing bound anything there on this command list. That layout is the bound pipeline's own reflected declaration, so its shaders do read that binding, and a descriptor left unwritten reads undefined contents on the GPU. Bind it, or check whether the caller meant one of the {state.Bindings.Count} binding(s) it did set on this set."));
        }
    }

    private void Record(int set, int binding, in Pending pending)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(binding);

        var state = Sets[set];

        // A binding rewritten with exactly what it already holds is not a change, and treating it as one
        // would allocate a fresh set for a draw that reads the same descriptors. The renderer rebinds the
        // reserved globals per pass and the same material's textures across a batch, so this is the
        // common case rather than a corner.
        if (state.Bindings.TryGetValue(binding, out var existing) && existing.Equals(pending))
        {
            return;
        }

        state.Bindings[binding] = pending;
        state.ContentsDirty = true;
    }

    /// <summary>
    /// Resolves a bare pipeline layout handle into the set layouts behind it, and which of those are the
    /// pipeline's own reflected declarations rather than shared canonical layouts.
    /// </summary>
    /// <param name="layout">The bound pipeline's layout.</param>
    /// <returns>The plan, cached per pipeline layout handle.</returns>
    /// <exception cref="InvalidOperationException">The layout did not come from the pipeline layout cache.</exception>
    /// <remarks>The canonical comparison is made here, once per pipeline layout, rather than per flush:
    /// <see cref="VulkanDescriptorLayoutCache.Canonical"/> creates its layout on first use, and a draw
    /// should not be the thing that discovers it.</remarks>
    private LayoutPlan ResolveFor(PipelineLayout layout)
    {
        if (ResolvedLayouts.TryGetValue(layout.Handle, out var cached))
        {
            return cached;
        }

        if (!PipelineLayouts.TryGetByHandle(layout, out var pipelineLayout))
        {
            throw new InvalidOperationException(
                $"That {nameof(PipelineLayout)} did not come from the pipeline layout cache this binder was given, so the descriptor set layouts behind it cannot be found. A pipeline layout built by hand cannot have descriptor sets allocated for it.");
        }

        var resolved = new VulkanDescriptorSetLayout?[DescriptorSets.Count];
        var reflected = new bool[DescriptorSets.Count];

        for (var set = 0; set < resolved.Length && set < pipelineLayout.SetLayouts.Count; set++)
        {
            if (!Layouts.TryResolve(pipelineLayout.SetLayouts[set], out var setLayout))
            {
                continue;
            }

            resolved[set] = setLayout;
            reflected[set] = !ReferenceEquals(setLayout, Layouts.Canonical(set));
        }

        var plan = new LayoutPlan(resolved, reflected);

        ResolvedLayouts[layout.Handle] = plan;
        return plan;
    }

    /// <summary>Releases the allocator, when this binder owns it.</summary>
    /// <remarks>Only legal once nothing referencing the sets is executing.</remarks>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;
        ResolvedLayouts.Clear();

        if (OwnsAllocator)
        {
            Allocator.Dispose();
        }
    }

    /// <summary>One accumulated binding, of either kind.</summary>
    /// <remarks>One struct rather than two so a set holds a single dictionary, and equatable so that
    /// rebinding the same resource is recognised as no change.</remarks>
    private readonly record struct Pending(
        bool IsImage,
        Silk.NET.Vulkan.Buffer Buffer,
        ulong Offset,
        ulong Size,
        ImageView View,
        Sampler Sampler,
        ImageLayout ImageLayout)
    {
        internal static Pending ForBuffer(Silk.NET.Vulkan.Buffer buffer, ulong offset, ulong size)
            => new(false, buffer, offset, size, default, default, default);

        internal static Pending ForImage(ImageView view, Sampler sampler, ImageLayout layout)
            => new(true, default, 0, 0, view, sampler, layout);
    }

    /// <summary>
    /// What one pipeline layout declares at each set index, and which of those declarations came from the
    /// pipeline's own shaders.
    /// </summary>
    /// <param name="SetLayouts">The set layout at each index, or <see langword="null"/> when the pipeline
    /// layout declares one this binder's cache does not know.</param>
    /// <param name="Reflected">Whether the entry at that index is a reflected layout rather than the
    /// shared canonical one. True for every index of set 3, which has no canonical layout because its
    /// contents are assigned per shader.</param>
    private sealed record LayoutPlan(VulkanDescriptorSetLayout?[] SetLayouts, bool[] Reflected);

    /// <summary>What one descriptor set has accumulated, and whether it is current on the command buffer.</summary>
    private sealed class SetState
    {
        internal readonly Dictionary<int, Pending> Bindings = [];

        /// <summary>A binding changed, so the next flush needs a new set rather than a rewrite.</summary>
        internal bool ContentsDirty;

        /// <summary>The set is bound at its index for the current pipeline layout and bind point.</summary>
        internal bool Bound;

        /// <summary><see cref="Current"/> holds a set built from <see cref="MaterializedFor"/>. False when
        /// the last materialisation found nothing that layout declares, which is not the same as the set
        /// being unbound: there is no set at all.</summary>
        internal bool HasSet;

        /// <summary>The set layout <see cref="Current"/> was allocated from and filtered against, or
        /// <see langword="null"/> before the first flush. Compared by reference, which is exact: the
        /// layout cache hands out one object per distinct binding table.</summary>
        internal VulkanDescriptorSetLayout? MaterializedFor;

        internal DescriptorSet Current;

        internal void Clear()
        {
            Bindings.Clear();
            ContentsDirty = false;
            Bound = false;
            HasSet = false;
            MaterializedFor = null;
            Current = default;
        }
    }
}
