// Added 2026-10-29 in the maker-support fork of KK_StudioMCP (AGPL-3.0); see README.md.
using System;
using System.Collections;
using System.Linq;
using KKAPI.Maker;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudioMcp.Bridge
{
    // Expressions, maker poses and accessory DynamicBone settings, for checking hair and clothes in motion.
    internal sealed class MotionCommands
    {
        // DynamicBone Editor setters, keyed by the DynamicBone field they persist.
        private static readonly string[][] BoneSettings = {
            new[] { "damping", "Damping", "m_Damping" }, new[] { "elasticity", "Elasticity", "m_Elasticity" },
            new[] { "stiffness", "Stiffness", "m_Stiffness" }, new[] { "inertia", "Inertia", "m_Inert" },
            new[] { "radius", "Radius", "m_Radius" }, new[] { "weight", "Weight", "m_Weight" } };
        private readonly Plugin _plugin;
        internal MotionCommands(Plugin plugin) { _plugin = plugin; }

        internal JToken Execute(string method, JObject args)
        {
            switch (method)
            {
                case "maker.pose_list": return Poses();
                case "maker.pose_set": return SetPose(args);
            }
            var character = _plugin.Characters.Character((int?)args["objectId"] ?? 0);
            switch (method)
            {
                case "chara.expression_get": return Expression(character);
                case "chara.expression_set":
                    foreach (var key in new[] { "eyes", "mouth", "eyebrow" })
                        if (args[key] != null && (args[key].Type != JTokenType.Integer || (int)args[key] < 0)) throw new ArgumentException(key + " requires a pattern index.");
                    foreach (var key in new[] { "eyesOpen", "mouthOpen" })
                        if (args[key] != null && (SceneCommands.Number(args[key]) < 0 || SceneCommands.Number(args[key]) > 1)) throw new ArgumentOutOfRangeException(key, "Use 0..1.");
                    if (args["eyes"] != null) character.ChangeEyesPtn((int)args["eyes"], false);
                    if (args["mouth"] != null) character.ChangeMouthPtn((int)args["mouth"], false);
                    if (args["eyebrow"] != null) character.ChangeEyebrowPtn((int)args["eyebrow"], false);
                    if (args["eyesOpen"] != null) character.ChangeEyesOpenMax(SceneCommands.Number(args["eyesOpen"]));
                    if (args["mouthOpen"] != null) character.ChangeMouthOpenMax(SceneCommands.Number(args["mouthOpen"]));
                    if (args["blink"] != null) character.ChangeEyesBlinkFlag((bool)args["blink"]);
                    return Expression(character);
                case "chara.dynamic_bones": return DynamicBones(character, (int?)args["slot"]);
                case "chara.dynamic_bone_set": return SetDynamicBone(character, args);
                default: throw new ArgumentException("Unsupported motion method: " + method);
            }
        }

        private static JObject Expression(ChaControl character)
        {
            var status = character.fileStatus;
            var result = new JObject { ["eyes"] = status.eyesPtn, ["mouth"] = status.mouthPtn, ["eyebrow"] = status.eyebrowPtn,
                ["eyesOpen"] = status.eyesOpenMax, ["mouthOpen"] = status.mouthOpenMax, ["blink"] = status.eyesBlink };
            // The maker knows the pattern names; Studio and the main game only have indices.
            if (!Plugin.InStudio && MakerAPI.InsideAndLoaded)
            {
                var maker = MakerAPI.GetMakerBase();
                result["eyesPatterns"] = Patterns(maker.lstEye);
                result["mouthPatterns"] = Patterns(maker.lstMouth);
                result["eyebrowPatterns"] = Patterns(maker.lstEyebrow);
            }
            return result;
        }

        private static JArray Patterns(IEnumerable list)
        {
            var result = new JArray();
            foreach (var item in list) result.Add(new JObject { ["id"] = JToken.FromObject(Member(item, "ID")), ["name"] = (string)Member(item, "Name") });
            return result;
        }

        private static JObject Poses()
        {
            var maker = Maker();
            var result = new JArray();
            var index = 0;
            foreach (var pose in (IEnumerable)Member(maker, "lstPose"))
                result.Add(new JObject { ["index"] = index++, ["state"] = (string)Member(pose, "State"), ["asset"] = (string)Member(pose, "Asset") });
            return new JObject { ["current"] = maker.animeStateName, ["poses"] = result };
        }

        private static JObject SetPose(JObject args)
        {
            var maker = Maker();
            var count = ((IList)Member(maker, "lstPose")).Count;
            if (args["index"] == null || args["index"].Type != JTokenType.Integer || (int)args["index"] < -1 || (int)args["index"] >= count)
                throw new ArgumentException("index requires a pose index from pose_list (0-" + (count - 1) + "), or -1 for the default pose.");
            var time = args["time"] == null ? 0f : SceneCommands.Number(args["time"]);
            if (time < 0 || time > 1) throw new ArgumentOutOfRangeException("time", "Use a normalized 0..1 animation time.");
            var draw = UnityEngine.Object.FindObjectOfType<ChaCustom.CvsDrawCtrl>() ?? throw new InvalidOperationException("Maker pose panel is unavailable.");
            if ((int)args["index"] < 0) { draw.ChangePoseDefault(); return Poses(); }
            draw.ChangeAnimationForce((int)args["index"], time);
            // Keep the maker's pose dropdown in step without re-triggering its change handler.
            var dropdown = draw.GetType().GetField("ddPose").GetValue(draw);
            dropdown?.GetType().GetMethod("SetValueWithoutNotify")?.Invoke(dropdown, new object[] { (int)args["index"] });
            return Poses();
        }

        private static ChaCustom.CustomBase Maker()
        {
            if (Plugin.InStudio || !MakerAPI.InsideAndLoaded) throw new InvalidOperationException("Poses are available in the character maker only.");
            return MakerAPI.GetMakerBase();
        }

        private static JObject DynamicBones(ChaControl character, int? only)
        {
            var type = MaterialCommands.FindType("DynamicBone") ?? throw new NotSupportedException("DynamicBone is unavailable.");
            var objects = character.GetAccessoryObjects();
            var result = new JArray();
            for (var slot = 0; slot < objects.Length; slot++)
            {
                if (objects[slot] == null || (only != null && only != slot)) continue;
                var bones = objects[slot].GetComponentsInChildren(type, true);
                for (var i = 0; i < bones.Length; i++)
                {
                    var entry = new JObject { ["slot"] = slot, ["index"] = i, ["root"] = (type.GetField("m_Root").GetValue(bones[i]) as Transform)?.name,
                        ["enabled"] = ((Behaviour)bones[i]).enabled };
                    foreach (var setting in BoneSettings)
                    {
                        var field = type.GetField(setting[2]);
                        if (field != null) entry[setting[0]] = (float)field.GetValue(bones[i]);
                    }
                    result.Add(entry);
                }
            }
            return new JObject { ["editor"] = Editor(character) != null, ["bones"] = result };
        }

        private JObject SetDynamicBone(ChaControl character, JObject args)
        {
            var editor = Editor(character) ?? throw new NotSupportedException("DynamicBone Editor is not installed; it is what saves these values to the card.");
            var type = MaterialCommands.FindType("DynamicBone");
            var slot = (int?)args["slot"] ?? throw new ArgumentException("slot is required.");
            var objects = character.GetAccessoryObjects();
            if (slot < 0 || slot >= objects.Length || objects[slot] == null) throw new ArgumentException("Accessory slot " + slot + " is empty.");
            var bones = objects[slot].GetComponentsInChildren(type, true);
            var index = (int?)args["index"] ?? 0;
            if (index < 0 || index >= bones.Length) throw new ArgumentOutOfRangeException("index", "Slot " + slot + " has " + bones.Length + " dynamic bones.");
            var changed = 0;
            foreach (var setting in BoneSettings)
            {
                if (args[setting[0]] == null) continue;
                var value = SceneCommands.Number(args[setting[0]]);
                if (value < 0 || value > (setting[0] == "radius" ? 10 : 1)) throw new ArgumentOutOfRangeException(setting[0]);
                var setter = editor.GetType().GetMethod("Set" + setting[1], new[] { typeof(int), type, typeof(float) })
                    ?? throw new NotSupportedException("DynamicBone Editor lacks Set" + setting[1] + ".");
                setter.Invoke(editor, new object[] { slot, bones[index], value });
                changed++;
            }
            if (changed == 0) throw new ArgumentException("Give at least one of damping, elasticity, stiffness, inertia, radius, weight.");
            return DynamicBones(character, slot);
        }

        private static Component Editor(ChaControl character)
        {
            var type = MaterialCommands.FindType("KK_Plugins.DynamicBoneEditor.CharaController");
            return type == null ? null : character.GetComponent(type);
        }

        private static object Member(object target, string name)
        {
            var type = target.GetType();
            var property = type.GetProperty(name);
            if (property != null) return property.GetValue(target, null);
            var field = type.GetField(name) ?? throw new MissingMemberException(type.Name, name);
            return field.GetValue(target);
        }
    }
}
