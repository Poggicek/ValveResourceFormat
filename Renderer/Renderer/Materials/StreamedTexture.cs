using System.Threading;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.ResourceTypes;

namespace ValveResourceFormat.Renderer.Materials
{
    /// <summary>
    /// Bookkeeping for one streamed texture, doubling as the thread pool work item for its mip reads.
    /// Chain level 0 is the biggest mip of the (possibly user-capped) chain. Mips load strictly smallest
    /// to largest with one read in flight per texture: the next read is dispatched only once the previous
    /// mip's upload has been applied. Reads may one day run a couple of mips ahead, but must stay
    /// sequential: the reveal assumes uploads arrive in chain order.
    /// </summary>
    sealed class StreamedTexture : IThreadPoolWorkItem
    {
        public required TextureStreamingHelper Streaming { get; init; }
        public required string Name { get; init; }
        public required RenderTexture Texture { get; init; }
        public required Texture Data { get; init; }
        public required ImageFormat Format { get; init; }
        public required SizedInternalFormat SizedInternalFormat { get; init; }
        public required bool Is3D { get; init; }
        public required int MinMipLevelAllowed { get; init; }

        /// <summary>Number of levels in the full (possibly user-capped) chain.</summary>
        public int ChainLevels => Mips.Length + 1;

        /// <summary>Dimensions of chain level 0, after the user's size cap. Depth carries the layer
        /// (times face) count for array and cube targets, and the spatial depth for volumes.</summary>
        public required int AllocWidth { get; init; }
        public required int AllocHeight { get; init; }
        public required int AllocDepth { get; init; }

        /// <summary>Chain level held by level 0 of the current storage. Storage is made once for the
        /// level the chain heads to, so it can reach past <see cref="ResidentChainLevel"/> while the mips
        /// in between are still loading.</summary>
        public int AllocatedChainLevel;

        /// <summary>Biggest chain level whose data has arrived: everything from here down is resident,
        /// and the texture's base level keeps sampling to it.</summary>
        public int ResidentChainLevel;

        /// <summary>Chain level the loads head to, or the storage shrinks to, as last decided.</summary>
        public int TargetChainLevel;

        /// <summary>The mips past the smallest one, ordered smallest to largest. The chain's smallest mip
        /// uploads synchronously at load so the texture is never sampleable while empty, and is not planned here.</summary>
        public required PlannedMip[] Mips { get; init; }

        /// <summary>Index into <see cref="Mips"/> of the mip to read next, the one above the resident ones.
        /// Moves as the pump thread applies uploads, and read back by the worker the pump then dispatches.</summary>
        public int NextMip => MipIndex(ResidentChainLevel - 1);

        /// <summary>How many mips the issued request reads.</summary>
        public int RequestMipCount;
        public int GateBytes;

        /// <summary>Set while a read is out, from its request until its data is applied or dropped. Only an
        /// idle texture can have its storage replaced outside the upload path.</summary>
        public volatile bool LoadInFlight;

        /// <summary>Set while the texture waits in the request queue, so it is queued once.</summary>
        public bool Queued;

        /// <summary>Set when a read failed, which ends streaming for the texture.</summary>
        public volatile bool Failed;

        /// <summary>Set for a texture something other than a mesh draws, which streams the whole chain.</summary>
        public bool NeedsWholeChain;

        /// <summary>Finest chain level any mesh needed in demand pass <see cref="DemandPass"/>.</summary>
        public int DemandChainLevel;

        /// <summary>The last demand pass that measured this texture, 0 before any did.</summary>
        public int DemandPass;

        /// <summary>When streaming began, for textures no demand pass measures.</summary>
        public long RegisteredTimestamp;

        /// <summary>Since when more is resident than the target wants, 0 while it is not.</summary>
        public long ExcessSinceTimestamp;

        /// <summary>The mip's level in the source texture data, past the user's size cap.</summary>
        public uint DataMipLevel(in PlannedMip mip) => (uint)(mip.ChainLevel + MinMipLevelAllowed);

        /// <summary>Index into <see cref="Mips"/> of the mip at a chain level.</summary>
        public int MipIndex(int chainLevel) => ChainLevels - 2 - chainLevel;

        public void Execute() => Streaming.LoadStreamingData(this);
    }

    /// <summary>One mip level of a <see cref="StreamedTexture"/> waiting to be read.</summary>
    readonly record struct PlannedMip(int ChainLevel, int Width, int Height, int Depth, int BufferSize);

    /// <summary>Loaded mip bits waiting for <see cref="TextureStreamingHelper"/> to hook them up on the
    /// render thread: one mip, or a texture's whole remaining chain packed into one buffer.</summary>
    /// <param name="Stream">The texture the data belongs to.</param>
    /// <param name="Mip">The first (smallest) mip in the buffer.</param>
    /// <param name="MipCount">Number of consecutive planned mips the buffer holds, starting at <paramref name="Mip"/>.</param>
    /// <param name="ByteSize">The amount this item holds of the in-flight throttle, unwound when it is consumed.</param>
    /// <param name="Buffer">Pooled buffer with its mips back to back, in plan order.</param>
    readonly record struct LoadedMipBits(StreamedTexture Stream, PlannedMip Mip, int MipCount, int ByteSize, byte[] Buffer);
}
