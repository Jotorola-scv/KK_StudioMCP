# Release Readiness

Review date: 2026-10-27. This is a release-preparation checklist, not a new game/render acceptance
or a published binary release. Source checks and runtime checks have different scopes.

## Candidate Boundary

The documentation update is based on mainline `d63428b` plus the new README, public agent guide,
portable shader-authoring skill and publication exclusions. Uncommitted runtime changes are not
part of this documentation candidate and are not qualified by older tests. Freeze an explicit
runtime commit before creating a distributable package.

**Recommendation:** ready to begin a scoped KKS preview's packaging work, not yet a completed stable
binary release. This update publishes source documentation only; it does not deploy new game DLLs.

## Checks To Record

| Check | Status |
| --- | --- |
| English README, agent entry and self-contained skill route all three shader RenderDocs | Passed skill/schema and local-link checks |
| Local evidence, connections, game references and build output excluded from publication | Reviewed explicit documentation-only staging list and ignore rules; reconfirm for each release |
| Isolated committed-source Release build: .NET host, KKS bridge, KK bridge | All passed, zero warnings/errors |
| Offline MCP initialize and tool discovery without a game connection | Passed; 37 committed-source tools, no live connection descriptor read |
| Separate KKS/KK bridge output directories retain the correct bridge plus its two dependencies | Passed with explicit per-edition output paths; default shared output is not a packaging recipe |
| NuGet direct/transitive advisory query for host and bridge packages | No listed advisories returned; not a complete security or fork-code review |
| Fresh live rendering/persistence on this exact candidate | Not run in this documentation review |
| Binary package / checksums / download-and-reload acceptance | Not prepared by this documentation update |

## Remaining Delivery Work

1. **Freeze feature scope and versions.** Decide which pending extensions ship or remain excluded.
   Keep SDK package version, host version, bridge plugin version and release tag distinct and
   traceable. The bridge declares plugin version 0.1.0 while its current assembly version is
   1.0.0.0; define/document that version policy before release. Do not build from a dirty source
   tree or from whatever DLL happens to be installed locally.
2. **Produce explicit packages.** Publish the complete host output (DLL, runtimeconfig, deps and
   dependencies), and separate KKS/KK game-bridge folders with their two third-party dependencies.
   Do not redistribute proprietary game assemblies, connection descriptors or a whole mod stack.
   Do not install the .NET 8 host DLL into BepInEx. Include install/uninstall/upgrade instructions,
   required runtime/Framework dependencies, licenses/notices, exact source revision and checksums.
   The default bridge output directory is shared across editions; a subsequent build can clean
   the prior edition's DLL. Use separate outputs or stage/verify each edition before the next build.
   Isolated per-edition outputs were checked in this review, not assembled into a release archive.
3. **Close the runtime matrix.** Existing verification covers the earlier common KK/KKS workflow;
   newer KKS scene-authoring, tonemap, sky, far-clip and camera-slot checks have their recorded
   scopes. Compilation alone does not qualify those features on KK. Declare a KKS preview scope
   or run the missing KK cases before promising equivalent support. Test missing optional providers.
4. **Run packaged regression.** With explicit authority, install the candidate in a disposable
   Studio. Check default read-only/authentication, stale-session and same-name material scope,
   texture/ST/Float/Color persistence, environment/sky/tonemap/slots, native PNG cold reload,
   capture cleanup and capability fallbacks. Keep raw evidence private; publish only summaries.
   Do not reuse an old rendered result as acceptance of a changed runtime binary.
5. **Make verification portable and repeatable.** The extended live verifier still assumes the
   maintainer's default game-folder layout. Parameterize game/server/output paths, document its
   Python/Pillow prerequisites, and keep live mutation tests opt-in. Add offline protocol/negative
   regressions and a reproducible packaging recipe without checking test output into Git.
6. **Review public distribution.** Confirm dependency/license notices and security/support policy,
   and decide how recipients obtain the currently access-controlled repository. Do not make a
   private repository public implicitly. A documentation push is not a binary or stable release.

## Evidence And Limits

See [verification history](verification.md) for earlier scoped checks and [capabilities](capabilities.md)
for supported behavior. Those summaries do not embed private scenes or raw responses. This review
does not launch or modify Studio, replace plugin DLLs, change repository visibility or upload local
evidence. Matching builds and offline schemas cannot establish game appearance, save/import behavior,
long-running stability, resource exhaustion behavior or compatibility with arbitrary graphics mods.
