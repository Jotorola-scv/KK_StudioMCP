using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace StudioMcp.Host;

[McpServerToolType]
public static class StudioTools
{
    [McpServerTool, Description("Start the game this server belongs to, and wait until its bridge answers: the main game (at its logo screen; then use maker_enter) on a maker server, CharaStudio on a Studio server. If it is already running from this folder it is left alone (launched=false). Refuses if the same game runs from another folder, because they share a port. Loads and saves nothing.")]
    public static Task<JsonObject> GameLaunch(BridgeClient bridge, float timeoutSeconds = 180) => GameLauncher.Launch(bridge, timeoutSeconds);

    [McpServerTool, Description("What StudioMCP has written under UserData/StudioMCP, per kind (captures, exports, samples, masks, checkpoints, backups) with counts, bytes and how much is from this session; items=true lists each one, newest first. Works with the game closed. The user's own files in that folder are not outputs and never listed.")]
    public static JsonObject OutputsList(string[]? kinds = null, bool items = false, int limit = 100) => OutputCleaner.List(kinds, items, limit);

    [McpServerTool, Description("Delete StudioMCP's own outputs to free disk space; call it on your own judgement during a task, as the server instructions describe. kinds selects whole kinds, paths selects single outputs (a capture file, or the folder of an export, sample job or mask preview, as listed by outputs_list; other paths are refused). scope session (default) only touches what was written since this server started; all includes older work, possibly another agent's. keepPaths protects what you will cite, keepLatest keeps the newest N per kind, olderThanHours spares recent ones, dryRun only reports. Backups always keep the newest one. Never touches cards, the user's own images or textures. Refused while a capture or job runs and on a read-only install. Cannot be undone.")]
    public static Task<JsonObject> OutputsCleanup(BridgeClient bridge, string[]? kinds = null, string[]? paths = null, string[]? keepPaths = null, string scope = "session",
        float? olderThanHours = null, int keepLatest = 0, bool dryRun = false)
        => OutputCleaner.Cleanup(bridge, kinds, paths, keepPaths, scope, olderThanHours, keepLatest, dryRun);

    [McpServerTool, Description("Inspect the connection: mode (studio or maker), maker state (not_in_maker, character_loading, ready), maker.gameState (starting, title, maker_loading, maker, other) and maker.canEnterMaker, running jobs, bridge and host versions/builds, schemaVersion, write permission and installed providers. This server only lists the tools that work in its mode.")]
    public static async Task<JsonObject> StudioStatus(BridgeClient bridge)
    {
        var status = await bridge.Call("status");
        status["result"]!["host"] = new JsonObject { ["version"] = ToolRegistry.Version, ["build"] = ToolRegistry.Build, ["mode"] = ToolRegistry.Mode,
            ["toolsExposed"] = ToolRegistry.Exposed, ["connectionFile"] = Path.GetFileName(Environment.GetEnvironmentVariable("STUDIOMCP_CONNECTION") ?? ""),
            ["outputs"] = OutputCleaner.Summary() };
        return status;
    }

    [MakerOnly]
    [McpServerTool, Description("Open the character maker from the main game's title menu, as clicking Character Maker and then Female or Male does, and wait until the character is ready. Works as soon as the game has started: it waits through the logos for the title. Start the game first with game_launch if needed. Returns the sceneSession, then use character_load for a card. If the maker is already open it returns at once (alreadyInMaker) without changing it. Refuses from any other scene (a playthrough, free H) rather than discard progress.")]
    public static async Task<JsonObject> MakerEnter(BridgeClient bridge, string sex = "female", float timeoutSeconds = 180)
    {
        var started = await bridge.Call("maker.enter", Args(new { sex, timeoutSeconds }));
        var jobId = started["result"]!["jobId"]!.GetValue<string>();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds + 15);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            JsonObject job;
            // Scene loading can stall the game's main thread past a request's lifetime; keep polling until the deadline.
            try { job = (await bridge.Call("maker.enter_status", new JsonObject { ["jobId"] = jobId }))["result"]!.AsObject(); }
            catch (ModelContextProtocol.McpException error) when (!error.Message.Contains("Unknown maker enter job")) { continue; }
            catch (Exception error) when (error is IOException or System.Net.WebSockets.WebSocketException) { continue; }
            var state = job["state"]!.GetValue<string>();
            if (state == "failed") throw new ModelContextProtocol.McpException(job["error"]?.GetValue<string>() ?? "The job failed.");
            if (state == "ready") return job;
        }
        throw new ModelContextProtocol.McpException("The maker is still opening; check studio_status maker.gameState before calling maker_enter again.");
    }

    [MakerOnly]
    [McpServerTool, Description("Maker mode: read the maker character's name, coordinate, clothes/hair/accessory slots (list IDs, names, clothing states) and camera, plus the sceneSession needed for writes. Material tools take objectId 0 in the maker.")]
    public static Task<JsonObject> MakerSnapshot(BridgeClient bridge) => bridge.Call("maker.snapshot");

    [McpServerTool, Description("Read a character's body (44 sliders) or face shape values with index, English key and in-game name. Maker sliders show value*100. objectId is the Studio character, or 0 in the maker.")]
    public static Task<JsonObject> ShapeGet(BridgeClient bridge, int objectId = 0, string part = "body")
        => bridge.Call("chara.shape_get", Args(new { objectId, part }));

    [McpServerTool, Description("Set body or face shape sliders like the maker does, e.g. values {\"bust_size\": 0.9, \"waist_position\": 0.4} (keys: index, English key or in-game name; 0..1 is the normal slider range, -10..10 allowed). Saved in the card/scene. Usable in batch_apply as chara.shape_set for one-step undo.")]
    public static Task<JsonObject> ShapeSet(BridgeClient bridge, string sceneSession,
        [JsonSchema("{\"type\":\"object\",\"description\":\"slider key (index, English key or in-game name) to value\",\"additionalProperties\":{\"type\":\"number\"}}")] System.Text.Json.JsonElement values,
        int objectId = 0, string part = "body")
        => bridge.Call("chara.shape_set", Args(new { sceneSession, objectId, part, values }));

    [McpServerTool, Description("Export a character's current meshes (pose, body shape and blend shapes baked) to OBJ+MTL with _MainTex PNGs under UserData/StudioMCP/exports, for checking clothing fit/clipping in Blender. scopes: Character, Clothing, Hair, Accessory (default all). Hidden or taken-off parts are skipped unless includeInactive; shadow/highlight/silhouette helper meshes unless includeHelpers. rendererIds/accessorySlots/hairSlots/clothes export just those parts, plus the body as a clipping reference unless includeBody=false. Positions keep full float precision, but OBJ welds nothing and is still posed geometry: for skin weights, bind poses and bone matrices use rig_export. objectId is the Studio character, or 0 in the maker.")]
    public static Task<JsonObject> MeshExport(BridgeClient bridge, int objectId = 0, string[]? scopes = null,
        bool includeInactive = false, bool textures = true, string? name = null, bool includeHelpers = false,
        int[]? rendererIds = null, int[]? accessorySlots = null, int[]? hairSlots = null, string[]? clothes = null, bool includeBody = true)
        => bridge.Call("mesh.export", Args(new { objectId, scopes, includeInactive, textures, name, includeHelpers,
            rendererIds, accessorySlots, hairSlots, clothes = Kinds(clothes), includeBody }));

    [McpServerTool, Description("List a character's accessories: slot, list ID/name, parent bone, both move corrections (position in maker units, rotation in degrees, scale), colors, hair flag and Hair Accessory Customizer values, plus the valid parent keys. objectId is the Studio character, or 0 in the maker.")]
    public static Task<JsonObject> AccessoryList(BridgeClient bridge, int objectId = 0, int? slot = null)
        => bridge.Call("chara.accessory_list", Args(new { objectId, slot }));

    [McpServerTool, Description("Adjust one accessory like the maker's accessory panel, for spliced hair: position/rotation/scale of move correction 0 or 1 (absolute, or relative with add=true), parent bone key, colors (1-4 RGBA), and hair {colorMatch, hairGloss, accessoryColor, outlineColor, glossColor, length} via Hair Accessory Customizer. Saved with the card. Usable in batch_apply as chara.accessory_set for undo.")]
    public static Task<JsonObject> AccessorySet(BridgeClient bridge, string sceneSession, int slot, int objectId = 0, int correction = 0,
        float[]? position = null, float[]? rotation = null, float[]? scale = null, bool add = false, string? parent = null,
        [JsonSchema("{\"type\":\"array\",\"description\":\"1-4 RGBA colors\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}}")] System.Text.Json.JsonElement? colors = null,
        [JsonSchema("{\"type\":\"object\"}")] System.Text.Json.JsonElement? hair = null)
        => bridge.Call("chara.accessory_set", Args(new { sceneSession, objectId, slot, correction, position, rotation, scale, add, parent, colors, hair }));

    [McpServerTool, Description("Check spliced hair against the head mesh: scalp coverage % (top/back/left/right; forehead reported separately), uncovered gap clusters with the nearest hair piece and a suggested accessory_set move (add=true), and per-piece seams (closest gap, % inside the head, max depth, floating/sunk verdict with a suggested move). slots adds non-hair accessories used as hair. Returns markers for capture_turntable (showUncovered: one marker per uncovered scalp sample).")]
    public static Task<JsonObject> HairCheck(BridgeClient bridge, int objectId = 0, int[]? slots = null, bool showUncovered = false)
        => bridge.Call("analysis.hair", Args(new { objectId, slots, showUncovered }));

    [McpServerTool, Description("Sweep body sliders and count where the body pokes through the worn clothing, e.g. sliders {\"bust_size\":[0,1],\"waist_position\":[0,1]} (default: bust, waist, thighs, butt at 0 and 1; max 16 values). Reports poke-through points, max depth, the clothing pieces involved and hotspot positions per step, accessorySlots adds accessory-based garments. Then restores the sliders.")]
    public static Task<JsonObject> ClippingSweep(BridgeClient bridge, string sceneSession, int objectId = 0,
        [JsonSchema("{\"type\":\"object\",\"description\":\"slider key to the values to test\",\"additionalProperties\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}}")] System.Text.Json.JsonElement? sliders = null, float toleranceMm = 1.5f, int[]? accessorySlots = null)
        => bridge.Call("analysis.clipping_sweep", Args(new { sceneSession, objectId, sliders, toleranceMm, accessorySlots }));

    [McpServerTool, Description("Read the character's eye, mouth and eyebrow patterns, openness and blinking; in the maker also the pattern names.")]
    public static Task<JsonObject> ExpressionGet(BridgeClient bridge, int objectId = 0)
        => bridge.Call("chara.expression_get", Args(new { objectId }));

    [McpServerTool, Description("Set eye/mouth/eyebrow pattern indices, eyesOpen/mouthOpen (0..1) and blink, e.g. to check hair and bangs over different expressions. Runtime only.")]
    public static Task<JsonObject> ExpressionSet(BridgeClient bridge, string sceneSession, int objectId = 0, int? eyes = null, int? mouth = null,
        int? eyebrow = null, float? eyesOpen = null, float? mouthOpen = null, bool? blink = null)
        => bridge.Call("chara.expression_set", Args(new { sceneSession, objectId, eyes, mouth, eyebrow, eyesOpen, mouthOpen, blink }));

    [MakerOnly]
    [McpServerTool, Description("Maker mode: list the maker's preview poses.")]
    public static Task<JsonObject> PoseList(BridgeClient bridge) => bridge.Call("maker.pose_list");

    [MakerOnly]
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

    [McpServerTool, Description("Read ABMX bone modifiers (scale, position, rotation, length) on the body, or on an accessory's own bones with slot; filter by bone name, allNames lists modifiable bones (for a slot: its transforms, root first). Head/neck scaling often explains misaligned hair.")]
    public static Task<JsonObject> BonesGet(BridgeClient bridge, int objectId = 0, string? filter = null, bool allNames = false, int? slot = null)
        => bridge.Call("chara.bones_get", Args(new { objectId, filter, allNames, slot }));

    [McpServerTool, Description("Set an ABMX bone modifier (scale/position/rotation as xyz, length) on the body, or on an accessory bone with slot, e.g. scale the last bones of a hair tail to shorten it instead of masking. Saved with the card. Usable in batch_apply as chara.bones_set for undo.")]
    public static Task<JsonObject> BonesSet(BridgeClient bridge, string sceneSession, string bone, int objectId = 0, int? slot = null,
        float[]? scale = null, float[]? position = null, float[]? rotation = null, float? length = null)
        => bridge.Call("chara.bones_set", Args(new { sceneSession, objectId, slot, bone, scale, position, rotation, length }));

    [McpServerTool, Description("Preview an alpha mask for one hair accessory (slot) without changing anything. mode: tail (default; follows each strand from its root and cuts from where it first goes into the body/clothes to the tip, ignoreNearRootCm, backoffCm), length (keep a fraction or keepCm of every strand), inside (only the buried part), below (world height) or region (center, radius). marginMm and accessorySlots/includeClothes define the body. Writes into the shader's _AlphaMask when it has one, else the _MainTex alpha (property to force). Reports per material the cut %, texture pixels shared with kept strands (protectShared keeps them), an alpha-clip hint, an overlay PNG (red cut, yellow shared), strand stats and 3D markers for capture_turntable. Returns a previewId.")]
    public static async Task<CallToolResult> HairMaskPreview(BridgeClient bridge, int slot, int objectId = 0, string mode = "tail",
        float? height = null, float[]? center = null, float? radius = null, float marginMm = 2, int[]? accessorySlots = null,
        bool includeClothes = true, float? ignoreNearRootCm = null, float? backoffCm = null, float? keep = null, float? keepCm = null,
        string property = "auto", int grow = 2, bool protectShared = true, int? rendererId = null)
    {
        var response = await bridge.Call("analysis.hair_mask", Args(new { objectId, slot, mode, height, center, radius, marginMm, accessorySlots,
            includeClothes, ignoreNearRootCm, backoffCm, keep, keepCm, property, grow, protectShared, rendererId }));
        var result = response["result"]!.AsObject();
        var content = new List<ContentBlock> { new TextContentBlock { Text = response.ToJsonString() } };
        var overlay = result["overlayPath"]?.GetValue<string>();
        if (overlay != null && File.Exists(overlay)) content.Add(ImageContentBlock.FromBytes(await File.ReadAllBytesAsync(overlay), "image/png"));
        return new CallToolResult { Content = content };
    }

    [McpServerTool, Description("Apply a hair_mask_preview: writes the mask into each material's _AlphaMask (or _MainTex alpha) and imports it through Material Editor, so it is saved with the card. Verify with capture_turntable; undo with hair_mask_revert.")]
    public static Task<JsonObject> HairMaskApply(BridgeClient bridge, string sceneSession, string previewId)
        => bridge.Call("mask.apply", Args(new { sceneSession, previewId }));

    [McpServerTool, Description("Undo an applied hair mask now: removes the Material Editor texture and restores the original texture immediately.")]
    public static Task<JsonObject> HairMaskRevert(BridgeClient bridge, string sceneSession, string previewId)
        => bridge.Call("mask.revert", Args(new { sceneSession, previewId }));

    [McpServerTool, Description("Capture several views around a character in one call and tile them into one contact sheet (default 8 yaws, 0 = front). target full or head; markers are world points drawn as red dots visible through hair (e.g. hair_check markers). pitch > 0 looks from above. Waits for the camera to stop, then at least settleFrames and settleSeconds (default 2 s), because the game's temporal effects ghost the previous view for well over a second. The camera is restored afterwards.")]
    public static async Task<CallToolResult> CaptureTurntable(BridgeClient bridge, string sceneSession, int objectId = 0,
        float[]? views = null, float pitch = 0, string target = "full", int tileWidth = 360, int tileHeight = 480, int columns = 4,
        float fov = 23, float? distance = null, [JsonSchema("{\"type\":\"array\",\"description\":\"world points [x,y,z]\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}}")] System.Text.Json.JsonElement? markers = null, float markerSize = 0.012f, int settleFrames = 10, float settleSeconds = 2)
    {
        var started = await bridge.Call("capture.turntable", Args(new { sceneSession, objectId, views, pitch, target, tileWidth, tileHeight,
            columns, fov, distance, markers, markerSize, settleFrames, settleSeconds }));
        return await WaitForCapture(bridge, started["result"]!["jobId"]!.GetValue<string>());
    }

    [MakerOnly]
    [McpServerTool, Description("Maker mode: set a clothing slot's runtime state (0 on, 1 half, 2 half 2, 3 off) to inspect half-off variants. kind is a slot 0-8 or top/bottom/bra/shorts/gloves/pantyhose/socks/shoes_inner/shoes_outer. Not saved to the card.")]
    public static Task<JsonObject> MakerSetClothesState(BridgeClient bridge, string sceneSession, string kind, int state)
        => bridge.Call("maker.clothes_state", Args(new { sceneSession, kind = int.TryParse(kind, out var slot) ? (object)slot : kind, state }));

    [MakerOnly]
    [McpServerTool, Description("Maker mode: save the current outfit as a coordinate card in UserData/coordinate, like the maker's save button (game thumbnail; plugin data such as Material Editor is included). name is the outfit name shown in the game (default: the current one); fileName is a plain file name without .png (default KKCoordeF_<timestamp>); an existing file needs overwrite=true. Returns the path, the plugin data stored in the card and the thumbnail.")]
    public static async Task<CallToolResult> CoordinateSave(BridgeClient bridge, string sceneSession, string? name = null, string? fileName = null, bool overwrite = false)
    {
        var started = await bridge.Call("maker.coordinate_save", Args(new { sceneSession, name, fileName, overwrite }));
        var jobId = started["result"]!["jobId"]!.GetValue<string>();
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(150);
            var job = (await bridge.Call("maker.coordinate_status", new JsonObject { ["jobId"] = jobId }))["result"]!.AsObject();
            var state = job["state"]!.GetValue<string>();
            if (state == "failed") throw new ModelContextProtocol.McpException(job["error"]?.GetValue<string>() ?? "The job failed.");
            if (state != "complete") continue;
            var thumbnail = job["thumbnailBase64"]?.GetValue<string>();
            job.Remove("thumbnailBase64");
            var content = new List<ContentBlock> { new TextContentBlock { Text = job.ToJsonString() } };
            if (thumbnail != null) content.Add(ImageContentBlock.FromBytes(Convert.FromBase64String(thumbnail), "image/png"));
            return new CallToolResult { Content = content };
        }
        throw new ModelContextProtocol.McpException("The coordinate save is still running. Check UserData/coordinate before saving again.");
    }

    [MakerOnly]
    [McpServerTool, Description("Maker mode: list coordinate cards in UserData/coordinate (subfolders included), newest first, with file and outfit names. query matches either name.")]
    public static Task<JsonObject> CoordinateList(BridgeClient bridge, string? query = null, int offset = 0, int limit = 50)
        => bridge.Call("maker.coordinate_list", Args(new { query, offset, limit }));

    [MakerOnly]
    [McpServerTool, Description("Maker mode: load a coordinate card into the current outfit like the maker's load button; plugin data such as Material Editor comes with it. path is from coordinate_list (absolute, or relative to UserData/coordinate). clothes/accessories pick what to load (both by default). Cannot be undone with batch_undo, so coordinate_save the current outfit first if it matters. Returns a new sceneSession; earlier renderer ids are stale.")]
    public static Task<JsonObject> CoordinateLoad(BridgeClient bridge, string sceneSession, string path, bool clothes = true, bool accessories = true)
        => bridge.Call("maker.coordinate_load", Args(new { sceneSession, path, clothes, accessories }));

    [MakerOnly]
    [McpServerTool, Description("Maker mode: list character cards in UserData/chara/female (or male; default the maker character's sex), subfolders included, newest first, with file and character names. query matches either name.")]
    public static Task<JsonObject> CharacterList(BridgeClient bridge, string? query = null, int offset = 0, int limit = 50, int? sex = null)
        => bridge.Call("maker.character_list", Args(new { query, offset, limit, sex }));

    [MakerOnly]
    [McpServerTool, Description("Maker mode: load a character card into the maker like its load button and wait until the character and its plugin data are ready. path is from character_list (absolute, or relative to the chara folder). face/body/hair/parameters/coordinates match the game's load toggles (all true by default); pluginToggles sets KKAPI plugin load toggles by part of their text, e.g. {\"Material Editor\": true, \"ABMX\": false} (the result lists all of them). Nothing is saved; the previous character is replaced in the maker, so coordinate_save first if it matters. Returns the new sceneSession.")]
    public static async Task<JsonObject> CharacterLoad(BridgeClient bridge, string sceneSession, string path, bool face = true, bool body = true,
        bool hair = true, bool parameters = true, bool coordinates = true,
        [JsonSchema("{\"type\":\"object\",\"additionalProperties\":{\"type\":\"boolean\"}}")] System.Text.Json.JsonElement? pluginToggles = null,
        float timeoutSeconds = 60)
    {
        var started = await bridge.Call("maker.character_load", Args(new { sceneSession, path, face, body, hair, parameters, coordinates, pluginToggles, timeoutSeconds }));
        var jobId = started["result"]!["jobId"]!.GetValue<string>();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds + 15);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            var job = (await bridge.Call("maker.character_load_status", new JsonObject { ["jobId"] = jobId }))["result"]!.AsObject();
            var state = job["state"]!.GetValue<string>();
            if (state == "failed") throw new ModelContextProtocol.McpException(job["error"]?.GetValue<string>() ?? "The job failed.");
            if (state == "ready") return job;
        }
        throw new ModelContextProtocol.McpException("The character is still loading; check studio_status maker.state before loading again.");
    }

    [McpServerTool, Description("Hide parts at runtime only, e.g. the hair over the back or all accessories, to inspect what is under them: rendererIds, scopes (Character/Clothing/Hair/Accessory), accessorySlots, hairSlots, clothes kinds, or allAccessories. Disables just the renderers (shadow casters included); accessory transforms, the card and Material Editor are untouched. Returns a token for visibility_restore; a character reload also clears it.")]
    public static Task<JsonObject> VisibilitySet(BridgeClient bridge, string sceneSession, int objectId = 0, int[]? rendererIds = null,
        string[]? scopes = null, int[]? accessorySlots = null, int[]? hairSlots = null, string[]? clothes = null, bool allAccessories = false)
        => bridge.Call("chara.visibility_set", Args(new { sceneSession, objectId, rendererIds, scopes, accessorySlots, hairSlots, clothes = Kinds(clothes), allAccessories }));

    [McpServerTool, Description("Show parts hidden by visibility_set again: one token, or every token when omitted.")]
    public static Task<JsonObject> VisibilityRestore(BridgeClient bridge, string sceneSession, string? token = null)
        => bridge.Call("chara.visibility_restore", Args(new { sceneSession, token }));

    [McpServerTool, Description("List active visibility_set and preview_set tokens.")]
    public static Task<JsonObject> VisibilityGet(BridgeClient bridge) => bridge.Call("chara.visibility_get");

    [McpServerTool, Description("Diagnostic look, runtime only: mode neutral swaps materials for a plain lit grey (color [r,g,b] to change it) so folds and spikes read in captures, unlit gives each renderer its own flat hue to tell overlapping garments apart, none keeps materials. postEffects=false turns off the camera's image effects (bloom, tonemapping, ...) against overexposure. Targets as in visibility_set, default the whole character. Never written to Material Editor or the card and undone before any card save. Returns a token for preview_restore.")]
    public static Task<JsonObject> PreviewSet(BridgeClient bridge, string sceneSession, int objectId = 0, string mode = "neutral", bool postEffects = true,
        float[]? color = null, int[]? rendererIds = null, string[]? scopes = null, int[]? accessorySlots = null, int[]? hairSlots = null,
        string[]? clothes = null, bool allAccessories = false)
        => bridge.Call("chara.preview_set", Args(new { sceneSession, objectId, mode, postEffects, color, rendererIds, scopes, accessorySlots, hairSlots,
            clothes = Kinds(clothes), allAccessories }));

    [McpServerTool, Description("Undo preview_set: one token, or every token when omitted.")]
    public static Task<JsonObject> PreviewRestore(BridgeClient bridge, string sceneSession, string? token = null)
        => bridge.Call("chara.preview_restore", Args(new { sceneSession, token }));

    [McpServerTool, Description("Read-only rig export of the current frame for weight diagnosis and exact offline replay: per renderer the unwelded rest mesh (vertices, normals, tangents, UVs, submesh indices, topology/UV hashes), skin weights, bind poses, bone hierarchy paths, root bone, blend shapes; plus this frame's bone world matrices, baked vertices, renderer matrices, shapes, ABMX, clothing states and animator time, all read at the end of one frame. A built-in replay of the skinning against BakeMesh is reported as verify.maxErrorMm. Binary arrays + manifest.json (numpy-readable) under UserData/StudioMCP/samples/<jobId>; fields that cannot be read are listed in missing. Filters as in mesh_export (body included unless includeBody=false).")]
    public static Task<CallToolResult> RigExport(BridgeClient bridge, string sceneSession, int objectId = 0, string[]? scopes = null,
        int[]? rendererIds = null, int[]? accessorySlots = null, int[]? hairSlots = null, string[]? clothes = null, bool includeBody = true,
        bool includeInactive = false, bool normals = true, bool verify = true, bool image = false, string? name = null)
        => RunSamples(bridge, Args(new { sceneSession, objectId, mode = "current", scopes, rendererIds, accessorySlots, hairSlots, clothes = Kinds(clothes),
            includeBody, includeInactive, normals, verify, images = image ? "all" : "none", name = name ?? "rig" }), TimeSpan.FromMinutes(2));

    [MakerOnly]
    [McpServerTool, Description("Maker mode: sample a pose exactly. mode frozen sets pose/time, stops the Animator there, waits warmupFrames and then until no bone moves more than settleMm per frame for 10 frames (up to maxWarmupFrames; the sample's settle block says if it settled; settleMm=0 skips this), then reads bone matrices, baked meshes and (images=true) a camera image in the same frame; physics reset (default: DynamicBones reset then settle during warm-up), settle (no reset) or off (DynamicBones disabled and returned to their rest pose: pure skinning). mode continuous keeps animation and physics running and records samples every intervalFrames without re-setting the pose. Both run the game at a fixed time step (fixedFps, default 60; 0 uses real time) so physics takes the same path every run, which is what makes samples repeatable; frozen also stops blinking. pose is a pose_list index, -1, \"asset/state\" or a state name; clothes e.g. {\"top\":1}; shapes e.g. {\"bust_size\":0.8} or {\"body\":{...},\"face\":{...}}. Pose, clothes and shapes are restored afterwards. Output as rig_export; the result has frameCount, the actual animator time and verify per renderer.")]
    public static Task<CallToolResult> PoseSample(BridgeClient bridge, string sceneSession, string? pose = null, float time = 0,
        [JsonSchema("{\"type\":\"object\",\"additionalProperties\":{\"type\":\"integer\"}}")] System.Text.Json.JsonElement? clothes = null,
        [JsonSchema("{\"type\":\"object\"}")] System.Text.Json.JsonElement? shapes = null,
        string mode = "frozen", string physics = "reset", int warmupFrames = 30, float settleMm = 0.002f, int maxWarmupFrames = 600, int fixedFps = 60, int samples = 10, int intervalFrames = 5, bool images = false,
        int imageWidth = 640, int imageHeight = 480, string[]? scopes = null, int[]? rendererIds = null, int[]? accessorySlots = null,
        int[]? hairSlots = null, string[]? clothesFilter = null, bool includeBody = true, bool normals = false, bool verify = true, string? name = null)
    {
        var item = new JsonObject();
        if (pose != null) item["pose"] = int.TryParse(pose, out var index) ? index : pose;
        if (pose != null) item["time"] = time;
        var parsed = Args(new { clothes, shapes });
        if (parsed["clothes"] != null) item["clothes"] = parsed["clothes"]!.DeepClone();
        if (parsed["shapes"] != null) item["shapes"] = parsed["shapes"]!.DeepClone();
        var args = Args(new { sceneSession, mode, physics, warmupFrames, settleMm, maxWarmupFrames, fixedFps, samples, intervalFrames, images = images ? "all" : "none", imageWidth, imageHeight,
            scopes, rendererIds, accessorySlots, hairSlots, clothes = Kinds(clothesFilter), includeBody, normals, verify, name = name ?? "pose" });
        args["cases"] = new JsonArray(item);
        return RunSamples(bridge, args, TimeSpan.FromMinutes(10));
    }

    [MakerOnly]
    [McpServerTool, Description("Maker mode: start a long sample job in the background and return at once. cases is a list of {pose, time, clothes, shapes} as in pose_sample (e.g. every pose x clothing state x time); options as in pose_sample. Poll sample_job_status for progress, current case, completed sample ids, bytes written and an estimate of the remaining disk use; sample_job_cancel stops it and restores the character. resume=<jobId> continues a cancelled, failed or interrupted job (even after a game restart) with its original cases and options, ignoring those given here, and skips samples already on disk. Rest meshes are written once per job, so many samples stay small; images only when images=true.")]
    public static Task<JsonObject> SampleJobStart(BridgeClient bridge, string sceneSession,
        [JsonSchema("{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"pose\":{\"type\":[\"integer\",\"string\"]},\"time\":{\"type\":\"number\"},\"clothes\":{\"type\":\"object\"},\"shapes\":{\"type\":\"object\"}}}}")] System.Text.Json.JsonElement? cases = null,
        string mode = "frozen", string physics = "reset", int? warmupFrames = null, float? settleMm = null, int? maxWarmupFrames = null, int? fixedFps = null, int? samples = null, int intervalFrames = 5, bool images = false,
        int imageWidth = 640, int imageHeight = 480, string[]? scopes = null, int[]? rendererIds = null, int[]? accessorySlots = null,
        int[]? hairSlots = null, string[]? clothesFilter = null, bool includeBody = true, bool normals = false, bool verify = false,
        string? name = null, string? resume = null)
        => bridge.Call("sample.start", Args(new { sceneSession, cases, mode, physics, warmupFrames, settleMm, maxWarmupFrames, fixedFps, samples, intervalFrames, images = images ? "all" : "none",
            imageWidth, imageHeight, scopes, rendererIds, accessorySlots, hairSlots, clothes = Kinds(clothesFilter), includeBody, normals, verify, name, resume }));

    [McpServerTool, Description("Progress of a sample job (the running one when jobId is omitted): state, current case, completed/remaining, last sample ids, bytes written, estimated remaining bytes, warnings and error.")]
    public static Task<JsonObject> SampleJobStatus(BridgeClient bridge, string? jobId = null)
        => bridge.Call("sample.status", Args(new { jobId }));

    [McpServerTool, Description("Cancel a running sample job after its current sample; the character's pose, clothes, shapes and physics are restored and finished samples are kept for resume.")]
    public static Task<JsonObject> SampleJobCancel(BridgeClient bridge, string? jobId = null)
        => bridge.Call("sample.cancel", Args(new { jobId }));

    [McpServerTool, Description("Delete the files a sample job wrote (only those recorded in its manifest, never other exports), then its empty folders. keepManifest keeps manifest.json.")]
    public static Task<JsonObject> SampleJobCleanup(BridgeClient bridge, string jobId, bool keepManifest = false)
        => bridge.Call("sample.cleanup", Args(new { jobId, keepManifest }));

    private static object? Kinds(string[]? kinds) => kinds?.Select(k => int.TryParse(k, out var slot) ? (object)slot : k).ToArray();

    // Runs a sample job to the end and summarises its manifest: renderers, missing fields, verification and samples.
    private static async Task<CallToolResult> RunSamples(BridgeClient bridge, JsonObject args, TimeSpan timeout)
    {
        var started = (await bridge.Call("sample.start", args))["result"]!.AsObject();
        var jobId = started["jobId"]!.GetValue<string>();
        var deadline = DateTime.UtcNow + timeout;
        JsonObject? status = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            status = (await bridge.Call("sample.status", new JsonObject { ["jobId"] = jobId }))["result"]!.AsObject();
            var state = status["state"]!.GetValue<string>();
            if (state == "failed") throw new ModelContextProtocol.McpException(status["error"]?.GetValue<string>() + " (jobId " + jobId + ")");
            if (state is "complete" or "cancelled") break;
            status = null;
        }
        if (status == null) throw new ModelContextProtocol.McpException("Sample job " + jobId + " is still running; follow it with sample_job_status.");
        var folder = status["folder"]!.GetValue<string>();
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "manifest.json")))!.AsObject();
        var renderers = new JsonArray();
        foreach (var pair in manifest["renderers"]!.AsObject())
        {
            var info = pair.Value!.AsObject();
            renderers.Add(new JsonObject { ["key"] = pair.Key, ["renderer"] = info["renderer"]?.DeepClone(), ["scope"] = info["scope"]?.DeepClone(),
                ["partSlot"] = info["partSlot"]?.DeepClone(), ["vertexCount"] = info["vertexCount"]?.DeepClone(), ["bones"] = (info["bones"] as JsonArray)?.Count,
                ["topologyHash"] = info["topologyHash"]?.DeepClone(), ["missing"] = info["missing"]?.DeepClone() });
        }
        var samples = new JsonArray();
        var images = new List<string>();
        foreach (var id in manifest["completed"]!.AsArray().Select(x => x!.GetValue<string>()).Take(12))
        {
            var sample = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "samples", id + ".json")))!.AsObject();
            var verify = new JsonObject();
            foreach (var pair in sample["renderers"]!.AsObject())
                if (pair.Value!["verify"] is JsonObject check) verify[pair.Key] = check["maxErrorMm"]?.DeepClone() ?? check["skipped"]?.DeepClone();
            samples.Add(new JsonObject { ["sampleId"] = id, ["frameCount"] = sample["frameCount"]?.DeepClone(), ["animator"] = sample["animator"]?.DeepClone(),
                ["settle"] = sample["settle"]?.DeepClone(), ["clothesState"] = sample["clothesState"]?.DeepClone(), ["verifyMaxErrorMm"] = verify, ["image"] = sample["image"]?.DeepClone() });
            if (sample["image"] != null) images.Add(Path.Combine(folder, sample["image"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar)));
        }
        status["manifestFormat"] = manifest["format"]?.DeepClone();
        status["coordinates"] = manifest["coordinates"]?.DeepClone();
        status["renderers"] = renderers;
        status["samples"] = samples;
        var content = new List<ContentBlock> { new TextContentBlock { Text = status.ToJsonString() } };
        foreach (var image in images.Take(4)) content.Add(ImageContentBlock.FromBytes(await File.ReadAllBytesAsync(image), "image/png"));
        return new CallToolResult { Content = content };
    }

    [StudioOnly]
    [McpServerTool, Description("Search installed Studio items by display name, returning authoritative runtime catalog IDs and native light IDs.")]
    public static Task<JsonObject> CatalogSearch(BridgeClient bridge, string query, int limit = 50)
        => bridge.Call("catalog.search", Args(new { query, limit }));

    [StudioOnly]
    [McpServerTool, Description("Create a saved native Studio light using an ID from catalog_search. Does not replace existing lights.")]
    public static Task<JsonObject> AddLight(BridgeClient bridge, string sceneSession, int lightId, string name,
        float[]? position = null, float[]? rotation = null)
        => bridge.Call("scene.add_light", Args(new { sceneSession, lightId, name, position, rotation }));

    [StudioOnly]
    [McpServerTool, Description("Set a Studio light's saved color, intensity, range, cone, enabled and shadow flags using its native setters.")]
    public static Task<JsonObject> LightSet(BridgeClient bridge, string sceneSession, int objectId,
        float[]? color = null, float? intensity = null, float? range = null, float? spotAngle = null,
        bool? enabled = null, bool? shadows = null, int? cullingMask = null, int? renderMode = null)
        => bridge.Call("light.set", Args(new { sceneSession, objectId, color, intensity, range, spotAngle, enabled, shadows, cullingMask, renderMode }));

    [McpServerTool, Description("Change to an installed ME-registered shader through ME's saved API. Inspect fresh material handles afterward. Checkpoint before changing shaders.")]
    public static Task<JsonObject> MaterialSetShader(BridgeClient bridge, string sceneSession, int objectId,
        int rendererId, string shader, int slot = 0)
        => bridge.Call("me.shader", Args(new { sceneSession, objectId, rendererId, shader, slot }));

    [StudioOnly]
    [McpServerTool, Description("Read Studio-owned objects, transforms, bounds and the current camera. Paginated; not a dump of hidden Unity objects.")]
    public static Task<JsonObject> SceneSnapshot(BridgeClient bridge, int offset = 0, int limit = 50)
        => bridge.Call("scene.snapshot", new JsonObject { ["offset"] = offset, ["limit"] = limit });

    [StudioOnly]
    [McpServerTool, Description("Create a Studio catalog item in the current inspected scene. Position and scale use Studio units.")]
    public static Task<JsonObject> AddItem(BridgeClient bridge, string sceneSession, int group, int category,
        int itemId, string name, float[]? position = null, float[]? scale = null)
        => bridge.Call("scene.add_item", Args(new { sceneSession, group, category, itemId, name, position, scale }));

    [StudioOnly]
    [McpServerTool, Description("Move, rotate or scale a Studio object through its saved changeAmount data, not a temporary Unity-only transform.")]
    public static Task<JsonObject> SetTransform(BridgeClient bridge, string sceneSession, int objectId,
        float[]? position = null, float[]? rotation = null, float[]? scale = null)
        => bridge.Call("object.set_transform", Args(new { sceneSession, objectId, position, rotation, scale }));

    [McpServerTool, Description("Read the camera orbit pivot, rotation, field of view and actual eye position.")]
    public static Task<JsonObject> CameraGet(BridgeClient bridge) => bridge.Call("camera.get");

    [McpServerTool, Description("Set the Studio or maker camera orbit pivot, Euler rotation and field of view. Pivot is not the eye position. The camera glides to the new orbit and the game's temporal effects ghost the previous view, so wait about 2 s before capture (capture_turntable waits by itself).")]
    public static Task<JsonObject> CameraSet(BridgeClient bridge, string sceneSession,
        float[]? pivot = null, float[]? rotation = null, float? fov = null, float[]? distance = null)
        => bridge.Call("camera.set", Args(new { sceneSession, pivot, rotation, fov, distance }));

    [StudioOnly]
    [McpServerTool, Description("Read the scene's saved Studio camera slots (the native camera save/load buttons): pivot, rotation, distance and field of view per slot.")]
    public static Task<JsonObject> CameraSlotsGet(BridgeClient bridge) => bridge.Call("camera.slots");

    [StudioOnly]
    [McpServerTool, Description("Save the current camera into a native Studio camera slot, exactly like Studio's camera save button. Slots are stored in the scene PNG when the scene is saved.")]
    public static Task<JsonObject> CameraSlotSave(BridgeClient bridge, string sceneSession, int slot)
        => bridge.Call("camera.slot_save", Args(new { sceneSession, slot }));

    [StudioOnly]
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
        int rendererId, string property, string kind,
        [JsonSchema("{\"anyOf\":[{\"type\":\"number\"},{\"type\":\"array\",\"items\":{\"type\":\"number\"}}],\"description\":\"number for Float, [r,g,b,a] for Color\"}")] System.Text.Json.JsonElement value, int slot = 0)
        => bridge.Call("me.set", Args(new { sceneSession, objectId, rendererId, property, kind, value, slot }));

    [McpServerTool, Description("Set a material's render queue through Material Editor's saved API (0-5000), or -1 to remove ME's override and restore the recorded original queue. Unity draws only queues <= 2500 into the camera depth texture, and only with the shader's ShadowCaster pass. ME applies it to all same-name materials in the object.")]
    public static Task<JsonObject> MaterialSetRenderQueue(BridgeClient bridge, string sceneSession, int objectId,
        int rendererId, int renderQueue, int slot = 0)
        => bridge.Call("me.render_queue", Args(new { sceneSession, objectId, rendererId, renderQueue, slot }));

    [StudioOnly]
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

    [StudioOnly]
    [McpServerTool, Description("Read native Studio postprocessing values and actual camera components. No PPE or external postprocessing stack is added.")]
    public static Task<JsonObject> NativeEffectsGet(BridgeClient bridge) => bridge.Call("native_effects.get");

    [StudioOnly]
    [McpServerTool, Description("Set whitelisted native Studio effects using native_effects_get values. Writes native SceneInfo and refreshes the existing effect controllers; saves in scene PNGs.")]
    public static Task<JsonObject> NativeEffectsSet(BridgeClient bridge, string sceneSession, [JsonSchema("{\"type\":\"object\"}")] System.Text.Json.JsonElement values)
        => bridge.Call("native_effects.set", Args(new { sceneSession, values }));

    [StudioOnly]
    [McpServerTool, Description("Read the optional shared KKS DOF provider, camera policy, render counters and supported setters. Missing, incompatible or not-ready providers return available=false; no plugin is installed or enabled.")]
    public static Task<JsonObject> DofCompatibilityGet(BridgeClient bridge) => bridge.Call("dof_compat.get");

    [StudioOnly]
    [McpServerTool, Description("Export the last completed main-camera BSDF opaque color buffer to a diagnostic LDR PNG, without tone mapping or requesting a new buffer/render. Missing resources return available=false. Readback is blocked during capture and bounded to 16777216 pixels. Not a viewport screenshot or DOF/depth acceptance.")]
    public static Task<JsonObject> DofBufferSnapshot(BridgeClient bridge) => bridge.Call("dof_compat.snapshot");

    [StudioOnly]
    [McpServerTool, Description("Set the installed optional KKS DOF provider's scenePolicy (Inherit, Native, BeforeTransparents) or globalMode (Native, BeforeTransparents). Provider owns persistence; native DOF parameters are unchanged. Early mode keeps ALL transparents sharp. Inputs are prevalidated, but provider exceptions can leave partial changes: inspect state before retrying.")]
    public static Task<JsonObject> DofCompatibilitySet(BridgeClient bridge, string sceneSession,
        string? scenePolicy = null, string? globalMode = null)
        => bridge.Call("dof_compat.set", Args(new { sceneSession, scenePolicy, globalMode }));

    [StudioOnly]
    [McpServerTool, Description("List original KKS sky presets and read the active native sky. Does not add a sky filter item.")]
    public static Task<JsonObject> NativeSkyGet(BridgeClient bridge) => bridge.Call("native_sky.get");

    [StudioOnly]
    [McpServerTool, Description("Select an original KKS sky preset by ID from native_sky_get. Saved through native Studio SceneInfo.")]
    public static Task<JsonObject> NativeSkySet(BridgeClient bridge, string sceneSession, int pattern, bool enabled = true)
        => bridge.Call("native_sky.set", Args(new { sceneSession, pattern, enabled }));

    [StudioOnly]
    [McpServerTool, Description("Bind an inspected Studio item's native Skybox material to RenderSettings. Disables the built-in sky driver while bound, saves the item reference in the scene PNG, restores the native driver on native_sky_set or scene clear. No postprocessing/filter item.")]
    public static Task<JsonObject> SkyboxBind(BridgeClient bridge, string sceneSession, int objectId, int rendererId,
        float exposure = 1, float rotation = 0, int slot = 0)
        => bridge.Call("skybox.bind", Args(new { sceneSession, objectId, rendererId, slot, exposure, rotation }));

    [StudioOnly]
    [McpServerTool, Description("Set a Studio object's native saved visibility, including its children. Useful for separately authored weather particle objects.")]
    public static Task<JsonObject> SetVisible(BridgeClient bridge, string sceneSession, int objectId, bool visible)
        => bridge.Call("object.set_visible", Args(new { sceneSession, objectId, visible }));

    [StudioOnly]
    [McpServerTool, Description("Read the Studio camera's native AmplifyColor tonemapper, exposure, linear white point and LUT blend.")]
    public static Task<JsonObject> TonemapGet(BridgeClient bridge) => bridge.Call("tonemap.get");

    [StudioOnly]
    [McpServerTool, Description("Set the Studio camera's native AmplifyColor tonemapper (Disabled, Photographic, FilmicHable, FilmicACES), exposure (0.05-16) and linear white point (0.5-64). Saved in the scene PNG. Scene loads reset only bridge-owned edits to their pre-edit values. No extra postprocessing stack.")]
    public static Task<JsonObject> TonemapSet(BridgeClient bridge, string sceneSession, [JsonSchema("{\"type\":\"object\"}")] System.Text.Json.JsonElement values)
        => bridge.Call("tonemap.set", Args(new { sceneSession, values }));

    [McpServerTool, Description("Read bounded actual camera, light and reflection-probe resources, including color space and probe texture availability. Read only.")]
    public static Task<JsonObject> RenderDiagnostics(BridgeClient bridge) => bridge.Call("render.diagnostics");

    [StudioOnly]
    [McpServerTool, Description("Patch native render settings using keys returned by render_get. Checkpoints also store these in a sidecar. Does not edit arbitrary graphics-mod internals.")]
    public static Task<JsonObject> RenderSet(BridgeClient bridge, string sceneSession, [JsonSchema("{\"type\":\"object\"}")] System.Text.Json.JsonElement values)
        => bridge.Call("render.set", Args(new { sceneSession, values }));

    [StudioOnly]
    [McpServerTool, Description("Save a new Studio scene PNG under UserData/StudioMCP using normal save hooks, plus a native-render-settings sidecar. Never overwrites an existing scene/card.")]
    public static Task<JsonObject> SceneCheckpoint(BridgeClient bridge, string sceneSession)
        => bridge.Call("scene.checkpoint", Args(new { sceneSession }));

    [StudioOnly]
    [McpServerTool, Description("Replace the current scene with one of this bridge's checkpoint PNGs. Unsaved edits are lost. Re-read the scene and obtain fresh renderer handles after restoring.")]
    public static Task<JsonObject> SceneRestore(BridgeClient bridge, string sceneSession, string path)
        => bridge.Call("scene.restore", Args(new { sceneSession, path }));

    [McpServerTool, Description("Apply 1-32 operations synchronously. Each is {method,args}; allowlist: me.set Float/Color, chara.shape_set, chara.accessory_set, chara.bones_set, object.set_transform, camera.set, render.set. Prevalidates and rolls back on failure; returns a batchId for one-level undo.")]
    public static Task<JsonObject> BatchApply(BridgeClient bridge, string sceneSession,
        [JsonSchema("{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"method\":{\"type\":\"string\"},\"args\":{\"type\":\"object\"}},\"required\":[\"method\",\"args\"]}}")] System.Text.Json.JsonElement operations)
        => bridge.Call("batch.apply", Args(new { sceneSession, operations }));

    [McpServerTool, Description("Undo the most recent batch in the same scene, before any other bridge writes. Reports restoration failures explicitly.")]
    public static Task<JsonObject> BatchUndo(BridgeClient bridge, string sceneSession, string batchId)
        => bridge.Call("batch.undo", Args(new { sceneSession, batchId }));

    [McpServerTool, Description("Render through installed Screencap and return a PNG preview plus the full path. alphaMode is None (default), blackout (native cutout) or rgAlpha (native gradual alpha); inspect studio_status.captureOptions for support. withDepth requires None. Waits for completion; never captures the desktop.")]
    public static async Task<CallToolResult> Capture(BridgeClient bridge, string sceneSession,
        int width = 640, int height = 360, int supersampling = 1, bool withDepth = false, string alphaMode = "None", float settleSeconds = 0)
    {
        var started = await bridge.Call("capture.start", Args(new { sceneSession, width, height, supersampling, withDepth, alphaMode, settleSeconds }));
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
            if (state == "failed") throw new ModelContextProtocol.McpException(job["error"]?.GetValue<string>() ?? "The job failed.");
            if (state != "complete") continue;
            var preview = job["previewBase64"]!.GetValue<string>();
            job.Remove("previewBase64");
            return new CallToolResult { Content = [
                new TextContentBlock { Text = job.ToJsonString() },
                ImageContentBlock.FromBytes(Convert.FromBase64String(preview), "image/png")
            ] };
        }
        throw new ModelContextProtocol.McpException("Capture is still running. Do not restart the game or repeat the capture blindly.");
    }

    // Parameters that carry JSON. Some MCP clients send them as JSON text even with an object schema; parse that here.
    private static readonly HashSet<string> JsonKeys = ["values", "colors", "hair", "sliders", "markers", "value", "operations", "cases", "pluginToggles", "clothes", "shapes"];

    private static JsonObject Args(object value)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(value, new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })!.AsObject();
        foreach (var key in node.Select(p => p.Key).ToList())
        {
            if (!JsonKeys.Contains(key) || node[key] is not JsonValue text || !text.TryGetValue<string>(out var raw)) continue;
            JsonNode? parsed = null;
            try { parsed = JsonNode.Parse(raw); } catch (System.Text.Json.JsonException) { }
            if (parsed is JsonObject or JsonArray || (parsed is JsonValue v && !v.TryGetValue<string>(out _))) node[key] = parsed;
            else throw new ModelContextProtocol.McpException($"{key} must be JSON (for example an object or array), not the text: {raw}");
        }
        return node;
    }
}
