using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.Buffers;
using ValveResourceFormat.Renderer.Shaders;

namespace ValveResourceFormat.Renderer;

/// <summary>
/// Per scene owner of the tile and depth bin cull passes: the item layout, the GPU buffers they read and
/// write, and the constants that tell the shading pass where to look.
/// </summary>
/// <remarks>
/// One per <see cref="Scene"/>, because the bit indices it produces are positions in that scene's barn
/// light and env map arrays. The 3D skybox has its own arrays, so its bits mean nothing against these.
/// </remarks>
public sealed class LightBinner(Scene scene) : IDisposable
{
    /// <summary>Screen tile size as a power of two shift. 4 is 16x16 pixels.</summary>
    private const int TileShift = 4;

    /// <summary>
    /// Number of depth slices items are binned into, uniform in view depth. Must be a multiple of the
    /// depth bin pass's group size, which dispatches exactly and does not bound check.
    /// </summary>
    private const int DepthSliceCount = 32; // todo: experiment with 64 and higher.

    /// <summary>
    /// Floor for the fitted slice far, in world units. A scene with nothing binned still needs a range
    /// wide enough to be worth distributing.
    /// </summary>
    private const float MinSliceFar = 256f;

    /// <summary>Ceiling for the fitted slice far, in world units.</summary>
    private const float MaxSliceFar = 32768f;

    private readonly TiledCullFeeder Feeder = new();

    private Shader? TileCullBitsShader;
    private Shader? DepthBinCullBitsShader;

    private StorageBuffer? CullItemsGpu;
    private StorageBuffer? CullPlanesGpu;
    private UniformBuffer<CullParams>? CullParamsGpu;
    private UniformBuffer<LightCullConstants>? ConstantsGpu;

    private readonly LightCullConstants Constants = new();

    private int TileCols;
    private int TileRows;
    private int CullBitsWords;
    private bool CullBitsAllVisible;
    private bool Active;

    /// <summary>Gets the buffer holding this scene's per tile and per depth bin masks.</summary>
    public StorageBuffer? CullBits { get; private set; }

    private bool CanCull => (scene.LightingInfo.LightingData.NumBarnLights > 0 || scene.LightingInfo.EnvMaps.Count > 0)
        && TileCullBitsShader != null
        && DepthBinCullBitsShader != null;

    /// <summary>Loads the two compute shaders. Call once the GL context exists.</summary>
    public void LoadShaders()
    {
        TileCullBitsShader = scene.RendererContext.ShaderLoader.LoadShader("compute_tile_cullbits");
        DepthBinCullBitsShader = scene.RendererContext.ShaderLoader.LoadShader("compute_depthbin_cullbits");
    }

    /// <summary>
    /// Points this pass's pixels at the ones the masks were built for. The viewmodel draws into the same
    /// target through a different projection, so it cannot take its tile from gl_FragCoord directly.
    /// </summary>
    /// <param name="remap">Pixel remap: xy scale, zw bias.</param>
    public void SetPixelRemap(Vector4 remap)
    {
        Constants.LightCullPixelRemap = remap;

        if (ConstantsGpu != null)
        {
            ConstantsGpu.Data = Constants;
        }
    }

    /// <summary>What this scene's binner produced for the frame, for the render stats display.</summary>
    /// <param name="Active">Whether items were binned, as opposed to every mask being filled with ones.</param>
    /// <param name="Faces">Barn light faces that reached a tile.</param>
    /// <param name="FaceSlots">Barn light faces the shading pass iterates.</param>
    /// <param name="Probes">Env map probes that reached a tile.</param>
    /// <param name="ProbeSlots">Env map probes the shading pass iterates.</param>
    /// <param name="HullVertices">Silhouette vertices the tile pass walks across every item.</param>
    /// <param name="MaskBytes">Size of the mask buffer for this layout.</param>
    /// <param name="SliceFar">Far distance the slice distribution was fitted to.</param>
    /// <param name="SliceWidth">View depth one slice spans, in world units.</param>
    internal readonly record struct BinnerStats(
        bool Active, int Faces, int FaceSlots, int Probes, int ProbeSlots, int HullVertices, int MaskBytes,
        float SliceFar, float SliceWidth);

    /// <summary>Gets what this binner produced for the frame.</summary>
    internal BinnerStats Stats => new(
        Active,
        Feeder.BinnedCount(TiledCullFeeder.BatchBarnLights), Feeder.SlotCount(TiledCullFeeder.BatchBarnLights),
        Feeder.BinnedCount(TiledCullFeeder.BatchEnvMaps), Feeder.SlotCount(TiledCullFeeder.BatchEnvMaps),
        Feeder.PlaneCount,
        Feeder.TotalWords * sizeof(uint),
        Feeder.SliceFar,
        Feeder.SliceWidth);

    /// <summary>Gets the first word and stride of a batch's tile region, for the debug overlay.</summary>
    /// <param name="envMaps">Whether to describe the env map batch rather than the barn light one.</param>
    public (uint TileBase, uint Words) GetOverlayRegion(bool envMaps) => envMaps
        ? (Constants.EnvMapTileBase, Constants.EnvMapCullWords)
        : (Constants.LightTileBase, Constants.LightCullWords);

    /// <summary>Binds this scene's masks and their layout for the shading pass.</summary>
    /// <param name="commandList">The command list to record into, or <see langword="null"/> to bind through OpenGL directly.</param>
    /// <remarks>
    /// The buffers are ensured here rather than only in <see cref="Update"/> because a bind can precede
    /// the first update within a frame: the sun shadow pass binds the scene-wide buffers before
    /// <c>UpdatePerViewGpuBuffers</c> runs, and a shadow caster drawn with its own material shader
    /// declares <c>LightCullConstants</c> there. Leaving the buffer uncreated made that a descriptor the
    /// draw reads and nothing wrote. The layout they are created with is the empty one -- a single
    /// all-ones mask word -- which is what <see cref="Dispatch"/> would publish for a scene with nothing
    /// binned anyway, and the first <see cref="Update"/> resizes it to the real one.
    /// </remarks>
    public void Bind(RHI.ICommandList? commandList)
    {
        EnsureBuffers();

        if (CullBits != null)
        {
            Scene.BindStorageBuffer(commandList, CullBits);
        }

        if (ConstantsGpu != null)
        {
            Scene.BindUniformBuffer(commandList, ConstantsGpu);
            ConstantsGpu.Update();
        }
    }

    /// <summary>
    /// Projects every cull item for this frame and publishes the resulting layout. Must run before the
    /// view buffer upload preceding <see cref="Dispatch"/>, since the shading pass reads the layout.
    /// </summary>
    /// <param name="viewConstants">View the items are projected against.</param>
    /// <param name="viewportWidth">Viewport width in pixels.</param>
    /// <param name="viewportHeight">Viewport height in pixels.</param>
    /// <param name="enabled">Whether the caller wants binning this frame.</param>
    public void Update(ViewConstants viewConstants, int viewportWidth, int viewportHeight, bool enabled)
    {
        Active = enabled && CanCull && viewportWidth > 0 && viewportHeight > 0;

        const int tileSize = 1 << TileShift;

        var width = Math.Max(viewportWidth, 1);
        var height = Math.Max(viewportHeight, 1);

        TileCols = (width + tileSize - 1) >> TileShift;
        TileRows = (height + tileSize - 1) >> TileShift;

        Feeder.Begin(
            TileCols, TileRows, tileSize,
            DepthSliceCount, MinSliceFar, MaxSliceFar,
            new Vector2(width, height),
            viewConstants.WorldToProjection,
            viewConstants.CameraPosition, viewConstants.CameraDirWs,
            viewConstants.NearPlane);

        if (Active)
        {
            Feeder.AddBarnLights(scene.LightingInfo.BinnedBarnLightVolumes);
            Feeder.AddEnvMaps(scene.LightingInfo.EnvMaps);
        }
        else
        {
            // Nothing will read a projected hull this frame, so only claim the slots. The layout still has
            // to be right: the shading pass indexes an all ones buffer through these same bases and strides.
            // Capped to the shader array: the scene list can hold probes past it, which CalculateEnvironmentMaps
            // logs and never assigns a shader index to, and iterating them would read off the end of the UBO.
            Feeder.AddCounts(
                scene.LightingInfo.BinnedBarnLightVolumes.Length,
                Math.Min(scene.LightingInfo.EnvMaps.Count, EnvMapArray.MAX_ENVMAPS));
        }

        Feeder.End();

        EnsureBuffers();

        Constants.LightTileBase = Feeder.TileBase(TiledCullFeeder.BatchBarnLights);
        Constants.LightSliceBase = Feeder.BinBase(TiledCullFeeder.BatchBarnLights);
        Constants.LightCullWords = Feeder.Stride(TiledCullFeeder.BatchBarnLights);
        Constants.LightTileShift = TileShift;
        Constants.LightTileCols = (uint)TileCols;
        Constants.LightTileRows = (uint)TileRows;
        Constants.LightSliceCount = DepthSliceCount;

        // Read after End, which fits the range. Slices per world unit, then the last slice index.
        Constants.LightDepthSliceParams = new Vector4(
            DepthSliceCount / Feeder.SliceFar,
            DepthSliceCount - 1,
            0f, 0f);

        Constants.EnvMapTileBase = Feeder.TileBase(TiledCullFeeder.BatchEnvMaps);
        Constants.EnvMapBinBase = Feeder.BinBase(TiledCullFeeder.BatchEnvMaps);
        Constants.EnvMapCullWords = Feeder.Stride(TiledCullFeeder.BatchEnvMaps);
        Constants.EnvMapCount = (uint)Feeder.SlotCount(TiledCullFeeder.BatchEnvMaps);

        Constants.LightCullCameraPosition = viewConstants.CameraPosition;
        Constants.LightCullCameraDir = viewConstants.CameraDirWs;

        Constants.LightCullPixelRemap = ViewConstants.PixelRemapIdentity;

        Debug.Assert(ConstantsGpu is not null);
        ConstantsGpu.Data = Constants;
    }

    /// <summary>
    /// Rasterizes the items <see cref="Update"/> projected into per tile and per depth bin masks, so the
    /// shading pass iterates only what reaches a given fragment.
    /// </summary>
    public void Dispatch()
    {
        if (CullBits == null)
        {
            return;
        }

        if (!Active || Feeder.MaskCount == 0)
        {
            if (!CullBitsAllVisible)
            {
                CullBits.Fill(uint.MaxValue);
                CullBitsAllVisible = true;
            }

            return;
        }

        CullBitsAllVisible = false;

        Debug.Assert(TileCullBitsShader is not null && DepthBinCullBitsShader is not null);
        Debug.Assert(CullItemsGpu is not null && CullPlanesGpu is not null && CullParamsGpu is not null);

        CullItemsGpu.Update(Feeder.ItemArray, 0, Feeder.ItemCount * Unsafe.SizeOf<CullItem>());
        CullPlanesGpu.Update(Feeder.PlaneArray, 0, Feeder.PlaneCount * Unsafe.SizeOf<Vector2>());
        CullParamsGpu.Data = Feeder.Params;

        CullBits.BindBufferBase();
        CullItemsGpu.BindBufferBase();
        CullPlanesGpu.BindBufferBase();
        CullParamsGpu.BindBufferBase();

        var (tileX, tileY, tileZ) = Feeder.TileDispatch;
        TileCullBitsShader.Use();
        SetOcclusionUniforms(TileCullBitsShader);
        GL.DispatchCompute(tileX, tileY, tileZ);

        var (binX, binY, binZ) = Feeder.BinDispatch;
        DepthBinCullBitsShader.Use();
        GL.DispatchCompute(binX, binY, binZ);

        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
    }

    /// <summary>
    /// Grows the GPU buffers to fit the layout the feeder just produced. The bits buffer is sized from
    /// that layout rather than a worst case, so a scene with few items pays for few words.
    /// </summary>
    private void EnsureBuffers()
    {
        CullParamsGpu ??= new UniformBuffer<CullParams>(ReservedBufferSlots.CullParams);
        ConstantsGpu ??= new UniformBuffer<LightCullConstants>(ReservedBufferSlots.LightCull);

        CullItemsGpu ??= StorageBuffer.Allocate<CullItem>(
            ReservedBufferSlots.CullItems, Feeder.ItemArray.Length, BufferUsageHint.DynamicDraw);

        CullPlanesGpu ??= StorageBuffer.Allocate<Vector2>(
            ReservedBufferSlots.CullPlanes, Feeder.PlaneArray.Length, BufferUsageHint.DynamicDraw);

        if (CullBits == null || CullBitsWords < Feeder.TotalWords)
        {
            CullBits?.Delete();

            // Never zero. A scene with no barn lights and no env maps lays out no words at all: both
            // batches round to zero masks, so every stride is zero and the feeder's cursor never advances.
            // An empty allocation is not bindable through the RHI - glBindBufferRange rejects a zero size
            // where glBindBufferBase accepts it, and vkCreateBuffer rejects one outright - so the
            // degenerate case is removed here rather than special-cased at every bind. Nothing reads the
            // spare word: with no items, both batches publish a stride of zero and the shading pass
            // iterates nothing.
            //
            // Defensive rather than demonstrated: every golden scene carries at least one env map, so the
            // suite never lays out zero words. CalculateEnvironmentMaps returning early on an empty list
            // is what makes it reachable off the suite. Scene.BindStorageBuffer asserts on the case.
            CullBitsWords = Math.Max(1, Feeder.TotalWords);
            CullBits = StorageBuffer.Allocate<uint>(
                ReservedBufferSlots.CullBits, CullBitsWords, BufferUsageHint.DynamicDraw);

            // A fresh allocation holds nothing in particular, and every zero bit reads as an item culled.
            // Start visible instead: a pass that never reaches Dispatch - a viewer holding a locked cull
            // frustum for the whole session - then shades against every light and probe rather than none.
            CullBits.Fill(uint.MaxValue);
            CullBitsAllVisible = true;
        }
    }

    /// <summary>
    /// Feeds the depth pyramid to the occlusion test in <c>compute_tile_cullbits</c>. The test sits behind
    /// a shader constant, but these are set regardless so toggling it needs no CPU change.
    /// </summary>
    private void SetOcclusionUniforms(Shader shader)
    {
        if (!scene.SetOcclusionUniforms(shader))
        {
            return;
        }

        Debug.Assert(scene.DepthPyramid != null);

        // Cull space is pixels, so this is just the pyramid's size over the viewport's. Only this pass
        // needs it, because only this pass starts from a screen rect rather than a world space box.
        shader.SetUniform("g_vCullToPyramidScale", new Vector2(
            scene.DepthPyramid.Width / MathF.Max(Feeder.ViewportSize.X, 1f),
            scene.DepthPyramid.Height / MathF.Max(Feeder.ViewportSize.Y, 1f)));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        CullBits?.Delete();
        CullItemsGpu?.Delete();
        CullPlanesGpu?.Delete();
        CullParamsGpu?.Dispose();
        ConstantsGpu?.Dispose();

        CullBits = null;
        CullItemsGpu = null;
        CullPlanesGpu = null;
        CullParamsGpu = null;
        ConstantsGpu = null;
    }
}
