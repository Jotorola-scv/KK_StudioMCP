// Added 2026-10-30 in the maker-support fork of KK_StudioMCP (AGPL-3.0); see README.md.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudioMcp.Bridge
{
    // Runtime-only display controls for diagnosis: hide renderers (hair over the back, accessories) and swap materials for
    // a neutral preview, optionally with the camera's image effects off. Nothing touches accessory transforms, the card or
    // Material Editor data; every change returns a token and is undone by restore, a card save or a character reload.
    internal sealed class VisibilityCommands
    {
        private static readonly string[] PreviewShaders = { "Standard", "Legacy Shaders/Diffuse", "Diffuse", "Unlit/Color" };
        private readonly Plugin _plugin;
        private readonly Dictionary<string, List<Renderer>> _hidden = new Dictionary<string, List<Renderer>>();
        private readonly Dictionary<string, Preview> _previews = new Dictionary<string, Preview>();
        private bool Suspended;

        private sealed class Preview
        {
            internal readonly List<KeyValuePair<Renderer, Material[]>> Materials = new List<KeyValuePair<Renderer, Material[]>>();
            internal readonly List<Material> Created = new List<Material>();
            internal readonly List<Behaviour> Effects = new List<Behaviour>();
        }

        internal VisibilityCommands(Plugin plugin)
        {
            _plugin = plugin;
            // A preview must never reach a saved card or its thumbnail data.
            ExtensibleSaveFormat.ExtendedSave.CardBeingSaved += file => RestoreAllPreviews();
            ExtensibleSaveFormat.ExtendedSave.CoordinateBeingSaved += file => RestoreAllPreviews();
        }

        internal JToken Execute(string method, JObject args)
        {
            switch (method)
            {
                case "chara.visibility_set": return Hide(args);
                case "chara.visibility_restore": return RestoreHidden((string)args["token"]);
                case "chara.preview_set": return SetPreview(args);
                case "chara.preview_restore": return RestorePreview((string)args["token"]);
                case "chara.visibility_get": return State();
                default: throw new ArgumentException("Unknown visibility method: " + method);
            }
        }

        private JObject Hide(JObject args)
        {
            var character = _plugin.Characters.Character((int?)args["objectId"] ?? 0);
            var renderers = SampleCommands.Renderers(character, args, true).Where(r => r.enabled).ToList();
            if (renderers.Count == 0) throw new ArgumentException("No visible renderer matches.");
            var token = "hide-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            foreach (var renderer in renderers) renderer.enabled = false;
            _hidden[token] = renderers;
            return new JObject { ["token"] = token, ["persistence"] = "runtime only; not saved, accessory transforms untouched",
                ["hidden"] = Describe(character, renderers) };
        }

        // The game re-enables nothing per frame today, but keep hidden renderers hidden if something does.
        internal void Tick()
        {
            if (_hidden.Count == 0 || Suspended) return;
            foreach (var list in _hidden.Values)
                foreach (var renderer in list)
                    if (renderer != null && renderer.enabled) renderer.enabled = false;
        }

        private JObject RestoreHidden(string token)
        {
            var tokens = string.IsNullOrEmpty(token) ? _hidden.Keys.ToList() : new List<string> { token };
            var restored = 0;
            foreach (var key in tokens)
            {
                List<Renderer> list;
                if (!_hidden.TryGetValue(key, out list)) throw new ArgumentException("Unknown visibility token " + key + ".");
                _hidden.Remove(key);
                foreach (var renderer in list)
                    if (renderer != null && !_hidden.Values.Any(other => other.Contains(renderer))) { renderer.enabled = true; restored++; }
            }
            return new JObject { ["restoredRenderers"] = restored, ["tokens"] = new JArray(tokens) };
        }

        private JObject SetPreview(JObject args)
        {
            var character = _plugin.Characters.Character((int?)args["objectId"] ?? 0);
            var mode = (string)args["mode"] ?? "neutral";
            if (mode != "neutral" && mode != "unlit" && mode != "none") throw new ArgumentException("mode must be neutral, unlit or none.");
            var postEffects = (bool?)args["postEffects"] ?? true;
            if (mode == "none" && postEffects) throw new ArgumentException("Nothing to preview: mode none with postEffects true.");
            var preview = new Preview();
            var token = "preview-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var result = new JObject { ["token"] = token, ["mode"] = mode,
                ["persistence"] = "runtime only; not written to Material Editor or the card; restored before any card save" };
            if (mode != "none")
            {
                var any = new[] { "rendererIds", "scopes", "accessorySlots", "hairSlots", "clothes" }.Any(k => args[k] != null) || (bool?)args["allAccessories"] == true;
                var renderers = any ? SampleCommands.Renderers(character, args, true)
                    : character.GetComponentsInChildren<Renderer>(true).Where(r => r.sharedMaterials.Any(m => m != null && !CharacterCommands.HelperShaders.Contains(m.shader.name))).ToList();
                if (_previews.Values.Any(p => p.Materials.Any(m => renderers.Contains(m.Key))))
                    throw new InvalidOperationException("Some of these renderers already have a preview; restore it first.");
                var shader = (mode == "unlit" ? new[] { "Unlit/Color" } : PreviewShaders).Select(Shader.Find).FirstOrDefault(s => s != null)
                    ?? throw new NotSupportedException("No neutral shader is included in this game build.");
                var color = args["color"] != null ? ParseColor(args["color"]) : new Color(0.72f, 0.72f, 0.72f, 1f);
                var index = 0;
                foreach (var renderer in renderers)
                {
                    var original = renderer.sharedMaterials;
                    var swapped = new Material[original.Length];
                    for (var i = 0; i < original.Length; i++)
                    {
                        if (original[i] == null || CharacterCommands.HelperShaders.Contains(original[i].shader.name)) { swapped[i] = original[i]; continue; }
                        var material = new Material(shader) { name = "StudioMCP preview" };
                        // Unlit gives each renderer its own hue so overlapping garments stay distinguishable.
                        material.color = mode == "unlit" ? Color.HSVToRGB((index * 0.137f) % 1f, 0.55f, 0.85f) : color;
                        swapped[i] = material;
                        preview.Created.Add(material);
                    }
                    preview.Materials.Add(new KeyValuePair<Renderer, Material[]>(renderer, original));
                    renderer.sharedMaterials = swapped;
                    index++;
                }
                result["shader"] = shader.name;
                result["renderers"] = Describe(character, renderers);
            }
            if (!postEffects)
            {
                var camera = Camera.main ?? throw new InvalidOperationException("No main camera.");
                foreach (var effect in camera.GetComponents<Behaviour>())
                    if (effect.enabled && !(effect is Camera) && IsImageEffect(effect.GetType())) { effect.enabled = false; preview.Effects.Add(effect); }
                result["disabledEffects"] = new JArray(preview.Effects.Select(e => e.GetType().FullName));
            }
            _previews[token] = preview;
            return result;
        }

        private static Color ParseColor(JToken token)
        {
            var array = token as JArray;
            if (array == null || array.Count < 3 || array.Count > 4) throw new ArgumentException("color requires [r, g, b] or [r, g, b, a] in 0..1.");
            return new Color(SceneCommands.Number(array[0]), SceneCommands.Number(array[1]), SceneCommands.Number(array[2]), array.Count > 3 ? SceneCommands.Number(array[3]) : 1f);
        }

        // Image effects render in OnRenderImage; the post-processing stack names itself.
        private static bool IsImageEffect(Type type)
        {
            // ChaCustom.CustomRender is the maker's own display buffer, not an effect.
            if (type.Namespace == "ChaCustom") return false;
            if (type.FullName.IndexOf("PostProcess", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                if (t.GetMethod("OnRenderImage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) != null) return true;
            return false;
        }

        private JObject RestorePreview(string token)
        {
            var tokens = string.IsNullOrEmpty(token) ? _previews.Keys.ToList() : new List<string> { token };
            foreach (var key in tokens) if (!_previews.ContainsKey(key)) throw new ArgumentException("Unknown preview token " + key + ".");
            foreach (var key in tokens) Undo(key);
            return new JObject { ["restored"] = new JArray(tokens) };
        }

        internal void RestoreAllPreviews() { foreach (var key in _previews.Keys.ToList()) Undo(key); }

        // A coordinate card's thumbnail shows the whole outfit: hidden parts come back for the capture, then hide again.
        internal void Suspend()
        {
            Suspended = true;
            foreach (var list in _hidden.Values)
                foreach (var renderer in list)
                    if (renderer != null) renderer.enabled = true;
        }

        internal void Resume() { Suspended = false; Tick(); }

        private void Undo(string key)
        {
            var preview = _previews[key];
            _previews.Remove(key);
            foreach (var pair in preview.Materials) if (pair.Key != null) pair.Key.sharedMaterials = pair.Value;
            foreach (var effect in preview.Effects) if (effect != null) effect.enabled = true;
            foreach (var material in preview.Created) if (material != null) UnityEngine.Object.Destroy(material);
        }

        // A reloaded character rebuilds its renderers; drop tokens that point only at destroyed ones.
        internal void Forget()
        {
            foreach (var key in _hidden.Keys.ToList()) if (_hidden[key].All(r => r == null)) _hidden.Remove(key);
            foreach (var key in _previews.Keys.ToList())
                if (_previews[key].Materials.All(m => m.Key == null) && _previews[key].Effects.All(e => e == null)) Undo(key);
        }

        private JObject State()
        {
            Forget();
            return new JObject { ["hidden"] = new JObject(_hidden.Select(p => new JProperty(p.Key, p.Value.Count(r => r != null)))),
                ["previews"] = new JObject(_previews.Select(p => new JProperty(p.Key, new JObject { ["renderers"] = p.Value.Materials.Count(m => m.Key != null),
                    ["disabledEffects"] = p.Value.Effects.Count }))) };
        }

        private static JArray Describe(ChaControl character, IEnumerable<Renderer> renderers)
        {
            var result = new JArray();
            foreach (var renderer in renderers)
            {
                int slot;
                GameObject part;
                var scope = MaterialCommands.PartOf(character, renderer.transform, out slot, out part);
                result.Add(new JObject { ["rendererId"] = renderer.GetInstanceID(), ["renderer"] = renderer.name, ["scope"] = scope, ["partSlot"] = slot });
            }
            return result;
        }
    }
}
