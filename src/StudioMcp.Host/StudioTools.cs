using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace StudioMcp.Host;

[McpServerToolType]
public static class StudioTools
{
    [McpServerTool, Description("Inspect the Studio connection, scene session, installed providers and write permissions. mode is studio or maker; in maker mode only the maker_*, material_*, camera_get/set, capture, batch_*, render_get and render_diagnostics tools work.")]
    public static Task<JsonObject> StudioStatus(BridgeClient bridge) => bridge.Call("status");

    [McpServerTool, Description("Maker mode: read the maker character's name, coordinate, clothes/hair/accessory slots (list IDs, names, clothing states) and camera, plus the sceneSession needed for writes. Material tools take objectId 0 in the maker.")]
    public static Task<JsonObject> MakerSnapshot(BridgeClient bridge) => bridge.Call("maker.snapshot");

    [McpServerTool, Description("Read a character's body (44 sliders) or face shape values with index, English key and in-game name. Maker sliders show value*100. objectId is the Studio character, or 0 in the maker.")]
    public static Task<JsonObject> ShapeGet(BridgeClient bridge, int objectId = 0, string part = "body")
        => bridge.Call("chara.shape_get", Args(new { objectId, part }));

    [McpServerTool, Description("Set body or face shape sliders like the maker does, e.g. values {\"bust_size\": 0.9, \"waist_position\": 0.4} (keys: index, English key or in-game name; 0..1 is the normal slider range, -10..10 allowed). Saved in the card/scene. Usable in batch_apply as chara.shape_set for one-step undo.")]
    public static Task<JsonObject> ShapeSet(BridgeClient bridge, string sceneSession, System.Text.Json.JsonElement values,
        int objectId = 0, string part = "body")
        => bridge.Call("chara.shape_set", Args(new { sceneSession, objectId, part, values }));

    [McpServerTool, Description("Export a character's current meshes (pose, body shape and blend shapes baked) to OBJ+MTL with _MainTex PNGs under UserData/StudioMCP/exports, for checking clothing fit/clipping in Blender. scopes: Character, Clothing, Hair, Accessory (default all). Hidden or taken-off parts are skipped unless includeInactive; shadow/highlight/silhouette helper meshes unless includeHelpers. objectId is the Studio character, or 0 in the maker.")]
    public static Task<JsonObject> MeshExport(BridgeClient bridge, int objectId = 0, string[]? scopes = null,
        bool includeInactive = false, bool textures = true, string? name = null, bool includeHelpers = false)
        => bridge.Call("mesh.export", Args(new { objectId, scopes, includeInactive, textures, name, includeHelpers }));

    [McpServerTool, Description("List a character's accessories: slot, list ID/name, parent bone, both move corrections (position in maker units, rotation in degrees, scale), colors, hair flag and Hair Accessory Customizer values, plus the valid parent keys. objectId is the Studio character, or 0 in the maker.")]
    public static Task<JsonObject> AccessoryList(BridgeClient bridge, int objectId = 0, int? slot = null)
        => bridge.Call("chara.accessory_list", Args(new { objectId, slot }));

    [McpServerTool, Description("Adjust one accessory like the maker's accessory panel, for spliced hair: position/rotation/scale of move correction 0 or 1 (absolute, or relative with add=true), parent bone key, colors (1-4 RGBA), and hair {colorMatch, hairGloss, accessoryColor, outlineColor, glossColor, length} via Hair Accessory Customizer. Saved with the card. Usable in batch_apply as chara.accessory_set for undo.")]
    public static Task<JsonObject> AccessorySet(BridgeClient bridge, string sceneSession, int slot, int objectId = 0, int correction = 0,
        float[]? position = null, float[]? rotation = null, float[]? scale = null, bool add = false, string? parent = null,
        System.Text.Json.JsonElement? colors = null, System.Text.Json.JsonElement? hair = null)
        => bridge.Call("chara.accessory_set", Args(new { sceneSession, objectId, slot, correction, position, rotation, scale, add, parent, colors, hair }));

    [McpServerTool, Description("Check spliced hair against the head mesh: scalp coverage % (top/back/left/right; forehead reported separately), uncovered gap clusters with the nearest hair piece and a suggested accessory_set move (add=true), and per-piece seams (closest gap, % inside the head, max depth, floating/sunk verdict with a suggested move). slots adds non-hair accessories used as hair. Returns markers for capture_turntable (showUncovered: one marker per uncovered scalp sample).")]
    public static Task<JsonObject> HairCheck(BridgeClient bridge, int objectId = 0, int[]? slots = null, bool showUncovered = false)
        => bridge.Call("analysis.hair", Args(new { objectId, slots, showUncovered }));

    [McpServerTool, Description("Sweep body sliders and count where the body pokes through the worn clothing, e.g. sliders {\"bust_size\":[0,1],\"waist_position\":[0,1]} (default: bust, waist, thighs, butt at 0 and 1; max 16 values). Reports poke-through points, max depth, the clothing pieces involved and hotspot positions per step, accessorySlots adds accessory-based garments. Then restores the sliders.")]
    public static Task<JsonObject> ClippingSweep(BridgeClient bridge, string sceneSession, int objectId = 0,
        System.Text.Json.JsonElement? sliders = null, float toleranceMm = 1.5f, int[]? accessorySlots = null)
        => bridge.Call("analysis.clipping_sweep", Args(new { sceneSession, objectId, sliders, toleranceMm, accessorySlots }));

    [McpServerTool, Description("Read the character's eye, mouth and eyebrow patterns, openness and blinking; in the maker also the pattern names.")]
    public static Task<JsonObject> ExpressionGet(BridgeClient bridge, int objectId = 0)
        => bridge.Call("chara.expression_get", Args(new { objectId }));

    [McpServerTool, Description("Set eye/mouth/eyebrow pattern indices, eyesOpen/mouthOpen (0..1) and blink, e.g. to check hair and bangs over different expressions. Runtime only.")]
    public static Task<JsonObject> ExpressionSet(BridgeClient bridge, string sceneSession, int objectId = 0, int? eyes = null, int? mouth = null,
        int? eyebrow = null, float? eyesOpen = null, float? mouthOpen = null, bool? blink = null)
        => bridge.Call("chara.expression_set", Args(new { sceneSession, objectId, eyes, mouth, eyebrow, eyesOpen, mouthOpen, blink }));

    [McpServerTool, Description("Maker mode: list the maker's preview poses.")]
    public static Task<JsonObject> PoseList(BridgeClient bridge) => bridge.Call("maker.pose_list");

    [McpServerTool, Description("Maker mode: switch the maker preview pose by index from pose_list (-1 = default pose), optionally at a normalized animation time (0..1), to check hair and clothes in motion.")]
    public static Task<JsonObject> PoseSet(BridgeClient bridge, string sceneSession, int index, float time = 0)
        => bridge.Call("maker.pose_set", Args(new { sceneSession, index, time }));

    [McpServerTool, Description("List the DynamicBone physics on accessories (slot, bone index, root bone, damping, elasticity, stiffness, inertia, radius, weight).")]
    public static Task<JsonObject> DynamicBonesGet(BridgeClient bridge, int objectId = 0, int? slot = null)
        => bridge.Call("chara.dynamic_bones", Args(new { objectId, slot }));

    [McpServerTool, Description("Set an accessory DynamicBone's damping/elasticity/stiffness/inertia/weight (0..1) or radius through DynamicBone Editor, which saves them to the card.")]
    public static Task<JsonObject> DynamicBoneSet(BridgeClient bridge, string sceneSession, int slot, int objectId = 0, int index = 0,
        float? damping = null, float? elasticity = null, float? stiffness = null, float? inertia = null, float? radius = null, float? weight = null)
        => bridge.Call("chara.dynamic_bone_set", Args(new { sceneSession, objectId, slot, index, damping, elasticity, stiffness, inertia, radius, weight }));

    [McpServerTool, Description("Read ABMX body bone modifiers (scale, position, rotation, length); filter by bone name, allNames lists modifiable bones. Head/neck scaling often explains misaligned hair.")]
    public static Task<JsonObject> BonesGet(BridgeClient bridge, int objectId = 0, string? filter = null, bool allNames = false)
        => bridge.Call("chara.bones_get", Args(new { objectId, filter, allNames }));

    [McpServerTool, Description("Set an ABMX body bone modifier (scale/position/rotation as xyz, length); saved with the card. Usable in batch_apply as chara.bones_set for undo.")]
    public static Task<JsonObject> BonesSet(BridgeClient bridge, string sceneSession, string bone, int objectId = 0,
        float[]? scale = null, float[]? position = null, float[]? rotation = null, float? length = null)
        => bridge.Call("chara.bones_set", Args(new { sceneSession, objectId, bone, scale, position, rotation, length }));

    [McpServerTool, Description("Capture several views around a character in one call and tile them into one contact sheet (default 8 yaws, 0 = front). target full or head; markers are world points drawn as red dots visible through hair (e.g. hair_check markers). The camera is restored afterwards.")]
    public static async Task<CallToolResult> CaptureTurntable(BridgeClient bridge, string sceneSession, int objectId = 0,
        float[]? views = null, float pitch = 0, string target = "full", int tileWidth = 360, int tileHeight = 480, int columns = 4,
        float fov = 23, float? distance = null, System.Text.Json.JsonElement? markers = null, float markerSize = 0.012f)
    {
        var started = await bridge.Call("capture.turntable", Args(new { sceneSession, objectId, views, pitch, target, tileWidth, tileHeight,
            columns, fov, distance, markers, markerSize }));
        return await WaitForCapture(bridge, started["result"]!["jobId"]!.GetValue<string>());
    }

    [McpServerTool, Description("Maker mode: set a clothing slot's runtime state (0 on, 1 half, 2 half 2, 3 off) to inspect half-off variants. kind is a slot 0-8 or top/bottom/bra/shorts/gloves/pantyhose/socks/shoes_inner/shoes_outer. Not saved to the card.")]
    public static Task<JsonObject> MakerSetClothesState(BridgeClient bridge, string sceneSession, string kind, int state)
        => bridge.Call("maker.clothes_state", Args(new { sceneSession, kind = int.TryParse(kind, out var slot) ? (object)slot : kind, state }));

    [McpServerTool, Description("Search installed Studio items by display name, returning authoritative runtime catalog IDs and native light IDs.")]
    public static Task<JsonObject> CatalogSearch(BridgeClient bridge, string query, int limit = 50)
        => bridge.Call("catalog.search", Args(new { query, limit }));

    [McpServerTool, Description("Create a saved native Studio light using an ID from catalog_search. Does not replace existing lights.")]
    public static Task<JsonObject> AddLight(BridgeClient bridge, string sceneSession, int lightId, string name,
        float[]? position = null, float[]? rotation = null)
        => bridge.Call("scene.add_light", Args(new { sceneSession, lightId, name, position, rotation }));

    [McpServerTool, Description("Set a Studio light's saved color, intensity, range, cone, enabled and shadow flags using its native setters.")]
    public static Task<JsonObject> LightSet(BridgeClient bridge, string sceneSession, int objectId,
        float[]? color = null, float? intensity = null, float? range = null, float? spotAngle = null,
        bool? enabled = null, bool? shadows = null, int? cullingMask = null, int? renderMode = null)
        => bridge.Call("light.set", Args(new { sceneSession, objectId, color, intensity, range, spotAngle, enabled, shadows, cullingMask, renderMode }));

    [McpServerTool, Description("Change to an installed ME-registered shader through ME's saved API. Inspect fresh material handles afterward. Checkpoint before changing shaders.")]
    public static Task<JsonObject> MaterialSetShader(BridgeClient bridge, string sceneSession, int objectId,
        int rendererId, string shader, int slot = 0)
        => bridge.Call("me.shader", Args(new { sceneSession, objectId, rendererId, shader, slot }));

    [McpServerTool, Description("Read Studio-owned objects, transforms, bounds and the current camera. Paginated; not a dump of hidden Unity objects.")]
    public static Task<JsonObject> SceneSnapshot(BridgeClient bridge, int offset = 0, int limit = 50)
        => bridge.Call("scene.snapshot", new JsonObject { ["offset"] = offset, ["limit"] = limit });

    [McpServerTool, Description("Create a Studio catalog item in the current inspected scene. Position and scale use Studio units.")]
    public static Task<JsonObject> AddItem(BridgeClient bridge, string sceneSession, int group, int category,
        int itemId, string name, float[]? position = null, float[]? scale = null)
        => bridge.Call("scene.add_item", Args(new { sceneSession, group, category, itemId, name, position, scale }));

    [McpServerTool, Description("Move, rotate or scale a Studio object through its saved changeAmount data, not a temporary Unity-only transform.")]
    public static Task<JsonObject> SetTransform(BridgeClient bridge, string sceneSession, int objectId,
        float[]? position = null, float[]? rotation = null, float[]? scale = null)
        => bridge.Call("object.set_transform", Args(new { sceneSession, objectId, position, rotation, scale }));

    [McpServerTool, Description("Read the camera orbit pivot, rotation, field of view and actual eye position.")]
    public static Task<JsonObject> CameraGet(BridgeClient bridge) => bridge.Call("camera.get");

    [McpServerTool, Description("Set the Studio camera orbit pivot, Euler rotation and field of view. Pivot is not the eye position.")]
    public static Task<JsonObject> CameraSet(BridgeClient bridge, string sceneSession,
        float[]? pivot = null, float[]? rotation = null, float? fov = null, float[]? distance = null)
        => bridge.Call("camera.set", Args(new { sceneSession, pivot, rotation, fov, distance }));

    [McpServerTool, Description("Read the scene's saved Studio camera slots (the native camera save/load buttons): pivot, rotation, distance and field of view per slot.")]
    public static Task<JsonObject> CameraSlotsGet(BridgeClient bridge) => bridge.Call("camera.slots");

    [McpServerTool, Description("Save the current camera into a native Studio camera slot, exactly like Studio's camera save button. Slots are stored in the scene PNG when the scene is saved.")]
    public static Task<JsonObject> CameraSlotSave(BridgeClient bridge, string sceneSession, int slot)
        => bridge.Call("camera.slot_save", Args(new { sceneSession, slot }));

    [McpServerTool, Description("Move the camera to a saved native Studio camera slot, exactly like Studio's camera load button.")]
    public static Task<JsonObject> CameraSlotLoad(BridgeClient bridge, string sceneSession, int slot)
        => bridge.Call("camera.slot_load", Args(new { sceneSession, slot }));

    [McpServerTool, Description("List renderer handles, material slots and shaders for a Studio object. Handles are valid only for the current scene. In the maker, use objectId 0 for the maker character.")]
    public static Task<JsonObject> MaterialsList(BridgeClient bridge, int objectId)
        => bridge.Call("me.list", Args(new { objectId }));

    [McpServerTool, Description("Inspect the selected material's shader properties and current values before editing.")]
    public static Task<JsonObject> MaterialInspect(BridgeClient bridge, int objectId, int rendererId, int slot = 0)
        => bridge.Call("me.inspect", Args(new { objectId, rendererId, slot }));

    [McpServerTool, Description("Set a texture's UV scale/offset through ME persistence, independently for each texture. Values are bounded finite vec2s.")]
    public static Task<JsonObject> MaterialTextureTransform(BridgeClient bridge, string sceneSession, int objectId,
        int rendererId, string property, float[]? scale = null, float[]? offset = null, int slot = 0)
        => bridge.Call("me.texture_transform", Args(new { sceneSession, objectId, rendererId, property, scale, offset, slot }));

    [McpServerTool, Description("Set a color (RGBA array) or float through Material Editor's persistence API. ME applies to all same-name materials in the object.")]
    public static Task<JsonObject> MaterialSet(BridgeClient bridge, string sceneSession, int objectId,
        int rendererId, string property, string kind, System.Text.Json.JsonElement value, int slot = 0)
        => bridge.Call("me.set", Args(new { sceneSession, objectId, rendererId, property, kind, value, slot }));

    [McpServerTool, Description("Set a material's render queue through Material Editor's saved API (0-5000), or -1 to remove ME's override and restore the recorded original queue. Unity draws only queues <= 2500 into the camera depth texture, and only with the shader's ShadowCaster pass. ME applies it to all same-name materials in the object.")]
    public static Task<JsonObject> MaterialSetRenderQueue(BridgeClient bridge, string sceneSession, int objectId,
        int rendererId, int renderQueue, int slot = 0)
        => bridge.Call("me.render_queue", Args(new { sceneSession, objectId, rendererId, renderQueue, slot }));

    [McpServerTool, Description("Load a character card without writing to the original card. Materials are classified as Character, Hair, Clothing or Accessory with a part slot.")]
    public static Task<JsonObject> AddCharacter(BridgeClient bridge, string sceneSession, string path,
        string sex = "female", float[]? position = null, string? name = null)
        => bridge.Call("scene.add_character", Args(new { sceneSession, path, sex, position, name }));

    [McpServerTool, Description("Import a local PNG/JPEG through ME so texture bytes persist in the scene/card. Checkpoint first if you need to restore an earlier imported texture.")]
    public static Task<JsonObject> MaterialImportTexture(BridgeClient bridge, string sceneSession, int objectId,
        int rendererId, string property, string path, int slot = 0)
        => bridge.Call("me.import_texture", Args(new { sceneSession, objectId, rendererId, property, path, slot, kind = "texture" }));

    [McpServerTool, Description("Remove an ME Float/Color/texture override through its original reset API. This restores the provider's original value.")]
    public static Task<JsonObject> MaterialReset(BridgeClient bridge, string sceneSession, int objectId,
        int rendererId, string property, string kind, int slot = 0)
        => bridge.Call("me.reset", Args(new { sceneSession, objectId, rendererId, property, kind, slot }));

    [McpServerTool, Description("Read supported native Unity ambient, fog and quality settings. These are runtime settings; graphics mods may override them.")]
    public static Task<JsonObject> RenderGet(BridgeClient bridge) => bridge.Call("render.get");

    [McpServerTool, Description("Read native Studio postprocessing values and actual camera components. No PPE or external postprocessing stack is added.")]
    public static Task<JsonObject> NativeEffectsGet(BridgeClient bridge) => bridge.Call("native_effects.get");

    [McpServerTool, Description("Set whitelisted native Studio effects using native_effects_get values. Writes native SceneInfo and refreshes the existing effect controllers; saves in scene PNGs.")]
    public static Task<JsonObject> NativeEffectsSet(BridgeClient bridge, string sceneSession, System.Text.Json.JsonElement values)
        => bridge.Call("native_effects.set", Args(new { sceneSession, values }));

    [McpServerTool, Description("Read the optional shared KKS DOF provider, camera policy, render counters and supported setters. Missing, incompatible or not-ready providers return available=false; no plugin is installed or enabled.")]
    public static Task<JsonObject> DofCompatibilityGet(BridgeClient bridge) => bridge.Call("dof_compat.get");

    [McpServerTool, Description("Export the last completed main-camera BSDF opaque color buffer to a diagnostic LDR PNG, without tone mapping or requesting a new buffer/render. Missing resources return available=false. Readback is blocked during capture and bounded to 16777216 pixels. Not a viewport screenshot or DOF/depth acceptance.")]
    public static Task<JsonObject> DofBufferSnapshot(BridgeClient bridge) => bridge.Call("dof_compat.snapshot");

    [McpServerTool, Description("Set the installed optional KKS DOF provider's scenePolicy (Inherit, Native, BeforeTransparents) or globalMode (Native, BeforeTransparents). Provider owns persistence; native DOF parameters are unchanged. Early mode keeps ALL transparents sharp. Inputs are prevalidated, but provider exceptions can leave partial changes: inspect state before retrying.")]
    public static Task<JsonObject> DofCompatibilitySet(BridgeClient bridge, string sceneSession,
        string? scenePolicy = null, string? globalMode = null)
        => bridge.Call("dof_compat.set", Args(new { sceneSession, scenePolicy, globalMode }));

    [McpServerTool, Description("List original KKS sky presets and read the active native sky. Does not add a sky filter item.")]
    public static Task<JsonObject> NativeSkyGet(BridgeClient bridge) => bridge.Call("native_sky.get");

    [McpServerTool, Description("Select an original KKS sky preset by ID from native_sky_get. Saved through native Studio SceneInfo.")]
    public static Task<JsonObject> NativeSkySet(BridgeClient bridge, string sceneSession, int pattern, bool enabled = true)
        => bridge.Call("native_sky.set", Args(new { sceneSession, pattern, enabled }));

    [McpServerTool, Description("Bind an inspected Studio item's native Skybox material to RenderSettings. Disables the built-in sky driver while bound, saves the item reference in the scene PNG, restores the native driver on native_sky_set or scene clear. No postprocessing/filter item.")]
    public static Task<JsonObject> SkyboxBind(BridgeClient bridge, string sceneSession, int objectId, int rendererId,
        float exposure = 1, float rotation = 0, int slot = 0)
        => bridge.Call("skybox.bind", Args(new { sceneSession, objectId, rendererId, slot, exposure, rotation }));

    [McpServerTool, Description("Set a Studio object's native saved visibility, including its children. Useful for separately authored weather particle objects.")]
    public static Task<JsonObject> SetVisible(BridgeClient bridge, string sceneSession, int objectId, bool visible)
        => bridge.Call("object.set_visible", Args(new { sceneSession, objectId, visible }));

    [McpServerTool, Description("Read the Studio camera's native AmplifyColor tonemapper, exposure, linear white point and LUT blend.")]
    public static Task<JsonObject> TonemapGet(BridgeClient bridge) => bridge.Call("tonemap.get");

    [McpServerTool, Description("Set the Studio camera's native AmplifyColor tonemapper (Disabled, Photographic, FilmicHable, FilmicACES), exposure (0.05-16) and linear white point (0.5-64). Saved in the scene PNG. Scene loads reset only bridge-owned edits to their pre-edit values. No extra postprocessing stack.")]
    public static Task<JsonObject> TonemapSet(BridgeClient bridge, string sceneSession, System.Text.Json.JsonElement values)
        => bridge.Call("tonemap.set", Args(new { sceneSession, values }));

    [McpServerTool, Description("Read bounded actual camera, light and reflection-probe resources, including color space and probe texture availability. Read only.")]
    public static Task<JsonObject> RenderDiagnostics(BridgeClient bridge) => bridge.Call("render.diagnostics");

    [McpServerTool, Description("Patch native render settings using keys returned by render_get. Checkpoints also store these in a sidecar. Does not edit arbitrary graphics-mod internals.")]
    public static Task<JsonObject> RenderSet(BridgeClient bridge, string sceneSession, System.Text.Json.JsonElement values)
        => bridge.Call("render.set", Args(new { sceneSession, values }));

    [McpServerTool, Description("Save a new Studio scene PNG under UserData/StudioMCP using normal save hooks, plus a native-render-settings sidecar. Never overwrites an existing scene/card.")]
    public static Task<JsonObject> SceneCheckpoint(BridgeClient bridge, string sceneSession)
        => bridge.Call("scene.checkpoint", Args(new { sceneSession }));

    [McpServerTool, Description("Replace the current scene with one of this bridge's checkpoint PNGs. Unsaved edits are lost. Re-read the scene and obtain fresh renderer handles after restoring.")]
    public static Task<JsonObject> SceneRestore(BridgeClient bridge, string sceneSession, string path)
        => bridge.Call("scene.restore", Args(new { sceneSession, path }));

    [McpServerTool, Description("Apply 1-32 operations synchronously. Each is {method,args}; allowlist: me.set Float/Color, chara.shape_set, chara.accessory_set, chara.bones_set, object.set_transform, camera.set, render.set. Prevalidates and rolls back on failure; returns a batchId for one-level undo.")]
    public static Task<JsonObject> BatchApply(BridgeClient bridge, string sceneSession, System.Text.Json.JsonElement operations)
        => bridge.Call("batch.apply", Args(new { sceneSession, operations }));

    [McpServerTool, Description("Undo the most recent batch in the same scene, before any other bridge writes. Reports restoration failures explicitly.")]
    public static Task<JsonObject> BatchUndo(BridgeClient bridge, string sceneSession, string batchId)
        => bridge.Call("batch.undo", Args(new { sceneSession, batchId }));

    [McpServerTool, Description("Render through installed Screencap and return a PNG preview plus the full path. alphaMode is None (default), blackout (native cutout) or rgAlpha (native gradual alpha); inspect studio_status.captureOptions for support. withDepth requires None. Waits for completion; never captures the desktop.")]
    public static async Task<CallToolResult> Capture(BridgeClient bridge, string sceneSession,
        int width = 640, int height = 360, int supersampling = 1, bool withDepth = false, string alphaMode = "None")
    {
        var started = await bridge.Call("capture.start", Args(new { sceneSession, width, height, supersampling, withDepth, alphaMode }));
        return await WaitForCapture(bridge, started["result"]!["jobId"]!.GetValue<string>());
    }

    private static async Task<CallToolResult> WaitForCapture(BridgeClient bridge, string jobId)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(150);
            var response = await bridge.Call("capture.status", new JsonObject { ["jobId"] = jobId });
            var job = response["result"]!.AsObject();
            var state = job["state"]!.GetValue<string>();
            if (state == "failed") throw new InvalidOperationException(job["error"]?.GetValue<string>());
            if (state != "complete") continue;
            var preview = job["previewBase64"]!.GetValue<string>();
            job.Remove("previewBase64");
            return new CallToolResult { Content = [
                new TextContentBlock { Text = job.ToJsonString() },
                ImageContentBlock.FromBytes(Convert.FromBase64String(preview), "image/png")
            ] };
        }
        throw new TimeoutException("Capture is still running. Do not restart the game or repeat the capture blindly.");
    }

    private static JsonObject Args(object value) => System.Text.Json.JsonSerializer.SerializeToNode(
        value, new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })!.AsObject();
}
