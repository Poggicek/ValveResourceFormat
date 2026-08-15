# A CPU Vulkan device for the golden gate

This directory holds a **software Vulkan driver** (an ICD). The golden suite's Vulkan run points the
Vulkan loader at it, so the run never reaches the machine's real GPU.

## Why

A Vulkan golden run on real hardware hung the display driver and bugchecked the machine. The renderer
still records draws whose pipelines read descriptor sets that were never bound, which is undefined
behaviour, and a kernel-mode graphics driver is entitled to respond to undefined behaviour by wedging.
A CPU implementation cannot take the display with it: the worst it can do is throw, crash the test
process, or produce wrong pixels.

## What is here

| File | What it is |
| --- | --- |
| `lvp_icd.x86_64.json` | The ICD manifest the Vulkan loader reads. Its `library_path` is relative, so this directory can move as a unit. |
| `vulkan_lvp.dll` | Mesa **lavapipe** — the `llvmpipe` rasteriser behind a Vulkan front end. |

Both are `.gitignore`d. They are a binary drop, not source; `fetch-lavapipe.ps1` puts them here.

## Provenance

- Upstream: <https://github.com/pal1000/mesa-dist-win> release `26.2.0`, asset
  `mesa3d-26.2.0-release-msvc.7z` (the `x64/` files).
  - `mesa3d-26.2.0-release-msvc.7z` SHA-256 `dcb2719ef346dab5b609fcb193a5f13cfc4b0502e3f4de1ad43d349477402f47`
  - `vulkan_lvp.dll` SHA-256 `a53822223acb84b084b77758102fbb7345ecda19480a2220627df5a2b6c0e463`
  - `lvp_icd.x86_64.json` SHA-256 `5b867ac6fe25dca5f23a3479a18333df2577542a0ac565ba34a9666af03fdd92`
- That project is an unofficial MSVC build of upstream Mesa; the driver itself is Mesa
  `26.2.0 (git-aacd123e02)` with LLVM `22.1.8`.
- **Licence: MIT** (Mesa's overall licence, <https://docs.mesa3d.org/license.html>). Individual
  components carry their own permissive notices; nothing in the drop is copyleft-encumbered for this
  use, and nothing is redistributed by this repository — the file is fetched, never committed.

Nothing is installed. There is no registry entry, no system directory, no service. Deleting this
directory removes it completely.

## Why lavapipe and not SwiftShader

`VulkanAdapter.Select` is a hard gate. It requires Vulkan 1.3, `dynamicRendering`,
`synchronization2`, `timelineSemaphore`, `drawIndirectCount`, `multiDrawIndirect`,
`drawIndirectFirstInstance`, five subgroup operation classes in the compute stage, and
`maxBoundDescriptorSets >= 5` — the contract declares five descriptor sets and Vulkan's guaranteed
floor is four.

Lavapipe clears every one of them (measured on this machine with `vulkaninfo`):

| Requirement | Lavapipe |
| --- | --- |
| API version | 1.4.354 |
| `maxBoundDescriptorSets` >= 5 | **8** |
| `dynamicRendering` / `synchronization2` / `timelineSemaphore` | true / true / true |
| `drawIndirectCount` / `multiDrawIndirect` / `drawIndirectFirstInstance` | true / true / true |
| subgroup basic, vote, arithmetic, ballot, shuffle | all present, `subgroupSize` 8 |
| subgroup ops in `COMPUTE` stage | yes |
| `subgroupSizeControl` + `computeFullSubgroups` | true (so full-subgroup dispatch is available) |
| graphics queue family | one universal family, graphics + compute + transfer |
| device type | `PHYSICAL_DEVICE_TYPE_CPU` |

SwiftShader was not adopted: it caps `maxBoundDescriptorSets` at 4, which is below the contract's
five, so `VulkanAdapter` would reject it outright and there would be nothing to run.

## Refreshing or removing it

```powershell
# fetch (needs 7-Zip on PATH or at its default install location)
pwsh Tests/SoftwareVulkan/fetch-lavapipe.ps1

# remove
Remove-Item -Recurse -Force Tests/SoftwareVulkan/vulkan_lvp.dll, Tests/SoftwareVulkan/lvp_icd.x86_64.json
```

## How the suite picks it up

See `Tests/Renderer/Golden/SoftwareVulkanIcd.cs`. In short: the Vulkan golden run points
`VK_DRIVER_FILES` at `lvp_icd.x86_64.json` and then **refuses to proceed unless the device that comes
back is a CPU device**. Setting `VRF_RHI_SOFTWARE=0` is the only way to reach the real GPU, and it has
to be typed deliberately.
