using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Studio;
using UnityEngine;

namespace StudioMcp.Bridge
{
    internal sealed class PoseEditorCommands
    {
        private readonly Plugin _plugin;
        internal PoseEditorCommands(Plugin plugin) { _plugin = plugin; }
        internal static bool Available => PluginAccess.Type(PluginAccess.PoseEditorId, "HSPE.PoseController") != null;

        internal object Controller(ObjectCtrlInfo target)
        {
            var type = PluginAccess.Type(PluginAccess.PoseEditorId, "HSPE.PoseController");
            if (type == null) throw new NotSupportedException("KKPE is not installed or has no compatible PoseController.");
            var controller = target.guideObject.transformTarget.GetComponent(type);
            if (controller == null || PluginAccess.Get(controller, "_bonesEditor") == null
                || PluginAccess.Get(controller, "_blendShapesEditor") == null)
                throw new InvalidOperationException("KKPE is not ready for this object; inspect again after the character finishes loading.");
            return controller;
        }

        internal JToken Execute(string method, JObject args)
        {
            var write = method == "pose.bone_set" || method == "pose.bone_reset"
                || method == "pose.blendshape_set" || method == "pose.blendshape_reset";
            if (!Available)
            {
                if (write) throw new NotSupportedException("KKPE is not installed.");
                return new JObject { ["available"] = false, ["reason"] = "KKPE is not installed." };
            }
            var target = _plugin.Scene.GetObject((int)args["objectId"]);
            var controller = Controller(target);
            var boneEditor = PluginAccess.Get(controller, "_bonesEditor");
            var offset = Math.Max(0, (int?)args["offset"] ?? 0);
            var limit = Math.Max(1, Math.Min(200, (int?)args["limit"] ?? 50));
            var query = (string)args["query"] ?? "";
            if (method == "pose.bones")
            {
                var bones = OwnedBones(controller).Where(b => b.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                return new JObject { ["available"] = true, ["objectId"] = target.objectInfo.dicKey,
                    ["space"] = "bone-local", ["rotationUnit"] = "degrees", ["total"] = bones.Length,
                    ["bones"] = new JArray(bones.Skip(offset).Take(limit).Select(b => BoneState(controller, boneEditor, b))) };
            }
            if (method == "pose.bone_set" || method == "pose.bone_reset")
            {
                var bone = Bone(target, (int)args["boneId"]);
                if (method == "pose.bone_set")
                {
                    Vector3? position = args["position"] == null ? (Vector3?)null : SceneCommands.Vector(args["position"]);
                    Vector3? rotation = args["rotation"] == null ? (Vector3?)null : SceneCommands.Vector(args["rotation"]);
                    Vector3? scale = args["scale"] == null ? (Vector3?)null : SceneCommands.Vector(args["scale"]);
                    if (!position.HasValue && !rotation.HasValue && !scale.HasValue)
                        throw new ArgumentException("Provide position, rotation or scale.");
                    var fk = rotation.HasValue ? NativeFk(boneEditor, bone) : null;
                    if (position.HasValue) PluginAccess.Call(boneEditor, "SetBonePosition", bone, position.Value);
                    // KKPE's own editor uses the saved native FK channel when that bone is active.
                    if (rotation.HasValue && fk != null && fk.active) fk.guideObject.changeAmount.rot = rotation.Value;
                    else if (rotation.HasValue) PluginAccess.Call(boneEditor, "SetBoneRotation", bone, Quaternion.Euler(rotation.Value));
                    if (scale.HasValue) PluginAccess.Call(boneEditor, "SetBoneScale", bone, scale.Value);
                }
                else
                {
                    var channel = (string)args["channel"] ?? "all";
                    if (!new[] { "all", "position", "rotation", "scale" }.Contains(channel))
                        throw new ArgumentException("channel must be all, position, rotation or scale.");
                    if (channel == "all" || channel == "position") PluginAccess.Call(boneEditor, "ResetBonePos", bone, null, false, 0);
                    if (channel == "all" || channel == "rotation") PluginAccess.Call(boneEditor, "ResetBoneRot", bone, null, false, 0);
                    if (channel == "all" || channel == "scale") PluginAccess.Call(boneEditor, "ResetBoneScale", bone, null, false, 0);
                }
                PluginAccess.Call(boneEditor, "IKExecutionOrderOnPostLateUpdate");
                return BoneState(controller, boneEditor, bone);
            }
            var blendEditor = PluginAccess.Get(controller, "_blendShapesEditor");
            if ((bool)PluginAccess.Get(blendEditor, "_isBusy"))
                throw new InvalidOperationException("KKPE is rebuilding blendshape renderers; inspect again after loading completes.");
            if (method == "pose.blendshapes")
            {
                var rows = new List<JObject>();
                var map = (IDictionary)PluginAccess.Get(blendEditor, "_blenderRenderbySkinnedRenderer");
                foreach (DictionaryEntry entry in map)
                {
                    var renderer = entry.Key as SkinnedMeshRenderer;
                    if (renderer == null || renderer.sharedMesh == null
                        || (args["rendererId"] != null && renderer.GetInstanceID() != (int)args["rendererId"])) continue;
                    for (var i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
                    {
                        var name = renderer.sharedMesh.GetBlendShapeName(i);
                        if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var dirty = (IDictionary)PluginAccess.Get(entry.Value, "_dirtyBlends");
                        var data = dirty.Contains(name) ? dirty[name] : null;
                        rows.Add(new JObject { ["rendererId"] = renderer.GetInstanceID(), ["renderer"] = renderer.name,
                            ["shapeIndex"] = i, ["shapeName"] = name, ["weight"] = renderer.GetBlendShapeWeight(i),
                            ["overridden"] = data != null,
                            ["overrideWeight"] = data == null ? (float?)null : (float)PluginAccess.Get(data, "weight"),
                            ["originalWeight"] = data == null ? (float?)null : (float)PluginAccess.Get(data, "originalWeight") });
                    }
                }
                return new JObject { ["available"] = true, ["objectId"] = target.objectInfo.dicKey,
                    ["linkEyesComponents"] = (bool)PluginAccess.Get(blendEditor, "_linkEyesComponents"),
                    ["total"] = rows.Count, ["blendshapes"] = new JArray(rows.Skip(offset).Take(limit)) };
            }
            if (method != "pose.blendshape_set" && method != "pose.blendshape_reset")
                throw new ArgumentException("Unknown KKPE operation.");
            object editor;
            SkinnedMeshRenderer meshRenderer;
            var blend = BlendRenderer(target, (int)args["rendererId"], out editor, out meshRenderer);
            var shapeName = (string)args["shapeName"];
            var index = ShapeIndex(meshRenderer, shapeName);
            var linkEyesComponents = (bool)PluginAccess.Get(editor, "_linkEyesComponents");
            var linkIndex = -1;
            if (linkEyesComponents)
            {
                var linkCount = ((IList)PluginAccess.Get(blend, "_linkKeynameList")).Count;
                if (linkCount > 0)
                    linkIndex = SavedShapeIndices(blend, meshRenderer, shapeName)
                        .Where(i => i >= 0 && i < linkCount).DefaultIfEmpty(-1).First();
            }
            var shapeOverrides = (IDictionary)PluginAccess.Get(blend, "_dirtyBlends");
            float? originalWeight = shapeOverrides.Contains(shapeName) ? (float?)PluginAccess.Get(shapeOverrides[shapeName], "originalWeight") : null;
            if (method == "pose.blendshape_set")
            {
                var weight = SceneCommands.Number(args["weight"]);
                if (PluginAccess.Call(blend, "SetBlendShapeWeight", shapeName, weight) == null)
                    throw new InvalidOperationException("KKPE did not accept this blendshape; refresh its renderer list.");
                originalWeight = (float)PluginAccess.Get(shapeOverrides[shapeName], "originalWeight");
                if (linkIndex >= 0) PluginAccess.Call(blend, "ApplyLink", weight, linkIndex, false);
            }
            else
            {
                PluginAccess.Call(blend, "NonDirty", shapeName);
                if (linkIndex >= 0) PluginAccess.Call(blend, "ApplyLink", 0f, linkIndex, true);
            }
            PluginAccess.Call(editor, "ApplyBlendShapeWeights");
            return new JObject { ["objectId"] = target.objectInfo.dicKey, ["rendererId"] = meshRenderer.GetInstanceID(),
                ["shapeName"] = shapeName, ["weight"] = meshRenderer.GetBlendShapeWeight(index),
                ["linkEyesComponents"] = linkEyesComponents,
                ["originalWeight"] = originalWeight, ["overridden"] = shapeOverrides.Contains(shapeName),
                ["overrideWeight"] = shapeOverrides.Contains(shapeName) ? (float?)PluginAccess.Get(shapeOverrides[shapeName], "weight") : null };
        }

        internal Transform Bone(ObjectCtrlInfo target, int boneId)
        {
            var controller = Controller(target);
            var bone = OwnedBones(controller).FirstOrDefault(t => t.GetInstanceID() == boneId);
            if (bone == null) throw new ArgumentException("Bone is stale or belongs to another KKPE object; inspect bones again.");
            PathFrom(((Component)controller).transform, bone);
            return bone;
        }

        internal object BlendRenderer(ObjectCtrlInfo target, int rendererId, out object editor, out SkinnedMeshRenderer renderer)
        {
            editor = PluginAccess.Get(Controller(target), "_blendShapesEditor");
            if ((bool)PluginAccess.Get(editor, "_isBusy")) throw new InvalidOperationException("KKPE blendshape renderers are not ready.");
            foreach (DictionaryEntry entry in (IDictionary)PluginAccess.Get(editor, "_blenderRenderbySkinnedRenderer"))
            {
                var candidate = entry.Key as SkinnedMeshRenderer;
                if (candidate != null && candidate.GetInstanceID() == rendererId && candidate.sharedMesh != null)
                {
                    renderer = candidate;
                    return entry.Value;
                }
            }
            throw new ArgumentException("Renderer is stale or does not belong to this KKPE object; inspect blendshapes again.");
        }

        internal static int ShapeIndex(SkinnedMeshRenderer renderer, string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("shapeName is required.");
            var matches = Enumerable.Range(0, renderer.sharedMesh.blendShapeCount)
                .Where(i => renderer.sharedMesh.GetBlendShapeName(i) == name).ToArray();
            if (matches.Length != 1) throw new ArgumentException("Use a unique shapeName from pose_blendshapes.");
            return matches[0];
        }

        internal static int[] SavedShapeIndices(object blend, SkinnedMeshRenderer renderer, string name)
        {
            var originals = (IDictionary)PluginAccess.Get(blend, "_oriBlendIndex");
            var indices = originals.Count == 0 ? Enumerable.Range(0, renderer.sharedMesh.blendShapeCount) : originals.Keys.Cast<int>();
            return indices.Where(i => (string)PluginAccess.Call(blend, "GetBlendShapeName", i) == name).ToArray();
        }

        internal static OCIChar.BoneInfo NativeFk(object editor, Transform bone)
        {
            var owner = PluginAccess.Get(editor, "_target");
            return (bool)PluginAccess.Get(owner, "fkEnabled")
                ? PluginAccess.Call(owner, "GetBoneInfo", bone.gameObject) as OCIChar.BoneInfo : null;
        }

        internal static string PathFrom(Transform root, Transform target, bool requireUnique = true)
        {
            var names = new List<string>();
            for (var current = target; current != root; current = current.parent)
            {
                if (current == null) throw new ArgumentException("Target is outside the object's hierarchy.");
                names.Add(current.name);
            }
            names.Reverse();
            var path = string.Join("/", names.ToArray());
            if (requireUnique && target != root && root.Find(path) != target)
                throw new ArgumentException("The provider cannot persist this ambiguous hierarchy path.");
            return path;
        }

        internal void Flush(ObjectCtrlInfo target)
        {
            var type = PluginAccess.Type(PluginAccess.PoseEditorId, "HSPE.PoseController");
            if (type == null || target.guideObject == null) return;
            var controller = target.guideObject.transformTarget.GetComponent(type);
            if (controller == null) return;
            PluginAccess.Call(PluginAccess.Get(controller, "_bonesEditor"), "IKExecutionOrderOnPostLateUpdate");
            var blends = PluginAccess.Get(controller, "_blendShapesEditor");
            if (!(bool)PluginAccess.Get(blends, "_isBusy")) PluginAccess.Call(blends, "ApplyBlendShapeWeights");
        }

        private static IEnumerable<Transform> OwnedBones(object controller)
        {
            var component = (Component)controller;
            var type = PluginAccess.Type(PluginAccess.PoseEditorId, "HSPE.PoseController");
            foreach (var bone in component.GetComponentsInChildren<Transform>(true))
            {
                if (bone == component.transform) continue;
                var owned = true;
                for (var parent = bone; parent != component.transform; parent = parent.parent)
                    if (parent.GetComponent(type) != null) { owned = false; break; }
                if (owned) yield return bone;
            }
        }

        private static JObject BoneState(object controller, object editor, Transform bone)
        {
            var root = ((Component)controller).transform;
            var path = PathFrom(root, bone, false);
            var dirty = (IDictionary)PluginAccess.Get(editor, "_dirtyBones");
            var data = dirty.Contains(bone.gameObject) ? dirty[bone.gameObject] : null;
            return new JObject { ["boneId"] = bone.GetInstanceID(), ["name"] = bone.name,
                ["path"] = path, ["pathUnique"] = root.Find(path) == bone, ["space"] = "bone-local",
                ["position"] = SceneCommands.Json(bone.localPosition), ["rotation"] = SceneCommands.Json(bone.localEulerAngles),
                ["scale"] = SceneCommands.Json(bone.localScale), ["overridden"] = dirty.Contains(bone.gameObject),
                ["overrides"] = StoredChannels(data, false), ["originals"] = StoredChannels(data, true) };
        }

        private static JObject StoredChannels(object data, bool originals)
        {
            var result = new JObject();
            if (data == null) return result;
            foreach (var channel in new[] { "position", "rotation", "scale" })
            {
                var field = originals ? "original" + char.ToUpperInvariant(channel[0]) + channel.Substring(1) : channel;
                var value = PluginAccess.Get(data, field);
                if (!(bool)PluginAccess.Get(value, "hasValue")) continue;
                var stored = PluginAccess.Get(value, "value");
                result[channel] = stored is Quaternion ? SceneCommands.Json(((Quaternion)stored).eulerAngles)
                    : SceneCommands.Json((Vector3)stored);
            }
            return result;
        }
    }
}
