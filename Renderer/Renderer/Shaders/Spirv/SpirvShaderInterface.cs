using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using ValveResourceFormat.Renderer.RHI;

namespace ValveResourceFormat.Renderer.Shaders.Spirv;

/// <summary>
/// What one shader's stages jointly declare, merged into the single view the renderer binds against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The bind side used to ask OpenGL what a shader contains &#8212;
/// <c>glGetUniformLocation</c> for whether a sampler exists, and the walk order of
/// <see cref="RenderMaterial.Textures"/> for which slot it takes. On the SPIR-V path there is no
/// program to ask, so every such question answered "no" and nothing was recorded. Worse, the two
/// numbering schemes were only related by argument: emission numbers a material's samplers in source
/// declaration order, and the bind side numbered them sequentially over whatever the linker had kept.
/// Those agree only when the compiler drops nothing, and it routinely drops samplers behind a dead
/// combo &#8212; at which point a material's normal map binds where its albedo belongs.
/// </para>
/// <para>
/// Deriving both from the module removes the question. The set and binding here are the ones glslang
/// wrote into the SPIR-V from the <c>layout(set=, binding=)</c> decorations <c>ShaderParser.VulkanGlsl</c>
/// emitted, so what the renderer records is what the shader declares, by construction rather than by
/// coincidence.
/// </para>
/// <para>
/// <b>Nothing here is OpenGL's.</b> This is built when the modules are compiled, before any pipeline
/// exists, and reading it issues no graphics call of any kind.
/// </para>
/// </remarks>
public sealed class SpirvShaderInterface
{
    /// <summary>Gets every descriptor the shader's stages declare, ordered by set then binding.</summary>
    /// <remarks>A descriptor declared by more than one stage appears once. The stages have to agree on
    /// where it lives, which <see cref="FromStages"/> enforces.</remarks>
    public ImmutableArray<SpirvDescriptorBinding> Descriptors { get; }

    /// <summary>Gets the descriptors that hold a texture, ordered by set then binding.</summary>
    /// <remarks>Storage images are not among them: an image unit is a separate index space living in
    /// <see cref="DescriptorSets.StorageImages"/>, and it is bound through
    /// <see cref="ICommandList.BindStorageTexture"/> rather than through a texture bind.</remarks>
    public ImmutableArray<SpirvDescriptorBinding> Textures { get; }

    /// <summary>
    /// Gets the textures a material supplies, those in <see cref="DescriptorSets.MaterialTextures"/>,
    /// ordered by binding.
    /// </summary>
    /// <remarks>
    /// The source of set 3 numbering for <see cref="RenderMaterial.CollectTextureBindings"/>. The
    /// binding numbers are the shader's own and are not necessarily contiguous: a sampler the compiler
    /// dropped leaves its number unused rather than closing the gap, which is precisely the case a
    /// sequential count over the surviving samplers gets wrong.
    /// </remarks>
    public ImmutableArray<SpirvDescriptorBinding> MaterialTextures { get; }

    /// <summary>
    /// Gets the names of the textures a material is expected to supply, ordered by binding.
    /// </summary>
    /// <remarks>
    /// <see cref="MaterialTextures"/> less any reserved sampler that somehow landed in set 3, which is
    /// an emission fault rather than something a material can satisfy. This is the list
    /// <see cref="Shader.Default"/>'s textures are seeded from, so it decides which names a
    /// material is asked for at all; <see cref="TryGetMaterialTextureSlot"/> decides where each goes.
    /// The two are kept apart so that neither can be checked against a copy of itself.
    /// </remarks>
    public ImmutableArray<string> MaterialTextureNames { get; }

    /// <summary>
    /// Gets every vertex input the shader reads, ordered by location. Empty for a shader with no vertex
    /// stage.
    /// </summary>
    /// <remarks>
    /// What a mesh's vertex layout is completed against. The renderer's shaders choose their UV set with a
    /// runtime uniform rather than with a combo, so <c>vTEXCOORD1</c> is read whether or not the mesh has a
    /// second UV stream, and a shader drawn over arbitrary geometry &#8212; <c>error</c> &#8212; reads
    /// whatever it reads no matter what the mesh carries. OpenGL answers an unsupplied attribute with
    /// <c>(0, 0, 0, 1)</c> and Vulkan has no equivalent, so the difference has to be made up by supplying
    /// the input, which needs this list to know which inputs those are. It has to be the shader's own
    /// reads: a material's input signature does not cover <c>error</c>, whose material never loads.
    /// </remarks>
    public ImmutableArray<SpirvVertexInput> VertexInputs { get; }

    /// <summary>Gets the locations <see cref="VertexInputs"/> occupy, as a bitmask.</summary>
    /// <remarks>
    /// Locations 31 and above are absent. The renderer tracks claimed locations in an <see cref="int"/>
    /// bitmask throughout &#8212; <c>GPUMeshBufferCache.CreateVertexArrayObject</c> and
    /// <c>MeshBatchRenderer.DescribeVertexInput</c> both do &#8212; and shifting past that would alias a low
    /// location rather than name a high one. Dropping such a location here leaves it unsupplied, which the
    /// pipeline's interface check then reports by name; aliasing it would silently point some other
    /// attribute at the wrong buffer.
    /// </remarks>
    public int VertexInputLocations { get; }

    /// <summary>The highest location representable in the renderer's location bitmasks.</summary>
    private const int MaxLocation = 30;

    private readonly FrozenDictionary<string, SpirvDescriptorBinding> ByName;

    private SpirvShaderInterface(ImmutableArray<SpirvDescriptorBinding> descriptors, ImmutableArray<SpirvVertexInput> vertexInputs)
    {
        Descriptors = descriptors;
        Textures = [.. descriptors.Where(static d => IsTexture(d.Kind))];
        MaterialTextures = [.. Textures.Where(static d => d.Set == DescriptorSets.MaterialTextures)];
        MaterialTextureNames = [.. MaterialTextures.Where(static d => !MaterialLoader.IsReservedTexture(d.Name)).Select(static d => d.Name)];
        ByName = descriptors.ToFrozenDictionary(static d => d.Name, StringComparer.Ordinal);
        VertexInputs = vertexInputs;

        var locations = 0;

        foreach (var input in vertexInputs)
        {
            // A matrix input takes one location per column, exactly as the pipeline's interface check
            // expands it. Counted the same way here so the two agree on which locations are read.
            for (var i = 0; i < Math.Max(1, input.LocationCount); i++)
            {
                var location = input.Location + i;

                if (location >= 0 && location <= MaxLocation)
                {
                    locations |= 1 << location;
                }
            }
        }

        VertexInputLocations = locations;
    }

    /// <summary>Finds the slot within <see cref="DescriptorSets.MaterialTextures"/> a named texture takes.</summary>
    /// <param name="name">The sampler's declared name.</param>
    /// <param name="binding">Receives the slot.</param>
    /// <returns><see langword="true"/> when the shader declares it there.</returns>
    /// <remarks>
    /// The number is the shader's, not a count. Emission numbered every sampler the source declared and
    /// the compiler is free to eliminate one, so the surviving numbers have gaps; counting over the
    /// survivors instead would shift every later texture down one and bind a material's normal map
    /// where its albedo belongs.
    /// </remarks>
    public bool TryGetMaterialTextureSlot(string name, out int binding)
    {
        if (TryGetTexture(name, out var texture) && texture.Set == DescriptorSets.MaterialTextures)
        {
            binding = texture.Binding;
            return true;
        }

        binding = -1;
        return false;
    }

    /// <summary>Returns whether a descriptor kind is one a texture bind can satisfy.</summary>
    /// <param name="kind">The reflected kind.</param>
    public static bool IsTexture(SpirvResourceKind kind)
        => kind is SpirvResourceKind.CombinedImageSampler or SpirvResourceKind.SampledImage;

    /// <summary>Finds where a named descriptor lives.</summary>
    /// <param name="name">The declared name.</param>
    /// <param name="binding">Receives the descriptor.</param>
    /// <returns><see langword="true"/> when some stage of this shader declares it.</returns>
    /// <remarks>
    /// A miss means the module does not contain that name at all, which covers both a sampler the
    /// shader never declared and one the compiler eliminated because nothing reachable sampled it.
    /// Both are cases where binding a texture would write a descriptor no draw reads, so a caller
    /// should record nothing rather than guess a slot.
    /// </remarks>
    public bool TryGet(string name, out SpirvDescriptorBinding binding) => ByName.TryGetValue(name, out binding);

    /// <summary>Finds where a named texture binds.</summary>
    /// <param name="name">The sampler's declared name.</param>
    /// <param name="binding">Receives the descriptor.</param>
    /// <returns><see langword="true"/> when the shader declares it and it is a texture.</returns>
    public bool TryGetTexture(string name, out SpirvDescriptorBinding binding)
        => TryGet(name, out binding) && IsTexture(binding.Kind);

    /// <summary>Merges what every stage of one shader declares.</summary>
    /// <param name="stages">The reflected stages.</param>
    /// <param name="shaderName">The shader's name, used only in the message when the stages disagree.</param>
    /// <returns>The merged view.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stages"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Two stages put the same name in different places.</exception>
    /// <remarks>
    /// Disagreement is treated as fatal rather than resolved by precedence. The stages of one shader are
    /// preprocessed into a single <see cref="ShaderLoader.ParsedShaderData"/> and take their sampler
    /// numbering from the one <see cref="ShaderLoader.ParsedShaderData.MaterialTextureBindings"/> table,
    /// so they cannot legitimately differ; if they do, the pipeline's layout would have to pick one and
    /// the other stage would sample a descriptor nobody wrote.
    /// </remarks>
    public static SpirvShaderInterface FromStages(IEnumerable<SpirvReflectionResult> stages, string shaderName)
    {
        ArgumentNullException.ThrowIfNull(stages);

        var merged = new Dictionary<string, SpirvDescriptorBinding>(StringComparer.Ordinal);

        // Only a vertex module reflects any, so this is a concatenation rather than a merge in practice.
        // Deduplicated by location all the same, because two inputs at one location would each contribute a
        // completion attribute and a pipeline may not name a location twice.
        var inputsByLocation = new Dictionary<int, SpirvVertexInput>();

        foreach (var stage in stages)
        {
            if (stage == null)
            {
                continue;
            }

            foreach (var input in stage.VertexInputs)
            {
                inputsByLocation.TryAdd(input.Location, input);
            }

            foreach (var binding in stage.DescriptorBindings)
            {
                if (!merged.TryGetValue(binding.Name, out var existing))
                {
                    merged.Add(binding.Name, binding);
                    continue;
                }

                if (existing.Set != binding.Set || existing.Binding != binding.Binding || existing.Kind != binding.Kind)
                {
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                        $"Shader '{shaderName}' declares '{binding.Name}' as a {existing.Kind} at set {existing.Set} binding {existing.Binding} in one stage and as a {binding.Kind} at set {binding.Set} binding {binding.Binding} in another. One pipeline layout cannot satisfy both."));
                }
            }
        }

        return new SpirvShaderInterface(
        [
            .. merged.Values
                .OrderBy(static d => d.Set)
                .ThenBy(static d => d.Binding)
                .ThenBy(static d => d.Name, StringComparer.Ordinal)
        ],
        [
            .. inputsByLocation.Values.OrderBy(static i => i.Location)
        ]);
    }
}
