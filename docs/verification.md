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
