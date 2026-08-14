namespace ValveResourceFormat.Renderer.RHI.OpenGL;

/// <summary>
/// A <see cref="GLRendererDevice"/> that can also record, which makes it the first OpenGL device with
/// no unimplemented holes and the one the presentation layer should construct.
/// </summary>
/// <remarks>
/// <para>
/// The backend's devices are layered by what they can reach: <see cref="GLDevice"/> creates resources,
/// <see cref="GLRendererDevice"/> adds pipelines because it can reach a <see cref="ShaderLoader"/>, and
/// this adds recording. Deriving rather than composing keeps that one chain, so a caller never has to
/// hold two devices and choose between them.
/// </para>
/// <para>
/// One command list is created and reused for the life of the device rather than one per frame. It
/// caches a framebuffer per distinct set of render pass attachments and a vertex array per distinct
/// vertex layout, both of which cost GL calls to build and stay valid across frames; a fresh command
/// list each frame would throw that away and rebuild it every time.
/// </para>
/// </remarks>
public class GLRecordingDevice : GLRendererDevice
{
    private readonly RendererContext rendererContext;
    private GLCommandList? commandList;

    /// <summary>Creates a device over the current OpenGL context.</summary>
    /// <param name="rendererContext">The context whose <see cref="ShaderLoader"/> links this device's
    /// programs and whose <see cref="RenderStateTracker"/> its pipelines apply state through.</param>
    /// <param name="messageCallback">Where to route driver diagnostics, or <see langword="null"/> to
    /// leave whatever debug callback the context already has installed alone.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rendererContext"/> is <see langword="null"/>.</exception>
    public GLRecordingDevice(RendererContext rendererContext, RhiMessageCallback? messageCallback = null)
        : base(rendererContext, messageCallback)
    {
        ArgumentNullException.ThrowIfNull(rendererContext);

        this.rendererContext = rendererContext;
    }

    /// <inheritdoc/>
    /// <remarks>Returns the device's one command list, rewound for a new frame. Recording into it is
    /// immediate, so the returned list is only valid until the next call.</remarks>
    public override ICommandList BeginCommandList(string name)
    {
        commandList ??= new GLCommandList(this, rendererContext.RenderState, name);
        commandList.Reset();

        return commandList;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            commandList?.Dispose();
            commandList = null;
        }

        base.Dispose(disposing);
    }
}
