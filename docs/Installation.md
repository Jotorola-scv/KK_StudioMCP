# StudioMCP 0.1.0 RC1 Installation

This is a private-access pre-release for acceptance, not a stable rendering-plugin bundle. Keep
your game, mod stack and current scenes backed up. No installer or test runs automatically.

## Choose Downloads

- `StudioMCP-0.1.0-rc1-Host.zip`: complete framework-dependent .NET 8 host, dependencies and the
  optional portable authoring skill. Tested on Windows; run the DLL through `dotnet`, not BepInEx.
- `StudioMCP-0.1.0-rc1-KKS.zip`: KKS game bridge and its two managed dependencies, in game-relative
  folders. The bridge targets .NET Framework 4.6 / KKS Mono.
- `StudioMCP-0.1.0-rc1-KK.zip`: KK compatibility build, targeting .NET Framework 3.5 / legacy Mono.
  Newer KKS authoring features have not all been requalified on KK; optional DOF/snapshot controls
  deliberately return unavailable there. Do not interpret a successful build as full KK acceptance.
- Verify all archives against the release's `SHA256SUMS.txt`. Each ZIP also contains a per-file
  `PACKAGE-MANIFEST.json` with exact source revision and payload hashes. Matching source is available
  at the same repository tag to recipients with repository access.

The repository remains private. Ask the maintainer for access or a matching source distribution;
do not publish someone else's local game install or connection descriptor as a workaround.

## Prerequisites

The external host requires a compatible .NET 8 runtime. Source builds additionally require a .NET
SDK capable of net8.0, the legacy Framework reference assemblies and your own game reference DLLs.

The game needs its matching BepInEx 5, KKAPI/KKSAPI and ExtendedSave installation. ME is required
for material tools; Screencap for color capture. Paired depth needs the compatible OfflineReShade
export API. Optional Tom Scene Effects, BSDF scene-buffer and Probe providers are separate products,
not dependencies that this package installs. Their absence must remain a supported fallback.

Blender and Unity Editor/Codex Bridge are authoring tools, not runtime requirements for StudioMCP.

## Install Or Upgrade

1. Close the matching Studio normally after saving your work. Do not replace a loaded bridge DLL.
2. Extract Host.zip outside the game and outside Unity Assets; keep the entire `Host` directory.
3. For ONE matching game, back up its existing `BepInEx/plugins/StudioMCP` directory, then copy the
   archive's `BepInEx` folder into that game root. It contains only `<GAME>_StudioMCP.dll`,
   `Newtonsoft.Json.dll` and `websocket-sharp.dll`. Do not copy the host or fixture DLLs there.
4. Ensure there is only one installed bridge identity `com.tomtom.studiomcp` in that game. Do not
   remove unrelated dependencies/providers from other plugin folders.
5. Start Studio normally. The bridge creates a fresh local descriptor at
   `<GameRoot>/UserData/StudioMCP/connection.json`. Never share this file or paste its token.
6. Configure your MCP client to launch `dotnet` with the absolute host DLL path and set
   `STUDIOMCP_CONNECTION` to that game's descriptor. Example placeholders only:

```json
{
  "mcpServers": {
    "studio": {
      "command": "dotnet",
      "args": ["<ExtractedHost>/Host/StudioMcp.Host.dll"],
      "env": {"STUDIOMCP_CONNECTION": "<GameRoot>/UserData/StudioMCP/connection.json"}
    }
  }
}
```

Client configuration formats differ. Merge reviewed settings; do not overwrite unrelated entries.
First call `studio_status` and `scene_snapshot`. The listener is authenticated loopback, default
read-only. Enable writes explicitly only when needed through `Server.AllowWrites` in the bridge's
BepInEx config or a documented process-scoped override. Old scene sessions/handles cannot be reused.

The authoring skill is optional: copy its complete folder from `skills/studiomcp-shader-authoring`
into one client-supported discovery root. It routes shader questions to the matching RenderDocs.
MCP connection alone does not automatically load those documents.

## Acceptance And Recovery

Use `ACCEPTANCE.md` from the archive, or the repository's `docs/RC1-Acceptance.md`. Work in a new
disposable scene. Native scene PNGs containing bridge-owned environment metadata require the game
bridge at load time, but not an active host connection. Optional provider settings still depend on
their own provider's persistence contract.

For uninstall/rollback, close Studio, remove only this bridge's owned plugin folder, and restore
the earlier folder if needed. Remove its MCP client entry/host directory separately. Keep user
scene PNGs and captures; do not recursively delete UserData. A stale connection descriptor is not
proof that a server is running. Removing the bridge may stop restoration of its extra scene data.

## Source Build And Package Audit

From a clean checkout at the release commit, PowerShell 7:

```powershell
$paths = @{
    KksGameRoot = '<your KKS installation>'
    KkGameRoot = '<your KK installation>'
    OutputDirectory = '.local/releases/rc1-new-candidate'
}
./tools/package.ps1 @paths
python ./tools/audit_packages.py --assets .local/releases/rc1-new-candidate/assets --commit <full-source-commit>
```

The recipe requires committed dependency locks, builds edition-isolated outputs and creates a new
owned output directory without deleting earlier candidates. It does not deploy, launch a game,
change provider plugins or assemble a whole mod stack. Build logs remain outside the ZIPs.
