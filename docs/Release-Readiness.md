# Release Readiness: 0.2.0 RC1

Review date: 2026-10-31. This public release candidate adds character posing and animation to the existing
scene/material/rendering tools, including direct blendshape linking consistent with KKPE.
Publishing does not change the repository's visibility or qualify
every game edition and provider combination.

## Frozen Candidate

Native FK/IK uses Studio's original interfaces. Fine bones and blendshapes use KKPE's override and
persistence methods; tracks and keyframes use existing Timeline/KKPE models. No new solver,
animation scheduler or saved-data format is introduced. Optional providers are not bundled.

Product version comes from Directory.Build.props: 0.2.0-rc1, assembly/file version 0.2.0.0.
BepInEx metadata uses version 0.2.0 and the unchanged GUID com.tomtom.studiomcp. Informational
versions and package manifests record the exact source commit; it must match the release tag.
The MCP SDK dependency version remains separate from the product version.

## Release Gates

| Gate | Scope |
| --- | --- |
| Existing adapter contracts | 72 KKS and 37 KK cases with linked production sources and minimal contract doubles |
| Real-reference builds | Release host plus KK and KKS bridges, locked dependency restore, no warnings/errors |
| Packaged host discovery | 60 tools; all original 40 tools retained, existing parameter/default contracts preserved |
| KKS animation acceptance | Native FK/IK, KKPE channels and resets, ten track kinds, interpolation, native scene reload and capture |
| Provider versions for that live check | KKPE 2.21.4 and Timeline 1.5.4; other versions need their own acceptance |
| Archive audit | Exact source/version, payload inventory, dependency allowlists and every file/archive hash |
| Published assets | Download all three archives and SHA256SUMS, repeat the audit, and retain the declared RC version and validation scope |

The KKS live check used an owned disposable scene and a copied card. It restored ten native tracks
and twenty keys after loading, including a step curve; the source card was unchanged. Read the
[verification history](verification.md) for the scope and limits. Existing contract fixtures do
not cover animation and do not replace these real-provider checks.

The linked-eye regression passed on four real KKS meshes, including negative/overdrive weights,
reset, native scene save/reload and Timeline interpolation. Thirteen production-linked adapter
checks covered the link switch, distinct originals and remapped indices using contract doubles.

The new animation tools are build-verified only on KK. A KKS pose/capture does not qualify other
provider versions, other posing/physics plugins, long-running motion or GPU failure paths.

## Packaging And Installation

See [Installation](Installation.md), [release notes](ReleaseNotes-0.2.0-rc1.md),
[character animation](Character-Animation.md), [acceptance checklist](RC1-Acceptance.md),
[contract tests](../tests/ExtensionContracts/README.md) and [dependency notices](../THIRD-PARTY-NOTICES.md).

Package only committed source. The recipe rejects a dirty tree, an existing output directory or
missing game references; it never deletes previous candidates, deploys or starts/stops a game.
The host archive contains the framework-dependent host and dependencies, not the .NET runtime.
Each game archive contains its matching bridge and two managed dependencies, not game/API/Unity
assemblies or a complete mod stack. Matching source is available from the same public tag.

Raw responses, connection descriptors/tokens, user cards, captures, logs, deployment backups and
build intermediates stay outside Git and release assets. Rendering-provider implementations,
defaults and saved keys remain unchanged. Downloaded-package installation and any broader stable
release qualification should follow the acceptance checklist in a disposable Studio scene.
