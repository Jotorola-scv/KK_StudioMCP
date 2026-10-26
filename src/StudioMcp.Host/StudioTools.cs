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
        float[]? pivot = null, float[]? rotation = null, float? fov = null)
        => bridge.Call("camera.set", Args(new { sceneSession, pivot, rotation, fov }));

    [McpServerTool, Description("List renderer handles, material slots and shaders for a Studio object. Handles are valid only for the current scene.")]
    public static Task<JsonObject> MaterialsList(BridgeClient bridge, int objectId)
        => bridge.Call("me.list", Args(new { objectId }));

    [McpServerTool, Description("Inspect the selected material's shader properties and current values before editing.")]
    public static Task<JsonObject> MaterialInspect(BridgeClient bridge, int objectId, int rendererId, int slot = 0)
        => bridge.Call("me.inspect", Args(new { objectId, rendererId, slot }));

    [McpServerTool, Description("Set a color (RGBA array) or float through Material Editor's persistence API. ME applies to all same-name materials in the object.")]
    public static Task<JsonObject> MaterialSet(BridgeClient bridge, string sceneSession, int objectId,
        int rendererId, string property, string kind, System.Text.Json.JsonElement value, int slot = 0)
        => bridge.Call("me.set", Args(new { sceneSession, objectId, rendererId, property, kind, value, slot }));

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
        int width = 640, int height = 360, int supersampling = 1)
    {
        var started = await bridge.Call("capture.start", Args(new { sceneSession, width, height, supersampling }));
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
