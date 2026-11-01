using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Studio;
using UnityEngine;

namespace StudioMcp.Bridge
{
    internal sealed partial class TimelineCommands
    {
        private JObject CreateTrack(object self, JObject args)
        {
            var target = _plugin.Scene.GetObject((int)args["objectId"]);
            var kind = (string)args["kind"];
            var alias = Name(args["name"]);
            var document = new XmlDocument();
            var node = document.CreateElement("interpolable");
            document.AppendChild(node);
            var owner = "Timeline";
            string modelId = null;
            GuideObject guide = null;
            switch (kind)
            {
                case "object_position": case "object_rotation": case "object_scale":
                    guide = target.guideObject;
                    break;
                case "fk_rotation": case "ik_position": case "ik_rotation":
                    guide = _plugin.Kinematics.Control(target.objectInfo.dicKey, kind.StartsWith("fk_", StringComparison.Ordinal) ? "FK" : "IK", RequiredId(args, "controlId"));
                    break;
                case "bone_position": case "bone_rotation": case "bone_scale":
                    var controller = _plugin.PoseEditor.Controller(target);
                    var bone = _plugin.PoseEditor.Bone(target, RequiredId(args, "boneId"));
                    var editor = PluginAccess.Get(controller, "_bonesEditor");
                    var fk = kind == "bone_rotation" ? PoseEditorCommands.NativeFk(editor, bone) : null;
                    if (fk != null && fk.active) guide = fk.guideObject;
                    else
                    {
                        owner = "KKPE";
                        modelId = kind == "bone_position" ? "bonePos" : kind == "bone_rotation" ? "boneRot" : "boneScale";
                        node.SetAttribute("parameter", PoseEditorCommands.PathFrom(((Component)controller).transform, bone));
                    }
                    break;
                case "blendshape":
                    object blends;
                    SkinnedMeshRenderer renderer;
                    var wrapper = _plugin.PoseEditor.BlendRenderer(target, RequiredId(args, "rendererId"), out blends, out renderer);
                    var shapeName = (string)args["shapeName"];
                    PoseEditorCommands.ShapeIndex(renderer, shapeName);
                    var matches = PoseEditorCommands.SavedShapeIndices(wrapper, renderer, shapeName);
                    if (matches.Length != 1) throw new InvalidOperationException("KKPE cannot bind this shape uniquely to its saved index.");
                    owner = "KKPE";
                    modelId = "lastBlendShape";
                    node.SetAttribute("parameter1", PoseEditorCommands.PathFrom(target.guideObject.transformTarget, renderer.transform));
                    node.SetAttribute("parameter2", XmlConvert.ToString(matches[0]));
                    break;
                default: throw new ArgumentException("Use a track kind returned by timeline_get.");
            }
            if (guide != null)
            {
                var rotation = kind.EndsWith("rotation", StringComparison.Ordinal);
                var scale = kind.EndsWith("scale", StringComparison.Ordinal);
                if ((rotation && !guide.enableRot) || (scale && !guide.enableScale) || (!rotation && !scale && !guide.enablePos))
                    throw new ArgumentException("This native GuideObject does not support the requested channel.");
                modelId = rotation ? "guideObjectRot" : scale ? "guideObjectScale" : "guideObjectPos";
                node.SetAttribute("guideObjectPath", PoseEditorCommands.PathFrom(target.guideObject.transformTarget, guide.transformTarget));
            }
            node.SetAttribute("owner", owner);
            node.SetAttribute("id", modelId);
            if (alias != null) node.SetAttribute("alias", alias);
            var model = ((IEnumerable)PluginAccess.Get(self, "_interpolableModelsList")).Cast<object>()
                .FirstOrDefault(m => (string)PluginAccess.Get(m, "owner") == owner && (string)PluginAccess.Get(m, "id") == modelId);
            if (model == null) throw new NotSupportedException("The installed provider has not registered this Timeline model.");
            if (!(bool)PluginAccess.Call(model, "IsCompatibleWithTarget", target))
                throw new ArgumentException("The native Timeline model does not support this object.");
            var readParameter = PluginAccess.Get(model, "readParameterFromXml") as Delegate;
            if (readParameter == null) throw new NotSupportedException("The Timeline model has no saved parameter binding.");
            var parameter = readParameter.DynamicInvoke(target, node);
            if (parameter == null) throw new InvalidOperationException("Timeline could not resolve this saved target path.");
            var type = Provider.Assembly.GetType("Timeline.Interpolable", true);
            var candidate = Activator.CreateInstance(type, new[] { target, parameter, model });
            if (JsonValue(PluginAccess.Call(candidate, "GetValue")) == null)
                throw new NotSupportedException("The Timeline model returned an unsupported value type.");
            var existing = Tracks().FirstOrDefault(t => t.GetHashCode() == candidate.GetHashCode());
            if (existing != null)
            {
                if ((string)PluginAccess.Get(existing, "owner") != owner || (string)PluginAccess.Get(existing, "id") != modelId
                    || !ReferenceEquals(PluginAccess.Get(existing, "oci"), target)
                    || !JToken.DeepEquals(ParameterAttributes(existing), ParameterAttributes(candidate)))
                    throw new InvalidOperationException("Timeline's track hash conflicts with a different target; no track was changed.");
                var unchanged = Describe(existing);
                unchanged["created"] = false;
                return unchanged;
            }
            if (Tracks().Count() >= 5000) throw new InvalidOperationException("The project has reached the MCP limit of 5000 tracks.");
            // Let Timeline construct and register its own track, tree entry and persistence binding.
            PluginAccess.Call(self, "ReadInterpolable", node, new List<KeyValuePair<int, ObjectCtrlInfo>>(), target, null);
            var added = Tracks().FirstOrDefault(t => t.GetHashCode() == candidate.GetHashCode());
            if (added == null) throw new InvalidOperationException("Timeline rejected the track; no new track was registered.");
            PluginAccess.Call(Provider, "RefreshInterpolablesList");
            var result = Describe(added);
            result["created"] = true;
            return result;
        }

        private static int RequiredId(JObject args, string name)
        {
            if (args[name] == null || args[name].Type != JTokenType.Integer) throw new ArgumentException(name + " requires an inspected integer handle.");
            return (int)args[name];
        }

        private static JObject ParameterAttributes(object track)
        {
            var callback = PluginAccess.Get(track, "writeParameterToXml") as Delegate;
            if (callback == null) return new JObject();
            var text = new StringWriter();
            using (var writer = new XmlTextWriter(text))
            {
                writer.WriteStartElement("binding");
                callback.DynamicInvoke(PluginAccess.Get(track, "oci"), writer, PluginAccess.Get(track, "parameter"));
                writer.WriteEndElement();
            }
            var document = new XmlDocument();
            document.LoadXml(text.ToString());
            var result = new JObject();
            foreach (XmlAttribute attribute in document.DocumentElement.Attributes.Cast<XmlAttribute>().OrderBy(a => a.Name, StringComparer.Ordinal))
                result[attribute.Name] = attribute.Value;
            return result;
        }
    }
}
