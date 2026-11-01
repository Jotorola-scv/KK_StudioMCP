using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Studio;
using UnityEngine;

namespace StudioMcp.Bridge
{
    internal sealed partial class TimelineCommands
    {
        private readonly Plugin _plugin;
        internal TimelineCommands(Plugin plugin) { _plugin = plugin; }
        private static Type Provider => PluginAccess.Type(PluginAccess.TimelineId, "Timeline.Timeline");
        internal static bool Available => Provider != null;
        private static bool Playing => (bool)PluginAccess.Get(Provider, "isPlaying");

        private static object Instance()
        {
            if (Provider == null) throw new NotSupportedException("Timeline is not installed.");
            var self = PluginAccess.Get(Provider, "_self");
            if (self == null || ((IList)PluginAccess.Get(self, "_interpolableModelsList")).Count == 0)
                throw new InvalidOperationException("Timeline has not finished initializing Studio.");
            foreach (var track in Tracks())
            {
                var target = PluginAccess.Get(track, "oci") as ObjectCtrlInfo;
                ObjectCtrlInfo current;
                if (target != null && (Studio.Studio.Instance == null
                    || !Studio.Studio.Instance.dicObjectCtrl.TryGetValue(target.objectInfo.dicKey, out current)
                    || !ReferenceEquals(current, target)))
                    throw new InvalidOperationException("Timeline is still restoring its native scene bindings; inspect again after loading completes.");
            }
            return self;
        }

        internal static JObject State()
        {
            if (!Available) return new JObject { ["available"] = false, ["reason"] = "Timeline is not installed." };
            try
            {
                var self = Instance();
                return new JObject { ["available"] = true, ["playing"] = Playing,
                    ["time"] = (float)PluginAccess.Get(Provider, "playbackTime"),
                    ["duration"] = (float)PluginAccess.Get(Provider, "duration"),
                    ["frameRate"] = (int)PluginAccess.Get(self, "_desiredFrameRate"),
                    ["speed"] = Time.timeScale, ["trackCount"] = Tracks().Count(),
                    ["trackKinds"] = new JArray("object_position", "object_rotation", "object_scale",
                        "fk_rotation", "ik_position", "ik_rotation", "bone_position", "bone_rotation", "bone_scale", "blendshape") };
            }
            catch (Exception error)
            {
                return new JObject { ["available"] = false, ["reason"] = error.Message };
            }
        }

        internal JToken Execute(string method, JObject args)
        {
            if (method == "timeline.get") return State();
            if (method == "timeline.tracks" || method == "timeline.keyframes")
            {
                var state = State();
                if (!(bool)state["available"]) return state;
            }
            var self = Instance();
            if (method == "timeline.control") return Control(self, args);
            if (method == "timeline.settings") return Settings(self, args);
            var offset = Math.Max(0, (int?)args["offset"] ?? 0);
            var limit = Math.Max(1, Math.Min(200, (int?)args["limit"] ?? 50));
            if (method == "timeline.tracks")
            {
                var tracks = Tracks().Where(t => args["objectId"] == null
                    || ((ObjectCtrlInfo)PluginAccess.Get(t, "oci"))?.objectInfo.dicKey == (int)args["objectId"]).ToArray();
                return new JObject { ["available"] = true, ["total"] = tracks.Length,
                    ["tracks"] = new JArray(tracks.Skip(offset).Take(limit).Select(Describe)) };
            }
            if (method == "timeline.track_create")
            {
                RequirePaused();
                return CreateTrack(self, args);
            }
            var track = Tracks().FirstOrDefault(t => t.GetHashCode() == (int)args["trackId"]);
            if (track == null) throw new ArgumentException("Track is stale or absent; inspect timeline_tracks again.");
            var keys = (IDictionary)PluginAccess.Get(track, "keyframes");
            if (method == "timeline.keyframes")
                return new JObject { ["available"] = true, ["trackId"] = track.GetHashCode(), ["total"] = keys.Count,
                    ["keyframes"] = new JArray(KeyEntries(keys).Skip(offset).Take(limit).Select(k =>
                        DescribeKey((float)k.Key, k.Value))) };
            RequirePaused();
            RequireEditable(track);
            if (method == "timeline.track_remove")
            {
                PluginAccess.Call(self, "RemoveInterpolable", track);
                return new JObject { ["trackId"] = track.GetHashCode(), ["removed"] = true };
            }
            if (method == "timeline.track_set")
            {
                var name = Name(args["name"]);
                if (args["enabled"] == null && name == null) throw new ArgumentException("Provide enabled or name.");
                if (args["enabled"] != null && args["enabled"].Type != JTokenType.Boolean)
                    throw new ArgumentException("enabled requires a boolean.");
                if (args["enabled"] != null) PluginAccess.Set(track, "enabled", (bool)args["enabled"]);
                if (name != null) PluginAccess.Set(track, "alias", name);
            }
            else if (method == "timeline.keyframe_set") SetKeyframe(track, keys, args);
            else if (method == "timeline.keyframe_remove")
            {
                var time = TimeValue(args["time"]);
                if (!keys.Contains(time)) throw new ArgumentException("No keyframe exists at this exact time; use a time returned by timeline_keyframes.");
                var key = keys[time];
                var pairType = typeof(KeyValuePair<,>).MakeGenericType(typeof(float), key.GetType());
                var selection = Array.CreateInstance(pairType, 1);
                selection.SetValue(Activator.CreateInstance(pairType, new[] { (object)time, key }), 0);
                PluginAccess.Call(self, "DeleteKeyframes", selection, false);
            }
            else throw new ArgumentException("Unknown Timeline operation.");
            PluginAccess.Call(Provider, "RefreshInterpolablesList");
            PluginAccess.Call(self, "UpdateGrid");
            PluginAccess.Call(self, "UpdateKeyframeWindow", false);
            return Describe(track);
        }

        private JToken Control(object self, JObject args)
        {
            var action = (string)args["action"];
            switch (action)
            {
                case "play": PluginAccess.Call(Provider, "Play"); break;
                case "pause": PluginAccess.Call(Provider, "Pause"); break;
                case "stop": PluginAccess.Call(Provider, "Stop"); break;
                case "seek":
                    var time = TimeValue(args["time"]);
                    if (time > (float)PluginAccess.Get(Provider, "duration"))
                        throw new ArgumentOutOfRangeException("time", "Seek time must be inside the project duration.");
                    var sameTime = time == (float)PluginAccess.Get(Provider, "playbackTime");
                    PluginAccess.Call(Provider, "Seek", time);
                    if (sameTime)
                    {
                        // Native Seek skips unchanged times, including after editing keys at that time.
                        PluginAccess.Call(self, "Interpolate", true);
                        PluginAccess.Call(self, "Interpolate", false);
                    }
                    break;
                case "next_frame": PluginAccess.Call(Provider, "NextFrame"); break;
                case "previous_frame": PluginAccess.Call(Provider, "PreviousFrame"); break;
                default: throw new ArgumentException("action must be play, pause, stop, seek, next_frame or previous_frame.");
            }
            if (action != "play" && action != "pause")
                foreach (var target in Tracks().Select(t => PluginAccess.Get(t, "oci") as ObjectCtrlInfo).Where(o => o != null).Distinct())
                    _plugin.PoseEditor.Flush(target);
            return State();
        }

        private static JToken Settings(object self, JObject args)
        {
            RequirePaused();
            float? duration = args["duration"] == null ? (float?)null : TimeValue(args["duration"]);
            float? speed = args["speed"] == null ? (float?)null : SceneCommands.Number(args["speed"]);
            int? fps = args["frameRate"] == null ? (int?)null : (int)args["frameRate"];
            if (!duration.HasValue && !fps.HasValue && !speed.HasValue) throw new ArgumentException("Provide duration, frameRate or speed.");
            if (duration.HasValue && duration.Value <= 0) throw new ArgumentOutOfRangeException("duration");
            if (speed.HasValue && (speed.Value < 0 || speed.Value > 100)) throw new ArgumentOutOfRangeException("speed");
            if (fps.HasValue && (args["frameRate"].Type != JTokenType.Integer || fps.Value < 1 || fps.Value > 240))
                throw new ArgumentOutOfRangeException("frameRate", "frameRate must be an integer from 1 to 240.");
            var durationInput = duration.HasValue ? PluginAccess.Get(self, "_durationInputField") : null;
            var fpsInput = fps.HasValue ? PluginAccess.Get(self, "_frameRateInputField") : null;
            var speedInput = speed.HasValue ? PluginAccess.Get(self, "_speedInputField") : null;
            if ((duration.HasValue && durationInput == null) || (fps.HasValue && fpsInput == null) || (speed.HasValue && speedInput == null))
                throw new InvalidOperationException("Timeline's project controls are not ready.");
            if (duration.HasValue)
            {
                var minutes = (int)(duration.Value / 60);
                var seconds = duration.Value - minutes * 60;
                PluginAccess.Set(durationInput, "text", minutes + ":" + seconds.ToString("00.###", CultureInfo.CurrentCulture));
                PluginAccess.Call(self, "UpdateDuration", "");
            }
            if (fps.HasValue)
            {
                PluginAccess.Set(fpsInput, "text", fps.Value.ToString(CultureInfo.CurrentCulture));
                PluginAccess.Call(self, "UpdateDesiredFrameRate", "");
            }
            if (speed.HasValue)
            {
                PluginAccess.Set(speedInput, "text", speed.Value.ToString(CultureInfo.CurrentCulture));
                PluginAccess.Call(self, "UpdateSpeed", "");
            }
            return State();
        }

        private static void SetKeyframe(object track, IDictionary keys, JObject args)
        {
            var time = TimeValue(args["time"]);
            if (args["replace"] != null && args["replace"].Type != JTokenType.Boolean) throw new ArgumentException("replace requires a boolean.");
            var exists = keys.Contains(time);
            if (exists && !((bool?)args["replace"] ?? false))
                throw new InvalidOperationException("A keyframe already exists at this time; set replace=true to edit it.");
            var current = PluginAccess.Call(track, "GetValue");
            var input = args["value"] ?? JsonValue(current);
            if (input == null) throw new NotSupportedException("This track's value type is not editable through MCP.");
            var value = ParseValue(input, current);
            var curveName = (string)args["curve"];
            AnimationCurve curve = null;
            if (curveName == "linear" || (curveName == null && !exists)) curve = AnimationCurve.Linear(0, 0, 1, 1);
            else if (curveName == "smooth") curve = AnimationCurve.EaseInOut(0, 0, 1, 1);
            else if (curveName == "step") curve = new AnimationCurve(new UnityEngine.Keyframe(0, 0, 0, float.PositiveInfinity), new UnityEngine.Keyframe(1, 1));
            else if (curveName != null) throw new ArgumentException("curve must be linear, smooth or step.");
            if (exists)
            {
                PluginAccess.Set(keys[time], "value", value);
                if (curve != null) PluginAccess.Set(keys[time], "curve", curve);
            }
            else
            {
                var keyType = track.GetType().Assembly.GetType("Timeline.Keyframe", true);
                keys.Add(time, Activator.CreateInstance(keyType, new[] { value, track, curve }));
            }
        }

        private static IEnumerable<object> Tracks() => ((IEnumerable)PluginAccess.Call(Provider, "GetAllInterpolables", false)).Cast<object>();
        private static void RequirePaused()
        {
            if (Playing) throw new InvalidOperationException("Pause Timeline before editing tracks or keyframes.");
        }
        private static float TimeValue(JToken value)
        {
            var time = SceneCommands.Number(value);
            if (time < 0 || time > 86400) throw new ArgumentOutOfRangeException("time", "Time must be from 0 to 86400 seconds.");
            return time;
        }
        private static string Name(JToken value)
        {
            if (value == null) return null;
            if (value.Type != JTokenType.String || ((string)value).Length > 160)
                throw new ArgumentException("name must be a string of at most 160 characters.");
            return (string)value;
        }
        private static void RequireEditable(object track)
        {
            var owner = (string)PluginAccess.Get(track, "owner");
            var id = (string)PluginAccess.Get(track, "id");
            if ((owner == "Timeline" && new[] { "guideObjectPos", "guideObjectRot", "guideObjectScale" }.Contains(id))
                || (owner == "KKPE" && new[] { "bonePos", "boneRot", "boneScale", "lastBlendShape" }.Contains(id))) return;
            throw new NotSupportedException("This MCP version edits native object/FK/IK and KKPE bone/blendshape tracks only.");
        }

        private static JObject Describe(object track)
        {
            var target = PluginAccess.Get(track, "oci") as ObjectCtrlInfo;
            var result = new JObject { ["trackId"] = track.GetHashCode(), ["owner"] = (string)PluginAccess.Get(track, "owner"),
                ["modelId"] = (string)PluginAccess.Get(track, "id"), ["objectId"] = target?.objectInfo.dicKey,
                ["enabled"] = (bool)PluginAccess.Get(track, "enabled"), ["alias"] = (string)PluginAccess.Get(track, "alias"),
                ["keyframeCount"] = ((IDictionary)PluginAccess.Get(track, "keyframes")).Count };
            try
            {
                result["name"] = (string)PluginAccess.Get(track, "name");
                result["binding"] = ParameterAttributes(track);
                var value = PluginAccess.Call(track, "GetValue");
                result["valueType"] = value?.GetType().Name;
                result["value"] = JsonValue(value);
                if (value is Quaternion) result["eulerDegrees"] = SceneCommands.Json(((Quaternion)value).eulerAngles);
                result["valid"] = true;
            }
            catch (Exception error) { result["valid"] = false; result["error"] = error.GetBaseException().Message; }
            return result;
        }

        private static JToken JsonValue(object value)
        {
            if (value is Vector3) return SceneCommands.Json((Vector3)value);
            if (value is Quaternion) { var q = (Quaternion)value; return new JArray(q.x, q.y, q.z, q.w); }
            if (value is float) return new JValue((float)value);
            return null;
        }
        private static object ParseValue(JToken value, object sample)
        {
            if (sample is Vector3) return SceneCommands.Vector(value);
            if (sample is float) return SceneCommands.Number(value);
            if (sample is Quaternion)
            {
                var array = value as JArray;
                if (array != null && array.Count == 3) return Quaternion.Euler(SceneCommands.Vector(array));
                if (array == null || array.Count != 4) throw new ArgumentException("Rotation requires Euler degrees [x,y,z] or quaternion [x,y,z,w].");
                var q = new Quaternion(SceneCommands.Number(array[0]), SceneCommands.Number(array[1]), SceneCommands.Number(array[2]), SceneCommands.Number(array[3]));
                var norm = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                if (norm < 0.00001f) throw new ArgumentException("A zero quaternion is invalid.");
                return new Quaternion(q.x / norm, q.y / norm, q.z / norm, q.w / norm);
            }
            throw new NotSupportedException("Unsupported track value type.");
        }
        private static JObject DescribeKey(float time, object key)
        {
            var curve = (AnimationCurve)PluginAccess.Get(key, "curve");
            return new JObject { ["time"] = time, ["value"] = JsonValue(PluginAccess.Get(key, "value")),
                ["curveKeyCount"] = curve.length, ["curveTruncated"] = curve.length > 64,
                ["curve"] = new JArray(curve.keys.Take(64).Select(k => new JObject { ["time"] = k.time,
                    ["value"] = k.value, ["inTangent"] = k.inTangent, ["outTangent"] = k.outTangent })) };
        }

        private static IEnumerable<DictionaryEntry> KeyEntries(IDictionary keys)
        {
            foreach (DictionaryEntry key in keys) yield return key;
        }
    }
}
