using OpenTK.Graphics.OpenGL;
using GLApi = OpenTK.Graphics.OpenGL.GL;

namespace ValveResourceFormat.Renderer.RHI.OpenGL;

/// <summary>
/// Translates RHI resource state transitions into OpenGL memory barrier bits, backing
/// <see cref="ICommandList.Barrier(in BufferBarrier)"/> and its overloads on the OpenGL backend.
/// </summary>
/// <remarks>
/// <para>
/// OpenGL orders almost every memory access on its own. <c>glMemoryBarrier</c> is required only for
/// <i>incoherent</i> writes: image stores, storage buffer writes and atomics. A transition whose
/// <c>Before</c> state is not one of those needs no barrier at all, which is why the OpenGL backend
/// discards most calls. Vulkan needs all of them, so call sites must still issue every transition:
/// what is dropped is decided here, once, and not at the call site.
/// </para>
/// <para>
/// The bits <c>glMemoryBarrier</c> takes describe how memory will be read <i>after</i> the barrier,
/// not how it was written before it. The destination state therefore selects the bits and the source
/// state only decides whether a barrier is emitted at all.
/// </para>
/// <para>
/// <c>glMemoryBarrier</c> is global rather than per resource, so a batch of transitions collapses to
/// the union of their bits and a single call. Batching transitions into one
/// <see cref="ICommandList.Barrier(ReadOnlySpan{BufferBarrier}, ReadOnlySpan{TextureBarrier})"/>
/// therefore costs one barrier here and one pipeline stall on Vulkan, exactly as the contract asks.
/// </para>
/// </remarks>
public static class GlBarrierTranslation
{
    /// <summary>
    /// Returns <see langword="true"/> when a state is an incoherent shader write, the only kind of
    /// write OpenGL does not order by itself.
    /// </summary>
    /// <param name="state">The state a resource is transitioning out of.</param>
    /// <returns><see langword="true"/> when leaving this state needs a barrier.</returns>
    public static bool IsIncoherentWrite(ResourceState state)
        => state is ResourceState.ShaderWrite or ResourceState.ShaderReadWrite;

    /// <summary>Translates one buffer transition.</summary>
    /// <param name="barrier">The transition.</param>
    /// <returns>The barrier bits, or zero when OpenGL needs no barrier.</returns>
    public static MemoryBarrierFlags Translate(in BufferBarrier barrier)
    {
        var buffer = barrier.Buffer;
        ArgumentNullException.ThrowIfNull(buffer, nameof(barrier));

        if (!IsIncoherentWrite(barrier.Before))
        {
            return 0;
        }

        MemoryBarrierFlags flags = barrier.After switch
        {
            // A shader read reaches the buffer through whichever binding it was created for.
            ResourceState.ShaderRead => UsageReadBits(buffer.Usage),
            ResourceState.ShaderWrite or ResourceState.ShaderReadWrite => MemoryBarrierFlags.ShaderStorageBarrierBit,
            ResourceState.IndirectArgument => MemoryBarrierFlags.CommandBarrierBit,
            ResourceState.IndexBuffer => MemoryBarrierFlags.ElementArrayBarrierBit,
            ResourceState.VertexBuffer => MemoryBarrierFlags.VertexAttribArrayBarrierBit,
            ResourceState.CopySource or ResourceState.CopyDestination => MemoryBarrierFlags.BufferUpdateBarrierBit,
            _ => 0,
        };

        // A persistently mapped readback buffer is read by the client, which only sees shader writes
        // made before a barrier carrying this bit.
        if (buffer.Memory == BufferMemory.HostReadback)
        {
            flags |= MemoryBarrierFlags.ClientMappedBufferBarrierBit;
        }

        return flags;
    }

    /// <summary>Translates one texture transition.</summary>
    /// <param name="barrier">The transition.</param>
    /// <returns>The barrier bits, or zero when OpenGL needs no barrier.</returns>
    public static MemoryBarrierFlags Translate(in TextureBarrier barrier)
    {
        var texture = barrier.Texture;
        ArgumentNullException.ThrowIfNull(texture, nameof(barrier));

        if (!IsIncoherentWrite(barrier.Before))
        {
            return 0;
        }

        return barrier.After switch
        {
            // The state does not say whether the read is a sample or an image load, so the usage the
            // texture was created with decides. A texture created for both is barriered for both.
            ResourceState.ShaderRead => UsageReadBits(texture.Usage),
            ResourceState.ShaderWrite => MemoryBarrierFlags.ShaderImageAccessBarrierBit,
            ResourceState.ShaderReadWrite => MemoryBarrierFlags.ShaderImageAccessBarrierBit | UsageReadBits(texture.Usage),
            ResourceState.ColorTarget or ResourceState.DepthWrite or ResourceState.DepthRead or ResourceState.Present
                => MemoryBarrierFlags.FramebufferBarrierBit,
            ResourceState.CopySource or ResourceState.CopyDestination => MemoryBarrierFlags.TextureUpdateBarrierBit,
            _ => 0,
        };
    }

    /// <summary>Translates a batch of transitions into the single set of bits that covers them all.</summary>
    /// <param name="bufferBarriers">Buffer transitions.</param>
    /// <param name="textureBarriers">Texture transitions.</param>
    /// <returns>The union of the barrier bits, or zero when OpenGL needs no barrier.</returns>
    public static MemoryBarrierFlags Translate(ReadOnlySpan<BufferBarrier> bufferBarriers, ReadOnlySpan<TextureBarrier> textureBarriers)
    {
        MemoryBarrierFlags flags = 0;

        foreach (ref readonly var barrier in bufferBarriers)
        {
            flags |= Translate(in barrier);
        }

        foreach (ref readonly var barrier in textureBarriers)
        {
            flags |= Translate(in barrier);
        }

        return flags;
    }

    /// <summary>
    /// Issues <c>glMemoryBarrier</c> for the given bits, skipping the call when there is nothing to
    /// order so that a transition OpenGL handles by itself costs no GL call.
    /// </summary>
    /// <param name="flags">The bits to issue, normally from <see cref="Translate(in BufferBarrier)"/>.</param>
    public static void Submit(MemoryBarrierFlags flags)
    {
        if (flags == 0)
        {
            return;
        }

        GLApi.MemoryBarrier(flags);
    }

    private static MemoryBarrierFlags UsageReadBits(BufferUsage usage)
    {
        MemoryBarrierFlags flags = 0;

        if (usage.HasFlag(BufferUsage.Uniform))
        {
            flags |= MemoryBarrierFlags.UniformBarrierBit;
        }

        if (usage.HasFlag(BufferUsage.Storage))
        {
            flags |= MemoryBarrierFlags.ShaderStorageBarrierBit;
        }

        return flags;
    }

    private static MemoryBarrierFlags UsageReadBits(TextureUsage usage)
    {
        MemoryBarrierFlags flags = 0;

        if (usage.HasFlag(TextureUsage.Sampled))
        {
            flags |= MemoryBarrierFlags.TextureFetchBarrierBit;
        }

        if (usage.HasFlag(TextureUsage.Storage))
        {
            flags |= MemoryBarrierFlags.ShaderImageAccessBarrierBit;
        }

        return flags;
    }
}
