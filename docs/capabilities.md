# Capabilities and boundaries

The bridge is a separate BepInEx plugin. The stdio MCP host runs outside Unity. A small bounded
transport queue brings requests onto the Unity main thread; it does not replace or patch ME's edit
lifecycle. Ordinary edits finish before their RPC returns. Screencap is an explicit asynchronous job
because it must wait for the end of a rendered frame; the MCP capture tool waits and returns the image.

| Area | Tools / behavior |
| --- | --- |
| Scene | `scene_snapshot`, `add_item`, `add_character`, `set_transform`. Studio object IDs, hierarchy, saved transforms and renderer bounds. |
| Camera | `camera_get`, `camera_set`. Orbit pivot, Euler rotation, distance and field of view. The pivot is distinct from the camera eye. `camera_slots_get`, `camera_slot_save`, `camera_slot_load` use Studio's native camera slots, which are saved in the scene PNG. |
| Lights | `catalog_search`, `add_light`, `light_set`. Native Studio light color, intensity, range, cone, enabled/shadow state, plus explicit culling mask and render mode. |
| ME | `materials_list`, `material_inspect`, `material_set_shader`, `material_set`, `material_reset`, `material_import_texture`, `material_set_render_queue`. Shader/Float/Color/render-queue edits and PNG/JPEG import use installed ME public persistence APIs. |
| Character scope | Body/head, hair, clothing and accessories; part slots are resolved from the actual renderer hierarchy. KKAPI includes MoreAccessories slots. |
| Native rendering | `render_get`, `render_set`, `render_diagnostics`. Ambient mode and sky/equator/ground colors, fog, shadow distance/cascades, MSAA, pixel-light count, realtime probes, camera clear settings and camera far clip (10-20000; restored on loading a scene without it). Diagnostics report camera depth/HDR, light layers and live TomProbe capture state. |
| Native Studio effects | `native_effects_get`, `native_effects_set`. Original SceneInfo AO, bloom, fog, DOF, vignette, sun shafts, color-effect blend and character-light intensity. No external post stack is installed or controlled. |
| Sky | `native_sky_get`, `native_sky_set` select original KKS presets. `skybox_bind` binds an inspected item's native Skybox material and stores the item/renderer reference in the scene PNG. |
| Tonemapping | `tonemap_get`, `tonemap_set`. The Studio camera's existing AmplifyColor tonemapper (Disabled, Photographic, FilmicHable, FilmicACES), exposure and linear white point. Opt-in; saved in the scene PNG. Scene loads reset only bridge-owned edits to values captured before its first write. Read-only inspection does not affect restoration. |
| Visibility and tiling | `set_visible` uses the native tree visibility path. `material_texture_transform` writes texture offset/scale using ME persistence APIs. |
| Output | `capture`. Screencap pre/post hooks, full PNG on disk and MCP image preview up to 960 pixels wide. Optional `withDepth` invokes the installed paired color/depth exporter and returns raw depth plus metadata paths. |
| Batch | `batch_apply`, `batch_undo`. 1–32 Float/Color, transform, camera or native-render operations. Validate all first; reverse applied operations on exceptions. One-level undo, invalidated by subsequent bridge writes or scene changes. |
| Recovery | `scene_checkpoint`, `scene_restore`. Native scene PNG with normal save hooks and embedded opt-in environment/light metadata; a compatibility sidecar is also written. Restore replaces the current scene. |

## Important limits

- ME edits are matched by the provider's material-name and object/part scope. They are not isolated
  to an individual renderer slot when several materials share that name. Inspect `matchingMaterials`.
- Renderer/material handles are ephemeral. Scene reloads invalidate the session. Character and clothing
  mods can also rebuild renderers without reloading a whole scene; fetch fresh handles after such changes.
- KK shader enumeration is limited to properties described by installed ME metadata. KKS can enumerate
  Unity shader properties. Unsupported kinds such as vectors and keywords are not writable in this version.
- Texture imports accept local PNG/JPEG files up to 64 MiB. Use scene checkpoints to recover previous
  texture imports; texture changes and object creation are deliberately outside reversible batches.
  Old ME's `setTexInUpdate` flag is set to false to select its existing synchronous import path.
- After `render_set` opts into ownership, native settings are saved in PNG ExtendedSave data and
  applied once after native scene initialization. Light masks/render modes are also saved when
  explicitly set. This requires the bridge plugin at load time, not an active MCP client. Scene
  import preserves the destination environment while mapping imported light overrides to new IDs.
  Arbitrary Graphics/IBL/post-processing plugin settings are not exposed; those plugins can still
  override native settings later. This is not an ongoing override loop.
- Bones/poses, object deletion and scene import/merge are not MCP tools yet.
- Undo restores ME's saved override or removes a newly created override. It does not snapshot arbitrary
  third-party plugin state. Perform undo before additional manual edits. Rollback errors are reported.
- Native scene PNGs remain loadable through Studio normally. New bridge-authored cards embed their
  environment, explicit light overrides and display names. Keep sidecars for older checkpoints.
  Other mods can still overwrite environment settings or display labels.
- Only bridge checkpoints under its own output directory can be restored. Checkpoint/image files remain
  on disk until you remove them. Capture job history retains eight results; RPC metadata cache is bounded.
- Capture dimensions are bounded to 7680 per axis and 100 million internal pixels including supersampling.
  Large texture decoding and GPU readback still have a main-thread cost; this is not a background renderer.
- `withDepth` requires the public OfflineReShade export API in the installed Screencap fork.
  Ordinary Screencap remains sufficient for color captures. Depth format and row orientation come
  from the returned metadata; do not assume every installation exports PNG depth.
- Scene authoring extensions added for Rainline are qualified on KKS only. They have not been
  requalified on KK/Unity 5.6. TomProbe diagnostics are optional and do not install that plugin.
- Native effect controls refresh bounded, version-specific Studio UI models after validating the
  complete request. Another graphics plugin can still override those components. This is not an
  arbitrary component editor or a continuous settings override.
- Particle diagnostics distinguish camera layer inclusion from an active, enabled renderer. They do
  not claim per-particle frustum or occlusion visibility. Particle counts/bounds change with simulation.

The endpoint binds to loopback, requires a generated token, and defaults to read-only. Enable writes
explicitly. This prototype has been tested against the local plugin installations documented in the
verification report; it is not a promise of compatibility with every ME/Screencap fork or graphics mod.
