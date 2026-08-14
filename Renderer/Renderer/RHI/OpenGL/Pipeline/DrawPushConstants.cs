using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ValveResourceFormat.Renderer.RHI.OpenGL;

/// <summary>
/// The renderer's per-draw push constant block, 92 bytes, laid out exactly as the table in
/// <c>RHI/CONTRACT.md</c> specifies. These are the values today's <c>glProgramUniform</c> call sites in
/// <see cref="MeshBatchRenderer"/> write one at a time.
/// </summary>
/// <remarks>
/// <para>
/// Packed plain-old-data with no padding, so its raw bytes are its exact bit image: the GL backend diffs
/// it field by field, and the Vulkan backend hands the whole 92 bytes to <c>vkCmdPushConstants</c>.
/// </para>
/// <para>
/// The block sits inside the 128 byte floor every Vulkan implementation guarantees. Query
/// <see cref="IDeviceLimits.MaxPushConstantSize"/> before growing it, and change the shader-side
/// declaration in the same commit &#8212; a mismatch corrupts every draw silently.
/// </para>
/// <para>
/// Only <see cref="System.Numerics"/> types appear here, deliberately: the block is backend-neutral and
/// belongs in <c>RHI/</c> proper once the lead brokers the addition. It lives under the GL backend today
/// only because the frozen contract surface may not be extended unilaterally.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public record struct DrawPushConstants
{
    /// <summary>The size of the block in bytes, which the shader-side declaration must match exactly.</summary>
    public const int SizeInBytes = 92;

    // Fields, not properties: a property getter returns a copy, which would break
    // constants.MeshId = x on a block held by reference across a batch.

    /// <summary>First row of the object-to-world transform, a <c>mat3x4</c>.</summary>
    public Vector4 TransformRow0;
    /// <summary>Second row of the object-to-world transform.</summary>
    public Vector4 TransformRow1;
    /// <summary>Third row of the object-to-world transform.</summary>
    public Vector4 TransformRow2;

    /// <summary>Whether skeletal animation is active, as 1 or 0. First component of <c>uAnimationData</c>.</summary>
    public uint Animated;
    /// <summary>Offset of this draw's bones into the bone transform buffer.</summary>
    public uint BoneOffset;
    /// <summary>Number of bones influencing this draw.</summary>
    public uint BoneCount;

    /// <summary>Size in texels of the morph composite texture.</summary>
    public Vector2 MorphCompositeTextureSize;

    /// <summary>Mesh index, read by the picking shader.</summary>
    public uint MeshId;
    /// <summary>Hash of the material's shader name, read by the picking shader.</summary>
    public uint ShaderId;
    /// <summary>The material's GL program handle, read by the picking shader.</summary>
    public uint ShaderProgramId;
    /// <summary>Packed <see cref="Color32"/> tint.</summary>
    public uint Tint;
    /// <summary>Whether this draw is instanced, as 1 or 0.</summary>
    public int IsInstancing;
    /// <summary>Offset of this draw's vertices into the morph composite, or -1 when it has no morphs.</summary>
    public int MorphVertexIdOffset;

    /// <summary>Sets the transform rows from a world transform, dropping the last column as
    /// <see cref="GLEnvironment.To3x4"/> does.</summary>
    /// <param name="transform">The object-to-world transform.</param>
    public void SetTransform(in Matrix4x4 transform)
    {
        TransformRow0 = new Vector4(transform.M11, transform.M21, transform.M31, transform.M41);
        TransformRow1 = new Vector4(transform.M12, transform.M22, transform.M32, transform.M42);
        TransformRow2 = new Vector4(transform.M13, transform.M23, transform.M33, transform.M43);
    }

    /// <summary>Sets the three <c>uAnimationData</c> components together, as the one uniform they are.</summary>
    /// <param name="animated">Whether skeletal animation is active.</param>
    /// <param name="boneOffset">Offset into the bone transform buffer.</param>
    /// <param name="boneCount">Number of bones influencing this draw.</param>
    public void SetAnimationData(bool animated, int boneOffset = 0, int boneCount = 0)
    {
        Animated = animated ? 1u : 0u;
        BoneOffset = (uint)boneOffset;
        BoneCount = (uint)boneCount;
    }

    // The contract fixes the block at 92 bytes and the shaders declare it as such. Padding introduced by
    // reordering or widening a field would go unnoticed on GL, which writes the fields separately, and
    // corrupt every draw on Vulkan, which memcpys the block.
    internal static void AssertLayout()
        => System.Diagnostics.Debug.Assert(Unsafe.SizeOf<DrawPushConstants>() == SizeInBytes,
            $"{nameof(DrawPushConstants)} is {Unsafe.SizeOf<DrawPushConstants>()} bytes, but the contract fixes the push constant block at {SizeInBytes}.");
}
