# Release Readiness: 0.1.0 RC1

Review date: 2026-10-27. RC1 is a private-access pre-release for maintainer acceptance, not a stable
binary qualification. Repository visibility and rendering-provider implementations are unchanged.

## Frozen Candidate

This candidate includes the MCP-only DOF state/settings, existing-buffer snapshot and native capture
alpha adapters, production-linked contract tests, English agent/RenderDocs routing, dependency locks
and audited packaging tools. The source commit is recorded in each PACKAGE-MANIFEST.json and must
match the release tag. Test doubles are source tests, never deployment DLLs.

Product version comes from Directory.Build.props: 0.1.0-rc1, assembly/file version 0.1.0.0. The stable
BepInEx GUID and plugin metadata version remain com.tomtom.studiomcp / 0.1.0. Informational versions
carry the exact source revision. MCP SDK 2.3.0 is a separate dependency version.

## Automated Gates

| Gate | Scope |
| --- | --- |
| KKS contract suite | 72 linked-production adapter cases with minimal backend/Unity doubles |
| KK/capture contract suite | 37 cases, including explicit KKS-only fallback |
| Host and both game bridge builds | Real-reference Release builds, zero warnings/errors in preparation |
| Offline initialize/tools/list | 40 tools; parameterless snapshot, session-required setter and None capture default preserved |
| Portable documentation/skill | English, local links and the three reviewed shader RenderDocs routes checked |
| Dependency freezes | Host and per-edition NuGet lock files; release recipe enforces locked restore |
| Edition isolation | Default bridge outputs and release staging are separated by KK/KKS; deploy helper uses matching paths |
| Archive gate | tools/audit_packages.py must verify exact contents, dependencies, source/version and every payload hash |
| Release gate | Download uploaded assets and recheck SHA256SUMS; keep repository private and release pre-release |

Build, package and protocol responses stay under ignored local outputs. Public release summaries
may report these checks without uploading raw evidence or connection descriptors. The package
recipe refuses a dirty source tree, existing output directory or missing game reference roots;
it never deletes prior candidates or deploys into a game.

## Ready For Pre-release, Not Stable

The automated preparation supports creating the RC for acceptance. It does not qualify real
provider behavior, GPU image correctness, scene persistence, long-running stability, arbitrary
graphics mods or every newer feature on KK. Use [the RC1 checklist](RC1-Acceptance.md) against the
downloaded package in a disposable Studio before promoting it.

Remaining gates for a stable release:

1. Install the exact downloaded host and matching game bridge with explicit authority. Check
   default read-only/authentication, stale sessions, material-name scope, missing providers,
   capture cleanup, actual images/depth and native PNG cold reloads.
2. Validate real-provider scene/global policy persistence without changing provider implementations;
   restore prior global settings. Missing/unsupported resources must preserve ordinary behavior.
3. Decide the declared KK support matrix. Earlier common-flow checks do not qualify every later
   KKS camera/environment/tonemap or optional-provider behavior on Unity 5.6.
4. Run longer/failure-path acceptance as appropriate. The live extended verifier now accepts
   explicit game/server/output paths and needs Python/Pillow; it remains opt-in and requires an
   empty disposable scene. Contract fixtures do not replace real capture-event or GPU cleanup tests.
5. Choose an eventual public distribution/access policy and complete support/security triage.
   This task does not change the private repository's visibility or expose local connection tokens.

## Installation, Licenses And Rebuilding

See [Installation](Installation.md), [RC1 release notes](ReleaseNotes-0.1.0-rc1.md),
[optional-control contracts](Optional-Controls.md), [contract test instructions](../tests/ExtensionContracts/README.md)
and [dependency notices](../THIRD-PARTY-NOTICES.md).

The host archive includes all published framework-dependent host files, not the .NET runtime.
Each game archive contains only its own bridge and two redistributed dependencies, not game/API/Unity
assemblies, backend rendering providers or a complete mod stack. Matching source is available from
the same private-access tag. Licenses, NuGet dependency metadata and manifests are intentional
delivery assets, not test intermediates.

No game is stopped or modified by the packaging/audit tools. No rendering-provider source, DLL,
shader, defaults or save keys are changed. See [verification history](verification.md) for earlier
checks with their own dates and limits; do not substitute them for this RC's packaged live acceptance.
