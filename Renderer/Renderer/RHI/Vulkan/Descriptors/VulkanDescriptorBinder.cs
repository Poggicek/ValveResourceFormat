using System.Globalization;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Descriptors;

/// <summary>What the binder has been doing, for a diagnostics overlay or a smoke test.</summary>
/// <param name="Flushes">How many times <see cref="VulkanDescriptorBinder.Flush"/> was called, which is
/// once per draw and dispatch.</param>
/// <param name="NoOpFlushes">How many of those found nothing changed and issued no Vulkan call at all.</param>
/// <param name="SetsAllocated">Descriptor sets taken from the frame chain.</param>
/// <param name="DescriptorsWritten">Individual descriptor writes.</param>
/// <param name="UpdateCalls">Calls to <c>vkUpdateDescriptorSets</c>. The ratio of
/// <paramref name="DescriptorsWritten"/> to this is what the writer's batching buys.</param>
/// <param name="BindCalls">Calls to <c>vkCmdBindDescriptorSets</c>.</param>
/// <param name="LayoutChanges">How many flushes saw a different pipeline layout than the one before and
/// had to rebind every set.</param>
public readonly record struct VulkanDescriptorBinderStatistics(
    long Flushes,
    long NoOpFlushes,
    long SetsAllocated,
    long DescriptorsWritten,
    long UpdateCalls,
    long BindCalls,
    long LayoutChanges);

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
/// <b>Two kinds of staleness, tracked separately, because they cost differently.</b> A set whose
/// contents changed needs a new set and a write. A set whose contents are unchanged but which is no
/// longer bound &#8212; because the pipeline layout changed under it &#8212; needs only a rebind of the
/// set it already has. Collapsing the two would allocate on every pipeline change, which for a scene
/// sorted by material is most draws.
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
    private readonly Dictionary<ulong, VulkanDescriptorSetLayout?[]> ResolvedLayouts = [];

    private PipelineLayout LastLayout;
    private PipelineBindPoint LastBindPoint;
    private bool Disposed;

    private long FlushCount;
    private long NoOpFlushCount;
    private long SetsAllocatedCount;
    private long DescriptorsWrittenCount;
    private long UpdateCallCount;
    private long BindCallCount;
    private long LayoutChangeCount;

    /// <summary>Gets what the binder has been doing.</summary>
    public VulkanDescriptorBinderStatistics Statistics => new(
        FlushCount,
        NoOpFlushCount,
        SetsAllocatedCount,
        DescriptorsWrittenCount,
        UpdateCallCount,
        BindCallCount,
        LayoutChangeCount);

    /// <summary>Gets the allocator the per-draw sets come from.</summary>
    public VulkanDescriptorAllocator DescriptorAllocator => Allocator;

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
    /// layout cache, or a binding does not match what the shader declared.</exception>
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

        var resolved = ResolveFor(layout);
        var written = 0;
        var toBind = 0;

        Span<DescriptorSet> handles = stackalloc DescriptorSet[DescriptorSets.Count];
        Span<int> indices = stackalloc int[DescriptorSets.Count];

        for (var set = 0; set < Sets.Length; set++)
        {
            var state = Sets[set];

            if (state.Bindings.Count == 0)
            {
                continue;
            }

            if (state.ContentsDirty)
            {
                var setLayout = resolved[set]
                    ?? throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                        $"A binding was recorded into descriptor set {set}, but the bound pipeline layout declares nothing this layer created there. The shader does not use that set."));

                state.Current = Allocator.AllocateForFrame(setLayout);
                SetsAllocatedCount++;

                foreach (var (binding, pending) in state.Bindings)
                {
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

                state.ContentsDirty = false;
                state.Bound = false;
            }

            if (!state.Bound)
            {
                handles[toBind] = state.Current;
                indices[toBind] = set;
                toBind++;
                state.Bound = true;
            }
        }

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

    private VulkanDescriptorSetLayout?[] ResolveFor(PipelineLayout layout)
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

        for (var set = 0; set < resolved.Length && set < pipelineLayout.SetLayouts.Count; set++)
        {
            resolved[set] = Layouts.TryResolve(pipelineLayout.SetLayouts[set], out var setLayout) ? setLayout : null;
        }

        ResolvedLayouts[layout.Handle] = resolved;
        return resolved;
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

    /// <summary>What one descriptor set has accumulated, and whether it is current on the command buffer.</summary>
    private sealed class SetState
    {
        internal readonly Dictionary<int, Pending> Bindings = [];

        /// <summary>A binding changed, so the next flush needs a new set rather than a rewrite.</summary>
        internal bool ContentsDirty;

        /// <summary>The set is bound at its index for the current pipeline layout and bind point.</summary>
        internal bool Bound;

        internal DescriptorSet Current;

        internal void Clear()
        {
            Bindings.Clear();
            ContentsDirty = false;
            Bound = false;
            Current = default;
        }
    }
}
