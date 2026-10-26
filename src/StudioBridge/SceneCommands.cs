using System;
using System.Linq;
using System.IO;
using Newtonsoft.Json.Linq;
using Studio;
using UnityEngine;

namespace StudioMcp.Bridge
{
    internal sealed class SceneCommands
    {
        private readonly Plugin _plugin;
        internal SceneCommands(Plugin plugin) { _plugin = plugin; }

        internal JToken Execute(string method, JObject args)
        {
            if (Studio.Studio.Instance == null) throw new InvalidOperationException("Studio is not ready.");
            switch (method)
            {
                case "scene.snapshot":
                    var offset = Math.Max(0, (int?)args["offset"] ?? 0);
                    var limit = Math.Max(1, Math.Min(200, (int?)args["limit"] ?? 50));
                    var objects = Studio.Studio.Instance.dicObjectCtrl.OrderBy(pair => pair.Key).ToArray();
                    return new JObject { ["sceneSession"] = _plugin.Session, ["total"] = objects.Length,
                        ["objects"] = new JArray(objects.Skip(offset).Take(limit).Select(pair => Describe(pair.Value))),
                        ["camera"] = CameraState() };
                case "scene.add_item":
                    ValidateTransform(args);
                    var item = AddObjectItem.Add((int?)args["group"] ?? 0,
                        (int?)args["category"] ?? 0, (int?)args["itemId"] ?? 0);
                    if (item == null) throw new InvalidOperationException("Studio did not create the item.");
                    if (args["name"] != null) item.treeNodeObject.textName = (string)args["name"];
                    ApplyTransform(item, args);
                    return Describe(item);
                case "scene.add_character":
                    ValidateTransform(args);
                    var path = Path.GetFullPath((string)args["path"] ?? "");
                    if (!File.Exists(path) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("An existing character card PNG is required.");
                    var sex = (string)args["sex"] ?? "female";
                    if (sex != "male" && sex != "female") throw new ArgumentException("sex must be female or male.");
                    OCIChar character = sex == "male" ? (OCIChar)AddObjectMale.Add(path) : AddObjectFemale.Add(path);
                    if (character == null) throw new InvalidOperationException("Studio did not load the character card.");
                    if (args["name"] != null) character.treeNodeObject.textName = (string)args["name"];
                    ApplyTransform(character, args);
                    return Describe(character);
                case "scene.checkpoint":
                    var studio = Studio.Studio.Instance;
                    foreach (var obj in studio.dicObjectCtrl.Values) obj.OnSavePreprocessing();
                    studio.sceneInfo.cameraSaveData = studio.cameraCtrl.Export();
                    var checkpoint = Path.Combine(_plugin.OutputRoot, "scene-" + Guid.NewGuid().ToString("N") + ".png");
                    if (!studio.sceneInfo.Save(checkpoint) || !File.Exists(checkpoint))
                        throw new IOException("Studio failed to write the scene checkpoint.");
                    File.WriteAllText(checkpoint + ".bridge.json", new JObject {
                        ["render"] = _plugin.Rendering.State(),
                        ["displayNames"] = new JArray(studio.dicObjectCtrl.Select(pair => new JObject {
                            ["id"] = pair.Key, ["name"] = pair.Value.treeNodeObject.textName })) }.ToString());
                    return new JObject { ["path"] = checkpoint, ["bytes"] = new FileInfo(checkpoint).Length };
                case "scene.restore":
                    var restore = Path.GetFullPath((string)args["path"] ?? "");
                    if (!restore.StartsWith(Path.GetFullPath(_plugin.OutputRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || !Path.GetFileName(restore).StartsWith("scene-", StringComparison.Ordinal) || !File.Exists(restore))
                        throw new ArgumentException("Only this bridge's scene checkpoints can be restored.");
                    if (!Studio.Studio.Instance.LoadScene(restore)) throw new IOException("Studio rejected the checkpoint.");
                    if (File.Exists(restore + ".bridge.json"))
                    {
                        var metadata = JObject.Parse(File.ReadAllText(restore + ".bridge.json"));
                        _plugin.Rendering.Apply((JObject)metadata["render"]);
                        foreach (var entry in (JArray)metadata["displayNames"])
                        {
                            ObjectCtrlInfo obj;
                            if (Studio.Studio.Instance.dicObjectCtrl.TryGetValue((int)entry["id"], out obj))
                                obj.treeNodeObject.textName = (string)entry["name"];
                        }
                    }
                    return new JObject { ["sceneSession"] = _plugin.Session, ["restored"] = restore, ["reinspectRequired"] = true };
                case "object.set_transform":
                    var target = GetObject((int)args["objectId"]);
                    ApplyTransform(target, args);
                    return Describe(target);
                case "camera.get": return CameraState();
                case "camera.set":
                    ValidateCamera(args);
                    var camera = Studio.Studio.Instance.cameraCtrl;
                    if (args["pivot"] != null) camera.targetPos = Vector(args["pivot"]);
                    if (args["rotation"] != null) camera.cameraAngle = Vector(args["rotation"]);
                    if (args["fov"] != null)
                    {
                        var fov = Number(args["fov"]);
                        if (fov < 1 || fov > 170) throw new ArgumentOutOfRangeException("fov");
                        camera.fieldOfView = fov;
                    }
                    return CameraState();
                default: throw new ArgumentException("Unsupported method: " + method);
            }
        }

        internal ObjectCtrlInfo GetObject(int id)
        {
            ObjectCtrlInfo target;
            if (!Studio.Studio.Instance.dicObjectCtrl.TryGetValue(id, out target))
                throw new ArgumentException("Studio object does not exist: " + id);
            return target;
        }

        internal static GameObject Root(ObjectCtrlInfo target)
        {
            var item = target as OCIItem;
            if (item != null) return item.objectItem;
            var character = target as OCIChar;
            return character != null ? character.charInfo.gameObject : null;
        }

        private static void ApplyTransform(ObjectCtrlInfo target, JObject args)
        {
            ValidateTransform(args);
            var amount = target.objectInfo.changeAmount;
            if (args["position"] != null) amount.pos = Vector(args["position"]);
            if (args["rotation"] != null) amount.rot = Vector(args["rotation"]);
            if (args["scale"] != null) amount.scale = Vector(args["scale"]);
        }

        private static void ValidateTransform(JObject args)
        {
            foreach (var key in new[] { "position", "rotation", "scale" })
                if (args[key] != null) Vector(args[key]);
        }

        private static void ValidateCamera(JObject args)
        {
            if (args["pivot"] != null) Vector(args["pivot"]);
            if (args["rotation"] != null) Vector(args["rotation"]);
            if (args["fov"] != null && (Number(args["fov"]) < 1 || Number(args["fov"]) > 170))
                throw new ArgumentOutOfRangeException("fov");
        }

        internal Action PrepareUndo(string method, JObject args)
        {
            if (method == "camera.set")
            {
                ValidateCamera(args);
                var before = CameraState();
                return () => Execute(method, before);
            }
            if (method != "object.set_transform") throw new ArgumentException("This scene operation is not supported in batches.");
            ValidateTransform(args);
            var target = GetObject((int)args["objectId"]);
            var state = Describe(target);
            return () => { if (!Studio.Studio.Instance.dicObjectCtrl.ContainsKey(target.objectInfo.dicKey))
                    throw new InvalidOperationException("Undo object was deleted."); ApplyTransform(target, state); };
        }

        internal JObject Describe(ObjectCtrlInfo target)
        {
            var amount = target.objectInfo.changeAmount;
            int? parentId = null;
            ObjectCtrlInfo parent;
            var node = target.treeNodeObject;
            if (node?.parent != null && Studio.Studio.Instance.dicInfo.TryGetValue(node.parent, out parent))
                parentId = parent.objectInfo.dicKey;
            var data = new JObject { ["id"] = target.objectInfo.dicKey,
                ["name"] = node?.textName, ["type"] = target.GetType().Name,
                ["parentId"] = parentId, ["position"] = Json(amount.pos),
                ["rotation"] = Json(amount.rot), ["scale"] = Json(amount.scale) };
            var root = Root(target);
            if (target is OCIChar) data["loadEnd"] = ((OCIChar)target).charInfo.loadEnd;
            if (root != null)
            {
                var renderers = root.GetComponentsInChildren<Renderer>(true);
                data["rendererCount"] = renderers.Length;
                if (renderers.Length > 0)
                {
                    var bounds = renderers[0].bounds;
                    foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
                    data["bounds"] = new JObject { ["center"] = Json(bounds.center), ["size"] = Json(bounds.size) };
                }
            }
            return data;
        }

        private static JObject CameraState()
        {
            var camera = Studio.Studio.Instance.cameraCtrl;
            return new JObject { ["pivot"] = Json(camera.targetPos), ["rotation"] = Json(camera.cameraAngle),
                ["fov"] = camera.fieldOfView,
                ["eye"] = Camera.main != null ? Json(Camera.main.transform.position) : null };
        }

        internal static float Number(JToken value)
        {
            if (value == null || (value.Type != JTokenType.Float && value.Type != JTokenType.Integer))
                throw new ArgumentException("A numeric JSON value is required.");
            var number = (float)value;
            if (float.IsNaN(number) || float.IsInfinity(number)) throw new ArgumentException("Values must be finite.");
            return number;
        }

        internal static Vector3 Vector(JToken value)
        {
            var values = value as JArray;
            if (values == null || values.Count != 3) throw new ArgumentException("A three-component vector is required.");
            return new Vector3(Number(values[0]), Number(values[1]), Number(values[2]));
        }

        internal static JArray Json(Vector3 value) => new JArray(value.x, value.y, value.z);
        internal static JArray Json(Color value) => new JArray(value.r, value.g, value.b, value.a);
    }
}
