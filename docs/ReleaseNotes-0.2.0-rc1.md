# StudioMCP 0.2.0 RC1

- Direct blendshape set/reset now follows KKPE's **Link eyes components** switch, matching the UI and Timeline.
- Linked resets restore each shape's own recorded original. Saved-index mapping is shared with Timeline bindings.
- Queries and edit responses expose `linkEyesComponents`. Tool signatures and saved formats are unchanged; 60 tools total.

Update the host and ONE matching game bridge. KKPE and Timeline remain optional and are not bundled.
KKS live checks passed for four linked meshes, negative/overdrive weights, scene save/reload and Timeline interpolation.
Host, KK and KKS builds and 13 targeted adapter checks passed. KK runtime behavior remains unqualified.

The earlier Character Posing and Timeline release is now numbered [0.2.0 RC0](ReleaseNotes-0.2.0-rc0.md).
