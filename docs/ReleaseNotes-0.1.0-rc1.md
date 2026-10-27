# StudioMCP 0.1.0 RC1

Private-access pre-release for maintainer acceptance. Repository visibility is unchanged.
This is not a stable release or a distribution of Tom rendering-provider plugins.

## Included

- Complete .NET 8 stdio host plus separate KKS and KK bridge packages.
- 40 MCP tools: the existing authoring tools, optional KKS DOF state/settings, existing BSDF color
  snapshot and native Screencap alpha-mode forwarding. Existing tool names/parameters are preserved;
  capture's new optional alphaMode defaults to None.
- Missing/incompatible-provider fallback, bounded named public API calls, input prevalidation,
  clearer partial-setter failure, readback limits and tested capture hook/busy/resource cleanup.
- English agent guide, portable shader-authoring skill and routes to TomX/TomBSDF/V+ RenderDocs.
- Dependency locks, edition-isolated build outputs, complete dependency/license payloads, per-file
  manifests and whole-archive checksums. Build recipe and read-only package auditor are in source.

## Scope

The MCP adapters, host, tests and packaging are the only changes. KKS_TomSceneEffects, TomBSDF
Runtime, Screencap and other backend provider source/DLLs are neither modified nor bundled.
Provider-owned rendering, defaults and persistence remain unchanged. Snapshot never requests a
service/buffer/render, and capture alpha is implemented by installed Screencap, not this bridge.

KKS is the preview target. KK is a compatibility build with explicit KKS-only fallbacks; later
KKS authoring features are not all newly qualified on KK. The bridge GUID stays
com.tomtom.studiomcp, plugin version 0.1.0. Host/bridge assembly and file versions are 0.1.0.0;
informational version identifies RC1 and its exact source commit. MCP SDK 2.3.0 is a dependency
version, not the product version.

## Acceptance Boundary

Release preparation uses the production-linked 72-case KKS and 37-case KK/capture contract suites,
real-reference builds and offline 40-tool discovery. Those are not GPU image, actual provider
persistence or packaged game acceptance. This RC is supplied for the remaining checks in
ACCEPTANCE.md. No user's running Studio is automatically stopped, modified or deployed into.

Snapshot PNG is clipped LDR without tone mapping, not depth or a viewport image. BeforeTransparents
keeps all later transparent objects sharp, not physical transparent DOF. Provider setter exceptions
can leave partial state: inspect before retrying. New controls do not remove normal provider,
main-thread cost, filesystem, game-version or graphics-plugin limitations.

Download Host plus ONE matching game ZIP, verify SHA256SUMS, then follow INSTALL.md. GitHub source
archives at this tag contain the matching source; no connection data, test output or game binaries
belong in Git or these release assets.
