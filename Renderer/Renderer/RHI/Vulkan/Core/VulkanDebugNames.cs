using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Core;

/// <summary>
/// Names Vulkan objects and brackets work in labelled scopes, so a capture in RenderDoc or the AMD
/// and NVIDIA tools reads the same way an OpenGL capture of this renderer already does.
/// </summary>
/// <remarks>
/// <para>
/// This is the <c>VK_EXT_debug_utils</c> counterpart of the existing <c>GL.ObjectLabel</c> and
/// <c>GLDebugGroup</c> usage, and deliberately preserves the naming those call sites established:
/// buffers are named <c>"{mesh} VB {i}"</c> and <c>"{mesh} IB {i}"</c>, single-instance objects take
/// their <c>nameof</c>, and labels are truncated rather than rejected when they run long.
/// </para>
/// <para>
/// Unlike the GL path, which only labels under <c>#if DEBUG</c> because <c>glObjectLabel</c> is a
/// round trip on some drivers, naming here is active whenever the extension is present. Assigning a
/// debug name is a client-side operation in the loader when no tool is attached, so it costs
/// essentially nothing and being able to read a customer's capture is worth more.
/// </para>
/// </remarks>
public sealed class VulkanDebugNames
{
    /// <summary>The longest label passed through, mirroring the truncation the
    /// <c>GL.ObjectLabel</c> call sites already apply via <c>GLEnvironment.MaxLabelLength</c>.</summary>
    public const int MaxLabelLength = 255;

    private readonly ExtDebugUtils? DebugUtils;
    private readonly Device Device;

    /// <summary>Gets a value indicating whether naming and labelling actually reach the driver.</summary>
    public bool IsActive => DebugUtils is not null;

    /// <summary>Initializes a new instance of the <see cref="VulkanDebugNames"/> class.</summary>
    /// <param name="debugUtils">The extension entry points, or <see langword="null"/> when
    /// <c>VK_EXT_debug_utils</c> is not available and every operation becomes a no-op.</param>
    /// <param name="device">The device owning the objects being named.</param>
    public VulkanDebugNames(ExtDebugUtils? debugUtils, Device device)
    {
        DebugUtils = debugUtils;
        Device = device;
    }

    private static string Truncate(string name)
        => name.Length <= MaxLabelLength ? name : name[..MaxLabelLength];

    /// <summary>Names an object.</summary>
    /// <param name="type">The kind of object <paramref name="handle"/> refers to.</param>
    /// <param name="handle">The raw handle value.</param>
    /// <param name="name">The debug name.</param>
    public unsafe void SetName(ObjectType type, ulong handle, string name)
    {
        if (DebugUtils is null || handle == 0)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(name);

        var utf8 = SilkMarshal.StringToPtr(Truncate(name));

        try
        {
            var info = new DebugUtilsObjectNameInfoEXT
            {
                SType = StructureType.DebugUtilsObjectNameInfoExt,
                ObjectType = type,
                ObjectHandle = handle,
                PObjectName = (byte*)utf8,
            };

            DebugUtils.SetDebugUtilsObjectName(Device, &info);
        }
        finally
        {
            SilkMarshal.Free(utf8);
        }
    }

    /// <summary>Names a buffer.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="name">The debug name.</param>
    public void SetName(Silk.NET.Vulkan.Buffer buffer, string name)
        => SetName(ObjectType.Buffer, buffer.Handle, name);

    /// <summary>Names an image.</summary>
    /// <param name="image">The image.</param>
    /// <param name="name">The debug name.</param>
    public void SetName(Image image, string name)
        => SetName(ObjectType.Image, image.Handle, name);

    /// <summary>Names an image view.</summary>
    /// <param name="view">The view.</param>
    /// <param name="name">The debug name.</param>
    public void SetName(ImageView view, string name)
        => SetName(ObjectType.ImageView, view.Handle, name);

    /// <summary>Names a block of device memory, so a leak report identifies its owner.</summary>
    /// <param name="memory">The memory.</param>
    /// <param name="name">The debug name.</param>
    public void SetName(DeviceMemory memory, string name)
        => SetName(ObjectType.DeviceMemory, memory.Handle, name);

    /// <summary>Names a command buffer.</summary>
    /// <param name="commandBuffer">The command buffer.</param>
    /// <param name="name">The debug name.</param>
    public void SetName(CommandBuffer commandBuffer, string name)
        => SetName(ObjectType.CommandBuffer, (ulong)commandBuffer.Handle, name);

    /// <summary>Names a semaphore.</summary>
    /// <param name="semaphore">The semaphore.</param>
    /// <param name="name">The debug name.</param>
    public void SetName(Semaphore semaphore, string name)
        => SetName(ObjectType.Semaphore, semaphore.Handle, name);

    /// <summary>Opens a labelled scope on a command buffer. Dispose to close it.</summary>
    /// <param name="commandBuffer">The command buffer being recorded.</param>
    /// <param name="name">The label.</param>
    /// <returns>A guard that closes the scope.</returns>
    public unsafe CommandScope Scope(CommandBuffer commandBuffer, string name)
    {
        if (DebugUtils is null)
        {
            return default;
        }

        ArgumentNullException.ThrowIfNull(name);

        var utf8 = SilkMarshal.StringToPtr(Truncate(name));

        try
        {
            var label = new DebugUtilsLabelEXT
            {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = (byte*)utf8,
            };

            DebugUtils.CmdBeginDebugUtilsLabel(commandBuffer, &label);
        }
        finally
        {
            SilkMarshal.Free(utf8);
        }

        return new CommandScope(DebugUtils, commandBuffer);
    }

    /// <summary>Marks a single point on a command buffer.</summary>
    /// <param name="commandBuffer">The command buffer being recorded.</param>
    /// <param name="name">The label.</param>
    public unsafe void Marker(CommandBuffer commandBuffer, string name)
    {
        if (DebugUtils is null)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(name);

        var utf8 = SilkMarshal.StringToPtr(Truncate(name));

        try
        {
            var label = new DebugUtilsLabelEXT
            {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = (byte*)utf8,
            };

            DebugUtils.CmdInsertDebugUtilsLabel(commandBuffer, &label);
        }
        finally
        {
            SilkMarshal.Free(utf8);
        }
    }

    /// <summary>Opens a labelled scope on a queue, spanning every submission made inside it.</summary>
    /// <param name="queue">The queue.</param>
    /// <param name="name">The label.</param>
    /// <returns>A guard that closes the scope.</returns>
    /// <remarks>Backs <see cref="IDevice.DebugScope"/>, which names a region for callers that do not
    /// own a command list at that altitude. A queue scope is the only Vulkan construct that can span
    /// work recorded by callees onto command buffers the opener never sees.</remarks>
    public unsafe IDisposable QueueScope(Queue queue, string name)
    {
        if (DebugUtils is null)
        {
            return NullScope.Instance;
        }

        ArgumentNullException.ThrowIfNull(name);

        var utf8 = SilkMarshal.StringToPtr(Truncate(name));

        try
        {
            var label = new DebugUtilsLabelEXT
            {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = (byte*)utf8,
            };

            DebugUtils.QueueBeginDebugUtilsLabel(queue, &label);
        }
        finally
        {
            SilkMarshal.Free(utf8);
        }

        return new QueueScopeGuard(DebugUtils, queue);
    }

    /// <summary>Closes a command buffer debug scope. A struct so that the common
    /// <c>using var _ = names.Scope(...)</c> costs no allocation.</summary>
    public readonly struct CommandScope : IDisposable, IEquatable<CommandScope>
    {
        private readonly ExtDebugUtils? DebugUtils;
        private readonly CommandBuffer CommandBuffer;

        internal CommandScope(ExtDebugUtils debugUtils, CommandBuffer commandBuffer)
        {
            DebugUtils = debugUtils;
            CommandBuffer = commandBuffer;
        }

        /// <summary>Closes the scope.</summary>
        public void Dispose() => DebugUtils?.CmdEndDebugUtilsLabel(CommandBuffer);

        /// <inheritdoc/>
        public bool Equals(CommandScope other)
            => ReferenceEquals(DebugUtils, other.DebugUtils) && CommandBuffer.Handle == other.CommandBuffer.Handle;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is CommandScope other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => CommandBuffer.Handle.GetHashCode();

        /// <summary>Compares two scopes.</summary>
        /// <param name="left">The first scope.</param>
        /// <param name="right">The second scope.</param>
        /// <returns><see langword="true"/> when they are the same scope.</returns>
        public static bool operator ==(CommandScope left, CommandScope right) => left.Equals(right);

        /// <summary>Compares two scopes.</summary>
        /// <param name="left">The first scope.</param>
        /// <param name="right">The second scope.</param>
        /// <returns><see langword="true"/> when they are not the same scope.</returns>
        public static bool operator !=(CommandScope left, CommandScope right) => !left.Equals(right);
    }

    private sealed class QueueScopeGuard : IDisposable
    {
        private readonly ExtDebugUtils DebugUtils;
        private readonly Queue Queue;
        private bool Closed;

        internal QueueScopeGuard(ExtDebugUtils debugUtils, Queue queue)
        {
            DebugUtils = debugUtils;
            Queue = queue;
        }

        public void Dispose()
        {
            if (!Closed)
            {
                Closed = true;
                DebugUtils.QueueEndDebugUtilsLabel(Queue);
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
