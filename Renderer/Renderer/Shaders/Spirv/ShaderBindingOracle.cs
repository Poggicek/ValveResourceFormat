using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using ValveResourceFormat.Renderer.RHI;

namespace ValveResourceFormat.Renderer.Shaders.Spirv;

/// <summary>What one run of <see cref="ShaderBindingOracle"/> found.</summary>
/// <param name="ShadersSeen">Renderer shaders the run looked at.</param>
/// <param name="ShadersCompiled">How many of those produced at least one module. A shader that will not
/// compile yet is reported, not counted against the checks: the port is not finished and this is not the
/// gate that measures it.</param>
/// <param name="DescriptorsChecked">Descriptors compared between the module and what the renderer would
/// record.</param>
/// <param name="TexturesChecked">Of those, the ones a texture bind can satisfy.</param>
/// <param name="Violations">Every disagreement found. Empty is the pass condition.</param>
/// <param name="Uncompilable">Shaders whose stages did not compile, with the first error.</param>
/// <param name="SequentialDivergences">
/// Places where the slot the shader declares differs from the slot a sequential count over the surviving
/// material textures would have produced. Not violations: they are the measurement of how wrong the
/// pre-reflection bind side was, and every one of them is a texture that would have bound in the wrong
/// place.
/// </param>
public readonly record struct ShaderBindingOracleReport(
    int ShadersSeen,
    int ShadersCompiled,
    int DescriptorsChecked,
    int TexturesChecked,
    ImmutableArray<string> Violations,
    ImmutableArray<string> Uncompilable,
    ImmutableArray<string> SequentialDivergences)
{
    /// <summary>Gets whether every check held.</summary>
    public bool Succeeded => Violations.IsDefaultOrEmpty;
}

/// <summary>
/// Checks, on the CPU and without a graphics device, that what the renderer records at bind time is what
/// the shader's SPIR-V declares: set, binding and resource kind, for every shader.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this can say something.</b> The bind side is derived from reflection, so comparing it against
/// reflection would be circular if both came from the same object. They do not. The ground truth here is
/// the raw <see cref="SpirvReflectionResult"/> per stage, straight out of
/// <see cref="SpirvReflection.Reflect"/>; the thing under test is the production
/// <see cref="SpirvShaderInterface"/> the renderer actually binds through, plus the emission table the
/// preprocessor filled in before glslang ever ran. Three independently produced descriptions of the same
/// shader are compared, and a defect in the middle one is what this catches.
/// </para>
/// <para>
/// <b>What it cannot say.</b> It does not run a device, so it cannot tell you that a bound descriptor is
/// read by the draw that follows. It also does not walk the renderer's call sites: a texture the renderer
/// never binds at all is invisible here, except for the small fixed table in
/// <see cref="PostProcessBindSites"/>, which exists to catch a bind site naming a sampler the shader does
/// not declare -- a case that silently records nothing.
/// </para>
/// <para>
/// <b>It compiles shaders and reads their SPIR-V. It submits nothing.</b> Every step is glslang and a
/// linear scan over words.
/// </para>
/// </remarks>
public static class ShaderBindingOracle
{
    /// <summary>
    /// Sampler names the post-process chain binds by name, per shader, so that a name no shader declares
    /// can be caught rather than recording nothing in silence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Maintained by hand against the <c>BindTexture</c> calls in <c>PostProcessRenderer</c>,
    /// <c>BloomRenderer</c>, <c>OutlineRenderer</c> and <c>Renderer</c>. It is a spot check on the call
    /// sites rather than a complete list of them; a name that moves without this moving shows up as a
    /// violation naming the shader, which is the outcome to want.
    /// </para>
    /// <para>
    /// Only samplers a shader declares under its <em>default</em> combos belong here, because that is
    /// what is compiled. <c>post_processing</c>'s <c>g_tBloom</c> and the depth-of-field variant of
    /// <c>msaa_resolve</c> are both behind a combo and so are out of reach of this check.
    /// </para>
    /// </remarks>
    public static readonly ImmutableArray<(string Shader, string Sampler)> PostProcessBindSites =
    [
        ("msaa_resolve", "g_tSourceMsaa"),
        ("depth_resolve", "g_tSourceDepthMsaa"),
        ("combine_luts", "g_tColorCorrection0"),
        ("combine_luts", "g_tColorCorrection1"),
        ("combine_luts", "g_tColorCorrection2"),
        ("combine_luts", "g_tColorCorrection3"),
        ("post_processing", "g_tColorBuffer"),
        ("post_processing", "g_tColorCorrectionLUT"),
        ("post_processing", "g_tBlueNoise"),
        ("downsample_bloomthreshold", "inputTexture"),
        ("gaussian_bloom_blur", "g_tSource"),
        ("histogram", "inputImage"),
        ("outline_post", "g_tStencilBuffer"),
    ];

    /// <summary>Runs every check over the renderer's own shaders.</summary>
    /// <param name="progress">Receives one line per shader, or <see langword="null"/> for none.</param>
    /// <param name="filter">Optional substring restricting which shaders are checked.</param>
    /// <returns>What the run found.</returns>
    public static ShaderBindingOracleReport Run(IProgress<string>? progress = null, string? filter = null)
    {
        var violations = ImmutableArray.CreateBuilder<string>();
        var uncompilable = ImmutableArray.CreateBuilder<string>();
        var divergences = ImmutableArray.CreateBuilder<string>();

        var seen = 0;
        var compiled = 0;
        var descriptors = 0;
        var textures = 0;

        var declaredByShader = new Dictionary<string, SpirvShaderInterface>(StringComparer.Ordinal);

        using var compiler = new SpirvCompiler();

        var names = SpirvShaderValidation.EnumerateShaders();

        if (filter != null)
        {
            names = [.. names.Where(name => name.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        }

        foreach (var shaderName in names)
        {
            seen++;

            ImmutableArray<SpirvShaderValidationResult> stages;

            try
            {
                stages = SpirvShaderValidation.CompileShader(shaderName, compiler, flavour: ShaderFlavour.Vulkan);
            }
            catch (ShaderLoader.ShaderCompilerException e)
            {
                uncompilable.Add(string.Create(CultureInfo.InvariantCulture, $"{shaderName}: preprocessing failed: {e.Message}"));
                continue;
            }

            var reflected = stages.Where(static s => s.Reflection != null).Select(static s => s.Reflection!).ToArray();

            foreach (var stage in stages.Where(static s => !s.Result.Success))
            {
                uncompilable.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{shaderName} [{stage.Stage}]: {stage.Result.PrimaryError?.ToString() ?? stage.Result.Status.ToString()}"));
            }

            if (reflected.Length == 0)
            {
                continue;
            }

            compiled++;

            // Whatever the module declares is a contract violation on its own terms first. This is the
            // check that now separates set 2 from set 3 by the sampler's name rather than accepting
            // either, which is what let a reserved texture sit in the material set unremarked.
            foreach (var stage in stages)
            {
                foreach (var problem in stage.ContractViolations)
                {
                    violations.Add(string.Create(CultureInfo.InvariantCulture, $"{shaderName} [{stage.Stage}]: {problem}"));
                }
            }

            SpirvShaderInterface declared;

            try
            {
                declared = SpirvShaderInterface.FromStages(reflected, shaderName);
            }
            catch (InvalidOperationException e)
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture, $"{shaderName}: {e.Message}"));
                continue;
            }

            declaredByShader[shaderName] = declared;

            CheckMergeKeptEverything(shaderName, reflected, declared, violations, ref descriptors);
            CheckTextureBindSlots(shaderName, reflected, declared, violations, ref textures);
            CheckMaterialSeeding(shaderName, reflected, declared, violations, divergences);
            CheckEmissionSurvivedTheCompiler(shaderName, stages, declared, violations);

            progress?.Report(string.Create(CultureInfo.InvariantCulture,
                $"{shaderName}: {declared.Descriptors.Length} descriptors, {declared.Textures.Length} textures, {declared.MaterialTextureNames.Length} from a material"));
        }

        CheckBindSitesResolve(declaredByShader, violations);

        return new ShaderBindingOracleReport(
            seen,
            compiled,
            descriptors,
            textures,
            violations.ToImmutable(),
            uncompilable.ToImmutable(),
            divergences.ToImmutable());
    }

    /// <summary>
    /// Every descriptor a stage declares survives the merge with its set, binding and kind intact.
    /// </summary>
    /// <remarks>The merge is where a shader's stages become one view, and a descriptor lost or moved
    /// here is a descriptor the renderer will never bind or will bind in the wrong place.</remarks>
    private static void CheckMergeKeptEverything(
        string shaderName,
        IEnumerable<SpirvReflectionResult> reflected,
        SpirvShaderInterface declared,
        ImmutableArray<string>.Builder violations,
        ref int descriptors)
    {
        foreach (var stage in reflected)
        {
            foreach (var raw in stage.DescriptorBindings)
            {
                descriptors++;

                if (!declared.TryGet(raw.Name, out var merged))
                {
                    violations.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{shaderName}: the module declares '{raw.Name}' (set={raw.Set}, binding={raw.Binding}) but the renderer's view of the shader does not contain it, so nothing can ever bind it."));
                    continue;
                }

                if (merged.Set != raw.Set || merged.Binding != raw.Binding || merged.Kind != raw.Kind)
                {
                    violations.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{shaderName}: the module declares '{raw.Name}' as a {raw.Kind} at set {raw.Set} binding {raw.Binding}, but the renderer would record a {merged.Kind} at set {merged.Set} binding {merged.Binding}."));
                }
            }
        }
    }

    /// <summary>
    /// A texture binds where its name says it does: a reserved sampler at its
    /// <see cref="ReservedTextureSlots"/> number in set 2, everything else in set 3.
    /// </summary>
    /// <remarks>
    /// Derived here from <see cref="MaterialLoader.ReservedTextureSlotByName"/> against the raw module,
    /// independently of what emission did. Set 2 and set 3 are not interchangeable -- one is bound
    /// scene-wide for whatever draws next and the other is the material's own -- and a texture in the
    /// wrong one binds silently rather than erroring.
    /// </remarks>
    private static void CheckTextureBindSlots(
        string shaderName,
        IEnumerable<SpirvReflectionResult> reflected,
        SpirvShaderInterface declared,
        ImmutableArray<string>.Builder violations,
        ref int textures)
    {
        foreach (var stage in reflected)
        {
            foreach (var raw in stage.DescriptorBindings)
            {
                if (!SpirvShaderInterface.IsTexture(raw.Kind))
                {
                    continue;
                }

                textures++;

                if (!declared.TryGetTexture(raw.Name, out var bound))
                {
                    violations.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{shaderName}: '{raw.Name}' is a {raw.Kind} in the module, but the renderer does not treat it as a texture and would bind nothing for it."));
                    continue;
                }

                if (MaterialLoader.ReservedTextureSlotByName.TryGetValue(raw.Name, out var slot))
                {
                    if (bound.Set != DescriptorSets.ReservedTextures || bound.Binding != (int)slot)
                    {
                        violations.Add(string.Create(CultureInfo.InvariantCulture,
                            $"{shaderName}: '{raw.Name}' is the reserved texture {slot}, bound scene-wide at set {DescriptorSets.ReservedTextures} binding {(int)slot}, but the renderer would record set {bound.Set} binding {bound.Binding}."));
                    }

                    continue;
                }

                if (bound.Set != DescriptorSets.MaterialTextures)
                {
                    violations.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{shaderName}: '{raw.Name}' is not a reserved texture, so it belongs to the material set {DescriptorSets.MaterialTextures}, but the renderer would record set {bound.Set} binding {bound.Binding}."));
                }
            }
        }
    }

    /// <summary>
    /// The names a material is asked for cover the set 3 textures exactly once each, and each one is
    /// recorded at the slot the module gives it.
    /// </summary>
    /// <remarks>
    /// This is the check the whole change exists for. It also measures the old rule alongside the new
    /// one: a sequential count over the surviving textures is what the bind side used to assign, and
    /// every place the two differ is a texture that used to bind where a different one belonged.
    /// </remarks>
    private static void CheckMaterialSeeding(
        string shaderName,
        IEnumerable<SpirvReflectionResult> reflected,
        SpirvShaderInterface declared,
        ImmutableArray<string>.Builder violations,
        ImmutableArray<string>.Builder divergences)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stage in reflected)
        {
            foreach (var raw in stage.DescriptorBindings)
            {
                if (SpirvShaderInterface.IsTexture(raw.Kind)
                    && raw.Set == DescriptorSets.MaterialTextures
                    && !MaterialLoader.IsReservedTexture(raw.Name))
                {
                    expected.Add(raw.Name);
                }
            }
        }

        var seeded = declared.MaterialTextureNames;

        foreach (var name in expected)
        {
            if (!seeded.Contains(name, StringComparer.Ordinal))
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{shaderName}: '{name}' is a material texture in the module, but the renderer would not ask a material for it, so it can only ever bind its fallback."));
            }
        }

        for (var i = 0; i < seeded.Length; i++)
        {
            var name = seeded[i];

            if (!expected.Contains(name))
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{shaderName}: the renderer would ask a material for '{name}', which the module does not declare as a material texture."));
                continue;
            }

            if (seeded.IndexOf(name, StringComparer.Ordinal) != i)
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{shaderName}: '{name}' appears more than once in the material texture list, so one binding would overwrite the other."));
                continue;
            }

            var moduleSlot = declared.MaterialTextures.First(d => string.Equals(d.Name, name, StringComparison.Ordinal)).Binding;

            if (!declared.TryGetMaterialTextureSlot(name, out var recorded))
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{shaderName}: the renderer would ask a material for '{name}' but has no slot to bind it at, so the texture is dropped."));
                continue;
            }

            if (recorded != moduleSlot)
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{shaderName}: '{name}' is declared at set {DescriptorSets.MaterialTextures} binding {moduleSlot}, but the renderer would record binding {recorded}."));
                continue;
            }

            if (recorded != i)
            {
                divergences.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{shaderName}: '{name}' is declared at binding {recorded}; a sequential count over the surviving material textures puts it at {i}."));
            }
        }
    }

    /// <summary>
    /// What the preprocessor numbered a sampler is what the module ends up declaring.
    /// </summary>
    /// <remarks>
    /// The one link neither side of the renderer can check alone: emission writes a decoration into
    /// text, glslang compiles it, and reflection reads it back out of the binary. A sampler the compiler
    /// eliminated is absent rather than renumbered, which is expected and not a violation; a sampler
    /// that survived at a different number is not.
    /// </remarks>
    private static void CheckEmissionSurvivedTheCompiler(
        string shaderName,
        IEnumerable<SpirvShaderValidationResult> stages,
        SpirvShaderInterface declared,
        ImmutableArray<string>.Builder violations)
    {
        foreach (var stage in stages)
        {
            foreach (var (name, emitted) in stage.EmittedMaterialTextureBindings)
            {
                if (!declared.TryGetTexture(name, out var bound))
                {
                    continue; // Eliminated by the compiler: nothing samples it in this variant.
                }

                if (bound.Set != DescriptorSets.MaterialTextures || bound.Binding != emitted)
                {
                    violations.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{shaderName}: emission numbered '{name}' as set {DescriptorSets.MaterialTextures} binding {emitted}, but the compiled module declares set {bound.Set} binding {bound.Binding}."));
                }
            }

            // One table per shader, shared by its stages, so one pass over it is enough.
            break;
        }
    }

    /// <summary>Every sampler the post-process chain binds by name is a sampler its shader declares.</summary>
    /// <remarks>
    /// A table entry naming a shader that does not exist is itself a violation. Skipping those quietly
    /// is how the entry for <c>outline_edge</c>, a shader by that name never having existed, sat here
    /// checking nothing.
    /// </remarks>
    private static void CheckBindSitesResolve(
        Dictionary<string, SpirvShaderInterface> declaredByShader,
        ImmutableArray<string>.Builder violations)
    {
        var exists = SpirvShaderValidation.EnumerateShaders();

        foreach (var (shaderName, sampler) in PostProcessBindSites)
        {
            if (!exists.Contains(shaderName, StringComparer.Ordinal))
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{shaderName}: this run's bind site table names a shader that does not exist, so nothing about '{sampler}' was checked."));
                continue;
            }

            if (!declaredByShader.TryGetValue(shaderName, out var declared))
            {
                continue; // The shader did not compile; already reported as such.
            }

            if (!declared.TryGetTexture(sampler, out _))
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{shaderName}: the renderer binds '{sampler}' by name, but the module declares no such texture, so the bind records nothing."));
            }
        }
    }

    /// <summary>Formats a run as a report.</summary>
    /// <param name="report">What the run found.</param>
    /// <returns>A multi-line summary.</returns>
    public static string Format(in ShaderBindingOracleReport report)
    {
        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture,
            $"{report.ShadersCompiled}/{report.ShadersSeen} shaders compiled, {report.DescriptorsChecked} descriptors checked, {report.TexturesChecked} of them textures");
        text.AppendLine();

        text.AppendLine(report.Succeeded
            ? "PASS: every descriptor the renderer would record is where the module declares it."
            : string.Create(CultureInfo.InvariantCulture, $"FAIL: {report.Violations.Length} disagreements."));

        foreach (var violation in report.Violations)
        {
            text.Append("  violation: ").AppendLine(violation);
        }

        foreach (var divergence in report.SequentialDivergences)
        {
            text.Append("  divergence: ").AppendLine(divergence);
        }

        foreach (var failure in report.Uncompilable)
        {
            text.Append("  uncompiled: ").AppendLine(failure);
        }

        return text.ToString();
    }
}
