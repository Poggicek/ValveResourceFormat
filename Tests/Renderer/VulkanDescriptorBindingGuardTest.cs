using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using ValveResourceFormat.Renderer.Buffers;
using ValveResourceFormat.Renderer.Materials;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.Vulkan.Descriptors;
using ValveResourceFormat.Renderer.Shaders;
using ValveResourceFormat.Renderer.Shaders.Spirv;

namespace Tests.Renderer
{
    /// <summary>
    /// The fuse that stops a draw whose pipeline declares a <i>binding</i> nothing wrote, inside a
    /// descriptor set that was bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The companion to <see cref="VulkanDescriptorSetGuardTest"/> one level down. That guard is set
    /// granular and passes as soon as anything at all was bound into a set;
    /// <c>VulkanDescriptorBinder.EnsureDeclaredBindingsBound</c> is binding granular but runs only for
    /// reflected set layouts. Every unwritten descriptor found in this port so far fell between the two:
    /// a canonical set — set 0's uniform buffers, set 2's reserved textures — that was bound, with one
    /// slot inside it that nothing filled.
    /// </para>
    /// <para>
    /// CPU side and touching no Vulkan device, for the reason the sibling file gives: the fault hangs the
    /// GPU, so it cannot be demonstrated by provoking it. What is demonstrated is that the masks are
    /// computed from real SPIR-V and that the comparison refuses the right cases and names them usefully.
    /// </para>
    /// </remarks>
    public class VulkanDescriptorBindingGuardTest
    {
        private static SpirvReflectionResult Stage(ShaderStage stage, params (int Set, int Binding, SpirvResourceKind Kind)[] bindings)
            => new()
            {
                Stage = stage,
                DescriptorBindings = [.. bindings.Select(b => new SpirvDescriptorBinding($"r{b.Set}_{b.Binding}", b.Set, b.Binding, b.Kind, 1, 0))],
            };

        /// <summary>A binder state that filled exactly the named bindings of one set and nothing else.</summary>
        private static uint[] Bound(params (int Set, int Binding)[] bindings)
        {
            var masks = new uint[DescriptorSets.Count];

            foreach (var (set, binding) in bindings)
            {
                masks[set] |= 1u << binding;
            }

            return masks;
        }

        /// <summary>Compiles one of the renderer's own shaders and returns every stage it declares.</summary>
        private static List<SpirvReflectionResult> RealShader(string shaderName)
        {
            var results = SpirvShaderValidation.CompileShader(shaderName, flavour: ShaderFlavour.Vulkan);

            Assert.That(results, Is.Not.Empty, $"'{shaderName}' produced no stages.");

            var reflections = new List<SpirvReflectionResult>();

            foreach (var stage in results)
            {
                Assert.That(stage.Result.Success, Is.True, $"'{shaderName}' [{stage.Stage}] did not compile: {stage.Result.FormatDiagnostics()}");
                Assert.That(stage.Reflection, Is.Not.Null);
                reflections.Add(stage.Reflection!);
            }

            return reflections;
        }

        // ---- the masks ----

        [Test]
        public void MaskNamesEveryBindingTheStagesDeclare()
        {
            var usage = VulkanDescriptorBindingUsage.For(Stage(ShaderStage.Compute,
                (DescriptorSets.UniformBuffers, 0, SpirvResourceKind.UniformBuffer),
                (DescriptorSets.UniformBuffers, 6, SpirvResourceKind.UniformBuffer),
                (DescriptorSets.StorageBuffers, 3, SpirvResourceKind.StorageBuffer),
                (DescriptorSets.StorageImages, 2, SpirvResourceKind.StorageImage)));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(usage.Masks[DescriptorSets.UniformBuffers], Is.EqualTo((1u << 0) | (1u << 6)));
                Assert.That(usage.Masks[DescriptorSets.StorageBuffers], Is.EqualTo(1u << 3));
                Assert.That(usage.Masks[DescriptorSets.ReservedTextures], Is.Zero);
                Assert.That(usage.Masks[DescriptorSets.MaterialTextures], Is.Zero);
                Assert.That(usage.Masks[DescriptorSets.StorageImages], Is.EqualTo(1u << 2));
            }
        }

        [Test]
        public void MaskIsTheUnionOfTheStages()
        {
            var stages = new List<SpirvReflectionResult>
            {
                Stage(ShaderStage.Vertex, (DescriptorSets.UniformBuffers, 0, SpirvResourceKind.UniformBuffer)),
                Stage(ShaderStage.Fragment, (DescriptorSets.UniformBuffers, 1, SpirvResourceKind.UniformBuffer)),
            };

            using (Assert.EnterMultipleScope())
            {
                Assert.That(VulkanDescriptorBindingUsage.For(stages).Masks[DescriptorSets.UniformBuffers],
                    Is.EqualTo((1u << 0) | (1u << 1)));

                // A stage a pipeline does not have declares nothing. Depth-only pipelines pass null here.
                Assert.That(VulkanDescriptorBindingUsage.For((SpirvReflectionResult?)null).Masks.ToArray(), Is.All.Zero);
                Assert.That(VulkanDescriptorBindingUsage.For(new List<SpirvReflectionResult>()).Masks.ToArray(), Is.All.Zero);
                Assert.That(VulkanDescriptorBindingUsage.None.Masks.ToArray(), Is.All.Zero);
            }
        }

        [Test]
        public void MaskLeavesOutWhatNothingCouldEverBind()
        {
            // A set past the contract's scheme is already reported by ValidateDescriptorSets, no layout
            // declares it and no call can bind it, so counting it would make the guard throw forever.
            // A binding past the mask is the same story for the other axis; only set 3 can reach one, and
            // set 3 is checked exactly by the binder's own per-layout check instead.
            var usage = VulkanDescriptorBindingUsage.For(Stage(ShaderStage.Compute,
                (DescriptorSets.UniformBuffers, 0, SpirvResourceKind.UniformBuffer),
                (DescriptorSets.Count + 4, 0, SpirvResourceKind.StorageBuffer),
                (DescriptorSets.MaterialTextures, VulkanDescriptorBindingUsage.MaskWidth, SpirvResourceKind.CombinedImageSampler),
                (DescriptorSets.MaterialTextures, VulkanDescriptorBindingUsage.MaskWidth + 9, SpirvResourceKind.CombinedImageSampler)));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(usage.Masks[DescriptorSets.UniformBuffers], Is.EqualTo(1u));
                Assert.That(usage.Masks[DescriptorSets.MaterialTextures], Is.Zero);

                Assert.That(() => VulkanDescriptorBindingUsage.EnsureBound("out_of_range", usage, Bound((DescriptorSets.UniformBuffers, 0))),
                    Throws.Nothing);
            }
        }

        [Test]
        public void EveryCanonicalSetIsNarrowEnoughForTheMask()
        {
            // The one assumption the guard rests on that an edit elsewhere could break: adding a slot to
            // ReservedBufferSlots or ReservedTextureSlots widens the canonical layouts automatically and
            // the mask not at all, and a binding that falls out of the mask falls out of the guard.
            Assert.That(VulkanDescriptorBindingUsage.EnsureCanonicalSetsFitTheMask(), Is.Null);
        }

        // ---- the six known misses ----

        [Test]
        public void TheSunShadowPassBufferMissesAreEachNamed()
        {
            // The shape of the miss: a caster with vertex animation or alpha test is routed to
            // DepthOnlyBucket.MaterialShader and drawn with its own full shading shader, which declares
            // the lighting buffers whether the shadow pass has any use for them or not. The pass bound
            // only the view constants, so set 0 was bound and the set-granularity guard passed.
            var stages = RealShader("complex");
            var usage = VulkanDescriptorBindingUsage.For(stages);

            // Grouped rather than keyed directly: a block declared by both the vertex and the fragment
            // stage appears once per stage, which is the merge the layout builder does too.
            var declared = stages
                .SelectMany(s => s.DescriptorBindings)
                .Where(b => b.Set == DescriptorSets.UniformBuffers)
                .GroupBy(b => b.Name)
                .ToDictionary(g => g.Key, g => g.First().Binding);

            using (Assert.EnterMultipleScope())
            {
                // Real SPIR-V really does declare them, at the slots the renderer records them at.
                Assert.That(declared["LightingConstants"], Is.EqualTo((int)ReservedBufferSlots.Lighting));
                Assert.That(declared["EnvMapArray"], Is.EqualTo((int)ReservedBufferSlots.EnvironmentMap));
                Assert.That(declared["LightCullConstants"], Is.EqualTo((int)ReservedBufferSlots.LightCull));

                Assert.That(() => VulkanDescriptorBindingUsage.EnsureBound(
                        "complex",
                        usage,
                        Bound((DescriptorSets.UniformBuffers, (int)ReservedBufferSlots.View))),
                    Throws.InstanceOf<InvalidOperationException>()
                        .With.Message.Contains("complex")
                        .And.Message.Contains("LightingConstants")
                        .And.Message.Contains("EnvMapArray")
                        .And.Message.Contains("LightCullConstants")
                        // and says what fills each, not only that something is missing
                        .And.Message.Contains(nameof(ICommandList.BindUniformBuffer))
                        .And.Message.Contains(nameof(ReservedBufferSlots.Lighting)));
            }
        }

        [Test]
        public void TheDroppedShadowMapIsNamed()
        {
            // g_tShadowDepthBufferDepth: bound by the pass and then dropped, because inside the sun shadow
            // pass the atlas it names is an attachment and binding it would be a feedback loop. Set 2 is
            // still bound -- the rest of the reserved textures are there -- so only a binding-granular
            // check can see it.
            var stages = RealShader("complex");
            var usage = VulkanDescriptorBindingUsage.For(stages);

            var shadow = stages
                .SelectMany(s => s.DescriptorBindings)
                .First(b => b.Name == "g_tShadowDepthBufferDepth");

            var everythingElse = new uint[DescriptorSets.Count];

            for (var set = 0; set < DescriptorSets.Count; set++)
            {
                everythingElse[set] = usage.Masks[set];
            }

            everythingElse[DescriptorSets.ReservedTextures] &= ~(1u << (int)ReservedTextureSlots.ShadowDepthBufferDepth);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(shadow.Set, Is.EqualTo(DescriptorSets.ReservedTextures));
                Assert.That(shadow.Binding, Is.EqualTo((int)ReservedTextureSlots.ShadowDepthBufferDepth));

                Assert.That(() => VulkanDescriptorBindingUsage.EnsureBound("complex", usage, everythingElse),
                    Throws.InstanceOf<InvalidOperationException>()
                        .With.Message.Contains("g_tShadowDepthBufferDepth")
                        .And.Message.Contains($"Set {DescriptorSets.ReservedTextures} binding {(int)ReservedTextureSlots.ShadowDepthBufferDepth}")
                        .And.Message.Contains(nameof(ReservedTextureSlots.ShadowDepthBufferDepth))
                        .And.Message.Contains(nameof(ICommandList.BindTexture)));

                // And with it bound, nothing is refused.
                Assert.That(() => VulkanDescriptorBindingUsage.EnsureBound("complex", usage, usage.Masks.ToArray()), Throws.Nothing);
            }
        }

        /// <summary>
        /// The morph composite slot, which is the one that crashed the display driver.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Compiled here rather than taken from <c>SpirvShaderValidation.CompileShader</c>, which has no
        /// way to pass a combo: <c>common/morph.slang</c> defines <c>F_MORPH_SUPPORTED</c> to 0 and guards
        /// the sampler declaration behind it, so <i>no</i> shader compiled with the default combos declares
        /// <c>morphCompositeTexture</c> at all. That is worth knowing on its own — it means the golden
        /// suite cannot reach this miss, and removing the floor bind in
        /// <c>MeshBatchRenderer.BindReservedTextures</c> changes nothing in a golden run.
        /// </para>
        /// <para>
        /// The module is still real SPIR-V through the real compiler and reflector, and the binding number
        /// is read from the enum rather than written out, so the pair this checks is the one that matters:
        /// what a morph-enabled shader declares against the slot the renderer binds.
        /// </para>
        /// </remarks>
        [Test]
        public void TheMorphCompositeSlotIsNamed()
        {
            var slot = (int)ReservedTextureSlots.MorphCompositeTexture;

            var source = string.Create(CultureInfo.InvariantCulture, $$"""
                #version 460
                layout(set = {{DescriptorSets.ReservedTextures}}, binding = {{slot}}) uniform sampler2D morphCompositeTexture;
                layout(set = {{DescriptorSets.ReservedTextures}}, binding = {{(int)ReservedTextureSlots.BRDFLookup}}) uniform sampler2D g_tBRDFLookup;
                layout(location = 0) out vec4 outColor;
                void main()
                {
                    outColor = texture(morphCompositeTexture, vec2(0.5)) + texture(g_tBRDFLookup, vec2(0.5));
                }
                """);

            using var compiler = new SpirvCompiler();

            var compiled = compiler.Compile(source, ShaderStage.Fragment);

            Assert.That(compiled.Success, Is.True, compiled.FormatDiagnostics());

            var usage = VulkanDescriptorBindingUsage.For(SpirvReflection.Reflect(compiled.Spirv.Span));

            // Every reserved texture the module declares is bound except the morph composite, which is what
            // a draw of a mesh with no composite looked like before the floor bind existed.
            var bound = Bound((DescriptorSets.ReservedTextures, (int)ReservedTextureSlots.BRDFLookup));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(usage.Masks[DescriptorSets.ReservedTextures], Is.EqualTo((1u << slot) | 1u));
                Assert.That(usage.NameOf(DescriptorSets.ReservedTextures, slot), Is.EqualTo("morphCompositeTexture"));

                Assert.That(() => VulkanDescriptorBindingUsage.EnsureBound("vr_skin (F_MORPH_SUPPORTED)", usage, bound),
                    Throws.InstanceOf<InvalidOperationException>()
                        .With.Message.Contains("vr_skin (F_MORPH_SUPPORTED)")
                        .And.Message.Contains("morphCompositeTexture")
                        .And.Message.Contains($"Set {DescriptorSets.ReservedTextures} binding {slot}")
                        .And.Message.Contains(nameof(ReservedTextureSlots.MorphCompositeTexture)));

                // The slot the guard names is the one MeshBatchRenderer binds the white floor into.
                Assert.That(() => VulkanDescriptorBindingUsage.EnsureBound(
                        "vr_skin (F_MORPH_SUPPORTED)",
                        usage,
                        Bound(
                            (DescriptorSets.ReservedTextures, (int)ReservedTextureSlots.BRDFLookup),
                            (DescriptorSets.ReservedTextures, slot))),
                    Throws.Nothing);
            }
        }

        // ---- the comparison ----

        [Test]
        public void ABoundBindingIsAllowed()
        {
            var usage = VulkanDescriptorBindingUsage.For(Stage(ShaderStage.Fragment,
                (DescriptorSets.UniformBuffers, 0, SpirvResourceKind.UniformBuffer),
                (DescriptorSets.ReservedTextures, 3, SpirvResourceKind.CombinedImageSampler)));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(() => VulkanDescriptorBindingUsage.EnsureBound("mesh", usage,
                    Bound((DescriptorSets.UniformBuffers, 0), (DescriptorSets.ReservedTextures, 3))), Throws.Nothing);

                // More bound than declared is not a fault: the reserved globals are bound once per pass for
                // every pipeline in it, including the ones that read almost none of them.
                var everything = new uint[DescriptorSets.Count];
                everything.AsSpan().Fill(uint.MaxValue);

                Assert.That(() => VulkanDescriptorBindingUsage.EnsureBound("mesh", usage, everything), Throws.Nothing);

                // A pipeline that declares no descriptors at all draws with nothing bound.
                Assert.That(() => VulkanDescriptorBindingUsage.EnsureBound("fullscreen", VulkanDescriptorBindingUsage.None, default),
                    Throws.Nothing);

                // A shorter bound span means the rest are empty rather than being an error.
                Assert.That(() => VulkanDescriptorBindingUsage.EnsureBound("mesh", usage, new uint[1]),
                    Throws.InstanceOf<InvalidOperationException>());
            }
        }

        [Test]
        public void EverySetSaysHowToBindOneOfItsBindings()
        {
            for (var set = 0; set < DescriptorSets.Count; set++)
            {
                Assert.That(VulkanDescriptorBindingUsage.BoundBy(set, 0), Does.Contain("Bind"),
                    $"Set {set} does not name the call that fills a binding.");
            }
        }
    }
}
