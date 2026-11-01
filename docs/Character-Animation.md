# Character posing and Timeline

Version 0.2.0 RC1 extends the original 40 tools with 20 character/animation tools. Update both the
host and game bridge and refresh the client's tool list. The 0.1.0 RC1 packages do not contain them.
The new tools passed live checks on KKS with KKPE 2.21.4 and Timeline 1.5.4; KK is build-verified only.

## Providers and saved data

- Character import still uses `add_character`. Native FK/IK tools need no pose plugin.
- Fine bone and blendshape editing requires installed KKPE/KKSPE with its original pose controller.
- Animation editing requires installed Timeline. KKPE tracks additionally require KKPE's Timeline models.
- KKPE and Timeline are optional dependencies. Their absence does not disable scene, ME, camera,
  capture or native FK/IK tools. Reads report availability; unsupported writes return an explicit error.

The bridge calls the original controllers and persistence methods. It does not add an IK solver,
animation scheduler, ongoing transform override or new bone/animation save format. Tracks use
the existing `Timeline` and `KKPE` model owners. Normal scene checkpoints invoke the normal save hooks.
Existing scenes, cards, tracks and unrelated providers' data retain their original formats.

All writes use the existing write-permission, scene-session and capture-busy guards. Bone/renderer/
control/track handles must be reacquired after reloading or rebuilding a character. Posing and
animation edits are outside `batch_apply`/`batch_undo`; use a new checkpoint for recovery.
Timeline restores its tracks after the native scene loader. During that interval its read tools
report unavailable and writes reject stale bindings; inspect again instead of replaying writes.

## Native FK and IK

1. Call `character_state(objectId)` to read mode enable flags and Studio's actual switchable groups.
2. Call `character_kinematics_set` with `mode="FK"` or `"IK"`, `enabled`, and optionally `group`.
3. Call `character_controls` with that mode to obtain current control IDs and supported channels.
4. Call `character_control_set` for an inspected control.

Without a group, mode changes use `OCIChar.ActiveKinematicMode`; enabling a mode follows native
Studio's mutual exclusion. With a group, the bridge uses `ActiveFK`/`ActiveIK` and updates that
group's saved flag. A configured group and a currently active group are reported separately.

FK accepts rotation. IK accepts position and/or rotation as indicated by `canPosition` and
`canRotation`. Enable the mode and group before editing a control. FK bone membership can contain
several flags; its `groups` list is distinct from the native UI's switchable group list. In particular,
arm/leg bones can belong to the native FK `Body` group. Do not guess group names from bone names.

The default `space="local"` refers to the saved GuideObject coordinate frame. Rotations use Euler
degrees. `space="world"` converts through the native control's parent; world-position writes use
the original `GuideObject.SetWorldPos` method. Values are saved through native `changeAmount` data.

## KKPE fine bones and blendshapes

`pose_bones` lists current bone-local transforms, hierarchy paths and whether KKPE owns an override.
It excludes attached objects that have another pose controller. Use `query`, `offset` and `limit`
to inspect a small subset. `boneId` is a runtime handle, not a saved bone index.
The main transform fields show the actual current pose; `overrides` shows KKPE's stored channel
values and `originals` shows the values recorded when those channels were first edited. A later
solver or another posing plugin can move a joint after KKPE applies it.
These tools retain that distinction and do not force a second continuous override to defeat it.

`pose_bone_set` accepts local position, Euler rotation and scale. It uses KKPE's original dirty-state
setters and correction pass. As in KKPE's own editor, rotation of an active FK bone uses its native
FK channel; position and scale remain KKPE edits. `pose_bone_reset` removes selected KKPE channels
using recorded originals, without resetting children or unrelated native FK rotation.

`pose_blendshapes` lists real renderer IDs, shape names, weights and override state. Use
`pose_blendshape_set` with a unique `shapeName`; the adapter uses KKPE's name-based setter rather
than guessing from an old mesh index. Negative and overdrive weights are supported. Resetting a
shape uses KKPE's original recorded weight and retains unrelated shape overrides. The adapter applies
the provider's changes before returning; it does not write Unity-only weights that disappear on save.
The response distinguishes the actual `weight`, KKPE's `overrideWeight` and its recorded
`originalWeight`. A blinking/animated shape can change between inspection and the first edit;
its reset target is the provider's recorded original, not an earlier inspection snapshot.

Direct set/reset follows KKPE's current **Link eyes components** switch, just like the KKPE UI and
its native Timeline track. Reads and mutation responses expose this as `linkEyesComponents`; the
MCP operation does not change the switch. When enabled, KKPE resolves related eye meshes using
its own link keys and original-index mapping. Reset restores each linked shape's recorded original
and clears its override; it does not assign the source shape's original weight to every mesh.
When the switch is disabled, or the selected shape has no link, only that named shape changes.

Meshes can be replaced during coordinate/clothing/character changes. Inspect again after such
changes. Ambiguous hierarchy paths are reported and cannot be used for saved edits.

## Timeline tracks and keyframes

Start with `timeline_get` and pause with `timeline_control(action="pause")` before editing. The
existing Timeline instance owns playback, interpolation and saving. `timeline_settings` uses its
native project handlers for duration, frame rate and speed. Speed changes Unity's global time
scale, as Timeline's own speed control does. Shrinking duration does not delete keys outside it.

`timeline_tracks` reads existing tracks, including other providers' tracks. `trackId` is valid for
the current scene. `timeline_track_create` accepts these explicit bindings:

| kind | Required inspected target | Native model |
| --- | --- | --- |
| `object_position`, `object_rotation`, `object_scale` | `objectId`; supported native GuideObject channel | `Timeline`: `guideObjectPos`, `guideObjectRot`, `guideObjectScale` |
| `fk_rotation` | `objectId`, FK `controlId` | `Timeline`: `guideObjectRot` |
| `ik_position`, `ik_rotation` | `objectId`, IK `controlId` with the requested channel | `Timeline`: `guideObjectPos`, `guideObjectRot` |
| `bone_position`, `bone_rotation`, `bone_scale` | `objectId`, KKPE `boneId` | `KKPE`: `bonePos`, `boneRot`, `boneScale` |
| `blendshape` | `objectId`, `rendererId`, `shapeName` | `KKPE`: `lastBlendShape` |

An active FK bone requested as `bone_rotation` binds its native FK track, matching the pose setter.
The blendshape adapter resolves KKPE's saved index/name mapping before binding; it does not assume
that the current Unity mesh index is the provider's saved index. Native KKPE eye-link settings still
apply during playback. Inspect the returned owner, model and saved binding.

Creating an existing binding returns `created=false` and retains its name and keys. The adapter uses
Timeline's original track loader to register new tracks in both the collection and UI tree; it does
not create an MCP-specific model that other installations cannot read.

- `timeline_keyframe_set`: supply `time` in seconds and either an explicit value or omit `value`
  to record the current target. Existing times require `replace=true`.
- Position/scale values are vec3s; weights are scalars. Rotation accepts Euler degrees `[x,y,z]`
  or a normalized quaternion `[x,y,z,w]`. Reads return native quaternion values with Euler summaries
  on the track, allowing the client to distinguish the two representations.
- `curve` accepts `linear`, `smooth` or `step` and controls the interval from that key to the next. Omitting
  it retains an existing curve on replacement; a new key defaults to linear. Existing custom curves
  are preserved. Keyframe reads report at most 64 curve points and flag truncation without modifying
  the stored curve.
- `timeline_keyframe_remove` deletes an exact returned time through Timeline's deletion method;
  an empty track is retained. `timeline_track_set` changes enabled/name, and `timeline_track_remove`
  removes a track using its original removal path.
- `timeline_control` supports play, pause, stop, seek and previous/next frame. Seeking the same time
  after changing keys also evaluates the native interpolation passes, since native Seek otherwise
  skips unchanged times. KKPE's correction passes then complete the sampled pose.

This version edits only the native object/FK/IK and KKPE bone/blendshape models listed above.
Other providers' tracks remain readable and unchanged. Key editing is separate from changing a
live pose: a live edit does not silently create animation keys.

Use a checkpoint, reload it, reacquire handles and seek several times to verify a saved animation.
Rendering a single pose does not by itself verify a complete motion or scene reload.

## Verification

Build the host and the bridge for the target game. The integration script requires a new, empty,
disposable Studio with writes enabled, KKPE and Timeline. It refuses a nonempty scene, loads a copy
of the supplied card, saves new checkpoints, verifies interpolation and reload, and captures a preview:

```powershell
python tools/verify_animation.py --game-root '<game>' --card '<owned test card.png>' `
  --server src/StudioMcp.Host/bin/Release/net8.0/StudioMcp.Host.dll `
  --output .local/evidence/animation-run
```

Keep its evidence local. Compilation and tool discovery alone are not runtime qualification for a
different game edition or provider version.
