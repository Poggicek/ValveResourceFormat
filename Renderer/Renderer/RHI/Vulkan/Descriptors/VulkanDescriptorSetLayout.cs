using System.Collections.Immutable;
using System.Globalization;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Descriptors;

/// <summary>
/// A <c>VkDescriptorSetLayout</c> and the binding table it was built from.
/// </summary>
/// <remarks>
/// <para>
/// Owned by <see cref="VulkanDescriptorLayoutCache"/> and never destroyed individually: two pipeline
/// layouts that declare the same table share this object, which is what makes their descriptor sets
/// interchangeable. Disposing it out from under one of them would take the other with it.
/// </para>
/// <para>
/// The binding table is kept because a <c>VkDescriptorSetLayout</c> is opaque once created and cannot
/// be queried back. <see cref="RequireType"/> uses it to reject a write of the wrong descriptor type
/// before Vulkan sees it, which turns an undefined-behaviour class of bug into an exception naming the
/// set, the binding and both types.
/// </para>
/// </remarks>
public sealed unsafe class VulkanDescriptorSetLayout : IDisposable
{
    private readonly Vk Api;
    private readonly Device Device;
    private readonly Dictionary<int, VulkanDescriptorBinding> ByBinding;

    private DescriptorSetLayout LayoutHandle;
    private bool Disposed;

    /// <summary>Gets the layout handle.</summary>
    public DescriptorSetLayout Handle => LayoutHandle;

    /// <summary>Gets which of the contract's four sets this layout describes.</summary>
    public int SetIndex { get; }

    /// <summary>Gets the bindings, ordered by binding number.</summary>
    public ImmutableArray<VulkanDescriptorBinding> Bindings { get; }

    /// <summary>Gets the debug name.</summary>
    public string Name { get; }

    /// <summary>Gets a value indicating whether the layout declares no bindings.</summary>
    /// <remarks>A pipeline layout must still declare one for every set below the highest it uses, so an
    /// empty layout is the placeholder that lets a shader use set 3 without using set 2.</remarks>
    public bool IsEmpty => Bindings.Length == 0;

    /// <summary>Creates a descriptor set layout.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="debugNames">Used to name the layout.</param>
    /// <param name="setIndex">Which of the contract's four sets this describes.</param>
    /// <param name="bindings">The binding table. Need not be sorted; duplicates are rejected.</param>
    /// <param name="name">Debug name.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="setIndex"/> is outside the
    /// contract's four sets, or a binding has a non-positive count.</exception>
    /// <exception cref="ArgumentException">Two bindings share a binding number.</exception>
    public VulkanDescriptorSetLayout(
        Vk api,
        Device device,
        VulkanDebugNames debugNames,
        int setIndex,
        ImmutableArray<VulkanDescriptorBinding> bindings,
        string name)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(debugNames);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfNegative(setIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(setIndex, DescriptorSets.Count);

        Api = api;
        Device = device;
        SetIndex = setIndex;
        Name = name;
        Bindings = [.. bindings.Sort(static (a, b) => a.Binding.CompareTo(b.Binding))];

        ByBinding = new Dictionary<int, VulkanDescriptorBinding>(Bindings.Length);

        foreach (var binding in Bindings)
        {
            // A runtime sized array reflects as zero and is only legal under descriptor indexing, which
            // this backend does not enable. Creating the layout anyway fails inside the driver with a
            // message that does not mention the shader.
            if (binding.Count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bindings), binding.Count, string.Create(CultureInfo.InvariantCulture,
                    $"Set {setIndex} binding {binding.Binding} of '{name}' has a descriptor count of {binding.Count}. A runtime sized array needs descriptor indexing, which this backend does not enable."));
            }

            if (!ByBinding.TryAdd(binding.Binding, binding))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                    $"Set {setIndex} of '{name}' declares binding {binding.Binding} twice, as {ByBinding[binding.Binding].Type} and as {binding.Type}."), nameof(bindings));
            }
        }

        var vulkanBindings = new DescriptorSetLayoutBinding[Bindings.Length];

        for (var i = 0; i < Bindings.Length; i++)
        {
            vulkanBindings[i] = Bindings[i].ToVulkan();
        }

        fixed (DescriptorSetLayoutBinding* p = vulkanBindings)
        {
            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)vulkanBindings.Length,
                PBindings = vulkanBindings.Length == 0 ? null : p,
            };

            Api.CreateDescriptorSetLayout(Device, &info, null, out LayoutHandle).Check("vkCreateDescriptorSetLayout");
        }

        debugNames.SetName(ObjectType.DescriptorSetLayout, LayoutHandle.Handle, name);
    }

    /// <summary>Finds what a binding holds.</summary>
    /// <param name="binding">The binding number.</param>
    /// <param name="declared">Receives the declaration.</param>
    /// <returns><see langword="true"/> when the layout declares it.</returns>
    public bool TryGetBinding(int binding, out VulkanDescriptorBinding declared) => ByBinding.TryGetValue(binding, out declared);

    /// <summary>
    /// Checks that a binding exists and holds the expected descriptor type.
    /// </summary>
    /// <param name="binding">The binding number.</param>
    /// <param name="expected">The type the caller is about to write.</param>
    /// <param name="operation">What the caller was doing, used in the message.</param>
    /// <exception cref="InvalidOperationException">The binding is not declared, or holds another type.</exception>
    /// <remarks>
    /// The failure this exists for is writing a buffer into a slot the layout types as an image. Vulkan
    /// calls that undefined behaviour rather than an error, so on a driver without the validation layer
    /// it reads whatever memory the descriptor happened to alias.
    /// </remarks>
    public void RequireType(int binding, DescriptorType expected, string operation)
    {
        if (!ByBinding.TryGetValue(binding, out var declared))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"{operation}: set {SetIndex} of '{Name}' declares no binding {binding}."));
        }

        if (declared.Type != expected)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"{operation}: set {SetIndex} binding {binding} of '{Name}' is a {declared.Type}, not a {expected}."));
        }
    }

    /// <summary>Destroys the layout.</summary>
    /// <remarks>Called by <see cref="VulkanDescriptorLayoutCache"/>. A layout is shared between every
    /// pipeline layout built from the same table, so nothing else should call this.</remarks>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        if (LayoutHandle.Handle != 0)
        {
            Api.DestroyDescriptorSetLayout(Device, LayoutHandle, null);
            LayoutHandle = default;
        }
    }
}
