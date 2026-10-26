# Rainline weather authoring extensions

## Purpose

Develop one KKS environment into four independently saved scenes: sunny day, rainy day, clear
moonlit night and rainy night. Keep the original Studio rendering path; do not add PPE, a second
postprocessing stack, screen-space filter items or UI automation. These additions extend the
existing repository directly. They are qualified on KKS only, not Unity 5.6.

## New interfaces

| Tool | Purpose and persistence |
| --- | --- |
| `native_effects_get` | Reports whitelisted native SceneInfo settings and actual camera component type, assembly and enabled state. |
| `native_effects_set` | Changes native AO, bloom, fog, DOF, vignette, sun shafts, AmplifyColor blend and character-light intensity. Saves through Studio's original scene format. |
| `native_sky_get` / `native_sky_set` | Lists and selects the original KKS sky catalog. Restores the native Funly sky controller when leaving a custom sky. |
| `skybox_bind` | Uses a catalog item's existing `Skybox/` material as the Unity skybox. Stores the object, unique renderer name, slot, exposure and rotation in PNG ExtendedSave data. |
| `material_texture_transform` | Writes UV offset and scale through ME's public texture-property APIs. Suitable for meter-scale detail normals without changing the base map. |
| `set_visible` | Uses native tree visibility; rain can be disabled independently of the street geometry. |

`scene_snapshot` now includes the native tree visibility flag. `render_diagnostics` includes up to
48 particle systems with playback, count, active state, renderer enablement and camera-layer checks.
`visibleToCamera` means active renderer plus matching layer; it does not test the frustum or occlusion.
The native light's instantaneous enabled state may differ from its saved Studio enable flag during
camera/capture lifecycle callbacks. The diagnostic intentionally reports the actual component.

## Implementation and safety

- `NativeEffectsCommands` validates all requested values before writing SceneInfo, then refreshes
  the specific original Studio UI models. It does not attach any new image-effect component.
- The character light is part of Studio's native camera-light system. Its saved intensity is applied
  using `cameraLightCtrl.Reflect()`, so it is not an untracked extra directional light.
- Custom sky binding accepts only inspected item materials with the native `Skybox/` prefix and
  exposure/rotation properties. Exposure is bounded to 0-8 and rotation to 0-360 degrees.
- While bound, the original sky driver is disabled so it cannot overwrite the selected material.
  Switching to a native preset or clearing the scene restores it. Existing scenes are not globally
  converted to custom skies.
- A full scene load remaps the saved item ID and finds its unique renderer name. An ephemeral Unity
  instance ID is never saved. Scene import does not replace the destination environment or sky.
- Rain geometry uses ordinary Unity ParticleSystems and the built-in alpha-blended particle shader.
  The sky carrier is a disabled renderer below the scene, not a filter object.
- All writes retain loopback authentication, explicit write enablement, scene-session guards and
  capture busy checks. No arbitrary C# execution or unrestricted component-editing endpoint was added.
- No connection token, game assembly or decompiled reference code belongs in the source delivery.

## Acceptance method

The Rainline task retains tool arguments and readbacks, four independent native PNG reload audits,
actual KKS color/depth renders and native DOF/IBL comparisons. Reload tests use fresh PNG filenames
with no `.bridge.json` sidecar. They compare native effects, camera, environment, material properties,
object transforms and tree visibility, then wait for the two reflection probes and SH volume.

Particle simulation bounds are excluded from deterministic comparisons. When the custom sky is
inactive, its unused exposure/rotation are also excluded: the native sky is the rendered sky and the
carrier correctly returns to its asset defaults on load. Active custom sky values remain audited.

## Rendering boundaries

The four-weather scenes use native AO, modest bloom and distance/height fog. Color-effect blend is
zero; vignette and sun shafts are disabled. Wide renders keep DOF disabled to preserve environmental
detail; a separate native DOF comparison validates the control path. No photographic filtering is
applied to exported renders.

Native depth contains opaque surfaces, not separate interfaces for glass or alpha-blended rain.
Normal-mapped droplets do not create geometric depth. Environment reflections use real local
cubemaps and SH captures, not SSR, planar reflection, ray tracing or simulated wet-surface fluid.
After editing lights, materials or the sky, explicitly refresh the probe group.

The installed game's unrelated corrupt zipmod and legacy shader warnings are not repaired by this
extension. Repeated JobTempAlloc warnings require separate investigation; scene acceptance is not
a clean-install or long-duration performance certification.
