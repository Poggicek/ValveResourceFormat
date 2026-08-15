using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.Vulkan.Descriptors;
using ValveResourceFormat.Renderer.Shaders;
using ValveResourceFormat.Renderer.Shaders.Spirv;

namespace Tests.Renderer
{
    /// <summary>
    /// The fuse that stops a draw whose pipeline uses a descriptor set nothing bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything here is CPU side and touches no Vulkan device, deliberately. The fault this guard
    /// stands in front of hangs the GPU and can bugcheck the machine, so it cannot be demonstrated by
    /// provoking it; what can be demonstrated is that the two halves of the comparison are computed
    /// correctly and that the comparison refuses the right cases.
    /// </para>
    /// <para>
    /// The mask half is checked against real SPIR-V. <see cref="SpirvShaderValidation.CompileShader"/>
    /// preprocesses the renderer's own sources through the real emitter and compiles them with shaderc,
    /// which is where the set decorations actually come from. A synthetic
    /// <see cref="SpirvReflectionResult"/> would only prove that this test and the guard agree with each
    /// other.
    /// </para>
    /// </remarks>
    public class VulkanDescriptorSetGuardTest
    {
        private static int MaskOf(params int[] sets)
        {
            var mask = 0;

            foreach (var set in sets)
            {
                mask |= 1 << set;
            }

            return mask;
        }

        private static SpirvReflectionResult Stage(ShaderStage stage, params (int Set, int Binding, SpirvResourceKind Kind)[] bindings)
            => new()
            {
                Stage = stage,
                DescriptorBindings = [.. bindings.Select(b => new SpirvDescriptorBinding($"r{b.Set}_{b.Binding}", b.Set, b.Binding, b.Kind, 1, 0))],
            };

        /// <summary>Compiles one of the renderer's own shaders and returns the single stage it declares.</summary>
        private static SpirvReflectionResult RealShader(string shaderName)
        {
            var results = SpirvShaderValidation.CompileShader(shaderName, flavour: ShaderFlavour.Vulkan);

            Assert.That(results, Is.Not.Empty, $"'{shaderName}' produced no stages.");

            var stage = results[0];

            Assert.That(stage.Result.Success, Is.True, $"'{shaderName}' did not compile: {stage.Result.FormatDiagnostics()}");
            Assert.That(stage.Reflection, Is.Not.Null);

            return stage.Reflection!;
        }

        // ---- the mask, over synthetic reflections ----

        [Test]
        public void MaskNamesEverySetTheStagesDeclare()
        {
            var stage = Stage(ShaderStage.Compute,
                (DescriptorSets.UniformBuffers, 0, SpirvResourceKind.UniformBuffer),
                (DescriptorSets.StorageBuffers, 3, SpirvResourceKind.StorageBuffer),
                (DescriptorSets.MaterialTextures, 0, SpirvResourceKind.CombinedImageSampler));

            Assert.That(VulkanDescriptorSetUsage.MaskFor(stage), Is.EqualTo(MaskOf(0, 1, 3)));
        }

        [Test]
        public void MaskIsTheUnionOfTheStages()
        {
            var vertex = Stage(ShaderStage.Vertex, (DescriptorSets.StorageBuffers, 0, SpirvResourceKind.StorageBuffer));
            var fragment = Stage(ShaderStage.Fragment, (DescriptorSets.MaterialTextures, 0, SpirvResourceKind.CombinedImageSampler));

            var stages = new List<SpirvReflectionResult> { vertex, fragment };

            using (Assert.EnterMultipleScope())
            {
                Assert.That(VulkanDescriptorSetUsage.MaskFor(stages), Is.EqualTo(MaskOf(1, 3)));

                // A stage a pipeline does not have is not a set it uses. Depth-only pipelines pass null here.
                Assert.That(VulkanDescriptorSetUsage.MaskFor((SpirvReflectionResult?)null), Is.EqualTo(VulkanDescriptorSetUsage.NoSets));
                Assert.That(VulkanDescriptorSetUsage.MaskFor(new List<SpirvReflectionResult>()), Is.EqualTo(VulkanDescriptorSetUsage.NoSets));
            }
        }

        [Test]
        public void MaskLeavesOutASetNoLayoutCanDeclare()
        {
            // Set 9 is past the contract's scheme. SpirvReflection.ValidateDescriptorSets already reports
            // it, no pipeline layout declares it, and no call can bind it -- counting it would make the
            // guard throw on every draw of that pipeline forever.
            var stage = Stage(ShaderStage.Compute,
                (DescriptorSets.UniformBuffers, 0, SpirvResourceKind.UniformBuffer),
                (DescriptorSets.Count + 4, 0, SpirvResourceKind.StorageBuffer));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(VulkanDescriptorSetUsage.MaskFor(stage), Is.EqualTo(MaskOf(DescriptorSets.UniformBuffers)));
                Assert.That(VulkanDescriptorSetUsage.MaskFor(stage) & ~VulkanDescriptorSetUsage.AllSets, Is.Zero);
            }
        }

        // ---- the mask, over real SPIR-V ----

        [Test]
        public void DepthPyramidReachesTheUniformBuffersAndTheStorageImages()
        {
            var reflection = RealShader("depth_pyramid");
            var mask = VulkanDescriptorSetUsage.MaskFor(reflection);

            // The shader that settled the set 4 argument: its images address image units, a third index
            // space, and they sit alongside the view constants in one module. A mask read off descriptor
            // kinds rather than off the decorations would put them in set 2 and demand the wrong set.
            using (Assert.EnterMultipleScope())
            {
                Assert.That(mask, Is.EqualTo(MaskOf(DescriptorSets.UniformBuffers, DescriptorSets.StorageImages)),
                    $"depth_pyramid reflected as {string.Join(", ", reflection.DescriptorBindings.Select(b => $"set {b.Set} binding {b.Binding} {b.Kind}"))}");

                Assert.That(VulkanDescriptorSetUsage.Uses(mask, DescriptorSets.StorageImages), Is.True);
                Assert.That(VulkanDescriptorSetUsage.Uses(mask, DescriptorSets.ReservedTextures), Is.False);

                // Every storage image really is decorated into set 4, which is what the mask is echoing.
                Assert.That(reflection.DescriptorBindings.Where(b => b.Kind == SpirvResourceKind.StorageImage).Select(b => b.Set),
                    Is.All.EqualTo(DescriptorSets.StorageImages));
            }
        }

        [Test]
        public void HistogramCountsAStd140StorageBufferAsAStorageBuffer()
        {
            var reflection = RealShader("histogram");
            var mask = VulkanDescriptorSetUsage.MaskFor(reflection);

            var luminance = reflection.DescriptorBindings.Single(b => b.Name == "Luminance");

            using (Assert.EnterMultipleScope())
            {
                // 'layout(binding = 3, std140) buffer Luminance' -- a storage buffer wearing a uniform
                // buffer's packing. Its set comes from the storage class, so it lands in set 1. A reading
                // that went by the packing qualifier would file it under set 0, and this mask would then
                // never demand set 1: a dispatch that forgot the histogram SSBOs would sail straight past
                // the guard into the fault it exists to catch.
                Assert.That(luminance.Kind, Is.EqualTo(SpirvResourceKind.StorageBuffer));
                Assert.That(luminance.Set, Is.EqualTo(DescriptorSets.StorageBuffers));

                Assert.That(mask, Is.EqualTo(MaskOf(
                    DescriptorSets.UniformBuffers,
                    DescriptorSets.StorageBuffers,
                    DescriptorSets.MaterialTextures)),
                    $"histogram reflected as {string.Join(", ", reflection.DescriptorBindings.Select(b => $"set {b.Set} binding {b.Binding} {b.Kind}"))}");

                Assert.That(VulkanDescriptorSetUsage.Describe(mask), Is.EqualTo("0, 1 and 3"));
            }
        }

        // ---- the comparison ----

        [Test]
        public void AnUnboundSetIsRefusedAndNamed()
        {
            var used = MaskOf(0, 1, 3);

            Assert.That(() => VulkanDescriptorSetUsage.EnsureBound("post_processing", used, MaskOf(0, 1)),
                Throws.InstanceOf<InvalidOperationException>()
                    .With.Message.Contains("post_processing")
                    .And.Message.Contains("set 3")
                    .And.Message.Contains("never bound")
                    // and says what would have bound it, not only that something is missing
                    .And.Message.Contains(nameof(ICommandList.BindTexture))
                    .And.Message.Contains(nameof(DescriptorSets.MaterialTextures)));
        }

        [Test]
        public void EveryMissingSetIsNamed()
        {
            Assert.That(() => VulkanDescriptorSetUsage.EnsureBound("light_binner", MaskOf(0, 1, 4), MaskOf(0)),
                Throws.InstanceOf<InvalidOperationException>()
                    .With.Message.Contains("sets 1 and 4")
                    .And.Message.Contains(nameof(ICommandList.BindStorageBuffer))
                    .And.Message.Contains(nameof(ICommandList.BindStorageTexture)));
        }

        [Test]
        public void ABoundSetIsAllowed()
        {
            using (Assert.EnterMultipleScope())
            {
                // Exactly the used sets.
                Assert.That(() => VulkanDescriptorSetUsage.EnsureBound("mesh", MaskOf(0, 1, 3), MaskOf(0, 1, 3)), Throws.Nothing);

                // More bound than used is not a fault: the reserved globals are bound once per pass for
                // every pipeline in it, including the ones that read none of them.
                Assert.That(() => VulkanDescriptorSetUsage.EnsureBound("mesh", MaskOf(0, 3), VulkanDescriptorSetUsage.AllSets), Throws.Nothing);

                // A pipeline that uses no descriptors at all draws with nothing bound.
                Assert.That(() => VulkanDescriptorSetUsage.EnsureBound("fullscreen", VulkanDescriptorSetUsage.NoSets, VulkanDescriptorSetUsage.NoSets), Throws.Nothing);
            }
        }

        [Test]
        public void TheGuardIsDrivenByARealShadersMask()
        {
            // The two halves composed: a mask taken from real SPIR-V, and a binder state that filled all
            // of it but the material textures. This is the shape of the mistake the guard exists for --
            // a port that recorded some of a shader's bindings and not all of them.
            var used = VulkanDescriptorSetUsage.MaskFor(RealShader("histogram"));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(() => VulkanDescriptorSetUsage.EnsureBound("histogram", used, MaskOf(DescriptorSets.UniformBuffers, DescriptorSets.StorageBuffers)),
                    Throws.InstanceOf<InvalidOperationException>()
                        .With.Message.Contains("histogram")
                        .And.Message.Contains($"set {DescriptorSets.MaterialTextures}"));

                Assert.That(() => VulkanDescriptorSetUsage.EnsureBound("histogram", used, used), Throws.Nothing);

                // And the storage buffer set is genuinely demanded, which is what the std140 case buys.
                Assert.That(() => VulkanDescriptorSetUsage.EnsureBound("histogram", used, used & ~MaskOf(DescriptorSets.StorageBuffers)),
                    Throws.InstanceOf<InvalidOperationException>()
                        .With.Message.Contains($"set {DescriptorSets.StorageBuffers}"));
            }
        }

        [Test]
        public void EverySetOfTheContractSaysHowToBindIt()
        {
            for (var set = 0; set < DescriptorSets.Count; set++)
            {
                Assert.That(VulkanDescriptorSetUsage.BoundBy(set), Does.Contain("Bind"),
                    $"Set {set} does not name the call that fills it.");
            }
        }
    }
}
