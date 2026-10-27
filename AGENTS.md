# StudioMCP Agent Entry

- Read [the agent guide](docs/Agent-Guide.md) before scene/material operations. This is the public
  project entry, not a record of one maintainer's installation or permission to run old test scripts.
- For shader work, read [the authoring skill](skills/studiomcp-shader-authoring/SKILL.md) and its
  [RenderDocs route](skills/studiomcp-shader-authoring/references/shader-renderdocs.md). Resolve the
  installed shader family/version before interpreting any map, value or lighting result.
- TomX, modern TomBSDF and V+ LTS have independent contracts. Do not infer them from similarly
  named parameters, the original texture's appearance, an old plan or a previous conversation.
- Read current MCP schemas and `studio_status`/`scene_snapshot` first. Respect read-only mode,
  current scene sessions, ephemeral handles, same-name ME scope and capture/write guards. Inspect
  state after uncertain writes rather than retrying blindly. Preserve the user's current scene.
- Use MCP/game APIs and exported captures, not desktop/input automation. A missing operation is
  a capability boundary; a typed extension requires a current request, not an archived example.
- Use Unity Editor APIs through the matching Codex Bridge for native authoring assets. Never
  hand-serialize Unity materials, prefabs, scenes, assets or metadata.
- Read [release readiness](docs/Release-Readiness.md) before release work. Keep committed candidates
  separate from unfinished runtime edits. Do not promote old KK tests to qualification of newer
  KKS-only features. Offline build/tool discovery is not game rendering or persistence acceptance.
- Keep connection descriptors/tokens, game assemblies, raw responses, images, logs, backups and
  build/cache output out of Git. Public documentation may summarize a check without bundling its
  machine-local evidence. Preserve unrelated work; stage only reviewed task files.
