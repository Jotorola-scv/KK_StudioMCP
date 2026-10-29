// Added 2026-10-30 in the maker-support fork of KK_StudioMCP (AGPL-3.0); see README.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KKAPI.Maker;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudioMcp.Bridge
{
    // Alpha masks for hair pieces: pick the clipping part in 3D, rasterize it in UV space, bake it into the texture alpha.
    internal sealed class MaskCommands
    {
        private const int MaxSize = 4096;
        private readonly Plugin _plugin;
        private readonly Dictionary<string, Preview> _previews = new Dictionary<string, Preview>();
        private readonly Queue<string> _order = new Queue<string>();
        internal MaskCommands(Plugin plugin) { _plugin = plugin; }

        private sealed class Layer
        {
            internal Renderer Renderer;
            internal int MaterialSlot, Width, Height, CutTriangles, KeptTriangles, Skipped;
            internal Material Material;
            internal Texture Source;
            internal bool[] Cut, Used, Shared;
        }

        private sealed class Preview
        {
            internal string Session;
            internal int ObjectId, Slot;
            internal List<Layer> Layers;
        }

        internal JToken Execute(string method, JObject args)
        {
            if (method == "analysis.hair_mask") return MakePreview(args);
            if (method == "mask.apply") return Apply(args);
            throw new ArgumentException("Unsupported mask method: " + method);
        }

        private JObject MakePreview(JObject args)
        {
            var objectId = (int?)args["objectId"] ?? 0;
            var character = _plugin.Characters.Character(objectId);
            var slot = (int?)args["slot"] ?? throw new ArgumentException("slot (the hair accessory) is required.");
            var objects = character.GetAccessoryObjects();
            if (slot < 0 || slot >= objects.Length || objects[slot] == null) throw new ArgumentException("Accessory slot " + slot + " is empty.");
            var only = (int?)args["rendererId"];
            var select = Selector(character, args);
            var grow = Mathf.Clamp((int?)args["grow"] ?? 2, 0, 16);
            var protect = (bool?)args["protectShared"] ?? true;

            var layers = new Dictionary<Material, Layer>();
            var markers = new List<Vector3>();
            foreach (var renderer in objects[slot].GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || (only != null && renderer.GetInstanceID() != only)) continue;
                var surface = AnalysisCommands.Bake(renderer);
                if (surface == null || surface.Uv == null || surface.Uv.Length != surface.Points.Length) continue;
                var materials = renderer.sharedMaterials;
                for (var sub = 0; sub < surface.SubTriangles.Length && sub < materials.Length; sub++)
                {
                    var material = materials[sub];
                    if (material == null || !material.HasProperty("_MainTex") || material.GetTexture("_MainTex") == null) continue;
                    Layer layer;
                    if (!layers.TryGetValue(material, out layer))
                    {
                        var texture = material.GetTexture("_MainTex");
                        if (texture.width > MaxSize || texture.height > MaxSize) continue;
                        layers[material] = layer = new Layer { Renderer = renderer, MaterialSlot = sub, Material = material, Source = texture,
                            Width = texture.width, Height = texture.height,
                            Cut = new bool[texture.width * texture.height], Used = new bool[texture.width * texture.height] };
                    }
                    var scale = material.GetTextureScale("_MainTex");
                    var offset = material.GetTextureOffset("_MainTex");
                    var triangles = surface.SubTriangles[sub];
                    for (var t = 0; t + 2 < triangles.Length; t += 3)
                    {
                        int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                        var centroid = (surface.Points[a] + surface.Points[b] + surface.Points[c]) / 3f;
                        var cut = select(centroid);
                        if (cut) { layer.CutTriangles++; if (markers.Count < 300 && layer.CutTriangles % 3 == 1) markers.Add(centroid); }
                        else layer.KeptTriangles++;
                        if (!Rasterize(layer, cut ? layer.Cut : layer.Used, Uv(surface.Uv[a], scale, offset), Uv(surface.Uv[b], scale, offset), Uv(surface.Uv[c], scale, offset)))
                            layer.Skipped++;
                    }
                }
            }
            if (layers.Count == 0) throw new InvalidOperationException("Slot " + slot + " has no visible textured mesh to mask.");

            var id = Guid.NewGuid().ToString("N");
            var folder = Path.Combine(Path.Combine(_plugin.OutputRoot, "masks"), id);
            Directory.CreateDirectory(folder);
            var report = new JArray();
            string firstOverlay = null;
            foreach (var layer in layers.Values)
            {
                for (var i = 0; i < grow; i++) Dilate(layer);
                int cutPixels = 0, shared = 0;
                layer.Shared = new bool[layer.Cut.Length];
                for (var i = 0; i < layer.Cut.Length; i++)
                {
                    if (!layer.Cut[i]) continue;
                    if (layer.Used[i])
                    {
                        shared++;
                        layer.Shared[i] = true;
                        if (protect) { layer.Cut[i] = false; continue; }
                    }
                    cutPixels++;
                }
                var name = Clean(layer.Material.name.Replace(" (Instance)", ""));
                var overlay = Path.Combine(folder, name + "-overlay.png");
                File.WriteAllBytes(Path.Combine(folder, name + "-mask.png"), MaskPng(layer));
                File.WriteAllBytes(overlay, OverlayPng(layer));
                if (firstOverlay == null) firstOverlay = overlay;
                var total = layer.CutTriangles + layer.KeptTriangles;
                report.Add(new JObject { ["renderer"] = layer.Renderer.name, ["rendererId"] = layer.Renderer.GetInstanceID(), ["materialSlot"] = layer.MaterialSlot,
                    ["material"] = layer.Material.name, ["shader"] = layer.Material.shader.name, ["texture"] = layer.Source.name,
                    ["textureSize"] = new JArray(layer.Width, layer.Height),
                    ["cutTriangles"] = layer.CutTriangles, ["cutPercent"] = total == 0 ? 0 : Math.Round(100.0 * layer.CutTriangles / total, 1),
                    ["cutPixels"] = cutPixels, ["sharedPixels"] = shared,
                    // Pixels that a cut triangle shares with a kept one: masking them would also hide parts you keep.
                    ["sharedPercent"] = cutPixels + (protect ? shared : 0) == 0 ? 0 : Math.Round(100.0 * shared / (cutPixels + (protect ? shared : 0)), 1),
                    ["alphaClipHint"] = AlphaClipHint(layer.Material), ["skippedTriangles"] = layer.Skipped,
                    ["overlay"] = overlay });
            }
            _previews[id] = new Preview { Session = _plugin.Session, ObjectId = objectId, Slot = slot, Layers = layers.Values.ToList() };
            _order.Enqueue(id);
            while (_order.Count > 6) _previews.Remove(_order.Dequeue());
            return new JObject { ["previewId"] = id, ["slot"] = slot, ["folder"] = folder, ["protectShared"] = protect, ["layers"] = report,
                ["markers"] = new JArray(markers.Select(m => (JToken)SceneCommands.Json(m))),
                ["overlayPath"] = firstOverlay,
                ["notes"] = "Red in the overlay is cut; yellow is texture shared with kept geometry (kept when protectShared). " +
                    "Nothing is changed until hair_mask_apply. If sharedPercent is high, consider shortening the tail with bones_set on the " +
                    "accessory bones instead. Pass markers to capture_turntable to see the cut part in 3D." };
        }

        private JObject Apply(JObject args)
        {
            Preview preview;
            if (!_previews.TryGetValue((string)args["previewId"] ?? "", out preview)) throw new ArgumentException("Unknown or expired previewId; run hair_mask_preview again.");
            if (preview.Session != _plugin.Session) throw new InvalidOperationException("The character changed since the preview; run hair_mask_preview again.");
            var folder = Path.Combine(Path.Combine(_plugin.OutputRoot, "masks"), (string)args["previewId"]);
            var applied = new JArray();
            foreach (var layer in preview.Layers)
            {
                if (layer.Renderer == null || layer.Material == null) throw new InvalidOperationException("The hair piece was rebuilt; run hair_mask_preview again.");
                var readable = CharacterCommands.Readable(layer.Source);
                string path;
                try
                {
                    var pixels = readable.GetPixels32();
                    for (var i = 0; i < pixels.Length && i < layer.Cut.Length; i++)
                        if (layer.Cut[i]) pixels[i].a = 0;
                    readable.SetPixels32(pixels);
                    readable.Apply(false, false);
                    path = Path.Combine(folder, Clean(layer.Material.name.Replace(" (Instance)", "")) + "-masked.png");
                    File.WriteAllBytes(path, readable.EncodeToPNG());
                }
                finally { UnityEngine.Object.Destroy(readable); }
                // Through Material Editor, so the masked texture is stored in the card and material_reset restores the original.
                _plugin.Dispatch("me.import_texture", new JObject { ["sceneSession"] = _plugin.Session, ["objectId"] = preview.ObjectId,
                    ["rendererId"] = layer.Renderer.GetInstanceID(), ["slot"] = layer.MaterialSlot, ["property"] = "_MainTex", ["kind"] = "texture", ["path"] = path });
                applied.Add(new JObject { ["renderer"] = layer.Renderer.name, ["rendererId"] = layer.Renderer.GetInstanceID(),
                    ["materialSlot"] = layer.MaterialSlot, ["material"] = layer.Material.name, ["path"] = path,
                    ["alphaClipHint"] = AlphaClipHint(layer.Material) });
            }
            return new JObject { ["applied"] = applied, ["persistence"] = "material_editor",
                ["notes"] = "Check with capture_turntable. To undo, material_reset the _MainTex texture of each rendererId listed." };
        }

        // ---- selection ----

        private Func<Vector3, bool> Selector(ChaControl character, JObject args)
        {
            var mode = (string)args["mode"] ?? "inside";
            switch (mode)
            {
                case "below":
                    if (args["height"] == null) throw new ArgumentException("below needs height (world Y, e.g. from mesh bounds or markers).");
                    var height = SceneCommands.Number(args["height"]);
                    return p => p.y < height;
                case "region":
                    var center = SceneCommands.Vector(args["center"] ?? throw new ArgumentException("region needs center [x,y,z]."));
                    var radius = args["radius"] == null ? 0.05f : SceneCommands.Number(args["radius"]);
                    if (radius <= 0 || radius > 1) throw new ArgumentOutOfRangeException("radius", "Use 0-1 (world units, about metres).");
                    return p => (p - center).sqrMagnitude < radius * radius;
                case "inside":
                    var margin = args["marginMm"] == null ? 0.002f : SceneCommands.Number(args["marginMm"]) / 1000f;
                    var garments = args["accessorySlots"] is JArray list ? list.Select(t => (int)t).ToList() : new List<int>();
                    var obstacles = Obstacles(character, garments, (bool?)args["includeClothes"] ?? true);
                    return p =>
                    {
                        int owner, index;
                        if (!obstacles.Grid.Nearest(p, 0.05f, out owner, out index)) return false;
                        // Behind the nearest body or clothing surface: buried in it.
                        return Vector3.Dot(p - obstacles.Points[index], obstacles.Normals[index]) < -margin;
                    };
                default: throw new ArgumentException("mode is inside, below or region.");
            }
        }

        private sealed class Obstacle
        {
            internal readonly List<Vector3> Points = new List<Vector3>(), Normals = new List<Vector3>();
            internal readonly NearGrid Grid = new NearGrid(0.02f);
        }

        private static Obstacle Obstacles(ChaControl character, List<int> garments, bool clothes)
        {
            var result = new Obstacle();
            var renderers = new List<Renderer> { character.rendBody };
            foreach (var renderer in character.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                int slot;
                GameObject part;
                var scope = MaterialCommands.PartOf(character, renderer.transform, out slot, out part);
                if ((clothes && scope == "Clothing") || (scope == "Accessory" && garments.Contains(slot))) renderers.Add(renderer);
            }
            foreach (var renderer in renderers)
            {
                var surface = AnalysisCommands.Bake(renderer);
                if (surface == null) continue;
                for (var i = 0; i < surface.Points.Length; i++)
                {
                    result.Grid.Add(surface.Points[i], result.Points.Count);
                    result.Points.Add(surface.Points[i]);
                    result.Normals.Add(surface.Normals[i]);
                }
            }
            return result;
        }

        // ---- UV raster ----

        private static Vector2 Uv(Vector2 uv, Vector2 scale, Vector2 offset) => new Vector2(uv.x * scale.x + offset.x, uv.y * scale.y + offset.y);

        // Fills a UV triangle into the pixel mask, wrapping for tiled UVs; false when it is too large to be a hair card.
        private static bool Rasterize(Layer layer, bool[] target, Vector2 a, Vector2 b, Vector2 c)
        {
            int w = layer.Width, h = layer.Height;
            var pa = new Vector2(a.x * w, a.y * h);
            var pb = new Vector2(b.x * w, b.y * h);
            var pc = new Vector2(c.x * w, c.y * h);
            int minX = Mathf.FloorToInt(Mathf.Min(pa.x, Mathf.Min(pb.x, pc.x))), maxX = Mathf.CeilToInt(Mathf.Max(pa.x, Mathf.Max(pb.x, pc.x)));
            int minY = Mathf.FloorToInt(Mathf.Min(pa.y, Mathf.Min(pb.y, pc.y))), maxY = Mathf.CeilToInt(Mathf.Max(pa.y, Mathf.Max(pb.y, pc.y)));
            if (maxX - minX > w || maxY - minY > h) return false;
            var area = (pb.x - pa.x) * (pc.y - pa.y) - (pc.x - pa.x) * (pb.y - pa.y);
            if (Mathf.Abs(area) < 1e-6f)
            {
                // Degenerate in UV: still mark the pixel it lands on.
                Mark(target, w, h, Mathf.FloorToInt(pa.x), Mathf.FloorToInt(pa.y));
                return true;
            }
            for (var y = minY; y <= maxY; y++)
                for (var x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    var w0 = ((pb.x - px) * (pc.y - py) - (pc.x - px) * (pb.y - py)) / area;
                    var w1 = ((pc.x - px) * (pa.y - py) - (pa.x - px) * (pc.y - py)) / area;
                    var w2 = 1 - w0 - w1;
                    // A small tolerance keeps thin hair-card triangles from dropping out between pixel centres.
                    if (w0 >= -0.02f && w1 >= -0.02f && w2 >= -0.02f) Mark(target, w, h, x, y);
                }
            return true;
        }

        private static void Mark(bool[] target, int w, int h, int x, int y)
        {
            x = ((x % w) + w) % w;
            y = ((y % h) + h) % h;
            target[y * w + x] = true;
        }

        private static void Dilate(Layer layer)
        {
            int w = layer.Width, h = layer.Height;
            var source = (bool[])layer.Cut.Clone();
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    if (source[y * w + x]) continue;
                    if ((x > 0 && source[y * w + x - 1]) || (x < w - 1 && source[y * w + x + 1])
                        || (y > 0 && source[(y - 1) * w + x]) || (y < h - 1 && source[(y + 1) * w + x]))
                        layer.Cut[y * w + x] = true;
                }
        }

        // ---- images ----

        private static byte[] MaskPng(Layer layer)
        {
            var texture = new Texture2D(layer.Width, layer.Height, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color32[layer.Cut.Length];
                for (var i = 0; i < pixels.Length; i++) pixels[i] = layer.Cut[i] ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255);
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                return texture.EncodeToPNG();
            }
            finally { UnityEngine.Object.Destroy(texture); }
        }

        // The texture over grey (so transparent strands stay visible), cut pixels red, shared pixels yellow.
        private static byte[] OverlayPng(Layer layer)
        {
            var readable = CharacterCommands.Readable(layer.Source);
            try
            {
                var pixels = readable.GetPixels32();
                for (var i = 0; i < pixels.Length && i < layer.Cut.Length; i++)
                {
                    var p = pixels[i];
                    var a = p.a / 255f;
                    var color = new Color(Mathf.Lerp(0.24f, p.r / 255f, a), Mathf.Lerp(0.24f, p.g / 255f, a), Mathf.Lerp(0.24f, p.b / 255f, a), 1);
                    var grey = color.grayscale;
                    if (layer.Cut[i]) color = new Color(1, grey * 0.3f, grey * 0.3f, 1);
                    else if (layer.Shared[i]) color = new Color(1, 0.85f, grey * 0.3f, 1);
                    pixels[i] = color;
                }
                readable.SetPixels32(pixels);
                readable.Apply(false, false);
                return readable.EncodeToPNG();
            }
            finally { UnityEngine.Object.Destroy(readable); }
        }

        // Alpha masking only shows when the shader cuts or blends by texture alpha.
        private static string AlphaClipHint(Material material)
        {
            if (material.HasProperty("_Cutoff")) return "alpha test (_Cutoff = " + material.GetFloat("_Cutoff").ToString("0.##") + ")";
            if (material.renderQueue >= 2450 && material.renderQueue < 3000) return "alpha-test queue " + material.renderQueue;
            if (material.renderQueue >= 3000) return "transparent queue " + material.renderQueue;
            return "unknown: verify with capture_turntable after applying";
        }

        private static string Clean(string value) => new string(value.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());

        // Point grid for nearest-surface queries.
        internal sealed class NearGrid
        {
            private readonly float _cell;
            private readonly Dictionary<long, List<KeyValuePair<Vector3, int>>> _cells = new Dictionary<long, List<KeyValuePair<Vector3, int>>>();
            internal NearGrid(float cell) { _cell = cell; }
            private long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

            internal void Add(Vector3 p, int index)
            {
                var key = Key(Mathf.FloorToInt(p.x / _cell), Mathf.FloorToInt(p.y / _cell), Mathf.FloorToInt(p.z / _cell));
                List<KeyValuePair<Vector3, int>> list;
                if (!_cells.TryGetValue(key, out list)) _cells[key] = list = new List<KeyValuePair<Vector3, int>>();
                list.Add(new KeyValuePair<Vector3, int>(p, index));
            }

            internal bool Nearest(Vector3 p, float radius, out int owner, out int index)
            {
                owner = 0;
                index = -1;
                var best = radius * radius;
                var r = Mathf.CeilToInt(radius / _cell);
                int cx = Mathf.FloorToInt(p.x / _cell), cy = Mathf.FloorToInt(p.y / _cell), cz = Mathf.FloorToInt(p.z / _cell);
                for (var x = -r; x <= r; x++) for (var y = -r; y <= r; y++) for (var z = -r; z <= r; z++)
                {
                    List<KeyValuePair<Vector3, int>> list;
                    if (!_cells.TryGetValue(Key(cx + x, cy + y, cz + z), out list)) continue;
                    foreach (var e in list)
                    {
                        var d = (e.Key - p).sqrMagnitude;
                        if (d < best) { best = d; index = e.Value; }
                    }
                }
                return index >= 0;
            }
        }
    }
}
