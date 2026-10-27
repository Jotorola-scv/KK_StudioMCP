# Optional Control Contract Tests

Run from the StudioMCP repository with a .NET 8-compatible SDK and runtime:

```powershell
dotnet run --project tests/ExtensionContracts/Runner/Runner.csproj -c Release -- .local/extension-contracts/KKS
dotnet run --project tests/ExtensionContracts/Runner/Runner.csproj -c Release --property:DefineConstants=KK -- .local/extension-contracts/KK
```

These link the production MCP DOF/snapshot and capture adapters, using the same legacy
Json.Net.Unity3D 9.0.1 assembly as the bridge. Minimal public contract doubles stand in for Unity,
the named DOF/scene-buffer facades and Screencap. They are not rendering-plugin implementations.
Never deploy any fixture DLL to a game. No game assemblies, installed plugins or live connection
descriptor are read by the tests. All generated files belong under ignored `.local`/`bin`/`obj`.

Coverage includes missing/incompatible/not-ready providers, mode validation and forwarding,
prevalidation before setters, explicit partial failure, no-demand snapshot behavior, camera and
readback limits, source-buffer ownership, native alpha forwarding, older capture API fallback,
paired-depth restrictions, stale sessions, busy handling and hook/resource cleanup on failures.
The KK profile verifies KKS-only fallback plus the common capture adapter.

Successful tests establish MCP adapter logic only. They do not validate real GPU pixels, the
backend plugin's algorithms or persistence, scene PNG reloads, actual ScreenshotManager events,
provider startup order, live write/session guards or performance. Actual KKS/KK bridge builds
against the user's game references and offline discovery of the 40 MCP tools are separate checks.
Run authorized live integration tests before claiming a packaged runtime acceptance.
