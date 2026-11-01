using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using static StudioMcp.Host.StudioTools;

namespace StudioMcp.Host;

[McpServerToolType]
public static class AnimationTools
{
    [StudioOnly]
    [McpServerTool, Description("Inspect a character's native Studio FK/IK enable state, groups and control counts. Does not require KKPE.")]
    public static Task<JsonObject> CharacterState(BridgeClient bridge, int objectId)
        => bridge.Call("character.state", Args(new { objectId }));

    [StudioOnly]
    [McpServerTool, Description("List native FK or IK controls for a character, including controlId, group, active state, saved guide-local position/rotation and world position. mode is FK or IK. Rotation units are Euler degrees; handles must be reacquired after character replacement or scene load.")]
    public static Task<JsonObject> CharacterControls(BridgeClient bridge, int objectId, string mode, int offset = 0, int limit = 50)
        => bridge.Call("character.controls", Args(new { objectId, mode, offset, limit }));

    [StudioOnly]
    [McpServerTool, Description("Enable/disable native FK or IK through Studio's original API. With group omitted, changes the whole mode using Studio's normal mutual-exclusion behavior. With group supplied, updates only that group's saved flag; use a group from character_state. Does not install or use a replacement solver.")]
    public static Task<JsonObject> CharacterKinematicsSet(BridgeClient bridge, string sceneSession, int objectId,
        string mode, bool enabled, string? group = null)
        => bridge.Call("character.kinematics", Args(new { sceneSession, objectId, mode, enabled, group }));

    [StudioOnly]
    [McpServerTool, Description("Set an inspected native FK rotation or IK target through its saved GuideObject. mode is FK or IK; enable that mode/group first. FK accepts rotation only; IK channels depend on the control's canPosition/canRotation. space is local (saved guide-local, default) or world; rotations are Euler degrees.")]
    public static Task<JsonObject> CharacterControlSet(BridgeClient bridge, string sceneSession, int objectId,
        string mode, int controlId, float[]? position = null, float[]? rotation = null, string space = "local")
        => bridge.Call("character.control_set", Args(new { sceneSession, objectId, mode, controlId, position, rotation, space }));

    [StudioOnly]
    [McpServerTool, Description("List KKPE-owned bones and their current local position, Euler rotation, scale and override state. Optional name query and pagination. Attached objects with their own KKPE controller are excluded. Returns available=false when KKPE is absent; reacquire boneId after hierarchy rebuilds.")]
    public static Task<JsonObject> PoseBones(BridgeClient bridge, int objectId, string query = "", int offset = 0, int limit = 50)
        => bridge.Call("pose.bones", Args(new { objectId, query, offset, limit }));

    [StudioOnly]
    [McpServerTool, Description("Edit a bone through KKPE's original override/persistence methods. Values are bone-local, rotations are Euler degrees. Active FK rotation follows KKPE's native FK route. Applies KKPE's correction pass before returning; no new override loop or save format.")]
    public static Task<JsonObject> PoseBoneSet(BridgeClient bridge, string sceneSession, int objectId, int boneId,
        float[]? position = null, float[]? rotation = null, float[]? scale = null)
        => bridge.Call("pose.bone_set", Args(new { sceneSession, objectId, boneId, position, rotation, scale }));

    [StudioOnly]
    [McpServerTool, Description("Reset the selected bone's KKPE overrides using KKPE's recorded originals, without resetting children. channel is all, position, rotation or scale. Does not reset unrelated native FK edits.")]
    public static Task<JsonObject> PoseBoneReset(BridgeClient bridge, string sceneSession, int objectId, int boneId, string channel = "all")
        => bridge.Call("pose.bone_reset", Args(new { sceneSession, objectId, boneId, channel }));

    [StudioOnly]
    [McpServerTool, Description("List KKPE blendshapes by actual rendererId and unique shapeName, with current Unity weight, override state and the provider's current linkEyesComponents setting. Optional renderer and shape-name filters; paginated. Returns available=false when KKPE is absent.")]
    public static Task<JsonObject> PoseBlendshapes(BridgeClient bridge, int objectId, int? rendererId = null,
        string query = "", int offset = 0, int limit = 50)
        => bridge.Call("pose.blendshapes", Args(new { objectId, rendererId, query, offset, limit }));

    [StudioOnly]
    [McpServerTool, Description("Set one named blendshape through KKPE's original name-based dirty-state API and apply it before returning. Follows KKPE's current Link eyes components setting, so linked meshes can also change. Preserves saved originals and scene persistence. Typical weights are 0-100; finite negative/overdrive values are supported.")]
    public static Task<JsonObject> PoseBlendshapeSet(BridgeClient bridge, string sceneSession, int objectId, int rendererId, string shapeName, float weight)
        => bridge.Call("pose.blendshape_set", Args(new { sceneSession, objectId, rendererId, shapeName, weight }));

    [StudioOnly]
    [McpServerTool, Description("Remove one named KKPE blendshape override and restore its recorded original weight. With KKPE's Link eyes components enabled, linked shapes also restore their own recorded originals. Other shapes are retained.")]
    public static Task<JsonObject> PoseBlendshapeReset(BridgeClient bridge, string sceneSession, int objectId, int rendererId, string shapeName)
        => bridge.Call("pose.blendshape_reset", Args(new { sceneSession, objectId, rendererId, shapeName }));

    [StudioOnly]
    [McpServerTool, Description("Inspect optional Timeline availability, playing state, time in seconds, duration, frame rate, speed and track kinds. Does not start playback or install a plugin.")]
    public static Task<JsonObject> TimelineGet(BridgeClient bridge) => bridge.Call("timeline.get");

    [StudioOnly]
    [McpServerTool, Description("Control installed Timeline using its original play/pause/stop/seek/next_frame/previous_frame methods. seek requires time in seconds inside the project duration. Playback is owned by Timeline, not an MCP scheduler.")]
    public static Task<JsonObject> TimelineControl(BridgeClient bridge, string sceneSession, string action, float? time = null)
        => bridge.Call("timeline.control", Args(new { sceneSession, action, time }));

    [StudioOnly]
    [McpServerTool, Description("Set paused Timeline project controls through its original handlers: duration in seconds (positive, <=86400), frameRate (integer 1-240) and speed (0-100, Timeline's global Unity time scale). Existing keyframes are retained when duration changes.")]
    public static Task<JsonObject> TimelineSettings(BridgeClient bridge, string sceneSession, float? duration = null, int? frameRate = null, float? speed = null)
        => bridge.Call("timeline.settings", Args(new { sceneSession, duration, frameRate, speed }));

    [StudioOnly]
    [McpServerTool, Description("List existing native Timeline tracks, their owner/model, target binding, value and keyframe count. Optional object filter and pagination. trackId is a current-scene handle; reacquire after loading. Other providers' tracks can be inspected but are not edited by this version.")]
    public static Task<JsonObject> TimelineTracks(BridgeClient bridge, int? objectId = null, int offset = 0, int limit = 50)
        => bridge.Call("timeline.tracks", Args(new { objectId, offset, limit }));

    [StudioOnly]
    [McpServerTool, Description("Create or find a paused Timeline track using existing Timeline/KKPE models. kind: object_position/object_rotation/object_scale; fk_rotation or ik_position/ik_rotation with controlId; bone_position/bone_rotation/bone_scale with boneId; blendshape with rendererId and shapeName. KKPE bone rotation controlled by active FK binds the native FK track. Optional name applies only to a new track. Native plugin save formats and interpolation are retained.")]
    public static Task<JsonObject> TimelineTrackCreate(BridgeClient bridge, string sceneSession, int objectId, string kind,
        int? controlId = null, int? boneId = null, int? rendererId = null, string? shapeName = null, string? name = null)
        => bridge.Call("timeline.track_create", Args(new { sceneSession, objectId, kind, controlId, boneId, rendererId, shapeName, name }));

    [StudioOnly]
    [McpServerTool, Description("Set enabled or the display name of an existing paused native object/FK/IK or KKPE bone/blendshape track. Other tracks and keyframes are retained.")]
    public static Task<JsonObject> TimelineTrackSet(BridgeClient bridge, string sceneSession, int trackId, bool? enabled = null, string? name = null)
        => bridge.Call("timeline.track_set", Args(new { sceneSession, trackId, enabled, name }));

    [StudioOnly]
    [McpServerTool, Description("Remove one paused native object/FK/IK or KKPE bone/blendshape Timeline track and its keyframes through Timeline's own removal method.")]
    public static Task<JsonObject> TimelineTrackRemove(BridgeClient bridge, string sceneSession, int trackId)
        => bridge.Call("timeline.track_remove", Args(new { sceneSession, trackId }));

    [StudioOnly]
    [McpServerTool, Description("Read a Timeline track's keyframe times, native values and interpolation curves, with pagination. Quaternion values are [x,y,z,w].")]
    public static Task<JsonObject> TimelineKeyframes(BridgeClient bridge, int trackId, int offset = 0, int limit = 50)
        => bridge.Call("timeline.keyframes", Args(new { trackId, offset, limit }));

    [StudioOnly]
    [McpServerTool, Description("Add/edit a paused native object/FK/IK or KKPE bone/blendshape keyframe at time seconds. Omit value to record the current target value; otherwise provide a scalar, vec3, Euler degrees [x,y,z] for rotation, or quaternion [x,y,z,w]. Existing time requires replace=true. curve is linear, smooth or step for the interval from this key to the next; omitted curve preserves an existing curve and defaults to linear for new keys.")]
    public static Task<JsonObject> TimelineKeyframeSet(BridgeClient bridge, string sceneSession, int trackId, float time,
        [JsonSchema("{\"anyOf\":[{\"type\":\"number\"},{\"type\":\"array\",\"items\":{\"type\":\"number\"}}],\"description\":\"number, [x,y,z] or a quaternion [x,y,z,w]; omit to record the current target\"}")] JsonElement? value = null, string? curve = null, bool replace = false)
        => bridge.Call("timeline.keyframe_set", Args(new { sceneSession, trackId, time, value, curve, replace }));

    [StudioOnly]
    [McpServerTool, Description("Remove the keyframe at an exact time from a paused native object/FK/IK or KKPE bone/blendshape track using Timeline's own deletion method. An empty track is retained.")]
    public static Task<JsonObject> TimelineKeyframeRemove(BridgeClient bridge, string sceneSession, int trackId, float time)
        => bridge.Call("timeline.keyframe_remove", Args(new { sceneSession, trackId, time }));
}
