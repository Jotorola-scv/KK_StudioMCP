# Capabilities and boundaries

The bridge is a separate BepInEx plugin. The stdio MCP host runs outside Unity. A small bounded
transport queue brings requests onto the Unity main thread; it does not replace or patch ME's edit
lifecycle. Ordinary edits finish before their RPC returns. Screencap is an explicit asynchronous job
because it must wait for the end of a rendered frame; the MCP capture tool waits and returns the image.

| Area | Tools / behavior |
| --- | --- |
| Scene | `scene_snapshot`, `add_item`, `add_character`, `set_transform`. Studio object IDs, hierarchy, saved transforms and renderer bounds. |
| Camera | `camera_get`, `camera_set`. Orbit pivot, Euler rotation and field of view. The pivot is distinct from the camera eye. |
| ME | `materials_list`, `material_inspect`, `material_set`, `material_reset`, `material_import_texture`. Float/Color edits and PNG/JPEG import use installed ME public persistence APIs. |
| Character scope | Body/head, hair, clothing and accessories; part slots are resolved from the actual renderer hierarchy. KKAPI includes MoreAccessories slots. |
| Native rendering | `render_get`, `render_set`. Ambient light/intensity, fog/color/density/distances, shadow distance/cascades, MSAA and pixel-light count. |
| Output | `capture`. Screencap pre/post hooks, full PNG on disk and MCP image preview up to 960 pixels wide. 4K was verified on both games. |
| Batch | `batch_apply`, `batch_undo`. 1–32 Float/Color, transform, camera or native-render operations. Validate all first; reverse applied operations on exceptions. One-level undo, invalidated by subsequent bridge writes or scene changes. |
| Recovery | `scene_checkpoint`, `scene_restore`. New native scene PNG with normal save hooks, plus a sidecar for native render settings and display labels. Restore replaces the current scene. |

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
- Native Unity render settings are runtime settings. A bridge checkpoint retains them in its sidecar;
  an ordinary third-party scene save may not. Arbitrary Graphics/IBL/post-processing plugin settings
  are not exposed. Such plugins may override native settings on later frames.
- Camera distance, bones/poses, lights, object deletion and scene import/merge are not tools yet.
- Undo restores ME's saved override or removes a newly created override. It does not snapshot arbitrary
  third-party plugin state. Perform undo before additional manual edits. Rollback errors are reported.
- Native scene PNGs remain loadable through Studio normally. Keep the `.bridge.json` sidecar when you
  want bridge-specific render settings/display labels restored. Other mods can override display labels.
- Only bridge checkpoints under its own output directory can be restored. Checkpoint/image files remain
  on disk until you remove them. Capture job history retains eight results; RPC metadata cache is bounded.
- Capture dimensions are bounded to 7680 per axis and 100 million internal pixels including supersampling.
  Large texture decoding and GPU readback still have a main-thread cost; this is not a background renderer.

The endpoint binds to loopback, requires a generated token, and defaults to read-only. Enable writes
explicitly. This prototype has been tested against the local plugin installations documented in the
verification report; it is not a promise of compatibility with every ME/Screencap fork or graphics mod.
