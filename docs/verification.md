# Live verification — 2026-10-26

All operations used PowerShell, stdio MCP requests and game APIs. Images were exported by Studio /
Screencap and then opened as files. No computer-use tools, input simulation or desktop capture were used.
Test Studios started empty; original cards and user scenes were not overwritten. Only bridge-owned
plugin folders were installed, with backups when replacing a bridge DLL.

## Existing projects

The unmodified KKStudioSocket KKS plugin and KoikatuMCP host were compiled and exercised first.
Through actual MCP tools: read the empty scene, create a catalog sphere, move and scale it, change the
camera, and decode/view the returned PNG image block. This established that the native Studio object,
camera and image APIs were reusable. Exact reference commits are in the README.

## Independent MVP

On KKS, ME changed a sphere to red and changed a shader Float. Screencap returned a 640×360 PNG
as MCP image content. After inspecting it, the agent changed the sphere to blue, adjusted the Float
again and tightened the field of view. The next returned image was inspected too. Pixel checks found
12,064 red pixels initially and 16,249 blue pixels after the adjustment.

## Extended run

The same `tools/verify_extended.py` ran successfully on both KKS and KK:

1. Create a sphere, inspect shader metadata, and obtain actual property values.
2. Reject a batch containing an invalid FOV before moving anything.
3. Apply Float/Color, transform, camera and native render changes in a batch; read back the changes.
4. Undo the batch and compare original material values and shadow distance.
5. Apply another batch and import a generated 64×64 checker texture through ME.
6. Load existing character cards far outside the capture camera; identify Character, Hair, Clothing and
   Accessory scopes; modify a color in each scope. Import the checker into a clothing material.
7. Save a new native scene checkpoint. Reset the item texture, change its color and shadow distance.
8. Restore the checkpoint. Reject the previous scene token; reacquire renderer handles. Confirm item
   Float/Color and texture, all four character colors, clothing texture, and native shadow distance.
9. Capture a 3840×2160 PNG through Screencap; inspect the image file and confirm the bridge leaves
   capture-busy state. The full PNG dimensions are checked, not inferred from a requested resolution.

Both bridge variants and the MCP host compiled with zero warnings/errors. KK's first runtime attempt
exposed an unsupported .NET API; its target was corrected to .NET 3.5 and the complete run repeated.

Installed providers during verification:

- KKS: Material Editor 4.0.3, KKSAPI 1.45.1, Screencap 21.1.3.
- KK: the actual installed KK Material Editor, KKAPI 1.46.1 and Screencap binaries; Unity 5.6 / legacy Mono.
- Both installations include additional character, accessory and rendering mods. They were retained.

Local protocol responses, PNGs and run summaries are under `.local/evidence/` and intentionally ignored
by Git. `extended-kks/summary.json` and `extended-kk/summary.json` record the last successful runs.

## Not covered by these runs

No long soak test, forced out-of-memory test, arbitrary graphics-plugin integration or exhaustive shader
coverage. Exception rollback is implemented but was not tested by injecting an artificial exception
inside a native setter; rejection-before-write and successful undo were exercised. Neither VR nor
multi-client concurrent authoring was validated. See the capability boundaries before using real scenes.

## Scene-authoring publication checks

The later Rainline additions are described separately in `rainline-authoring.md` and
`rainline-weather.md`. Before publishing the combined source:

- The MCP host and both current bridge targets compiled with zero warnings/errors. The KK reference
  path was corrected to its actual `KK_BepisPlugins/ExtensibleSaveFormat.dll` filename.
- A live, read-only KKS connection reported Studio, ME, Screencap and paired depth ready. The actual
  stdio MCP server exposed all 31 tool schemas. The current scene was not edited for publication.
- Existing SunnyDay, RainyDay, MoonlitNight and RainyNight reload audits each reported no differing
  entries, no sidecar dependency, and ready probe capture resources.
- Batch undo now restores the previous opt-in environment-persistence flag as well as the values.
  This small publication correction was compiled and reviewed; it was not given a new runtime test.
- Source was checked for credential patterns. Local credentials, game assemblies, research clones,
  build output and showcase evidence are excluded from the source upload.

Successful KK compilation is not new runtime qualification of the later KKS scene-authoring tools.

## Tonemapping and sky reload fix — 2026-10-27

Exercised through the stdio MCP host on a second KKS installation (Material Editor 5.0, KKSAPI 1.45.1,
Screencap 21.1.3) with the four Rainline weather cards:

- `tonemap_get` reported the camera default (Disabled, exposure 1.0, white point 11.2). `tonemap_set`
  switched to FilmicACES, FilmicHable and Photographic; each change was confirmed by a capture. The
  values were saved in new native scene PNGs and restored, with `owned` set, after cold reloads.
- Before the fix, native-sky cards loaded directly after custom-sky cards (Sunny Day after Rainy Day,
  Moonlit Night after Rainy Night) rendered with the earlier scene's skybox material. After it,
  `native_sky_get` reported each card's own Funly material and captures matched fresh-load references.
- Four re-authored cards were reloaded cold with no differences in tonemapping, native render settings,
  native effects, light overrides or tree visibility; all three probe controllers reached Ready.

The MCP host and KKS bridge compiled with zero warnings/errors. The KK target was not compiled or run
for this change.

## Camera far clip — 2026-10-27

The ProceduralParadise ClubRoom exterior spans about 8.3 km, beyond Studio's 1000 m camera far plane,
so its city skyline and sky halo were clipped. `render_set` now accepts `cameraFarClip` (10-20000).
Live checks on the same KKS installation:

- A ClubRoom capture at 9000 showed the clipped city and halo; 1000 did not.
- A value of 5 was rejected without changing the far plane.
- A checkpoint saved at 9000 restored 9000. A second checkpoint saved at 7500 restored 7500 with its
  `.bridge.json` sidecar deleted, so the value is read from the scene PNG's extended data.
- Loading a card without the setting, including after each restore, returned the far plane to 1000.

The KK target was not compiled or run for this change.

## Material render queue — 2026-10-27

`material_set_render_queue` calls Material Editor 5.0's `SetMaterialShaderRenderQueue` (or
`RemoveMaterialShaderRenderQueue` for -1); `materials_list` and `material_inspect` now report the queue.
On the ProceduralParadise ClubRoom window glass, 2475 survived a checkpoint save and reload from another
card, and -1 restored the shader default 3000.

Depth-texture check on the CrystalFantasy glass, with native DOF and paired depth export: tom/BSDFGlass at
3000 and 2475 and tom/BSDFAlpha (Cutout caster) at 3000 exported byte-identical depth; tom/BSDFAlpha at
2475 moved 46.6% of pixels nearer. Unity only draws queues <= 2500 into the camera depth texture, through
the ShadowCaster pass, and tom/BSDFGlass has none. At 2475 the skybox also overwrites tom/BSDFGlass
wherever the sky is behind it. Material Editor 4.x was not tested; the tool reports when the API is absent.

## Camera slots — 2026-10-27

`camera_slots_get`, `camera_slot_save` and `camera_slot_load` use Studio's `SceneInfo.cameraData` (10 slots)
through `CameraControl.Export` and `Import`, the same path as Studio's camera save and load buttons, so
`SceneInfo.Save` writes the slots into the scene PNG. Live checks on the same KKS installation:

- The three ProceduralParadise v3 cards stored 5, 6 and 5 slots, with slot 0 as the main view. After a cold
  reload of each checkpoint, `camera_slots_get` returned the saved pivot, rotation, distance and field of view
  within 1e-3 for every stored slot, and the main camera matched slot 0.
- Captures of the main view and of `camera_slot_load` on the last stored slot reproduced the original 4K
  renders: mean absolute difference 0 of 255 after downscaling to 480x270.
- Slots 10 and -1 were rejected ("Use 0-9.") without moving the camera, and 1.5 was rejected as a
  non-integer. Slots the tool did not save were reported with whatever the card already held.

The KK target was not compiled or run for this change.
