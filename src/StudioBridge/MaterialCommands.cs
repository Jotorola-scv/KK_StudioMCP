using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Bootstrap;
using KKAPI.Maker;
using Newtonsoft.Json.Linq;
using Studio;
using UnityEngine;

namespace StudioMcp.Bridge
{
    internal sealed class MaterialCommands
    {
        private readonly Plugin _plugin;
        internal MaterialCommands(Plugin plugin) { _plugin = plugin; }
        internal bool Available => FindType("KK_Plugins.MaterialEditor.SceneController") != null;

        internal sealed class Target
        {
            internal int ObjectId, Slot, PartSlot;
            internal GameObject Root;
            internal Renderer Renderer;
            internal Material Material;
            internal Component Controller;
            internal string Scope = "Item";
        }

        internal JToken Execute(string method, JObject args)
        {
            if (method == "me.list") return List((int)args["objectId"]);
            var target = Resolve(args);
            if (method == "me.inspect") return Inspect(target);
            if (method == "me.render_queue")
            {
                if (args["renderQueue"] == null || args["renderQueue"].Type != JTokenType.Integer)
                    throw new ArgumentException("renderQueue requires an integer.");
                var queue = (int)args["renderQueue"];
                if (queue != -1 && (queue < 0 || queue > 5000))
                    throw new ArgumentOutOfRangeException("renderQueue", "Use 0-5000, or -1 to remove the ME override and restore its recorded original queue.");
                var beforeQueue = target.Material.renderQueue;
                var call = new List<object>();
                if (target.Scope == "Item") call.Add(target.ObjectId);
                else { call.Add(target.PartSlot); call.Add(Enum.Parse(target.Controller.GetType().GetNestedType("ObjectType"), target.Scope)); }
                call.Add(target.Material);
                if (queue >= 0) call.Add(queue);
                if (target.Scope != "Item") call.Add(target.Root);
                call.Add(true);
                var queueMethod = queue >= 0 ? "SetMaterialShaderRenderQueue" : "RemoveMaterialShaderRenderQueue";
                var queueApi = target.Controller.GetType().GetMethods().SingleOrDefault(m => m.Name == queueMethod && m.GetParameters().Length == call.Count);
                if (queueApi == null) throw new NotSupportedException("Installed ME does not expose " + queueMethod + ".");
                queueApi.Invoke(target.Controller, call.ToArray());
                return new JObject { ["before"] = beforeQueue, ["after"] = target.Material.renderQueue,
                    ["persistence"] = "material_editor", ["matchingMaterials"] = MatchingMaterials(target) };
            }
            if (method == "me.texture_transform")
            {
                var textureName = (string)args["property"];
                ValidateProperty(target, textureName, "texture");
                var transforms = new Dictionary<string, Vector2>();
                foreach (var key in new[] { "scale", "offset" })
                {
                    if (args[key] == null) continue;
                    var a = args[key] as JArray;
                    if (a == null || a.Count != 2) throw new ArgumentException(key + " requires two numbers.");
                    var v = new Vector2(SceneCommands.Number(a[0]), SceneCommands.Number(a[1]));
                    if (Math.Abs(v.x) > 10000 || Math.Abs(v.y) > 10000) throw new ArgumentOutOfRangeException(key);
                    transforms.Add(key, v);
                }
                if (transforms.Count == 0) throw new ArgumentException("A texture scale or offset is required.");
                foreach (var p in transforms)
                    Invoke(target, p.Key == "scale" ? "SetMaterialTextureScale" : "SetMaterialTextureOffset", textureName.Substring(1), p.Value, true);
                var scale = target.Material.GetTextureScale(textureName);
                var offset = target.Material.GetTextureOffset(textureName);
                return new JObject { ["property"] = textureName, ["scale"] = new JArray(scale.x, scale.y),
                    ["offset"] = new JArray(offset.x, offset.y), ["persistence"] = "material_editor" };
            }
            if (method == "me.shader")
            {
                var shaderName = (string)args["shader"];
                if (string.IsNullOrEmpty(shaderName)) throw new ArgumentException("shader is required.");
                var metadata = (IDictionary)RequiredType("MaterialEditorAPI.MaterialEditorPluginBase")
                    .GetField("XMLShaderProperties").GetValue(null);
                if (!metadata.Contains(shaderName)) throw new ArgumentException("Shader is not registered in Material Editor.");
                var beforeShader = target.Material.shader.name;
                var parameters = new List<object>();
                if (target.Scope == "Item") parameters.Add(target.ObjectId);
                else { parameters.Add(target.PartSlot); parameters.Add(Enum.Parse(target.Controller.GetType().GetNestedType("ObjectType"), target.Scope)); }
                parameters.Add(target.Material); parameters.Add(shaderName);
                if (target.Scope != "Item") parameters.Add(target.Root);
                parameters.Add(true);
                var api = target.Controller.GetType().GetMethods().SingleOrDefault(m => m.Name == "SetMaterialShader" && m.GetParameters().Length == parameters.Count);
                if (api == null) throw new NotSupportedException("ME SetMaterialShader is unavailable.");
                api.Invoke(target.Controller, parameters.ToArray());
                var updated = Resolve(args);
                if (updated.Material.shader.name != shaderName) throw new InvalidOperationException("ME did not apply the requested shader.");
                return new JObject { ["before"] = beforeShader, ["after"] = shaderName,
                    ["persistence"] = "material_editor", ["reinspectRequired"] = true,
                    ["matchingMaterials"] = MatchingMaterials(updated) };
            }
            var name = (string)args["property"];
            var kind = (string)args["kind"] ?? "float";
            ValidateProperty(target, name, kind);
            var property = name.Substring(1);
            var before = Value(target.Material, name, kind);
            if (method == "me.reset")
                Invoke(target, "RemoveMaterial" + Suffix(kind), property, null, true);
            else if (method == "me.set" && kind != "texture")
            {
                object value = kind == "color" ? (object)ColorValue(args["value"]) : SceneCommands.Number(args["value"]);
                Invoke(target, "SetMaterial" + Suffix(kind), property, value, true);
            }
            else if (method == "me.import_texture" && kind == "texture")
            {
                var path = Path.GetFullPath((string)args["path"] ?? "");
                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
                    throw new ArgumentException("Texture imports currently accept PNG and JPEG files.");
                var file = new FileInfo(path);
                if (!file.Exists || file.Length == 0 || file.Length > 64 * 1024 * 1024)
                    throw new ArgumentException("Texture file is missing, empty or exceeds 64 MiB.");
                // The provider owns texture lifetime and the bytes saved into cards/scenes.
                Invoke(target, "SetMaterialTextureFromFile", property, path, true);
                if (Invoke(target, "GetMaterialTexture", property) == null || target.Material.GetTexture(name) == null)
                    throw new InvalidOperationException("ME did not register and apply the imported texture.");
            }
            else throw new ArgumentException("Unsupported material operation.");
            return new JObject { ["property"] = name, ["before"] = before,
                ["after"] = Value(target.Material, name, kind), ["persistence"] = "material_editor",
                ["scope"] = target.Scope, ["partSlot"] = target.PartSlot,
                ["objectId"] = target.ObjectId, ["material"] = target.Material.name,
                ["matchingMaterials"] = MatchingMaterials(target) };
        }

        // Restore ME's saved value, or remove the override when none existed.
        internal Action PrepareUndo(JObject args)
        {
            var target = Resolve(args);
            var name = (string)args["property"];
            var kind = (string)args["kind"] ?? "float";
            if (kind != "color" && kind != "float") throw new ArgumentException("Batch ME edits support Float/Color only; checkpoint before texture imports.");
            ValidateProperty(target, name, kind);
            if (kind == "color") ColorValue(args["value"]); else SceneCommands.Number(args["value"]);
            var property = name.Substring(1);
            var saved = Invoke(target, "GetMaterial" + (kind == "color" ? "Color" : "Float") + "PropertyValue", property);
            return () => {
                if (target.Material == null || target.Renderer == null) throw new InvalidOperationException("Undo target was destroyed.");
                if (saved == null) Invoke(target, "RemoveMaterial" + Suffix(kind), property, null, true);
                else Invoke(target, "SetMaterial" + Suffix(kind), property, saved, true);
            };
        }

        private static string Suffix(string kind) => kind == "texture" ? "Texture" : kind == "color" ? "ColorProperty" : "FloatProperty";

        internal Target Resolve(JObject args)
        {
            var id = (int)args["objectId"];
            var owner = _plugin.Scene.GetObject(id);
            var root = SceneCommands.Root(owner);
            if (root == null) throw new ArgumentException("Object has no editable material root.");
            var renderer = root.GetComponentsInChildren<Renderer>(true)
                .FirstOrDefault(r => r.GetInstanceID() == (int)args["rendererId"]);
            if (renderer == null) throw new ArgumentException("Renderer handle is stale or belongs to another object.");
            var materials = renderer.materials;
            var slot = (int?)args["slot"] ?? 0;
            if (slot < 0 || slot >= materials.Length || materials[slot] == null) throw new ArgumentOutOfRangeException("slot");
            var target = MakeTarget(id, owner, renderer, materials[slot], slot);
            if (args["materialId"] != null && (int)args["materialId"] != target.Material.GetInstanceID())
                throw new InvalidOperationException("Material identity changed; inspect it again.");
            return target;
        }

        private Target MakeTarget(int id, ObjectCtrlInfo owner, Renderer renderer, Material material, int slot)
        {
            var target = new Target { ObjectId = id, Root = SceneCommands.Root(owner), Renderer = renderer, Material = material, Slot = slot };
            var character = owner as OCIChar;
            if (character == null)
                target.Controller = Chainloader.ManagerObject.GetComponentInChildren(RequiredType("KK_Plugins.MaterialEditor.SceneController"));
            else
            {
                var cha = character.charInfo;
                target.Controller = cha.GetComponent(RequiredType("KK_Plugins.MaterialEditor.MaterialEditorCharaController"));
                target.Scope = "Character";
                // Part roots must win over the enclosing character root, including inactive parts.
                if (!FindPart(target, cha.GetAccessoryObjects(), "Accessory") && !FindPart(target, cha.objClothes, "Clothing"))
                    FindPart(target, cha.objHair, "Hair");
            }
            if (target.Controller == null) throw new InvalidOperationException("Material Editor controller is not ready.");
            return target;
        }

        private static bool FindPart(Target target, GameObject[] parts, string scope)
        {
            for (var i = 0; i < parts.Length; i++)
                if (parts[i] != null && target.Renderer.transform.IsChildOf(parts[i].transform))
                { target.Root = parts[i]; target.PartSlot = i; target.Scope = scope; return true; }
            return false;
        }

        private JArray List(int id)
        {
            var owner = _plugin.Scene.GetObject(id);
            var root = SceneCommands.Root(owner);
            var result = new JArray();
            if (root == null) return result;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.materials;
                for (var slot = 0; slot < materials.Length; slot++)
                {
                    if (materials[slot] == null) continue;
                    var target = MakeTarget(id, owner, renderer, materials[slot], slot);
                    result.Add(new JObject { ["objectId"] = id, ["rendererId"] = renderer.GetInstanceID(),
                        ["renderer"] = renderer.name, ["layer"] = renderer.gameObject.layer, ["slot"] = slot, ["materialId"] = materials[slot].GetInstanceID(),
                        ["name"] = materials[slot].name, ["shader"] = materials[slot].shader.name,
                        ["renderQueue"] = materials[slot].renderQueue,
                        ["scope"] = target.Scope, ["partSlot"] = target.PartSlot });
                }
            }
            return result;
        }

        private static object Invoke(Target target, string method, string property, object value = null, bool write = false)
        {
            var args = new List<object>();
            if (target.Scope == "Item") args.Add(target.ObjectId);
            else
            {
                args.Add(target.PartSlot);
                args.Add(Enum.Parse(target.Controller.GetType().GetNestedType("ObjectType"), target.Scope));
            }
            args.Add(target.Material);
            args.Add(property);
            if (method.StartsWith("Set", StringComparison.Ordinal)) args.Add(value);
            if (target.Scope != "Item") args.Add(target.Root);
            // Older ME calls this flag setTexInUpdate; false selects its existing synchronous import path.
            if (write) args.Add(method != "SetMaterialTextureFromFile");
            var api = target.Controller.GetType().GetMethods().SingleOrDefault(m => m.Name == method && m.GetParameters().Length == args.Count);
            if (api == null) throw new NotSupportedException("Installed ME does not expose " + method + ".");
            return api.Invoke(target.Controller, args.ToArray());
        }

        private static void ValidateProperty(Target target, string name, string kind)
        {
            if (kind != "color" && kind != "float" && kind != "texture")
                throw new ArgumentException("Supported ME property kinds are color, float and texture.");
            if (string.IsNullOrEmpty(name) || !name.StartsWith("_", StringComparison.Ordinal) || !target.Material.HasProperty(name))
                throw new ArgumentException("Use an existing shader property name including its leading underscore.");
            var property = Properties(target.Material).FirstOrDefault(p => (string)p["name"] == name);
            if (property == null) throw new ArgumentException("Property is not described by the installed shader/ME metadata.");
            var actual = (string)property["kind"];
            if (actual != kind && !(kind == "float" && actual == "range"))
                throw new ArgumentException("Property type is " + actual + ", not " + kind + ".");
        }

        private static JObject Inspect(Target target) => new JObject {
            ["objectId"] = target.ObjectId, ["rendererId"] = target.Renderer.GetInstanceID(),
            ["slot"] = target.Slot, ["materialId"] = target.Material.GetInstanceID(), ["name"] = target.Material.name,
            ["shader"] = target.Material.shader.name, ["renderQueue"] = target.Material.renderQueue,
            ["scope"] = target.Scope, ["partSlot"] = target.PartSlot,
            ["properties"] = Properties(target.Material), ["matchingMaterials"] = MatchingMaterials(target) };

        private static JArray Properties(Material material)
        {
            var properties = new JArray();
#if KKS
            var shader = material.shader;
            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                var name = shader.GetPropertyName(i);
                var kind = shader.GetPropertyType(i).ToString().ToLowerInvariant();
                properties.Add(new JObject { ["name"] = name, ["kind"] = kind, ["value"] = Value(material, name, kind) });
            }
#else
            // Unity 5.6 cannot enumerate shader properties. Use ME's metadata, not guessed names.
            var metadata = (IDictionary)RequiredType("MaterialEditorAPI.MaterialEditorPluginBase").GetField("XMLShaderProperties").GetValue(null);
            var names = new HashSet<string>();
            foreach (var key in new[] { material.shader.name, "default" })
            {
                if (!metadata.Contains(key)) continue;
                foreach (DictionaryEntry entry in (IDictionary)metadata[key])
                {
                    var data = entry.Value;
                    var name = "_" + (string)data.GetType().GetField("Name").GetValue(data);
                    if (!material.HasProperty(name) || !names.Add(name)) continue;
                    var kind = data.GetType().GetField("Type").GetValue(data).ToString().ToLowerInvariant();
                    properties.Add(new JObject { ["name"] = name, ["kind"] = kind, ["value"] = Value(material, name, kind) });
                }
            }
#endif
            return properties;
        }

        private static JToken Value(Material material, string name, string kind)
        {
            if (kind == "color") return SceneCommands.Json(material.GetColor(name));
            if (kind == "float" || kind == "range") return new JValue(material.GetFloat(name));
            if (kind == "vector") { var v = material.GetVector(name); return new JArray(v.x, v.y, v.z, v.w); }
            if (kind != "texture") return null;
            var texture = material.GetTexture(name);
            return texture == null ? null : new JObject { ["name"] = texture.name, ["width"] = texture.width,
                ["height"] = texture.height, ["instanceId"] = texture.GetInstanceID() };
        }

        private static string Formatted(Material material) => material.name.Replace(" (Instance)", "").Trim();
        private static JArray MatchingMaterials(Target target) => new JArray(target.Root.GetComponentsInChildren<Renderer>(true)
            .Where(r => r.materials.Any(m => m != null && Formatted(m) == Formatted(target.Material)))
            .Select(r => new JObject { ["rendererId"] = r.GetInstanceID(), ["renderer"] = r.name }));

        internal static Color ColorValue(JToken value)
        {
            var rgba = value as JArray;
            if (rgba == null || rgba.Count != 4) throw new ArgumentException("Color requires four RGBA values.");
            return new Color(SceneCommands.Number(rgba[0]), SceneCommands.Number(rgba[1]), SceneCommands.Number(rgba[2]), SceneCommands.Number(rgba[3]));
        }

        private static Type RequiredType(string name) => FindType(name) ?? throw new NotSupportedException("Required provider type is unavailable: " + name);
        internal static Type FindType(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name, false)).FirstOrDefault(type => type != null);
    }
}
