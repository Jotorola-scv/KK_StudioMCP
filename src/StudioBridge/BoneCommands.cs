// Added 2026-10-29 in the maker-support fork of KK_StudioMCP (AGPL-3.0); see README.md.
using System;
using System.Collections;
using System.Linq;
using KKAPI.Maker;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudioMcp.Bridge
{
    // ABMX (KKABMX) body bone modifiers through reflection, so the bridge still loads without ABMX installed.
    internal sealed class BoneCommands
    {
        private readonly Plugin _plugin;
        internal BoneCommands(Plugin plugin) { _plugin = plugin; }

        internal JToken Execute(string method, JObject args)
        {
            var character = _plugin.Characters.Character((int?)args["objectId"] ?? 0);
            var controller = Controller(character);
            var slot = (int?)args["slot"];
            if (method == "chara.bones_get") return List(character, controller, (string)args["filter"], (bool?)args["allNames"] ?? false, slot);
            if (method != "chara.bones_set") throw new ArgumentException("Unsupported bone method: " + method);
            var bone = (string)args["bone"];
            if (string.IsNullOrEmpty(bone)) throw new ArgumentException("bone is required (see bones_get with allNames).");
            var values = new JObject();
            foreach (var key in new[] { "scale", "position", "rotation" })
                if (args[key] != null) { SceneCommands.Vector(args[key]); values[key] = args[key]; }
            if (args["length"] != null) values["length"] = SceneCommands.Number(args["length"]);
            if (!values.HasValues) throw new ArgumentException("Give scale, position, rotation or length.");
            if (args["scale"] != null && Vector3Abs(SceneCommands.Vector(args["scale"])) > 20)
                throw new ArgumentOutOfRangeException("scale");
            Set(character, controller, bone, values, slot);
            return List(character, controller, bone, false, slot);
        }

        internal Action PrepareUndo(JObject args)
        {
            var character = _plugin.Characters.Character((int?)args["objectId"] ?? 0);
            var controller = Controller(character);
            var bone = (string)args["bone"];
            var slot = (int?)args["slot"];
            var data = Data(character, controller, bone, false, slot);
            JObject saved = data == null ? new JObject { ["scale"] = new JArray(1, 1, 1), ["position"] = new JArray(0, 0, 0),
                ["rotation"] = new JArray(0, 0, 0), ["length"] = 1 } : Describe(data);
            return () => Set(character, controller, bone, saved, slot);
        }

        private static float Vector3Abs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));

        private static Component Controller(ChaControl character)
        {
            var type = MaterialCommands.FindType("KKABMX.Core.BoneController") ?? throw new NotSupportedException("ABMX is not installed.");
            return character.GetComponent(type) ?? throw new InvalidOperationException("ABMX controller is not ready on this character.");
        }

        // ABMX addresses accessory bones as Accessory0 + slot, past the named enum values for MoreAccessories slots.
        private static object Location(int? slot)
        {
            var type = MaterialCommands.FindType("KKABMX.Core.BoneLocation");
            if (slot == null) return Enum.Parse(type, "BodyTop");
            if (slot < 0) throw new ArgumentOutOfRangeException("slot");
            return Enum.ToObject(type, Convert.ToInt32(Enum.Parse(type, "Accessory0")) + slot.Value);
        }

        // A coordinate-specific modifier keeps one data block per outfit; otherwise block 0 applies to all outfits.
        private static object Data(ChaControl character, Component controller, string bone, bool create, int? slot)
        {
            var location = Location(slot);
            var modifier = create
                ? controller.GetType().GetMethod("GetOrAddModifier").Invoke(controller, new[] { bone, location })
                : controller.GetType().GetMethod("GetModifier", new[] { typeof(string), location.GetType() }).Invoke(controller, new[] { bone, location });
            if (modifier == null) return null;
            var blocks = (Array)modifier.GetType().GetProperty("CoordinateModifiers").GetValue(modifier, null);
            var specific = (bool)modifier.GetType().GetMethod("IsCoordinateSpecific").Invoke(modifier, null);
            var index = specific ? Mathf.Clamp(character.fileStatus.coordinateType, 0, blocks.Length - 1) : 0;
            return blocks.GetValue(index);
        }

        private static void Set(ChaControl character, Component controller, string bone, JObject values, int? slot)
        {
            if (slot != null)
            {
                var objects = character.GetAccessoryObjects();
                if (slot < 0 || slot >= objects.Length || objects[slot.Value] == null) throw new ArgumentException("Accessory slot " + slot + " is empty.");
                if (!objects[slot.Value].GetComponentsInChildren<Transform>(true).Any(t => t.name == bone))
                    throw new ArgumentException("Accessory slot " + slot + " has no bone " + bone + " (see bones_get with slot and allNames).");
            }
            var data = Data(character, controller, bone, true, slot) ?? throw new ArgumentException("ABMX cannot modify bone " + bone + ".");
            var type = data.GetType();
            if (values["scale"] != null) type.GetField("ScaleModifier").SetValue(data, SceneCommands.Vector(values["scale"]));
            if (values["position"] != null) type.GetField("PositionModifier").SetValue(data, SceneCommands.Vector(values["position"]));
            if (values["rotation"] != null) type.GetField("RotationModifier").SetValue(data, SceneCommands.Vector(values["rotation"]));
            if (values["length"] != null) type.GetField("LengthModifier").SetValue(data, SceneCommands.Number(values["length"]));
            controller.GetType().GetProperty("NeedsFullRefresh").SetValue(controller, true, null);
        }

        private static JObject Describe(object data)
        {
            var type = data.GetType();
            return new JObject { ["scale"] = SceneCommands.Json((Vector3)type.GetField("ScaleModifier").GetValue(data)),
                ["position"] = SceneCommands.Json((Vector3)type.GetField("PositionModifier").GetValue(data)),
                ["rotation"] = SceneCommands.Json((Vector3)type.GetField("RotationModifier").GetValue(data)),
                ["length"] = (float)type.GetField("LengthModifier").GetValue(data) };
        }

        private JObject List(ChaControl character, Component controller, string filter, bool allNames, int? slot)
        {
            var wanted = Location(slot).ToString();
            var modifiers = new JArray();
            foreach (var modifier in (IEnumerable)controller.GetType().GetMethod("GetAllModifiers", Type.EmptyTypes).Invoke(controller, null))
            {
                var name = (string)modifier.GetType().GetProperty("BoneName").GetValue(modifier, null);
                if (!string.IsNullOrEmpty(filter) && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if ((bool)modifier.GetType().GetMethod("IsEmpty").Invoke(modifier, null)) continue;
                var location = modifier.GetType().GetProperty("BoneLocation").GetValue(modifier, null).ToString();
                if (slot != null && location != wanted) continue;
                var data = location == wanted ? Data(character, controller, name, false, slot) : null;
                var entry = new JObject { ["bone"] = name, ["location"] = location,
                    ["coordinateSpecific"] = (bool)modifier.GetType().GetMethod("IsCoordinateSpecific").Invoke(modifier, null) };
                if (data != null) entry.Merge(Describe(data));
                modifiers.Add(entry);
            }
            var result = new JObject { ["sceneSession"] = _plugin.Session, ["location"] = wanted, ["modifiers"] = modifiers };
            if (allNames)
            {
                IEnumerable names;
                if (slot == null) names = (IEnumerable)controller.GetType().GetMethod("GetAllPossibleBoneNames", Type.EmptyTypes).Invoke(controller, null);
                else
                {
                    var objects = character.GetAccessoryObjects();
                    if (slot < 0 || slot >= objects.Length || objects[slot.Value] == null) throw new ArgumentException("Accessory slot " + slot + " is empty.");
                    // The accessory's own transforms, root first: the tail is usually the end of a chain.
                    names = objects[slot.Value].GetComponentsInChildren<Transform>(true).Select(t => t.name).ToArray();
                }
                result["boneNames"] = new JArray(names.Cast<string>().Where(n => string.IsNullOrEmpty(filter) || n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).Take(400));
            }
            return result;
        }
    }
}
