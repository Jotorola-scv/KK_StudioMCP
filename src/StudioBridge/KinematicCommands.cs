using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Studio;
using UnityEngine;

namespace StudioMcp.Bridge
{
    internal sealed class KinematicCommands
    {
        private readonly Plugin _plugin;
        private static readonly OIBoneInfo.BoneGroup[] IkParts = { OIBoneInfo.BoneGroup.Body,
            OIBoneInfo.BoneGroup.RightLeg, OIBoneInfo.BoneGroup.LeftLeg,
            OIBoneInfo.BoneGroup.RightArm, OIBoneInfo.BoneGroup.LeftArm };
        internal KinematicCommands(Plugin plugin) { _plugin = plugin; }

        internal OCIChar GetCharacter(int objectId)
        {
            var character = _plugin.Scene.GetObject(objectId) as OCIChar;
            if (character == null) throw new ArgumentException("The object is not a Studio character.");
            return character;
        }

        internal JToken Execute(string method, JObject args)
        {
            var character = GetCharacter((int)args["objectId"]);
            if (method == "character.state") return State(character);
            var mode = Mode((string)args["mode"]);
            if (method == "character.kinematics")
            {
                if (args["enabled"] == null || args["enabled"].Type != JTokenType.Boolean)
                    throw new ArgumentException("enabled requires a boolean.");
                var enabled = (bool)args["enabled"];
                if (args["group"] == null)
                    character.ActiveKinematicMode(mode, enabled, false);
                else
                {
                    var name = (string)args["group"];
                    var groups = mode == OICharInfo.KinematicMode.FK ? FKCtrl.parts : IkParts;
                    var matches = groups.Where(g => g.ToString() == name).ToArray();
                    if (matches.Length != 1) throw new ArgumentException("Use a group returned by character_state.");
                    if (mode == OICharInfo.KinematicMode.FK) character.ActiveFK(matches[0], enabled, false);
                    else character.ActiveIK(matches[0], enabled, false);
                }
                return State(character);
            }
            if (method == "character.controls")
            {
                var controls = mode == OICharInfo.KinematicMode.FK
                    ? character.listBones.Select(b => Describe(b.guideObject, b.boneGroup, b.active, b.boneID))
                    : character.listIKTarget.Select(b => Describe(b.guideObject, b.boneGroup, b.active, null));
                var offset = Math.Max(0, (int?)args["offset"] ?? 0);
                var limit = Math.Max(1, Math.Min(200, (int?)args["limit"] ?? 50));
                return new JObject { ["objectId"] = character.objectInfo.dicKey,
                    ["mode"] = mode.ToString(), ["space"] = "guide-local", ["rotationUnit"] = "degrees",
                    ["total"] = controls.Count(), ["controls"] = new JArray(controls.Skip(offset).Take(limit)) };
            }
            if (method != "character.control_set") throw new ArgumentException("Unknown character operation.");
            var id = (int)args["controlId"];
            var guide = Control(character, mode, id);
            Vector3? position = args["position"] == null ? (Vector3?)null : SceneCommands.Vector(args["position"]);
            Vector3? rotation = args["rotation"] == null ? (Vector3?)null : SceneCommands.Vector(args["rotation"]);
            if (!position.HasValue && !rotation.HasValue) throw new ArgumentException("Provide position or rotation.");
            var space = (string)args["space"] ?? "local";
            if (space != "local" && space != "world") throw new ArgumentException("space must be local or world.");
            if (mode == OICharInfo.KinematicMode.FK)
            {
                if (position.HasValue) throw new ArgumentException("Native FK controls accept rotation only.");
                if (!character.oiCharInfo.enableFK || !character.listBones.First(b => b.guideObject == guide).active)
                    throw new InvalidOperationException("Enable FK and this control's group before setting its rotation.");
            }
            else if (!character.oiCharInfo.enableIK || !character.listIKTarget.First(b => b.guideObject == guide).active)
                throw new InvalidOperationException("Enable IK and this control's group before setting its target.");
            if (position.HasValue && !guide.enablePos) throw new ArgumentException("This control has no position channel.");
            if (rotation.HasValue && !guide.enableRot) throw new ArgumentException("This control has no rotation channel.");
            if (position.HasValue)
            {
                if (space == "world") guide.SetWorldPos(position.Value);
                else guide.changeAmount.pos = position.Value;
            }
            if (rotation.HasValue)
            {
                var value = Quaternion.Euler(rotation.Value);
                if (space == "world" && guide.transformTarget.parent != null)
                    value = Quaternion.Inverse(guide.transformTarget.parent.rotation) * value;
                guide.changeAmount.rot = space == "local" ? rotation.Value : value.eulerAngles;
            }
            return new JObject { ["objectId"] = character.objectInfo.dicKey, ["mode"] = mode.ToString(),
                ["controlId"] = id, ["position"] = SceneCommands.Json(guide.changeAmount.pos),
                ["rotation"] = SceneCommands.Json(guide.changeAmount.rot), ["space"] = "guide-local" };
        }

        internal GuideObject Control(int objectId, string mode, int controlId)
            => Control(GetCharacter(objectId), Mode(mode), controlId);

        private static GuideObject Control(OCIChar character, OICharInfo.KinematicMode mode, int id)
        {
            var controls = mode == OICharInfo.KinematicMode.FK
                ? character.listBones.Select(b => b.guideObject) : character.listIKTarget.Select(b => b.guideObject);
            var guide = controls.FirstOrDefault(g => g != null && g.dicKey == id);
            if (guide == null) throw new ArgumentException("Control is stale or does not belong to this character; inspect controls again.");
            return guide;
        }

        private static OICharInfo.KinematicMode Mode(string mode)
        {
            if (mode == "FK") return OICharInfo.KinematicMode.FK;
            if (mode == "IK") return OICharInfo.KinematicMode.IK;
            throw new ArgumentException("mode must be FK or IK.");
        }

        private static JObject State(OCIChar character)
        {
            return new JObject { ["objectId"] = character.objectInfo.dicKey,
                ["fkEnabled"] = character.oiCharInfo.enableFK, ["ikEnabled"] = character.oiCharInfo.enableIK,
                ["fkGroups"] = GroupStates(FKCtrl.parts, character.oiCharInfo.activeFK, character.oiCharInfo.enableFK),
                ["ikGroups"] = GroupStates(IkParts, character.oiCharInfo.activeIK, character.oiCharInfo.enableIK),
                ["fkControlCount"] = character.listBones.Count, ["ikControlCount"] = character.listIKTarget.Count };
        }

        private static JObject Describe(GuideObject guide, OIBoneInfo.BoneGroup group, bool active, int? boneId)
        {
            return new JObject { ["controlId"] = guide.dicKey, ["boneId"] = boneId,
                ["name"] = guide.transformTarget.name, ["group"] = string.Join("|", GroupNames(group)),
                ["groups"] = new JArray(GroupNames(group)), ["active"] = active,
                ["canPosition"] = guide.enablePos, ["canRotation"] = guide.enableRot,
                ["position"] = SceneCommands.Json(guide.changeAmount.pos),
                ["rotation"] = SceneCommands.Json(guide.changeAmount.rot),
                ["worldPosition"] = SceneCommands.Json(guide.transformTarget.position) };
        }

        private static string[] GroupNames(OIBoneInfo.BoneGroup group) => Enum.GetValues(typeof(OIBoneInfo.BoneGroup))
            .Cast<OIBoneInfo.BoneGroup>().Where(g => (g & group) != 0).Select(g => g.ToString()).ToArray();

        private static JArray GroupStates(OIBoneInfo.BoneGroup[] groups, bool[] flags, bool enabled) =>
            new JArray(groups.Select((g, i) => new JObject { ["group"] = g.ToString(),
                ["configured"] = i < flags.Length && flags[i], ["active"] = enabled && i < flags.Length && flags[i] }));
    }
}
