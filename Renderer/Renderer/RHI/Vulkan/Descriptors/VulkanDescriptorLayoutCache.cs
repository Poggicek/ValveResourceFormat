using System.Collections.Immutable;
using System.Globalization;
using System.Threading;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Descriptors;

/// <summary>
/// Hands out one <c>VkDescriptorSetLayout</c> per distinct binding table, and owns every layout it
/// creates.
/// </summary>
/// <remarks>
/// <para>
/// Deduplication is not a memory optimisation. Vulkan decides whether two pipeline layouts are
/// compatible for a given set by comparing the layout <i>objects</i>, and rebinding an incompatible
/// pipeline layout invalidates the sets bound at and after the first set that differs. Two shaders that
/// declare identical tables must therefore receive the same object, or binding one pipeline after
/// another silently drops the descriptors the renderer bound once per pass.
/// </para>
/// <para>
/// <b>Sets 0, 1 and 2 are canonical rather than reflected.</b> The contract fixes their binding numbers
/// to <see cref="Buffers.ReservedBufferSlots"/> and <see cref="Materials.ReservedTextureSlots"/>, so
/// this cache declares every slot in each of those sets once, for every pipeline, whether or not a
/// given shader uses them. A layout may declare more than the shader reads &#8212; only descriptors a
/// pipeline <i>statically uses</i> have to be written &#8212; and the payoff is that all three sets are
/// one shared object, bound once per pass rather than once per pipeline. Reflecting them per shader
/// would give a different layout to every shader that happens to declare a different subset of the
/// globals, and every pipeline change would then invalidate them.
/// </para>
/// <para>
/// <b>Set 3 is always reflected</b>, because there is nothing canonical to declare: its numbering is
/// assigned per shader by <see cref="Materials.RenderMaterial.CollectTextureBindings"/>, in declaration
/// order, and the same slot means a different texture in a different shader.
/// </para>
/// <para>
/// <b>Set 4, storage images, is canonical like sets 0 to 2</b>, at the width OpenGL guarantees for image
/// units. It exists at all because of what this cache could not express before it did: image units are a
/// third index space that OpenGL keeps apart from texture units, and the numbers collide head-on with
/// the reserved textures. See <see cref="DescriptorSets.StorageImages"/>.
/// </para>
/// <para>
/// <b>Thread safe</b>, unlike most of this backend. Pipeline creation is explicitly allowed to run on
/// several threads, and a pipeline layout cannot be built without asking this for its set layouts, so
/// the alternative would be to push the lock out into every caller.
/// </para>
/// </remarks>
public sealed class VulkanDescriptorLayoutCache : IDisposable
{
    private readonly Vk Api;
    private readonly Device Device;
    private readonly VulkanDebugNames DebugNames;
    private readonly Action<bool>? OnLookup;
    private readonly Dictionary<LayoutKey, VulkanDescriptorSetLayout> Layouts = [];
    private readonly Dictionary<ulong, VulkanDescriptorSetLayout> ByHandle = [];
    private readonly VulkanDescriptorSetLayout?[] EmptyLayouts = new VulkanDescriptorSetLayout?[DescriptorSets.Count];
    private readonly Lock Gate = new();

    private VulkanDescriptorSetLayout? UniformBuffersLayout;
    private VulkanDescriptorSetLayout? StorageBuffersLayout;
    private VulkanDescriptorSetLayout? ReservedTexturesLayout;
    private VulkanDescriptorSetLayout? StorageImagesLayout;
    private bool Disposed;

    /// <summary>Gets how many distinct layouts have been created.</summary>
    public int LayoutCount
    {
        get
        {
            lock (Gate)
            {
                return Layouts.Count;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the canonical layouts are used at all.
    /// </summary>
    /// <remarks>False on a device whose per-stage descriptor limits cannot hold them, in which case
    /// every set is reflected and pipelines are not layout-compatible for the globals. See
    /// <see cref="CanonicalLayoutsFit"/>.</remarks>
    public bool UseCanonicalLayouts { get; }

    /// <summary>Creates a cache.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="debugNames">Used to name the layouts.</param>
    /// <param name="useCanonicalLayouts">Whether sets 0, 1, 2 and 4 declare their whole reserved range.
    /// Pass the answer from <see cref="CanonicalLayoutsFit"/>.</param>
    /// <param name="onLookup">Called for every <see cref="GetOrCreate"/>, with <see langword="true"/>
    /// when a layout was created and <see langword="false"/> on a cache hit, or <see langword="null"/>
    /// for no instrumentation. Kept as a delegate so this layer does not have to know about the pipeline
    /// layer's counters.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public VulkanDescriptorLayoutCache(
        Vk api,
        Device device,
        VulkanDebugNames debugNames,
        bool useCanonicalLayouts = true,
        Action<bool>? onLookup = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(debugNames);

        Api = api;
        Device = device;
        DebugNames = debugNames;
        UseCanonicalLayouts = useCanonicalLayouts;
        OnLookup = onLookup;
    }

    /// <summary>
    /// Reports whether a device can hold the canonical layouts.
    /// </summary>
    /// <param name="limits">The physical device limits.</param>
    /// <returns><see langword="true"/> when they fit.</returns>
    /// <remarks>
    /// <para>
    /// A declared binding costs against <c>maxPerStageDescriptor*</c> whether the shader reads it or not,
    /// and the canonical layouts declare whole reserved ranges, so this asks rather than assumes. The
    /// specification's floors are far below what the renderer numbers by &#8212; four storage buffers per
    /// stage against sixteen <see cref="Buffers.ReservedBufferSlots"/> values &#8212; while every desktop
    /// driver reports orders of magnitude more.
    /// </para>
    /// <para>
    /// <b><c>maxBoundDescriptorSets</c> is the one that is genuinely tight.</b> Vulkan guarantees only
    /// four, and the contract now needs <see cref="DescriptorSets.Count"/> of them since storage images
    /// took set 4. A device at the floor cannot run this renderer at all, canonical layouts or not, so
    /// that check belongs at adapter selection rather than here &#8212; this only stops such a device
    /// silently building layouts it could never bind.
    /// </para>
    /// </remarks>
    public static bool CanonicalLayoutsFit(in PhysicalDeviceLimits limits)
        => limits.MaxBoundDescriptorSets >= DescriptorSets.Count
        && limits.MaxPerStageDescriptorUniformBuffers >= (uint)VulkanDescriptorTypes.UniformBufferSlotCount
        && limits.MaxPerStageDescriptorStorageBuffers >= (uint)VulkanDescriptorTypes.StorageBufferSlotCount
        && limits.MaxPerStageDescriptorSampledImages >= (uint)VulkanDescriptorTypes.ReservedTextureSlotCount
        && limits.MaxPerStageDescriptorStorageImages >= (uint)VulkanDescriptorTypes.StorageImageSlotCount;

    /// <summary>
    /// Gets the shared layout for <see cref="DescriptorSets.UniformBuffers"/>: every
    /// <see cref="Buffers.ReservedBufferSlots"/> UBO slot, visible to every stage.
    /// </summary>
    public VulkanDescriptorSetLayout UniformBuffers => UniformBuffersLayout ??= GetOrCreate(
        DescriptorSets.UniformBuffers,
        Uniform(DescriptorType.UniformBuffer, VulkanDescriptorTypes.UniformBufferSlotCount),
        "Set 0 uniform buffers");

    /// <summary>
    /// Gets the shared layout for <see cref="DescriptorSets.StorageBuffers"/>: every
    /// <see cref="Buffers.ReservedBufferSlots"/> SSBO slot, visible to every stage.
    /// </summary>
    /// <remarks>This set exists solely because the SSBO range restarts at zero on top of the UBO range.
    /// Set 0 binding 0 is <c>View</c> and set 1 binding 0 is <c>Objects</c>; on OpenGL the binding target
    /// keeps them apart, and here the set does.</remarks>
    public VulkanDescriptorSetLayout StorageBuffers => StorageBuffersLayout ??= GetOrCreate(
        DescriptorSets.StorageBuffers,
        Uniform(DescriptorType.StorageBuffer, VulkanDescriptorTypes.StorageBufferSlotCount),
        "Set 1 storage buffers");

    /// <summary>
    /// Gets the shared layout for <see cref="DescriptorSets.ReservedTextures"/>: every
    /// <see cref="Materials.ReservedTextureSlots"/> value as a combined image sampler.
    /// </summary>
    /// <remarks>
    /// Combined rather than separate image and sampler, because that is what a GLSL <c>sampler2D</c>
    /// compiles to and what <see cref="ICommandList.BindTexture"/> supplies: a texture and a sampler in
    /// one call.
    /// </remarks>
    public VulkanDescriptorSetLayout ReservedTextures => ReservedTexturesLayout ??= GetOrCreate(
        DescriptorSets.ReservedTextures,
        Uniform(DescriptorType.CombinedImageSampler, VulkanDescriptorTypes.ReservedTextureSlotCount),
        "Set 2 reserved textures");

    /// <summary>
    /// Gets the shared layout for <see cref="DescriptorSets.StorageImages"/>: eight storage images, the
    /// count OpenGL guarantees for image units.
    /// </summary>
    public VulkanDescriptorSetLayout StorageImages => StorageImagesLayout ??= GetOrCreate(
        DescriptorSets.StorageImages,
        Uniform(DescriptorType.StorageImage, VulkanDescriptorTypes.StorageImageSlotCount),
        "Set 4 storage images");

    /// <summary>Gets the canonical layout for a set, or <see langword="null"/> when the set has none.</summary>
    /// <param name="set">The set index.</param>
    /// <returns>The shared layout for sets 0, 1, 2 and 4, or <see langword="null"/> for set 3, whose
    /// contents are assigned per shader and therefore cannot be declared up front. Also
    /// <see langword="null"/> for every set when <see cref="UseCanonicalLayouts"/> is off.</returns>
    public VulkanDescriptorSetLayout? Canonical(int set) => !UseCanonicalLayouts ? null : set switch
    {
        DescriptorSets.UniformBuffers => UniformBuffers,
        DescriptorSets.StorageBuffers => StorageBuffers,
        DescriptorSets.ReservedTextures => ReservedTextures,
        DescriptorSets.StorageImages => StorageImages,
        _ => null,
    };

    /// <summary>Gets a layout that declares nothing, for a set a pipeline does not use.</summary>
    /// <param name="set">The set index.</param>
    /// <returns>The shared empty layout for that set.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="set"/> is outside the contract's sets.</exception>
    /// <remarks>A pipeline layout declares its sets positionally, so a shader that uses only set 3 still
    /// needs something in positions 0 to 2.</remarks>
    public VulkanDescriptorSetLayout Empty(int set)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(set);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(set, DescriptorSets.Count);

        lock (Gate)
        {
            return EmptyLayouts[set] ??= GetOrCreateCore(set, [], string.Create(CultureInfo.InvariantCulture, $"Set {set} empty"));
        }
    }

    /// <summary>
    /// Gets the layout for a binding table, creating it the first time the table is seen.
    /// </summary>
    /// <param name="set">Which of the contract's sets this describes.</param>
    /// <param name="bindings">The binding table. Order does not matter; the layout sorts it.</param>
    /// <param name="name">Debug name, used only when the layout is actually created.</param>
    /// <returns>The layout, owned by this cache.</returns>
    /// <exception cref="ObjectDisposedException">The cache has been disposed.</exception>
    public VulkanDescriptorSetLayout GetOrCreate(int set, ImmutableArray<VulkanDescriptorBinding> bindings, string name)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        lock (Gate)
        {
            return GetOrCreateCore(set, bindings, name);
        }
    }

    /// <summary>
    /// Finds the layout object behind a raw <c>VkDescriptorSetLayout</c> handle.
    /// </summary>
    /// <param name="handle">The handle, as held by a pipeline layout.</param>
    /// <param name="layout">Receives the layout.</param>
    /// <returns><see langword="true"/> when this cache created it.</returns>
    /// <remarks>
    /// A <c>VkDescriptorSetLayout</c> is opaque and cannot be queried for its bindings, and a pipeline
    /// layout keeps only handles. <see cref="VulkanDescriptorBinder"/> needs the binding table behind one
    /// to allocate against it and to check what it is being asked to write, so the cache that created
    /// every layout is where that lookup belongs.
    /// </remarks>
    public bool TryResolve(DescriptorSetLayout handle, out VulkanDescriptorSetLayout layout)
    {
        lock (Gate)
        {
            return ByHandle.TryGetValue(handle.Handle, out layout!);
        }
    }

    private VulkanDescriptorSetLayout GetOrCreateCore(int set, ImmutableArray<VulkanDescriptorBinding> bindings, string name)
    {
        var sorted = bindings.IsDefault
            ? []
            : bindings.Sort(static (a, b) => a.Binding.CompareTo(b.Binding));

        var key = new LayoutKey(set, sorted);

        if (Layouts.TryGetValue(key, out var existing))
        {
            OnLookup?.Invoke(false);
            return existing;
        }

        var layout = new VulkanDescriptorSetLayout(Api, Device, DebugNames, set, sorted, name);
        Layouts[key] = layout;
        ByHandle[layout.Handle.Handle] = layout;
        OnLookup?.Invoke(true);
        return layout;
    }

    private static ImmutableArray<VulkanDescriptorBinding> Uniform(DescriptorType type, int count)
    {
        var builder = ImmutableArray.CreateBuilder<VulkanDescriptorBinding>(count);

        for (var binding = 0; binding < count; binding++)
        {
            builder.Add(new VulkanDescriptorBinding(binding, type, 1, VulkanDescriptorTypes.AllStages));
        }

        return builder.MoveToImmutable();
    }

    /// <summary>Destroys every layout this cache created.</summary>
    /// <remarks>Only legal once nothing referencing them is executing. Pipeline layouts built from these
    /// must be destroyed first.</remarks>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        lock (Gate)
        {
            foreach (var layout in Layouts.Values)
            {
                layout.Dispose();
            }

            Layouts.Clear();
            ByHandle.Clear();
            Array.Clear(EmptyLayouts);
            UniformBuffersLayout = null;
            StorageBuffersLayout = null;
            ReservedTexturesLayout = null;
            StorageImagesLayout = null;
        }
    }

    /// <summary>
    /// The cache key: a set index and its binding table compared element by element.
    /// </summary>
    private readonly struct LayoutKey : IEquatable<LayoutKey>
    {
        private readonly int Set;
        private readonly ImmutableArray<VulkanDescriptorBinding> Bindings;
        private readonly int Hash;

        internal LayoutKey(int set, ImmutableArray<VulkanDescriptorBinding> bindings)
        {
            Set = set;
            Bindings = bindings;

            var hash = new HashCode();
            hash.Add(set);

            foreach (var binding in bindings)
            {
                hash.Add(binding);
            }

            Hash = hash.ToHashCode();
        }

        public bool Equals(LayoutKey other)
        {
            if (Set != other.Set || Bindings.Length != other.Bindings.Length || Hash != other.Hash)
            {
                return false;
            }

            for (var i = 0; i < Bindings.Length; i++)
            {
                if (!Bindings[i].Equals(other.Bindings[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is LayoutKey other && Equals(other);

        public override int GetHashCode() => Hash;
    }
}
