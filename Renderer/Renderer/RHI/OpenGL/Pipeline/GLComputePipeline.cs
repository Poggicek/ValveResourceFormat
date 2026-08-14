using System.Runtime.CompilerServices;
using OpenTK.Graphics.OpenGL;
using GLApi = OpenTK.Graphics.OpenGL.GL;

namespace ValveResourceFormat.Renderer.RHI.OpenGL;

/// <summary>
/// <see cref="IComputePipeline"/> on OpenGL: a linked compute program and the workgroup size its source
/// declares.
/// </summary>
/// <remarks>
/// The workgroup size is read back from the linked program rather than restated at the call site, so
/// <see cref="DispatchThreads"/> can size a dispatch from a thread count. The existing dispatch sites
/// divide by a group size written out by hand, which is the same number in two places.
/// </remarks>
public sealed class GLComputePipeline : IComputePipeline
{
    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public (int X, int Y, int Z) WorkgroupSize { get; }

    /// <summary>Gets the linked program this pipeline dispatches.</summary>
    public Shader Program { get; }

    /// <summary>Gets the push constant block of <see cref="Program"/>.</summary>
    public GLPushConstantBlock PushConstants => Program.PushConstants;

    /// <summary>Initializes a compute pipeline over an already linked program.</summary>
    /// <param name="description">The pipeline description, which supplies the debug name.</param>
    /// <param name="program">The linked compute program, which the pipeline does not take ownership of.</param>
    public GLComputePipeline(in ComputePipelineDesc description, Shader program)
    {
        ArgumentNullException.ThrowIfNull(program);

        Name = description.Name;
        Program = program;

        program.EnsureLoaded();

        var size = new int[3];

        // OpenTK only declares this one on ProgramPropertyArb, which GetProgram has no overload for.
        GLApi.GetProgram(program.Program, (GetProgramParameterName)ProgramPropertyArb.ComputeWorkGroupSize, size);

        WorkgroupSize = (size[0], size[1], size[2]);
    }

    /// <summary>Makes this pipeline current.</summary>
    public void Bind() => Program.Use();

    /// <summary>Dispatches a number of workgroups.</summary>
    /// <param name="groupCountX">Workgroups on X.</param>
    /// <param name="groupCountY">Workgroups on Y.</param>
    /// <param name="groupCountZ">Workgroups on Z.</param>
    public static void Dispatch(int groupCountX, int groupCountY = 1, int groupCountZ = 1)
        => GLApi.DispatchCompute(groupCountX, groupCountY, groupCountZ);

    /// <summary>Dispatches enough workgroups to cover a number of threads, rounding up.</summary>
    /// <param name="threadsX">Threads needed on X.</param>
    /// <param name="threadsY">Threads needed on Y.</param>
    /// <param name="threadsZ">Threads needed on Z.</param>
    /// <remarks>The shader still has to bounds check, since rounding up dispatches threads past the end.</remarks>
    public void DispatchThreads(int threadsX, int threadsY = 1, int threadsZ = 1)
        => Dispatch(
            GroupsFor(threadsX, WorkgroupSize.X),
            GroupsFor(threadsY, WorkgroupSize.Y),
            GroupsFor(threadsZ, WorkgroupSize.Z));

    private static int GroupsFor(int threads, int groupSize)
        => groupSize <= 0 ? 0 : (threads + groupSize - 1) / groupSize;

    /// <summary>Writes push constants for this pipeline's program.</summary>
    /// <typeparam name="T">The block type. Only <see cref="DrawPushConstants"/> is mappable on OpenGL.</typeparam>
    /// <param name="data">The block to write.</param>
    /// <param name="offsetInBytes">Byte offset within the block. Only 0 is supported on OpenGL.</param>
    /// <exception cref="NotSupportedException">The block type has no uniform mapping on this backend.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offsetInBytes"/> is not zero.</exception>
    public void SetPushConstants<T>(in T data, int offsetInBytes = 0) where T : unmanaged
    {
        if (typeof(T) != typeof(DrawPushConstants))
        {
            throw new NotSupportedException(
                $"The OpenGL backend cannot map '{typeof(T).Name}' onto program uniforms. Push constant blocks it writes have to be {nameof(DrawPushConstants)}.");
        }

        ref var block = ref Unsafe.As<T, DrawPushConstants>(ref Unsafe.AsRef(in data));
        PushConstants.SetPushConstants(in block, offsetInBytes);
    }

    /// <summary>Releases the pipeline. The program belongs to <see cref="ShaderLoader"/> and outlives it.</summary>
    public void Dispose()
    {
        // Nothing to release: a GL pipeline owns no object of its own.
    }
}
