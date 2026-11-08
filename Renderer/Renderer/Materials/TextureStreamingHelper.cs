using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenTK.Graphics.OpenGL;

namespace ValveResourceFormat.Renderer.Materials
{
    /// <summary>How texture mip data reaches the GPU.</summary>
    public enum TextureStreamingMode
    {
        /// <summary>Read in the background, arriving over the frames after load.</summary>
        Timesliced,

        /// <summary>Read in the background, but complete before the next frame is drawn.</summary>
        Immediate,

        /// <summary>Read whole on the thread that asks for the texture, using no background threads.</summary>
        Synchronous,
    }

    /// <summary>
    /// Reads texture mip data on background threads and gives it to the GPU over the frames that follow.
    /// A streamed texture starts out holding only its smallest mip. A texture materials draw then grows to
    /// the resolution the closest mesh drawing it needs, measured by <see cref="GatherDemand"/>, and gives
    /// back mips no mesh has needed for a while. Any other texture grows to its whole chain.
    /// </summary>
    public sealed class TextureStreamingHelper(RendererContext rendererContext)
    {
        // Streams waiting to ask for their next mips: chains heading to a target, and chains parked by the throttle
        private readonly ConcurrentQueue<StreamedTexture> pendingLoadRequests = new();

        // One FIFO for everything: a texture only has one request out at a time, so this is already in chain order
        private readonly ConcurrentQueue<LoadedMipBits> loadedMipBits = new();

        // Textures created since the last slice, which may happen on a loading thread
        private readonly ConcurrentQueue<StreamedTexture> newStreams = new();

        // Every live streamed texture, owned by the thread doing the slices
        private readonly List<StreamedTexture> streams = [];
        private readonly Lock streamsLock = new();

        // Cap on buffer bytes between a load request and its hookup.
        private const long MaxBitsInFlight = 256L * 1024 * 1024;

        private long bitsInFlight;

        private long lastSliceDuration;

        // smallest mips are batched together, up to this size
        private const int RequestByteBudget = 32 * 1024; // 32KB, ~128x128 with block compression

        // How often the meshes are measured, and how long a texture no mesh measured waits before it is
        // taken to be drawn by something else and streamed whole.
        private const float DemandIntervalSeconds = 0.25f;
        private const float UnmeasuredGraceSeconds = 1f;

        // How long a texture holds mips it no longer needs, so looking around does not reload them
        private const float ShrinkDelaySeconds = 3f;
        private const int MaxShrinksPerSlice = 32;

        // The demand is measured to the closest point of a mesh's bounds as if it faced the camera, which
        // undershoots at the edges of a wide view, so it asks for a little more than it works out
        private const float DemandBiasLevels = 0.5f;

        private int demandPass;
        private long lastDemandTimestamp;

        /// <summary>
        /// How textures load. Safe to change at any point; chains already under way finish either way.
        /// </summary>
        public TextureStreamingMode Mode { get; set; }

        private int RequestBudget => Mode == TextureStreamingMode.Immediate ? int.MaxValue : RequestByteBudget;

        internal void BeginStreaming(StreamedTexture stream)
        {
            stream.RegisteredTimestamp = Stopwatch.GetTimestamp();
            stream.TargetChainLevel = stream.ResidentChainLevel;
            newStreams.Enqueue(stream);
        }

        /// <summary>
        /// Throws away everything queued and hands its buffers back to the pool. Makes no GL calls, so it
        /// is safe during teardown with no current context.
        /// </summary>
        public void CancelAllStreaming()
        {
            while (pendingLoadRequests.TryDequeue(out var stream))
            {
                stream.Queued = false;
            }

            // Drained rather than cleared: these buffers belong to the pool, not the garbage collector. The
            // count comes off per item, never zeroed, so a load still in flight keeps its bytes counted.
            // This does not wait for those: teardown does the waiting, off the thread that closed the tab.
            while (loadedMipBits.TryDequeue(out var bits))
            {
                ArrayPool<byte>.Shared.Return(bits.Buffer);
                Interlocked.Add(ref bitsInFlight, -bits.ByteSize);
                bits.Stream.LoadInFlight = false;
            }

            newStreams.Clear();

            using (streamsLock.EnterScope())
            {
                streams.Clear();
            }
        }

        /// <summary>
        /// Waits for dispatched loads to land, so no thread is left reading a texture resource that the
        /// caller is about to dispose. Makes no GL calls. Never call this on the UI thread.
        /// </summary>
        public void DrainPendingLoads()
        {
            var backoff = new SpinWait();
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;

            while (Interlocked.Read(ref bitsInFlight) > 0)
            {
                while (loadedMipBits.TryDequeue(out var bits))
                {
                    ArrayPool<byte>.Shared.Return(bits.Buffer);
                    Interlocked.Add(ref bitsInFlight, -bits.ByteSize);
                    bits.Stream.LoadInFlight = false;
                }

                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    // Only reachable if the throttle stopped balancing, which is a bug in here rather than
                    // a slow read. Give up so teardown is not held hostage by it.
                    rendererContext.Logger.LogError("Gave up waiting on {Bytes} streaming bytes", Interlocked.Read(ref bitsInFlight));
                    break;
                }

                backoff.SpinOnce();
            }
        }

        /// <summary>
        /// Measures the resolution each streamed texture needs, from the distance to the closest mesh that
        /// draws it, the mesh's texel density and the camera's projection. Runs a few times a second, or
        /// every call while textures load immediately. Call before the slice, on a thread with a GL context.
        /// </summary>
        /// <param name="camera">The camera the scenes are seen through.</param>
        /// <param name="scenes">The scenes drawn, with sky scenes mapped into the viewer's world.</param>
        internal void GatherDemand(Camera camera, IReadOnlyList<Scene> scenes)
        {
            if (Mode == TextureStreamingMode.Timesliced && newStreams.IsEmpty && lastDemandTimestamp != 0
                && Stopwatch.GetElapsedTime(lastDemandTimestamp).TotalSeconds < DemandIntervalSeconds)
            {
                return;
            }

            lastDemandTimestamp = Stopwatch.GetTimestamp();
            demandPass++;

            // Pixels a world unit covers at distance 1. An orthographic projection is the same at any distance.
            var projection = camera.ProjectionMatrix;
            var orthographic = projection.M44 == 1f;
            var pixelsPerUnitAtUnitDistance = projection.M22 * camera.WindowSize.Y * 0.5f;

            foreach (var scene in scenes)
            {
                var toViewerWorld = scene.ToViewerWorld;
                var sceneScale = MathUtils.MaxAxisScale(toViewerWorld);

                GatherNodeDemand(scene.StaticNodes, camera.Location, toViewerWorld, sceneScale, orthographic, pixelsPerUnitAtUnitDistance);
                GatherNodeDemand(scene.DynamicNodes, camera.Location, toViewerWorld, sceneScale, orthographic, pixelsPerUnitAtUnitDistance);
            }
        }

        private void GatherNodeDemand(IReadOnlyList<SceneNode> nodes, Vector3 eye, in Matrix4x4 toViewerWorld, float sceneScale, bool orthographic, float pixelsPerUnitAtUnitDistance)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];

                if (!node.LayerEnabled || node is not (MeshCollectionNode or SceneAggregate.Fragment))
                {
                    continue;
                }

                var distance = MathF.Sqrt(node.BoundingBox.Transform(toViewerWorld).DistanceSquared(eye));
                var pixelsPerUnit = orthographic ? pixelsPerUnitAtUnitDistance : pixelsPerUnitAtUnitDistance / Math.Max(distance, 1f);
                var worldScale = sceneScale * MathUtils.MaxAxisScale(node.Transform);

                if (node is SceneAggregate.Fragment fragment)
                {
                    AddDemand(fragment.DrawCall, pixelsPerUnit, worldScale);
                    continue;
                }

                foreach (var mesh in ((MeshCollectionNode)node).AllRenderableMeshes)
                {
                    AddDemand(mesh.DrawCallsOpaque, pixelsPerUnit, worldScale);
                    AddDemand(mesh.DrawCallsBlended, pixelsPerUnit, worldScale);
                    AddDemand(mesh.DrawCallsOverlay, pixelsPerUnit, worldScale);
                }
            }
        }

        private void AddDemand(List<DrawCall> drawCalls, float pixelsPerUnit, float worldScale)
        {
            foreach (var drawCall in drawCalls)
            {
                AddDemand(drawCall, pixelsPerUnit, worldScale);
            }
        }

        private void AddDemand(DrawCall drawCall, float pixelsPerUnit, float worldScale)
        {
            var material = drawCall.Material;

            // World units one unit of the coordinates the textures are sampled with spans
            var unitsPerUv = drawCall.UvDensity * worldScale / material.TexCoordScale;

            foreach (var (_, texture) in material.Textures)
            {
                if (texture.Stream is not { } stream)
                {
                    continue;
                }

                var level = 0;

                // A mesh without a density, or a cube map, which is not sampled by texture coordinates, needs it whole
                if (unitsPerUv > 0f && texture.Target is not (TextureTarget.TextureCubeMap or TextureTarget.TextureCubeMapArray))
                {
                    var texelsPerPixel = Math.Max(stream.AllocWidth, stream.AllocHeight) / (unitsPerUv * pixelsPerUnit);
                    var levelFromSize = MathF.Floor(MathF.Log2(texelsPerPixel) - DemandBiasLevels);

                    level = float.IsFinite(levelFromSize) ? (int)Math.Clamp(levelFromSize, 0f, stream.ChainLevels - 1) : 0;
                }

                if (stream.DemandPass != demandPass)
                {
                    stream.DemandPass = demandPass;
                    stream.DemandChainLevel = level;
                }
                else if (level < stream.DemandChainLevel)
                {
                    stream.DemandChainLevel = level;
                }
            }
        }

        // The chain level a texture should hold, from its last measured demand
        private int ChooseTarget(StreamedTexture stream, bool immediate)
        {
            if (stream.NeedsWholeChain)
            {
                return 0;
            }

            if (stream.DemandPass == demandPass && demandPass != 0)
            {
                return stream.DemandChainLevel;
            }

            // Measured before but by no mesh now, such as one on a hidden layer: hold what it has
            if (stream.DemandPass != 0)
            {
                return stream.ResidentChainLevel;
            }

            // Never measured, so either no mesh draws it, or the measure has not run since it loaded
            if (immediate || Stopwatch.GetElapsedTime(stream.RegisteredTimestamp).TotalSeconds >= UnmeasuredGraceSeconds)
            {
                return 0;
            }

            return stream.ResidentChainLevel;
        }

        // Points every texture at its target: queues loads for those that need more, and shrinks those that
        // have held more than they need for a while.
        private void UpdateTargets(bool immediate)
        {
            using var _ = streamsLock.EnterScope();

            while (newStreams.TryDequeue(out var stream))
            {
                streams.Add(stream);
            }

            var now = Stopwatch.GetTimestamp();
            var shrinkDelay = (long)(ShrinkDelaySeconds * Stopwatch.Frequency);
            var shrinks = 0;

            for (var i = streams.Count - 1; i >= 0; i--)
            {
                var stream = streams[i];

                if (stream.Texture.Handle == 0 || stream.Failed)
                {
                    streams[i] = streams[^1];
                    streams.RemoveAt(streams.Count - 1);
                    continue;
                }

                var target = ChooseTarget(stream, immediate);
                stream.TargetChainLevel = target;

                if (target < stream.ResidentChainLevel)
                {
                    stream.ExcessSinceTimestamp = 0;

                    if (!stream.LoadInFlight && !stream.Queued)
                    {
                        stream.Queued = true;
                        pendingLoadRequests.Enqueue(stream);
                    }
                }
                else if (target > stream.AllocatedChainLevel && !immediate)
                {
                    if (stream.ExcessSinceTimestamp == 0)
                    {
                        stream.ExcessSinceTimestamp = now;
                    }
                    else if (now - stream.ExcessSinceTimestamp >= shrinkDelay && !stream.LoadInFlight && !stream.Queued && shrinks < MaxShrinksPerSlice)
                    {
                        ReplaceStorage(stream, target);
                        stream.ExcessSinceTimestamp = 0;
                        shrinks++;
                    }
                }
                else
                {
                    stream.ExcessSinceTimestamp = 0;
                }
            }
        }

        // Uploads the new mips, first making storage for the level the chain heads to if it has none yet.
        // A texture's bits always arrive smallest first, and the base level only reveals the levels that
        // arrived, so nothing can ever sample as black.
        private static void HookUpMipBits(in LoadedMipBits bits)
        {
            var stream = bits.Stream;
            var texture = stream.Texture;

            if (texture.Handle == 0)
            {
                return; // deleted while the load was in flight; drop the data
            }

            Debug.Assert(bits.MipCount == 1 || stream.Mips[stream.NextMip] == bits.Mip);

            var lastMip = bits.MipCount == 1 ? bits.Mip : stream.Mips[stream.NextMip + bits.MipCount - 1];

            // Made once for the whole way to the target, instead of growing a level at a time: every growth
            // is a new allocation, and the driver does not hand the old ones back
            var storageLevel = Math.Min(lastMip.ChainLevel, stream.TargetChainLevel);

            if (storageLevel < stream.AllocatedChainLevel)
            {
                ReplaceStorage(stream, storageLevel);
            }

            var offset = 0;

            for (var i = 0; i < bits.MipCount; i++)
            {
                var mip = i == 0 ? bits.Mip : stream.Mips[stream.NextMip + i];

                SubImageMip(stream, mip.ChainLevel - stream.AllocatedChainLevel, mip, bits.Buffer, offset);
                offset += mip.BufferSize;
            }

            stream.ResidentChainLevel = lastMip.ChainLevel;
            UpdateBaseLevel(stream);
        }

        private static unsafe void SubImageMip(StreamedTexture stream, int level, in PlannedMip mip, byte[] buffer, int offset)
        {
            var texture = stream.Texture;

            fixed (byte* data = &buffer[offset])
            {
                if (stream.Format.IsBlockCompressed())
                {
                    if (stream.Is3D)
                    {
                        GL.CompressedTextureSubImage3D(texture.Handle, level, 0, 0, 0, mip.Width, mip.Height, mip.Depth, (PixelFormat)stream.SizedInternalFormat, mip.BufferSize, (IntPtr)data);
                    }
                    else
                    {
                        GL.CompressedTextureSubImage2D(texture.Handle, level, 0, 0, mip.Width, mip.Height, (PixelFormat)stream.SizedInternalFormat, mip.BufferSize, (IntPtr)data);
                    }
                }
                else
                {
                    if (stream.Is3D)
                    {
                        GL.TextureSubImage3D(texture.Handle, level, 0, 0, 0, mip.Width, mip.Height, mip.Depth, stream.Format.ToGLPixelFormat(), stream.Format.ToGLPixelType(), (IntPtr)data);
                    }
                    else
                    {
                        GL.TextureSubImage2D(texture.Handle, level, 0, 0, mip.Width, mip.Height, stream.Format.ToGLPixelFormat(), stream.Format.ToGLPixelType(), (IntPtr)data);
                    }
                }
            }
        }

        // Immutable storage cannot change size, so a different top level means a new texture object: allocate
        // one, copy the resident levels it keeps into it, and swap the handle over. Growing leaves the new
        // levels for the loads to fill, and shrinking drops the levels above the new top.
        private static void ReplaceStorage(StreamedTexture stream, int chainLevel)
        {
            var texture = stream.Texture;
            var target = texture.Target;
            var levels = stream.ChainLevels - chainLevel;
            var (width, height, depth) = MaterialLoader.GetChainLevelSize(target, stream.AllocWidth, stream.AllocHeight, stream.AllocDepth, chainLevel);

            var newHandle = GraphicsDevice.CreateTexture(target, stream.Name);
            MaterialLoader.CreateStorageForTarget(newHandle, target, levels, stream.SizedInternalFormat, width, height, depth);

            for (var c = Math.Max(stream.ResidentChainLevel, chainLevel); c < stream.ChainLevels; c++)
            {
                // For array and cube targets the copy's depth spans the layers; cubes count six faces
                var (levelWidth, levelHeight, levelDepth) = MaterialLoader.GetChainLevelSize(target, stream.AllocWidth, stream.AllocHeight, stream.AllocDepth, c);

                GL.CopyImageSubData(
                    texture.Handle, (ImageTarget)target, c - stream.AllocatedChainLevel, 0, 0, 0,
                    newHandle, (ImageTarget)target, c - chainLevel, 0, 0, 0,
                    levelWidth, levelHeight, levelDepth);
            }

            texture.ReplaceHandle(newHandle, levels);
            stream.AllocatedChainLevel = chainLevel;
            stream.ResidentChainLevel = Math.Max(stream.ResidentChainLevel, chainLevel);
            UpdateBaseLevel(stream);
        }

        // Samples only the levels whose data arrived
        private static void UpdateBaseLevel(StreamedTexture stream)
        {
            var levels = stream.ChainLevels - stream.AllocatedChainLevel;
            stream.Texture.SetBaseMaxLevel(stream.ResidentChainLevel - stream.AllocatedChainLevel, levels - 1);
        }

        /// <summary>
        /// Does one frame's worth of streaming: points textures at the resolution they need, starts the
        /// loads it can, and gives the GPU whatever mip data has arrived since last time. Call once per
        /// frame, after <see cref="GatherDemand"/>, on a thread with a GL context.
        /// </summary>
        /// <param name="frameTime">Length of the previous frame in seconds. This call gets 20% of it, minus
        /// what the previous call spent, so its own cost cannot grow its budget.</param>
        public void Timeslice(float frameTime = 1f / 60f)
        {
            var start = Stopwatch.GetTimestamp();
            var budgetSeconds = Math.Max(0f, frameTime * 0.2f - (float)lastSliceDuration / Stopwatch.Frequency);

            UpdateTargets(immediate: false);
            RunUntil(deadline: start + (long)(budgetSeconds * Stopwatch.Frequency));

            lastSliceDuration = Stopwatch.GetTimestamp() - start;
        }

        // The work itself, until the queues run dry or the deadline passes. Says whether it hooked anything up.
        private bool RunUntil(long deadline)
        {
            var hookedUp = false;

            // Start chains heading to a new target and restart chains parked by the throttle
            while (Interlocked.Read(ref bitsInFlight) < MaxBitsInFlight && pendingLoadRequests.TryDequeue(out var stream))
            {
                IssueLoadRequest(stream);
            }

            // Otherwise captures carry a permanent zero-cost debug group
            if (!loadedMipBits.IsEmpty)
            {
                using var _ = new GLDebugGroup("Texture Mip Uploads");

                while (loadedMipBits.TryDequeue(out var bits))
                {
                    Interlocked.Add(ref bitsInFlight, -bits.ByteSize);

                    var stream = bits.Stream;
                    stream.LoadInFlight = false;

                    // Texture deleted while the load was in flight; the chain ends here
                    if (stream.Texture.Handle == 0)
                    {
                        ArrayPool<byte>.Shared.Return(bits.Buffer);
                        continue;
                    }

                    HookUpMipBits(in bits);
                    hookedUp = true;

                    // The GL upload copies out of the buffer before returning, so it is dead here
                    ArrayPool<byte>.Shared.Return(bits.Buffer);

                    // Only a hookup moves a chain forward, which is what paces the whole pipeline
                    if (stream.ResidentChainLevel > stream.TargetChainLevel)
                    {
                        IssueLoadRequest(stream);
                    }

                    if (Stopwatch.GetTimestamp() >= deadline)
                    {
                        break;
                    }
                }
            }

            return hookedUp;
        }

        // Anything left to request, in flight, or waiting to be hooked up
        private bool HasPendingWork
            => !pendingLoadRequests.IsEmpty || !loadedMipBits.IsEmpty || Interlocked.Read(ref bitsInFlight) > 0;

        /// <summary>
        /// Blocks until every texture holds what it needs, to no budget. Must be called on a thread with a
        /// GL context, after <see cref="GatherDemand"/>.
        /// </summary>
        /// <param name="cancellationToken">Stops early, leaving chains unfinished.</param>
        public void FinishAllStreaming(CancellationToken cancellationToken = default)
        {
            var backoff = new SpinWait();

            UpdateTargets(immediate: true);

            while (HasPendingWork && !cancellationToken.IsCancellationRequested)
            {
                if (RunUntil(deadline: long.MaxValue))
                {
                    backoff = new SpinWait();
                }
                else
                {
                    backoff.SpinOnce();
                }
            }
        }

        // Asks for a stream's next mips toward its target, or parks it until the throttle lets up. Only ever
        // runs on the thread doing the slice, so reading the byte count cannot race the adds made right below it.
        private void IssueLoadRequest(StreamedTexture stream)
        {
            stream.Queued = false;

            // Already loading, finished inline by a non-streaming request, or gone
            if (stream.LoadInFlight || stream.Failed || stream.Texture.Handle == 0)
            {
                return;
            }

            if (Interlocked.Read(ref bitsInFlight) >= MaxBitsInFlight)
            {
                stream.Queued = true;
                pendingLoadRequests.Enqueue(stream);
                return;
            }

            PlanRequest(stream, RequestBudget);

            // Nothing left this side of the target, so there is no request to make
            if (stream.RequestMipCount == 0)
            {
                return;
            }

            // Counted from the request, not from the enqueue, so the throttle bounds rented buffers too
            Interlocked.Add(ref bitsInFlight, stream.GateBytes);

            stream.LoadInFlight = true;
            ThreadPool.UnsafeQueueUserWorkItem(stream, preferLocal: false);
        }

        // Decides how much of the chain the next request reads: from where the texture got to, toward the
        // target, taking mips while they fit the budget. Always at least one, or a chain whose next mip is
        // bigger than the budget would never move.
        private static void PlanRequest(StreamedTexture stream, int budget)
        {
            var count = 0;
            var bytes = 0;

            for (var i = stream.NextMip; i < stream.Mips.Length && stream.Mips[i].ChainLevel >= stream.TargetChainLevel; i++)
            {
                var size = stream.Mips[i].BufferSize;

                if (count > 0 && bytes + size > budget)
                {
                    break;
                }

                bytes += size;
                count++;
            }

            stream.RequestMipCount = count;
            stream.GateBytes = bytes;
        }

        // Finishes a chain on the spot, for a caller that needs the whole texture rather than the part
        // the meshes drawing it asked for, and keeps it whole from then on. Returns true if texture is whole.
        internal static bool FinishStreaming(RenderTexture tex)
        {
            if (tex.Stream is not { } stream)
            {
                return true;
            }

            stream.NeedsWholeChain = true;
            stream.TargetChainLevel = 0;

            if (stream.ResidentChainLevel == 0)
            {
                return true;
            }

            if (stream.LoadInFlight)
            {
                // The load in flight has bookkeeping this would race; the chain finishes through the slices
                return false;
            }

            while (stream.ResidentChainLevel > 0)
            {
                LoadAndHookUpMip(stream, stream.Mips[stream.NextMip]);
            }

            return true;
        }

        internal static void LoadAndHookUpMip(StreamedTexture stream, in PlannedMip mip)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(mip.BufferSize);

            try
            {
                lock (stream.Data)
                {
                    stream.Data.ReadTextureMipLevel(buffer.AsSpan(0, mip.BufferSize), stream.DataMipLevel(mip));
                }

                HookUpMipBits(new LoadedMipBits(stream, mip, MipCount: 1, mip.BufferSize, buffer));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Serves one load request, on a thread pool thread: reads the mips it was asked for into a single
        // pooled buffer, back to back, and queues them to be hooked up. The buffer holds exactly the mips: an
        // in-place decode would need a margin past each one, pushing power of two mips into the pool bucket
        // twice their size.
        internal void LoadStreamingData(StreamedTexture stream)
        {
            var first = stream.NextMip;
            var count = stream.RequestMipCount;
            var gateBytes = stream.GateBytes;

            // Deleted since the request was issued, so there is nothing to load into
            if (stream.Texture.Handle == 0)
            {
                Interlocked.Add(ref bitsInFlight, -gateBytes);
                stream.LoadInFlight = false;
                return;
            }

            // Owned here until the enqueue hands it over; any exit before that returns it
            byte[]? buffer = null;

            try
            {
                // The throttle counts the mips' exact sizes, which is all the buffer has to hold
                buffer = ArrayPool<byte>.Shared.Rent(gateBytes);

                var offset = 0;

                // The srgb and non-srgb variants of one resource share the Texture block and its reader
                lock (stream.Data)
                {
                    for (var i = 0; i < count; i++)
                    {
                        var mip = stream.Mips[first + i];

                        stream.Data.ReadTextureMipLevel(buffer.AsSpan(offset, mip.BufferSize), stream.DataMipLevel(mip));
                        offset += mip.BufferSize;
                    }
                }

                loadedMipBits.Enqueue(new LoadedMipBits(stream, stream.Mips[first], count, gateBytes, buffer));
                buffer = null; // handed over
            }
            catch (Exception e) // A corrupt or truncated vtex, or the package disposed under a load still in flight at teardown.
            {
                rendererContext.Logger.LogError(e, "Loading mips of {Texture} failed", stream.Name);
                Interlocked.Add(ref bitsInFlight, -gateBytes);
                stream.Failed = true;
                stream.LoadInFlight = false;
            }
            finally
            {
                if (buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
    }
}
