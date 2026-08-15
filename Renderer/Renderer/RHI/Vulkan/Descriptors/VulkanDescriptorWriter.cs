using System.Globalization;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Descriptors;

/// <summary>
/// Accumulates descriptor writes and applies them in one <c>vkUpdateDescriptorSets</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Batching is the point.</b> <c>vkUpdateDescriptorSets</c> takes an array, and calling it once per
/// binding makes the driver re-enter, re-validate and re-flush its descriptor writing path for every
/// texture in a material. The renderer binds up to eighteen globals and a dozen material textures per
/// draw; issued one at a time that is thirty calls where one will do.
/// </para>
/// <para>
/// <b>The descriptor type comes from the layout, not from the caller.</b> Every write is checked against
/// the <see cref="VulkanDescriptorSetLayout"/> it targets, so a buffer written into a slot the shader
/// declared as an image throws here naming both types. Vulkan calls that mismatch undefined behaviour
/// rather than an error: with the validation layer loaded it is a red message, and without it the
/// descriptor reads whatever memory it aliases and the frame is subtly wrong on one vendor's driver.
/// </para>
/// <para>
/// The write structures hold raw pointers into the info lists, so the pointers are only filled in at
/// <see cref="Flush"/>, under a <c>fixed</c> that spans the actual call. Nothing between
/// <see cref="WriteBuffer"/> and <see cref="Flush"/> may hold a pointer into this writer.
/// </para>
/// <para>
/// Not thread safe. One writer per recording thread.
/// </para>
/// </remarks>
public sealed unsafe class VulkanDescriptorWriter
{
    private readonly Vk Api;
    private readonly Device Device;
    private readonly List<WriteDescriptorSet> Writes = [];
    private readonly List<DescriptorBufferInfo> BufferInfos = [];
    private readonly List<DescriptorImageInfo> ImageInfos = [];
    private readonly List<int> InfoIndices = [];

    /// <summary>Gets how many writes are waiting for a flush.</summary>
    public int PendingCount => Writes.Count;

    /// <summary>Gets how many writes have been applied over this writer's lifetime.</summary>
    public int AppliedCount { get; private set; }

    /// <summary>Gets how many times <see cref="Flush"/> has actually called into Vulkan.</summary>
    /// <remarks>The ratio of <see cref="AppliedCount"/> to this is the batch factor the writer exists
    /// for. A ratio near one means something is flushing per write.</remarks>
    public int FlushCount { get; private set; }

    /// <summary>Creates a writer.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <exception cref="ArgumentNullException"><paramref name="api"/> is <see langword="null"/>.</exception>
    public VulkanDescriptorWriter(Vk api, Device device)
    {
        ArgumentNullException.ThrowIfNull(api);

        Api = api;
        Device = device;
    }

    /// <summary>
    /// Queues a buffer write. The layout decides whether it lands as a uniform or a storage buffer.
    /// </summary>
    /// <param name="set">The set to write into.</param>
    /// <param name="layout">The layout that set was allocated against.</param>
    /// <param name="binding">The binding within the set: a <see cref="Buffers.ReservedBufferSlots"/> value.</param>
    /// <param name="buffer">The buffer to bind.</param>
    /// <param name="offsetInBytes">Byte offset of the bound range.</param>
    /// <param name="sizeInBytes">Length of the bound range, or -1 for the rest of the buffer.</param>
    /// <returns>This writer, so writes can be chained.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range falls outside the buffer.</exception>
    /// <exception cref="InvalidOperationException">The layout does not declare that binding, or declares
    /// it as something other than a buffer.</exception>
    public VulkanDescriptorWriter WriteBuffer(
        DescriptorSet set,
        VulkanDescriptorSetLayout layout,
        int binding,
        VulkanBuffer buffer,
        int offsetInBytes = 0,
        int sizeInBytes = -1)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offsetInBytes);

        var type = DeclaredType(layout, binding, "Writing a buffer");

        if (!VulkanDescriptorTypes.IsBuffer(type))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Writing a buffer: set {layout.SetIndex} binding {binding} of '{layout.Name}' is a {type}, which is not written with a buffer."));
        }

        var size = sizeInBytes < 0 ? buffer.SizeInBytes - offsetInBytes : sizeInBytes;

        if (size <= 0 || offsetInBytes + size > buffer.SizeInBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeInBytes), sizeInBytes, string.Create(CultureInfo.InvariantCulture,
                $"A range of {size} bytes at offset {offsetInBytes} does not fit in '{buffer.Name}', which is {buffer.SizeInBytes} bytes."));
        }

        buffer.RequireUsage(
            type is DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic ? BufferUsage.Uniform : BufferUsage.Storage,
            string.Create(CultureInfo.InvariantCulture, $"Binding to set {layout.SetIndex} binding {binding} as a {type}"));

        return WriteBufferHandle(set, layout, binding, buffer.Handle, (ulong)offsetInBytes, (ulong)size);
    }

    /// <summary>
    /// Queues a buffer write from raw handles, for a caller that has already resolved the range.
    /// </summary>
    /// <param name="set">The set to write into.</param>
    /// <param name="layout">The layout that set was allocated against.</param>
    /// <param name="binding">The binding within the set.</param>
    /// <param name="buffer">The buffer handle.</param>
    /// <param name="offsetInBytes">Byte offset of the bound range.</param>
    /// <param name="sizeInBytes">Length of the bound range. Must be greater than zero.</param>
    /// <returns>This writer, so writes can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sizeInBytes"/> is zero.</exception>
    /// <exception cref="InvalidOperationException">The layout does not declare that binding, or declares
    /// it as something other than a buffer.</exception>
    /// <remarks>
    /// The overload <see cref="VulkanDescriptorBinder"/> uses. It cannot reach a <see cref="VulkanBuffer"/>
    /// because <see cref="IVulkanDescriptorBinder"/> is handed handles on purpose: the command list has
    /// already resolved the range and checked the usage, and doing either again on this side would be a
    /// second opinion about it. The layout type check is not skipped, because that one is about the
    /// shader rather than about the resource.
    /// </remarks>
    public VulkanDescriptorWriter WriteBufferHandle(
        DescriptorSet set,
        VulkanDescriptorSetLayout layout,
        int binding,
        Silk.NET.Vulkan.Buffer buffer,
        ulong offsetInBytes,
        ulong sizeInBytes)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var type = DeclaredType(layout, binding, "Writing a buffer");

        if (!VulkanDescriptorTypes.IsBuffer(type))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Writing a buffer: set {layout.SetIndex} binding {binding} of '{layout.Name}' is a {type}, which is not written with a buffer."));
        }

        if (sizeInBytes == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeInBytes), sizeInBytes,
                "A descriptor cannot name an empty range; VK_WHOLE_SIZE is what an open ended binding uses.");
        }

        InfoIndices.Add(BufferInfos.Count);

        BufferInfos.Add(new DescriptorBufferInfo
        {
            Buffer = buffer,
            Offset = offsetInBytes,
            Range = sizeInBytes,
        });

        Writes.Add(new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = (uint)binding,
            DstArrayElement = 0,
            DescriptorCount = 1,
            DescriptorType = type,
        });

        return this;
    }

    /// <summary>
    /// Queues an image write from raw handles. The layout decides whether it lands as a combined image
    /// sampler, a sampled image or a storage image.
    /// </summary>
    /// <param name="set">The set to write into.</param>
    /// <param name="layout">The layout that set was allocated against.</param>
    /// <param name="binding">The binding within the set.</param>
    /// <param name="view">The image view.</param>
    /// <param name="sampler">The sampler, or a null handle for a binding that takes none.</param>
    /// <param name="imageLayout">The layout the image is in, already resolved from its tracked state.</param>
    /// <returns>This writer, so writes can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The layout does not declare that binding, declares it
    /// as something other than an image, or a combined image sampler was written without a sampler.</exception>
    /// <remarks>The image layout is passed in rather than derived, for the reason on
    /// <see cref="WriteBufferHandle"/>: the command list resolved it from the texture's tracked state and
    /// refused the bind outright if the texture was somewhere a descriptor may not name.</remarks>
    public VulkanDescriptorWriter WriteImageHandle(
        DescriptorSet set,
        VulkanDescriptorSetLayout layout,
        int binding,
        ImageView view,
        Sampler sampler,
        ImageLayout imageLayout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var type = DeclaredType(layout, binding, "Writing an image");

        if (!VulkanDescriptorTypes.IsImage(type))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Writing an image: set {layout.SetIndex} binding {binding} of '{layout.Name}' is a {type}, which is not written with an image."));
        }

        if (type == DescriptorType.CombinedImageSampler && sampler.Handle == 0)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Writing an image: set {layout.SetIndex} binding {binding} of '{layout.Name}' is a combined image sampler and needs a sampler. Vulkan has no equivalent of OpenGL's sampler object zero, which defers to the texture's own parameters."));
        }

        AddImage(new DescriptorImageInfo
        {
            // A sampler on a binding that does not take one is ignored by Vulkan, but clearing it keeps
            // two writes that mean the same thing from differing.
            Sampler = type is DescriptorType.CombinedImageSampler or DescriptorType.Sampler ? sampler : default,
            ImageView = view,
            ImageLayout = imageLayout,
        });

        Writes.Add(new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = (uint)binding,
            DstArrayElement = 0,
            DescriptorCount = 1,
            DescriptorType = type,
        });

        return this;
    }

    /// <summary>
    /// Queues a texture write for sampling.
    /// </summary>
    /// <param name="set">The set to write into.</param>
    /// <param name="layout">The layout that set was allocated against.</param>
    /// <param name="binding">The binding within the set.</param>
    /// <param name="texture">The texture, or a view of it.</param>
    /// <param name="sampler">The sampler. Required for a combined image sampler.</param>
    /// <returns>This writer, so writes can be chained.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The layout does not declare that binding, declares it
    /// as something other than a sampled texture, or a combined image sampler was written without a
    /// sampler.</exception>
    /// <remarks>
    /// The image layout is derived from the texture's aspect, not assumed: a depth texture bound for
    /// sampling belongs in <c>DEPTH_STENCIL_READ_ONLY_OPTIMAL</c>, and naming the colour layout for it is
    /// the mistake that looks legal and fails at the bind. This does <i>not</i> transition the texture;
    /// the caller must already have put it in <see cref="ResourceState.ShaderRead"/>.
    /// </remarks>
    public VulkanDescriptorWriter WriteTexture(
        DescriptorSet set,
        VulkanDescriptorSetLayout layout,
        int binding,
        VulkanTexture texture,
        VulkanSampler? sampler)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(texture);

        var type = DeclaredType(layout, binding, "Writing a texture");

        if (type is not (DescriptorType.CombinedImageSampler or DescriptorType.SampledImage))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Writing a texture: set {layout.SetIndex} binding {binding} of '{layout.Name}' is a {type}, which is not written with a sampled texture."));
        }

        if (type == DescriptorType.CombinedImageSampler && sampler is null)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Writing a texture: set {layout.SetIndex} binding {binding} of '{layout.Name}' is a combined image sampler and needs a sampler. Vulkan has no equivalent of OpenGL's sampler object zero, which defers to the texture's own parameters."));
        }

        var isDepth = (texture.Aspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0;

        AddImage(new DescriptorImageInfo
        {
            Sampler = sampler?.Handle ?? default,
            ImageView = texture.View,
            ImageLayout = VulkanResourceStates.ForImage(ResourceState.ShaderRead, isDepth).Layout,
        });

        Writes.Add(new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = (uint)binding,
            DstArrayElement = 0,
            DescriptorCount = 1,
            DescriptorType = type,
        });

        return this;
    }

    /// <summary>
    /// Queues a read/write storage image write.
    /// </summary>
    /// <param name="set">The set to write into.</param>
    /// <param name="layout">The layout that set was allocated against.</param>
    /// <param name="binding">The binding within the set.</param>
    /// <param name="texture">The texture, or a view naming the mip level to write through.</param>
    /// <returns>This writer, so writes can be chained.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The layout does not declare that binding, or declares
    /// it as something other than a storage image.</exception>
    /// <remarks>
    /// A storage image is always bound in <c>GENERAL</c>, the only layout that permits both reads and
    /// writes. The caller must have transitioned the texture to
    /// <see cref="ResourceState.ShaderReadWrite"/> (or <see cref="ResourceState.ShaderWrite"/>) first.
    /// </remarks>
    public VulkanDescriptorWriter WriteStorageTexture(
        DescriptorSet set,
        VulkanDescriptorSetLayout layout,
        int binding,
        VulkanTexture texture)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(texture);

        var type = DeclaredType(layout, binding, "Writing a storage image");

        if (type != DescriptorType.StorageImage)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Writing a storage image: set {layout.SetIndex} binding {binding} of '{layout.Name}' is a {type}, which is not written with a storage image."));
        }

        texture.RequireUsage(TextureUsage.Storage, string.Create(CultureInfo.InvariantCulture,
            $"Binding to set {layout.SetIndex} binding {binding} as a storage image"));

        AddImage(new DescriptorImageInfo
        {
            Sampler = default,
            ImageView = texture.View,
            ImageLayout = ImageLayout.General,
        });

        Writes.Add(new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = (uint)binding,
            DstArrayElement = 0,
            DescriptorCount = 1,
            DescriptorType = type,
        });

        return this;
    }

    /// <summary>Applies every queued write and empties the queue.</summary>
    /// <returns>How many writes were applied.</returns>
    /// <remarks>Cheap and safe to call with nothing queued. Must not be called while the sets being
    /// written are referenced by a command buffer the GPU is still executing.</remarks>
    public int Flush()
    {
        var count = Writes.Count;

        if (count == 0)
        {
            return 0;
        }

        var writes = CollectionsMarshal.AsSpan(Writes);
        var buffers = CollectionsMarshal.AsSpan(BufferInfos);
        var images = CollectionsMarshal.AsSpan(ImageInfos);

        // The pointers are filled in here rather than at queue time because a List reallocates as it
        // grows, which would leave every earlier write pointing into a freed array.
        fixed (WriteDescriptorSet* writePointer = writes)
        fixed (DescriptorBufferInfo* bufferPointer = buffers)
        fixed (DescriptorImageInfo* imagePointer = images)
        {
            for (var i = 0; i < count; i++)
            {
                var index = InfoIndices[i];

                if (VulkanDescriptorTypes.IsImage(writePointer[i].DescriptorType))
                {
                    writePointer[i].PImageInfo = imagePointer + index;
                }
                else
                {
                    writePointer[i].PBufferInfo = bufferPointer + index;
                }
            }

            Api.UpdateDescriptorSets(Device, (uint)count, writePointer, 0, null);
        }

        Discard();

        AppliedCount += count;
        FlushCount++;

        return count;
    }

    /// <summary>Throws away every queued write without applying it.</summary>
    public void Discard()
    {
        Writes.Clear();
        BufferInfos.Clear();
        ImageInfos.Clear();
        InfoIndices.Clear();
    }

    private void AddImage(DescriptorImageInfo info)
    {
        InfoIndices.Add(ImageInfos.Count);
        ImageInfos.Add(info);
    }

    private static DescriptorType DeclaredType(VulkanDescriptorSetLayout layout, int binding, string operation)
    {
        if (!layout.TryGetBinding(binding, out var declared))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"{operation}: set {layout.SetIndex} of '{layout.Name}' declares no binding {binding}."));
        }

        if (VulkanDescriptorTypes.IsTexelBuffer(declared.Type))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"{operation}: set {layout.SetIndex} binding {binding} of '{layout.Name}' is a {declared.Type}, which needs a buffer view. No renderer shader declares one, so this writer does not build them."));
        }

        return declared.Type;
    }
}
