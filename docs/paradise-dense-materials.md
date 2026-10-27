# Procedural Paradise: Dense Material Operations

The KKS greenhouse port contains over 1,000 renderers sharing a leaf material.
Material Editor can legitimately take longer than the host's original
35-second timeout to change that material's shader. The game operation can
complete after the client has stopped waiting. Blindly retrying a timed-out
write is therefore unsafe.

## Addition

`STUDIOMCP_REQUEST_TIMEOUT_SECONDS` configures the host response wait. It must
be an integer from 5 through 180; the default remains 35 seconds. The showcase
client uses 120 seconds. Invalid values fail with an explanatory error.

This does not change the game's queue admission deadline, cancel an operation
already executing on Unity's main thread, or introduce parallel Unity access.
After any timeout, inspect the current state before deciding whether to retry.

## Scope and Verification

- Modified `src/StudioMcp.Host/BridgeClient.cs` and documented the environment
  variable in the repository README.
- Built the Release host successfully with no warnings or errors.
- Read back the state after the original leaf-material timeout, skipped the
  shader change that had already completed, and completed the remaining ME
  binding operations with the extended bounded wait.
- No game bridge DLL, Material Editor source, shader core, scene save format,
  or external plugin was changed for this host-only feature.
- All interaction used MCP, CLI, Editor APIs and exported native render files;
  no desktop capture or computer-use automation was used.

The showcase's existing light/sky/effect persistence depends on the earlier
Rainline authoring extensions. Those are not new features in this change.
