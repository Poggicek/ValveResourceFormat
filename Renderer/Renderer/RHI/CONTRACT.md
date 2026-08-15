# RHI Contract

The render hardware interface the renderer draws through. Two backends implement it: OpenGL 4.6 and
Vulkan 1.3. **Both ship permanently** — GL is the parity oracle the golden image suite diffs Vulkan
against, so it is not scaffolding to be deleted.

This surface is **frozen**. Adding members is a lead-brokered change; renaming or removing one is a
broadcast event affecting every in-flight agent.

## Files

| File | Holds |
|---|---|
| `Formats.cs` | `RhiFormat` and format predicates |
| `Enums.cs` | Usage flags, load/store ops, topology, `ResourceState` |
| `Resources.cs` | `IBuffer`, `ITexture`, `ISampler`, `IShaderModule`, pipelines, `IRenderTarget` |
| `Descriptors.cs` | Creation descriptors, `DescriptorSets`, `PipelineCacheKey` |
| `IDevice.cs` | Device, limits, resource creation, frame lifecycle |
| `ICommandList.cs` | Recording surface |

Backends live in `RHI/GL/` and `RHI/Vulkan/`.

## Descriptor sets

The binding *numbers* are the existing `ReservedBufferSlots` and `ReservedTextureSlots` values,
unchanged — 120 shaders are not being renumbered.

| Set | Contents | Binding numbers from |
|---|---|---|
| 0 | Uniform buffers | `ReservedBufferSlots` (UBO range, 0–7) |
| 1 | Storage buffers | `ReservedBufferSlots` (SSBO range, 0–15) |
| 2 | Global textures | `ReservedTextureSlots` |
| 3 | Per-material textures | assigned by the material |
| 4 | Storage images | the image unit the shader declares |

**This resolves a real ambiguity, not just a layout preference.** `ReservedBufferSlots` deliberately
overlaps its UBO and SSBO index spaces — both start at 0, which is why the file suppresses CA1069.
OpenGL keeps those namespaces separate per binding target; Vulkan does not. Splitting them across
sets 0 and 1 preserves both numbering schemes and makes the collision impossible to hit.

**Set 4 exists for the same reason as the 0/1 split, one index space further on.** `glBindImageTexture`
addresses *image units*, which OpenGL keeps separate from texture units and Vulkan does not. The
numbers really do collide: storage images bound at 0–3 land on `BRDFLookup`, `BlueNoise`,
`FogCubeTexture` and `Lightmap1`, typed as combined image samplers. `depth_pyramid.comp` settles that
no renumbering can fix it inside one set — it declares a sampler at 0 *and* images at 1 and 2 in the
same shader.

> Shader-side `layout(set=, binding=)` decorations must match this table exactly — a mismatch binds
> the wrong resource *silently*. Note that shader emission currently places storage images in set 2,
> which predates this row and must be updated to set 4.

## Push constants

The per-draw block is **92 bytes**, inside the 128-byte floor every Vulkan implementation
guarantees. These are today's `glProgramUniform` call sites in `MeshBatchRenderer.cs`.

**Member order is load-bearing, and this table is not the authority.** The single source of truth is
`ShaderParser.PushConstantMembers`, which generates the GLSL block and throws if the total ever
leaves 92. Derive `PushConstantRange` from SPIR-V reflection rather than hardcoding a size.

The order matters because `uvec3 uAnimationData` ends at offset 60 and a `vec2` aligns to 8. Placing
the `vec2` next wastes four bytes and the block becomes **96**. Slotting a scalar into that hole
first keeps it at 92:

| Order | Member | Type | Offset |
|---|---|---|---|
| 1 | `transform` | `mat3x4` | 0 |
| 2 | `uAnimationData` | `uvec3` | 48 |
| 3 | `morphVertexIdOffset` | `int` | 60 ← fills the hole |
| 4 | `morphCompositeTextureSize` | `vec2` | 64 |
| 5 | `meshId`, `shaderId`, `shaderProgramId`, `vTint`, `bIsInstancing` | scalars | 72–92 |

An earlier revision of this table listed the members in declaration order, which packs to 96. Two
agents caught it independently from opposite sides — one generating the block, one reflecting the
compiled SPIR-V. Read the order above as normative.

`bIsInstancing` is carried as `uint` with a macro, because GLSL block members cannot be `bool`.

## Pipeline caching

`GraphicsPipelineDesc` is a record *class* — it holds shader module references and arrays, so it
cannot be POD. The cache key is the separate `PipelineCacheKey` struct: `Pack = 1`, no references,
so its raw bytes are its exact bit image and hash or memcmp directly.

It embeds `RenderState` whole. That type was already `Pack = 1` POD mirroring Valve's
`rendersystemdx11` descriptors, and its own remarks anticipate exactly this use.

## Barriers are not optional

`ICommandList.Barrier` is explicit even though the GL backend largely ignores it.

**Model every call site for Vulkan.** GL is the backend that tolerates a missing barrier; Vulkan is
the one that corrupts. A barrier omitted while porting to GL becomes a race in the Vulkan backend
that reproduces on one vendor's driver and not another's — the worst class of bug to find late.

Batch transitions that happen at the same point into one `Barrier` call; separate calls cost
separate pipeline stalls.

## Conventions that carry over

- **Reverse-Z.** Already in use via `ClipControl`, and already Vulkan's native convention. Prefer
  `Comparison.Closer` / `Farther` over `Greater` / `Less` — they are depth-direction independent.
  Depth clears to `0` (far plane).
- **Y-flip** is handled by a negative-height viewport in the Vulkan backend, never in shaders.
  Pushing it into shaders breaks every screen-space derivative and the whole post-process chain.
- **Dynamic rendering.** `RenderPassDesc` describes attachments and load/store ops directly. There
  is no render pass or framebuffer object to create, cache or invalidate.
- **Deferred destruction.** Call `IDevice.DeferredDestroy`. Destroying a resource still referenced by
  an in-flight frame is undefined.
- **Debug names.** Every descriptor takes one. They map to `glObjectLabel` and
  `VK_EXT_debug_utils`; preserve the naming already used at existing call sites.

## Revision 2 — gaps closed after first-consumer review

The viewer-layer and format-table agents were the first real consumers and found genuine holes.
**Every change below is additive** except the removal of an unreachable type, so work already in
flight against revision 1 still compiles.

| Gap | Fix |
|---|---|
| No way to obtain an `IDevice` | `RendererContext.Device`, assigned by the presentation layer |
| No GPU→CPU readback — `BufferMemory.HostReadback` was unfillable | `ICommandList.CopyTextureToBuffer` |
| **No MSAA resolve** | `ColorAttachmentDesc.ResolveTexture` |
| `IRenderTarget` had no producer and no consumer | Removed; render targets are the `ITexture` attachments of a `RenderPassDesc` |
| No stencil aspect on `ITexture.CreateView` | `TextureAspect` parameter |
| No max sample count | `IDeviceLimits.MaxSampleCount` |
| `PushConstantRange.OffsetInBytes` unusable | `SetPushConstants(in T, int offsetInBytes = 0)` |
| Nowhere for viewer- and pass-local uniform blocks | `ICommandList.BindTransientUniform<T>` |
| No inbound diagnostics channel | `RhiMessageCallback`, supplied at device creation |
| `DebugScope` needed a command list the caller lacks | `IDevice.DebugScope` |
| `BindTexture` documented a texture "default sampler" that cannot exist | Null now means the device default sampler |
| **Vertex integer formats missing** | `R8G8B8A8_UInt`, `R16G16_SInt`, `R16G16B16A16_UInt`, `R16G16B16A16_SInt`, `R32G32B32A32_SInt` |
| ETC2 family missing | Four `ETC2_*` members |
| `VertexInputDesc` had no empty value | `VertexInputDesc.Empty` |

Two of these deserve their reasoning recorded, because both are silent on the GL oracle:

**MSAA resolve.** `glBlitFramebuffer` resolves multisampled sources implicitly, so mapping a resolve
onto `BlitTexture` works perfectly on OpenGL. `vkCmdBlitImage` rejects a multisampled source
outright. That is the barrier-section hazard in another guise: correct on the oracle, hard failure
on Vulkan. Use `ColorAttachmentDesc.ResolveTexture` and never `BlitTexture` for this.

**Vertex integer formats.** Source 2 encodes `BLENDINDICES` as `R8G8B8A8_UINT`, `R16G16_SINT`,
`R16G16B16A16_SINT` and the eight-joint variants — see the table at `VBIB.cs:392`. Without these
members no skinned mesh could describe its vertex input at all.

Still open, needing a decision rather than an addition:

- **`R16G16B16_SFloat`** — `MorphComposite.cs:86` uses GL `Rgb16f`. Three-component 16-bit float is
  optional in Vulkan and widely unsupported, so the port should move that texture to
  `R16G16B16A16_SFloat` rather than the contract growing a member most devices cannot honour.
- **`GLGraphViewer`** drives Skia's GL backend directly against a raw FBO handle
  (`GRGlFramebufferInfo`), which has no RHI expression and would need a `VkDevice` handed out
  through an otherwise handle-free interface. Likeliest answer is Skia CPU raster plus a texture
  upload, but it needs deciding before anything touches that file.

## A render pass never names the presented surface

Revision 2 said the presentation layer surfaces the backbuffer as an `ITexture`. That is true on
Vulkan and **unachievable on OpenGL**: framebuffer 0 has no texture handle and
`glFramebufferTexture` cannot produce one. A `GLTexture` wrapping handle 0 would be an object on
which sampling, copying, viewing, uploading and blitting are all invalid — the same
documented-behaviour-that-cannot-exist shape as the old null-sampler wording.

The rule instead:

> Every frame renders into an offscreen colour `ITexture` the presentation layer owns. Getting that
> texture onto the screen is a backend-specific step **outside** the RHI, performed once per frame by
> the presentation layer. `IDevice` gains no present method and `ICommandList` gains nothing.

On OpenGL that step is a blit from a texture-backed FBO into framebuffer 0, then a buffer swap. On
Vulkan it is acquire, render into the acquired image, present — a swapchain image *is* a real
`VkImage`, so Vulkan renders straight into it and skips the copy.

Two consequences worth recording:

- **OpenGL pays one full-screen blit per frame that Vulkan does not.** Forced by the API, not chosen.
  Whoever eventually benchmarks the two backends should know why they differ before drawing
  conclusions from it.
- **Offscreen consumers have no present step at all.** Thumbnails, the golden harness and RenderTest
  render into a capture texture and read back. That is the windowed path minus its last step, which
  is the strongest evidence this shape is right: the two cases differ by exactly one operation.

## Recording is gated until passes and pipelines exist

`Renderer.EnableRhiRecording` is off. Nine call sites issue draws through a command list and none
opens a render pass or binds a pipeline first, because neither existed when they were written. Both
are mandatory — `DrawIndexed` carries no topology, it comes from `IGraphicsPipeline.Description` —
so turning recording on throws on the first scene that draws.

That is not a defect in those sites so much as an ordering artifact, and the golden suite is what
surfaced it: with the flag on, every drawing scene fails identically. Turn the flag on once the scene
passes are wrapped and the material path produces pipelines, and let the suite say whether it worked.

## Depth range belongs to the viewport, and the renderer's layer scheme has to follow

`SetViewport` carries `minDepth`/`maxDepth`, and there is deliberately no separate depth-range call.
That matches Vulkan, where viewport *is* the depth range and nothing else sets it.

The renderer does not work that way yet. It layers the frame by calling `glDepthRange` independently
of `glViewport` — `DepthRange.Scene` (0.95–0.05), `Viewmodel` (1.0–0.95), `Sky` (0.05–0). Those are
separate calls today, and `BeginRenderPass` sets the viewport to the full attachment with the default
0–1 range, so **any recorded `SetViewport` silently discards the layer's range**. On OpenGL the
symptom is nothing at all, because the raw `glDepthRange` calls still run alongside; on Vulkan the
three layers collapse into one and the viewmodel and sky sort against the scene incorrectly.

The contract is right here and the renderer is what has to change: each depth layer must re-issue
`SetViewport` with its own min and max rather than calling a depth-range setter. That is a structural
change to the layer scheme, not a mechanical port, which is why `Renderer.cs`'s viewport and
depth-range calls are still raw OpenGL — porting them piecemeal would trip exactly this.

## `GLDebugGroup` is not a debug marker — do not port it to `DebugScope`

`IDevice.DebugScope` and `ICommandList.DebugScope` exist for *marker* usage. `GLDebugGroup` is not
marker usage, despite the name.

Read `GLDebugGroup.cs:19`: `TimeQueryId = PerfStats.Active.BeginTimingQuery(name)` sits **outside**
the `#if DEBUG`. It is the renderer's timing-scope primitive that also pushes a marker, and the
timing half runs in Release. Substituting `DebugScope` compiles, renders identically, and silently
deletes the instrumentation feeding the Timings overlay.

`GLDebugGroup` should keep its timing role and gain a `DebugScope` *inside* it. Timestamps are
outside this contract by design, so the two surfaces have to be reconciled deliberately rather than
by mechanical substitution.

## Not in this contract, by design

- **Queries and timestamps** — `PerfStats` / `Timings` keep their own surface (agent `E2`).
- **Swapchain creation** — platform-specific, owned by the presentation layer (agents `A5`, `C6`).
- **Shader compilation** — `IDevice.CreateShaderModule` takes *already compiled* code. Producing it
  is `ShaderLoader`'s job (agents `A6`, `A7`).
