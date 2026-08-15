using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.OpenGL;

namespace Tests.Renderer.Golden
{
    /// <summary>
    /// Answers "which RHI draw sites does this suite actually reach?" by measurement rather than by
    /// reading the catalog and hoping.
    ///
    /// <para>Coverage of the RHI path is not the same as coverage of the renderer. A scene can exercise a
    /// great deal of the renderer while every draw in it comes from one call site, and the suite would look
    /// healthy either way. This wraps the command list the recording device hands out, captures the type
    /// that called each draw, and reports the set at the end of the run, so the gap is a number rather than
    /// an opinion.</para>
    ///
    /// <para>Off unless <c>VRF_RHI_CENSUS=1</c>. It walks a stack trace per draw, which is far too expensive
    /// to leave on, and it is a diagnostic rather than an assertion.</para>
    /// </summary>
    internal static class RhiCallSiteCensus
    {
        /// <summary>Set to <c>1</c> to collect the census. Requires <c>VRF_RHI_RECORDING=1</c> to see anything.</summary>
        public const string EnvironmentVariable = "VRF_RHI_CENSUS";

        /// <summary>Whether this run collects the census.</summary>
        public static bool IsEnabled
            => Environment.GetEnvironmentVariable(EnvironmentVariable) is "1" or "true";

        /// <summary>Draw calls seen per calling type, and per scene.</summary>
        private static readonly ConcurrentDictionary<string, int> DrawsByCallSite = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> ScenesByCallSite
            = new(StringComparer.Ordinal);

        /// <summary>The scene currently rendering, so the census can say which scene reached a site.</summary>
        public static string CurrentScene { get; set; } = "(none)";

        /// <summary>
        /// The nine drawing call sites the RHI migration cares about, plus the two that arrived with it.
        /// Listed explicitly so the census can report what it did <em>not</em> see, which is the half that
        /// matters; a census that only lists what it found cannot tell you what is missing.
        /// </summary>
        public static IReadOnlyList<string> ExpectedCallSites { get; } =
        [
            "MeshBatchRenderer",
            "ShapeSceneNode",
            "TextRenderer",
            "QuadOverdraw",
            "MorphComposite",
            "OcclusionDebugRenderer",
            "RenderTrails",
            "RenderSprites",
            "RenderCables",
            "PostProcessRenderer",
        ];

        /// <summary>Records one draw against whichever renderer type issued it.</summary>
        public static void RecordDraw(string callSite)
        {
            DrawsByCallSite.AddOrUpdate(callSite, 1, static (_, count) => count + 1);

            ScenesByCallSite
                .GetOrAdd(callSite, static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))
                .TryAdd(CurrentScene, 0);
        }

        /// <summary>Formats the census: what was reached, by which scenes, and what was not reached at all.</summary>
        public static string Report()
        {
            var lines = new List<string> { "RHI draw call site census (VRF_RHI_RECORDING + VRF_RHI_CENSUS):" };

            var reached = DrawsByCallSite.Keys.ToHashSet(StringComparer.Ordinal);

            foreach (var site in ExpectedCallSites)
            {
                if (DrawsByCallSite.TryGetValue(site, out var draws))
                {
                    var scenes = ScenesByCallSite[site].Keys.Order(StringComparer.Ordinal).ToList();
                    var shown = string.Join(", ", scenes.Take(4));
                    var more = scenes.Count > 4 ? $" (+{scenes.Count - 4} more)" : string.Empty;

                    lines.Add($"  REACHED     {site,-24} {draws,7} draws from {scenes.Count} scene(s): {shown}{more}");
                }
                else
                {
                    lines.Add($"  NOT REACHED {site}");
                }
            }

            foreach (var site in reached.Except(ExpectedCallSites, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                lines.Add($"  (unlisted)  {site,-24} {DrawsByCallSite[site],7} draws");
            }

            var reachedCount = ExpectedCallSites.Count(reached.Contains);
            lines.Add($"  => {reachedCount} of {ExpectedCallSites.Count} expected call sites reached.");

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// A recording device whose command list is wrapped in <see cref="CensusCommandList"/>.
    /// </summary>
    internal sealed class CensusRecordingDevice(RendererContext rendererContext, RhiMessageCallback? messageCallback)
        : GLRecordingDevice(rendererContext, messageCallback)
    {
        /// <inheritdoc/>
        public override ICommandList BeginCommandList(string name)
            => new CensusCommandList(base.BeginCommandList(name));
    }

    /// <summary>
    /// Passes every call through to the real command list, noting who called the drawing ones.
    /// </summary>
    internal sealed class CensusCommandList(ICommandList inner) : ICommandList
    {
        /// <summary>
        /// Walks back to the first frame outside the RHI and this decorator, which is the renderer type
        /// that issued the draw. Frame names are not needed, only declaring types, so the trace is
        /// captured without file information.
        /// </summary>
        private static void Note()
        {
            var trace = new StackTrace(skipFrames: 2, fNeedFileInfo: false);

            for (var i = 0; i < trace.FrameCount; i++)
            {
                var type = trace.GetFrame(i)?.GetMethod()?.DeclaringType;

                if (type is null)
                {
                    continue;
                }

                var ns = type.Namespace ?? string.Empty;

                if (ns.StartsWith("ValveResourceFormat.Renderer.RHI", StringComparison.Ordinal)
                    || ns.StartsWith("Tests.", StringComparison.Ordinal))
                {
                    continue;
                }

                // Generated closure and iterator types carry the outer type in DeclaringType.
                var owner = type;

                while (owner.DeclaringType is not null && owner.Name.StartsWith('<'))
                {
                    owner = owner.DeclaringType;
                }

                RhiCallSiteCensus.RecordDraw(owner.Name);
                return;
            }

            RhiCallSiteCensus.RecordDraw("(unattributed)");
        }

        public void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0)
        {
            Note();
            inner.Draw(vertexCount, instanceCount, firstVertex, firstInstance);
        }

        public void DrawIndexed(int indexCount, int instanceCount = 1, int firstIndex = 0, int baseVertex = 0, int firstInstance = 0)
        {
            Note();
            inner.DrawIndexed(indexCount, instanceCount, firstIndex, baseVertex, firstInstance);
        }

        public void DrawIndirect(IBuffer argumentBuffer, int offsetInBytes, int drawCount, int strideInBytes = 0)
        {
            Note();
            inner.DrawIndirect(argumentBuffer, offsetInBytes, drawCount, strideInBytes);
        }

        public void DrawIndexedIndirect(IBuffer argumentBuffer, int offsetInBytes, int drawCount, int strideInBytes = 0)
        {
            Note();
            inner.DrawIndexedIndirect(argumentBuffer, offsetInBytes, drawCount, strideInBytes);
        }

        public void DrawIndexedIndirectCount(IBuffer argumentBuffer, int argumentOffsetInBytes, IBuffer countBuffer, int countOffsetInBytes, int maxDrawCount, int strideInBytes = 0)
        {
            Note();
            inner.DrawIndexedIndirectCount(argumentBuffer, argumentOffsetInBytes, countBuffer, countOffsetInBytes, maxDrawCount, strideInBytes);
        }

        public void Dispatch(int groupCountX, int groupCountY = 1, int groupCountZ = 1)
        {
            Note();
            inner.Dispatch(groupCountX, groupCountY, groupCountZ);
        }

        // Everything below is pass-through.

        public void BeginRenderPass(in RenderPassDesc desc) => inner.BeginRenderPass(in desc);
        public void EndRenderPass() => inner.EndRenderPass();
        public void SetViewport(int x, int y, int width, int height, float minDepth = 0f, float maxDepth = 1f)
            => inner.SetViewport(x, y, width, height, minDepth, maxDepth);
        public void SetScissor(int x, int y, int width, int height) => inner.SetScissor(x, y, width, height);
        public void BindPipeline(IGraphicsPipeline pipeline) => inner.BindPipeline(pipeline);
        public void BindPipeline(IComputePipeline pipeline) => inner.BindPipeline(pipeline);
        public void BindVertexBuffer(int binding, IBuffer buffer, int offsetInBytes = 0)
            => inner.BindVertexBuffer(binding, buffer, offsetInBytes);
        public void BindIndexBuffer(IBuffer buffer, IndexType indexType, int offsetInBytes = 0)
            => inner.BindIndexBuffer(buffer, indexType, offsetInBytes);
        public void BindUniformBuffer(int binding, IBuffer buffer, int offsetInBytes = 0, int sizeInBytes = -1)
            => inner.BindUniformBuffer(binding, buffer, offsetInBytes, sizeInBytes);
        public void BindStorageBuffer(int binding, IBuffer buffer, int offsetInBytes = 0, int sizeInBytes = -1)
            => inner.BindStorageBuffer(binding, buffer, offsetInBytes, sizeInBytes);
        public void BindTexture(int descriptorSet, int binding, ITexture texture, ISampler? sampler = null)
            => inner.BindTexture(descriptorSet, binding, texture, sampler);
        public void BindStorageTexture(int binding, ITexture texture, int mipLevel = 0)
            => inner.BindStorageTexture(binding, texture, mipLevel);
        public void DispatchIndirect(IBuffer argumentBuffer, int offsetInBytes)
            => inner.DispatchIndirect(argumentBuffer, offsetInBytes);
        public void Barrier(ReadOnlySpan<BufferBarrier> bufferBarriers, ReadOnlySpan<TextureBarrier> textureBarriers)
            => inner.Barrier(bufferBarriers, textureBarriers);
        public void Barrier(in TextureBarrier barrier) => inner.Barrier(in barrier);
        public void Barrier(in BufferBarrier barrier) => inner.Barrier(in barrier);
        public void CopyBuffer(IBuffer source, int sourceOffsetInBytes, IBuffer destination, int destinationOffsetInBytes, int sizeInBytes)
            => inner.CopyBuffer(source, sourceOffsetInBytes, destination, destinationOffsetInBytes, sizeInBytes);
        public void CopyTexture(ITexture source, ITexture destination, int mipLevel = 0)
            => inner.CopyTexture(source, destination, mipLevel);
        public void CopyTextureToBuffer(ITexture source, int mipLevel, int arrayLayer, IBuffer destination, int destinationOffsetInBytes = 0)
            => inner.CopyTextureToBuffer(source, mipLevel, arrayLayer, destination, destinationOffsetInBytes);
        public void BlitTexture(ITexture source, ITexture destination, FilterMode filter = FilterMode.Linear)
            => inner.BlitTexture(source, destination, filter);
        public void FillBuffer(IBuffer buffer, int offsetInBytes, int sizeInBytes, uint value)
            => inner.FillBuffer(buffer, offsetInBytes, sizeInBytes, value);
        public void ClearTexture(ITexture texture, int mipLevel, uint value) => inner.ClearTexture(texture, mipLevel, value);
        public IDisposable DebugScope(string name) => inner.DebugScope(name);
        public void DebugMarker(string name) => inner.DebugMarker(name);
        public IDevice Device => inner.Device;
        public void BindTransientUniform<T>(int binding, in T data) where T : unmanaged
            => inner.BindTransientUniform(binding, in data);
        public void SetPushConstants<T>(in T data, int offsetInBytes = 0) where T : unmanaged
            => inner.SetPushConstants(in data, offsetInBytes);

        /// <summary>The wrapped list is owned by the device, which disposes it; this decorator owns nothing.</summary>
        public void Dispose()
        {
        }
    }
}
