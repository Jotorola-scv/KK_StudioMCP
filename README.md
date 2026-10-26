# StudioMCP

An independent, local-only MCP bridge for Koikatsu Studio and Koikatsu Sunshine Studio.
It consumes installed plugins' public APIs; it does not patch Material Editor source or save formats.

## Current verified state

- Original KoikatuMCP + KKStudioSocket built and exercised in a real KKS process: scene tree,
  catalog item creation, saved object transforms, camera changes, and MCP PNG image content.
- Independent KKS bridge and MCP 2.3.0 host built and exercised in the same installation.
- ME color and float writes were read back from the material using ME's scene-controller API.
- Screencap produced real 640x360 PNGs with its pre/post-capture hooks and temporary-resource cleanup.
- The agent inspected a red sphere, changed it to blue, tightened the camera, and inspected the next
  returned MCP image. The integration verifier also checked the two rendered color regions.

- KKS extended verification passed: Character/Hair/Clothing/Accessory scopes; item and clothing
  texture import; Float/Color and texture persistence after scene reload; batch prevalidation and
  undo; native render settings; checkpoint restore; stale-session rejection; and 3840x2160 Screencap.

- KK passed the same extended checks against its actual Unity 5.6 / Mono runtime.
  Its bridge targets .NET 3.5; KKS targets .NET 4.6. Shader introspection on KK uses ME metadata.

All interaction and verification uses PowerShell, MCP, game APIs and exported image files.
No mouse/keyboard automation, computer-use tools, or desktop capture is used.

## Scene authoring extensions

The Rainline KKS showcase adds native light creation and light-layer control, saved ME shader
selection, camera orbit distance, reflection/SH diagnostics, and paired color/depth export through
the installed OfflineReShade-capable Screencap. Opt-in environment settings and light overrides
are embedded in the native scene PNG through KKAPI/ExtendedSave; a checkpoint sidecar is no longer
the only copy. Loading a scene requires this bridge to restore those extra settings, but does not
require an MCP host connection or write-enabled server.

See [the scene authoring change record](docs/rainline-authoring.md) for dependencies, persistence
boundaries and the KKS-only qualification of these new features. The previous KK verification
above does not qualify these later additions on Unity 5.6.

The four-weather Rainline extension adds native Studio AO, bloom, fog, DOF and character-light
controls, native sky selection, reloadable panoramic sky binding, saved texture tiling and object
visibility. It uses the existing game effect components, not PPE, a filter item or a replacement
postprocessing stack. See [the weather authoring record](docs/rainline-weather.md).

## Structure

- `src/StudioBridge`: .NET Framework BepInEx bridge. All Unity access executes on the main thread.
- `src/StudioMcp.Host`: standalone .NET 8 stdio MCP server; the MCP SDK is not loaded into Unity.
- `tools/mcp_client.py`: stdio protocol verifier, including actual image-content decoding.
- `tools/verify_mvp.py`: live ME/Screencap closed-loop verification.
- `tools/verify_extended.py`: live character, texture, batch, checkpoint and 4K verification.
- `tools/deploy.ps1`: installs only the bridge's own plugin directory and backs up replaced files.
  It only closes processes carrying the test harness's own launch marker.

References, credentials, build output, generated images and local evidence are git-ignored.

## Build and install

```powershell
dotnet build src/StudioMcp.Host/StudioMcp.Host.csproj -c Release
./tools/deploy.ps1 -GameRoot '<KKS installation>' -Game KKS -Launch -EnableWrites
# For Koikatsu, use its installation path and -Game KK instead.
```

The bridge defaults to read-only. The test launcher enables writes only in the launched process's
environment. For ordinary use, explicitly enable `Server.AllowWrites` in the bridge configuration.
The listener binds to loopback and requires a generated token. Do not share `connection.json`.

Configure an MCP client to run `dotnet` with the built `StudioMcp.Host.dll`. Set
`STUDIOMCP_CONNECTION` to `<game>/UserData/StudioMCP/connection.json`.
Read `studio_status` or `scene_snapshot` first; writes must include that scene's `sceneSession`.

A typical client configuration is:

```json
{
  "mcpServers": {
    "studio": {
      "command": "dotnet",
      "args": ["<absolute repository path>/src/StudioMcp.Host/bin/Release/net8.0/StudioMcp.Host.dll"],
      "env": {"STUDIOMCP_CONNECTION": "<game>/UserData/StudioMCP/connection.json"}
    }
  }
}
```

See [capabilities and boundaries](docs/capabilities.md) and [live verification](docs/verification.md).

## Research baseline

- KKStudioSocket: `https://github.com/great-majority/KKStudioSocket`,
  commit `ee47ca555ff2764d69ea41a46e8fb4dbaf436e25`, GPL-3.0.
- KoikatuMCP: `https://github.com/great-majority/KoikatuMCP`,
  commit `d17c27006f8d9f7f45861e0fe77ee3db5518adab`, MIT.

These projects demonstrated the Studio API and image-return path. The independent bridge uses a
separate plugin GUID, authenticated endpoint, bounded dispatch and the installed ME/Screencap APIs.
Research clones live under `.references/` and are not included in this repository.

StudioMCP is licensed under GPL-3.0. Dependency licenses remain applicable.
