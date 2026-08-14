using System.Diagnostics;
using ValveResourceFormat.Renderer.RHI;

namespace ValveResourceFormat.Renderer.PostProcess;

/// <summary>
/// Fullscreen pass that draws an outline using stencil edge detection.
/// </summary>
public class OutlineRenderer(RendererContext rendererContext)
{
    private Shader? outlineEdge;

    /// <summary>Loads the outline edge detection shader.</summary>
    public void Load()
    {
        outlineEdge = rendererContext.ShaderLoader.LoadShader("outline_post");
    }

    /// <summary>
    /// Execute the outline post-pass.
    /// </summary>
    /// <param name="stencil">The scene's stencil buffer, whose edges the outline is drawn along.</param>
    /// <param name="numSamples">The scene framebuffer's sample count.</param>
    /// <param name="flipY">Whether the image is flipped vertically on the way out.</param>
    /// <param name="target">The framebuffer to composite the outline onto. Only needed when recording;
    /// the OpenGL path draws into whichever framebuffer the caller left bound.</param>
    /// <param name="commandList">The list to record into, or <see langword="null"/> to run through OpenGL.</param>
    /// <remarks>The outline blends over the tonemapped image rather than replacing it, so its pass loads
    /// what is already there. The blend state has to be applied before the pass opens, because that is
    /// where the pipeline reads it from.</remarks>
    public void Render(RenderTexture stencil, int numSamples, bool flipY, Framebuffer? target = null,
        ICommandList? commandList = null)
    {
        Debug.Assert(outlineEdge != null);

        outlineEdge.Use();

        outlineEdge.SetUniform("g_bFlipY", flipY);
        outlineEdge.SetUniform("g_nNumSamplesMSAA", numSamples);

        PostProcessRenderer.BindTexture(commandList, outlineEdge, 0, "g_tStencilBuffer", stencil);

        using var _ = rendererContext.RenderState.Scope(blend: true, srcBlend: BlendFactor.SrcAlpha, dstBlend: BlendFactor.OneMinusSrcAlpha);

        // A pass needs somewhere to render into, so a caller that supplies no target keeps the OpenGL path
        // and its "whatever is bound" contract.
        var recording = target == null ? null : commandList;

        using var pass = PostProcessRenderer.BeginPass(recording, target!, "Outline Edge");

        PostProcessRenderer.DrawFullscreenTriangle(recording, rendererContext, outlineEdge, target);
    }
}
