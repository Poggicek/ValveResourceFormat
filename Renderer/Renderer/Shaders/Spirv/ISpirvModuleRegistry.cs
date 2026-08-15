using ValveResourceFormat.Renderer.RHI;

namespace ValveResourceFormat.Renderer.Shaders.Spirv;

/// <summary>
/// Records the SPIR-V interface of a shader module, so a pipeline layout can be derived from what the
/// shader declares rather than guessed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than a type test.</b> The reflecting entry point lives on the Vulkan
/// pipeline device, not on <see cref="IDevice"/>, so a caller holding an <see cref="IDevice"/> can only
/// reach it by knowing the concrete type. That test fails the moment the device is anything but that
/// exact class, and two shapes already in the tree are:
/// </para>
/// <list type="bullet">
/// <item><description>a <em>decorator</em> that implements <see cref="IDevice"/> and forwards, such as
/// the golden suite's call census;</description></item>
/// <item><description>a <em>composite</em> that is a sibling of the pipeline device and holds one
/// privately, forwarding only pipeline creation to it, such as the golden suite's combined
/// device.</description></item>
/// </list>
/// <para>
/// Neither is reachable by a type test, and both silently produce modules whose interface was never
/// recorded &#8212; which surfaces much later as a pipeline refusing to be built. A capability the device
/// answers for itself works through any number of such layers, because each one forwards it the same
/// way it already forwards everything else.
/// </para>
/// <para>
/// The refusal it protects is deliberate: a pipeline layout that disagrees with its shader binds the
/// wrong resource without erroring, so the pipeline layer throws rather than guessing. This interface
/// is how a caller supplies the information that keeps that from happening, not a way around it.
/// </para>
/// </remarks>
public interface ISpirvModuleRegistry
{
    /// <summary>Records the interface a module's SPIR-V declares.</summary>
    /// <param name="shaderModule">The module the code was compiled into.</param>
    /// <param name="spirv">The SPIR-V the module was created from.</param>
    /// <remarks>Implementations are expected to be idempotent: the same module registered twice is not
    /// an error, because reflection is keyed on the module's content hash.</remarks>
    void RegisterModuleInterface(IShaderModule shaderModule, ReadOnlySpan<byte> spirv);
}
