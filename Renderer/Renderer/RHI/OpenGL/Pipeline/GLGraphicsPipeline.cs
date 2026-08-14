using System.Runtime.CompilerServices;
using OpenTK.Graphics.OpenGL;

namespace ValveResourceFormat.Renderer.RHI.OpenGL;

/// <summary>
/// <see cref="IGraphicsPipeline"/> on OpenGL: a linked program plus the fixed-function state to apply
/// before drawing with it.
/// </summary>
/// <remarks>
/// <para>
/// OpenGL has no pipeline object, so this is not a compiled artifact the way a <c>VkPipeline</c> is. It
/// is the same pair the renderer already draws with &#8212; a program and a <see cref="RenderState"/>
/// &#8212; behind the interface the Vulkan backend bakes into a real pipeline state object.
/// </para>
/// <para>
/// That difference is why <see cref="Bind"/> takes the context's <see cref="RenderStateTracker"/> rather
/// than applying state itself. The tracker diffs at two levels, one compare per descriptor and then only
/// the calls whose fields changed, which is a real saving across a batch of draws that share most of
/// their state. Vulkan gets that saving from the pipeline object instead; on GL it stays the strategy.
/// </para>
/// <para>
/// A pipeline does not own its program. <see cref="ShaderLoader"/> does, and the same program backs many
/// pipelines that differ only in state, so <see cref="Dispose"/> deliberately does not delete it.
/// </para>
/// </remarks>
public sealed class GLGraphicsPipeline : IGraphicsPipeline
{
    /// <inheritdoc/>
    public GraphicsPipelineDesc Description { get; }

    /// <inheritdoc/>
    public string Name => Description.Name;

    /// <summary>Gets the linked program this pipeline draws with.</summary>
    public Shader Program { get; }

    /// <summary>Gets the primitive topology, already translated for the draw entry points.</summary>
    public PrimitiveType Topology { get; }

    /// <summary>Gets the push constant block of <see cref="Program"/>.</summary>
    /// <remarks>Cached on the shader, not here: uniform state belongs to the program, so two pipelines
    /// over one program share a block and its diff baseline.</remarks>
    public GLPushConstantBlock PushConstants => Program.PushConstants;

    /// <summary>Gets the key this pipeline would be cached under.</summary>
    public PipelineCacheKey CacheKey { get; }

    /// <summary>Initializes a pipeline over an already linked program.</summary>
    /// <param name="description">The pipeline state. Its <see cref="GraphicsPipelineDesc.RenderState"/> is
    /// applied by <see cref="Bind"/>.</param>
    /// <param name="program">The linked program, which the pipeline does not take ownership of.</param>
    public GLGraphicsPipeline(GraphicsPipelineDesc description, Shader program)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(program);

        Description = description;
        Program = program;
        Topology = ToGL(description.Topology);
        CacheKey = CreateCacheKey(description);
    }

    /// <summary>Makes this pipeline current: installs its program and applies its render state.</summary>
    /// <param name="tracker">The state tracker of the context being drawn into, which diffs the state
    /// against what is already applied.</param>
    public void Bind(RenderStateTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        Program.Use();

        var state = Description.RenderState;
        tracker.Apply(in state);
    }

    /// <summary>Writes push constants for this pipeline's program.</summary>
    /// <typeparam name="T">The block type. Only <see cref="DrawPushConstants"/> is mappable on OpenGL.</typeparam>
    /// <param name="data">The block to write.</param>
    /// <param name="offsetInBytes">Byte offset within the block. Only 0 is supported on OpenGL.</param>
    /// <exception cref="NotSupportedException">The block type has no uniform mapping on this backend.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offsetInBytes"/> is not zero.</exception>
    /// <remarks>
    /// OpenGL has no push constants, so a block can only be written if the backend knows which uniforms its
    /// fields correspond to. An unknown block throws rather than silently writing nothing, which would
    /// render every draw with stale constants.
    /// </remarks>
    public void SetPushConstants<T>(in T data, int offsetInBytes = 0) where T : unmanaged
    {
        if (typeof(T) != typeof(DrawPushConstants))
        {
            throw new NotSupportedException(
                $"The OpenGL backend cannot map '{typeof(T).Name}' onto program uniforms. Push constant blocks it writes have to be {nameof(DrawPushConstants)}.");
        }

        ref var block = ref Unsafe.As<T, DrawPushConstants>(ref Unsafe.AsRef(in data));
        PushConstants.SetPushConstants(in block, offsetInBytes);
    }

    /// <summary>Builds the cache key a pipeline description dedups under.</summary>
    /// <param name="description">The description to key.</param>
    /// <returns>The key, which contains no references and so can be hashed or compared as raw bytes.</returns>
    public static PipelineCacheKey CreateCacheKey(GraphicsPipelineDesc description)
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

    // FNV-1a rather than HashCode, whose seed is randomized per process. The key is only ever compared
    // in memory today, but a pipeline cache that survives a run is the obvious next use of it, and a
    // hash that changes between runs would silently miss every entry.
    private const ulong FnvOffsetBasis = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    private static ulong HashValue(ulong hash, ulong value)
    {
        for (var i = 0; i < sizeof(ulong); i++)
        {
            hash ^= (value >> (i * 8)) & 0xFF;
            hash *= FnvPrime;
        }

        return hash;
    }

    private static ulong HashVertexInput(in VertexInputDesc vertexInput)
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

    private static ulong HashRenderTarget(RhiFormat[] colorFormats, RhiFormat depthFormat, int sampleCount)
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

    private static PrimitiveType ToGL(PrimitiveTopology topology) => topology switch
    {
        PrimitiveTopology.PointList => PrimitiveType.Points,
        PrimitiveTopology.LineList => PrimitiveType.Lines,
        PrimitiveTopology.LineStrip => PrimitiveType.LineStrip,
        PrimitiveTopology.TriangleList => PrimitiveType.Triangles,
        PrimitiveTopology.TriangleStrip => PrimitiveType.TriangleStrip,
        _ => throw new NotImplementedException($"Unknown primitive topology {topology}"),
    };

    /// <summary>Releases the pipeline. The program belongs to <see cref="ShaderLoader"/> and outlives it.</summary>
    public void Dispose()
    {
        // Nothing to release: a GL pipeline owns no object of its own.
    }
}
