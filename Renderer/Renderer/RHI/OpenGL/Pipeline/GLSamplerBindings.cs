using System.Collections.Immutable;
using ValveResourceFormat.Renderer.Materials;
using GLApi = OpenTK.Graphics.OpenGL.GL;

namespace ValveResourceFormat.Renderer.RHI.OpenGL;

/// <summary>
/// Where each of a program's sampler uniforms lives in the contract's descriptor scheme, and which
/// OpenGL texture unit that works out to.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece that lets <see cref="ICommandList.BindTexture"/> stand on its own. That call
/// carries a descriptor set and a binding but no uniform name, so on OpenGL it can bind a texture to a
/// unit but cannot tell the sampler which unit to read. Callers were doing both jobs &#8212;
/// <see cref="Shader.SetTexture(int, string, RenderTexture)"/> on the GL path for the uniform, and
/// <c>BindTexture</c> on the RHI path for the texture &#8212; which left the RHI path depending on GL
/// uniform state it was supposed to be replacing.
/// </para>
/// <para>
/// The fix is that a texture unit is a pure function of the descriptor set and binding, so a sampler's
/// unit follows from its binding rather than from the draw. The reserved globals settle at link time in
/// <see cref="PointReservedSamplersAtUnits"/>, since their units are the same for every program. The
/// per-material samplers are aimed by <see cref="EnsurePointedAt"/> as they are bound, diffed so a batch
/// pays once. Either way the caller makes one call and writes no uniform of its own.
/// </para>
/// <para>
/// The bindings are read from the program and from the renderer's existing conventions rather than from
/// SPIR-V reflection. <see cref="Shaders.Spirv.SpirvReflection"/> reports the same thing, but it needs
/// the module, and on the OpenGL path there is none: programs are linked from GLSL and the SPIR-V
/// toolchain is a validation step that needs a native shaderc. Deriving the map from the program keeps
/// it available at runtime with nothing to deploy, and keeps it in agreement with
/// <see cref="RenderMaterial.CollectTextureBindings"/> by construction, because both walk the same list
/// in the same order.
/// </para>
/// </remarks>
public sealed class GLSamplerBindings
{
    /// <summary>One sampler uniform, located in both descriptor terms and OpenGL terms.</summary>
    /// <param name="Name">The sampler uniform name.</param>
    /// <param name="DescriptorSet">
    /// <see cref="DescriptorSets.ReservedTextures"/> for a global texture, or
    /// <see cref="DescriptorSets.MaterialTextures"/> for one the material supplies.
    /// </param>
    /// <param name="Binding">The slot within that set.</param>
    /// <param name="TextureUnit">The OpenGL texture unit that set and binding resolve to.</param>
    /// <param name="UniformLocation">The sampler uniform's location in the program.</param>
    public readonly record struct SamplerBinding(
        string Name,
        int DescriptorSet,
        int Binding,
        int TextureUnit,
        int UniformLocation);

    private readonly Dictionary<string, SamplerBinding> byName;
    private readonly Dictionary<int, int> pointedAt = [];
    private readonly int program;

    /// <summary>Gets every sampler this program declares that the descriptor scheme covers, in set then
    /// binding order.</summary>
    public ImmutableArray<SamplerBinding> Bindings { get; }

    /// <summary>
    /// Gets the OpenGL texture unit a descriptor set and binding resolve to.
    /// </summary>
    /// <param name="descriptorSet">The descriptor set.</param>
    /// <param name="binding">The binding within it.</param>
    /// <returns>The texture unit.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="descriptorSet"/> holds no textures.</exception>
    /// <remarks>
    /// OpenGL has one flat texture unit namespace where the contract has two sets, so the set decides the
    /// base. This has to stay identical to the same mapping inside the command list's
    /// <see cref="ICommandList.BindTexture"/>: a sampler pointed at one unit while its texture binds to
    /// another samples whatever happens to be there, without erroring.
    /// </remarks>
    public static int UnitFor(int descriptorSet, int binding) => descriptorSet switch
    {
        DescriptorSets.ReservedTextures => binding,
        DescriptorSets.MaterialTextures => RenderMaterial.TextureUnitStart + binding,
        _ => throw new ArgumentOutOfRangeException(nameof(descriptorSet), descriptorSet,
            $"Only sets {DescriptorSets.ReservedTextures} and {DescriptorSets.MaterialTextures} hold textures."),
    };

    /// <summary>Reads a linked program's sampler bindings.</summary>
    /// <param name="shader">The program to read. Must already be linked.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shader"/> is <see langword="null"/>.</exception>
    public GLSamplerBindings(Shader shader)
    {
        ArgumentNullException.ThrowIfNull(shader);

        program = shader.Program;

        var bindings = ImmutableArray.CreateBuilder<SamplerBinding>();

        // Set 2, the global textures. Table driven, because the reserved samplers include array and
        // shadow samplers that the program's own uniform walk does not classify.
        foreach (var (name, slot) in MaterialLoader.ReservedTextureSlotByName)
        {
            var location = shader.GetUniformLocation(name);

            if (location > -1)
            {
                bindings.Add(new SamplerBinding(name, DescriptorSets.ReservedTextures, (int)slot, UnitFor(DescriptorSets.ReservedTextures, (int)slot), location));
            }
        }

        // Set 3, the per-material textures. This walk assigns the binding numbers, and has to stay in
        // step with RenderMaterial.CollectTextureBindings and RenderMaterial.Render, which walk the same
        // dictionary and skip on the same condition. Binding N here is the texture those paths put on
        // texture unit TextureUnitStart + N.
        var materialBinding = 0;

        foreach (var (name, _) in shader.Default.Textures)
        {
            var location = shader.GetUniformLocation(name);

            if (location < 0)
            {
                continue;
            }

            bindings.Add(new SamplerBinding(name, DescriptorSets.MaterialTextures, materialBinding, UnitFor(DescriptorSets.MaterialTextures, materialBinding), location));
            materialBinding++;
        }

        Bindings = [.. bindings];

        byName = new Dictionary<string, SamplerBinding>(Bindings.Length, StringComparer.Ordinal);

        foreach (var binding in Bindings)
        {
            byName[binding.Name] = binding;
        }
    }

    /// <summary>Finds where a named sampler binds.</summary>
    /// <param name="name">The sampler uniform name.</param>
    /// <param name="binding">Receives the binding.</param>
    /// <returns><see langword="true"/> when the program declares it and the descriptor scheme covers it.</returns>
    /// <remarks>
    /// <para>
    /// A sampler outside the scheme returns <see langword="false"/>. Those exist: the scheme covers the
    /// reserved globals and the material textures, and a shader is free to declare a sampler that is
    /// neither, such as the particle renderers' <c>uTexture</c>. Such a sampler keeps its unit from
    /// whoever binds it.
    /// </para>
    /// <para>
    /// A set 3 binding reported here is the slot <see cref="RenderMaterial"/> would assign, which only
    /// means anything for a shader actually drawn through a material. A shader that binds its own
    /// textures is free to ignore it and use units of its own, and <c>post_processing</c> does exactly
    /// that with <c>g_tColorBuffer</c>. Treat a binding as an offer, not an obligation.
    /// </para>
    /// </remarks>
    public bool TryGetBinding(string name, out SamplerBinding binding) => byName.TryGetValue(name, out binding);

    /// <summary>
    /// Points the reserved global samplers of <see cref="DescriptorSets.ReservedTextures"/> at their
    /// texture units.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called once, when the program links. A reserved sampler's unit is fixed by
    /// <see cref="ReservedTextureSlots"/> for every program that declares it, so it never has to be
    /// aimed again.
    /// </para>
    /// <para>
    /// Deliberately does not touch the per-material samplers of
    /// <see cref="DescriptorSets.MaterialTextures"/>. Their binding numbers are what
    /// <see cref="RenderMaterial"/> assigns, and not every shader that declares such a sampler is drawn
    /// through a material: <c>post_processing</c> binds its own <c>g_tColorBuffer</c> to a unit of its
    /// choosing. Aiming those here would point a sampler somewhere its texture never binds for any
    /// consumer that does its own binding, so they are aimed by <see cref="EnsurePointedAt"/> when a
    /// binding is actually recorded.
    /// </para>
    /// </remarks>
    public void PointReservedSamplersAtUnits()
    {
        foreach (var binding in Bindings)
        {
            if (binding.DescriptorSet == DescriptorSets.ReservedTextures)
            {
                GLApi.ProgramUniform1(program, binding.UniformLocation, binding.TextureUnit);
            }
        }
    }

    /// <summary>
    /// Makes a sampler read the given texture unit, skipping the call when it already does.
    /// </summary>
    /// <param name="uniformLocation">The sampler uniform's location.</param>
    /// <param name="textureUnit">The unit it should read.</param>
    /// <remarks>
    /// The per-draw half of aiming a sampler, for the bindings that link time cannot settle. Diffed
    /// against what this program was last told, so a batch that binds the same textures repeatedly pays
    /// for the first draw only.
    /// </remarks>
    public void EnsurePointedAt(int uniformLocation, int textureUnit)
    {
        if (pointedAt.TryGetValue(uniformLocation, out var current) && current == textureUnit)
        {
            return;
        }

        GLApi.ProgramUniform1(program, uniformLocation, textureUnit);
        pointedAt[uniformLocation] = textureUnit;
    }

    /// <summary>Forgets what each sampler was last pointed at.</summary>
    /// <remarks>For when the program's uniform state changed behind this map's back, which outside of
    /// shader hot reload it does not.</remarks>
    public void Invalidate() => pointedAt.Clear();
}
