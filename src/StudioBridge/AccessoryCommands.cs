// Added 2026-10-29 in the maker-support fork of KK_StudioMCP (AGPL-3.0); see README.md.
using System;
using System.Collections.Generic;
using System.Linq;
using KKAPI.Maker;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudioMcp.Bridge
{
    // Accessory placement, parent, colors and Hair Accessory Customizer values: the knobs for spliced hair.
    internal sealed class AccessoryCommands
    {
        private static readonly string[] HairFields = { "colorMatch", "hairGloss", "accessoryColor", "outlineColor", "glossColor", "length" };
        private readonly Plugin _plugin;
        internal AccessoryCommands(Plugin plugin) { _plugin = plugin; }

        internal JToken Execute(string method, JObject args)
        {
            var character = _plugin.Characters.Character((int?)args["objectId"] ?? 0);
            if (method == "chara.accessory_list")
                return new JObject { ["sceneSession"] = _plugin.Session, ["coordinateType"] = character.fileStatus.coordinateType,
                    ["parents"] = new JArray(ParentKeys()), ["accessories"] = new JArray(Slots(character)
                        .Where(i => args["slot"] == null || (int)args["slot"] == i).Select(i => Describe(character, i))) };
            if (method != "chara.accessory_set") throw new ArgumentException("Unsupported accessory method: " + method);
            var slot = Slot(character, args);
            Apply(character, slot, args);
            return Describe(character, slot);
        }

        internal Action PrepareUndo(JObject args)
        {
            var character = _plugin.Characters.Character((int?)args["objectId"] ?? 0);
            var slot = Slot(character, args);
            var saved = Describe(character, slot);
            var restore = new JObject { ["slot"] = slot, ["parent"] = saved["parent"], ["colors"] = saved["colors"] };
            for (var c = 0; c < 2; c++) restore["move" + c] = saved["moves"][c];
            if (saved["hair"] is JObject hair) restore["hair"] = hair;
            return () => Apply(character, slot, restore);
        }

        private static IEnumerable<int> Slots(ChaControl character)
        {
            var objects = character.GetAccessoryObjects();
            var parts = character.nowCoordinate.accessory.parts;
            for (var i = 0; i < Math.Min(objects.Length, parts.Length); i++)
                if (objects[i] != null && parts[i].type != 120) yield return i;
        }

        private static int Slot(ChaControl character, JObject args)
        {
            if (args["slot"] == null || args["slot"].Type != JTokenType.Integer) throw new ArgumentException("slot requires an integer.");
            var slot = (int)args["slot"];
            if (!Slots(character).Contains(slot)) throw new ArgumentException("Accessory slot " + slot + " is empty or does not exist.");
            return slot;
        }

        private JObject Describe(ChaControl character, int slot)
        {
            var part = character.nowCoordinate.accessory.parts[slot];
            var obj = character.GetAccessoryObjects()[slot];
#if KK
            var info = character.lstCtrl.GetListInfo((ChaListDefine.CategoryNo)part.type, part.id);
#else
            var info = character.lstCtrl.GetInfo((ChaListDefine.CategoryNo)part.type, part.id);
#endif
            var moves = new JArray();
            for (var c = 0; c < 2; c++)
                moves.Add(new JObject { ["position"] = SceneCommands.Json(part.addMove[c, 0]),
                    ["rotation"] = SceneCommands.Json(part.addMove[c, 1]), ["scale"] = SceneCommands.Json(part.addMove[c, 2]) });
            var entry = new JObject { ["slot"] = slot, ["type"] = part.type, ["id"] = part.id, ["name"] = info?.Name,
                ["parent"] = part.parentKey, ["moves"] = moves, ["movable"] = info == null || info.GetInfoInt((ChaListDefine.KeyType)24) != 1,
                ["colors"] = new JArray(part.color.Select(c => (JToken)SceneCommands.Json(c))), ["noShake"] = part.noShake,
                ["visible"] = obj.activeSelf, ["isHair"] = obj.GetComponent<ChaCustomHairComponent>() != null,
                ["rendererCount"] = obj.GetComponentsInChildren<Renderer>(true).Length };
            var hair = HairController(character);
            if (hair != null && (bool)Call(hair, "IsHairAccessory", slot))
                entry["hair"] = new JObject { ["colorMatch"] = (bool)Call(hair, "GetColorMatch", slot),
                    ["hairGloss"] = (bool)Call(hair, "GetHairGloss", slot),
                    ["accessoryColor"] = SceneCommands.Json((Color)Call(hair, "GetAccessoryColor", slot)),
                    ["outlineColor"] = SceneCommands.Json((Color)Call(hair, "GetOutlineColor", slot)),
                    ["glossColor"] = SceneCommands.Json((Color)Call(hair, "GetGlossColor", slot)),
                    ["length"] = (float)Call(hair, "GetHairLength", slot) };
            return entry;
        }

        // Validates everything first, then writes the live coordinate and the card's copy of it, like the maker's accessory panel.
        private void Apply(ChaControl character, int slot, JObject args)
        {
            var moves = new Dictionary<int, JObject>();
            for (var c = 0; c < 2; c++)
            {
                var token = args["move" + c] ?? (c == ((int?)args["correction"] ?? 0) ? Pick(args, "position", "rotation", "scale") : null);
                if (token == null) continue;
                var move = token as JObject ?? throw new ArgumentException("move" + c + " requires {position, rotation, scale}.");
                foreach (var key in new[] { "position", "rotation", "scale" })
                    if (move[key] != null && move[key].Type != JTokenType.Null) SceneCommands.Vector(move[key]);
                moves[c] = move;
            }
            var add = (bool?)args["add"] ?? false;
            var parent = (string)args["parent"];
            if (parent != null && !ParentKeys().Contains(parent)) throw new ArgumentException("Unknown parent key: " + parent + " (see accessory_list.parents).");
            var colors = args["colors"] as JArray;
            if (args["colors"] != null && (colors == null || colors.Count == 0 || colors.Count > 4)) throw new ArgumentException("colors requires 1-4 RGBA arrays.");
            if (colors != null) foreach (var c in colors) if (c.Type != JTokenType.Null) MaterialCommands.ColorValue(c);
            var hairArgs = args["hair"] as JObject;
            var hair = hairArgs != null ? HairController(character) : null;
            if (hairArgs != null && (hair == null || !(bool)Call(hair, "IsHairAccessory", slot)))
                throw new ArgumentException("hair settings need Hair Accessory Customizer and a hair accessory in this slot.");

            var parts = new List<ChaFileAccessory.PartsInfo> { character.nowCoordinate.accessory.parts[slot] };
            var coordinates = character.chaFile.coordinate;
            var type = character.fileStatus.coordinateType;
            if (type >= 0 && type < coordinates.Length && slot < coordinates[type].accessory.parts.Length)
                parts.Add(coordinates[type].accessory.parts[slot]);

            foreach (var move in moves)
            {
                var keys = new[] { "position", "rotation", "scale" };
                foreach (var part in parts)
                    for (var k = 0; k < 3; k++)
                    {
                        var token = move.Value[keys[k]];
                        if (token == null || token.Type == JTokenType.Null) continue;
                        var value = SceneCommands.Vector(token);
                        part.addMove[move.Key, k] = add ? part.addMove[move.Key, k] + value : value;
                    }
            }
            if (moves.Count > 0) ApplyMoves(character, slot);
            if (parent != null && parent != parts[0].parentKey)
            {
                character.ChangeAccessoryParent(slot, parent);
                if (parts[0].parentKey != parent) throw new NotSupportedException("The game did not re-parent slot " + slot + " (slots past 19 need MoreAccessories).");
                foreach (var part in parts.Skip(1)) part.parentKey = parent;
                ApplyMoves(character, slot);
            }
            if (colors != null)
            {
                foreach (var part in parts)
                    for (var i = 0; i < colors.Count; i++)
                        if (colors[i].Type != JTokenType.Null) part.color[i] = MaterialCommands.ColorValue(colors[i]);
                character.ChangeAccessoryColor(slot);
            }
            if (hairArgs != null)
            {
                foreach (var key in hairArgs.Properties().Select(p => p.Name))
                    if (!HairFields.Contains(key)) throw new ArgumentException("hair keys are: " + string.Join(", ", HairFields));
                if (hairArgs["colorMatch"] != null) Call(hair, "SetColorMatch", (bool)hairArgs["colorMatch"], slot);
                if (hairArgs["hairGloss"] != null) Call(hair, "SetHairGloss", (bool)hairArgs["hairGloss"], slot);
                if (hairArgs["accessoryColor"] != null) Call(hair, "SetAccessoryColor", MaterialCommands.ColorValue(hairArgs["accessoryColor"]), slot);
                if (hairArgs["outlineColor"] != null) Call(hair, "SetOutlineColor", MaterialCommands.ColorValue(hairArgs["outlineColor"]), slot);
                if (hairArgs["glossColor"] != null) Call(hair, "SetGlossColor", MaterialCommands.ColorValue(hairArgs["glossColor"]), slot);
                if (hairArgs["length"] != null) Call(hair, "SetHairLength", SceneCommands.Number(hairArgs["length"]), slot);
                Call(hair, "UpdateAccessory", slot, true);
            }
            RefreshMakerPanel(slot);
        }

        private static JObject Pick(JObject args, params string[] keys)
        {
            if (!keys.Any(k => args[k] != null)) return null;
            var result = new JObject();
            foreach (var key in keys) if (args[key] != null) result[key] = args[key];
            return result;
        }

        // Mirrors ChaControl.UpdateAccessoryMoveFromInfo, which only accepts slots 0-19: N_move/N_move2 local transforms.
        internal static void ApplyMoves(ChaControl character, int slot)
        {
            if (slot < character.objAcsMove.GetLength(0)) { character.UpdateAccessoryMoveFromInfo(slot); return; }
            var part = character.nowCoordinate.accessory.parts[slot];
            var root = character.GetAccessoryObjects()[slot].transform;
            for (var c = 0; c < 2; c++)
            {
                var move = MoveTransform(character, slot, c);
                if (move == null || !move.IsChildOf(root)) continue;
                move.localPosition = part.addMove[c, 0] * 0.01f;
                move.localEulerAngles = part.addMove[c, 1];
                move.localScale = part.addMove[c, 2];
            }
        }

        internal static Transform MoveTransform(ChaControl character, int slot, int correction)
        {
            if (slot < character.objAcsMove.GetLength(0) && character.objAcsMove[slot, correction] != null)
                return character.objAcsMove[slot, correction].transform;
            var name = correction == 0 ? "N_move" : "N_move2";
            return character.GetAccessoryObjects()[slot].GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);
        }

        // The maker caches slider values per accessory panel; re-read them from the character.
        private static void RefreshMakerPanel(int slot)
        {
            if (Plugin.InStudio || !MakerAPI.InsideAndLoaded) return;
            var type = MaterialCommands.FindType("ChaCustom.CvsAccessory");
            if (type == null) return;
            foreach (var panel in UnityEngine.Object.FindObjectsOfType(type))
                try
                {
                    if ((int)type.GetProperty("nSlotNo").GetValue(panel, null) == slot)
                        type.GetMethod("UpdateCustomUI", Type.EmptyTypes)?.Invoke(panel, null);
                }
                catch (Exception error) { UnityEngine.Debug.LogWarning("[StudioMCP] Accessory panel refresh: " + error.GetBaseException().Message); }
        }

        private static string[] _parentKeys;
        private static string[] ParentKeys() => _parentKeys ?? (_parentKeys = Enum.GetNames(typeof(ChaAccessoryDefine.AccessoryParentKey)).Where(n => n != "none").ToArray());

        private static Component HairController(ChaControl character)
        {
            var type = MaterialCommands.FindType("KK_Plugins.HairAccessoryCustomizer+HairAccessoryController");
            return type == null ? null : character.GetComponent(type);
        }

        private static object Call(Component target, string name, params object[] args)
        {
            var method = target.GetType().GetMethods().FirstOrDefault(m => m.Name == name && m.GetParameters().Length == args.Length
                && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(args.Select(a => a.GetType())));
            if (method == null) throw new NotSupportedException("Installed plugin lacks " + target.GetType().Name + "." + name + ".");
            return method.Invoke(target, args);
        }
    }
}
