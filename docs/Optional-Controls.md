# Optional DOF, Buffer Snapshot And Capture Controls

This is a StudioMCP adapter contract. It does not install, enable, rebuild or replace rendering
providers. Native DOF parameters and the backends' rendering/persistence algorithms stay with their
own plugins. Missing providers do not disable ordinary StudioMCP tools.

## DOF State And Settings

`dof_compatibility_get` uses only `TomSceneEffectsPlugin.Plugin` from `KKS_TomSceneEffects`:

- `public static string StateJson()` supplies provider state. Existing JSON fields are preserved.
- Added `mcp` metadata reports provider assembly/version and support for each setter.
- Missing, incompatible or not-ready providers return `available=false`. KK returns `kks_only`.
- `dof_compatibility_set` retains `sceneSession`, optional `scenePolicy` and optional `globalMode`.
  Named values map to the provider's integers: Inherit=-1 (scene only), Native=0,
  BeforeTransparents=1. A request must supply at least one mode.

The adapter calls only `public static void ApplyScenePolicy(int)` and
`public static void ApplyGlobalMode(int)`. It validates both values and all requested signatures
before the first write, and refuses to write while provider state is unavailable. The existing
server write permission, scene-session and capture-busy guards apply. No arbitrary reflection or
provider-owned default change is exposed.

Two provider calls are not a transaction: if a setter throws, an earlier setter may have succeeded.
The error explicitly warns about partial state. Read `dof_compatibility_get` before retrying; the
adapter does not blindly replay changes or invent a rollback of provider-owned configuration.
Prefer one setting per request when isolating a failure. The provider owns all scene/global
persistence; MCP does not add a duplicate save record.

BeforeTransparents keeps all subsequently drawn transparent objects sharp. It is not physical
transparent DOF, selective glass focus or a replacement for native DOF parameter controls.

## Read-only Scene Buffer Snapshot

`dof_buffer_snapshot` retains its parameterless MCP interface. It reads only the existing
`TomBSDFRuntime.TomBSDFSceneService` in `TomBSDF.Runtime`, through static `Current`, instance
`Buffers`, and `TryGetSnapshot(Camera, out snapshot)` with a public RenderTexture `color` field.
These old facade signatures remain the compatibility boundary if services move behind them.

The adapter never creates a service, acquires a lease, requests a buffer, calls Camera.Render,
changes quality or enables a provider. Missing/incompatible services, no main camera, no completed
snapshot or no live color return `available=false` with a reason code. KK is unavailable. A camera
owner field is checked when present, and a sequence field is returned when supplied by the facade.

Readback is blocked during a Screencap job. Only resolved buffers up to 7680 per axis and 16777216
pixels are accepted. The caller cannot choose an arbitrary output path. Output uses a new unique
PNG under the bridge output directory, restores RenderTexture.active and destroys only its own
readable texture. The provider's color texture is never released. File/readback/encoding failures
remain errors; partial writes receive best-effort cleanup.

The PNG is a clipped LDR readback without tone mapping, not a viewport screenshot or a new capture.
`freshCapture=false` is explicit. It is not depth, HDR export, DOF correctness or proof that a new
frame has completed. The snapshot backend is independent of the optional DOF policy provider.

## Native Screencap Alpha Modes

`capture` adds optional `alphaMode`, default `None`, without changing earlier call defaults:

| Name | Installed Screencap operation |
| --- | --- |
| None | Existing no-transparency capture |
| blackout | Native cutout-alpha mode |
| rgAlpha | Native gradual-alpha mode, with that backend's effect limitations |

`studio_status.captureOptions` reports actual supported allowlisted modes and paired-depth API
availability. Unsupported enum names, values/types, API signatures or alpha/depth combinations
are rejected before queuing a job, firing hooks or allocating a capture. Paired depth requires
None and both export/extension methods. Older Screencap can retain supported color modes without
the optional paired-depth API. No alpha extraction algorithm is implemented in MCP.

Capture uses the native pre/post hooks, passing the selected native enum to CaptureRender.
Post cleanup runs once when pre capture began, including failures; a scene change before capture
fires neither hook. Busy state and owned temporary resources are cleared on tested failure paths.
Provider/GPU failures and filesystem limits are still possible; a failure is not permission to
retry blindly or to modify the backend plugin.

## Validation Scope

The [contract harness](../tests/ExtensionContracts/README.md) links production adapters to minimal
stand-ins and requires no game or backend installation. Its checks, real-reference bridge builds
and offline MCP tool discovery are not an actual rendering or scene-save acceptance. Verify the
installed provider versions, missing-provider fallback, PNG output and save/reload behavior with
the final bridge package in an explicitly authorized disposable Studio before release.
