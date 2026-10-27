# RC1 Maintainer Acceptance Checklist

Use a disposable Studio scene and the downloaded RC archives, not a development bin directory.
This checklist grants no permission to overwrite user scenes, restart unrelated processes or alter
rendering-provider DLLs. Keep raw protocol responses, images and connection data private.

## Package And Startup

- Verify SHA256SUMS and each package's source revision. Install the host plus ONE matching game
  bridge. Confirm no duplicate plugin GUID or wrong-edition DLL. Keep all host dependencies.
- With writes disabled, run studio_status and scene_snapshot. Confirm correct game/session,
  provider availability and captureOptions; reject a mutation. Confirm a bad token is rejected
  in an owned test client without printing credentials. Do not expose the listener to the LAN.
- Enable writes explicitly for the test. Make a new checkpoint before each destructive phase.

## Material, Scene And Capture

- Load a known installed test prop; inspect exact material names/slots and matchingMaterials.
  Change Float/Color, texture and ST through ME. Read back, save a new native PNG, load another
  disposable card, reload independently and reacquire handles. Check all values and maps.
- Reject a stale session and an invalid batch without modifying the scene. Apply/undo one valid
  bounded batch before other writes. Texture imports and item creation are not batch undo targets.
- Check native camera and camera slots, light state, saved environment/sky/tonemap and visibility
  within the edition's declared support. Cold reload the native PNG without relying on a sidecar.
- Capture a small normal image, then supported blackout and rgAlpha images. Inspect actual color
  and alpha against the installed Screencap's behavior. None must preserve ordinary captures.
- Reject unsupported alpha names and non-None withDepth before a capture begins. If paired depth
  is installed, use None, inspect output dimensions/metadata and ordered foreground/background depth.
- Exercise an owned failure case and confirm captureBusy clears and pre/post cleanup restores
  state. Never induce GPU out-of-memory or terminate the game just to force an error.

## Optional Controls Without Backend Changes

- With an absent/incompatible DOF provider, dof_compatibility_get must report unavailable; ordinary
  tools remain usable. Do not install or remove the user's provider solely to satisfy this check.
- With the existing compatible KKS provider, read state, change ONE scene policy, read back, save
  and reload a new PNG. Restore the original value. Provider owns persistence; this is not physical
  transparent DOF. Test globalMode only with explicit approval, record and restore its prior value.
- Request dof_buffer_snapshot with no completed color buffer: expect explicit unavailable, not a
  new service, lease or render. With an already active compatible buffer, inspect the exported LDR
  PNG/sequence. This is not a tone-mapped screenshot or proof of depth/DOF correctness.
- The same snapshot must not release provider buffers or change quality/settings. During capture,
  readback is blocked. Readback limits reject excessive/unresolved buffers rather than reallocating.
- On KK, DOF and BSDF snapshot controls remain unavailable; validate only the common supported path.

## Record The Result

Record exact archive/source hashes, game/API/ME/Screencap versions, accepted tools and remaining
failures. Public release notes may summarize results but must not include connection descriptors,
raw logs, proprietary binaries or user cards. Offline fixtures and 40-tool discovery do not replace
this live acceptance. Until it passes, keep this release explicitly pre-release.

The optional extended live verifier needs Python and Pillow, and explicit paths:

```powershell
python tools/verify_extended.py --game KKS --game-root '<game>' --card '<owned test card>' --server '<candidate host DLL>' --output .local/evidence/rc1-kks
```

It mutates a disposable empty scene and runs its own defined subset, not every case above. Read the
script first, obtain current authority, and never run it against a populated user scene.
