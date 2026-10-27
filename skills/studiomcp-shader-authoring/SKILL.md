---
name: studiomcp-shader-authoring
description: Author or diagnose TomX, TomBSDF and V+ LTS materials and scene lighting through StudioMCP by reading the matching shader RenderDocs, inspecting live ME state and validating bounded render and persistence changes. Use for Studio shader/material work, not general MCP installation or unrelated shader development.
---

# StudioMCP Shader Authoring

Use the installed game's real material state and the selected shader's documented contract.
StudioMCP metadata describes writable values; it does not substitute for shader rendering knowledge.
No earlier conversation, sibling checkout or separately installed skill is required.

## Find And Read The Shader Contract

Read [Shader RenderDocs](references/shader-renderdocs.md) to resolve TomX, TomBSDF and V+ LTS.
It includes local/source-overlay locations and public GitHub entries. Use the exact shader identity
and package version, not only a prefix or a familiar parameter label. Legacy xukmi/TomBSDF entries
belong to TomBSDF; MainOpaqueBlend remains V+ and is not the modern BSDF implementation.

Load the chosen RenderDocs README, then relevant Pipeline and MaterialContracts sections. Resolve
the exact ShaderCatalog entry and current manifest/tooltip. Inspect implementation branches when
needed. If only newer documentation is available, state that mismatch before inferring behavior.
Do not copy the three projects' packed-map, normal, height, transparency or ShadowColor semantics.

## Inspect, Compare, Preserve

1. Read current tool schemas, `studio_status` and `scene_snapshot`; establish edition/session,
   providers and current task authority. Do not read or print connection secrets for diagnosis.
2. Inspect the target with `materials_list`/`material_inspect`. Use actual wire property names,
   active map branches and `matchingMaterials`; handles are ephemeral and ME scope can span slots.
3. Confirm input channels, import/ME conversion, units and lighting resource availability against
   RenderDocs before altering settings. Separate direct light, SH/Probe, MatCap and emission.
4. Preserve the user scene; checkpoint only when authorized. Make a bounded ME-backed change,
   read it back, capture a small actual game image and inspect the relevant result.
5. For requested persistence/depth acceptance, independently reload a native scene PNG and inspect
   values/maps/ST and provider state; export supported depth with metadata. An RGB still is not
   proof of correct depth, motion stability, energy or frame time.

After timeouts or scene/renderer changes, inspect state and reacquire handles before writing again.
No blind replay, arbitrary reflection execution or computer-use fallback is part of this workflow.
Do not rebuild shaders, deploy plugins, change Queue/ZWrite, replace a scene or introduce extra post
effects without a reason grounded in the selected contract and the current request.

Use the referenced repositories' optional RenderDocs audit for source/catalog freshness, not proof
of the installed Bundle or rendering. Retain private evidence locally; never publish connection
descriptors, tokens, game binaries, raw responses or user scenes. Report the documentation revision,
installed-version uncertainty, measured scope and remaining limitations in the handoff.
