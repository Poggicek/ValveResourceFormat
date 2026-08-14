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

## Not in this contract, by design

- **Queries and timestamps** — `PerfStats` / `Timings` keep their own surface (agent `E2`).
- **Swapchain creation** — platform-specific, owned by the presentation layer (agents `A5`, `C6`).
- **Shader compilation** — `IDevice.CreateShaderModule` takes *already compiled* code. Producing it
  is `ShaderLoader`'s job (agents `A6`, `A7`).
