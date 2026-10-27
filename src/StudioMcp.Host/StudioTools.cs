using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace StudioMcp.Host;

[McpServerToolType]
public static class StudioTools
{
    [McpServerTool, Description("Inspect the Studio connection, scene session, installed providers and write permissions.")]
    public static Task<JsonObject> StudioStatus(BridgeClient bridge) => bridge.Call("status");

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

    [McpServerTool, Description("List renderer handles, material slots and shaders for a Studio object. Handles are valid only for the current scene.")]
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

    [McpServerTool, Description("Set a material's render queue through Material Editor's saved API (0-5000), or -1 to restore the shader default. Unity draws only queues <= 2500 into the camera depth texture, and only with the shader's ShadowCaster pass. ME applies it to all same-name materials in the object.")]
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

    [McpServerTool, Description("Set the Studio camera's native AmplifyColor tonemapper (Disabled, Photographic, FilmicHable, FilmicACES), exposure (0.05-16) and linear white point (0.5-64). Saved in the scene PNG; loading a scene without saved values restores the camera defaults. No extra postprocessing stack.")]
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

    [McpServerTool, Description("Apply 1-32 operations synchronously. Each is {method,args}; allowlist: me.set Float/Color, object.set_transform, camera.set, render.set. Prevalidates and rolls back on failure; returns a batchId for one-level undo.")]
    public static Task<JsonObject> BatchApply(BridgeClient bridge, string sceneSession, System.Text.Json.JsonElement operations)
        => bridge.Call("batch.apply", Args(new { sceneSession, operations }));

    [McpServerTool, Description("Undo the most recent batch in the same scene, before any other bridge writes. Reports restoration failures explicitly.")]
    public static Task<JsonObject> BatchUndo(BridgeClient bridge, string sceneSession, string batchId)
        => bridge.Call("batch.undo", Args(new { sceneSession, batchId }));

    [McpServerTool, Description("Render through Screencap, return an image preview plus the full PNG path. Waits for actual capture completion; does not capture the desktop.")]
    public static async Task<CallToolResult> Capture(BridgeClient bridge, string sceneSession,
        int width = 640, int height = 360, int supersampling = 1, bool withDepth = false)
    {
        var started = await bridge.Call("capture.start", Args(new { sceneSession, width, height, supersampling, withDepth }));
        var jobId = started["result"]!["jobId"]!.GetValue<string>();
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
