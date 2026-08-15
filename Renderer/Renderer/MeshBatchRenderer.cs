using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Renderer.Buffers;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.OpenGL;
using ValveResourceFormat.Renderer.World;

namespace ValveResourceFormat.Renderer
{
    /// <summary>
    /// Sorts and dispatches batched mesh draw calls for a render pass.
    /// </summary>
    public static class MeshBatchRenderer
    {
        /// <summary>
        /// Draw call request with distance and render order for sorting.
        /// </summary>
#if DEBUG
        [DebuggerDisplay("{Node.DebugName,nq}")]
#endif
        public record struct Request(RenderableMesh Mesh, DrawCall? Call, float DistanceFromCamera, int RenderOrder, SceneNode Node);
        record struct BatchRequest(RenderableMesh Mesh, DrawCall Call, SceneNode Node);

        /// <summary>Compares two requests by shader pipeline sort ID, placing custom-render nodes at the boundary.</summary>
        public static int CompareCustomPipeline(Request a, Request b)
        {
            const int CustomRenderSortId = 500 * -RenderMaterial.PerShaderSortIdRange;

            return (a.Call, b.Call) switch
            {
                ({ }, { }) => b.Call.Material.SortId - a.Call.Material.SortId,
                (null, { }) => b.Call.Material.SortId - CustomRenderSortId,
                ({ }, null) => CustomRenderSortId - a.Call.Material.SortId,
                (null, null) => 0,
            };
        }

        /// <summary>Compares two requests first by render order, then by shader pipeline sort ID.</summary>
        public static int CompareRenderOrderThenPipeline(Request a, Request b)
        {
            if (a.RenderOrder == b.RenderOrder)
            {
                return a.Call!.Material.SortId - b.Call!.Material.SortId;
            }

            return a.RenderOrder - b.RenderOrder;
        }

        /// <summary>Compares two requests by distance from camera, furthest first (back-to-front).</summary>
        public static int CompareCameraDistance(Request a, Request b)
        {
            return -a.DistanceFromCamera.CompareTo(b.DistanceFromCamera);
        }

        /// <summary>Compares two requests by alpha-test flag first, then by shader program sort ID.</summary>
        public static int CompareAlphaTestThenProgram(Request a, Request b)
        {
            Debug.Assert(a.Call != null && b.Call != null);
            var alphaTestA = a.Call.Material.IsAlphaTest;
            var alphaTestB = b.Call.Material.IsAlphaTest;

            if (alphaTestA == alphaTestB)
            {
                return a.Call.Material.SortId - b.Call.Material.SortId;
            }

            return alphaTestA.CompareTo(alphaTestB);
        }

        /// <summary>Returns <see langword="true"/> if the request is a <see cref="SceneAggregate"/> with no visible children.</summary>
        public static bool IsAggregateWithNoVisibleChildren(Request req)
        {
            return req.Node is SceneAggregate { AnyChildrenVisible: false };
        }

        /// <summary>Sorts requests according to the active render pass and issues all draw calls.</summary>
        /// <param name="requests">Draw call requests to process.</param>
        /// <param name="context">Render context describing the current pass and scene state.</param>
        public static void Render(List<Request> requests, Scene.RenderContext context)
        {
            // Material-ignoring replacement shaders draw without applying render state, so a scope
            // latches the pass baseline for them. Regular materials apply full state per draw.
            using var batchScope = context.ReplacementShader?.IgnoreMaterialData == true
                ? context.Scene.RendererContext.RenderState.Scope()
                : default;

            if (context.RenderPass is RenderPass.Opaque or RenderPass.OpaqueRefract)
            {
                requests.Sort(CompareCustomPipeline);
            }
            else if (context.RenderPass == RenderPass.OpaqueAggregate)
            {
                using var _ = new GLDebugGroup("Sort Indirect Draws");
                var removed = requests.RemoveAll(IsAggregateWithNoVisibleChildren);
                requests.Sort(CompareAlphaTestThenProgram);
            }
            else if (context.RenderPass == RenderPass.StaticOverlay)
            {
                requests.Sort(CompareRenderOrderThenPipeline);
            }
            else if (context.RenderPass == RenderPass.Translucent)
            {
                requests.Sort(CompareCameraDistance);
            }

            BindReservedTextures(context);

            DrawBatch(requests, context);
        }

        /// <summary>Binds the scene-wide textures to their reserved texture units for this pass.</summary>
        private static void BindReservedTextures(Scene.RenderContext context)
        {
            var commandList = context.CommandList;

            foreach (var (slot, _, texture) in context.Textures)
            {
                BindReservedTexture(commandList, slot, texture);
            }

            context.Scene.LightingInfo.BindLightmapTextures(commandList);
        }

        private ref struct Uniforms
        {
            public int AnimationData = -1;
            public int EnvmapTexture = -1;
            public int LPVIrradianceTexture = -1;
            public int Transform = -1;
            public int IsInstancing = -1;
            public int Tint = -1;
            public int MeshId = -1;
            public int ShaderId = -1;
            public int ShaderProgramId = -1;
            public int MorphCompositeTextureSize = -1;
            public int MorphVertexIdOffset = -1;

            public Uniforms() { }
        }

        private ref struct Config
        {
            public bool NeedsCubemapBinding;
            public int LightmapGameVersionNumber;
            public bool IndirectDraw;
            public LightProbeType LightProbeType;

            /// <summary>The command list to record through, or null to issue OpenGL calls directly.</summary>
            public ICommandList? CommandList;

            /// <summary>The device backing <see cref="CommandList"/>, which builds the pipelines.</summary>
            public IDevice? Device;

            /// <summary>The state tracker whose current pass every pipeline's state is composed over.</summary>
            public RenderStateTracker? RenderState;

            /// <summary>The attachment formats a pipeline for this pass must declare.</summary>
            public RhiFormat[] ColorFormats;
            public RhiFormat DepthFormat;
            public int SampleCount;

            /// <summary>
            /// :DrawPushConstantParity - the per-draw block, carried across the batch so that
            /// <see cref="ICommandList.SetPushConstants{T}"/> replaces the loose
            /// <c>glProgramUniform</c> writes rather than being a second copy of them.
            /// </summary>
            /// <remarks>
            /// One block for the whole batch, refilled per draw. The OpenGL backend diffs it field by field
            /// against what the program already holds, so a field that did not change between two draws
            /// still costs no call and the recorded path issues the same GL calls the direct path does. The
            /// Vulkan backend pushes all 92 bytes, which is why <c>Draw</c> writes every field on every
            /// draw: a field left alone would carry the previous draw's value on that backend while
            /// costing nothing on this one, and the oracle could never see the difference.
            /// </remarks>
            public DrawPushConstants PushConstants;
        }

        /// <summary>
        /// Binds a texture to its reserved unit, recording when there is a command list.
        /// </summary>
        /// <remarks>
        /// Not routed through <see cref="Shader.BindTexture"/>, because a reserved unit is bound for
        /// whatever draws next rather than for one program: the pass-wide binds have no shader to ask, and
        /// the per-draw ones are already gated on the bound shader declaring the sampler. Either way the
        /// unit is settled for every program at link time by
        /// <see cref="GLSamplerBindings.PointReservedSamplersAtUnits"/>, so no sampler uniform is written
        /// here on either path.
        /// </remarks>
        private static void BindReservedTexture(ICommandList? commandList, ReservedTextureSlots slot, RenderTexture texture)
        {
            if (commandList != null)
            {
                // SamplerFor rather than RhiSampler: null on OpenGL, so the unit keeps sampler 0 and
                // defers to the texture's own parameters. See the note at the matching call in Shader.
                commandList.BindTexture(DescriptorSets.ReservedTextures, (int)slot, texture.RhiTexture, texture.SamplerFor(commandList.Device));
                return;
            }

            GL.BindTextureUnit((int)slot, texture.Handle);
        }

        private static void DrawBatch(List<Request> requests, Scene.RenderContext context)
        {
            var vao = -1;
            Shader? shader = null;
            RenderMaterial? material = null;
            Uniforms uniforms = new();

            var commandList = context.CommandList;
            var framebuffer = context.Framebuffer;

            Config config = new()
            {
                NeedsCubemapBinding = context.Scene.LightingInfo.CubemapType == CubemapType.IndividualCubemaps,
                LightmapGameVersionNumber = context.Scene.LightingInfo.LightmapGameVersionNumber,
                LightProbeType = context.Scene.LightingInfo.LightProbeType,
                IndirectDraw = context.Scene.DrawMeshletsIndirect && context.RenderPass < RenderPass.Opaque,

                CommandList = commandList,
                Device = commandList?.Device,
                RenderState = context.Scene.RendererContext.RenderState,
                ColorFormats = framebuffer.Color is { } color ? [color.RhiFormat] : [],
                DepthFormat = framebuffer.Depth?.RhiFormat ?? RhiFormat.Undefined,
                SampleCount = Math.Max(1, framebuffer.NumSamples),
            };

            // Set whenever the pipeline or the geometry a draw needs stops matching what is bound. The
            // same three things that make the OpenGL path rebind: the shader, the material, and the VAO.
            var rebindPipeline = false;

            // Set alongside it whenever the material changes, and consumed after the pipeline bind.
            var rebindMaterialTextures = false;

            // Reused across every material change in this batch; CollectTextureBindings clears it.
            var materialTextures = commandList != null ? new List<RenderMaterial.TextureBinding>() : null;

            var counters = PerfStats.Active;

            foreach (var request in requests)
            {
                if (request.Call == null)
                {
                    if (context.RenderPass is RenderPass.Opaque or RenderPass.Translucent or RenderPass.Outline or RenderPass.DepthOnly)
                    {
                        material?.PostRender();

                        // Custom nodes render themselves and may issue several draws internally; count them as one draw call.
                        counters.Count(Counter.DrawCall);
                        request.Node.Render(context);

                        // Custom nodes bind over the reserved units, so restore them.
                        BindReservedTextures(context);

                        if (context.ReplacementShader?.IgnoreMaterialData == true)
                        {
                            // The stateless draws that follow cannot re-apply state, so restore the
                            // pass baseline the node's scope left latched.
                            var renderState = context.Scene.RendererContext.RenderState;
                            renderState.Apply(renderState.CurrentPass);
                        }

                        shader = null;
                        material = null;
                        vao = -1;
                    }

                    continue;
                }

                var requestMaterial = request.Call.Material;

                var requestShader = context.ReplacementShader?.WithSkinning(request.Mesh.ActiveSkinning) ?? requestMaterial.Shader;

                if (material != requestMaterial || shader != requestShader)
                {
                    counters.Count(Counter.MaterialChange);

                    if (context.ReplacementShader?.IgnoreMaterialData != true)
                    {
                        material?.PostRender();
                    }

                    if (shader != requestShader)
                    {
                        shader = requestShader;
                        uniforms = new Uniforms
                        {
                            AnimationData = shader.GetUniformLocation("uAnimationData"),
                            Transform = shader.GetUniformLocation("transform"),
                            IsInstancing = shader.GetUniformLocation("bIsInstancing"),
                            Tint = shader.GetUniformLocation("vTint"),
                        };

                        if (shader.Parameters.ContainsKey("S_SCENE_CUBEMAP_TYPE"))
                        {
                            uniforms.EnvmapTexture = shader.GetUniformLocation("g_tEnvironmentMap");
                        }

                        if (shader.Parameters.ContainsKey("F_MORPH_SUPPORTED"))
                        {
                            uniforms.MorphCompositeTextureSize = shader.GetUniformLocation("morphCompositeTextureSize");
                            uniforms.MorphVertexIdOffset = shader.GetUniformLocation("morphVertexIdOffset");
                        }

                        if (shader.Parameters.ContainsKey("D_BAKED_LIGHTING_FROM_PROBE"))
                        {
                            uniforms.LPVIrradianceTexture = shader.GetUniformLocation("g_tLPV_Irradiance");
                        }

                        if (shader.Name == "picking")
                        {
                            uniforms.MeshId = shader.GetUniformLocation("meshId");
                            uniforms.ShaderId = shader.GetUniformLocation("shaderId");
                            uniforms.ShaderProgramId = shader.GetUniformLocation("shaderProgramId");
                        }

                        // The list goes in for the shaders that are drawn without a material at all: a
                        // replacement shader with IgnoreMaterialData set never reaches
                        // RenderMaterial.Render, so this is the only bind of its constant buffer.
                        shader.Use(commandList);

                        Debug.Assert(context.Scene.InstanceBufferGpu != null && context.Scene.TransformBufferGpu != null);
                        context.Scene.TransformBufferGpu.BindBufferBase();
                        context.Scene.InstanceBufferGpu.BindBufferBase();

                        context.Scene.TransformBufferGpu.BindBufferBase(ReservedBufferSlots.BoneTransforms);

                        if (config.IndirectDraw)
                        {
                            GL.ProgramUniform1((uint)shader.Program, uniforms.IsInstancing, 1);
                        }
                    }

                    material = requestMaterial;

                    // The list goes in so the material's constant buffer is bound as set 0 binding 7
                    // rather than only through OpenGL. Its texture binds still are not recorded from in
                    // there; those are restated below.
                    material.Render(shader, commandList);

                    rebindPipeline = true;

                    // Render's texture binds are OpenGL's own and record nothing, so a backend that binds
                    // by descriptor set gets set 3 restated. Deferred to after the pipeline bind below
                    // rather than done here: a descriptor write is validated against the layout of the
                    // pipeline that is bound when it happens, so writing this material's slots while the
                    // previous draw's pipeline is still bound checks them against the wrong set 3 and
                    // rejects any slot that one does not declare.
                    rebindMaterialTextures = true;
                }

                var requestVao = request.Call.GetVertexArrayObject();

                VertexArray.Validate(requestVao, shader!);

                if (vao != requestVao)
                {
                    vao = requestVao;
                    counters.Count(Counter.VaoChange);
                    rebindPipeline = true;

                    // When recording, geometry is bound through the command list instead: the vertex
                    // array comes from the pipeline's vertex input, so this mesh's own VAO is not what
                    // fetches its attributes. It is still resolved above, because it is what keys the
                    // change detection and what VertexArray.Validate checks against the shader.
                    if (config.CommandList == null)
                    {
                        GL.BindVertexArray(vao);
                    }
                }

                if (config.CommandList != null && rebindPipeline)
                {
                    BindPipelineAndGeometry(shader!, material!, request.Call, ref config);
                    rebindPipeline = false;
                }

                // After the pipeline, so the writes are checked against the set 3 this draw actually
                // reads. See where the flag is set.
                if (commandList != null && rebindMaterialTextures)
                {
                    BindMaterialTextures(commandList, material!, shader!, materialTextures!);
                    rebindMaterialTextures = false;
                }

                Draw(shader!, ref uniforms, ref config, new(request.Mesh, request.Call, request.Node));
            }

            if (vao > -1)
            {
                material!.PostRender();
            }
        }

        /// <summary>
        /// Records this material's own textures into <see cref="DescriptorSets.MaterialTextures"/>.
        /// </summary>
        /// <param name="commandList">The command list to record into.</param>
        /// <param name="material">The material whose textures are being bound.</param>
        /// <param name="shader">The shader being drawn with, which may be a replacement rather than the
        /// material's own; it is what decides which samplers exist and where they live.</param>
        /// <param name="bindings">Scratch list, cleared by the collect.</param>
        /// <remarks>
        /// <para>
        /// The slots are the shader's, not a count: <see cref="RenderMaterial.CollectTextureBindings"/>
        /// reads them out of the SPIR-V when there is any, so a sampler the compiler dropped leaves its
        /// number unused instead of shifting every later texture down one. On OpenGL there is no module
        /// and it falls back to the same walk <see cref="RenderMaterial.Render"/> makes, which is what
        /// keeps the recorded units identical to the ones that path binds directly.
        /// </para>
        /// <para>
        /// Each binding carries its own sampler rather than defaulting, because a recorded bind settles
        /// the sampler either way and defaulting would drop the material's
        /// <c>g_nTextureAddressModeU</c>/<c>V</c> back to repeat. See
        /// <see cref="RenderMaterial.SamplerFor"/>.
        /// </para>
        /// </remarks>
        private static void BindMaterialTextures(ICommandList commandList, RenderMaterial material, Shader shader, List<RenderMaterial.TextureBinding> bindings)
        {
            material.CollectTextureBindings(shader, bindings);

            foreach (var binding in bindings)
            {
                commandList.BindTexture(binding.DescriptorSet, binding.Binding, binding.Texture.RhiTexture, material.SamplerFor(binding));
            }
        }

        /// <summary>
        /// Binds the pipeline this draw call needs and the geometry it fetches from, for the recording path.
        /// </summary>
        /// <remarks>
        /// Called on the same three changes that make the OpenGL path rebind, so a run of draws sharing a
        /// material and a mesh costs one pipeline bind between them rather than one each.
        /// </remarks>
        private static void BindPipelineAndGeometry(Shader shader, RenderMaterial material, DrawCall call, ref Config config)
        {
            var commandList = config.CommandList!;
            var vertexBuffers = VertexBuffersWithDefaults(call);

            // The state the pipeline bakes has to be the state the OpenGL path applies, or the two
            // backends stop being each other's oracle. A material-ignoring replacement shader applies
            // none and draws under the pass baseline the batch scope latched; every other material
            // composes its own over that baseline, which is exactly what RenderMaterial.Render applies.
            var passState = config.RenderState!.CurrentPass;
            var state = shader.IgnoreMaterialData ? passState : material.GetRenderState(in passState);

            var pipeline = GLRendererDevice.PipelineFor(
                config.Device!,
                shader,
                in state,
                DescribeVertexInput(call, vertexBuffers),
                ToTopology(call.PrimitiveType),
                config.ColorFormats,
                config.DepthFormat,
                config.SampleCount,
                GLRendererDevice.DrawConstants);

            commandList.BindPipeline(pipeline);

            var meshBuffers = call.MeshBuffers;

            for (var binding = 0; binding < vertexBuffers.Length; binding++)
            {
                // Offset zero, matching the vertex array: a draw call's own Offset is folded into the
                // attribute offsets, not the buffer binding.
                commandList.BindVertexBuffer(binding, meshBuffers.GetRhiBuffer(vertexBuffers[binding]));
            }

            if (call.IndexBuffer.HasBuffer)
            {
                var (indexType, _) = GPUMeshBufferCache.DescribeIndexedDraw(call);
                commandList.BindIndexBuffer(meshBuffers.GetRhiBuffer(call.IndexBuffer), indexType);
            }
        }

        private static readonly VBIB.RenderInputLayoutField[] DefaultColorLayout =
        [
            new VBIB.RenderInputLayoutField
            {
                SemanticName = "COLOR",
                Format = DXGI_FORMAT.R32G32B32A32_FLOAT,
            },
        ];

        /// <summary>
        /// Returns the draw call's vertex buffers, with the default white COLOR stream appended when the
        /// mesh has none.
        /// </summary>
        /// <remarks>
        /// :VertexInputParity - mirrors <c>GPUMeshBufferCache.AddMissingAttributes</c>, which does the same
        /// for the vertex array the OpenGL path fetches through. The two have to agree: a mesh whose COLOR
        /// stream one path substitutes and the other does not renders with different vertex colours. The
        /// substitute buffer has a stride of zero on purpose, which is what makes its single value apply to
        /// every vertex.
        /// </remarks>
        private static VertexDrawBuffer[] VertexBuffersWithDefaults(DrawCall call)
        {
            foreach (var buffer in call.VertexBuffers)
            {
                foreach (var field in buffer.InputLayoutFields)
                {
                    if (field.SemanticName == "COLOR")
                    {
                        return call.VertexBuffers;
                    }
                }
            }

            // Named as an RHI buffer rather than as a handle: this is only ever reached from the recording
            // path, and asking for the handle would create an OpenGL buffer on whatever device is live.
            return [.. call.VertexBuffers, new VertexDrawBuffer
            {
                RhiBuffer = call.MeshBuffers.VectorOneRhiBuffer,
                ElementSizeInBytes = 0,
                InputLayoutFields = DefaultColorLayout,
            }];
        }

        /// <summary>
        /// Describes a draw call's geometry as pipeline vertex input state.
        /// </summary>
        /// <remarks>
        /// :VertexInputParity - mirrors <c>GPUMeshBufferCache.CreateVertexArrayObject</c> attribute for
        /// attribute, because on the recording path this replaces it: the vertex array a draw fetches
        /// through is built from the pipeline's vertex input, not from the mesh's own VAO. It resolves
        /// locations the same way, skips the same attributes, and takes the same first-wins rule for a
        /// location two buffers both claim, which the alias table allows.
        /// </remarks>
        private static VertexInputDesc DescribeVertexInput(DrawCall call, VertexDrawBuffer[] vertexBuffers)
        {
            var inputSignature = call.Material.Material.InputSignature;
            var bindings = new VertexBindingDesc[vertexBuffers.Length];
            var attributes = new List<VertexAttributeDesc>();
            var boundLocations = 0;

            for (var binding = 0; binding < vertexBuffers.Length; binding++)
            {
                var buffer = vertexBuffers[binding];
                bindings[binding] = new VertexBindingDesc(binding, (int)buffer.ElementSizeInBytes);

                foreach (var attribute in buffer.InputLayoutFields)
                {
                    var location = VertexAttributeLocations.Resolve(inputSignature, attribute, out _);

                    // Unknown, or a location an earlier buffer already took
                    if (location == -1 || (boundLocations & (1 << location)) != 0)
                    {
                        continue;
                    }

                    boundLocations |= 1 << location;

                    attributes.Add(new VertexAttributeDesc(
                        location,
                        FormatTables.FromDxgiFormat(attribute.Format),
                        (int)attribute.Offset,
                        binding));
                }
            }

            return new VertexInputDesc([.. attributes], bindings);
        }

        private static PrimitiveTopology ToTopology(PrimitiveType primitiveType) => primitiveType switch
        {
            PrimitiveType.Points => PrimitiveTopology.PointList,
            PrimitiveType.Lines => PrimitiveTopology.LineList,
            PrimitiveType.LineStrip => PrimitiveTopology.LineStrip,
            PrimitiveType.Triangles => PrimitiveTopology.TriangleList,
            PrimitiveType.TriangleStrip => PrimitiveTopology.TriangleStrip,
            _ => throw new NotSupportedException($"Primitive type {primitiveType} has no {nameof(PrimitiveTopology)} member."),
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Draw(Shader shader, ref Uniforms uniforms, ref Config config, BatchRequest request)
        {
            // The per-draw block, filled here in full and pushed once just before the draw. Every field is
            // written on every draw rather than only where its OpenGL twin is: a loose uniform a draw does
            // not touch keeps the value the program object already holds, whereas the block is a single
            // range that is pushed whole, so a field left unwritten would carry the previous draw's value
            // into this one. See :DrawPushConstantParity.
            ref var constants = ref config.PushConstants;

            constants.MeshId = (uint)request.Mesh.MeshIndex;
            constants.ShaderId = request.Call.Material.Shader.NameHash;
            constants.ShaderProgramId = (uint)request.Call.Material.Shader.Program;

            if (uniforms.MeshId != -1)
            {
                GL.ProgramUniform1((uint)shader.Program, uniforms.MeshId, (uint)request.Mesh.MeshIndex);
                GL.ProgramUniform1((uint)shader.Program, uniforms.ShaderId, request.Call.Material.Shader.NameHash);
                GL.ProgramUniform1((uint)shader.Program, uniforms.ShaderProgramId, (uint)request.Call.Material.Shader.Program);
            }

            constants.SetAnimationData(false);

            if (uniforms.AnimationData != -1)
            {
                var bAnimated = request.Mesh.BoneMatricesGpu != null;
                var numBones = 0u;
                var boneStart = 0u;

                // BoneTransforms is per-draw, not per-scene: a skinned mesh overrides the slot with its
                // own matrices and an unskinned one puts the scene's transforms back. Recording only the
                // scene-wide fallback satisfies the draw-time guard for every draw -- set 1 is bound
                // either way -- while leaving a skinned model on Vulkan reading node transforms as bone
                // matrices. That renders wrong rather than failing, so nothing but this catches it.
                if (bAnimated)
                {
                    var boneMatrices = request.Mesh.BoneMatricesGpu!;

                    boneMatrices.BindBufferBase();
                    config.CommandList?.BindStorageBuffer((int)ReservedBufferSlots.BoneTransforms, boneMatrices.RhiBuffer);

                    numBones = (uint)request.Mesh.MeshBoneCount;
                    boneStart = (uint)request.Mesh.MeshBoneOffset;
                }
                else
                {
                    // todo: this is not resetting when there are no aggregates in scene
                    var transforms = request.Node.Scene.TransformBufferGpu;

                    transforms?.BindBufferBase(ReservedBufferSlots.BoneTransforms);

                    if (transforms != null)
                    {
                        config.CommandList?.BindStorageBuffer((int)ReservedBufferSlots.BoneTransforms, transforms.RhiBuffer);
                    }
                }

                constants.SetAnimationData(bAnimated, (int)boneStart, (int)numBones);

                GL.ProgramUniform3((uint)shader.Program, uniforms.AnimationData, bAnimated ? 1u : 0u, boneStart, numBones);
            }

            // Ahead of the indirect branch, which returns without reaching the writes further down. An
            // indirect aggregate draws instanced and reads its transform and tint out of the object buffer
            // instead, but the block is pushed whole either way, so these have to hold this draw's values
            // rather than the last non-indirect draw's.
            var morphComposite = request.Mesh.FlexStateManager?.MorphComposite;

            constants.MorphVertexIdOffset = morphComposite != null ? request.Call.VertexIdOffset : -1;
            constants.MorphCompositeTextureSize = morphComposite != null
                ? new Vector2(morphComposite.CompositeTexture.Width, morphComposite.CompositeTexture.Height)
                : Vector2.Zero;

            constants.SetTransform(request.Node.Transform);

            // Content can author out-of-range tints (e.g. renderamt above 255 baked into the draw call
            // alpha); the packed byte color can only represent [0, 1].
            var fragmentTint = (request.Node is SceneAggregate.Fragment tinted) ? tinted.Tint : Vector4.One;

            constants.Tint = Color32.FromVector4Clamped(
                request.Mesh.Tint * request.Call.TintColor * fragmentTint).PackedValue;

            if (config.IndirectDraw)
            {
                if (request.Node is SceneAggregate agg && agg.IndirectDrawCount > 0)
                {
                    // Non-indirect draws below reset this program uniform
                    if (uniforms.IsInstancing > -1)
                    {
                        GL.ProgramUniform1((uint)shader.Program, uniforms.IsInstancing, 1);
                    }

                    constants.IsInstancing = 1;
                    config.CommandList?.SetPushConstants(in constants);

                    PerfStats.Active.CountIndirectDraw(agg.IndirectDrawCount);

                    var scene = agg.Scene;
                    if (scene.CompactMeshletDraws && agg.CompactionIndex >= 0)
                    {
                        if (config.CommandList != null)
                        {
                            Debug.Assert(scene.CompactedDrawsGpu != null && scene.CompactedCountsGpu != null);

                            config.CommandList.DrawIndexedIndirectCount(
                                scene.CompactedDrawsGpu.RhiBuffer,
                                agg.IndirectDrawByteOffset,
                                scene.CompactedCountsGpu.RhiBuffer,
                                agg.CompactionIndex * sizeof(uint),
                                agg.IndirectDrawCount,
                                0);
                            return;
                        }

                        GL.MultiDrawElementsIndirectCount(
                            request.Call.PrimitiveType,
                            request.Call.IndexType,
                            agg.IndirectDrawByteOffset,
                            agg.CompactionIndex * sizeof(uint), // drawcount buffer offset
                            agg.IndirectDrawCount, // maxdrawcount
                            0); // stride
                        return;
                    }

                    if (config.CommandList != null)
                    {
                        // Whichever buffer Scene bound to GL_DRAW_INDIRECT_BUFFER for this frame. The
                        // compacted one is bound whenever compaction is on, even for an aggregate that
                        // has no compaction slot and so takes this uncounted path.
                        var arguments = scene.CompactMeshletDraws ? scene.CompactedDrawsGpu : scene.IndirectDrawsGpu;
                        Debug.Assert(arguments != null);

                        config.CommandList.DrawIndexedIndirect(arguments.RhiBuffer, agg.IndirectDrawByteOffset, agg.IndirectDrawCount, 0);
                        return;
                    }

                    GL.MultiDrawElementsIndirect(request.Call.PrimitiveType, request.Call.IndexType, agg.IndirectDrawByteOffset, agg.IndirectDrawCount, 0);
                    return;
                }
            }

            if (config.NeedsCubemapBinding && uniforms.EnvmapTexture != -1 && request.Node.EnvMaps.Count > 0)
            {
                var envmap = request.Node.EnvMaps[0];
                BindReservedTexture(config.CommandList, ReservedTextureSlots.EnvironmentMap, envmap.EnvMapTexture);
            }

            if (config.LightProbeType == LightProbeType.IndividualProbes && uniforms.LPVIrradianceTexture != -1
                && request.Node.LightProbeBinding is { } lightProbe)
            {
                request.Node.Scene.LightingInfo.BindInstanceLightProbeTextures(config.CommandList, lightProbe);
            }

            if (uniforms.MorphVertexIdOffset != -1)
            {
                if (morphComposite != null)
                {
                    BindReservedTexture(config.CommandList, ReservedTextureSlots.MorphCompositeTexture, morphComposite.CompositeTexture);
                    GL.ProgramUniform2(shader.Program, uniforms.MorphCompositeTextureSize, (float)morphComposite.CompositeTexture.Width, morphComposite.CompositeTexture.Height);
                }

                GL.ProgramUniform1(shader.Program, uniforms.MorphVertexIdOffset, morphComposite != null ? request.Call.VertexIdOffset : -1);
            }

            if (uniforms.Transform > -1)
            {
                var transform = request.Node.Transform.To3x4();
                GL.ProgramUniformMatrix3x4(shader.Program, uniforms.Transform, false, ref transform);

                // The two spellings of the same 3x4, and the block has to agree with the loose uniform for
                // the recorded OpenGL path to issue identical calls. GLEnvironment.To3x4 and
                // DrawPushConstants.SetTransform drop the same column in the same order; this is where that
                // stops being a coincidence.
                Debug.Assert(constants.TransformRow0 == new Vector4(transform.Row0.X, transform.Row0.Y, transform.Row0.Z, transform.Row0.W));
            }

            if (uniforms.Tint > -1)
            {
                GL.ProgramUniform1((uint)shader.Program, uniforms.Tint, constants.Tint);
            }

            var instanceCount = 1;

            if (request.Node is SceneAggregate { InstanceTransforms.Count: > 0 } aggregate)
            {
                instanceCount = aggregate.InstanceTransforms.Count;
            }

            constants.IsInstancing = instanceCount > 1 ? 1 : 0;

            if (uniforms.IsInstancing > -1)
            {
                GL.ProgramUniform1((uint)shader.Program, uniforms.IsInstancing, instanceCount > 1 ? 1 : 0);
            }

            PerfStats.Active.CountDrawCall(request.Node);

            if (config.CommandList != null)
            {
                // Last, so the block a draw executes with is the one filled for it. Without this the
                // object-to-world transform never reaches a Vulkan draw at all -- push constants start
                // undefined, the shaders read a zero mat3x4 out of the block, every vertex collapses onto
                // the world origin, and a frame that records the right draws in the right passes rasterises
                // nothing. That failure has no error attached to it: validation is silent, the pass still
                // clears, and the depth buffer stays at the value it was cleared to. See
                // :DrawPushConstantParity.
                config.CommandList.SetPushConstants(in constants);

                // StartIndex is a byte offset because that is the pointer glDrawElements takes, while
                // firstIndex is an element count. DescribeIndexedDraw converts it, and returns the index
                // type with it so the two cannot be applied by halves.
                var (_, firstIndex) = GPUMeshBufferCache.DescribeIndexedDraw(request.Call);

                // The node id travels as the base instance. The contract types it signed and the backend
                // casts it back, so the bit pattern the shader reads as gl_BaseInstance is unchanged.
                config.CommandList.DrawIndexed(
                    request.Call.IndexCount,
                    instanceCount,
                    firstIndex,
                    request.Call.BaseVertex,
                    (int)request.Node.Id);
                return;
            }

            GL.DrawElementsInstancedBaseVertexBaseInstance(
                request.Call.PrimitiveType,
                request.Call.IndexCount,
                request.Call.IndexType,
                request.Call.StartIndex,
                instanceCount,
                request.Call.BaseVertex,
                request.Node.Id
            );
        }
    }
}
