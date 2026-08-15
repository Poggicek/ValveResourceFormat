using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Silk.NET.Vulkan;
using ValveResourceFormat.Renderer.Buffers;
using ValveResourceFormat.Renderer.Materials;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;
using ValveResourceFormat.Renderer.Shaders.Spirv;

namespace ValveResourceFormat.Renderer.RHI.Vulkan.Descriptors;

/// <summary>
/// A deliberate descriptor misuse the smoke test can commit, to prove the validation layer is watching.
/// </summary>
/// <remarks>
/// <para>
/// Every provocation goes around <see cref="VulkanDescriptorWriter"/>, because the writer refuses all
/// of them by design. A run that passed only because the writer's own guard fired would prove the guard
/// works and say nothing about whether Vulkan is watching, which is the thing this needs to establish.
/// </para>
/// <para>
/// <b><see cref="ImageIntoBufferSlot"/> must be run one to a process.</b> Both write provocations break
/// the same rule, <c>VUID-VkWriteDescriptorSet-descriptorType-00319</c>, and the two directions behave
/// differently on real hardware: writing a buffer into an image slot is reported and the call is
/// suppressed, while writing an image into a buffer slot takes an access violation inside
/// <c>vkUpdateDescriptorSets</c> and kills the process before the result can be returned. That
/// asymmetry is worth knowing rather than hiding &#8212; a descriptor type mismatch is not a diagnostic,
/// it is memory being reinterpreted, and only one half of it happens to be caught before the driver is
/// reached. <see cref="MismatchedSetLayout"/> and <see cref="BufferIntoImageSlot"/> both leave the
/// process healthy and are the two to run in a suite.
/// </para>
/// </remarks>
public enum VulkanDescriptorProvocation
{
    /// <summary>Commit no violation. The normal run.</summary>
    None,

    /// <summary>
    /// Bind a descriptor set at set 2 that was allocated from a layout the pipeline layout does not
    /// declare there. Recorded and discarded, so the process survives.
    /// </summary>
    MismatchedSetLayout,

    /// <summary>
    /// Write a buffer into a binding the layout types as a combined image sampler. Reported and
    /// suppressed before the driver sees it, so the process survives.
    /// </summary>
    BufferIntoImageSlot,

    /// <summary>
    /// Write an image into a binding the layout types as a uniform buffer. Reported, and then fatal:
    /// run it alone, and do not expect a result back.
    /// </summary>
    ImageIntoBufferSlot,
}

/// <summary>What <see cref="VulkanDescriptorSmokeTest.Run"/> observed.</summary>
/// <param name="Succeeded">Whether every check held and validation stayed silent. For a provoked run,
/// whether every check held and the provocation was seen.</param>
/// <param name="ValidationActive">Whether <c>VK_LAYER_KHRONOS_validation</c> was actually loaded. A pass
/// with this <see langword="false"/> proves only that the code ran.</param>
/// <param name="DeviceName">The physical device that was used.</param>
/// <param name="Provocation">The violation this run deliberately committed, if any.</param>
/// <param name="ProvokedErrors">How many validation errors the provocation raised. One is the goal:
/// zero means validation is not watching, and more than one means the provocation is impure and cannot
/// show which rule caught it.</param>
/// <param name="Checks">Every named check and whether it held.</param>
/// <param name="ValidationErrors">Error-severity messages attributable to this code's use of the API.</param>
/// <param name="ValidationWarnings">Warning-severity messages attributable to this code.</param>
/// <param name="ForeignMessages">Error and warning messages from the loader's view of the machine
/// rather than from this code, counted separately so an overlay layer cannot fail the run.</param>
/// <param name="Layouts">How many distinct descriptor set layouts the cache ended up creating.</param>
/// <param name="Allocator">Allocator state at the end.</param>
/// <param name="Messages">Every message the debug messenger delivered.</param>
public readonly record struct VulkanDescriptorSmokeTestResult(
    bool Succeeded,
    bool ValidationActive,
    string DeviceName,
    VulkanDescriptorProvocation Provocation,
    int ProvokedErrors,
    IReadOnlyList<(string Name, bool Passed)> Checks,
    int ValidationErrors,
    int ValidationWarnings,
    int ForeignMessages,
    int Layouts,
    VulkanDescriptorAllocatorStatistics Allocator,
    IReadOnlyList<string> Messages)
{
    /// <summary>Renders the result as a short report.</summary>
    /// <returns>The report text.</returns>
    public override string ToString()
    {
        var builder = new StringBuilder();

        builder.Append(CultureInfo.InvariantCulture, $"Vulkan descriptor smoke test [{Provocation}]: {(Succeeded ? "PASS" : "FAIL")}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"  device            {DeviceName}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture,
            $"  validation layer  {(ValidationActive ? "loaded" : "NOT LOADED - this run proves little")}");
        builder.AppendLine();

        foreach (var (name, passed) in Checks)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  {(passed ? "ok  " : "FAIL")}  {name}");
            builder.AppendLine();
        }

        builder.Append(CultureInfo.InvariantCulture,
            $"  validation        {ValidationErrors} errors, {ValidationWarnings} warnings ({ForeignMessages} foreign, not counted)");
        builder.AppendLine();

        if (Provocation != VulkanDescriptorProvocation.None)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  provoked          {ProvokedErrors} error(s)");
            builder.AppendLine();
        }

        builder.Append(CultureInfo.InvariantCulture,
            $"  layouts           {Layouts} distinct");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture,
            $"  allocator         {Allocator.FrameSlots} slots, peak {Allocator.PeakPerFrame} sets/frame, {Allocator.FramePoolCount} pools, {Allocator.PersistentAllocated} persistent");
        builder.AppendLine();

        foreach (var message in Messages)
        {
            builder.Append("  | ").Append(message);
            builder.AppendLine();
        }

        return builder.ToString();
    }
}

/// <summary>
/// Exercises the descriptor layer against a real device with the validation layer required to stay
/// completely silent: layout deduplication, the canonical sets, reflection-driven layouts, per-frame
/// allocation across a wrap of the frame ring, persistent allocation, batched updates against real
/// buffers and textures, and the writer's own type guards.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="VulkanResourceSmokeTest"/> one layer up, and the only verification this
/// layer can have: the golden image suite drives the OpenGL backend, so nothing in it reaches a
/// <c>VkDescriptorSet</c>.
/// </para>
/// <para>
/// The check with the most to say is <c>overlap</c>: it binds buffer <c>A</c> to set 0 binding 0 and a
/// different buffer <c>B</c> to set 1 binding 0 and reads both back distinctly. That pair of bindings is
/// <see cref="ReservedBufferSlots.View"/> and <see cref="ReservedBufferSlots.Objects"/>, the collision
/// the whole multi-set scheme exists to make unrepresentable, and it is the one thing here that would be
/// impossible if the sets had been merged.
/// </para>
/// <para>
/// Only <c>Validation</c> and <c>Performance</c> typed messages are counted. A consumer machine carries
/// overlay layers whose stale manifests the loader reports at error severity through the same messenger,
/// and counting those would make the result depend on what the developer happens to have installed.
/// </para>
/// </remarks>
public static unsafe class VulkanDescriptorSmokeTest
{
    private const int ImageSize = 32;
    private const int BufferBytes = 1024;

    /// <summary>Runs the smoke test.</summary>
    /// <param name="provocation">A deliberate violation to commit, or
    /// <see cref="VulkanDescriptorProvocation.None"/> for the normal run.</param>
    /// <param name="options">Overrides, or <see langword="null"/> for validation-on defaults.</param>
    /// <returns>What happened.</returns>
    public static VulkanDescriptorSmokeTestResult Run(
        VulkanDescriptorProvocation provocation = VulkanDescriptorProvocation.None,
        VulkanCoreOptions? options = null)
    {
        var messages = new List<string>();
        var checks = new List<(string, bool)>();

        var effective = (options ?? new VulkanCoreOptions()) with
        {
            EnableValidation = options?.EnableValidation ?? true,
            EnableSynchronizationValidation = options?.EnableSynchronizationValidation ?? true,
            ApplicationName = "VulkanDescriptorSmokeTest",
        };

        void OnMessage(RhiMessageSeverity severity, string message)
        {
            lock (messages)
            {
                messages.Add($"{severity}: {message}");
            }
        }

        using var device = new VulkanDevice(OnMessage, effective);
        using var cache = new VulkanDescriptorLayoutCache(device.Core.Api, device.Core.Handle, device.Core.DebugNames);
        using var allocator = new VulkanDescriptorAllocator(
            device.Core.Api,
            device.Core.Handle,
            device.Core.DebugNames,
            device.Core.FrameRing,
            cache);

        var writer = new VulkanDescriptorWriter(device.Core.Api, device.Core.Handle);
        var probe = new DescriptorProbe(device);
        var provokedErrors = 0;

        try
        {
            CheckCanonicalLayouts(cache, checks);
            CheckDeduplication(cache, checks);
            CheckReflectedLayouts(cache, checks);
            CheckWrites(device, cache, allocator, writer, probe, checks);
            CheckTypeGuards(device, cache, allocator, writer, probe, checks);
            CheckFrameLifecycle(device, cache, allocator, checks);
            CheckPersistent(cache, allocator, checks);
            CheckBinder(device, cache, allocator, probe, checks);

            if (provocation != VulkanDescriptorProvocation.None)
            {
                var before = device.Core.Instance.ValidationErrorCount;
                Provoke(device, cache, allocator, probe, provocation);
                provokedErrors = device.Core.Instance.ValidationErrorCount - before;

                checks.Add(($"provocation {provocation} raised exactly one validation error", provokedErrors == 1));
            }
        }
        finally
        {
            device.WaitIdle();
            probe.Dispose();
        }

        var errors = device.Core.Instance.ValidationErrorCount;
        var warnings = device.Core.Instance.ValidationWarningCount;
        var foreign = device.Core.Instance.ErrorCount + device.Core.Instance.WarningCount - errors - warnings;

        var everyCheckPassed = true;

        foreach (var (_, passed) in checks)
        {
            everyCheckPassed &= passed;
        }

        var succeeded = provocation == VulkanDescriptorProvocation.None
            ? everyCheckPassed && errors == 0 && warnings == 0
            : everyCheckPassed;

        return new VulkanDescriptorSmokeTestResult(
            succeeded,
            device.Core.ValidationEnabled,
            device.Core.Adapter.Name,
            provocation,
            provokedErrors,
            checks.ConvertAll(c => (c.Item1, c.Item2)),
            errors,
            warnings,
            foreign,
            cache.LayoutCount,
            allocator.Statistics,
            messages);
    }

    private static void CheckCanonicalLayouts(VulkanDescriptorLayoutCache cache, List<(string, bool)> checks)
    {
        var uniforms = cache.UniformBuffers;
        var storage = cache.StorageBuffers;
        var textures = cache.ReservedTextures;

        checks.Add(("canonical: set 0 declares every reserved uniform buffer slot",
            uniforms.Bindings.Length == (int)ReservedBufferSlots.Max));
        checks.Add(("canonical: set 1 declares every reserved storage buffer slot",
            storage.Bindings.Length == (int)ReservedBufferSlots.CullPlanes + 1));
        checks.Add(("canonical: set 2 declares every reserved texture slot",
            textures.Bindings.Length == (int)ReservedTextureSlots.Last + 1));

        // The overlap sets 0 and 1 exist for, stated as a layout fact: binding 0 means a
        // different thing in each of the two buffer sets, and both are declared.
        checks.Add(("canonical: binding 0 is a uniform buffer in set 0 and a storage buffer in set 1",
            uniforms.TryGetBinding((int)ReservedBufferSlots.View, out var view)
            && view.Type == DescriptorType.UniformBuffer
            && storage.TryGetBinding((int)ReservedBufferSlots.Objects, out var objects)
            && objects.Type == DescriptorType.StorageBuffer));

        checks.Add(("canonical: the reserved textures are combined image samplers",
            textures.TryGetBinding((int)ReservedTextureSlots.BRDFLookup, out var brdf)
            && brdf.Type == DescriptorType.CombinedImageSampler));

        // A shared layout has to be readable from every stage, because the next shader to use the same
        // table may read the slot from a stage the first one did not.
        checks.Add(("canonical: the shared layouts are visible to every stage",
            view.Stages == VulkanDescriptorTypes.AllStages));

        checks.Add(("canonical: set 3 has no canonical layout", cache.Canonical(DescriptorSets.MaterialTextures) is null));
        checks.Add(("canonical: an empty layout declares nothing", cache.Empty(DescriptorSets.MaterialTextures).IsEmpty));

        var images = cache.StorageImages;

        checks.Add(("canonical: set 4 declares the guaranteed image unit count",
            images.Bindings.Length == VulkanDescriptorTypes.StorageImageSlotCount));
        checks.Add(("canonical: set 4 holds storage images",
            images.TryGetBinding(0, out var image) && image.Type == DescriptorType.StorageImage));

        // The reason set 4 exists. Binding 0 is a storage image there and g_tBRDFLookup as a combined
        // image sampler in set 2, and a shader can declare both at once -- depth_pyramid.comp does.
        // Before set 4 there was nowhere to put the second one.
        checks.Add(("canonical: binding 0 is a storage image in set 4 and a sampled texture in set 2",
            image.Type == DescriptorType.StorageImage && brdf.Type == DescriptorType.CombinedImageSampler));

        checks.Add(("canonical: every set of the contract resolves to a layout",
            AllSetsResolve(cache)));

        // What the shaders are decorated with and what the layouts declare have to be the same numbers,
        // or a binding lands in a set the layout describes differently and the wrong resource is read
        // without an error. Cheap to assert and the only place the two halves meet.
        checks.Add(("emission: the shader emitter and this layer agree on every set number",
            VulkanGlsl.UniformBufferSet == DescriptorSets.UniformBuffers
            && VulkanGlsl.StorageBufferSet == DescriptorSets.StorageBuffers
            && VulkanGlsl.GlobalTextureSet == DescriptorSets.ReservedTextures
            && VulkanGlsl.MaterialTextureSet == DescriptorSets.MaterialTextures
            && VulkanGlsl.StorageImageSet == DescriptorSets.StorageImages));
    }

    /// <summary>
    /// Every set index the contract declares must produce a layout, canonical or empty. A set that
    /// resolved to nothing would leave a hole in the array <c>vkCreatePipelineLayout</c> is handed.
    /// </summary>
    private static bool AllSetsResolve(VulkanDescriptorLayoutCache cache)
    {
        for (var set = 0; set < DescriptorSets.Count; set++)
        {
            var layout = cache.Canonical(set) ?? cache.Empty(set);

            if (layout.Handle.Handle == 0 || layout.SetIndex != set)
            {
                return false;
            }
        }

        return true;
    }

    private static void CheckDeduplication(VulkanDescriptorLayoutCache cache, List<(string, bool)> checks)
    {
        ImmutableArray<VulkanDescriptorBinding> table =
        [
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
        ];

        var first = cache.GetOrCreate(DescriptorSets.MaterialTextures, table, "Dedup A");

        // Same table, different order, different debug name. All three must land on one object: Vulkan
        // decides pipeline layout compatibility by comparing the layout handles, so two of them here
        // would silently invalidate set 3 on every pipeline change.
        ImmutableArray<VulkanDescriptorBinding> reordered =
        [
            new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
        ];

        var second = cache.GetOrCreate(DescriptorSets.MaterialTextures, reordered, "Dedup B");

        checks.Add(("cache: an identical table returns the same layout object", ReferenceEquals(first, second)));
        checks.Add(("cache: and therefore the same handle", first.Handle.Handle == second.Handle.Handle));

        ImmutableArray<VulkanDescriptorBinding> different =
        [
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            new(1, DescriptorType.StorageImage, 1, ShaderStageFlags.FragmentBit),
        ];

        var third = cache.GetOrCreate(DescriptorSets.MaterialTextures, different, "Dedup C");

        checks.Add(("cache: a table differing only by descriptor type is a different layout", !ReferenceEquals(first, third)));
        checks.Add(("cache: the same set index is required to match",
            !ReferenceEquals(first, cache.GetOrCreate(DescriptorSets.ReservedTextures, table, "Dedup D"))));

        checks.Add(("cache: a duplicated binding number is refused",
            Throws<ArgumentException>(() => cache.GetOrCreate(
                DescriptorSets.MaterialTextures,
                [
                    new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
                    new(0, DescriptorType.StorageImage, 1, ShaderStageFlags.FragmentBit),
                ],
                "Dedup clash"))));

        checks.Add(("cache: a runtime sized array is refused, since descriptor indexing is off",
            Throws<ArgumentOutOfRangeException>(() => cache.GetOrCreate(
                DescriptorSets.MaterialTextures,
                [new(9, DescriptorType.CombinedImageSampler, 0, ShaderStageFlags.FragmentBit)],
                "Dedup unsized"))));
    }

    private static void CheckReflectedLayouts(VulkanDescriptorLayoutCache cache, List<(string, bool)> checks)
    {
        // A conforming graphics pair: view constants, the transform SSBO, one reserved texture and two
        // material textures. Nothing here is outside the reserved tables, so all of sets 0 to 2 should
        // come back as the shared objects.
        var vertex = new SpirvReflectionResult
        {
            Stage = ShaderStage.Vertex,
            DescriptorBindings =
            [
                new("ViewConstants", DescriptorSets.UniformBuffers, (int)ReservedBufferSlots.View, SpirvResourceKind.UniformBuffer, 1, 256),
                new("g_transformBuffer", DescriptorSets.StorageBuffers, (int)ReservedBufferSlots.Transforms, SpirvResourceKind.StorageBuffer, 1, 64),
                new("g_tColor", DescriptorSets.MaterialTextures, 0, SpirvResourceKind.CombinedImageSampler, 1, 0),
            ],
            PushConstantSizeInBytes = 92,
        };

        var fragment = new SpirvReflectionResult
        {
            Stage = ShaderStage.Fragment,
            DescriptorBindings =
            [
                new("ViewConstants", DescriptorSets.UniformBuffers, (int)ReservedBufferSlots.View, SpirvResourceKind.UniformBuffer, 1, 256),
                new("g_tBRDFLookup", DescriptorSets.ReservedTextures, (int)ReservedTextureSlots.BRDFLookup, SpirvResourceKind.CombinedImageSampler, 1, 0),
                new("g_tColor", DescriptorSets.MaterialTextures, 0, SpirvResourceKind.CombinedImageSampler, 1, 0),
                new("g_tNormal", DescriptorSets.MaterialTextures, 1, SpirvResourceKind.CombinedImageSampler, 1, 0),
            ],
            PushConstantSizeInBytes = 92,
        };

        var graphics = VulkanPipelineDescriptorLayouts.Build(cache, "SmokeTest graphics", vertex, fragment);

        checks.Add(("reflect: a conforming pipeline reports no diagnostics", graphics.Diagnostics.Length == 0));
        checks.Add((string.Create(CultureInfo.InvariantCulture, $"reflect: it declares all {DescriptorSets.Count} sets"),
            graphics.Sets.Length == DescriptorSets.Count));
        checks.Add(("reflect: sets 0, 1, 2 and 4 are the shared canonical objects",
            ReferenceEquals(graphics.Sets[0], cache.UniformBuffers)
            && ReferenceEquals(graphics.Sets[1], cache.StorageBuffers)
            && ReferenceEquals(graphics.Sets[2], cache.ReservedTextures)
            && ReferenceEquals(graphics.Sets[DescriptorSets.StorageImages], cache.StorageImages)));
        checks.Add(("reflect: set 3 is reflected, with the material's own numbering",
            graphics.Sets[3].Bindings.Length == 2
            && graphics.Sets[3].Bindings[0].Binding == 0
            && graphics.Sets[3].Bindings[1].Binding == 1));

        // g_tColor is declared by both stages, so its visibility is the union rather than whichever
        // stage happened to be walked last.
        checks.Add(("reflect: a binding declared by two stages is visible to both",
            graphics.Sets[3].Bindings[0].Stages == (ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit)));
        checks.Add(("reflect: the push constant range comes back from the module",
            graphics.PushConstants is { SizeInBytes: 92, OffsetInBytes: 0 }));

        checks.Add(("reflect: handles copy out in set order", CopiesHandlesInOrder(graphics)));
        checks.Add(("reflect: a short destination is refused",
            Throws<ArgumentException>(() => graphics.CopyHandlesTo(new DescriptorSetLayout[DescriptorSets.Count - 1]))));

        // A shader with no material textures still needs something in position 3.
        var bare = VulkanPipelineDescriptorLayouts.Build(cache, "SmokeTest bare", new SpirvReflectionResult
        {
            Stage = ShaderStage.Vertex,
            DescriptorBindings = [],
        });

        checks.Add(("reflect: a shader with no set 3 gets the empty layout there", bare.Sets[3].IsEmpty));
        checks.Add(("reflect: and still shares sets 0 to 2", ReferenceEquals(bare.Sets[2], cache.ReservedTextures)));

        // depth_pyramid.comp as ShaderParser emits it TODAY: a sampler at binding 0 and storage images
        // at 1 and 2, all decorated into set 2 because emission has not moved to set 4 yet. Set 2
        // binding 1 is BlueNoise in the reserved table, so the canonical layout cannot hold this and the
        // pipeline gets a reflected set 2 of its own. Relocating the images to set 4 here would be
        // wrong: the module reads set 2, so the layout has to say set 2.
        var transitional = VulkanPipelineDescriptorLayouts.Build(cache, "SmokeTest depth pyramid (set 2 images)", new SpirvReflectionResult
        {
            Stage = ShaderStage.Compute,
            DescriptorBindings =
            [
                new("g_tSourceDepthNpot", DescriptorSets.ReservedTextures, 0, SpirvResourceKind.CombinedImageSampler, 1, 0),
                new("g_tSourceDepth", DescriptorSets.ReservedTextures, 1, SpirvResourceKind.StorageImage, 1, 0),
                new("g_tDestDepth", DescriptorSets.ReservedTextures, 2, SpirvResourceKind.StorageImage, 1, 0),
            ],
        });

        checks.Add(("reflect: a storage image still decorated into set 2 displaces the shared set 2",
            transitional.StorageImagesDisplaceSet2));
        checks.Add(("reflect: and says so, naming the transition",
            transitional.Diagnostics.Length - transitional.ConformanceViolationCount == 1
            && transitional.Diagnostics[^1].Contains("transitional", StringComparison.Ordinal)));
        checks.Add(("reflect: the displaced set 2 is reflected, not shared",
            !ReferenceEquals(transitional.Sets[2], cache.ReservedTextures) && transitional.Sets[2].Bindings.Length == 3));
        checks.Add(("reflect: a displaced set 2 does not disturb the other sets",
            ReferenceEquals(transitional.Sets[0], cache.UniformBuffers)
            && ReferenceEquals(transitional.Sets[DescriptorSets.StorageImages], cache.StorageImages)));

        // The same shader once ShaderParser decorates its images into set 4. Nothing in this layer
        // changes for that to work, which is the property worth pinning: set 2 goes back to shared and
        // the images land in the canonical set 4.
        var target = VulkanPipelineDescriptorLayouts.Build(cache, "SmokeTest depth pyramid (set 4 images)", new SpirvReflectionResult
        {
            Stage = ShaderStage.Compute,
            DescriptorBindings =
            [
                new("g_tSourceDepthNpot", DescriptorSets.ReservedTextures, 0, SpirvResourceKind.CombinedImageSampler, 1, 0),
                new("g_tSourceDepth", DescriptorSets.StorageImages, 1, SpirvResourceKind.StorageImage, 1, 0),
                new("g_tDestDepth", DescriptorSets.StorageImages, 2, SpirvResourceKind.StorageImage, 1, 0),
            ],
        });

        checks.Add(("reflect: images decorated into set 4 displace nothing", !target.StorageImagesDisplaceSet2));
        checks.Add(("reflect: every set is then shared, set 2 included",
            ReferenceEquals(target.Sets[2], cache.ReservedTextures)
            && ReferenceEquals(target.Sets[DescriptorSets.StorageImages], cache.StorageImages)));

        // Stated as "this layer added nothing of its own" rather than "there are no diagnostics",
        // because SpirvReflection.ValidateDescriptorSets has not learned set 4 yet and still reports a
        // correctly placed storage image as belonging in set 2 or 3. Splitting the count is what lets
        // this assert the right thing today and keep asserting it once that is fixed.
        checks.Add(("reflect: a set 4 image needs no fallback from this layer",
            target.Diagnostics.Length == target.ConformanceViolationCount));

        // An image unit past what OpenGL guarantees has no canonical slot, so it falls back rather than
        // being silently dropped or clamped.
        var wideImage = VulkanPipelineDescriptorLayouts.Build(cache, "SmokeTest wide image unit", new SpirvReflectionResult
        {
            Stage = ShaderStage.Compute,
            DescriptorBindings =
            [
                new("g_tFar", DescriptorSets.StorageImages, VulkanDescriptorTypes.StorageImageSlotCount, SpirvResourceKind.StorageImage, 1, 0),
            ],
        });

        checks.Add(("reflect: an image unit past the canonical width falls back with a diagnostic",
            !ReferenceEquals(wideImage.Sets[DescriptorSets.StorageImages], cache.StorageImages)
            && wideImage.Diagnostics.Length - wideImage.ConformanceViolationCount == 1));

        // The reflector's own contract check has to reach the diagnostics, or a shader that decorates a
        // storage buffer into set 0 would build a layout that binds the wrong buffer without a word.
        var misplaced = VulkanPipelineDescriptorLayouts.Build(cache, "SmokeTest misplaced", new SpirvReflectionResult
        {
            Stage = ShaderStage.Vertex,
            DescriptorBindings =
            [
                new("g_objectBuffer", DescriptorSets.UniformBuffers, 0, SpirvResourceKind.StorageBuffer, 1, 64),
            ],
        });

        checks.Add(("reflect: a storage buffer decorated into set 0 is reported", misplaced.Diagnostics.Length > 0));
    }

    private static bool CopiesHandlesInOrder(VulkanPipelineDescriptorLayouts layouts)
    {
        Span<DescriptorSetLayout> handles = stackalloc DescriptorSetLayout[DescriptorSets.Count];
        layouts.CopyHandlesTo(handles);

        for (var i = 0; i < handles.Length; i++)
        {
            if (handles[i].Handle != layouts.Sets[i].Handle.Handle)
            {
                return false;
            }
        }

        return true;
    }

    private static void CheckWrites(
        VulkanDevice device,
        VulkanDescriptorLayoutCache cache,
        VulkanDescriptorAllocator allocator,
        VulkanDescriptorWriter writer,
        DescriptorProbe probe,
        List<(string, bool)> checks)
    {
        var uniformSet = allocator.AllocateForFrame(cache.UniformBuffers);
        var storageSet = allocator.AllocateForFrame(cache.StorageBuffers);
        var textureSet = allocator.AllocateForFrame(cache.ReservedTextures);

        checks.Add(("allocate: three sets came back with real handles",
            uniformSet.Handle != 0 && storageSet.Handle != 0 && textureSet.Handle != 0));
        checks.Add(("allocate: they are distinct",
            uniformSet.Handle != storageSet.Handle && storageSet.Handle != textureSet.Handle));

        var materialLayout = cache.GetOrCreate(
            DescriptorSets.MaterialTextures,
            [new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit)],
            "SmokeTest material set");

        var materialSet = allocator.AllocateForFrame(materialLayout);

        // The whole point of splitting the buffer sets: binding number 0 in both, two different buffers,
        // no ambiguity. On one merged set this pair could not be expressed at all.
        writer
            .WriteBuffer(uniformSet, cache.UniformBuffers, (int)ReservedBufferSlots.View, probe.Uniform)
            .WriteBuffer(storageSet, cache.StorageBuffers, (int)ReservedBufferSlots.Objects, probe.Storage)
            .WriteBuffer(uniformSet, cache.UniformBuffers, (int)ReservedBufferSlots.Lighting, probe.Uniform, 256, 256)
            .WriteTexture(textureSet, cache.ReservedTextures, (int)ReservedTextureSlots.BRDFLookup, probe.Texture, device.DefaultSampler)
            .WriteTexture(textureSet, cache.ReservedTextures, (int)ReservedTextureSlots.SceneDepth, probe.Depth, device.DefaultSampler)
            .WriteTexture(materialSet, materialLayout, 0, probe.Texture, device.DefaultSampler);

        checks.Add(("write: six writes are queued rather than issued", writer.PendingCount == 6));

        var applied = writer.Flush();

        checks.Add(("write: one flush applied all six", applied == 6 && writer.FlushCount == 1));
        checks.Add(("write: the queue is empty afterwards", writer.PendingCount == 0));

        if (device.Limits.SupportsFormat(RhiFormat.R32_SFloat, TextureUsage.Storage))
        {
            var storageImageLayout = cache.GetOrCreate(
                DescriptorSets.ReservedTextures,
                [new(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)],
                "SmokeTest storage image set");

            var storageImageSet = allocator.AllocateForFrame(storageImageLayout);

            writer.WriteStorageTexture(storageImageSet, storageImageLayout, 1, probe.StorageImage!);
            checks.Add(("write: a storage image write applies", writer.Flush() == 1));
        }
        else
        {
            checks.Add(("write: R32_SFloat storage images unsupported here, skipped", true));
        }

        writer.Discard();
        checks.Add(("write: discarding an empty queue is harmless", writer.PendingCount == 0));
    }

    private static void CheckTypeGuards(
        VulkanDevice device,
        VulkanDescriptorLayoutCache cache,
        VulkanDescriptorAllocator allocator,
        VulkanDescriptorWriter writer,
        DescriptorProbe probe,
        List<(string, bool)> checks)
    {
        var uniformSet = allocator.AllocateForFrame(cache.UniformBuffers);
        var textureSet = allocator.AllocateForFrame(cache.ReservedTextures);

        // The failure worth catching before Vulkan sees it. A type mismatch here is undefined behaviour
        // rather than an error, so without the validation layer it reads whatever it aliases.
        checks.Add(("guard: a buffer into an image slot is refused",
            Throws<InvalidOperationException>(() => writer.WriteBuffer(textureSet, cache.ReservedTextures, 0, probe.Uniform))));
        checks.Add(("guard: an image into a buffer slot is refused",
            Throws<InvalidOperationException>(() => writer.WriteTexture(uniformSet, cache.UniformBuffers, 0, probe.Texture, device.DefaultSampler))));
        checks.Add(("guard: a storage image into a sampled slot is refused",
            Throws<InvalidOperationException>(() => writer.WriteStorageTexture(textureSet, cache.ReservedTextures, 0, probe.Texture))));
        checks.Add(("guard: an undeclared binding is refused",
            Throws<InvalidOperationException>(() => writer.WriteBuffer(uniformSet, cache.UniformBuffers, 64, probe.Uniform))));
        checks.Add(("guard: a combined image sampler with no sampler is refused",
            Throws<InvalidOperationException>(() => writer.WriteTexture(textureSet, cache.ReservedTextures, 0, probe.Texture, null))));
        checks.Add(("guard: a range past the end of the buffer is refused",
            Throws<ArgumentOutOfRangeException>(() => writer.WriteBuffer(uniformSet, cache.UniformBuffers, 0, probe.Uniform, 0, BufferBytes * 2))));
        checks.Add(("guard: a buffer without the declared usage is refused by name",
            Throws<InvalidOperationException>(() => writer.WriteBuffer(uniformSet, cache.UniformBuffers, 0, probe.Storage))));

        // Every guard above must have refused before queueing, or the writer is now holding a write it
        // cannot legally apply.
        checks.Add(("guard: nothing invalid reached the queue", writer.PendingCount == 0));

        writer.Discard();
    }

    private static void CheckFrameLifecycle(
        VulkanDevice device,
        VulkanDescriptorLayoutCache cache,
        VulkanDescriptorAllocator allocator,
        List<(string, bool)> checks)
    {
        var before = allocator.Statistics;

        // More passes than there are frame slots, so the ring wraps and each slot's chain has to be
        // recycled against a serial that was actually signalled.
        var passes = (before.FrameSlots * 3) + 1;
        const int perFrame = 24;
        var slots = new HashSet<int>();
        var everyFrameStartsEmpty = true;

        for (var pass = 0; pass < passes; pass++)
        {
            // BeginFrame on the device drives the ring; the allocator notices the new serial itself,
            // which is why nothing here has to remember to tell it.
            device.BeginFrame();
            slots.Add(device.FrameIndex);

            // The first allocation of the frame is what recycles the slot's chain, so the count it
            // leaves behind is one and not one plus whatever the previous pass through this slot left.
            allocator.AllocateForFrame(cache.UniformBuffers);
            everyFrameStartsEmpty &= allocator.Statistics.AllocatedThisFrame == 1;

            for (var i = 1; i < perFrame; i++)
            {
                allocator.AllocateForFrame(cache.UniformBuffers);
            }

            device.EndFrame();
        }

        var after = allocator.Statistics;

        checks.Add(("frames: the ring wrapped without deadlocking", slots.Count == before.FrameSlots));
        checks.Add(("frames: a slot's chain is recycled on the frame's first allocation", everyFrameStartsEmpty));
        checks.Add(("frames: the peak reflects one frame's worth, not the total",
            after.PeakPerFrame >= perFrame && after.PeakPerFrame < perFrame * passes));

        // A chain that overflows doubles rather than adding one pool per frame, so a steady per-frame
        // load must settle rather than grow with the frame count.
        checks.Add(("frames: pool count stays bounded across the wrap",
            after.FramePoolCount <= before.FrameSlots * 2));
    }

    /// <summary>
    /// Drives <see cref="VulkanDescriptorBinder"/> against a real pipeline layout and a real command
    /// buffer, with the validation layer watching.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The command buffer is recorded and thrown away rather than submitted, the same shape the
    /// provocations use. That is enough: <c>vkCmdBindDescriptorSets</c> checks a set against the pipeline
    /// layout's declaration at <i>record</i> time, so the thing most worth proving here &#8212; that a set
    /// allocated from this layer's layout is compatible with a layout the pipeline layer built &#8212; is
    /// established without a draw, a shader or a render pass.
    /// </para>
    /// <para>
    /// The pipeline layout is built through the real <see cref="VulkanPipelineLayoutCache"/> rather than
    /// by hand, because a hand-built one would not be in the cache the binder resolves through and the
    /// test would be checking a path no frame takes.
    /// </para>
    /// </remarks>
    private static void CheckBinder(
        VulkanDevice device,
        VulkanDescriptorLayoutCache cache,
        VulkanDescriptorAllocator allocator,
        DescriptorProbe probe,
        List<(string, bool)> checks)
    {
        var core = device.Core;

        using var pipelineLayouts = new VulkanPipelineLayoutCache(core.Api, core.Handle, core.DebugNames, new VulkanPipelineStats(), cache);
        using var binder = new VulkanDescriptorBinder(core.Api, cache, pipelineLayouts, allocator);

        // A shader touching all five sets, so every one of them resolves and the binder has somewhere to
        // put each kind of binding.
        var reflection = new SpirvReflectionResult
        {
            Stage = ShaderStage.Compute,
            DescriptorBindings =
            [
                new("ViewConstants", DescriptorSets.UniformBuffers, (int)ReservedBufferSlots.View, SpirvResourceKind.UniformBuffer, 1, 256),
                new("g_objectBuffer", DescriptorSets.StorageBuffers, (int)ReservedBufferSlots.Objects, SpirvResourceKind.StorageBuffer, 1, 64),
                new("g_tBRDFLookup", DescriptorSets.ReservedTextures, (int)ReservedTextureSlots.BRDFLookup, SpirvResourceKind.CombinedImageSampler, 1, 0),
                new("g_tColor", DescriptorSets.MaterialTextures, 0, SpirvResourceKind.CombinedImageSampler, 1, 0),
                new("g_tDest", DescriptorSets.StorageImages, 1, SpirvResourceKind.StorageImage, 1, 0),
            ],
        };

        var layout = pipelineLayouts.GetOrCreate([reflection], device.Limits.MaxPushConstantSize, "SmokeTest binder layout");

        checks.Add(("binder: the pipeline layout resolves back through the cache",
            pipelineLayouts.TryGetByHandle(layout.Handle, out var found) && ReferenceEquals(found, layout)));
        checks.Add(("binder: its set layouts resolve back to this layer's objects",
            cache.TryResolve(layout.SetLayouts[DescriptorSets.UniformBuffers], out var resolved)
            && ReferenceEquals(resolved, cache.UniformBuffers)));

        using var pool = new VulkanCommandPool(core.Api, core.Handle, core.GraphicsQueueFamily, core.DebugNames, "SmokeTest binder pool");

        var command = pool.Acquire("SmokeTest binder");
        binder.Reset(command);

        // Nothing bound yet, so a flush before any binding must issue no Vulkan call at all.
        binder.Flush(command, PipelineBindPoint.Compute, layout.Handle);

        checks.Add(("binder: a flush with nothing bound issues nothing", binder.Statistics.NoOpFlushes == 1));

        binder.BindUniformBuffer((int)ReservedBufferSlots.View, probe.Uniform.Handle, 0, (ulong)probe.Uniform.SizeInBytes);
        binder.BindStorageBuffer((int)ReservedBufferSlots.Objects, probe.Storage.Handle, 0, (ulong)probe.Storage.SizeInBytes);
        binder.BindSampledImage(DescriptorSets.ReservedTextures, (int)ReservedTextureSlots.BRDFLookup,
            probe.Texture.View, device.DefaultSampler.Handle, ImageLayout.ShaderReadOnlyOptimal);
        binder.BindSampledImage(DescriptorSets.MaterialTextures, 0,
            probe.Texture.View, device.DefaultSampler.Handle, ImageLayout.ShaderReadOnlyOptimal);

        binder.Flush(command, PipelineBindPoint.Compute, layout.Handle);

        var afterFirst = binder.Statistics;

        checks.Add(("binder: the first flush allocated one set per touched set", afterFirst.SetsAllocated == 4));
        checks.Add(("binder: and wrote every binding", afterFirst.DescriptorsWritten == 4));
        checks.Add(("binder: in a single vkUpdateDescriptorSets", afterFirst.UpdateCalls == 1));

        // Sets 0, 1, 2 and 3 are consecutive, so they bind in one call rather than four.
        checks.Add(("binder: consecutive sets bind in one call", afterFirst.BindCalls == 1));

        // The property the interface asks for in so many words: a binder that has seen no change since
        // the last flush should do nothing.
        binder.Flush(command, PipelineBindPoint.Compute, layout.Handle);

        checks.Add(("binder: an unchanged flush does nothing", binder.Statistics.NoOpFlushes == 2
            && binder.Statistics.SetsAllocated == afterFirst.SetsAllocated));

        // Rebinding the identical resource is not a change either, which is what stops a batch that
        // rebinds the same material's textures from allocating a set per draw.
        binder.BindUniformBuffer((int)ReservedBufferSlots.View, probe.Uniform.Handle, 0, (ulong)probe.Uniform.SizeInBytes);
        binder.Flush(command, PipelineBindPoint.Compute, layout.Handle);

        checks.Add(("binder: rebinding the same resource is not a change", binder.Statistics.SetsAllocated == afterFirst.SetsAllocated));

        // Changing one binding must produce a NEW set, never a rewrite: the previous set is referenced by
        // commands already recorded, and rewriting it would change what they read.
        var previous = binder.Statistics.SetsAllocated;
        binder.BindUniformBuffer((int)ReservedBufferSlots.View, probe.Uniform.Handle, 256, 256);
        binder.Flush(command, PipelineBindPoint.Compute, layout.Handle);

        checks.Add(("binder: a changed binding allocates a fresh set rather than rewriting one",
            binder.Statistics.SetsAllocated == previous + 1));
        checks.Add(("binder: and rebinds only that set", binder.Statistics.BindCalls == afterFirst.BindCalls + 1));

        // Set 4 is not consecutive with 0 to 3, so it costs its own bind call.
        var beforeImage = binder.Statistics.BindCalls;
        binder.BindStorageImage(1, probe.StorageImage?.View ?? probe.Texture.View, ImageLayout.General);

        if (probe.StorageImage is not null)
        {
            binder.Flush(command, PipelineBindPoint.Compute, layout.Handle);

            checks.Add(("binder: a storage image lands in set 4 and binds separately",
                binder.Statistics.BindCalls == beforeImage + 1));
        }
        else
        {
            checks.Add(("binder: no storage image format supported here, skipped", true));
        }

        // A pipeline layout this binder cannot resolve is refused rather than guessed at.
        checks.Add(("binder: an unknown pipeline layout is refused",
            Throws<InvalidOperationException>(() => binder.Flush(command, PipelineBindPoint.Compute, default))));

        binder.Reset(command);

        checks.Add(("binder: reset drops everything accumulated", ResetIsQuiet(binder, command, layout)));

        core.Api.EndCommandBuffer(command).Check("vkEndCommandBuffer");
    }

    private static bool ResetIsQuiet(VulkanDescriptorBinder binder, CommandBuffer command, VulkanPipelineLayout layout)
    {
        var before = binder.Statistics;
        binder.Flush(command, PipelineBindPoint.Compute, layout.Handle);
        var after = binder.Statistics;

        return after.SetsAllocated == before.SetsAllocated
            && after.BindCalls == before.BindCalls
            && after.NoOpFlushes == before.NoOpFlushes + 1;
    }

    private static void CheckPersistent(
        VulkanDescriptorLayoutCache cache,
        VulkanDescriptorAllocator allocator,
        List<(string, bool)> checks)
    {
        var persistent = allocator.AllocatePersistent(cache.ReservedTextures);
        var outstanding = allocator.Statistics.PersistentAllocated;

        checks.Add(("persistent: a set is handed out", persistent.Handle != 0 && outstanding > 0));

        allocator.FreePersistent(persistent);

        checks.Add(("persistent: freeing it is accounted for", allocator.Statistics.PersistentAllocated == outstanding - 1));
        checks.Add(("persistent: freeing a foreign set is refused rather than passed on",
            Throws<InvalidOperationException>(() => allocator.FreePersistent(persistent))));
    }

    /// <summary>
    /// Issues one raw <c>vkUpdateDescriptorSets</c> whose descriptor type disagrees with the layout.
    /// </summary>
    /// <remarks>
    /// Deliberately bypasses <see cref="VulkanDescriptorWriter"/>, which refuses exactly this. The point
    /// is to establish that the validation layer catches it too, since the writer's guard only protects
    /// call sites that go through the writer.
    /// </remarks>
    private static void Provoke(
        VulkanDevice device,
        VulkanDescriptorLayoutCache cache,
        VulkanDescriptorAllocator allocator,
        DescriptorProbe probe,
        VulkanDescriptorProvocation provocation)
    {
        var api = device.Core.Api;

        switch (provocation)
        {
            case VulkanDescriptorProvocation.MismatchedSetLayout:
                {
                    ProvokeMismatchedSetLayout(device, cache, allocator);
                    break;
                }

            case VulkanDescriptorProvocation.BufferIntoImageSlot:
                {
                    // Set 2 binding 0 is g_tBRDFLookup, a combined image sampler.
                    var set = allocator.AllocateForFrame(cache.ReservedTextures);

                    var info = new DescriptorBufferInfo
                    {
                        Buffer = probe.Uniform.Handle,
                        Offset = 0,
                        Range = BufferBytes,
                    };

                    var write = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = set,
                        DstBinding = (uint)(int)ReservedTextureSlots.BRDFLookup,
                        DstArrayElement = 0,
                        DescriptorCount = 1,
                        DescriptorType = DescriptorType.UniformBuffer,
                        PBufferInfo = &info,
                    };

                    api.UpdateDescriptorSets(device.Core.Handle, 1, &write, 0, null);
                    break;
                }

            case VulkanDescriptorProvocation.ImageIntoBufferSlot:
                {
                    // Set 0 binding 0 is the view constants uniform buffer.
                    var set = allocator.AllocateForFrame(cache.UniformBuffers);

                    var info = new DescriptorImageInfo
                    {
                        Sampler = device.DefaultSampler.Handle,
                        ImageView = probe.Texture.View,
                        ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                    };

                    var write = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = set,
                        DstBinding = (uint)(int)ReservedBufferSlots.View,
                        DstArrayElement = 0,
                        DescriptorCount = 1,
                        DescriptorType = DescriptorType.CombinedImageSampler,
                        PImageInfo = &info,
                    };

                    api.UpdateDescriptorSets(device.Core.Handle, 1, &write, 0, null);
                    break;
                }

            default:
                break;
        }
    }

    /// <summary>
    /// Records one <c>vkCmdBindDescriptorSets</c> whose set was allocated from a layout the pipeline
    /// layout does not declare at that index, then throws the command buffer away.
    /// </summary>
    /// <remarks>
    /// The safe provocation, and the one that matches the shape of the real hazard: nothing about a
    /// descriptor set handle says which layout it came from, so binding the wrong one is invisible at
    /// the call site. The layer catches it while the command is being recorded, which is why nothing has
    /// to be submitted for the error to appear.
    /// </remarks>
    private static void ProvokeMismatchedSetLayout(
        VulkanDevice device,
        VulkanDescriptorLayoutCache cache,
        VulkanDescriptorAllocator allocator)
    {
        var api = device.Core.Api;

        // A pipeline layout that expects the shared sets everywhere.
        Span<DescriptorSetLayout> handles =
        [
            cache.UniformBuffers.Handle,
            cache.StorageBuffers.Handle,
            cache.ReservedTextures.Handle,
            cache.Empty(DescriptorSets.MaterialTextures).Handle,
        ];

        PipelineLayout pipelineLayout;

        fixed (DescriptorSetLayout* p = handles)
        {
            var info = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)handles.Length,
                PSetLayouts = p,
            };

            api.CreatePipelineLayout(device.Core.Handle, &info, null, out pipelineLayout).Check("vkCreatePipelineLayout");
        }

        try
        {
            // A set 2 that is emphatically not the shared one.
            var foreign = cache.GetOrCreate(
                DescriptorSets.ReservedTextures,
                [new(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)],
                "Provocation foreign set 2");

            var set = allocator.AllocateForFrame(foreign);

            using (var pool = new VulkanCommandPool(
                api,
                device.Core.Handle,
                device.Core.GraphicsQueueFamily,
                device.Core.DebugNames,
                "Descriptor provocation pool"))
            {
                var command = pool.Acquire("Descriptor provocation");
                var bound = set;

                api.CmdBindDescriptorSets(
                    command,
                    PipelineBindPoint.Graphics,
                    pipelineLayout,
                    (uint)DescriptorSets.ReservedTextures,
                    1,
                    &bound,
                    0,
                    null);

                api.EndCommandBuffer(command).Check("vkEndCommandBuffer");
            }
        }
        finally
        {
            // After the pool, so the command buffer referencing it is gone first.
            api.DestroyPipelineLayout(device.Core.Handle, pipelineLayout, null);
        }
    }

    /// <summary>The real buffers and textures the writes are made against.</summary>
    private sealed class DescriptorProbe : IDisposable
    {
        internal VulkanBuffer Uniform { get; }
        internal VulkanBuffer Storage { get; }
        internal VulkanTexture Texture { get; }
        internal VulkanTexture Depth { get; }
        internal VulkanTexture? StorageImage { get; }

        internal DescriptorProbe(VulkanDevice device)
        {
            Uniform = (VulkanBuffer)device.CreateBuffer(new BufferDesc(
                BufferBytes, BufferUsage.Uniform | BufferUsage.CopyDestination, BufferMemory.HostUpload, "Descriptor probe uniform"));

            Storage = (VulkanBuffer)device.CreateBuffer(new BufferDesc(
                BufferBytes, BufferUsage.Storage | BufferUsage.CopyDestination, BufferMemory.DeviceLocal, "Descriptor probe storage"));

            Texture = (VulkanTexture)device.CreateTexture(new TextureDesc(
                ImageSize, ImageSize, RhiFormat.R8G8B8A8_UNorm, TextureUsage.Sampled | TextureUsage.CopyDestination, "Descriptor probe texture"));

            var depthFormat = device.Limits.SupportsFormat(RhiFormat.D32_SFloat, TextureUsage.DepthStencilTarget)
                ? RhiFormat.D32_SFloat
                : RhiFormat.D24_UNorm_S8_UInt;

            Depth = (VulkanTexture)device.CreateTexture(new TextureDesc(
                ImageSize, ImageSize, depthFormat, TextureUsage.DepthStencilTarget | TextureUsage.Sampled, "Descriptor probe depth"));

            if (device.Limits.SupportsFormat(RhiFormat.R32_SFloat, TextureUsage.Storage))
            {
                StorageImage = (VulkanTexture)device.CreateTexture(new TextureDesc(
                    ImageSize, ImageSize, RhiFormat.R32_SFloat, TextureUsage.Storage, "Descriptor probe storage image"));
            }

            // A descriptor names the layout the image will be in when it is read, so the images are put
            // there now rather than left in Undefined for a draw to trip over later.
            var command = device.Uploads.BeginBatch();
            Texture.TransitionTo(command, ResourceState.ShaderRead);
            Depth.TransitionTo(command, ResourceState.ShaderRead);
            StorageImage?.TransitionTo(command, ResourceState.ShaderReadWrite);
            device.Uploads.Flush();
        }

        public void Dispose()
        {
            StorageImage?.Dispose();
            Depth.Dispose();
            Texture.Dispose();
            Storage.Dispose();
            Uniform.Dispose();
        }
    }

    private static bool Throws<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }
}
