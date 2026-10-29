// Added 2026-10-29 in the maker-support fork of KK_StudioMCP (AGPL-3.0); see README.md.
using System;
using KKAPI.Maker;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudioMcp.Bridge
{
    // Main-game character maker: one ChaControl, the maker orbit camera, and runtime clothing states.
    internal sealed class MakerCommands
    {
        private static readonly string[] ClothesKinds = { "top", "bottom", "bra", "shorts", "gloves", "pantyhose", "socks", "shoes_inner", "shoes_outer" };
        private readonly Plugin _plugin;
        internal MakerCommands(Plugin plugin) { _plugin = plugin; }

        internal static ChaControl Character()
        {
            if (!MakerAPI.InsideAndLoaded) throw new InvalidOperationException("Character maker is not open.");
            var character = MakerAPI.GetCharacterControl();
            if (character == null || !character.loadEnd) throw new InvalidOperationException("Maker character is still loading.");
            return character;
        }

        internal JToken Execute(string method, JObject args)
        {
            switch (method)
            {
                case "maker.snapshot": return Snapshot();
                case "maker.clothes_state":
                    var character = Character();
                    var kind = ClothesKind(args["kind"]);
                    if (args["state"] == null || args["state"].Type != JTokenType.Integer || (int)args["state"] < 0 || (int)args["state"] > 3)
                        throw new ArgumentException("state requires 0 (on), 1 (half), 2 (half 2) or 3 (off).");
                    character.SetClothesState(kind, (byte)(int)args["state"], false);
                    return Clothes(character);
                case "camera.get": return CameraState();
                case "camera.set":
                    ValidateCamera(args);
                    var camera = CameraControl();
                    if (args["pivot"] != null) camera.TargetPos = SceneCommands.Vector(args["pivot"]);
                    if (args["rotation"] != null) camera.CameraAngle = SceneCommands.Vector(args["rotation"]);
                    if (args["distance"] != null) camera.CameraDir = SceneCommands.Vector(args["distance"]);
                    if (args["fov"] != null) camera.CameraFov = SceneCommands.Number(args["fov"]);
                    CaptureCommands.LastCameraMove = Time.unscaledTime;
                    return CameraState();
                default: throw new NotSupportedException(method + " is Studio-only; it is unavailable in the character maker.");
            }
        }

        internal Action PrepareUndo(string method, JObject args)
        {
            if (method != "camera.set") throw new ArgumentException("Maker batches support me.set and camera.set only.");
            ValidateCamera(args);
            var before = CameraState();
            return () => Execute(method, before);
        }

        private JObject Snapshot()
        {
            var character = Character();
            var accessories = new JArray();
            var objects = character.GetAccessoryObjects();
            var parts = character.nowCoordinate.accessory.parts;
            for (var i = 0; i < objects.Length; i++)
            {
                if (objects[i] == null) continue;
                var entry = new JObject { ["slot"] = i, ["object"] = objects[i].name,
                    ["rendererCount"] = objects[i].GetComponentsInChildren<Renderer>(true).Length };
                if (i < parts.Length) { entry["type"] = parts[i].type; entry["id"] = parts[i].id; entry["parent"] = parts[i].parentKey; }
                if (i < character.infoAccessory.Length && character.infoAccessory[i] != null) entry["name"] = character.infoAccessory[i].Name;
                accessories.Add(entry);
            }
            var hair = new JArray();
            for (var i = 0; i < character.objHair.Length; i++)
                if (character.objHair[i] != null) hair.Add(new JObject { ["slot"] = i, ["object"] = character.objHair[i].name,
                    ["id"] = character.fileHair.parts[i].id, ["name"] = character.infoHair[i]?.Name });
            return new JObject { ["sceneSession"] = _plugin.Session, ["objectId"] = 0,
                ["name"] = character.fileParam.fullname, ["sex"] = character.sex,
                ["coordinateType"] = character.fileStatus.coordinateType,
                ["rendererCount"] = character.GetComponentsInChildren<Renderer>(true).Length,
                ["clothes"] = Clothes(character), ["hair"] = hair, ["accessories"] = accessories, ["camera"] = CameraState() };
        }

        private static JArray Clothes(ChaControl character)
        {
            var clothes = new JArray();
            for (var i = 0; i < character.objClothes.Length; i++)
            {
                var part = character.objClothes[i];
                clothes.Add(new JObject { ["slot"] = i, ["kind"] = i < ClothesKinds.Length ? ClothesKinds[i] : "slot" + i,
                    ["id"] = character.nowCoordinate.clothes.parts[i].id, ["name"] = character.infoClothes[i]?.Name,
                    ["object"] = part?.name, ["state"] = character.fileStatus.clothesState[i],
                    ["rendererCount"] = part == null ? 0 : part.GetComponentsInChildren<Renderer>(true).Length });
            }
            return clothes;
        }

        private static int ClothesKind(JToken value)
        {
            if (value != null && value.Type == JTokenType.Integer && (int)value >= 0 && (int)value < ClothesKinds.Length) return (int)value;
            var index = value != null && value.Type == JTokenType.String ? Array.IndexOf(ClothesKinds, (string)value) : -1;
            if (index < 0) throw new ArgumentException("kind requires a slot 0-8 or one of: " + string.Join(", ", ClothesKinds));
            return index;
        }

        private static BaseCameraControl_Ver2 CameraControl()
        {
            var camera = Camera.main != null ? Camera.main.GetComponent<BaseCameraControl_Ver2>() : null;
            if (camera == null) throw new InvalidOperationException("Maker camera controller is unavailable.");
            return camera;
        }

        private static JObject CameraState()
        {
            var camera = CameraControl();
            return new JObject { ["pivot"] = SceneCommands.Json(camera.TargetPos), ["rotation"] = SceneCommands.Json(camera.CameraAngle),
                ["fov"] = camera.CameraFov, ["distance"] = SceneCommands.Json(camera.CameraDir),
                ["eye"] = SceneCommands.Json(Camera.main.transform.position) };
        }

        private static void ValidateCamera(JObject args)
        {
            foreach (var key in new[] { "pivot", "rotation", "distance" })
                if (args[key] != null) SceneCommands.Vector(args[key]);
            if (args["distance"] != null)
            {
                var distance = SceneCommands.Vector(args["distance"]).magnitude;
                if (distance < 0.01f || distance > 1000) throw new ArgumentOutOfRangeException("distance");
            }
            if (args["fov"] != null && (SceneCommands.Number(args["fov"]) < 1 || SceneCommands.Number(args["fov"]) > 170))
                throw new ArgumentOutOfRangeException("fov");
        }
    }
}
