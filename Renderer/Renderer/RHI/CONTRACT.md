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

**This resolves a real ambiguity, not just a layout preference.** `ReservedBufferSlots` deliberately
overlaps its UBO and SSBO index spaces — both start at 0, which is why the file suppresses CA1069.
OpenGL keeps those namespaces separate per binding target; Vulkan does not. Splitting them across
sets 0 and 1 preserves both numbering schemes and makes the collision impossible to hit.

> Shader-side `layout(set=, binding=)` decorations must match this table exactly. This is what agent
> `A7` emits and agent `C2` builds layouts from — a mismatch binds the wrong buffer *silently*.

## Push constants

The per-draw block is **92 bytes**, inside the 128-byte floor every Vulkan implementation guarantees:

| Member | Type | Bytes |
|---|---|---|
| `Transform` | `mat3x4` | 48 |
| `AnimationData` | `uvec3` | 12 |
| `MorphCompositeTextureSize` | `vec2` | 8 |
| `MeshId`, `ShaderId`, `ShaderProgramId`, `Tint`, `IsInstancing`, `MorphVertexIdOffset` | scalars | 24 |
| | | **92** |

These are today's `glProgramUniform` call sites in `MeshBatchRenderer.cs`. Query
`IDeviceLimits.MaxPushConstantSize` before growing the block.

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

## Not in this contract, by design

- **Queries and timestamps** — `PerfStats` / `Timings` keep their own surface (agent `E2`).
- **Swapchain creation** — platform-specific, owned by the presentation layer (agents `A5`, `C6`).
- **Shader compilation** — `IDevice.CreateShaderModule` takes *already compiled* code. Producing it
  is `ShaderLoader`'s job (agents `A6`, `A7`).
