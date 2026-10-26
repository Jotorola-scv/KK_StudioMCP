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

The broader goal is **not complete**. Character-part material scopes, texture import, render settings,
high-resolution capture verification, batch recovery, save/load persistence verification, and KK
runtime verification remain to be implemented or verified.

All interaction and verification uses PowerShell, MCP, game APIs and exported image files.
No mouse/keyboard automation, computer-use tools, or desktop capture is used.

## Structure

- `src/StudioBridge`: .NET Framework BepInEx bridge. All Unity access executes on the main thread.
- `src/StudioMcp.Host`: standalone .NET 8 stdio MCP server; the MCP SDK is not loaded into Unity.
- `tools/mcp_client.py`: stdio protocol verifier, including actual image-content decoding.
- `tools/verify_mvp.py`: live ME/Screencap closed-loop verification.
- `tools/deploy.ps1`: installs only the bridge's own plugin directory and backs up replaced files.
  It only closes processes carrying the test harness's own launch marker.

References, credentials, build output, generated images and local evidence are git-ignored.

## Build and install

```powershell
dotnet build src/StudioMcp.Host/StudioMcp.Host.csproj -c Release
./tools/deploy.ps1 -GameRoot '<KKS installation>' -Game KKS -Launch -EnableWrites
```

The bridge defaults to read-only. The test launcher enables writes only in the launched process's
environment. For ordinary use, explicitly enable `Server.AllowWrites` in the bridge configuration.
The listener binds to loopback and requires a generated token. Do not share `connection.json`.

Configure an MCP client to run `dotnet` with the built `StudioMcp.Host.dll`. Set
`STUDIOMCP_CONNECTION` to `<game>/UserData/StudioMCP/connection.json`.
Read `studio_status` or `scene_snapshot` first; writes must include that scene's `sceneSession`.

## Research baseline

- KKStudioSocket: `https://github.com/great-majority/KKStudioSocket`,
  commit `ee47ca555ff2764d69ea41a46e8fb4dbaf436e25`, GPL-3.0.
- KoikatuMCP: `https://github.com/great-majority/KoikatuMCP`,
  commit `d17c27006f8d9f7f45861e0fe77ee3db5518adab`, MIT.

These projects demonstrated the Studio API and image-return path. The independent bridge uses a
separate plugin GUID, authenticated endpoint, bounded dispatch and the installed ME/Screencap APIs.
Research clones live under `.references/` and are not included in this repository.

StudioMCP is licensed under GPL-3.0. Dependency licenses remain applicable.
