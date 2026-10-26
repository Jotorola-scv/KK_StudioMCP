using System;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using Newtonsoft.Json.Linq;
using Studio;
using UnityEngine;

namespace StudioMcp.Bridge
{
    internal sealed class MaterialCommands
    {
        private readonly Plugin _plugin;
        private Component _controller;
        internal MaterialCommands(Plugin plugin) { _plugin = plugin; }
        internal bool Available => FindType("KK_Plugins.MaterialEditor.SceneController") != null;

        internal sealed class Target
        {
            internal int ObjectId;
            internal GameObject Root;
            internal Renderer Renderer;
            internal int Slot;
            internal Material Material;
        }

        internal JToken Execute(string method, JObject args)
        {
            if (method == "me.list") return List((int)args["objectId"]);
            var target = Resolve(args);
            if (method == "me.inspect") return Inspect(target);
            if (method != "me.set") throw new ArgumentException("Unsupported material method.");
            var name = (string)args["property"];
            if (string.IsNullOrEmpty(name) || !target.Material.HasProperty(name))
                throw new ArgumentException("Shader property does not exist: " + name);
            var property = name.StartsWith("_", StringComparison.Ordinal) ? name.Substring(1) : name;
            var kind = (string)args["kind"] ?? "float";
            object value;
            string setter;
            JToken before;
            if (kind == "color")
            {
                var rgba = args["value"] as JArray;
                if (rgba == null || rgba.Count != 4) throw new ArgumentException("Color requires RGBA values.");
                value = new Color(SceneCommands.Number(rgba[0]), SceneCommands.Number(rgba[1]),
                    SceneCommands.Number(rgba[2]), SceneCommands.Number(rgba[3]));
                setter = "SetMaterialColorProperty";
                before = SceneCommands.Json(target.Material.GetColor(name));
            }
            else if (kind == "float")
            {
                value = SceneCommands.Number(args["value"]);
                setter = "SetMaterialFloatProperty";
                before = new JValue(target.Material.GetFloat(name));
            }
            else throw new ArgumentException("This bridge version supports color and float writes.");
            var controller = SceneController();
            var setMethod = controller.GetType().GetMethods().Single(m => m.Name == setter && m.GetParameters().Length == 5);
            setMethod.Invoke(controller, new[] { (object)target.ObjectId, target.Material, property, value, true });
            var after = kind == "color" ? (JToken)SceneCommands.Json(target.Material.GetColor(name))
                : new JValue(target.Material.GetFloat(name));
            return new JObject { ["property"] = name, ["before"] = before, ["after"] = after,
                ["persistence"] = "material_editor_scene", ["scope"] = "object_and_material_name",
                ["objectId"] = target.ObjectId, ["material"] = target.Material.name };
        }

        internal Target Resolve(JObject args)
        {
            var id = (int)args["objectId"];
            var owner = _plugin.Scene.GetObject(id);
            if (!(owner is OCIItem)) throw new NotSupportedException("Character material scopes are not implemented yet.");
            var root = SceneCommands.Root(owner);
            var rendererId = (int)args["rendererId"];
            var renderer = root.GetComponentsInChildren<Renderer>(true).FirstOrDefault(r => r.GetInstanceID() == rendererId);
            if (renderer == null) throw new ArgumentException("Renderer handle is stale or belongs to another object.");
            var materials = renderer.materials;
            var slot = (int?)args["slot"] ?? 0;
            if (slot < 0 || slot >= materials.Length || materials[slot] == null) throw new ArgumentOutOfRangeException("slot");
            var material = materials[slot];
            if (args["materialId"] != null && (int)args["materialId"] != material.GetInstanceID())
                throw new InvalidOperationException("Material identity changed; inspect it again.");
            return new Target { ObjectId = id, Root = root, Renderer = renderer, Slot = slot, Material = material };
        }

        private JArray List(int id)
        {
            var root = SceneCommands.Root(_plugin.Scene.GetObject(id));
            if (root == null) return new JArray();
            var result = new JArray();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.materials;
                for (var slot = 0; slot < materials.Length; slot++)
                {
                    var material = materials[slot];
                    if (material == null) continue;
                    result.Add(new JObject { ["objectId"] = id, ["rendererId"] = renderer.GetInstanceID(),
                        ["renderer"] = renderer.name, ["slot"] = slot, ["materialId"] = material.GetInstanceID(),
                        ["name"] = material.name, ["shader"] = material.shader.name });
                }
            }
            return result;
        }

        private static JObject Inspect(Target target)
        {
            var material = target.Material;
            var properties = new JArray();
#if KKS
            var shader = material.shader;
            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                var name = shader.GetPropertyName(i);
                var kind = shader.GetPropertyType(i).ToString();
                JToken value = null;
                if (kind == "Color") value = SceneCommands.Json(material.GetColor(name));
                else if (kind == "Float" || kind == "Range") value = new JValue(material.GetFloat(name));
                else if (kind == "Vector") { var v = material.GetVector(name); value = new JArray(v.x, v.y, v.z, v.w); }
                else if (kind == "Texture")
                {
                    var texture = material.GetTexture(name);
                    value = texture != null ? new JObject { ["name"] = texture.name,
                        ["width"] = texture.width, ["height"] = texture.height } : null;
                }
                properties.Add(new JObject { ["name"] = name, ["kind"] = kind.ToLowerInvariant(), ["value"] = value });
            }
#else
            foreach (var name in new[] { "_Color", "_Color2", "_Color3", "_Glossiness", "_Metallic" })
                if (material.HasProperty(name)) properties.Add(new JObject { ["name"] = name,
                    ["kind"] = name.StartsWith("_Color") ? "color" : "float" });
#endif
            return new JObject { ["objectId"] = target.ObjectId, ["rendererId"] = target.Renderer.GetInstanceID(),
                ["slot"] = target.Slot, ["materialId"] = material.GetInstanceID(), ["name"] = material.name,
                ["shader"] = material.shader.name, ["properties"] = properties };
        }

        private Component SceneController()
        {
            if (_controller != null) return _controller;
            var type = FindType("KK_Plugins.MaterialEditor.SceneController");
            if (type == null) throw new NotSupportedException("Material Editor is not installed.");
            _controller = Chainloader.ManagerObject.GetComponentInChildren(type);
            if (_controller == null) throw new InvalidOperationException("Material Editor's scene controller is not ready.");
            return _controller;
        }

        internal static Type FindType(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name, false)).FirstOrDefault(type => type != null);
    }
}
