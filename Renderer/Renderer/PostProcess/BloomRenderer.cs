using System.Diagnostics;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.RHI;
using Vector2i = OpenTK.Mathematics.Vector2i;

namespace ValveResourceFormat.Renderer.PostProcess;

/// <summary>
/// Post-processing renderer that creates bloom effects using multi-pass Gaussian blur.
/// </summary>
public class BloomRenderer
{
    private Shader? firstDownsampleBloomThreshold;
    private Shader? downsample;
    private Shader? horizontalBlur;
    private Shader? verticalBlur;
    private Shader? firstUpsample;
    private Shader? upsample;

    private Framebuffer? Ping;
    private Framebuffer? Pong;
    private Framebuffer? Accumulation;

    /// <summary>Gets the texture containing the final composited bloom result after the upsample passes.</summary>
    public RenderTexture? AccumulationResult => Accumulation!.Color!;

    /// <summary>Number of mip levels in the bloom accumulation buffer.</summary>
    public const int BloomMipCount = 4;
    private readonly RendererContext RendererContext;
    private readonly PostProcessRenderer PostProcessRenderer;

    /// <summary>
    /// Initializes a new <see cref="BloomRenderer"/> using the given renderer context and post-process renderer.
    /// </summary>
    /// <param name="rendererContext">The renderer context providing shader loading and mesh buffer access.</param>
    /// <param name="postProcessRenderer">The owning post-process renderer, used for shared state and format.</param>
    public BloomRenderer(RendererContext rendererContext, PostProcessRenderer postProcessRenderer)
    {
        RendererContext = rendererContext;
        PostProcessRenderer = postProcessRenderer;
    }

    /// <summary>Loads bloom shaders and allocates ping-pong and accumulation framebuffers.</summary>
    public void Load()
    {
        firstDownsampleBloomThreshold = RendererContext.ShaderLoader.LoadShader("downsample_bloomthreshold");
        downsample = RendererContext.ShaderLoader.LoadShader("gaussian_bloom_blur");
        horizontalBlur = RendererContext.ShaderLoader.LoadShader("gaussian_bloom_blur", ("D_BLUR_PASS", 1), ("D_BLUR_PASS_HORIZONTAL", 1));
        verticalBlur = RendererContext.ShaderLoader.LoadShader("gaussian_bloom_blur", ("D_BLUR_PASS", 1), ("D_BLUR_PASS_HORIZONTAL", 0));
        upsample = RendererContext.ShaderLoader.LoadShader("gaussian_bloom_blur", ("D_BLUR_PASS", 2));
        firstUpsample = RendererContext.ShaderLoader.LoadShader("gaussian_bloom_blur", ("D_BLUR_PASS", 3));

        Ping = CreateFramebuffer("BloomPing");
        Pong = CreateFramebuffer("BloomPong");
        Accumulation = CreateFramebuffer("BloomAccumulation", BloomMipCount);
    }

    private static Framebuffer CreateFramebuffer(string name, int mips = 1)
    {
        var framebuffer = Framebuffer.Prepare(name, 4, 4, 0, PostProcessRenderer.DefaultColorFormat, null);
        framebuffer.NumMips = mips;
        framebuffer.Initialize();
        framebuffer.CheckStatus_ThrowIfIncomplete();
        return framebuffer;
    }

    /// <summary>
    /// Renders multi-pass bloom from the resolved scene color into <see cref="AccumulationResult"/>.
    /// </summary>
    /// <param name="input">The resolved scene colour the threshold pass reads.</param>
    /// <param name="commandList">The list to record into, or <see langword="null"/> to run through OpenGL.</param>
    public void Render(RenderTexture input, ICommandList? commandList = null)
    {
        Debug.Assert(firstDownsampleBloomThreshold != null);
        Debug.Assert(downsample != null);
        Debug.Assert(horizontalBlur != null);
        Debug.Assert(verticalBlur != null);
        Debug.Assert(upsample != null);
        Debug.Assert(firstUpsample != null);

        Debug.Assert(Accumulation != null && Accumulation.Color != null);
        Debug.Assert(Ping != null && Ping.Color != null);
        Debug.Assert(Pong != null && Pong.Color != null);

        Vector2i maxBloomRes = new(input.Width, input.Height);

        // Start at 1/4 resolution
        maxBloomRes /= 4;

        static bool InvalidSize(Vector2i size) => size.X < 16 || size.Y < 16;

        // skip bloom if the resolution is too small
        if (InvalidSize(maxBloomRes))
        {
            if (commandList == null)
            {
                Accumulation.BindAndClear();
            }
            else
            {
                // A pass that only clears: nothing is drawn, but the tonemap still samples the result.
                using var clearPass = PostProcessRenderer.BeginPass(commandList, Accumulation, "Bloom Clear", clear: true);
            }

            return;
        }

        var settings = PostProcessRenderer.State.BloomSettings;
        var tonemapScalar = PostProcessRenderer.TonemapScalar;

        if (Accumulation.Resize(maxBloomRes.X, maxBloomRes.Y))
        {
            Accumulation.Color.SetFiltering(TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear);
            Accumulation.Color.SetWrapMode(TextureWrapMode.ClampToEdge);
        }

        if (Ping.Resize(maxBloomRes.X, maxBloomRes.Y))
        {
            Ping.Color.SetFiltering(TextureMinFilter.Linear, TextureMagFilter.Linear);
            Ping.Color.SetWrapMode(TextureWrapMode.ClampToEdge);
        }

        if (Pong.Resize(maxBloomRes.X, maxBloomRes.Y))
        {
            Pong.Color.SetFiltering(TextureMinFilter.Linear, TextureMagFilter.Linear);
            Pong.Color.SetWrapMode(TextureWrapMode.ClampToEdge);
        }

        using (new GLDebugGroup("Bloom Downsample Threshold Pass"))
        {
            Debug.Assert(input.Target == TextureTarget.Texture2D);

            firstDownsampleBloomThreshold.Use(commandList);
            PostProcessRenderer.BindTexture(commandList, firstDownsampleBloomThreshold, 0, "inputTexture", input);

            if (commandList == null)
            {
                Ping.Bind(FramebufferTarget.DrawFramebuffer);
                GL.Viewport(0, 0, Ping.Width, Ping.Height);
            }

            using var pass = PostProcessRenderer.BeginPass(commandList, Ping, "Bloom Downsample Threshold Pass");

            var thresholdParams = new Vector2(settings.BloomThreshold / settings.BloomThresholdWidth * -1, 1 / settings.BloomThresholdWidth);
            firstDownsampleBloomThreshold.SetUniform("g_flBloomScale", settings.BloomStrength);
            firstDownsampleBloomThreshold.SetUniform("g_flToneMapScalarLinear", tonemapScalar);
            firstDownsampleBloomThreshold.SetUniform("g_flThresholdParams", thresholdParams);

            PostProcessRenderer.DrawFullscreenTriangle(commandList, RendererContext, firstDownsampleBloomThreshold, Ping);
        }

        var lastWrittenMip = 0;
        var downsampledSize = maxBloomRes;

        for (var i = 0; i < BloomMipCount; i++)
        {
            using var _ = new GLDebugGroup(i switch { 0 => "Bloom Accumulation 0", 1 => "Bloom Accumulation 1", 2 => "Bloom Accumulation 2", 3 => "Bloom Accumulation 3", _ => "Bloom Accumulation" });

            if (InvalidSize(downsampledSize))
            {
                break;
            }

            if (i != 0)
            {
                if (commandList == null)
                {
                    Ping.BindAndClear();
                    Pong.BindAndClear();
                }

                // cheap downsample from previous mip
                // Accumulation.AttachColorMipLevel(i - 1);
                //
                // The clear is load bearing on this one: the downsample draws into a viewport smaller than
                // Ping, and the horizontal blur below reads all of it. Pong's clear folds into its own pass,
                // which does cover every texel.
                RenderTexture(commandList, downsample, Accumulation, Ping, downsampledSize, i - 1, clear: true);
            }

            // blur horizontal
            RenderTexture(commandList, horizontalBlur, Ping, Pong, maxBloomRes, clear: i != 0);

            // blur vertical
            RenderTexture(commandList, verticalBlur, Pong, Ping, maxBloomRes);

            // write to bloom accumulation buffer
            Accumulation.AttachColorMipLevel(i);
            RenderTexture(commandList, downsample, Ping, Accumulation, maxBloomRes, i, destMipLevel: i);

            lastWrittenMip = i;
            downsampledSize /= 2;
        }

        // loop through mips backwards, from lowest res to highest
        for (var i = lastWrittenMip; i >= 1; i--)
        {
            using var _ = new GLDebugGroup(i switch { 1 => "Bloom Upsample 1", 2 => "Bloom Upsample 2", 3 => "Bloom Upsample 3", 4 => "Bloom Upsample 4", _ => "Bloom Upsample" });
            var isFirstUpsample = i == lastWrittenMip;
            var isLastUpsample = i == 1;

            var currentMipSize = Accumulation.GetMipSize(i - 1);
            var invTexSize = Vector2.One / new Vector2(currentMipSize.X, currentMipSize.Y);

            if (commandList == null)
            {
                Accumulation.Bind(FramebufferTarget.DrawFramebuffer);
            }

            // render into next higher res mip
            Accumulation.AttachColorMipLevel(i - 1);

            // first combine needs two blur+tint values, for first and second mip
            // subsequent combines only need one, for current mip
            var upsampleShader = isFirstUpsample
                ? firstUpsample
                : upsample;

            upsampleShader.Use(commandList);

            // Only the coarser level being sampled is made readable, before the pass claims the finer one
            // as its target. The two levels are in different states for the length of this pass, which is
            // exactly what the mip range is for.
            PostProcessRenderer.AttachmentMipReadBarrier(commandList, Accumulation.Color, i, 1);

            // The pass renders into one mip of the same texture it samples, which is why it loads rather
            // than clears: the merge composites the coarser level onto what is already in the finer one.
            using var upsamplePass = PostProcessRenderer.BeginPass(commandList, Accumulation, "Bloom Upsample", i - 1);

            PostProcessRenderer.SetViewport(commandList, currentMipSize.X, currentMipSize.Y);

            PostProcessRenderer.BindTexture(commandList, upsampleShader, 0, "g_tSource", Accumulation.Color);
            upsampleShader.SetUniform("g_vTexelSize", invTexSize);
            upsampleShader.SetUniform("g_nCurrentMip", (float)i);

            if (isFirstUpsample)
            {
                var prevBlurTint = settings.BlurTint[i + 1] * settings.BlurWeight[i + 1];
                upsampleShader.SetUniform("g_vPrevMipBlurTint", prevBlurTint);
            }

            var blurTint = settings.BlurTint[i] * settings.BlurWeight[i];

            // last merge
            // there are 5 blur+tint values, but only 4 mips to combine, seems like s2 bloom used to start
            // at half res, and downsample to 5 mips at some point, but right now it looks like they just
            // combine the last two blur + tint combos into a single one for last mip merge
            if (isLastUpsample)
            {
                blurTint += settings.BlurTint[i - 1] * settings.BlurWeight[i - 1];
            }

            upsampleShader.SetUniform("g_vCurMipBlurTint", blurTint);

            PostProcessRenderer.DrawFullscreenTriangle(commandList, RendererContext, upsampleShader, Accumulation);
        }

        Accumulation.AttachColorMipLevel(0);

        // The last upsample rendered into level zero, and the tonemap samples this result next. The
        // coarser levels were made readable as the loop went and are left alone.
        PostProcessRenderer.AttachmentMipReadBarrier(commandList, Accumulation.Color, 0, 1);
    }

    /// <summary>
    /// Render a texture from <paramref name="ping"/> to <paramref name="pong"/> using the provided screenspace shader.
    /// </summary>
    /// <param name="commandList">The list to record into, or <see langword="null"/> to run through OpenGL.</param>
    /// <param name="shader">The screen-space shader to draw with.</param>
    /// <param name="ping">The framebuffer whose colour attachment is sampled.</param>
    /// <param name="pong">The framebuffer being rendered into.</param>
    /// <param name="size">The viewport to draw into, which is not always the whole attachment.</param>
    /// <param name="mip">Which mip level of the source the shader samples.</param>
    /// <param name="clear">Whether the destination is cleared first.</param>
    /// <param name="destMipLevel">Which mip level of <paramref name="pong"/> is rendered into. The RHI
    /// equivalent of the <see cref="Framebuffer.AttachColorMipLevel"/> the caller makes for the OpenGL
    /// path, and separate from <paramref name="mip"/>, which names a level of the source.</param>
    private void RenderTexture(ICommandList? commandList, Shader shader, Framebuffer ping, Framebuffer pong,
        Vector2i size, int mip = 0, bool clear = false, int destMipLevel = 0)
    {
        var texSize = new Vector2(size.X, size.Y);
        var invTexSize = Vector2.One / new Vector2(size.X, size.Y);

        Debug.Assert(ping.Color != null);

        if (commandList == null)
        {
            pong.Bind(FramebufferTarget.DrawFramebuffer);
        }

        shader.Use(commandList);

        // The source was rendered into by the previous step of the ping-pong, so it is still a colour
        // target. Transitioned before the destination's pass opens, since the two are different textures
        // and the destination's own transition happens there.
        PostProcessRenderer.AttachmentReadBarrier(commandList, ping.Color);

        using var pass = PostProcessRenderer.BeginPass(commandList, pong, "Bloom Blit", destMipLevel, clear);

        PostProcessRenderer.SetViewport(commandList, size.X, size.Y);

        shader.SetUniform("g_vTexelSize", invTexSize);
        shader.SetUniform("g_vTextureSize", texSize);
        shader.SetUniform("g_nCurrentMip", (float)mip);
        PostProcessRenderer.BindTexture(commandList, shader, 0, "g_tSource", ping.Color);

        PostProcessRenderer.DrawFullscreenTriangle(commandList, RendererContext, shader, pong);
    }
}
