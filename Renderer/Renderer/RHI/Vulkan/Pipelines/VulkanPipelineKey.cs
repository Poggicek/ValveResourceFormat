using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// Builds and hashes the <see cref="PipelineCacheKey"/> a Vulkan pipeline is deduplicated under.
/// </summary>
/// <remarks>
/// <para>
/// Every hash here is FNV-1a, never <see cref="HashCode"/>. That is not a style preference:
/// <see cref="HashCode"/> is seeded per process, so a key hashed in one run does not match the same key
/// hashed in the next, and this backend genuinely persists a cache across runs
/// (<see cref="VulkanPipelineCache"/> writes one to disk). A per-process seed would make every entry of
/// that file a miss, silently, with nothing to observe but a slow first frame.
/// </para>
/// <para>
/// The key itself is <c>Pack = 1</c> plain-old-data holding no references, so it is hashed and compared
/// over its raw bytes. That is what <see cref="PipelineCacheKey"/> was shaped for.
/// </para>
/// <para>
/// This deliberately duplicates <c>GLGraphicsPipeline.CreateCacheKey</c> rather than calling it, so the
/// Vulkan backend does not take a compile-time dependency on the OpenGL one. The two are written to
/// agree, and <c>VulkanPipelineSmokeTest</c> checks that they still do.
/// </para>
/// </remarks>
public static class VulkanPipelineKey
{
    /// <summary>The FNV-1a 64 bit offset basis, the starting value of every hash here.</summary>
    public const ulong OffsetBasis = 14695981039346656037;

    private const ulong FnvOffsetBasis = OffsetBasis;
    private const ulong FnvPrime = 1099511628211;

    /// <summary>Builds the key a graphics pipeline description deduplicates under.</summary>
    /// <param name="description">The description to key.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="description"/> is <see langword="null"/>.</exception>
    /// <exception cref="OverflowException">The sample count does not fit a byte.</exception>
    public static PipelineCacheKey ForGraphics(GraphicsPipelineDesc description)
    {
        ArgumentNullException.ThrowIfNull(description);

        return new PipelineCacheKey
        {
            RenderState = description.RenderState,
            VertexShaderHash = description.VertexShader.ContentHash,
            FragmentShaderHash = description.FragmentShader?.ContentHash ?? 0,
            VertexInputHash = HashVertexInput(description.VertexInput),
            RenderTargetHash = HashRenderTarget(description.ColorFormats, description.DepthFormat, description.SampleCount),
            Topology = description.Topology,
            SampleCount = checked((byte)description.SampleCount),
        };
    }

    /// <summary>Builds the key a compute pipeline description deduplicates under.</summary>
    /// <param name="description">The description to key.</param>
    /// <returns>The key.</returns>
    /// <remarks>Compute has no render state, vertex input or attachments, so only the module identifies
    /// it. Compute pipelines are kept in their own dictionary anyway, so a graphics pipeline over a
    /// default render state cannot collide with one.</remarks>
    public static PipelineCacheKey ForCompute(in ComputePipelineDesc description)
        => new()
        {
            VertexShaderHash = description.ComputeShader.ContentHash,
        };

    /// <summary>Hashes a vertex input layout.</summary>
    /// <param name="vertexInput">The layout to hash.</param>
    /// <returns>The hash.</returns>
    public static ulong HashVertexInput(in VertexInputDesc vertexInput)
    {
        var hash = FnvOffsetBasis;

        foreach (var attribute in vertexInput.Attributes ?? [])
        {
            hash = HashValue(hash, (ulong)attribute.Location);
            hash = HashValue(hash, (ulong)attribute.Format);
            hash = HashValue(hash, (ulong)attribute.OffsetInBytes);
            hash = HashValue(hash, (ulong)attribute.Binding);
        }

        foreach (var binding in vertexInput.Bindings ?? [])
        {
            hash = HashValue(hash, (ulong)binding.Binding);
            hash = HashValue(hash, (ulong)binding.StrideInBytes);
            hash = HashValue(hash, binding.PerInstance ? 1ul : 0ul);
        }

        return hash;
    }

    /// <summary>Hashes the attachment formats a pipeline renders into.</summary>
    /// <param name="colorFormats">The colour attachment formats, or <see langword="null"/> for none.</param>
    /// <param name="depthFormat">The depth attachment format.</param>
    /// <param name="sampleCount">The sample count.</param>
    /// <returns>The hash.</returns>
    /// <remarks>Part of the key because dynamic rendering has no render pass object to infer these from,
    /// and a pipeline is only usable inside a rendering info that matches them.</remarks>
    public static ulong HashRenderTarget(RhiFormat[]? colorFormats, RhiFormat depthFormat, int sampleCount)
    {
        var hash = FnvOffsetBasis;

        foreach (var format in colorFormats ?? [])
        {
            hash = HashValue(hash, (ulong)format);
        }

        hash = HashValue(hash, (ulong)depthFormat);
        hash = HashValue(hash, (ulong)sampleCount);

        return hash;
    }

    /// <summary>Hashes a render state over its raw bytes.</summary>
    /// <param name="state">The state to hash.</param>
    /// <returns>The hash.</returns>
    public static ulong HashRenderState(in RenderState state)
        => Fnv1a(MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<RenderState, byte>(ref Unsafe.AsRef(in state)),
            Unsafe.SizeOf<RenderState>()));

    /// <summary>Folds two hashes into one.</summary>
    /// <param name="first">The first hash.</param>
    /// <param name="second">The second hash.</param>
    /// <returns>The combined hash.</returns>
    public static ulong Mix(ulong first, ulong second) => HashValue(HashValue(FnvOffsetBasis, first), second);

    /// <summary>Hashes a byte sequence with FNV-1a.</summary>
    /// <param name="data">The bytes to hash.</param>
    /// <returns>The hash.</returns>
    public static ulong Fnv1a(ReadOnlySpan<byte> data)
    {
        var hash = FnvOffsetBasis;

        foreach (var b in data)
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        return hash;
    }

    /// <summary>Folds a string into a running hash, terminated so concatenations cannot collide.</summary>
    /// <param name="hash">The running hash.</param>
    /// <param name="value">The string to fold in.</param>
    /// <returns>The updated hash.</returns>
    public static ulong HashString(ulong hash, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        foreach (var c in value)
        {
            hash ^= (byte)c;
            hash *= FnvPrime;
            hash ^= (byte)(c >> 8);
            hash *= FnvPrime;
        }

        hash ^= 0xFF;
        hash *= FnvPrime;

        return hash;
    }

    /// <summary>Folds a 64 bit value into a running hash, byte by byte.</summary>
    /// <param name="hash">The running hash.</param>
    /// <param name="value">The value to fold in.</param>
    /// <returns>The updated hash.</returns>
    public static ulong HashValue(ulong hash, ulong value)
    {
        for (var i = 0; i < sizeof(ulong); i++)
        {
            hash ^= (value >> (i * 8)) & 0xFF;
            hash *= FnvPrime;
        }

        return hash;
    }

    /// <summary>Gets the raw bytes of a key.</summary>
    /// <param name="key">The key to view.</param>
    /// <returns>Its exact bit image.</returns>
    public static ReadOnlySpan<byte> AsBytes(ref PipelineCacheKey key)
        => MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<PipelineCacheKey, byte>(ref key),
            Unsafe.SizeOf<PipelineCacheKey>());

    /// <summary>
    /// Hashes and compares a <see cref="PipelineCacheKey"/> over its raw bytes, reproducibly across runs.
    /// </summary>
    /// <remarks>The default equality of a <c>record struct</c> would compare correctly, but it hashes
    /// field by field through <see cref="HashCode"/> and so is not reproducible. See the class remarks.</remarks>
    public sealed class Comparer : IEqualityComparer<PipelineCacheKey>
    {
        /// <summary>Gets the shared instance.</summary>
        public static Comparer Instance { get; } = new();

        /// <inheritdoc/>
        public bool Equals(PipelineCacheKey x, PipelineCacheKey y)
            => AsBytes(ref x).SequenceEqual(AsBytes(ref y));

        /// <inheritdoc/>
        public int GetHashCode(PipelineCacheKey obj)
        {
            var hash = Fnv1a(AsBytes(ref obj));

            // The dictionary wants 32 bits; fold rather than truncate so the whole hash contributes.
            return (int)(hash ^ (hash >> 32));
        }
    }
}
