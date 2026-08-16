# MapProbe

Renders one **map** offscreen through either RHI backend and writes a PNG.

The golden image suite covers 37 hand-built scenes and no map. That is its largest blind spot: the closest
scene, `world_static_batch`, builds `ModelSceneNode`s and reaches no indirect draw at all, so the aggregate
and map-loading paths go unmeasured and maps have only ever been debugged through the GUI — with a person
acting as the test harness. This tool is the offscreen substitute.

It cannot be a test, and deliberately is not one: a map means installed game content, `de_mirage.vpk` alone
is 170 MB, and nothing here can be vendored or fetched in CI. **It runs in no gate.** When the content is
absent it exits `4` with the path it wanted — never silently, never green.

## Getting an image

```
dotnet run --project Misc/MapProbe -- --vpk <path.vpk> --backend gl --out gl.png
dotnet run --project Misc/MapProbe -- --vpk <path.vpk> --backend vk --out vk.png
dotnet run --project Misc/MapProbe -- --diff gl.png vk.png --out diff.png
```

`--map` defaults to the first map in the package, so a single-map VPK needs nothing else. `--list` prints
what a package holds when it has more than one.

**`--backend gl` currently needs `--no-recording` on a map.** As of `3a28aa61d` the OpenGL *recording*
path — the default — dies with an access violation inside the driver at
`GLCommandList.DrawIndexed`, reached from `ShapeSceneNode.DrawPicking` and, before that, from
`RenderCables.DrawTube`. Both are draw sites no golden scene exercises, which is why the gate is green
and every map is not. The process is killed outright, so there is no image and no exception; measured on
`de_mirage` and `ar_baggage`, 5 runs each. `--no-recording` takes the renderer's direct OpenGL route and
renders both.

Two runs, not one: the Vulkan run installs a process-wide OpenGL call trap and pins the Vulkan loader's
driver selection before its first call, and neither can be undone. Comparing the backends therefore always
means two processes and a third invocation for the diff.

Maps to point it at:

- a workshop map — small, simple, fast, the one to reach for first
- `.../steamapps/common/Counter-Strike Global Offensive/game/csgo/maps/de_mirage.vpk` — the large
  real-world case

Both are read-only inputs. Nothing under a Steam directory is written or modified.

## GPU safety

The Vulkan backend goes through `Tests/Renderer/Golden/SoftwareVulkanIcd.cs`, which restricts the loader to
the vendored CPU driver in `Tests/SoftwareVulkan/` and then **reads the adapter's own device type back and
destroys the device before submitting anything** if it is not a CPU device.

This is not caution for its own sake. The renderer still records draws whose pipelines read descriptor sets
that were never bound; that is undefined behaviour, and a kernel-mode display driver answered it by hanging
and bugchecking the machine this port is being written on. `VRF_RHI_SOFTWARE=0` is the only route back to
real hardware and has to be typed on purpose.

**Confirm every Vulkan run prints `device type Cpu` and an `llvmpipe` adapter before trusting its output.**

If the run says no driver is vendored, `Tests/SoftwareVulkan/fetch-lavapipe.ps1` puts one there.

## Options

| Flag | Meaning |
| --- | --- |
| `--vpk <path>` | The map package. Required. |
| `--map <path>` | Map inside it. Defaults to the first. |
| `--backend gl\|vk` | Which RHI backend. Default `gl`. |
| `--out <file>` | Output PNG. Default `probe.png`. |
| `--list` | Print the maps in the package and exit. |
| `--diff <a> <b>` | Score two PNGs against the golden suite's `Lit` tolerance; `--out` writes the diff image. |
| `--frames N` | Frames to render before capture. Default 2. |
| `--width` / `--height` | Capture size. Default 320x240, as the golden suite uses. |
| `--maxtex N` | Texture size cap. Default 256. |
| `--camera x,y,z` | Camera position. Defaults to the map's spawn marker. |
| `--look x,y,z` | Look-at target, instead of `--pitch`/`--yaw`. |
| `--pitch` / `--yaw` | Degrees, used with `--camera`. |
| `--drop A,B` | Remove scene nodes whose type name contains any of these. |
| `--keep A,B` | Remove every scene node whose type name contains none of these. |
| `--trace <file>` | One flushed line per command list recorded and submitted. |
| `--sync` | Wait for the device after every frame. |
| `--validation <file>` | One flushed line per validation message. |
| `--transcript <file>` | Every Vulkan command the run recorded, in order. |
| `--log <file>` | The console output, as a file. |
| `--no-recording` | OpenGL only: take the renderer's direct GL route instead of the RHI one. |
| `--verbose` | Renderer warnings on standard error. |

Exit codes: `0` rendered, `1` the probe threw, `2` usage, `3` the environment refused (no software driver,
or the loader returned a hardware adapter), `4` game content missing.

### The three debugging flags, and why they exist

The Vulkan map failure mode is **not an exception**. The recorded frame executes inside the driver's own
worker thread and takes the process with it, so nothing managed runs afterwards — no `finally`, no exit
handler, no buffered writer. Anything reported only at the end is not reported at all.

- `--trace` writes an unbuffered line per command list, so the last line of the file names the pass that
  died.
- `--sync` waits for the device at each frame boundary. The Vulkan device batches every list of a frame into
  one `vkQueueSubmit2` at `EndFrame`, so without this a fault surfaces during whatever ran next; with it, it
  lands on the frame that caused it.
- `--validation` writes each message as it arrives rather than collecting them.

### Bisecting a bad image

`--drop` and `--keep` filter the scene by node type name after the map loads. A map builds thousands of
nodes of a handful of types, and dropping types until the image changes is the cheap way to find which path
is at fault:

```
--keep SceneAggregate      # only the indirect-draw path
--drop SceneAggregate      # everything but it
```

## What it shares with the golden suite, and why

`MapProbe.csproj` compiles six files straight out of `Tests/Renderer/Golden/` rather than copying them:
`SoftwareVulkanIcd`, `ValidationGate`, `GLCallTrap`, `VulkanGoldenDevice`, `RhiDeviceCensus` and
`ImageDiff`.

The tool's whole claim is that a map fails, or renders, *the way the gate would see it*. That requires
selecting the driver by the same rule, refusing a hardware adapter by the same rule, trapping direct OpenGL
by the same rule and scoring against the same tolerances. Six drifting copies would make every number this
tool reports unattributable.

A `ProjectReference` to `Tests.csproj` would be tidier and is not available: that project is a test host, so
referencing it would drag the entire suite and its NUnit runner into a developer tool. None of the six files
touches NUnit — they are already framework-neutral infrastructure that happens to live under `Tests/`. If a
third consumer appears, extract them into a small class library both can reference; with two, the link is
cheaper than the project.
