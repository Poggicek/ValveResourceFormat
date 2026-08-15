using System.Collections.Immutable;
using System.Globalization;
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
/// </remarks>
public sealed class VulkanDescriptorLayoutCache : IDisposable
{
    private readonly Vk Api;
    private readonly Device Device;
    private readonly VulkanDebugNames DebugNames;
    private readonly Dictionary<LayoutKey, VulkanDescriptorSetLayout> Layouts = [];
    private readonly VulkanDescriptorSetLayout?[] EmptyLayouts = new VulkanDescriptorSetLayout?[DescriptorSets.Count];

    private VulkanDescriptorSetLayout? UniformBuffersLayout;
    private VulkanDescriptorSetLayout? StorageBuffersLayout;
    private VulkanDescriptorSetLayout? ReservedTexturesLayout;
    private bool Disposed;

    /// <summary>Gets how many distinct layouts have been created.</summary>
    public int Count => Layouts.Count;

    /// <summary>Creates a cache.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="debugNames">Used to name the layouts.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public VulkanDescriptorLayoutCache(Vk api, Device device, VulkanDebugNames debugNames)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(debugNames);

        Api = api;
        Device = device;
        DebugNames = debugNames;
    }

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

    /// <summary>Gets the canonical layout for a set, or <see langword="null"/> when the set has none.</summary>
    /// <param name="set">The set index.</param>
    /// <returns>The shared layout for sets 0 to 2, or <see langword="null"/> for set 3, whose contents
    /// are assigned per shader and therefore cannot be declared up front.</returns>
    public VulkanDescriptorSetLayout? Canonical(int set) => set switch
    {
        DescriptorSets.UniformBuffers => UniformBuffers,
        DescriptorSets.StorageBuffers => StorageBuffers,
        DescriptorSets.ReservedTextures => ReservedTextures,
        _ => null,
    };

    /// <summary>Gets a layout that declares nothing, for a set a pipeline does not use.</summary>
    /// <param name="set">The set index.</param>
    /// <returns>The shared empty layout for that set.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="set"/> is outside the four sets.</exception>
    /// <remarks>A pipeline layout declares its sets positionally, so a shader that uses only set 3 still
    /// needs something in positions 0 to 2.</remarks>
    public VulkanDescriptorSetLayout Empty(int set)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(set);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(set, DescriptorSets.Count);

        return EmptyLayouts[set] ??= GetOrCreate(set, [], string.Create(CultureInfo.InvariantCulture, $"Set {set} empty"));
    }

    /// <summary>
    /// Gets the layout for a binding table, creating it the first time the table is seen.
    /// </summary>
    /// <param name="set">Which of the contract's four sets this describes.</param>
    /// <param name="bindings">The binding table. Order does not matter; the layout sorts it.</param>
    /// <param name="name">Debug name, used only when the layout is actually created.</param>
    /// <returns>The layout, owned by this cache.</returns>
    /// <exception cref="ObjectDisposedException">The cache has been disposed.</exception>
    public VulkanDescriptorSetLayout GetOrCreate(int set, ImmutableArray<VulkanDescriptorBinding> bindings, string name)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        var sorted = bindings.IsDefault
            ? []
            : bindings.Sort(static (a, b) => a.Binding.CompareTo(b.Binding));

        var key = new LayoutKey(set, sorted);

        if (Layouts.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var layout = new VulkanDescriptorSetLayout(Api, Device, DebugNames, set, sorted, name);
        Layouts[key] = layout;
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

        foreach (var layout in Layouts.Values)
        {
            layout.Dispose();
        }

        Layouts.Clear();
        Array.Clear(EmptyLayouts);
        UniformBuffersLayout = null;
        StorageBuffersLayout = null;
        ReservedTexturesLayout = null;
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
