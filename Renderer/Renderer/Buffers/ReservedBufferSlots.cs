using System.Collections.Frozen;

namespace ValveResourceFormat.Renderer.Buffers;

#pragma warning disable CA1069 // Enum values should not be duplicated

/// <summary>
/// Reserved GPU buffer binding slots for uniform and storage buffers.
/// </summary>
public enum ReservedBufferSlots
{
    // ubo

    /// <summary>View constants UBO slot.</summary>
    View = 0,
    /// <summary>Lighting constants UBO slot.</summary>
    Lighting = 1,
    /// <summary>Environment map array UBO slot.</summary>
    EnvironmentMap = 2,
    /// <summary>Light probe volume array UBO slot.</summary>
    LightProbe = 3,
    /// <summary>Frustum planes UBO slot.</summary>
    FrustumPlanes = 4,
    /// <summary>Shared constants for the tile and depth bin cull passes.</summary>
    CullParams = 5,
    /// <summary>Per scene cull mask layout read by the shading passes.</summary>
    LightCull = 6,
    /// <summary>Packed material properties.</summary>
    Globals = 7,

    // ssbo

    /// <summary>Per-object data SSBO slot.</summary>
    Objects = 0,
    /// <summary>Transform matrices SSBO slot.</summary>
    Transforms = 1,
    /// <summary>Histogram SSBO slot.</summary>
    Histogram = 2,
    /// <summary>Average luminance SSBO slot.</summary>
    AverageLuminance = 3,
    /// <summary>Aggregate indirect draw commands SSBO slot.</summary>
    AggregateDraws = 4,
    /// <summary>Aggregate draw bounding boxes SSBO slot.</summary>
    AggregateDrawBounds = 5,
    /// <summary>Meshlet cull data SSBO slot.</summary>
    AggregateMeshlets = 6,
    /// <summary>Occluded bounds debug output SSBO slot.</summary>
    OccludedBoundsDebug = 7,
    /// <summary>Compacted draw commands SSBO slot.</summary>
    CompactedDraws = 8,
    /// <summary>Compacted draw counts SSBO slot.</summary>
    CompactedCounts = 9,
    /// <summary>Compaction request descriptors SSBO slot.</summary>
    CompactionRequests = 10,
    /// <summary>Bone transform matrices SSBO slot.</summary>
    BoneTransforms = 11,
    /// <summary>Barn light constants SSBO slot.</summary>
    BarnLights = 12,
    /// <summary>Tile and depth slice bit masks the cull passes produce.</summary>
    CullBits = 13,
    /// <summary>Screen space cull items: barn light faces, env map probes and light probe volumes.</summary>
    CullItems = 14,
    /// <summary>Convex hull vertices referenced by <see cref="CullItems"/>.</summary>
    CullPlanes = 15,

    /// <summary>Guaranteed minimum binding point count in OpenGL 4.6.</summary>
    Max = 8,
}

#pragma warning restore CA1069 // Enum values should not be duplicated

/// <summary>
/// The interface block each <see cref="ReservedBufferSlots"/> value is declared as in the shaders, so a
/// reflected module can be checked against the slot the renderer binds it at.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a name table rather than a derivation.</b> A material texture's slot used to be a count over
/// whatever samplers the compiler kept, which is a different quantity from the number emission wrote, and
/// deriving it from the module was the only way to make the two agree. A buffer is not like that. Its
/// binding is a literal in one shared include &#8212; <c>common/ViewConstants.slang</c> declares
/// <c>binding = 0</c> once for every shader in the tree &#8212; and the matching number lives in
/// <see cref="ReservedBufferSlots"/>. There is nothing to derive: there are two hand-written copies of one
/// number, and what they need is to be compared.
/// </para>
/// <para>
/// The set is not compared here because it is not a copy of anything.
/// <c>ShaderParser.VulkanGlsl.DecorateInterfaceBlock</c> writes it from the block's storage qualifier, so
/// a <c>buffer</c> block is in <see cref="RHI.DescriptorSets.StorageBuffers"/> and a <c>uniform</c> block
/// in <see cref="RHI.DescriptorSets.UniformBuffers"/> by construction.
/// <c>SpirvReflection.ValidateDescriptorSets</c> checks that anyway, since a shader can be written by
/// hand against the Vulkan flavour.
/// </para>
/// <para>
/// A name absent from these tables is a block the renderer has no buffer for, and nothing would ever bind
/// it. That is worth reporting rather than passing over, which is why the lookup is by exact name and
/// misses are treated as findings by the callers.
/// </para>
/// </remarks>
public static class ReservedBufferBlocks
{
    /// <summary>Gets the uniform block names, mapped to the slot in
    /// <see cref="RHI.DescriptorSets.UniformBuffers"/> the renderer binds them at.</summary>
    public static FrozenDictionary<string, ReservedBufferSlots> UniformBlocks { get; } =
        new Dictionary<string, ReservedBufferSlots>(StringComparer.Ordinal)
        {
            ["ViewConstants"] = ReservedBufferSlots.View,
            ["LightingConstants"] = ReservedBufferSlots.Lighting,
            ["EnvMapArray"] = ReservedBufferSlots.EnvironmentMap,
            ["LightProbeVolumeArray"] = ReservedBufferSlots.LightProbe,
            ["FrustumPlanes"] = ReservedBufferSlots.FrustumPlanes,
            ["CullParams_t"] = ReservedBufferSlots.CullParams,
            ["LightCullConstants"] = ReservedBufferSlots.LightCull,

            // Generated per shader by GlobalsLayout rather than written in a .slang file, and the only
            // entry here whose binding really is derived: the block source is built from
            // ReservedBufferSlots.Globals, so this pair cannot drift.
            ["Globals"] = ReservedBufferSlots.Globals,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Gets the storage block names, mapped to the slot in
    /// <see cref="RHI.DescriptorSets.StorageBuffers"/> the renderer binds them at.</summary>
    /// <remarks>Two names share <see cref="ReservedBufferSlots.CullBits"/>: the cull passes declare the
    /// mask buffer as <c>OutputCullBitsBuffer</c> to write it and the shading passes as
    /// <c>LightCullBitsBuffer</c> to read it. One buffer, one slot, two declarations.</remarks>
    public static FrozenDictionary<string, ReservedBufferSlots> StorageBlocks { get; } =
        new Dictionary<string, ReservedBufferSlots>(StringComparer.Ordinal)
        {
            ["g_objectBuffer"] = ReservedBufferSlots.Objects,
            ["g_transformBuffer"] = ReservedBufferSlots.Transforms,
            ["HistogramBuffer"] = ReservedBufferSlots.Histogram,
            ["Luminance"] = ReservedBufferSlots.AverageLuminance,
            ["DrawCommands"] = ReservedBufferSlots.AggregateDraws,
            ["DrawCallBounds"] = ReservedBufferSlots.AggregateDrawBounds,
            ["MeshletInfoBuffer"] = ReservedBufferSlots.AggregateMeshlets,
            ["OccludedBoundsDebugBuffer"] = ReservedBufferSlots.OccludedBoundsDebug,
            ["CompactDrawCommands"] = ReservedBufferSlots.CompactedDraws,
            ["DrawCommandCounts"] = ReservedBufferSlots.CompactedCounts,
            ["CompactionRequests"] = ReservedBufferSlots.CompactionRequests,
            ["g_boneTransformBuffer"] = ReservedBufferSlots.BoneTransforms,
            ["BarnLightBuffer"] = ReservedBufferSlots.BarnLights,
            ["LightCullBitsBuffer"] = ReservedBufferSlots.CullBits,
            ["OutputCullBitsBuffer"] = ReservedBufferSlots.CullBits,
            ["CullItemsBuffer"] = ReservedBufferSlots.CullItems,
            ["CullPlanesBuffer"] = ReservedBufferSlots.CullPlanes,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Finds the slot a declared block binds at.</summary>
    /// <param name="name">The block name the module declares.</param>
    /// <param name="storage"><see langword="true"/> for a storage block, <see langword="false"/> for a
    /// uniform one.</param>
    /// <param name="slot">Receives the slot.</param>
    /// <returns><see langword="true"/> when the renderer has a buffer for that name.</returns>
    public static bool TryGetSlot(string name, bool storage, out ReservedBufferSlots slot)
        => (storage ? StorageBlocks : UniformBlocks).TryGetValue(name, out slot);
}
