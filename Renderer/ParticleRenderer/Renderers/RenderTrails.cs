using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.Particles.Utils;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.OpenGL;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Particles.Renderers
{
    /// <summary>
    /// Renders particles as trail segments stretched between the particle's current and previous
    /// positions, with configurable length, fade-in, texture scaling, and blend modes.
    /// </summary>
    /// <remarks>
    /// Trails are sprites that stretch based on their speed over time. Traditional use cases
    /// include bullet tracers and sparks; they are also useful when particles need to be oriented
    /// in 3D space, which regular sprites handle poorly.
    /// </remarks>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/particles/C_OP_RenderTrails">C_OP_RenderTrails</seealso>
    internal class RenderTrails : ParticleFunctionRenderer
    {
        private const string ShaderName = "particle_trail";
        private const string DefaultTextureName = "materials/particle/base_trail.vtex";

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct Vertex(Vector3 position, Vector4 color, Vector2 uv, Vector2 uvNextFrame, float frameBlend)
        {
            [VertexAttribute(VertexSlot.Position)] public readonly Vector3 Position = position;
            [VertexAttribute(VertexSlot.Color)] public readonly Vector4 Color = color;
            [VertexAttribute(VertexSlot.TexCoord)] public readonly Vector2 UV = uv;
            [VertexAttribute(VertexSlot.TexCoord1)] public readonly Vector2 UVNextFrame = uvNextFrame;
            [VertexAttribute("vFrameBlend")] public readonly float FrameBlend = frameBlend;

            /// <summary>The layout of this vertex, for creating vertex array objects.</summary>
            public static readonly VertexInputLayout InputLayout = VertexInputLayout.FromStruct<Vertex>();
        }

        // The shared quad index buffer covers 65532 indices, six per quad
        private const int MaxQuads = 65532 / 6;

        // Quad corners in ring order, matching the winding of the shared quad index buffer
        private static readonly Vector2[] QuadCorners = [new(-1f, -1f), new(-1f, 1f), new(1f, 1f), new(1f, -1f)];

        private readonly Shader shader;
        private readonly RendererContext rendererContext;
        private readonly int vaoHandle;
        private readonly int vertexBufferHandle;
        private RenderTexture texture;

        // Non-owning RHI view of the OpenGL vertex buffer this renderer draws from, so the draw can be
        // recorded through a command list without the OpenGL path losing the buffer name its vertex array
        // was built from. Rebuilt when the vertex buffer is reallocated to a different size, which
        // glNamedBufferData does every frame the quad count changes. OpenGL only.
        private GLBuffer? vertexRhiBuffer;

        // The same buffer on a device that has no OpenGL in it, owned rather than wrapped: there is no
        // glCreateBuffers name to view, so this is created through IDevice and written through
        // IDevice.UploadBuffer. Reallocated whenever the quad count changes the size it needs.
        private IBuffer? deviceVertexBuffer;

        // Built once on first RHI draw rather than in a static initializer, so a layout the contract has
        // no format for throws at the draw that needs it instead of as a type initializer failure.
        private VertexInputDesc? vertexInputDesc;

        private readonly float animationRate = 0.1f;
        private readonly ParticleAnimationType animationType = ParticleAnimationType.ANIMATION_TYPE_FIXED_RATE;
        private readonly bool animateInFps;
        private readonly bool blendFrames = true;

        // m_bEnableFadingAndClamping gates the size clamp, the distance fade and the view angle fade
        // together. The bounds below are trail-specific and differ from the sprite renderer's.
        private readonly bool enableFadingAndClamping;
        private readonly float minSize;
        private readonly float maxSize = 2000f;
        private readonly INumberProvider startFadeSize = new LiteralNumberProvider(1000f);
        private readonly INumberProvider endFadeSize = new LiteralNumberProvider(2000f);
        private readonly float startFadeDot = 1f;
        private readonly float endFadeDot = 2f;

        private readonly ParticleBlendMode blendMode = ParticleBlendMode.PARTICLE_OUTPUT_BLEND_MODE_ALPHA;
        private readonly ParticleOrientation orientationType;
        private readonly ParticleField prevPositionSource = ParticleField.PositionPrevious; // this is a real thing

        private readonly float finalTextureScaleU = 1f;
        private readonly float finalTextureScaleV = 1f;

        private readonly float maxLength = 2000f;
        private readonly float minLength;
        private readonly float lengthScale = 1f;
        private readonly float lengthFadeInTime;
        private readonly bool ignoreDeltaTime;
        private readonly float constrainRadiusToLengthRatio = 1f;
        private readonly float forwardShift;

        private readonly INumberProvider headRadiusTaper = new LiteralNumberProvider(1f);
        private readonly INumberProvider tailRadiusTaper = new LiteralNumberProvider(1f);
        private readonly INumberProvider headAlphaScale = new LiteralNumberProvider(1f);
        private readonly INumberProvider tailAlphaScale = new LiteralNumberProvider(1f);
        private readonly IVectorProvider headColorScale = new LiteralVectorProvider(Vector3.One);
        private readonly IVectorProvider tailColorScale = new LiteralVectorProvider(Vector3.One);

        public RenderTrails(ParticleDefinitionParser parse, RendererContext rendererContext) : base(parse)
        {
            this.rendererContext = rendererContext;

            blendMode = parse.Enum<ParticleBlendMode>("m_nOutputBlendMode", blendMode);

            shader = rendererContext.ShaderLoader.LoadShader(ShaderName);

            // All trails of this renderer are batched into a single dynamic vertex buffer
            (vaoHandle, vertexBufferHandle) = SetupQuadBuffer();

            string? textureName = null;

            if (parse.Data.ContainsKey("m_hTexture"))
            {
                textureName = parse.Data.GetStringProperty("m_hTexture");
            }
            else
            {
                var textures = parse.Array("m_vecTexturesInput");
                if (textures.Length > 0)
                {
                    // TODO: Support more than one texture
                    textureName = textures[0].Data.GetStringProperty("m_hTexture");
                }
            }

            texture = rendererContext.MaterialLoader.GetTexture(textureName ?? DefaultTextureName, srgbRead: true);

#if DEBUG
            if (RendererDevice.IsOpenGL(rendererContext.Device))
            {
                var vaoLabel = $"{nameof(RenderTrails)}: {System.IO.Path.GetFileName(textureName)}";
                GL.ObjectLabel(ObjectLabelIdentifier.VertexArray, vaoHandle, Math.Min(GLEnvironment.MaxLabelLength, vaoLabel.Length), vaoLabel);
                GL.ObjectLabel(ObjectLabelIdentifier.Buffer, vertexBufferHandle, Math.Min(GLEnvironment.MaxLabelLength, vaoLabel.Length), vaoLabel);
            }
#endif

            orientationType = parse.Enum("m_nOrientationType", orientationType);
            animationRate = parse.Float("m_flAnimationRate", animationRate);
            finalTextureScaleU = parse.Float("m_flFinalTextureScaleU", finalTextureScaleU);
            finalTextureScaleV = parse.Float("m_flFinalTextureScaleV", finalTextureScaleV);
            maxLength = parse.Float("m_flMaxLength", maxLength);
            minLength = parse.Float("m_flMinLength", minLength);
            lengthScale = parse.Float("m_flLengthScale", lengthScale);
            lengthFadeInTime = parse.Float("m_flLengthFadeInTime", lengthFadeInTime);
            ignoreDeltaTime = parse.Boolean("m_bIgnoreDT", ignoreDeltaTime);
            constrainRadiusToLengthRatio = parse.Float("m_flConstrainRadiusToLengthRatio", constrainRadiusToLengthRatio);
            forwardShift = parse.Float("m_flForwardShift", forwardShift);
            animationType = parse.Enum<ParticleAnimationType>("m_nAnimationType", animationType);
            animateInFps = parse.Boolean("m_bAnimateInFPS", animateInFps);
            blendFrames = parse.Boolean("m_bBlendFramesSeq0", blendFrames);
            enableFadingAndClamping = parse.Boolean("m_bEnableFadingAndClamping", enableFadingAndClamping);
            minSize = parse.Float("m_flMinSize", minSize);
            maxSize = parse.Float("m_flMaxSize", maxSize);
            startFadeSize = parse.NumberProvider("m_flStartFadeSize", startFadeSize);
            endFadeSize = parse.NumberProvider("m_flEndFadeSize", endFadeSize);
            startFadeDot = parse.Float("m_flStartFadeDot", startFadeDot);
            endFadeDot = parse.Float("m_flEndFadeDot", endFadeDot);
            prevPositionSource = parse.ParticleField("m_nPrevPntSource", prevPositionSource);
            headRadiusTaper = parse.NumberProvider("m_flRadiusHeadTaper", headRadiusTaper);
            tailRadiusTaper = parse.NumberProvider("m_flRadiusTaper", tailRadiusTaper);
            headAlphaScale = parse.NumberProvider("m_flHeadAlphaScale", headAlphaScale);
            tailAlphaScale = parse.NumberProvider("m_flTailAlphaScale", tailAlphaScale);
            headColorScale = parse.VectorProvider("m_vecHeadColorScale", headColorScale);
            tailColorScale = parse.VectorProvider("m_vecTailColorScale", tailColorScale);

            if (minLength > maxLength)
            {
                // Some particles may have length range set up incorrectly
                maxLength = minLength;
            }
        }

        public override void SetWireframe(bool isWireframe)
        {
            shader.SetUniform1("isWireframe", isWireframe ? 1 : 0);
        }

        /// <inheritdoc/>
        public override void SetTextureOverride(RenderTexture texture)
        {
            this.texture = texture;
        }

        /// <summary>
        /// Creates the OpenGL vertex buffer and the vertex array that reads it.
        /// </summary>
        /// <remarks>
        /// Both are OpenGL-only. On any other backend the storage is allocated at the first draw instead,
        /// once the frame's quad count says how much is needed, and there is no vertex array at all
        /// &#8212; the recorded draw takes its layout from the pipeline.
        /// </remarks>
        private (int Vao, int Buffer) SetupQuadBuffer()
        {
            if (!RendererDevice.IsOpenGL(rendererContext.Device))
            {
                return (0, 0);
            }

            GL.CreateBuffers(1, out int buffer);

            var vao = Vertex.InputLayout.CreateVertexArray(nameof(RenderTrails), buffer, rendererContext.MeshBufferCache.QuadIndices.GLHandle);

            return (vao, buffer);
        }

        /// <summary>
        /// Builds one quad per visible trail into the shared vertex buffer, returning how many were written.
        /// </summary>
        /// <param name="particleBag">The live particles to build ribbons from.</param>
        /// <param name="systemRenderState">Render state the number providers are evaluated against.</param>
        /// <param name="camera">Camera the ribbons are oriented and size-faded against.</param>
        /// <param name="device">The device the draw will be recorded on, which decides whether the
        /// vertices go into the OpenGL buffer name or into device-allocated storage.</param>
        private int UpdateVertices(ParticleCollection particleBag, ParticleSystemRenderState systemRenderState, Camera camera, IDevice? device)
        {
            var headColor = headColorScale.NextVector(systemRenderState);
            var tailColor = tailColorScale.NextVector(systemRenderState);

            // The moved distance is converted back to a velocity (distance / dt) before scaling by
            // the trail-length attribute. The division only applies when the previous point comes
            // from the Verlet pair, and the operator can opt out of it entirely.
            var usesVerletDelta = prevPositionSource == ParticleField.PositionPrevious;
            var oneOverDt = ignoreDeltaTime || !usesVerletDelta || particleBag.CurrentFrameTime == 0f
                ? 1f
                : 1f / particleBag.CurrentFrameTime;

            // Both fade bounds are a radius per unit of camera distance, as the two size bounds are.
            var startFadeSlope = startFadeSize.NextNumber(systemRenderState);
            var endFadeSlope = endFadeSize.NextNumber(systemRenderState);

            // The shader fades by view angle in every mode and outside the fade-and-clamp gate; the
            // defaults of (1, 2) put the smoothstep past its own range, which is what makes it inert.
            var viewAngleFadeActive = startFadeDot < 1f && endFadeDot > startFadeDot;

            var quadCount = 0;

            // Rented from the shared float pool so the memory is reused across renderers.
            using (var vertexBuffer = new RentedFloatBuffer<Vertex>(particleBag.Count * 4))
            {
                var vertices = vertexBuffer.Span;

                foreach (ref var particle in particleBag.Current)
                {
                    var position = particle.Position;
                    var previousPosition = particle.GetVector(prevPositionSource);
                    // A particle that has not moved has no direction to run in, and the engine collapses
                    // its four control points onto the position rather than streaking along a fixed axis
                    var difference = previousPosition - position;

                    if (difference == Vector3.Zero)
                    {
                        continue;
                    }

                    var direction = Vector3.Normalize(difference);

                    var length = lengthScale * particle.TrailLength * difference.Length() * oneOverDt;

                    length *= MathF.Min(1f, particle.Age / MathF.Max(lengthFadeInTime, 1e-9f));

                    // The engine clamps the full extent of the trail, and it clamps unconditionally: an
                    // effect that authors m_flLengthScale 0 alongside a minimum length is asking for a
                    // fixed streak that does not track speed, so a zero raw length still draws.
                    length = Math.Clamp(length, minLength, maxLength);

                    if (length == 0f)
                    {
                        continue;
                    }

                    var particleRadius = particle.Radius * RadiusScale.NextNumber(ref particle, systemRenderState);

                    // Scales rgb and alpha alike, as the view angle fade below touches alpha only
                    var colorFade = 1f;
                    var alphaFade = 1f;

                    if (enableFadingAndClamping)
                    {
                        var cameraDistance = Vector3.Distance(camera.Location, particle.Position);
                        var fadeStart = startFadeSlope * cameraDistance;
                        var fadeEnd = endFadeSlope * cameraDistance;

                        if (particleRadius > fadeStart)
                        {
                            if (particleRadius >= fadeEnd)
                            {
                                continue;
                            }

                            colorFade = 1f - ((particleRadius - fadeStart) / (fadeEnd - fadeStart));
                        }
                    }

                    if (viewAngleFadeActive)
                    {
                        var toCamera = camera.Location - particle.Position;

                        if (toCamera.LengthSquared() > Epsilon.LengthSquared)
                        {
                            // Only the normal-aligned mode has a normal to face with; the others
                            // substitute the direction the ribbon runs in.
                            var facingAxis = orientationType == ParticleOrientation.PARTICLE_ORIENTATION_ALIGN_TO_PARTICLE_NORMAL
                                ? particle.Normal
                                : direction;

                            var facing = MathF.Abs(Vector3.Dot(Vector3.Normalize(facingAxis), Vector3.Normalize(toCamera)));
                            alphaFade = 1f - MathUtils.Smoothstep(startFadeDot, endFadeDot, facing);
                        }
                    }

                    // A short trail is narrowed so it cannot render wider than it is long
                    var radius = MathF.Min(particleRadius, constrainRadiusToLengthRatio * length);

                    // The ribbon always runs along the motion; only the plane it is flattened into
                    // changes with the orientation. The spritecard vertex shader takes the up vector
                    // for that plane from three buckets, and only ALIGN_TO_PARTICLE_NORMAL reads the
                    // particle's normal: it is the sole mode whose instance record carries one.
                    var planeNormal = orientationType switch
                    {
                        ParticleOrientation.PARTICLE_ORIENTATION_ALIGN_TO_PARTICLE_NORMAL => particle.Normal,
                        ParticleOrientation.PARTICLE_ORIENTATION_WORLD_Z_ALIGNED => Vector3.UnitZ,
                        _ => camera.Location - position,
                    };

                    var widthAxis = Vector3.Cross(direction, planeNormal);
                    widthAxis = widthAxis.LengthSquared() > Epsilon.LengthSquared
                        ? Vector3.Normalize(widthAxis)
                        : Vector3.Normalize(Vector3.Cross(direction, MathF.Abs(direction.Z) < 0.999f ? Vector3.UnitZ : Vector3.UnitX));

                    var lengthAxis = direction;
                    // The radius is the half extent across the ribbon, while the length spans it end to end
                    var halfWidth = radius;
                    var halfLength = length * 0.5f;

                    // The engine slides the trail along the motion axis by m_flForwardShift lengths;
                    // direction runs backwards along travel here, so the shift subtracts
                    var center = position + (direction * (length * (0.5f - forwardShift)));

                    var headHalfWidth = halfWidth * headRadiusTaper.NextNumber(ref particle, systemRenderState);
                    var tailHalfWidth = halfWidth * tailRadiusTaper.NextNumber(ref particle, systemRenderState);

                    // The shader clamps per vertex, on the radius the CPU has already constrained and
                    // tapered, so each end is bounded against its own distance rather than the centre's
                    if (enableFadingAndClamping)
                    {
                        var headDistance = Vector3.Distance(camera.Location, center - (lengthAxis * halfLength));
                        var tailDistance = Vector3.Distance(camera.Location, center + (lengthAxis * halfLength));

                        headHalfWidth = MathF.Min(MathF.Max(headHalfWidth, minSize * headDistance), maxSize * headDistance);
                        tailHalfWidth = MathF.Min(MathF.Max(tailHalfWidth, minSize * tailDistance), maxSize * tailDistance);
                    }

                    var uvOffset = Vector2.Zero;
                    var uvScale = new Vector2(finalTextureScaleU, finalTextureScaleV);
                    var uvNextOffset = uvOffset;
                    var uvNextScale = uvScale;
                    var frameBlend = 0f;

                    var spriteSheetData = texture.SpriteSheetData;
                    if (spriteSheetData != null && spriteSheetData.Sequences.Length > 0 && spriteSheetData.Sequences[0].Frames.Length > 0)
                    {
                        var sequence = spriteSheetData.Sequences[particle.SequenceNumber % spriteSheetData.Sequences.Length];
                        var (frame, nextFrame, blend) = GetSheetFrame(ref particle, sequence, animationRate, animationType, animateInFps);
                        frameBlend = blend;

                        // TODO: Support more than one image per frame?
                        var currentImage = sequence.Frames[frame].Images[0];
                        var nextImage = sequence.Frames[nextFrame].Images[0];

                        uvOffset = currentImage.UncroppedMin;
                        uvScale *= currentImage.UncroppedMax - currentImage.UncroppedMin;
                        uvNextOffset = nextImage.UncroppedMin;
                        uvNextScale *= nextImage.UncroppedMax - nextImage.UncroppedMin;
                    }

                    // Corners in index buffer winding order, with the local quad's [-1, 1] axes mapping to [0, 1] uvs
                    var quadStart = quadCount * 4;
                    var alpha = particle.Alpha * particle.AlphaAlternate * colorFade * alphaFade
                        * AlphaScale.NextNumber(ref particle, systemRenderState);
                    var tint = particle.Color * colorFade;

                    var head = Vector4.Clamp(
                        new Vector4(tint * headColor, alpha * headAlphaScale.NextNumber(ref particle, systemRenderState)),
                        Vector4.Zero, Vector4.One);
                    var tail = Vector4.Clamp(
                        new Vector4(tint * tailColor, alpha * tailAlphaScale.NextNumber(ref particle, systemRenderState)),
                        Vector4.Zero, Vector4.One);

                    for (var j = 0; j < 4; ++j)
                    {
                        var corner = QuadCorners[j];
                        var isHead = corner.Y < 0f;
                        var worldPosition = center
                            + (widthAxis * (corner.X * (isHead ? headHalfWidth : tailHalfWidth)))
                            + (lengthAxis * (corner.Y * halfLength));
                        var color = isHead ? head : tail;
                        var cornerUv = (corner * 0.5f) + new Vector2(0.5f);
                        var uv = uvOffset + (cornerUv * uvScale);
                        var uvNext = uvNextOffset + (cornerUv * uvNextScale);

                        vertices[quadStart + j] = new Vertex(worldPosition, color, uv, uvNext, frameBlend);
                    }

                    quadCount++;

                    if (quadCount == MaxQuads)
                    {
                        break;
                    }
                }

                if (quadCount > 0)
                {
                    UploadVertices(device, vertexBuffer.FloatArray, quadCount * 4 * Vertex.InputLayout.Stride);
                }
            }

            return quadCount;
        }

        /// <inheritdoc/>
        public override void Render(ParticleCollection particleBag, ParticleSystemRenderState systemRenderState, Scene.RenderContext context)
            => Render(particleBag, systemRenderState, context.Camera, context);

        /// <inheritdoc/>
        /// <remarks>Still overridden because the prewarm path reaches this renderer with only a camera:
        /// it starts in the viewer, which has no render context to thread through.</remarks>
        public override void Render(ParticleCollection particleBag, ParticleSystemRenderState systemRenderState, Camera camera)
            => Render(particleBag, systemRenderState, camera, context: null);

        private void Render(ParticleCollection particleBag, ParticleSystemRenderState systemRenderState, Camera camera, Scene.RenderContext? context)
        {
            var commandList = context?.CommandList;

            if (particleBag.Count == 0)
            {
                return;
            }

            var device = RendererDevice.Resolve(commandList?.Device ?? rendererContext.Device);

            var quadCount = UpdateVertices(particleBag, systemRenderState, camera, device);

            if (quadCount == 0)
            {
                return;
            }

            // The translucent pass leaves blend/depth state to each draw. Enable blending and stop
            // depth writes, or trails render opaque. Cables instead draw opaque with depth writes.
            // Modulate-2x scales what is behind it, so it needs its own factors; see RenderSprites.
            // Trail quads are oriented by motion direction, so either side can face the camera.
            var mod2x = blendMode == ParticleBlendMode.PARTICLE_OUTPUT_BLEND_MODE_MOD2X;
            using var _ = rendererContext.RenderState.Scope(blend: true, depthWrite: false, cullMode: CullMode.None,
                srcBlend: mod2x ? BlendFactor.DstColor : BlendFactor.One,
                dstBlend: mod2x ? BlendFactor.SrcColor : BlendFactor.OneMinusSrcAlpha);

            shader.Use();

            if (commandList == null)
            {
                VertexArray.Bind(vaoHandle, shader);
            }
            else
            {
                // Ribbon quads, two triangles each through the shared quad index buffer.
                commandList.BindPipeline(PipelineFor(commandList, context!.Value));
                commandList.BindVertexBuffer(0, VertexStorage(device, quadCount * 4 * Vertex.InputLayout.Stride));
                commandList.BindIndexBuffer(rendererContext.MeshBufferCache.QuadIndices.GetBuffer(device), IndexType.UInt16);
            }

            // Set on both paths: this also points the sampler uniform at its texture unit, which
            // BindTexture cannot do because it carries no uniform name. That mapping belongs to the
            // pipeline layer, so the two calls stop overlapping once pipelines carry reflection.
            shader.SetTexture(RenderMaterial.TextureUnitStart, "uTexture", texture);

            if (commandList != null)
            {
                // The shader's own slot for this sampler, not a counted one: emission numbers every
                // sampler the source declares and the compiler is free to drop one, so a count can name a
                // descriptor the shader never reads. Zero only stands in for a shader with no reflected
                // interface, where it is the unit TextureUnitStart maps to above.
                var slot = 0;

                if (shader.SpirvInterface is not { } declared || declared.TryGetMaterialTextureSlot("uTexture", out slot))
                {
                    // SamplerFor, never RhiSampler: null on OpenGL so the unit keeps sampler 0 and the
                    // texture's own parameters stay in charge.
                    commandList.BindTexture(DescriptorSets.MaterialTextures, slot,
                        texture.RhiTexture, texture.SamplerFor(commandList.Device));
                }
            }

            // TODO: This formula is a guess but still seems too bright compared to valve particles
            SetSharedUniforms(shader, systemRenderState);

            shader.SetUniform1("uBlendFrames", blendFrames);

            // Set every draw: the program is shared with every other trail renderer, whatever their mode.
            shader.SetUniform1("uBlendMode", (int)blendMode);

            PerfStats.Active.Count(Counter.ParticleDraw);

            if (commandList != null)
            {
                commandList.DrawIndexed(quadCount * 6);
            }
            else
            {
                GL.DrawElements(PrimitiveType.Triangles, quadCount * 6, DrawElementsType.UnsignedShort, 0);
            }
        }

        // Resolved per draw because the blend state depends on the output blend mode, which the pipeline
        // has to bake in. Cached on the pipeline key, so only the first draw of each state builds one.
        private IGraphicsPipeline PipelineFor(ICommandList commandList, Scene.RenderContext context)
        {
            var framebuffer = context.Framebuffer;

            vertexInputDesc ??= Vertex.InputLayout.ToVertexInputDesc();

            return GLRendererDevice.PipelineFor(
                commandList.Device,
                shader,
                rendererContext.RenderState.CurrentPass,
                vertexInputDesc,
                PrimitiveTopology.TriangleList,
                framebuffer.Color is { } color ? [color.RhiFormat] : [],
                framebuffer.Depth?.RhiFormat ?? RhiFormat.Undefined,
                Math.Max(1, framebuffer.NumSamples),
                GLRendererDevice.DrawConstants);
        }

        /// <summary>
        /// Writes this frame's ribbon vertices into the buffer the draw reads, allocating storage first
        /// when the quad count changed the size it needs.
        /// </summary>
        /// <param name="device">The device the draw will be recorded on.</param>
        /// <param name="floats">The rented scratch array the vertices were built in. Longer than the data,
        /// because the pool hands out whole buckets.</param>
        /// <param name="sizeInBytes">How much of <paramref name="floats"/> is this frame's vertices.</param>
        private void UploadVertices(IDevice? device, float[] floats, int sizeInBytes)
        {
            if (RendererDevice.IsOpenGL(device))
            {
                // Unchanged: a mutable store replaced wholesale every frame, which is what the vertex
                // array built in SetupQuadBuffer reads through.
                GL.NamedBufferData(vertexBufferHandle, sizeInBytes, floats, BufferUsageHint.DynamicDraw);
                return;
            }

            EnsureDeviceVertexBuffer(device!, sizeInBytes);

            // Staged by the device. A frame's own uploads are flushed before its command lists execute, so
            // writing here rather than at load time is correct even though this runs mid-frame.
            device!.UploadBuffer(deviceVertexBuffer!, 0, MemoryMarshal.AsBytes(floats.AsSpan(0, sizeInBytes / sizeof(float))));
        }

        private void EnsureDeviceVertexBuffer(IDevice device, int sizeInBytes)
        {
            if (deviceVertexBuffer is not null && deviceVertexBuffer.SizeInBytes == sizeInBytes)
            {
                return;
            }

            if (deviceVertexBuffer is not null)
            {
                // Deferred, not destroyed: a frame still in flight may be reading the old storage.
                device.DeferredDestroy(deviceVertexBuffer);
            }

            deviceVertexBuffer = device.CreateBuffer(new BufferDesc(
                sizeInBytes, BufferUsage.Vertex | BufferUsage.CopyDestination, BufferMemory.DeviceLocal, nameof(RenderTrails)));
        }

        /// <summary>Gets the buffer the recorded draw reads its vertices from.</summary>
        /// <param name="device">The device the draw is recorded on.</param>
        /// <param name="sizeInBytes">This frame's vertex data size.</param>
        private IBuffer VertexStorage(IDevice? device, int sizeInBytes)
        {
            if (!RendererDevice.IsOpenGL(device))
            {
                return deviceVertexBuffer ?? throw new InvalidOperationException(
                    $"{nameof(RenderTrails)} has no vertex storage, so this frame's ribbons were never uploaded.");
            }

            if (vertexRhiBuffer is null || vertexRhiBuffer.SizeInBytes != sizeInBytes)
            {
                vertexRhiBuffer = GLBuffer.Wrap(vertexBufferHandle, sizeInBytes, BufferUsage.Vertex, BufferMemory.DeviceLocal, nameof(RenderTrails));
            }

            return vertexRhiBuffer;
        }

        public override IEnumerable<string> GetSupportedRenderModes() => shader.RenderModes;

        public override void SetRenderMode(string renderMode)
        {
        }

        public override void Delete()
        {
            var device = RendererDevice.Resolve(rendererContext.Device);

            if (!RendererDevice.IsOpenGL(device))
            {
                if (deviceVertexBuffer is not null)
                {
                    // Deferred rather than disposed: a frame that bound this may still be in flight.
                    device!.DeferredDestroy(deviceVertexBuffer);
                    deviceVertexBuffer = null;
                }

                return;
            }

            VertexArray.Delete(vaoHandle);
            GL.DeleteBuffer(vertexBufferHandle);
        }
    }
}
