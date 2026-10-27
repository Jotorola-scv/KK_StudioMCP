# Find And Read Shader RenderDocs

Reviewed 2026-10-27. RenderDocs means rendering documentation, not the RenderDoc GPU debugger or
a capture archive. Do not require private case evidence or conversation history to interpret it.

## Authoritative Projects

| Project | Identity and scope | Current RenderDocs entry |
| --- | --- | --- |
| TomX | `tom.Shaders.X`; toon-first `tom/*X*` surfaces, skin, hair, eyes, liquid and separate Tess entries | [TomshadersX RenderDocs](https://github.com/tomTom1010-IEE/TomshadersX/tree/main/Documents/RenderDocs) |
| TomBSDF | `tom.Shaders.BSDF`; modern `tom/BSDF*`, its rectangle control and retained legacy `xukmi/TomBSDF*` | [TomBSDF RenderDocs](https://github.com/tomTom1010-IEE/TomBSDF/tree/main/Assets/Mods/TomShadersBSDF/Documents/RenderDocs) |
| V+ LTS | `xukmi.Shaders.VanillaPlus`; legacy-compatible Plus families and retained Blend/height/overlay experiments | [KKShadersPlus RenderDocs](https://github.com/tomTom1010-IEE/KKShadersPlus/tree/main/Documents/RenderDocs) |

These links are navigation, not permission to assume the newest source matches an installed bundle.
Reviewed documentation snapshots are TomX `47cd3450d0ed94a6b2b9196dc5dcdaa672bf08bf` (0.4.0),
TomBSDF `3265e6b301f5aa705af87084f465d09c8b8d26eb` (0.2.0 RC1), and V+
`5e94147cfc9fcac5717fa75bac467358782324df` (KKS 1.7.6). Select the installed release/tag where
available; otherwise disclose the source/installed-version uncertainty. A shader name alone cannot
authenticate a built Bundle. The source catalogs are static inventories, not native-program audits.

## Local Or Remote Resolution

1. Ask for or inspect the user's chosen source/asset root, without guessing a maintainer's drive.
   In a Unity project, look under `Assets/Mods/<package>/Documents/RenderDocs/README.md`.
2. A TomX or V+ package checkout has `Documents/RenderDocs/README.md` at its root. TomBSDF's published
   source overlay has `Assets/Mods/TomShadersBSDF/Documents/RenderDocs/README.md` at repository root;
   a standalone module still has its own `Documents/RenderDocs`.
3. If no matching local copy exists, open the corresponding GitHub entry above and read its files
   at the appropriate tag/commit. Raw GitHub file URLs use
   `https://raw.githubusercontent.com/<owner>/<repo>/<ref>/<path>` with those same paths. An agent
   needs file/web access for the documents; StudioMCP does not fetch them or inject them into context.
4. If that access is unavailable, request the relevant RenderDocs files or source package. Do not
   invent semantics from MCP ranges. Do not clone a complete Unity project just to answer one
   property question if the matching documentation/source can be read directly.

Modern TomX and TomBSDF are qualified for KKS first. V+ has a separately maintained KK edition;
verify its actual game tag and source version rather than treating the KKS snapshot as KK proof.
For an unknown/custom shader, use its own source and author documentation instead of force-fitting
it into one of these families.

## Reading Order

1. **README.md**: select the exact public entry and its intended scope, release and limitations.
2. **SharedRendering.md**: input/ME transport calibration, light terms, transparency/depth and the
   common diagnostic method. It does not define universal material defaults.
3. **Pipeline.md**: Base/Add/Reflect/caster/depth ownership, physical or stylized composition,
   services/resources and the missing-plugin fallback. Read the relevant sections, not every plan.
4. **MaterialContracts.md**: channels, encoding, color/data routes, units, masks, normals, height,
   layer and alpha semantics. This is the critical step before choosing parameter values.
5. **ShaderCatalog.json**: resolve the exact name, source/carrier path, current registered properties
   and categories, literal Pass/UsePass names and source fingerprints. Schema 2 normalizes text to
   UTF-8/LF without BOM; it does not hash delivered binaries or expand every preprocessor path.
6. **manifest.xml and tooltip**: confirm current editable IDs/types/ranges and family-specific help.
   Manifest IDs and MCP wire names may differ by the leading underscore; use inspected wire names.
7. **Implementation/source contract**: follow the catalog paths and include/macro branches when a
   result depends on source details. Proposals and old validation summaries are not current code.

The optional project-local `Audit-RenderDocs.ps1` is read-only by default. It needs the corresponding
source/carriers, not just the document folder. Never refresh its catalog solely to hide drift or
treat a successful audit as compilation, correct installed resources, appearance or GPU performance.

## Family Guardrails

- TomX MaterialMap RGBA is metallic/roughness/AO/specular mask. Modern BSDF ORM RGB is
  AO/roughness/metallic; its active ORM branch can replace separate-map inputs. V+ has no universal
  packed material contract. Similar property labels do not establish map compatibility.
- The main/detail normal slots may be converted by ME. A source PNG's color is not its sampled
  layout. Read the actual slot's decoder, restore/flip options and current transport before tuning.
- V+ ShadowColor alpha, AnotherRamp and Reflect/MatCap composition vary by family. Check the exact
  shader/include; do not infer reversed intensity or mask ownership across every Plus variant.
- Modern BSDF has no toon ramp/MatCap layer; its legacy xukmi entries are distinct. MainOpaqueBlend
  stays in V+ and does not gain modern BSDF contracts merely because its name includes blending.
- Separate coverage fade, binary cutoff and optical glass. POM is shading-only; Tess is actual
  geometry with bounds/seam costs. Neither intensity sliders nor Queue/ZWrite fix absent Probe,
  SH, caster or transparent-depth data. Test intended DOF through the actual supported depth path.

Read only relevant family sections and cite the selected file/section in a material explanation.
Keep shader semantics in the owning RenderDocs, not a copied and gradually diverging MCP manual.
