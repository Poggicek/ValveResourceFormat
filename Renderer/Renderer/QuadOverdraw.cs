using System.Diagnostics;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.OpenGL;

namespace ValveResourceFormat.Renderer;

/// <summary>
/// Quad overdraw debug visualization.
/// </summary>
/// 
/// <remarks>
/// The scene is rendered with a replacement shader that counts, per 2x2 pixel quad, how
/// many primitives were shaded into it, using an atomic lock image so a primitive spanning
/// several pixels of the same quad counts once.
///
/// The first render only fills out the depth buffer, the second one renders overdraw
/// </remarks>
public class QuadOverdraw(RendererContext rendererContext)
{
    /// <summary>
    /// Image unit for the lock image. Units 1 and 2 are used by the MSAA resolve and depth pyramid compute passes.
    /// </summary>
    public const int LockImageUnit = 3;

    /// <summary>
    /// Image unit for the count image. Units 1 and 2 are used by the MSAA resolve and depth pyramid compute passes.
    /// </summary>
    public const int CountImageUnit = 4;

    private Shader? sceneShader;
    private Shader? visualizeShader;

    private RenderTexture? quadLock;
    private RenderTexture? quadCount;
    private ClearBufferMask savedClearMask;
    private RenderState savedPassState;

    /// <summary>Gets the replacement shader that counts quad overdraw while the scene renders.</summary>
    public Shader SceneShader => sceneShader ??= rendererContext.ShaderLoader.LoadShader("quad_overdraw");

    /// <summary>Gets whether the current render mode has activated the quad overdraw visualization.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Loads the shaders up front so the render mode is registered for the mode dropdown.</summary>
    public void Load()
    {
        sceneShader ??= rendererContext.ShaderLoader.LoadShader("quad_overdraw");
        visualizeShader ??= rendererContext.ShaderLoader.LoadShader("visualize_quad_overdraw");
    }

    /// <summary>Updates <see cref="IsActive"/> based on the active render mode name.</summary>
    public void SetRenderMode(string renderMode)
    {
        IsActive = SceneShader.RenderModes.Contains(renderMode);
    }

    /// <summary>
    /// Sizes the count images to the framebuffer, resets them, binds them to their image
    /// units, and puts <see cref="SceneShader"/> in depth prime mode. Call before the first
    /// scene render of the frame.
    /// </summary>
    /// <param name="width">Framebuffer width in pixels.</param>
    /// <param name="height">Framebuffer height in pixels.</param>
    /// <param name="context">
    /// The pass being drawn, when one is available. Supplying it records the counter resets through
    /// <see cref="Scene.RenderContext.CommandList"/>; omitting it keeps the OpenGL path.
    /// </param>
    public void Prepare(int width, int height, Scene.RenderContext? context = null)
    {
        SceneShader.SetUniform1("bCountQuads", false);

        // one texel per 2x2 pixel quad
        var quadWidth = (width + 1) / 2;
        var quadHeight = (height + 1) / 2;

        if (quadLock == null || quadLock.Width != quadWidth || quadLock.Height != quadHeight)
        {
            // :QuadOverdrawResizeLifetime - these free their OpenGL objects immediately, while a frame
            // that sampled them may still be in flight. Deferring needs IDevice.DeferredDestroy, which
            // needs the textures to be owned by the device rather than wrapped non-owningly by
            // RenderTexture, so it has to wait for texture allocation to move onto IDevice.CreateTexture.
            quadLock?.Delete();
            quadCount?.Delete();

            // The RHI format overload records the format on the texture, which is what lets RhiTexture
            // describe it completely enough to be cleared and bound through the command list.
            quadLock = RenderTexture.Create(quadWidth, quadHeight, RhiFormat.R32_UInt);
            quadLock.SetLabel("QuadOverdrawLock");

            quadCount = RenderTexture.Create(quadWidth, quadHeight, RhiFormat.R32_UInt);
            quadCount.SetLabel("QuadOverdrawCount");
            quadCount.SetFiltering(TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        }

        var commandList = context?.CommandList;

        if (commandList != null)
        {
            // uint.MaxValue is the unlocked sentinel; no float clear colour can represent it, which is
            // why this is a raw-integer texture clear rather than a LoadOp.Clear attachment.
            commandList.ClearTexture(quadLock.RhiTexture, 0, uint.MaxValue);
            commandList.ClearTexture(quadCount!.RhiTexture, 0, 0u);
        }
        else
        {
            var unlocked = uint.MaxValue;
            var zero = 0u;
            GL.ClearTexImage(quadLock.Handle, 0, PixelFormat.RedInteger, PixelType.UnsignedInt, ref unlocked);
            GL.ClearTexImage(quadCount!.Handle, 0, PixelFormat.RedInteger, PixelType.UnsignedInt, ref zero);
        }

        GL.BindImageTexture(LockImageUnit, quadLock.Handle, 0, false, 0, TextureAccess.ReadWrite, SizedInternalFormat.R32ui);
        GL.BindImageTexture(CountImageUnit, quadCount.Handle, 0, false, 0, TextureAccess.ReadWrite, SizedInternalFormat.R32ui);
    }

    /// <summary>
    /// Switches from the depth prime render to the counting render by enabling counting in <see cref="SceneShader"/>.
    /// </summary>
    /// <param name="framebuffer">The framebuffer the scene renders into.</param>
    public void BeginCountingPass(Framebuffer framebuffer)
    {
        savedClearMask = framebuffer.ClearMask;
        framebuffer.ClearMask &= ~ClearBufferMask.DepthBufferBit;

        savedPassState = rendererContext.RenderState.CurrentPass;
        var countingState = savedPassState;
        countingState.DepthStencil.DepthFunc = Comparison.CloserEqual;
        rendererContext.RenderState.ApplyAsPassBaseline(in countingState);

        SceneShader.SetUniform1("bCountQuads", true);
    }

    /// <summary>Restores the depth state changed by <see cref="BeginCountingPass"/>.</summary>
    /// <param name="framebuffer">The framebuffer passed to <see cref="BeginCountingPass"/>.</param>
    public void EndCountingPass(Framebuffer framebuffer)
    {
        framebuffer.ClearMask = savedClearMask;
        rendererContext.RenderState.ApplyAsPassBaseline(in savedPassState);
    }

    /// <summary>
    /// Replaces the bound framebuffer contents with the overdraw heat map and legend.
    /// Call after the scene rendered with <see cref="SceneShader"/> as the replacement shader.
    /// </summary>
    /// <param name="context">
    /// The pass being drawn, when one is available. Supplying it records the barrier that publishes the
    /// counter image to the sampling pass; omitting it keeps the OpenGL path.
    /// </param>
    public void Render(Scene.RenderContext? context = null)
    {
        Debug.Assert(quadCount != null, $"{nameof(Prepare)} must be called before {nameof(Render)}");

        visualizeShader ??= rendererContext.ShaderLoader.LoadShader("visualize_quad_overdraw");

        using var _ = new GLDebugGroup("Quad Overdraw Visualization");

        var commandList = context?.CommandList;

        // The counts were written as image stores, the fullscreen pass samples them.
        if (commandList != null)
        {
            commandList.Barrier(new TextureBarrier(quadCount.RhiTexture, ResourceState.ShaderWrite, ResourceState.ShaderRead));
        }
        else
        {
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
        }

        visualizeShader.Use();
        visualizeShader.SetTexture(0, "g_tQuadOverdraw", quadCount);

        using (rendererContext.RenderState.Scope(depthTest: false, depthWrite: false))
        {
            // A fullscreen triangle generated from the vertex index: no vertex buffer, so the pipeline
            // takes VertexInputDesc.Empty. Three vertices of one triangle, so TriangleList.
            if (commandList != null)
            {
                var framebuffer = context!.Value.Framebuffer;
                var device = (GLRendererDevice)commandList.Device;

                var pipeline = device.GetOrCreatePipeline(
                    visualizeShader,
                    rendererContext.RenderState.CurrentPass,
                    VertexInputDesc.Empty,
                    PrimitiveTopology.TriangleList,
                    framebuffer.Color is { } color ? [color.RhiFormat] : [],
                    framebuffer.Depth?.RhiFormat ?? RhiFormat.Undefined,
                    Math.Max(1, framebuffer.NumSamples),
                    GLRendererDevice.DrawConstants);

                commandList.BindPipeline(pipeline);
                commandList.Draw(3);
            }
            else
            {
                GL.BindVertexArray(rendererContext.MeshBufferCache.EmptyVAO);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            }
        }
    }

    /// <summary>Releases the GPU textures owned by this visualization.</summary>
    public void Dispose()
    {
        quadLock?.Delete();
        quadCount?.Delete();
        quadLock = null;
        quadCount = null;
    }
}
