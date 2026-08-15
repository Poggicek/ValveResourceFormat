using System.Collections.Concurrent;
using System.Linq;
using ValveResourceFormat.Renderer.RHI;

namespace Tests.Renderer.Golden
{
    /// <summary>
    /// Counts what the suite asks the RHI device for, which is the mirror image of what
    /// <see cref="GLCallTrap"/> counts.
    ///
    /// <para>The two together answer the question a Vulkan run exists to answer. The trap says how much of
    /// a frame still goes straight to OpenGL; this says how much of it reaches the device at all. Either
    /// number alone is easy to misread -- a device that is never called looks identical to a device that
    /// works perfectly, from the outside -- and the pair is not.</para>
    /// </summary>
    /// <remarks>
    /// A decorator rather than a subclass, unlike <c>CensusRecordingDevice</c>. Most of
    /// <see cref="IDevice"/> is deliberately non-virtual, so resource creation cannot be observed by
    /// deriving from a backend device; forwarding the interface can observe all of it, and the renderer
    /// only ever holds an <see cref="IDevice"/>.
    /// </remarks>
    internal sealed class RhiDeviceCensus(IDevice inner) : IDevice, ValveResourceFormat.Renderer.IDeviceDecorator, ValveResourceFormat.Renderer.Shaders.Spirv.ISpirvModuleRegistry
    {
        private readonly ConcurrentDictionary<string, int> Counts = new(StringComparer.Ordinal);

        /// <summary>The device being observed.</summary>
        public IDevice Inner { get; } = inner;

        /// <summary>Total calls made to the device.</summary>
        public int TotalCalls => Counts.Values.Sum();

        /// <summary>How many times one member was called.</summary>
        /// <param name="member">The member name, as it appears in <see cref="Report"/>.</param>
        public int CountOf(string member) => Counts.TryGetValue(member, out var count) ? count : 0;

        /// <summary>Formats what the device was asked for, most-used first.</summary>
        public string Report()
        {
            if (Counts.IsEmpty)
            {
                return "  nothing was asked of the RHI device.";
            }

            return string.Join(Environment.NewLine, Counts
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"  {pair.Key,-24} {pair.Value,8:N0}"));
        }

        private T Note<T>(string member, Func<T> call)
        {
            Counts.AddOrUpdate(member, 1, static (_, count) => count + 1);
            return call();
        }

        private void Note(string member, Action call)
        {
            Counts.AddOrUpdate(member, 1, static (_, count) => count + 1);
            call();
        }

        /// <inheritdoc/>
        /// <summary>
        /// Forwards module registration to the decorated device. A decorator hides the concrete type,
        /// which is why the shader loader asks whether a device can register rather than what it is;
        /// without this forward every module would silently take the unreflected path.
        /// </summary>
        public void RegisterModuleInterface(IShaderModule shaderModule, ReadOnlySpan<byte> spirv)
        {
            if (Inner is ValveResourceFormat.Renderer.Shaders.Spirv.ISpirvModuleRegistry registry)
            {
                registry.RegisterModuleInterface(shaderModule, spirv);
            }
        }

        public RhiBackend Backend => Inner.Backend;

        /// <inheritdoc/>
        public IDeviceLimits Limits => Inner.Limits;

        /// <inheritdoc/>
        public int FramesInFlight => Inner.FramesInFlight;

        /// <inheritdoc/>
        public int FrameIndex => Inner.FrameIndex;

        /// <inheritdoc/>
        public IBuffer CreateBuffer(in BufferDesc desc)
        {
            var description = desc;
            return Note(nameof(CreateBuffer), () => Inner.CreateBuffer(in description));
        }

        /// <inheritdoc/>
        public ITexture CreateTexture(in TextureDesc desc)
        {
            var description = desc;
            return Note(nameof(CreateTexture), () => Inner.CreateTexture(in description));
        }

        /// <inheritdoc/>
        public ISampler CreateSampler(in SamplerDesc desc)
        {
            var description = desc;
            return Note(nameof(CreateSampler), () => Inner.CreateSampler(in description));
        }

        /// <inheritdoc/>
        public IShaderModule CreateShaderModule(ReadOnlySpan<byte> code, ShaderStage stage, string name)
        {
            Counts.AddOrUpdate(nameof(CreateShaderModule), 1, static (_, count) => count + 1);
            return Inner.CreateShaderModule(code, stage, name);
        }

        /// <inheritdoc/>
        public IGraphicsPipeline CreateGraphicsPipeline(GraphicsPipelineDesc desc)
            => Note(nameof(CreateGraphicsPipeline), () => Inner.CreateGraphicsPipeline(desc));

        /// <inheritdoc/>
        public IComputePipeline CreateComputePipeline(in ComputePipelineDesc desc)
        {
            var description = desc;
            return Note(nameof(CreateComputePipeline), () => Inner.CreateComputePipeline(in description));
        }

        /// <inheritdoc/>
        public void UploadBuffer(IBuffer destination, int offsetInBytes, ReadOnlySpan<byte> data)
        {
            Counts.AddOrUpdate(nameof(UploadBuffer), 1, static (_, count) => count + 1);
            Inner.UploadBuffer(destination, offsetInBytes, data);
        }

        /// <inheritdoc/>
        public void UploadTexture(ITexture destination, int mipLevel, int arrayLayer, ReadOnlySpan<byte> data)
        {
            Counts.AddOrUpdate(nameof(UploadTexture), 1, static (_, count) => count + 1);
            Inner.UploadTexture(destination, mipLevel, arrayLayer, data);
        }

        /// <inheritdoc/>
        public void BeginFrame() => Note(nameof(BeginFrame), Inner.BeginFrame);

        /// <inheritdoc/>
        public ICommandList BeginCommandList(string name)
            => Note(nameof(BeginCommandList), () => Inner.BeginCommandList(name));

        /// <inheritdoc/>
        public void Submit(ICommandList commandList) => Note(nameof(Submit), () => Inner.Submit(commandList));

        /// <inheritdoc/>
        public void EndFrame() => Note(nameof(EndFrame), Inner.EndFrame);

        /// <inheritdoc/>
        public void WaitIdle() => Note(nameof(WaitIdle), Inner.WaitIdle);

        /// <inheritdoc/>
        public void DeferredDestroy(IRhiResource resource)
            => Note(nameof(DeferredDestroy), () => Inner.DeferredDestroy(resource));

        /// <inheritdoc/>
        public IDisposable DebugScope(string name) => Note(nameof(DebugScope), () => Inner.DebugScope(name));

        /// <summary>The observed device is owned by whoever created it; this decorator owns nothing.</summary>
        public void Dispose()
        {
        }
    }
}
