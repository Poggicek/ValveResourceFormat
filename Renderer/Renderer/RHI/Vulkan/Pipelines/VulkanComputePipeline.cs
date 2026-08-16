using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;
using ValveResourceFormat.Renderer.RHI.Vulkan.Descriptors;
using ValveResourceFormat.Renderer.Shaders.Spirv;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// <see cref="IComputePipeline"/> on Vulkan: one <c>VkPipeline</c> over a compute module, and the local
/// workgroup size its SPIR-V declares.
/// </summary>
/// <remarks>
/// <para>
/// The workgroup size is read out of the module's <c>OpExecutionMode LocalSize</c> rather than restated
/// at the call site, matching what <c>GLComputePipeline</c> reads back from the linked program. That is
/// what lets <see cref="DispatchGroupsFor"/> size a dispatch from a thread count instead of every
/// dispatch site dividing by a group size written out by hand in two places.
/// </para>
/// <para>
/// A module whose workgroup size comes from specialisation constants reports none, and the size is taken
/// as one on each axis. None of the renderer's compute passes do that today; if one ever does, its
/// dispatch sites have to supply group counts directly.
/// </para>
/// </remarks>
public sealed unsafe class VulkanComputePipeline : IComputePipeline, IVulkanPipeline
{
    private readonly Vk Api;
    private readonly Device Device;

    private Pipeline PipelineHandle;
    private bool Disposed;

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public (int X, int Y, int Z) WorkgroupSize { get; }

    /// <summary>Gets the pipeline handle, or a null handle once disposed.</summary>
    public Pipeline Handle => PipelineHandle;

    /// <summary>Gets the layout descriptor sets and push constants are bound through. Owned by the
    /// layout cache, not by this pipeline.</summary>
    public VulkanPipelineLayout Layout { get; }

    /// <inheritdoc/>
    /// <remarks>Explicit, for the reason given on
    /// <see cref="VulkanGraphicsPipeline"/>: the interface wants the bare <c>VkPipelineLayout</c> under
    /// a name this type already uses for the layout object, and narrowing to
    /// <see cref="VulkanPipelineLayout.Handle"/> is all it is doing.</remarks>
    PipelineLayout IVulkanPipeline.Layout => Layout.Handle;

    /// <inheritdoc/>
    public PipelineBindPoint BindPoint => PipelineBindPoint.Compute;

    /// <inheritdoc/>
    /// <remarks>Derived from SPIR-V reflection when the layout was built. A compute shader's block is
    /// usually its own rather than the graphics per-draw block, which is exactly why the range is read
    /// from the module instead of assumed.</remarks>
    public PushConstantRange? PushConstants => Layout.PushConstants;

    /// <inheritdoc/>
    /// <remarks>Computed once here from the same reflection the layout was built from. Compute is where
    /// the hazard bites hardest: <c>depth_pyramid.comp</c> and <c>histogram.comp</c> reach three index
    /// spaces at once, and a dispatch that missed one has no rasterizer between it and the device.</remarks>
    public int UsedDescriptorSets { get; }

    /// <inheritdoc/>
    /// <remarks>From the same reflection and at the same moment as <see cref="UsedDescriptorSets"/>, and
    /// for the same reason: the sets a dispatch reaches are canonical ones declaring their whole reserved
    /// range, so only the module's own declaration says which slots inside them it reads.</remarks>
    public VulkanDescriptorBindingUsage DeclaredDescriptorBindings { get; }

    /// <inheritdoc/>
    /// <remarks>Always zero. A compute pipeline fetches no vertices.</remarks>
    public uint UsedVertexBindings => 0;

    /// <summary>Gets one message per interface problem found while building this pipeline. Empty when
    /// the shader conforms to the contract's descriptor set scheme.</summary>
    public IReadOnlyList<string> Problems => Layout.Problems;

    /// <summary>Creates a compute pipeline.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="debugNames">Used to name the pipeline.</param>
    /// <param name="cache">The pipeline cache to compile against.</param>
    /// <param name="layout">The pipeline layout, from the layout cache.</param>
    /// <param name="description">What to build.</param>
    /// <param name="reflection">The reflected module, supplying the entry point and workgroup size.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The description names a module this backend did not create,
    /// or one that is not a compute module.</exception>
    /// <exception cref="VulkanException">The driver rejected the pipeline.</exception>
    public VulkanComputePipeline(
        Vk api,
        Device device,
        VulkanDebugNames debugNames,
        PipelineCache cache,
        VulkanPipelineLayout layout,
        in ComputePipelineDesc description,
        SpirvReflectionResult reflection)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(debugNames);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(reflection);

        if (description.ComputeShader is not VulkanShaderModule module)
        {
            throw new ArgumentException(
                $"Compute pipeline '{description.Name}' names a {description.ComputeShader?.GetType().Name ?? "null"} module. The Vulkan backend needs a {nameof(VulkanShaderModule)}.",
                nameof(description));
        }

        if (module.Stage != ShaderStage.Compute)
        {
            throw new ArgumentException(
                $"Compute pipeline '{description.Name}' names a {module.Stage} module.",
                nameof(description));
        }

        Api = api;
        Device = device;
        Name = description.Name ?? string.Empty;
        Layout = layout;
        WorkgroupSize = reflection.WorkgroupSize ?? (1, 1, 1);
        UsedDescriptorSets = VulkanDescriptorSetUsage.MaskFor(reflection);
        DeclaredDescriptorBindings = VulkanDescriptorBindingUsage.For(reflection);

        var entryPoint = SilkMarshal.StringToPtr(reflection.EntryPoint);

        try
        {
            var info = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module.Handle,
                    PName = (byte*)entryPoint,
                },
                Layout = layout.Handle,
            };

            Api.CreateComputePipelines(Device, cache, 1, &info, null, out PipelineHandle)
                .Check($"vkCreateComputePipelines for '{Name}'");
        }
        finally
        {
            SilkMarshal.Free(entryPoint);
        }

        debugNames.SetName(ObjectType.Pipeline, PipelineHandle.Handle, Name);
    }

    /// <summary>Gets how many workgroups cover a number of threads, rounding up.</summary>
    /// <param name="threadsX">Threads needed on X.</param>
    /// <param name="threadsY">Threads needed on Y.</param>
    /// <param name="threadsZ">Threads needed on Z.</param>
    /// <returns>The group counts to dispatch.</returns>
    /// <remarks>The shader still has to bounds check: rounding up dispatches threads past the end.</remarks>
    public (int X, int Y, int Z) DispatchGroupsFor(int threadsX, int threadsY = 1, int threadsZ = 1)
        => (GroupsFor(threadsX, WorkgroupSize.X), GroupsFor(threadsY, WorkgroupSize.Y), GroupsFor(threadsZ, WorkgroupSize.Z));

    private static int GroupsFor(int threads, int groupSize)
        => groupSize <= 0 ? 0 : (threads + groupSize - 1) / groupSize;

    /// <summary>Queues this pipeline on a deletion queue.</summary>
    /// <param name="deletionQueue">The queue to enqueue on.</param>
    /// <param name="frameSerial">The serial of the frame during which destruction was requested.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deletionQueue"/> is <see langword="null"/>.</exception>
    public void EnqueueDestroy(VulkanDeletionQueue deletionQueue, ulong frameSerial)
    {
        ArgumentNullException.ThrowIfNull(deletionQueue);

        if (Disposed || PipelineHandle.Handle == 0)
        {
            return;
        }

        Disposed = true;

        var api = Api;
        var device = Device;
        var handle = PipelineHandle;

        deletionQueue.Enqueue(frameSerial, () => api.DestroyPipeline(device, handle, null));
        PipelineHandle = default;
    }

    /// <summary>Destroys the pipeline. Its layout and module outlive it.</summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        if (PipelineHandle.Handle != 0)
        {
            Api.DestroyPipeline(Device, PipelineHandle, null);
            PipelineHandle = default;
        }
    }
}
