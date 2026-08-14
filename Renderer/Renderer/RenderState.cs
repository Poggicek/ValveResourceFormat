using OpenTK.Graphics.OpenGL;

namespace ValveResourceFormat.Renderer
{
    /// <summary>Polygon fill mode. Mirrors <see href="https://s2v.app/SchemaExplorer/cs2/rendersystemdx11/RsFillMode_t"><c>RsFillMode_t</c></see>.</summary>
    public enum FillMode : byte
    {
        /// <summary>Filled polygons.</summary>
        Solid,
        /// <summary>Outlined polygons.</summary>
        Wireframe,
    }

    /// <summary>Face culling mode. Mirrors <see href="https://s2v.app/SchemaExplorer/cs2/rendersystemdx11/RsCullMode_t"><c>RsCullMode_t</c></see>.</summary>
    public enum CullMode : byte
    {
        /// <summary>No culling.</summary>
        None,
        /// <summary>Cull back faces.</summary>
        Back,
        /// <summary>Cull front faces.</summary>
        Front,
    }

    /// <summary>
    /// Comparison function for depth testing. Mirrors <see href="https://s2v.app/SchemaExplorer/cs2/rendersystemdx11/RsComparison_t"><c>RsComparison_t</c></see>, including its
    /// depth-direction-agnostic values: this renderer uses reverse-Z, so prefer
    /// <see cref="Closer"/>/<see cref="Farther"/> to state intent - they resolve to the correct
    /// GL comparison for the depth convention in one place.
    /// </summary>
    public enum Comparison : byte
    {
        /// <summary>Never passes.</summary>
        Never,
        /// <summary>Passes when incoming is less than stored.</summary>
        Less,
        /// <summary>Passes when equal.</summary>
        Equal,
        /// <summary>Passes when less than or equal.</summary>
        LessEqual,
        /// <summary>Passes when greater.</summary>
        Greater,
        /// <summary>Passes when not equal.</summary>
        NotEqual,
        /// <summary>Passes when greater than or equal.</summary>
        GreaterEqual,
        /// <summary>Always passes.</summary>
        Always,
        /// <summary>Passes when closer to the camera, regardless of depth convention.</summary>
        Closer,
        /// <summary>Passes when closer to the camera or equal, regardless of depth convention.</summary>
        CloserEqual,
        /// <summary>Passes when farther from the camera, regardless of depth convention.</summary>
        Farther,
        /// <summary>Passes when farther from the camera or equal, regardless of depth convention.</summary>
        FartherEqual,
    }

    /// <summary>Blend factor for source or destination color.</summary>
    public enum BlendFactor : byte
    {
        /// <summary>Zero.</summary>
        Zero,
        /// <summary>One.</summary>
        One,
        /// <summary>Source color.</summary>
        SrcColor,
        /// <summary>One minus source color.</summary>
        OneMinusSrcColor,
        /// <summary>Source alpha.</summary>
        SrcAlpha,
        /// <summary>One minus source alpha.</summary>
        OneMinusSrcAlpha,
        /// <summary>Destination color.</summary>
        DstColor,
        /// <summary>One minus destination color.</summary>
        OneMinusDstColor,
        /// <summary>Destination alpha.</summary>
        DstAlpha,
        /// <summary>One minus destination alpha.</summary>
        OneMinusDstAlpha,
    }

    /// <summary>Stencil operation, applied when its associated test outcome occurs.</summary>
    public enum StencilOperation : byte
    {
        /// <summary>Keep the stored value.</summary>
        Keep,
        /// <summary>Set the stored value to zero.</summary>
        Zero,
        /// <summary>Replace the stored value with the reference value.</summary>
        Replace,
        /// <summary>Increment the stored value, clamping at maximum.</summary>
        IncrementSaturate,
        /// <summary>Decrement the stored value, clamping at zero.</summary>
        DecrementSaturate,
        /// <summary>Bitwise invert the stored value.</summary>
        Invert,
        /// <summary>Increment the stored value with wrap-around.</summary>
        Increment,
        /// <summary>Decrement the stored value with wrap-around.</summary>
        Decrement,
    }

    /// <summary>Stencil test state. Mirrors <see href="https://s2v.app/SchemaExplorer/cs2/rendersystemdx11/RsStencilStateDesc_t"><c>RsStencilStateDesc_t</c></see>; one set of ops serves both
    /// faces until a consumer needs Valve's front/back split. The reference value is
    /// <see cref="DepthStencilStateDesc.StencilRef"/> - D3D and Vulkan treat it as bind-time dynamic
    /// state, and it stays out of this descriptor to match.</summary>
    public record struct StencilStateDesc
    {
        /// <summary>Whether stencil testing is enabled.</summary>
        public bool StencilEnable { get; set; }
        /// <summary>Stencil comparison function.</summary>
        public Comparison Func { get; set; }
        /// <summary>Operation when the stencil test fails.</summary>
        public StencilOperation FailOp { get; set; }
        /// <summary>Operation when the stencil test passes but the depth test fails.</summary>
        public StencilOperation DepthFailOp { get; set; }
        /// <summary>Operation when both stencil and depth tests pass.</summary>
        public StencilOperation PassOp { get; set; }
        /// <summary>Mask applied to stored and reference values before comparison.</summary>
        public byte ReadMask { get; set; }
        /// <summary>Mask of stencil bits writes (and stencil clears) can touch.</summary>
        public byte WriteMask { get; set; }
    }

    /// <summary>Rasterizer state. Mirrors <see href="https://s2v.app/SchemaExplorer/cs2/rendersystemdx11/RsRasterizerStateDesc_t"><c>RsRasterizerStateDesc_t</c></see>.</summary>
    public record struct RasterizerStateDesc
    {
        /// <summary>Polygon fill mode.</summary>
        public FillMode FillMode { get; set; }
        /// <summary>Face culling mode.</summary>
        public CullMode CullMode { get; set; }
        /// <summary>Constant depth bias, in the smallest resolvable depth units.</summary>
        public float DepthBias { get; set; }
        /// <summary>Maximum total depth bias; 0 disables clamping.</summary>
        public float DepthBiasClamp { get; set; }
        /// <summary>Depth bias scaled by the polygon's depth slope.</summary>
        public float SlopeScaledDepthBias { get; set; }
    }

    /// <summary>Depth and stencil test state. Mirrors <see href="https://s2v.app/SchemaExplorer/cs2/rendersystemdx11/RsDepthStencilStateDesc_t"><c>RsDepthStencilStateDesc_t</c></see>.</summary>
    public record struct DepthStencilStateDesc
    {
        /// <summary>Whether depth testing is enabled.</summary>
        public bool DepthTestEnable { get; set; }
        /// <summary>Whether depth writes are enabled.</summary>
        public bool DepthWriteEnable { get; set; }
        /// <summary>Depth comparison function.</summary>
        public Comparison DepthFunc { get; set; }
        /// <summary>Stencil test state.</summary>
        public StencilStateDesc Stencil { get; set; }
        /// <summary>Stencil reference value. Dynamic bind-time state in D3D and Vulkan; GL couples it
        /// to the comparison, so it rides along here.</summary>
        public byte StencilRef { get; set; }
    }

    /// <summary>Blend state. Mirrors <see href="https://s2v.app/SchemaExplorer/cs2/rendersystemdx11/RsBlendStateDesc_t"><c>RsBlendStateDesc_t</c></see> for a single render target.</summary>
    public record struct BlendStateDesc
    {
        /// <summary>Whether blending is enabled.</summary>
        public bool BlendEnable { get; set; }
        /// <summary>Source blend factor.</summary>
        public BlendFactor SrcBlend { get; set; }
        /// <summary>Destination blend factor.</summary>
        public BlendFactor DstBlend { get; set; }
        /// <summary>Whether MSAA alpha-to-coverage is enabled.</summary>
        public bool AlphaToCoverageEnable { get; set; }
        /// <summary>Color channel write mask, RGBA in bits 0-3.</summary>
        public byte RenderTargetWriteMask { get; set; }
    }

    /// <summary>
    /// The complete declarative render state for a draw, mirroring the
    /// <see href="https://s2v.app/SchemaExplorer/cs2/rendersystemdx11"><c>rendersystemdx11</c></see>
    /// state descriptors. Pure data: state is composed, not toggled, and the per-GL-context
    /// <see cref="RenderStateTracker"/> (on <see cref="RendererContext.RenderState"/>) applies it.
    /// </summary>
    public record struct RenderState
    {
        // Fields rather than properties so members can be set directly (state.DepthStencil.DepthFunc = x);
        // a property getter would return a copy of the sub-state.
#pragma warning disable CA1051 // Do not declare visible instance fields
        /// <summary>Rasterizer state.</summary>
        public RasterizerStateDesc Rasterizer;
        /// <summary>Depth test state.</summary>
        public DepthStencilStateDesc DepthStencil;
        /// <summary>Blend state.</summary>
        public BlendStateDesc Blend;
#pragma warning restore CA1051

        /// <summary>Gets the renderer's ambient default: solid fill, backface culling, depth test
        /// and write on with the closer-wins comparison, blending off with standard alpha factors,
        /// all color channels written.</summary>
        public static RenderState Default => new()
        {
            Rasterizer = new()
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.Back,
            },
            DepthStencil = new()
            {
                DepthTestEnable = true,
                DepthWriteEnable = true,
                DepthFunc = Comparison.Closer,
                Stencil = new()
                {
                    Func = Comparison.Always,
                    ReadMask = 0xFF,
                    WriteMask = 0xFF, // stencil clears respect the write mask even with the test disabled
                },
            },
            Blend = new()
            {
                SrcBlend = BlendFactor.SrcAlpha,
                DstBlend = BlendFactor.OneMinusSrcAlpha,
                RenderTargetWriteMask = 0xF,
            },
        };

    }

    /// <summary>
    /// Tracks and applies render state for one GL context: owns the pass baseline
    /// (<see cref="CurrentPass"/>) and a shadow of the state last applied to the context, so
    /// <see cref="Apply"/> only emits GL calls for fields that actually changed - redundant applies
    /// (the common case for baseline restores) cost no driver work. GL state is per context, so
    /// each <see cref="RendererContext"/> owns its own tracker.
    /// </summary>
    public class RenderStateTracker
    {
        /// <summary>Gets the state established by the innermost enclosing pass. Per-draw state is
        /// composed by copying this and overriding fields.</summary>
        public RenderState CurrentPass { get; private set; } = RenderState.Default;

        // Shadow of the state last applied to the GL context, the diffing baseline for Apply().
        // Only meaningful once the first Apply has run (appliedValid).
        private RenderState applied;
        private bool appliedValid;

        /// <summary>Composes a state over <see cref="CurrentPass"/> and applies it for the lifetime of
        /// the returned <see langword="using"/> guard, which restores the previous baseline on dispose.
        /// Omitted arguments inherit the enclosing pass. For state the arguments do not cover (e.g.
        /// stencil), compose a <see cref="RenderState"/> by hand and open a
        /// <see cref="RenderPassScope"/> directly.</summary>
        /// <param name="fillMode">Polygon fill mode.</param>
        /// <param name="cullMode">Face culling mode.</param>
        /// <param name="depthBias">Constant depth bias.</param>
        /// <param name="depthBiasClamp">Maximum total depth bias.</param>
        /// <param name="slopeScaledDepthBias">Slope-scaled depth bias.</param>
        /// <param name="depthTest">Whether depth testing is enabled.</param>
        /// <param name="depthWrite">Whether depth writes are enabled.</param>
        /// <param name="depthFunc">Depth comparison function.</param>
        /// <param name="blend">Whether blending is enabled.</param>
        /// <param name="srcBlend">Source blend factor.</param>
        /// <param name="dstBlend">Destination blend factor.</param>
        /// <param name="colorWriteMask">Color channel write mask, RGBA in bits 0-3.</param>
        /// <returns>The scope guard restoring the previous baseline on dispose.</returns>
        public RenderPassScope Scope(
            FillMode? fillMode = null,
            CullMode? cullMode = null,
            float? depthBias = null,
            float? depthBiasClamp = null,
            float? slopeScaledDepthBias = null,
            bool? depthTest = null,
            bool? depthWrite = null,
            Comparison? depthFunc = null,
            bool? blend = null,
            BlendFactor? srcBlend = null,
            BlendFactor? dstBlend = null,
            byte? colorWriteMask = null)
        {
            var state = CurrentPass;

            state.Rasterizer.FillMode = fillMode ?? state.Rasterizer.FillMode;
            state.Rasterizer.CullMode = cullMode ?? state.Rasterizer.CullMode;
            state.Rasterizer.DepthBias = depthBias ?? state.Rasterizer.DepthBias;
            state.Rasterizer.DepthBiasClamp = depthBiasClamp ?? state.Rasterizer.DepthBiasClamp;
            state.Rasterizer.SlopeScaledDepthBias = slopeScaledDepthBias ?? state.Rasterizer.SlopeScaledDepthBias;
            state.DepthStencil.DepthTestEnable = depthTest ?? state.DepthStencil.DepthTestEnable;
            state.DepthStencil.DepthWriteEnable = depthWrite ?? state.DepthStencil.DepthWriteEnable;
            state.DepthStencil.DepthFunc = depthFunc ?? state.DepthStencil.DepthFunc;
            state.Blend.BlendEnable = blend ?? state.Blend.BlendEnable;
            state.Blend.SrcBlend = srcBlend ?? state.Blend.SrcBlend;
            state.Blend.DstBlend = dstBlend ?? state.Blend.DstBlend;
            state.Blend.RenderTargetWriteMask = colorWriteMask ?? state.Blend.RenderTargetWriteMask;

            return new RenderPassScope(this, in state);
        }

        /// <summary>Applies a state and makes it the baseline that composed per-draw states and
        /// restores derive from, until the enclosing pass re-establishes its own.</summary>
        /// <param name="state">The pass baseline state.</param>
        public void ApplyAsPassBaseline(in RenderState state)
        {
            CurrentPass = state;
            Apply(in state);
        }

        /// <summary>Applies a state to GL, emitting calls only for fields that differ from the
        /// previously applied state.</summary>
        /// <param name="state">The state to apply.</param>
        public void Apply(in RenderState state)
        {
            var force = !appliedValid;

            if (!force && state == applied)
            {
                return;
            }

            if (force || state.Rasterizer.FillMode != applied.Rasterizer.FillMode)
            {
                GL.PolygonMode(TriangleFace.FrontAndBack, state.Rasterizer.FillMode == FillMode.Wireframe ? PolygonMode.Line : PolygonMode.Fill);
            }

            if (force || state.Rasterizer.CullMode != applied.Rasterizer.CullMode)
            {
                if (state.Rasterizer.CullMode == CullMode.None)
                {
                    GL.Disable(EnableCap.CullFace);
                }
                else
                {
                    GL.Enable(EnableCap.CullFace);
                    GL.CullFace(state.Rasterizer.CullMode == CullMode.Front ? TriangleFace.Front : TriangleFace.Back);
                }
            }

            if (force
                || state.Rasterizer.DepthBias != applied.Rasterizer.DepthBias
                || state.Rasterizer.DepthBiasClamp != applied.Rasterizer.DepthBiasClamp
                || state.Rasterizer.SlopeScaledDepthBias != applied.Rasterizer.SlopeScaledDepthBias)
            {
                // Both polygon modes get the bias, Vulkan-style, so a biased material stays biased in wireframe.
                if (state.Rasterizer.DepthBias != 0f || state.Rasterizer.SlopeScaledDepthBias != 0f)
                {
                    GL.Enable(EnableCap.PolygonOffsetFill);
                    GL.Enable(EnableCap.PolygonOffsetLine);
                    GL.PolygonOffsetClamp(state.Rasterizer.SlopeScaledDepthBias, state.Rasterizer.DepthBias, state.Rasterizer.DepthBiasClamp);
                }
                else
                {
                    GL.Disable(EnableCap.PolygonOffsetFill);
                    GL.Disable(EnableCap.PolygonOffsetLine);
                    GL.PolygonOffsetClamp(0f, 0f, 0f);
                }
            }

            if (force || state.DepthStencil.DepthTestEnable != applied.DepthStencil.DepthTestEnable)
            {
                if (state.DepthStencil.DepthTestEnable)
                {
                    GL.Enable(EnableCap.DepthTest);
                }
                else
                {
                    GL.Disable(EnableCap.DepthTest);
                }
            }

            if (force || state.DepthStencil.DepthWriteEnable != applied.DepthStencil.DepthWriteEnable)
            {
                GL.DepthMask(state.DepthStencil.DepthWriteEnable);
            }

            if (force || state.DepthStencil.DepthFunc != applied.DepthStencil.DepthFunc)
            {
                GL.DepthFunc(ToGL(state.DepthStencil.DepthFunc));
            }

            var stencil = state.DepthStencil.Stencil;
            var appliedStencil = applied.DepthStencil.Stencil;

            if (force || stencil.StencilEnable != appliedStencil.StencilEnable)
            {
                if (stencil.StencilEnable)
                {
                    GL.Enable(EnableCap.StencilTest);
                }
                else
                {
                    GL.Disable(EnableCap.StencilTest);
                }
            }

            if (force
                || stencil.FailOp != appliedStencil.FailOp
                || stencil.DepthFailOp != appliedStencil.DepthFailOp
                || stencil.PassOp != appliedStencil.PassOp)
            {
                GL.StencilOp(ToGL(stencil.FailOp), ToGL(stencil.DepthFailOp), ToGL(stencil.PassOp));
            }

            if (force
                || stencil.Func != appliedStencil.Func
                || stencil.ReadMask != appliedStencil.ReadMask
                || state.DepthStencil.StencilRef != applied.DepthStencil.StencilRef)
            {
                GL.StencilFunc(ToGLStencil(stencil.Func), state.DepthStencil.StencilRef, stencil.ReadMask);
            }

            if (force || stencil.WriteMask != appliedStencil.WriteMask)
            {
                GL.StencilMask(stencil.WriteMask);
            }

            if (force || state.Blend.BlendEnable != applied.Blend.BlendEnable)
            {
                if (state.Blend.BlendEnable)
                {
                    GL.Enable(EnableCap.Blend);
                }
                else
                {
                    GL.Disable(EnableCap.Blend);
                }
            }

            if (force || state.Blend.SrcBlend != applied.Blend.SrcBlend || state.Blend.DstBlend != applied.Blend.DstBlend)
            {
                GL.BlendFunc(ToGLSrc(state.Blend.SrcBlend), ToGLDst(state.Blend.DstBlend));
            }

            if (force || state.Blend.AlphaToCoverageEnable != applied.Blend.AlphaToCoverageEnable)
            {
                if (state.Blend.AlphaToCoverageEnable)
                {
                    GL.Enable(EnableCap.SampleAlphaToCoverage);
                }
                else
                {
                    GL.Disable(EnableCap.SampleAlphaToCoverage);
                }
            }

            if (force || state.Blend.RenderTargetWriteMask != applied.Blend.RenderTargetWriteMask)
            {
                var mask = state.Blend.RenderTargetWriteMask;
                GL.ColorMask((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0);
            }

            applied = state;
            appliedValid = true;
        }

        // The renderer is reverse-Z: greater depth values are closer to the camera.
        private static DepthFunction ToGL(Comparison comparison) => comparison switch
        {
            Comparison.Never => DepthFunction.Never,
            Comparison.Less => DepthFunction.Less,
            Comparison.Equal => DepthFunction.Equal,
            Comparison.LessEqual => DepthFunction.Lequal,
            Comparison.Greater => DepthFunction.Greater,
            Comparison.NotEqual => DepthFunction.Notequal,
            Comparison.GreaterEqual => DepthFunction.Gequal,
            Comparison.Always => DepthFunction.Always,
            Comparison.Closer => DepthFunction.Greater,
            Comparison.CloserEqual => DepthFunction.Gequal,
            Comparison.Farther => DepthFunction.Less,
            Comparison.FartherEqual => DepthFunction.Lequal,
            _ => throw new NotImplementedException($"Unknown comparison {comparison}"),
        };

        private static StencilFunction ToGLStencil(Comparison comparison) => (StencilFunction)ToGL(comparison);

        private static StencilOp ToGL(StencilOperation operation) => operation switch
        {
            StencilOperation.Keep => StencilOp.Keep,
            StencilOperation.Zero => StencilOp.Zero,
            StencilOperation.Replace => StencilOp.Replace,
            StencilOperation.IncrementSaturate => StencilOp.Incr,
            StencilOperation.DecrementSaturate => StencilOp.Decr,
            StencilOperation.Invert => StencilOp.Invert,
            StencilOperation.Increment => StencilOp.IncrWrap,
            StencilOperation.Decrement => StencilOp.DecrWrap,
            _ => throw new NotImplementedException($"Unknown stencil operation {operation}"),
        };

        private static BlendingFactor ToGLSrc(BlendFactor factor) => (BlendingFactor)ToGLDst(factor);

        private static BlendingFactor ToGLDst(BlendFactor factor) => factor switch
        {
            BlendFactor.Zero => BlendingFactor.Zero,
            BlendFactor.One => BlendingFactor.One,
            BlendFactor.SrcColor => BlendingFactor.SrcColor,
            BlendFactor.OneMinusSrcColor => BlendingFactor.OneMinusSrcColor,
            BlendFactor.SrcAlpha => BlendingFactor.SrcAlpha,
            BlendFactor.OneMinusSrcAlpha => BlendingFactor.OneMinusSrcAlpha,
            BlendFactor.DstColor => BlendingFactor.DstColor,
            BlendFactor.OneMinusDstColor => BlendingFactor.OneMinusDstColor,
            BlendFactor.DstAlpha => BlendingFactor.DstAlpha,
            BlendFactor.OneMinusDstAlpha => BlendingFactor.OneMinusDstAlpha,
            _ => throw new NotImplementedException($"Unknown blend factor {factor}"),
        };
    }

    /// <summary>
    /// Establishes a render pass baseline for its scope and restores the previous baseline on
    /// dispose. Nesting-safe: the new baseline is typically composed from
    /// <see cref="RenderStateTracker.CurrentPass"/>, so an outer pass's overrides (e.g. global
    /// wireframe) survive into sub-passes.
    /// </summary>
    public readonly ref struct RenderPassScope
    {
        private readonly RenderStateTracker tracker;
        private readonly RenderState previous;

        /// <summary>Applies <paramref name="state"/> as the pass baseline.</summary>
        /// <param name="tracker">The state tracker of the GL context being rendered to.</param>
        /// <param name="state">The baseline state for this pass.</param>
        public RenderPassScope(RenderStateTracker tracker, scoped in RenderState state)
        {
            this.tracker = tracker;
            previous = tracker.CurrentPass;
            tracker.ApplyAsPassBaseline(in state);
        }

        /// <summary>Restores the previous pass baseline.</summary>
        public void Dispose() => tracker.ApplyAsPassBaseline(in previous);
    }
}
