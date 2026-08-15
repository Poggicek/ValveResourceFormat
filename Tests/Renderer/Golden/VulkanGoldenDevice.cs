using System.Diagnostics.CodeAnalysis;
using ValveResourceFormat.Renderer.RHI;
using ValveResourceFormat.Renderer.RHI.Vulkan;
using ValveResourceFormat.Renderer.RHI.Vulkan.Core;

namespace Tests.Renderer.Golden
{
    /// <summary>
    /// The Vulkan device the golden harness renders through: one that can create resources, create
    /// pipelines <em>and</em> record commands.
    ///
    /// <para><b>No such device exists in the backend, and assembling one is a finding in itself.</b>
    /// <c>VulkanRecordingDevice</c> and <c>VulkanPipelineDevice</c> are both direct subclasses of
    /// <c>VulkanDevice</c> -- siblings, not a chain -- so neither can do the other's job and no single
    /// type in <c>RHI/Vulkan</c> satisfies <see cref="IDevice"/> without a hole. The recording device's own
    /// remarks anticipate the fix ("a device that can also link pipelines derives from this one"), but the
    /// pipeline device was written against <c>VulkanDevice</c> instead, so that derivation was never made.
    /// The OpenGL side has the chain the Vulkan side is missing: <c>GLRecordingDevice</c> derives from
    /// <c>GLDevice</c>, which creates pipelines.</para>
    ///
    /// <para>Both classes do, however, take a <see cref="VulkanCoreDevice"/> the caller already built, so
    /// the two can be put over one core and one of them made to forward. That is what this does, and it
    /// is a composition rather than a fix: the right repair is for <c>VulkanPipelineDevice</c> to derive
    /// from <c>VulkanRecordingDevice</c>, in a file this harness does not own.</para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the composition costs.</b> Two <c>VulkanDevice</c> instances over one core means two
    /// <c>VulkanUploadContext</c> staging rings and two default samplers, of which one of each is never
    /// used. Both are small and neither is incorrect, but a real device should have one.
    /// </para>
    /// <para>
    /// <b>No presentation device is involved.</b> <c>VulkanPresentDevice</c> lives behind
    /// <c>Silk.NET.Vulkan.Extensions.KHR</c>, which <c>Renderer.csproj</c> does not reference, and the
    /// contract's own rule is that an offscreen consumer is "the windowed path minus its last step". The
    /// harness renders into a capture texture and reads it back, so it needs no swapchain, no surface and
    /// no platform extension. That half of the task turned out to be free.
    /// </para>
    /// <para>
    /// <b>The command list is built with no descriptor binder, because none exists.</b>
    /// <c>IVulkanDescriptorBinder</c> is the seam <c>VulkanCommandList</c> writes every
    /// <c>BindUniformBuffer</c>, <c>BindStorageBuffer</c>, <c>BindTexture</c> and <c>BindStorageTexture</c>
    /// through, and nothing in the repository implements it -- the descriptor layer supplies an allocator,
    /// a pool, a layout cache and a writer, but not the adapter that joins them to the command list. Every
    /// binding call therefore refuses with the message <c>VulkanCommandList.RequireBinder</c> raises.
    /// Supplying a stand-in from the test project was rejected deliberately: it would be a second opinion
    /// about descriptor strategy living outside the layer that owns it, and it would hide the gap rather
    /// than report it.
    /// </para>
    /// </remarks>
    internal sealed class VulkanGoldenDevice : VulkanRecordingDevice
    {
        private readonly VulkanPipelineDevice Pipelines;

        private bool PipelinesDisposed;

        private VulkanGoldenDevice(VulkanCoreDevice core, VulkanPipelineDevice pipelines)
            : base(core, ownsCore: true, binder: null)
        {
            Pipelines = pipelines;
        }

        /// <summary>The adapter this device selected, for the run banner and failure messages.</summary>
        public string AdapterName => Core.Adapter.Name;

        /// <summary>
        /// Brings up Vulkan and assembles the device.
        /// </summary>
        /// <param name="messageCallback">Where validation and driver diagnostics are routed.</param>
        /// <param name="enableValidation">Whether the Khronos validation layers are requested.</param>
        /// <returns>The device.</returns>
        /// <exception cref="VulkanException">No suitable Vulkan device exists, or creation failed.</exception>
        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "The core is handed to the returned device, which owns it; the catch disposes it only when no device was returned to take ownership.")]
        public static VulkanGoldenDevice Create(RhiMessageCallback? messageCallback, bool enableValidation)
        {
            var core = new VulkanCoreDevice(new VulkanCoreOptions
            {
                ApplicationName = "VRF golden image harness",
                MessageCallback = messageCallback,
                EnableValidation = enableValidation,

                // Synchronization validation was silently disabled until 2bd2ba759 and works now. The
                // contract's barrier section is the reason to pay for it: a missing barrier is the one
                // class of Vulkan bug that reproduces on one vendor and not another, and this is what
                // reports it deterministically at the call that needed it.
                EnableSynchronizationValidation = enableValidation,
            });

            try
            {
                var pipelines = new VulkanPipelineDevice(core, ownsCore: false, new VulkanPipelineOptions
                {
                    MessageCallback = messageCallback,

                    // A pipeline whose SPIR-V interface disagrees with the layout binds the wrong resource
                    // silently, which is precisely the failure a golden image cannot catch, so it is made
                    // fatal here rather than reported.
                    TreatInterfaceProblemsAsErrors = true,

                    // Nothing is timed here and a cache carried between runs would make the first scene of
                    // a run depend on what a previous run happened to compile.
                    EnableDiskCache = false,
                });

                return new VulkanGoldenDevice(core, pipelines);
            }
            catch
            {
                core.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Clears an offscreen target through this device and reads the result back, so that "the Vulkan
        /// device came up" is a claim about pixels rather than about a constructor returning.
        /// </summary>
        /// <param name="width">Target width.</param>
        /// <param name="height">Target height.</param>
        /// <param name="clearColor">The colour to clear to, straight-alpha RGBA in 0..1.</param>
        /// <returns>The bytes read back: four bytes per texel in <see cref="RhiFormat.R8G8B8A8_UNorm"/>
        /// channel order, top row first, which is Vulkan's own orientation and the opposite of what
        /// <c>glReadPixels</c> hands back.</returns>
        /// <exception cref="InvalidOperationException">The pixels that came back are not the ones asked for.</exception>
        /// <remarks>
        /// <para>
        /// Every Vulkan agent before this one reported the same thing: the golden suite could not test them
        /// and nothing in the backend had drawn a pixel. This is the smallest thing that changes that. It
        /// is the contract's offscreen shape end to end -- create a colour texture, transition it, open a
        /// dynamic-rendering pass that clears it, transition it again, copy it into a host-readable buffer,
        /// submit, and look at what arrives -- with no swapchain, no surface and no shader.
        /// </para>
        /// <para>
        /// It also has teeth. The clear colour is checked on read, so a device that submits nothing, a
        /// missing barrier that leaves the copy reading undefined contents, and a store operation that
        /// discards the pass all fail here rather than passing quietly.
        /// </para>
        /// </remarks>
        public byte[] ClearAndReadBack(int width, int height, Vector4 clearColor)
        {
            var texture = CreateTexture(new TextureDesc(width, height, RhiFormat.R8G8B8A8_UNorm,
                TextureUsage.ColorTarget | TextureUsage.CopySource, "VulkanSelfTestColor"));

            var readback = CreateBuffer(new BufferDesc(width * height * 4,
                BufferUsage.CopyDestination, BufferMemory.HostReadback, "VulkanSelfTestReadback"));

            try
            {
                BeginFrame();

                var commandList = BeginCommandList("VulkanSelfTest");

                commandList.Barrier(new TextureBarrier(texture, ResourceState.Undefined, ResourceState.ColorTarget));

                commandList.BeginRenderPass(new RenderPassDesc(
                    [new ColorAttachmentDesc(texture, LoadOp.Clear, StoreOp.Store, clearColor)],
                    null,
                    "VulkanSelfTest"));

                commandList.EndRenderPass();

                commandList.Barrier(new TextureBarrier(texture, ResourceState.ColorTarget, ResourceState.CopySource));
                commandList.CopyTextureToBuffer(texture, 0, 0, readback);

                Submit(commandList);
                EndFrame();

                // The copy has to have completed before the mapping is read, and this device submits once
                // per frame, so there is no finer-grained fence to wait on than the device itself.
                WaitIdle();

                var pixels = readback.MappedData[..(width * height * 4)].ToArray();

                Verify(pixels, clearColor);

                return pixels;
            }
            finally
            {
                readback.Dispose();
                texture.Dispose();
            }
        }

        private static void Verify(byte[] pixels, Vector4 clearColor)
        {
            static byte Encode(float value) => (byte)Math.Clamp((int)(value * 255f + 0.5f), 0, 255);

            // R8G8B8A8_UNorm is red first in memory, whatever a viewer later does with the channel order.
            var expected = new[] { Encode(clearColor.X), Encode(clearColor.Y), Encode(clearColor.Z), Encode(clearColor.W) };

            for (var channel = 0; channel < 4; channel++)
            {
                // One step of tolerance: the clear value goes through a float attachment clear and comes
                // back quantised, and a driver is free to round either way.
                if (Math.Abs(pixels[channel] - expected[channel]) > 1)
                {
                    throw new InvalidOperationException(
                        $"The Vulkan device cleared to [{pixels[0]}, {pixels[1]}, {pixels[2]}, {pixels[3]}] but was asked for [{expected[0]}, {expected[1]}, {expected[2]}, {expected[3]}].");
                }
            }
        }

        /// <inheritdoc/>
        public override IGraphicsPipeline CreateGraphicsPipeline(GraphicsPipelineDesc desc)
            => Pipelines.CreateGraphicsPipeline(desc);

        /// <inheritdoc/>
        public override IComputePipeline CreateComputePipeline(in ComputePipelineDesc desc)
            => Pipelines.CreateComputePipeline(in desc);

        /// <inheritdoc/>
        /// <remarks>The pipeline half goes first: it destroys pipeline and layout handles, which is only
        /// legal while the core it borrowed is still alive, and the base class is what disposes that
        /// core.</remarks>
        protected override void Dispose(bool disposing)
        {
            if (disposing && !PipelinesDisposed)
            {
                PipelinesDisposed = true;
                Pipelines.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
