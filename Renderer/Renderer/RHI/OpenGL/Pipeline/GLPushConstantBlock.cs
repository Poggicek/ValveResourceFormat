using OpenTK.Graphics.OpenGL;
using GLApi = OpenTK.Graphics.OpenGL.GL;

namespace ValveResourceFormat.Renderer.RHI.OpenGL;

/// <summary>
/// Emulates a push constant block on OpenGL, which has no such thing, by writing the block's fields as
/// loose program uniforms.
/// </summary>
/// <remarks>
/// <para>
/// Uniform state belongs to the program object, so one block is cached per <see cref="Shader"/> and its
/// shadow copy stays valid across binds. Writes are diffed field by field against that shadow, which is
/// what keeps the emulation as cheap as the hand-written <c>glProgramUniform</c> calls it replaces: a
/// field left untouched between draws costs nothing.
/// </para>
/// <para>
/// A field whose uniform the linker dropped resolves to location -1 and is skipped, so a block may be
/// filled in fully and written to any shader. That is how one <see cref="DrawPushConstants"/> value
/// serves the picking, depth-only and material shaders alike.
/// </para>
/// <para>
/// The Vulkan backend has no equivalent of this class: it writes the block whole with one
/// <c>vkCmdPushConstants</c>, and the per-field locations here have no counterpart.
/// </para>
/// </remarks>
public sealed class GLPushConstantBlock
{
    private readonly int program;

    private readonly int transform;
    private readonly int animationData;
    private readonly int morphCompositeTextureSize;
    private readonly int morphVertexIdOffset;
    private readonly int meshId;
    private readonly int shaderId;
    private readonly int shaderProgramId;
    private readonly int tint;
    private readonly int isInstancing;

    // Shadow of the last written block, the diff baseline. Valid only after the first write.
    private DrawPushConstants written;
    private bool writtenValid;

    /// <summary>Resolves the block's uniform locations in a linked program.</summary>
    /// <param name="shader">The shader whose program the block is written to.</param>
    /// <remarks>The names are the ones the shaders declare today, and the ones
    /// <see cref="MeshBatchRenderer"/> resolves at batch setup.</remarks>
    public GLPushConstantBlock(Shader shader)
    {
        ArgumentNullException.ThrowIfNull(shader);
        DrawPushConstants.AssertLayout();

        program = shader.Program;

        transform = shader.GetUniformLocation("transform");
        animationData = shader.GetUniformLocation("uAnimationData");
        morphCompositeTextureSize = shader.GetUniformLocation("morphCompositeTextureSize");
        morphVertexIdOffset = shader.GetUniformLocation("morphVertexIdOffset");
        meshId = shader.GetUniformLocation("meshId");
        shaderId = shader.GetUniformLocation("shaderId");
        shaderProgramId = shader.GetUniformLocation("shaderProgramId");
        tint = shader.GetUniformLocation("vTint");
        isInstancing = shader.GetUniformLocation("bIsInstancing");
    }

    /// <summary>Gets a value indicating whether this program reads none of the block.</summary>
    public bool IsEmpty => transform == -1 && animationData == -1 && morphCompositeTextureSize == -1
        && morphVertexIdOffset == -1 && meshId == -1 && shaderId == -1 && shaderProgramId == -1
        && tint == -1 && isInstancing == -1;

    /// <summary>
    /// Writes the block, emitting a GL call only for the fields that this program reads and that changed
    /// since the last write.
    /// </summary>
    /// <param name="values">The block to write.</param>
    /// <param name="offsetInBytes">Byte offset the block is written at. Only 0, the whole block, is
    /// supported on OpenGL.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offsetInBytes"/> is not zero.</exception>
    /// <remarks>
    /// <para>
    /// Hold one block per batch and mutate the fields that apply to each draw rather than building a fresh
    /// value, so that a field a draw does not concern itself with keeps its previous value and costs no
    /// call. Rebuilding the block per draw would write zeroes over the fields the draw left alone.
    /// </para>
    /// <para>
    /// A partial push exists on Vulkan to avoid re-uploading the whole range. It buys nothing here, where
    /// the fields are separate uniforms diffed one by one, so writing the whole block already costs only
    /// what actually changed. The parameter is present to match
    /// <see cref="ICommandList.SetPushConstants{T}"/> and rejects anything else rather than quietly
    /// writing the block to the wrong place.
    /// </para>
    /// </remarks>
    public void SetPushConstants(in DrawPushConstants values, int offsetInBytes = 0)
    {
        if (offsetInBytes != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offsetInBytes), offsetInBytes,
                $"The OpenGL backend writes the push constant block as separate uniforms and diffs them, so it only supports pushing the whole {nameof(DrawPushConstants)} at offset 0.");
        }

        var force = !writtenValid;

        if (transform != -1 && (force
            || values.TransformRow0 != written.TransformRow0
            || values.TransformRow1 != written.TransformRow1
            || values.TransformRow2 != written.TransformRow2))
        {
            var matrix = new OpenTK.Mathematics.Matrix3x4(
                values.TransformRow0.X, values.TransformRow0.Y, values.TransformRow0.Z, values.TransformRow0.W,
                values.TransformRow1.X, values.TransformRow1.Y, values.TransformRow1.Z, values.TransformRow1.W,
                values.TransformRow2.X, values.TransformRow2.Y, values.TransformRow2.Z, values.TransformRow2.W);

            GLApi.ProgramUniformMatrix3x4(program, transform, false, ref matrix);
        }

        if (animationData != -1 && (force
            || values.Animated != written.Animated
            || values.BoneOffset != written.BoneOffset
            || values.BoneCount != written.BoneCount))
        {
            GLApi.ProgramUniform3((uint)program, animationData, values.Animated, values.BoneOffset, values.BoneCount);
        }

        if (morphCompositeTextureSize != -1 && (force || values.MorphCompositeTextureSize != written.MorphCompositeTextureSize))
        {
            GLApi.ProgramUniform2(program, morphCompositeTextureSize, values.MorphCompositeTextureSize.X, values.MorphCompositeTextureSize.Y);
        }

        if (morphVertexIdOffset != -1 && (force || values.MorphVertexIdOffset != written.MorphVertexIdOffset))
        {
            GLApi.ProgramUniform1(program, morphVertexIdOffset, values.MorphVertexIdOffset);
        }

        if (meshId != -1 && (force || values.MeshId != written.MeshId))
        {
            GLApi.ProgramUniform1((uint)program, meshId, values.MeshId);
        }

        if (shaderId != -1 && (force || values.ShaderId != written.ShaderId))
        {
            GLApi.ProgramUniform1((uint)program, shaderId, values.ShaderId);
        }

        if (shaderProgramId != -1 && (force || values.ShaderProgramId != written.ShaderProgramId))
        {
            GLApi.ProgramUniform1((uint)program, shaderProgramId, values.ShaderProgramId);
        }

        if (tint != -1 && (force || values.Tint != written.Tint))
        {
            GLApi.ProgramUniform1((uint)program, tint, values.Tint);
        }

        if (isInstancing != -1 && (force || values.IsInstancing != written.IsInstancing))
        {
            GLApi.ProgramUniform1(program, isInstancing, values.IsInstancing);
        }

        written = values;
        writtenValid = true;
    }

    /// <summary>Drops the diff baseline, so the next write emits every field this program reads.</summary>
    /// <remarks>For when the program's uniform state changed behind this block's back, which outside of
    /// shader hot reload it does not.</remarks>
    public void Invalidate() => writtenValid = false;
}
