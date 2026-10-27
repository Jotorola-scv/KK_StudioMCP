# Agent Guide

This guide is for a new agent with no prior conversation. StudioMCP is a local game-control bridge,
not a rendering engine and not an authority on every installed shader's mathematics.

## Select The Task

- For first-time installation, use the README's build/install instructions and, if useful, the
  separate [scene-pipeline setup package](https://github.com/tomTom1010-IEE/TomBSDF/releases/tag/scene-pipeline-setup-20261027).
  That package is optional; this guide and the authoring skill are usable without installing it.
- For scene/material editing, read the current tool schemas and the workflow below.
- For shader meaning, route through [Shader RenderDocs](../skills/studiomcp-shader-authoring/references/shader-renderdocs.md).
- For source or binary publication, use [Release Readiness](Release-Readiness.md). Do not run live
  verification scripts as part of a read-only documentation or source audit.

## Establish Actual State

Use `studio_status` and `scene_snapshot` to establish the game edition, session, provider availability
and scene scope. Do not print or copy the generated connection descriptor. Explicit user authority
and server write permission are separate requirements for mutations.

Use `materials_list` and `material_inspect` for the relevant object/renderer/slot. Record the exact
shader identity, actual wire property names, active map modes and `matchingMaterials`. ME can affect
several same-named materials in the same object/part scope; a renderer handle does not guarantee
slot-local isolation. Fetch fresh handles after shader switches, scene reloads or renderer rebuilds.

A tool schema's accepted type/range and a material's current value do not explain the BRDF, mask
channels, color conversion, layer ownership, units or depth behavior. KK introspection can come
from ME metadata rather than Unity shader reflection. Do not treat it as source verification.

## Read Shader Knowledge Before Tuning

The [routing guide](../skills/studiomcp-shader-authoring/references/shader-renderdocs.md) gives public
repository links, local/source-overlay layouts, reviewed documentation revisions and the reading
order for TomX, TomBSDF and V+ LTS. Prefer a matching local source/release; otherwise read the
matching GitHub files. Do not silently apply the newest main-branch settings to an older bundle.

Read the selected RenderDocs README, then only the relevant Pipeline/MaterialContracts sections,
the exact ShaderCatalog entry and the corresponding manifest/tooltip. Open implementation sources
when the answer depends on a branch or Pass not settled by prose. Distinguish implemented behavior
from old proposals, feature goals and historical validation summaries.

Typical mistakes this prevents:

- TomX's RGBA MaterialMap is not TomBSDF's RGB ORM map. Enabling ORM can bypass separate BSDF maps.
- A blue source normal can be repacked by ME. Select the decoder by the sampled route, not the PNG.
- V+ ShadowColor alpha and separate Reflect paths have family-specific semantics; do not import
  assumptions from modern TomX/BSDF or even another Plus family.
- Coverage Alpha is not reflection-preserving glass. Queue/ZWrite changes do not create missing
  camera-depth or transparent interfaces. POM changes shading, not the mesh silhouette or depth.
- Probe/SH, native direct light, MatCap and emission are different terms. AO or reflection gain
  cannot create scene-lighting data that is absent.

## Make A Bounded Comparison

1. Checkpoint only with authority, preserving an owned recovery path and the current user scene.
2. Fix camera, native rendering and lighting; identify provider resources with diagnostics when
   relevant. Avoid compensating for an unknown normal/color contract by changing exposure.
3. Change a small related set through ME-backed tools, retaining imported texture ST and correct
   object/part scope. Read values back; request a small actual Screencap capture and inspect it.
4. Compare one hypothesis at a time. A good still does not prove motion stability, energy
   conservation, correct depth or GPU performance. Test those separately when requested.
5. For persistence, save a new native scene PNG, reload independently, reacquire session/handles
   and verify maps/ST, values, environment/light state and required probe completion.

Use existing accepted shader packages for geometry/ME-only work. Do not launch a full shader build,
Force Rebuild, install extra post stacks or change global render settings merely to make a material
look plausible. Native asset work belongs in the matching Unity Editor/Bridge, not serialized text.

## Recovery And Handoff

After a timeout, inspect live state before replaying a write; extending the host wait does not
cancel a game operation already executing. Do not restore a scene or terminate an application
without current authority. Batch undo is bounded and does not cover arbitrary external state.

Record the shader/package version, documentation revision, evidence scope and resulting owned
card/capture locations. Keep actual paths, raw captures and private responses in the user's local
handoff, not Git. Report unsupported features and untested KK/KKS cases explicitly. Connection to
StudioMCP does not by itself install or load this skill or the external shader documentation.
