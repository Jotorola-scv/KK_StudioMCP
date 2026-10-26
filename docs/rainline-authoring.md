# Rainline scene authoring extensions

These changes support a native, reloadable TomBSDF material showcase in KKS Studio. They extend the
existing plugin source and MCP host, rather than introducing a second bridge. All scene changes use
Studio, ME, KKAPI and Screencap APIs. No desktop automation or arbitrary runtime script endpoint was
added. Unity asset construction remains a separate Codex Editor Bridge operation.

## Added capabilities

| Capability | Implementation | Persistence or boundary |
| --- | --- | --- |
| Runtime item and light catalog | `SceneCommands` and host schemas | Uses actual resolved IDs; do not cache IDs across game restarts. |
| Native lights | `add_light`, `light_set` | Standard parameters use `OCILight` setters; culling mask and render mode use versioned scene extended data. |
| ME shader selection | `material_set_shader` | Registered shader names only; the ME scene controller saves the override. |
| Camera orbit distance | `camera_get`, `camera_set` | Uses native camera export/import; distance is distinct from pivot and eye position. |
| Environment controls | `RenderCommands` | Ambient mode/colors, camera clear settings and realtime probe flag join the existing bounded settings. |
| Scene persistence | `BridgeSceneController` | Opt-in environment, names and explicit light overrides are embedded in native PNG scene data. Load restores once after initialization; import does not replace destination environment. |
| Depth export | `CaptureCommands` | Calls `ExportOfflineReShadeInputs`; returns color, raw depth and metadata paths. No replacement camera-depth implementation. |
| Reflection diagnostics | `render_diagnostics` | Reports native lights plus actual TomProbe runtime capture textures, including detached hidden probes missed by ordinary object enumeration. |
| Renderer diagnostics | `materials_list` | Exposes each renderer's actual GameObject layer. |

## Why explicit lighting layers matter

In this KKS installation, Studio assigns imported scenery to layer 10. Its default newly created
lights used mask 2064, which excluded that layer. A valid light object therefore did not necessarily
illuminate the scenery. The showcase explicitly uses mask `-1` and `ForcePixel` for its lights and
saves those values. This is a scene choice, not a global change to all game lights.

The default TomProbe environment capture preset also omitted the layer containing this scenery.
The scene uses `ProbeCapturePreset = 1` on its two reflection probes and SH volume. It refreshes
captures after material and lighting edits. The bridge inspects public controller state; it does
not patch TomProbe or synthesize reflection textures.

## Dependencies and safety

- The plugin adds a compile reference to the game's existing ExtensibleSaveFormat DLL.
- ME remains responsible for shader, material and imported texture persistence.
- TomProbe is optional for the bridge, but required by this showcase's local IBL/SH setup.
- Paired depth requires the local OfflineReShade-capable Screencap fork. Color capture still uses
  ordinary Screencap APIs and always brackets capture with pre/post hooks.
- Read-only defaults, loopback authentication, scene-session guards, bounded requests, capture limits
  and resource cleanup remain in place. Connection tokens are not part of any showcase delivery.
- Newly exposed light-mask values are validated as signed 32-bit integers before mutating the light.
- Existing scenes with no bridge-owned environment or light overrides are not given an environment
  override. Imported scene metadata does not take ownership of the destination's environment.

## Qualification

The new authoring path targets KKS with Unity 2019.4.9f1. No Unity 5.6 asset build or new KK runtime
qualification is claimed. The Rainline evidence contains real game renders, property readbacks,
native PNG reload checks and device-depth exports. Editor preview images are not substituted for
game acceptance. Scene import override mapping has a code path but is not part of this task's live
acceptance; ordinary full scene loading is the supported showcase workflow.

Depth remains the camera's actual opaque depth. Refractive glass normally exposes the background
depth, so a single depth layer cannot give both the glass interface and the transmitted subject to
DOF. Normal-map ripples and droplets also do not change geometric depth.
