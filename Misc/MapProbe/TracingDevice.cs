using System.Runtime.CompilerServices;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.RHI;

namespace MapProbe;

/// <summary>
/// A device decorator that names every command list, writes a flushed line for each submission, and can
/// wait for the device between them.
///
/// <para><b>Why flushed, and why the wait.</b> The characteristic Vulkan map failure is not an exception.
/// The recorded frame executes inside the driver's own worker thread and takes the process with it, so
/// nothing managed runs afterwards -- no <c>finally</c>, no exit handler, no buffered writer. An unbuffered
/// line per command list costs nothing and turns that silence into a file whose last line names the pass
/// that died.</para>
///
/// <para>The wait is the other half. The Vulkan device batches every list of a frame into one
/// <c>vkQueueSubmit2</c> issued by <see cref="EndFrame"/>, so nothing has executed until then and a fault
/// otherwise surfaces during whatever ran next. Waiting for idle at the frame boundary is what makes a
/// driver fault land on the frame that caused it.</para>
/// </summary>
/// <remarks>
/// A decorator rather than a subclass, for the reason <c>RhiDeviceCensus</c> gives: most of
/// <see cref="IDevice"/> is deliberately non-virtual, and the renderer only ever holds an
/// <see cref="IDevice"/>.
/// </remarks>
internal sealed class TracingDevice(IDevice inner, TextWriter log, bool syncEachFrame)
    : IDevice, IDeviceDecorator, ValveResourceFormat.Renderer.Shaders.Spirv.ISpirvModuleRegistry
{
    private readonly ConditionalWeakTable<ICommandList, string> names = [];

    public IDevice Inner { get; } = inner;

    private void Write(string line)
    {
        log.WriteLine(line);
        log.Flush();
    }

    public void RegisterModuleInterface(IShaderModule shaderModule, ReadOnlySpan<byte> spirv)
    {
        if (Inner is ValveResourceFormat.Renderer.Shaders.Spirv.ISpirvModuleRegistry registry)
        {
            registry.RegisterModuleInterface(shaderModule, spirv);
        }
    }

    public RhiBackend Backend => Inner.Backend;

    public IDeviceLimits Limits => Inner.Limits;

    public int FramesInFlight => Inner.FramesInFlight;

    public int FrameIndex => Inner.FrameIndex;

    public IBuffer CreateBuffer(in BufferDesc desc) => Inner.CreateBuffer(in desc);

    public ITexture CreateTexture(in TextureDesc desc) => Inner.CreateTexture(in desc);

    public ISampler CreateSampler(in SamplerDesc desc) => Inner.CreateSampler(in desc);

    public IShaderModule CreateShaderModule(ReadOnlySpan<byte> code, ShaderStage stage, string name)
        => Inner.CreateShaderModule(code, stage, name);

    public IGraphicsPipeline CreateGraphicsPipeline(GraphicsPipelineDesc desc) => Inner.CreateGraphicsPipeline(desc);

    public IComputePipeline CreateComputePipeline(in ComputePipelineDesc desc) => Inner.CreateComputePipeline(in desc);

    public void UploadBuffer(IBuffer destination, int offsetInBytes, ReadOnlySpan<byte> data)
        => Inner.UploadBuffer(destination, offsetInBytes, data);

    public void UploadTexture(ITexture destination, int mipLevel, int arrayLayer, ReadOnlySpan<byte> data)
        => Inner.UploadTexture(destination, mipLevel, arrayLayer, data);

    public void BeginFrame()
    {
        Write("begin frame");
        Inner.BeginFrame();
    }

    public ICommandList BeginCommandList(string name)
    {
        var list = Inner.BeginCommandList(name);

        names.AddOrUpdate(list, name);
        Write($"  record  {name}");

        return list;
    }

    public void Submit(ICommandList commandList)
    {
        var name = names.TryGetValue(commandList, out var recorded) ? recorded : "(unnamed)";

        Write($"  submit  {name}");
        Inner.Submit(commandList);
        Write($"  queued  {name}");
    }

    public void EndFrame()
    {
        Inner.EndFrame();
        Write("end frame");

        if (syncEachFrame)
        {
            Inner.WaitIdle();
            Write("frame executed");
        }
    }

    public void WaitIdle() => Inner.WaitIdle();

    public void DeferredDestroy(IRhiResource resource) => Inner.DeferredDestroy(resource);

    public IDisposable DebugScope(string name) => Inner.DebugScope(name);

    /// <summary>
    /// Deliberately does not dispose the inner device. The probe owns the device it created and tears it
    /// down itself; a decorator that destroyed it here would do so twice.
    /// </summary>
    public void Dispose()
    {
    }
}
