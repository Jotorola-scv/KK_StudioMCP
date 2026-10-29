// Added 2026-10-29 in the maker-support fork of KK_StudioMCP (AGPL-3.0); see README.md.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using KKAPI.Maker;
using Newtonsoft.Json.Linq;
using Studio;
using UnityEngine;

namespace StudioMcp.Bridge
{
    // Character body/face shape sliders and baked mesh export, for both Studio characters and the maker character.
    internal sealed class CharacterCommands
    {
        // English keys in ChaFileDefine.cf_bodyshapename order; the Japanese names come from the game at runtime.
        private static readonly string[] BodyKeys = {
            "height", "head_size", "neck_width", "neck_thickness", "bust_size", "bust_vertical", "bust_spacing",
            "bust_horizontal", "bust_angle", "bust_pointiness", "bust_shape", "areola_puffiness", "nipple_thickness",
            "nipple_erection", "shoulder_area_width", "shoulder_area_thickness", "upper_torso_width",
            "upper_torso_thickness", "lower_torso_width", "lower_torso_thickness", "waist_position", "belly",
            "upper_hip_width", "upper_hip_thickness", "lower_hip_width", "lower_hip_thickness", "butt", "butt_angle",
            "upper_thigh_width", "upper_thigh_thickness", "lower_thigh_width", "lower_thigh_thickness", "knee_width",
            "knee_thickness", "calves", "ankle_width", "ankle_thickness", "shoulder_width", "shoulder_thickness",
            "upper_arm_width", "upper_arm_thickness", "elbow_width", "elbow_thickness", "forearm" };
        private static readonly string[] Scopes = { "Character", "Clothing", "Hair", "Accessory" };
        // Pushup (KK_Plugins) keeps the unpushed bust sliders 4-13 in PushupController.BaseData and re-applies them on
        // every clothing change, so bust edits must go there too or the next clothes change silently reverts them.
        private static readonly string[] PushupFields = { "Size", "VerticalPosition", "HorizontalAngle", "HorizontalPosition",
            "VerticalAngle", "Depth", "Roundness", "AreolaDepth", "NippleWidth", "NippleDepth" };
        private const int PushupFirst = 4;
        // Shadow proxies, selection highlights, silhouettes and the maker height bar are not part of the visible model.
        private static readonly string[] HelperShaders = { "Bonelyfans", "Shader Forge/shadowcast", "Custom/Silhouette", "Shader Forge/FlatColorTransparent" };
        private readonly Plugin _plugin;
        internal CharacterCommands(Plugin plugin) { _plugin = plugin; }

        internal JToken Execute(string method, JObject args)
        {
            var character = Character((int?)args["objectId"] ?? 0);
            switch (method)
            {
                case "chara.shape_get": return Shapes(character, Part(args));
                case "chara.shape_set":
                    var face = Part(args) == "face";
                    var changes = Changes(character, face, args["values"]);
                    var before = Shapes(character, face ? "face" : "body");
                    foreach (var change in changes)
                        if (face) character.SetShapeFaceValue(change.Key, change.Value);
                        else character.SetShapeBodyValue(change.Key, change.Value);
                    if (!face) SyncPushup(character, changes);
                    // The maker sliders cache their values; ask the maker to re-read them from the character.
                    if (!Plugin.InStudio && MakerAPI.InsideAndLoaded)
                    {
                        if (face) MakerAPI.GetMakerBase().updateCvsFaceShapeAll = true;
                        else MakerAPI.GetMakerBase().updateCvsBodyShapeAll = true;
                    }
                    var after = Shapes(character, face ? "face" : "body");
                    return new JObject { ["part"] = face ? "face" : "body", ["persistence"] = "character_card",
                        ["changed"] = new JArray(changes.Keys.Select(i => new JObject { ["index"] = i,
                            ["key"] = ((JArray)before["shapes"])[i]["key"], ["before"] = ((JArray)before["shapes"])[i]["value"],
                            ["after"] = ((JArray)after["shapes"])[i]["value"] })) };
                case "mesh.export": return Export(character, args);
                default: throw new ArgumentException("Unsupported character method: " + method);
            }
        }

        internal Action PrepareUndo(JObject args)
        {
            var character = Character((int?)args["objectId"] ?? 0);
            var face = Part(args) == "face";
            var saved = Changes(character, face, args["values"]).Keys.ToDictionary(i => i,
                i => face ? character.fileFace.shapeValueFace[i] : BodyValue(character, i));
            return () => {
                if (character == null) throw new InvalidOperationException("Undo character was destroyed.");
                var values = new JObject();
                foreach (var p in saved) values[p.Key.ToString(CultureInfo.InvariantCulture)] = p.Value;
                Execute("chara.shape_set", new JObject { ["objectId"] = args["objectId"], ["part"] = face ? "face" : "body", ["values"] = values });
            };
        }

        internal static int BodyIndex(string key)
        {
            int index;
            if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index >= 0 && index < BodyKeys.Length) return index;
            index = Array.IndexOf(BodyKeys, key);
            if (index < 0) index = Array.IndexOf(ChaFileDefine.cf_bodyshapename, key);
            if (index < 0) throw new ArgumentException("Unknown body shape: " + key);
            return index;
        }

        internal static string BodyKey(int index) => index < BodyKeys.Length ? BodyKeys[index] : index.ToString(CultureInfo.InvariantCulture);

        internal ChaControl Character(int id)
        {
            if (!Plugin.InStudio) return MakerCommands.Character();
            var character = _plugin.Scene.GetObject(id) as OCIChar;
            if (character == null) throw new ArgumentException("A Studio character object is required.");
            return character.charInfo;
        }

        private static string Part(JObject args)
        {
            var part = (string)args["part"] ?? "body";
            if (part != "body" && part != "face") throw new ArgumentException("part must be body or face.");
            return part;
        }

        private static JObject Shapes(ChaControl character, string part)
        {
            var face = part == "face";
            var values = face ? character.fileFace.shapeValueFace : character.fileBody.shapeValueBody;
            var names = face ? ChaFileDefine.cf_headshapename : ChaFileDefine.cf_bodyshapename;
            var shapes = new JArray();
            for (var i = 0; i < values.Length; i++)
            {
                var value = face ? values[i] : BodyValue(character, i);
                var entry = new JObject { ["index"] = i, ["key"] = !face && i < BodyKeys.Length ? BodyKeys[i] : null,
                    ["name"] = i < names.Length ? names[i] : null, ["value"] = value };
                // With Pushup, worn bras/tops reshape the bust; "value" is the card's slider, "displayed" what is rendered.
                if (Math.Abs(values[i] - value) > 1e-4f) entry["displayed"] = values[i];
                shapes.Add(entry);
            }
            return new JObject { ["part"] = part, ["sceneSession"] = Plugin.Instance.Session, ["shapes"] = shapes };
        }

        private static Component PushupController(ChaControl character, out Type type)
        {
            type = MaterialCommands.FindType("KK_Plugins.Pushup+PushupController");
            return type == null ? null : character.GetComponent(type);
        }

        private static float BodyValue(ChaControl character, int index)
        {
            Type type = null;
            var controller = index >= PushupFirst && index < PushupFirst + PushupFields.Length ? PushupController(character, out type) : null;
            if (controller == null) return character.fileBody.shapeValueBody[index];
            var data = type.GetField("BaseData").GetValue(controller);
            return (float)data.GetType().GetProperty(PushupFields[index - PushupFirst]).GetValue(data, null);
        }

        private static void SyncPushup(ChaControl character, Dictionary<int, float> changes)
        {
            Type type;
            var controller = PushupController(character, out type);
            var bust = changes.Where(c => c.Key >= PushupFirst && c.Key < PushupFirst + PushupFields.Length).ToList();
            if (controller == null || bust.Count == 0) return;
            var data = type.GetField("BaseData").GetValue(controller);
            foreach (var change in bust)
                data.GetType().GetProperty(PushupFields[change.Key - PushupFirst]).SetValue(data, change.Value, null);
            // Re-applies the worn bra/top push on top of the new base values, exactly as Pushup does after a clothes change.
            type.GetMethod("RecalculateBody").Invoke(controller, new object[] { true, false });
        }

        // Accepts {"<index>|<english key>|<game name>": value}; validates everything before anything is applied.
        private static Dictionary<int, float> Changes(ChaControl character, bool face, JToken token)
        {
            var values = token as JObject;
            if (values == null || !values.HasValues) throw new ArgumentException("values requires an object such as {\"bust_size\": 0.8}.");
            var count = face ? character.fileFace.shapeValueFace.Length : character.fileBody.shapeValueBody.Length;
            var names = face ? ChaFileDefine.cf_headshapename : ChaFileDefine.cf_bodyshapename;
            var result = new Dictionary<int, float>();
            foreach (var property in values.Properties())
            {
                int index;
                if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
                {
                    index = face ? -1 : Array.IndexOf(BodyKeys, property.Name);
                    if (index < 0) index = Array.IndexOf(names, property.Name);
                }
                if (index < 0 || index >= count) throw new ArgumentException("Unknown " + (face ? "face" : "body") + " shape: " + property.Name);
                var value = SceneCommands.Number(property.Value);
                if (value < -10 || value > 10) throw new ArgumentOutOfRangeException(property.Name, "Shape values must be within -10..10 (maker sliders are 0..1).");
                result[index] = value;
            }
            return result;
        }

        private JObject Export(ChaControl character, JObject args)
        {
            var scopes = args["scopes"] is JArray list ? list.Select(t => (string)t).ToArray() : Scopes;
            foreach (var scope in scopes)
                if (!Scopes.Contains(scope)) throw new ArgumentException("scopes may contain Character, Clothing, Hair and Accessory.");
            var includeInactive = (bool?)args["includeInactive"] ?? false;
            var textures = (bool?)args["textures"] ?? true;
            var includeHelpers = (bool?)args["includeHelpers"] ?? false;
            var name = Clean((string)args["name"] ?? "character").Trim('_');
            if (name.Length == 0) name = "character";
            var folder = Path.Combine(Path.Combine(_plugin.OutputRoot, "exports"), DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + name);
            Directory.CreateDirectory(folder);

            var obj = new StringBuilder("# Exported by StudioMCP from Koikatsu; world space, right-handed (x mirrored).\nmtllib " + name + ".mtl\n");
            var mtl = new StringBuilder();
            var materials = new Dictionary<Material, string>();
            var exported = new JArray();
            var skipped = new JArray();
            int vertexBase = 0, totalTriangles = 0;
            foreach (var renderer in character.GetComponentsInChildren<Renderer>(true))
            {
                int slot;
                GameObject part;
                var scope = MaterialCommands.PartOf(character, renderer.transform, out slot, out part);
                if (!scopes.Contains(scope)) continue;
                if (!includeInactive && (!renderer.enabled || !renderer.gameObject.activeInHierarchy)) continue;
                if (!includeHelpers && renderer.sharedMaterials.Length > 0
                    && renderer.sharedMaterials.All(m => m == null || HelperShaders.Contains(m.shader.name))) continue;
                var entry = new JObject { ["rendererId"] = renderer.GetInstanceID(), ["renderer"] = renderer.name,
                    ["scope"] = scope, ["partSlot"] = slot };
                Mesh mesh;
                Matrix4x4 matrix;
                var skinned = renderer as SkinnedMeshRenderer;
                var baked = false;
                if (skinned != null)
                {
                    if (skinned.sharedMesh == null) { entry["reason"] = "no mesh"; skipped.Add(entry); continue; }
                    // BakeMesh applies the current pose, blend shapes and the renderer's scale, but not its position/rotation.
                    mesh = new Mesh();
                    skinned.BakeMesh(mesh);
                    baked = true;
                    matrix = Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one);
                }
                else
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    mesh = filter != null ? filter.sharedMesh : null;
                    if (mesh == null) { entry["reason"] = "not a mesh renderer"; skipped.Add(entry); continue; }
                    if (!mesh.isReadable) { entry["reason"] = "mesh is not CPU-readable"; skipped.Add(entry); continue; }
                    matrix = renderer.transform.localToWorldMatrix;
                }
                try
                {
                    var triangles = Write(obj, scope + "_" + slot + "_" + renderer.name + "_" + Math.Abs(renderer.GetInstanceID()),
                        mesh, matrix, renderer.sharedMaterials, materials, ref vertexBase);
                    totalTriangles += triangles;
                    entry["vertices"] = mesh.vertexCount;
                    entry["triangles"] = triangles;
                    entry["visible"] = renderer.enabled && renderer.gameObject.activeInHierarchy;
                    exported.Add(entry);
                }
                finally { if (baked) UnityEngine.Object.Destroy(mesh); }
            }
            var textureCount = 0;
            foreach (var pair in materials)
            {
                mtl.Append("newmtl ").Append(pair.Value).Append('\n');
                if (pair.Key.HasProperty("_Color"))
                {
                    var c = pair.Key.GetColor("_Color");
                    mtl.Append("Kd ").Append(F(c.r)).Append(' ').Append(F(c.g)).Append(' ').Append(F(c.b)).Append('\n');
                }
                var texture = textures && pair.Key.HasProperty("_MainTex") ? pair.Key.GetTexture("_MainTex") : null;
                if (texture != null && texture.width <= 4096 && texture.height <= 4096)
                {
                    File.WriteAllBytes(Path.Combine(folder, pair.Value + ".png"), Png(texture));
                    mtl.Append("map_Kd ").Append(pair.Value).Append(".png\n");
                    textureCount++;
                }
                mtl.Append('\n');
            }
            var objPath = Path.Combine(folder, name + ".obj");
            File.WriteAllText(objPath, obj.ToString());
            File.WriteAllText(Path.Combine(folder, name + ".mtl"), mtl.ToString());
            return new JObject { ["path"] = objPath, ["folder"] = folder, ["vertices"] = vertexBase,
                ["triangles"] = totalTriangles, ["textures"] = textureCount, ["renderers"] = exported, ["skipped"] = skipped,
                ["coordinates"] = "world space; OBJ x = -Unity x (Blender: import with forward -Z, up Y)" };
        }

        private static int Write(StringBuilder obj, string group, Mesh mesh, Matrix4x4 matrix, Material[] slots,
            Dictionary<Material, string> materials, ref int vertexBase)
        {
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var uvs = mesh.uv;
            obj.Append("o ").Append(Clean(group)).Append('\n');
            foreach (var v in vertices)
            {
                var w = matrix.MultiplyPoint3x4(v);
                obj.Append("v ").Append(F(-w.x)).Append(' ').Append(F(w.y)).Append(' ').Append(F(w.z)).Append('\n');
            }
            var hasUv = uvs.Length == vertices.Length;
            var hasNormal = normals.Length == vertices.Length;
            if (hasUv) foreach (var uv in uvs) obj.Append("vt ").Append(F(uv.x)).Append(' ').Append(F(uv.y)).Append('\n');
            if (hasNormal) foreach (var n in normals)
            {
                var w = matrix.MultiplyVector(n).normalized;
                obj.Append("vn ").Append(F(-w.x)).Append(' ').Append(F(w.y)).Append(' ').Append(F(w.z)).Append('\n');
            }
            var triangles = 0;
            for (var sub = 0; sub < mesh.subMeshCount; sub++)
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                var material = sub < slots.Length ? slots[sub] : null;
                if (material != null)
                {
                    string mtlName;
                    if (!materials.TryGetValue(material, out mtlName))
                        materials[material] = mtlName = Clean(material.name.Replace(" (Instance)", "")) + "_" + materials.Count;
                    obj.Append("usemtl ").Append(mtlName).Append('\n');
                }
                var indices = mesh.GetTriangles(sub);
                // Mirroring x flips handedness, so reverse the winding to keep faces pointing outward.
                for (var i = 0; i + 2 < indices.Length; i += 3)
                {
                    obj.Append('f');
                    foreach (var k in new[] { indices[i], indices[i + 2], indices[i + 1] })
                    {
                        var index = (vertexBase + k + 1).ToString(CultureInfo.InvariantCulture);
                        obj.Append(' ').Append(index);
                        if (hasUv || hasNormal) obj.Append('/').Append(hasUv ? index : "");
                        if (hasNormal) obj.Append('/').Append(index);
                    }
                    obj.Append('\n');
                    triangles++;
                }
            }
            vertexBase += vertices.Length;
            return triangles;
        }

        // GPU blit, so textures that are not CPU-readable (most game textures) can still be saved.
        private static byte[] Png(Texture texture)
        {
            var readable = Readable(texture);
            try { return readable.EncodeToPNG(); }
            finally { UnityEngine.Object.Destroy(readable); }
        }

        // A CPU-readable RGBA copy of any texture; the caller destroys it.
        internal static Texture2D Readable(Texture texture)
        {
            var previous = RenderTexture.active;
            var target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D readable = null;
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                readable = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                readable.Apply(false, false);
                var result = readable;
                readable = null;
                return result;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                if (readable != null) UnityEngine.Object.Destroy(readable);
            }
        }

        private static string F(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        // .NET 3.5 (KK) has no string.Concat(IEnumerable<char>), so build the string explicitly.
        private static string Clean(string value) => new string(value.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
    }
}
