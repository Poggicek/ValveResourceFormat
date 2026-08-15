using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace ValveResourceFormat.Renderer.RHI.Vulkan;

/// <summary>
/// Something the pipeline layer counts, one per line of the report.
/// </summary>
/// <remarks>
/// Named and shaped after the renderer's existing <c>Counter</c> enum in <c>PerfStats</c>: a flat enum
/// indexing a fixed array, incremented through one method. It is separate from that enum only because
/// <c>Counter</c> lives in <c>PerfStats.cs</c>, which this agent does not own; see the remarks on
/// <see cref="VulkanPipelineStats"/>.
/// </remarks>
public enum VulkanPipelineCounter
{
    /// <summary>Graphics pipelines actually compiled by the driver.</summary>
    GraphicsPipelinesCreated,

    /// <summary>Compute pipelines actually compiled by the driver.</summary>
    ComputePipelinesCreated,

    /// <summary>Graphics pipeline requests answered from the in-memory cache.</summary>
    GraphicsCacheHits,

    /// <summary>Compute pipeline requests answered from the in-memory cache.</summary>
    ComputeCacheHits,

    /// <summary>Pipeline layouts created.</summary>
    PipelineLayoutsCreated,

    /// <summary>Pipeline layout requests answered from the layout cache.</summary>
    PipelineLayoutCacheHits,

    /// <summary>Descriptor set layouts created.</summary>
    DescriptorSetLayoutsCreated,

    /// <summary>Descriptor set layout requests answered from the layout cache.</summary>
    DescriptorSetLayoutCacheHits,

    /// <summary>Shader modules whose SPIR-V interface was reflected.</summary>
    ShaderModulesReflected,

    /// <summary>Descriptor declarations that violate the set scheme in the RHI contract.</summary>
    DescriptorConformanceViolations,

    /// <summary>Vertex inputs a shader reads that its pipeline's vertex layout does not supply.</summary>
    VertexInterfaceMismatches,

    /// <summary>Bytes of <c>VkPipelineCache</c> data loaded from disk.</summary>
    DiskCacheBytesLoaded,

    /// <summary>Bytes of <c>VkPipelineCache</c> data written to disk.</summary>
    DiskCacheBytesWritten,

    /// <summary>Milliseconds of wall clock spent inside <c>vkCreate*Pipelines</c>, summed across threads.</summary>
    CompileMilliseconds,

    /// <summary>Milliseconds the slowest single pipeline compile took.</summary>
    SlowestCompileMilliseconds,

    /// <summary>Pipelines compiled on a thread other than the one that asked for them.</summary>
    PipelinesCompiledAsynchronously,
}

/// <summary>
/// Counters for the Vulkan pipeline layer, including the shader-variant instrumentation that answers
/// how many pipelines the material system actually produces.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not yet wired into <see cref="PerfStats"/>, and it should be.</b> <c>PerfStats</c> counts
/// through an <see langword="internal"/> <c>Counter</c> enum declared in <c>PerfStats.cs</c>, and an
/// enum cannot be extended from another file, so publishing these through it needs members added to
/// that enum. This class mirrors its pattern exactly &#8212; flat enum, fixed array, one
/// <see cref="Count"/> method &#8212; so that wiring is a mechanical change once ownership allows.
/// </para>
/// <para>
/// Unlike <c>PerfStats</c> these counters are process-lifetime rather than per-frame, and they are
/// updated with <see cref="Interlocked"/> because pipeline compilation deliberately runs on the thread
/// pool. A pipeline is compiled once and then reused for the rest of the run, so a per-frame reset
/// would hide exactly the number worth knowing.
/// </para>
/// </remarks>
public sealed class VulkanPipelineStats
{
    private static readonly int CounterCount = Enum.GetValues<VulkanPipelineCounter>().Length;

    private readonly long[] Counts = new long[CounterCount];

    // Pipelines produced per distinct shader pair, which is what makes the combinatorial risk visible:
    // the count is programs, the values are how many render states each program was drawn under.
    private readonly ConcurrentDictionary<ulong, int> PipelinesPerShaderPair = new();
    private readonly ConcurrentDictionary<ulong, byte> DistinctRenderStates = new();
    private readonly ConcurrentDictionary<ulong, byte> DistinctVertexLayouts = new();
    private readonly ConcurrentDictionary<ulong, byte> DistinctRenderTargets = new();

    /// <summary>Gets the value of a counter.</summary>
    /// <param name="counter">The counter to read.</param>
    /// <returns>Its current value.</returns>
    public long this[VulkanPipelineCounter counter] => Volatile.Read(ref Counts[(int)counter]);

    /// <summary>Gets the number of distinct shader pairs any graphics pipeline was built from.</summary>
    public int DistinctShaderPairs => PipelinesPerShaderPair.Count;

    /// <summary>Gets the number of distinct <see cref="RenderState"/> values seen across all pipelines.</summary>
    public int DistinctRenderStateCount => DistinctRenderStates.Count;

    /// <summary>Gets the number of distinct vertex input layouts seen across all pipelines.</summary>
    public int DistinctVertexLayoutCount => DistinctVertexLayouts.Count;

    /// <summary>Gets the number of distinct attachment format and sample count combinations seen.</summary>
    public int DistinctRenderTargetCount => DistinctRenderTargets.Count;

    /// <summary>Gets the largest number of pipelines any one shader pair produced.</summary>
    /// <remarks>The headline combinatorial number. On OpenGL this is always 1, because a program is the
    /// pipeline; here it is how many render states the worst shader gets drawn under.</remarks>
    public int WorstCaseVariantsPerShaderPair
        => PipelinesPerShaderPair.IsEmpty ? 0 : PipelinesPerShaderPair.Values.Max();

    /// <summary>Gets the mean number of pipelines produced per shader pair.</summary>
    public double MeanVariantsPerShaderPair
        => PipelinesPerShaderPair.IsEmpty
            ? 0
            : (double)this[VulkanPipelineCounter.GraphicsPipelinesCreated] / PipelinesPerShaderPair.Count;

    /// <summary>Increments a counter.</summary>
    /// <param name="counter">The counter to raise.</param>
    /// <param name="amount">How much to add.</param>
    public void Count(VulkanPipelineCounter counter, long amount = 1)
        => Interlocked.Add(ref Counts[(int)counter], amount);

    /// <summary>Raises a counter to <paramref name="value"/> if it is currently lower.</summary>
    /// <param name="counter">The counter to raise.</param>
    /// <param name="value">The candidate high-water mark.</param>
    public void Max(VulkanPipelineCounter counter, long value)
    {
        ref var slot = ref Counts[(int)counter];

        while (true)
        {
            var current = Volatile.Read(ref slot);

            if (value <= current || Interlocked.CompareExchange(ref slot, value, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>Records the shape of a graphics pipeline that was compiled, for the variant report.</summary>
    /// <param name="key">The key it was cached under.</param>
    /// <remarks>Only called on a genuine compile, never on a cache hit, so the counts are of distinct
    /// pipeline objects rather than of requests.</remarks>
    public void RecordVariant(in PipelineCacheKey key)
    {
        // The pair, not either stage alone: two pipelines sharing a vertex shader but not a fragment
        // shader are different programs, and counting by vertex stage alone would understate the fan-out.
        var pair = VulkanPipelineKey.Mix(key.VertexShaderHash, key.FragmentShaderHash);

        PipelinesPerShaderPair.AddOrUpdate(pair, 1, static (_, existing) => existing + 1);
        DistinctRenderStates.TryAdd(VulkanPipelineKey.HashRenderState(in key.RenderState), 0);
        DistinctVertexLayouts.TryAdd(key.VertexInputHash, 0);
        DistinctRenderTargets.TryAdd(VulkanPipelineKey.Mix(key.RenderTargetHash, (ulong)key.SampleCount), 0);
    }

    /// <summary>Renders the counters and the variant report as text.</summary>
    /// <returns>The report.</returns>
    public override string ToString()
    {
        var builder = new StringBuilder(1024);

        builder.AppendLine("Vulkan pipeline stats");

        foreach (var counter in Enum.GetValues<VulkanPipelineCounter>())
        {
            var value = this[counter];

            if (value != 0)
            {
                builder.Append(CultureInfo.InvariantCulture, $"  {counter,-34} {value:N0}");
                builder.AppendLine();
            }
        }

        builder.Append(CultureInfo.InvariantCulture,
            $"  distinct shader pairs              {DistinctShaderPairs:N0}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture,
            $"  distinct render states             {DistinctRenderStateCount:N0}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture,
            $"  distinct vertex layouts            {DistinctVertexLayoutCount:N0}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture,
            $"  distinct render targets            {DistinctRenderTargetCount:N0}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture,
            $"  variants per shader pair           {MeanVariantsPerShaderPair:0.00} mean, {WorstCaseVariantsPerShaderPair:N0} worst");
        builder.AppendLine();

        return builder.ToString();
    }
}
