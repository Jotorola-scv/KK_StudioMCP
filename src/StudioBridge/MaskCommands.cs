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
    // Alpha masks for hair pieces: pick the part to hide in 3D, rasterize it in UV space, and write it into the
    // shader's _AlphaMask (KK hair/clothes shaders) or the _MainTex alpha, through Material Editor.
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
            internal string Property;
            internal Texture Target, Reference;
            internal bool[] Cut, Used, Shared;
        }

        private sealed class Preview
        {
            internal string Id, Session;
            internal int ObjectId, Slot;
            internal List<Layer> Layers;
            internal bool Applied;
        }

        internal JToken Execute(string method, JObject args)
        {
            switch (method)
            {
                case "analysis.hair_mask": return MakePreview(args);
                case "mask.apply": return Apply(Find(args));
                case "mask.revert": return Revert(Find(args));
                default: throw new ArgumentException("Unsupported mask method: " + method);
            }
        }

        private Preview Find(JObject args)
        {
            Preview preview;
            if (!_previews.TryGetValue((string)args["previewId"] ?? "", out preview)) throw new ArgumentException("Unknown or expired previewId; run hair_mask_preview again.");
            if (preview.Session != _plugin.Session) throw new InvalidOperationException("The character changed since the preview; run hair_mask_preview again.");
            if (preview.Layers.Any(l => l.Renderer == null || l.Material == null)) throw new InvalidOperationException("The hair piece was rebuilt; run hair_mask_preview again.");
            return preview;
        }

        // ---- preview ----

        private JObject MakePreview(JObject args)
        {
            var objectId = (int?)args["objectId"] ?? 0;
            var character = _plugin.Characters.Character(objectId);
            var slot = (int?)args["slot"] ?? throw new ArgumentException("slot (the hair accessory) is required.");
            var objects = character.GetAccessoryObjects();
            if (slot < 0 || slot >= objects.Length || objects[slot] == null) throw new ArgumentException("Accessory slot " + slot + " is empty.");
            var only = (int?)args["rendererId"];
            var requested = (string)args["property"] ?? "auto";
            if (requested != "auto" && requested != "_AlphaMask" && requested != "_MainTex") throw new ArgumentException("property is auto, _AlphaMask or _MainTex.");
            var grow = Mathf.Clamp((int?)args["grow"] ?? 2, 0, 16);
            var protect = (bool?)args["protectShared"] ?? true;
            var mode = (string)args["mode"] ?? "tail";
            var pointTest = PointTest(character, args, mode);
            var face = AnalysisCommands.Bake(character.rendFace);
            var headCenter = face == null ? character.objHeadBone.transform.position : face.Points.Aggregate(Vector3.zero, (a, b) => a + b) / face.Points.Length;

            var layers = new Dictionary<Material, Layer>();
            var markers = new List<Vector3>();
            var stats = new Stats();
            foreach (var renderer in objects[slot].GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || (only != null && renderer.GetInstanceID() != only)) continue;
                var surface = AnalysisCommands.Bake(renderer);
                if (surface == null || surface.Uv == null || surface.Uv.Length != surface.Points.Length) continue;
                var cutTriangle = TriangleTest(surface, args, mode, pointTest, headCenter, stats);
                var materials = renderer.sharedMaterials;
                for (var sub = 0; sub < surface.SubTriangles.Length && sub < materials.Length; sub++)
                {
                    var layer = LayerFor(layers, renderer, sub, materials[sub], requested);
                    if (layer == null) continue;
                    var scale = layer.Material.GetTextureScale(layer.Property);
                    var offset = layer.Material.GetTextureOffset(layer.Property);
                    var triangles = surface.SubTriangles[sub];
                    for (var t = 0; t + 2 < triangles.Length; t += 3)
                    {
                        int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                        var cut = cutTriangle(a, b, c);
                        if (cut)
                        {
                            layer.CutTriangles++;
                            if (markers.Count < 300 && layer.CutTriangles % 3 == 1) markers.Add((surface.Points[a] + surface.Points[b] + surface.Points[c]) / 3f);
                        }
                        else layer.KeptTriangles++;
                        if (!Rasterize(layer, cut ? layer.Cut : layer.Used, Uv(surface.Uv[a], scale, offset), Uv(surface.Uv[b], scale, offset), Uv(surface.Uv[c], scale, offset)))
                            layer.Skipped++;
                    }
                }
            }
            if (layers.Count == 0) throw new InvalidOperationException("Slot " + slot + " has no visible mesh whose shader has _AlphaMask or a _MainTex.");

            var id = Guid.NewGuid().ToString("N");
            var folder = Folder(id);
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
                var name = Name(layer);
                var overlay = Path.Combine(folder, name + "-overlay.png");
                File.WriteAllBytes(Path.Combine(folder, name + "-mask.png"), MaskPng(layer));
                File.WriteAllBytes(overlay, OverlayPng(layer));
                if (firstOverlay == null) firstOverlay = overlay;
                var total = layer.CutTriangles + layer.KeptTriangles;
                var considered = cutPixels + (protect ? shared : 0);
                report.Add(new JObject { ["renderer"] = layer.Renderer.name, ["rendererId"] = layer.Renderer.GetInstanceID(), ["materialSlot"] = layer.MaterialSlot,
                    ["material"] = layer.Material.name, ["shader"] = layer.Material.shader.name, ["property"] = layer.Property,
                    ["textureSize"] = new JArray(layer.Width, layer.Height),
                    ["cutTriangles"] = layer.CutTriangles, ["cutPercent"] = total == 0 ? 0 : Math.Round(100.0 * layer.CutTriangles / total, 1),
                    ["cutPixels"] = cutPixels, ["sharedPixels"] = shared,
                    // Pixels a cut triangle shares with a kept one: masking them would also hide parts you keep.
                    ["sharedPercent"] = considered == 0 ? 0 : Math.Round(100.0 * shared / considered, 1),
                    ["alphaClipHint"] = AlphaClipHint(layer.Material), ["skippedTriangles"] = layer.Skipped, ["overlay"] = overlay });
            }
            var preview = new Preview { Id = id, Session = _plugin.Session, ObjectId = objectId, Slot = slot, Layers = layers.Values.ToList() };
            _previews[id] = preview;
            _order.Enqueue(id);
            while (_order.Count > 6) _previews.Remove(_order.Dequeue());
            var result = new JObject { ["previewId"] = id, ["slot"] = slot, ["mode"] = mode, ["folder"] = folder, ["protectShared"] = protect, ["layers"] = report,
                ["markers"] = new JArray(markers.Select(m => (JToken)SceneCommands.Json(m))), ["overlayPath"] = firstOverlay,
                ["notes"] = "Red in the overlay is cut; yellow is texture shared with kept geometry (kept when protectShared). Nothing changes " +
                    "until hair_mask_apply, and hair_mask_revert restores the textures. If sharedPercent is high, shorten the tail with " +
                    "bones_set on the accessory bones instead. Pass markers to capture_turntable to see the cut part in 3D." };
            if (mode == "tail" || mode == "length")
                result["hair"] = new JObject { ["vertices"] = stats.Nodes, ["cutVertices"] = stats.CutNodes,
                    ["cutPercent"] = stats.Nodes == 0 ? 0 : Math.Round(100.0 * stats.CutNodes / stats.Nodes, 1),
                    // tail: crossings into the body, and how much of the cut is outside it (the visible fix).
                    ["crossings"] = stats.Crossings, ["visibleCutVertices"] = stats.VisibleNodes };
            return result;
        }

        // Prefer the shader's own alpha mask; otherwise bake into the main texture's alpha.
        private static Layer LayerFor(Dictionary<Material, Layer> layers, Renderer renderer, int sub, Material material, string requested)
        {
            if (material == null) return null;
            Layer layer;
            if (layers.TryGetValue(material, out layer)) return layer;
            string property = null;
            if ((requested == "auto" || requested == "_AlphaMask") && material.HasProperty("_AlphaMask")) property = "_AlphaMask";
            else if ((requested == "auto" || requested == "_MainTex") && material.HasProperty("_MainTex") && material.GetTexture("_MainTex") != null) property = "_MainTex";
            if (property == null) return null;
            var target = material.GetTexture(property);
            var reference = new[] { "_MainTex", "_ColorMask", "_DetailMask", "_AlphaMask" }.Where(material.HasProperty)
                .Select(material.GetTexture).FirstOrDefault(t => t != null);
            var size = target ?? reference;
            int width = size != null ? size.width : 1024, height = size != null ? size.height : 1024;
            if (width > MaxSize || height > MaxSize) return null;
            layers[material] = layer = new Layer { Renderer = renderer, MaterialSlot = sub, Material = material, Property = property,
                Target = target, Reference = reference, Width = width, Height = height,
                Cut = new bool[width * height], Used = new bool[width * height] };
            return layer;
        }

        // ---- selection ----

        private Func<Vector3, bool> PointTest(ChaControl character, JObject args, string mode)
        {
            switch (mode)
            {
                case "below":
                    if (args["height"] == null) throw new ArgumentException("below needs height (world Y, e.g. from markers or mesh_export).");
                    var height = SceneCommands.Number(args["height"]);
                    return p => p.y < height;
                case "region":
                    var center = SceneCommands.Vector(args["center"] ?? throw new ArgumentException("region needs center [x,y,z]."));
                    var radius = args["radius"] == null ? 0.05f : SceneCommands.Number(args["radius"]);
                    if (radius <= 0 || radius > 1) throw new ArgumentOutOfRangeException("radius", "Use 0-1 (world units, about metres).");
                    return p => (p - center).sqrMagnitude < radius * radius;
                case "inside":
                case "tail":
                    // tail ignores hair that merely grazes a surface; it looks for strands that really go through.
                    var margin = (args["marginMm"] == null ? (mode == "tail" ? 5f : 2f) : SceneCommands.Number(args["marginMm"])) / 1000f;
                    var garments = args["accessorySlots"] is JArray list ? list.Select(t => (int)t).ToList() : new List<int>();
                    var obstacles = Obstacles(character, garments, (bool?)args["includeClothes"] ?? true);
                    return p =>
                    {
                        int owner, index;
                        if (!obstacles.Grid.Nearest(p, 0.05f, out owner, out index)) return false;
                        // Hair whose nearest surface is the head is on the scalp, not through the body. Without the head,
                        // the body mesh's open neck edge would make hair at the back of the head look buried.
                        if (obstacles.Head[index]) return false;
                        // Behind the nearest body or clothing surface: buried in it.
                        return Vector3.Dot(p - obstacles.Points[index], obstacles.Normals[index]) < -margin;
                    };
                case "length": return null;
                default: throw new ArgumentException("mode is tail, length, inside, below or region.");
            }
        }

        // tail and length follow the hair from its root (the point nearest the head) along a shortest-path tree over the
        // mesh surface. Whole hairstyles are often one connected mesh, so cuts take a vertex's downstream branch, not a component.
        private static Func<int, int, int, bool> TriangleTest(AnalysisCommands.Surface surface, JObject args, string mode, Func<Vector3, bool> pointTest,
            Vector3 headCenter, Stats stats)
        {
            if (mode != "tail" && mode != "length")
                return (a, b, c) => pointTest((surface.Points[a] + surface.Points[b] + surface.Points[c]) / 3f);

            var tree = new StrandTree(surface, headCenter);
            var cut = new bool[tree.Nodes];
            if (mode == "tail")
            {
                // Roots buried in the scalp or nape are normal; only a crossing further down the hair counts.
                var ignore = args["ignoreNearRootCm"] == null ? 0.06f : SceneCommands.Number(args["ignoreNearRootCm"]) / 100f;
                var backoff = args["backoffCm"] == null ? 0.01f : SceneCommands.Number(args["backoffCm"]) / 100f;
                var inside = new bool[tree.Nodes];
                for (var n = 0; n < tree.Nodes; n++) inside[n] = tree.Distance[n] > ignore && pointTest(tree.Position[n]);
                for (var n = 0; n < tree.Nodes; n++)
                {
                    // Only the first crossing on each branch: one whose parent chain is not already inside.
                    if (!inside[n] || cut[n] || (tree.Parent[n] >= 0 && inside[tree.Parent[n]])) continue;
                    var from = n;
                    while (tree.Parent[from] >= 0 && tree.Distance[n] - tree.Distance[tree.Parent[from]] < backoff && tree.Distance[tree.Parent[from]] > ignore)
                        from = tree.Parent[from];
                    tree.MarkBranch(from, cut);
                    stats.Crossings++;
                }
                // What actually changes on screen: cut hair that is outside the body, i.e. the part that pierced through.
                for (var n = 0; n < tree.Nodes; n++)
                    if (cut[n] && !pointTest(tree.Position[n])) stats.VisibleNodes++;
            }
            else
            {
                var keep = args["keep"] == null ? (float?)null : SceneCommands.Number(args["keep"]);
                var keepCm = args["keepCm"] == null ? (float?)null : SceneCommands.Number(args["keepCm"]);
                if (keep == null && keepCm == null) throw new ArgumentException("length needs keep (fraction 0-1 of each strand) or keepCm (from the piece's root).");
                if (keep != null && (keep <= 0 || keep >= 1)) throw new ArgumentOutOfRangeException("keep", "Use a fraction between 0 and 1.");
                for (var n = 0; n < tree.Nodes; n++)
                    cut[n] = keepCm != null ? tree.Distance[n] > keepCm.Value / 100f : tree.Distance[n] > keep.Value * tree.BranchLength[n];
            }
            for (var n = 0; n < tree.Nodes; n++) if (cut[n]) stats.CutNodes++;
            stats.Nodes += tree.Nodes;
            var node = tree.Node;
            return (a, b, c) => cut[node[a]] || cut[node[b]] || cut[node[c]];
        }

        private sealed class Stats { internal int Nodes, CutNodes, VisibleNodes, Crossings; }

        // Shortest-path tree from the vertex nearest the head, over a position-welded graph (UV seams split vertices).
        private sealed class StrandTree
        {
            internal readonly int Nodes;
            internal readonly int[] Node, Parent;
            internal readonly Vector3[] Position;
            internal readonly float[] Distance, BranchLength;
            private readonly List<int>[] _children;

            internal StrandTree(AnalysisCommands.Surface surface, Vector3 headCenter)
            {
                var count = surface.Points.Length;
                Node = new int[count];
                var keys = new Dictionary<long, int>();
                for (var v = 0; v < count; v++)
                {
                    var p = surface.Points[v] * 10000f;
                    var key = ((long)(Mathf.RoundToInt(p.x) & 0x1FFFFF) << 42) | ((long)(Mathf.RoundToInt(p.y) & 0x1FFFFF) << 21) | (long)(Mathf.RoundToInt(p.z) & 0x1FFFFF);
                    int id;
                    if (!keys.TryGetValue(key, out id)) keys[key] = id = keys.Count;
                    Node[v] = id;
                }
                Nodes = keys.Count;
                Position = new Vector3[Nodes];
                for (var v = 0; v < count; v++) Position[Node[v]] = surface.Points[v];
                var edges = new List<int>[Nodes];
                for (var n = 0; n < Nodes; n++) edges[n] = new List<int>();
                var tris = surface.Triangles;
                for (var t = 0; t + 2 < tris.Length; t += 3)
                    for (var k = 0; k < 3; k++)
                    {
                        int a = Node[tris[t + k]], b = Node[tris[t + (k + 1) % 3]];
                        if (a == b) continue;
                        edges[a].Add(b);
                        edges[b].Add(a);
                    }
                Distance = new float[Nodes];
                Parent = new int[Nodes];
                var seen = new bool[Nodes];
                for (var n = 0; n < Nodes; n++) { Distance[n] = float.MaxValue; Parent[n] = -1; }
                // Each connected piece gets its own root: the vertex closest to the head.
                for (var start = 0; start < Nodes; start++)
                {
                    if (seen[start]) continue;
                    var members = new List<int> { start };
                    seen[start] = true;
                    for (var i = 0; i < members.Count; i++)
                        foreach (var other in edges[members[i]])
                            if (!seen[other]) { seen[other] = true; members.Add(other); }
                    var root = members.OrderBy(m => (Position[m] - headCenter).sqrMagnitude).First();
                    var heap = new MinHeap();
                    Distance[root] = 0;
                    heap.Push(0, root);
                    while (heap.Count > 0)
                    {
                        float d;
                        var current = heap.Pop(out d);
                        if (d > Distance[current]) continue;
                        foreach (var other in edges[current])
                        {
                            var next = d + (Position[other] - Position[current]).magnitude;
                            if (next < Distance[other]) { Distance[other] = next; Parent[other] = current; heap.Push(next, other); }
                        }
                    }
                }
                _children = new List<int>[Nodes];
                for (var n = 0; n < Nodes; n++) _children[n] = new List<int>();
                for (var n = 0; n < Nodes; n++) if (Parent[n] >= 0) _children[Parent[n]].Add(n);
                // Branch length: how far the hair through this vertex reaches, i.e. its strand's tip distance.
                BranchLength = (float[])Distance.Clone();
                foreach (var n in Enumerable.Range(0, Nodes).OrderByDescending(n => Distance[n]))
                    if (Parent[n] >= 0) BranchLength[Parent[n]] = Mathf.Max(BranchLength[Parent[n]], BranchLength[n]);
            }

            internal void MarkBranch(int from, bool[] marks)
            {
                var stack = new Stack<int>();
                stack.Push(from);
                while (stack.Count > 0)
                {
                    var n = stack.Pop();
                    if (marks[n]) continue;
                    marks[n] = true;
                    foreach (var child in _children[n]) stack.Push(child);
                }
            }
        }

        private sealed class MinHeap
        {
            private readonly List<KeyValuePair<float, int>> _items = new List<KeyValuePair<float, int>>();
            internal int Count => _items.Count;

            internal void Push(float key, int value)
            {
                _items.Add(new KeyValuePair<float, int>(key, value));
                for (var i = _items.Count - 1; i > 0;)
                {
                    var parent = (i - 1) / 2;
                    if (_items[parent].Key <= _items[i].Key) break;
                    var swap = _items[parent]; _items[parent] = _items[i]; _items[i] = swap;
                    i = parent;
                }
            }

            internal int Pop(out float key)
            {
                var top = _items[0];
                var last = _items[_items.Count - 1];
                _items.RemoveAt(_items.Count - 1);
                if (_items.Count > 0)
                {
                    _items[0] = last;
                    for (var i = 0; ;)
                    {
                        int left = i * 2 + 1, right = left + 1, smallest = i;
                        if (left < _items.Count && _items[left].Key < _items[smallest].Key) smallest = left;
                        if (right < _items.Count && _items[right].Key < _items[smallest].Key) smallest = right;
                        if (smallest == i) break;
                        var swap = _items[smallest]; _items[smallest] = _items[i]; _items[i] = swap;
                        i = smallest;
                    }
                }
                key = top.Key;
                return top.Value;
            }
        }

        private sealed class Obstacle
        {
            internal readonly List<Vector3> Points = new List<Vector3>(), Normals = new List<Vector3>();
            internal readonly List<bool> Head = new List<bool>();
            internal readonly NearGrid Grid = new NearGrid(0.02f);
        }

        private static Obstacle Obstacles(ChaControl character, List<int> garments, bool clothes)
        {
            var result = new Obstacle();
            var renderers = new List<Renderer> { character.rendFace, character.rendBody };
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
                var head = renderer == character.rendFace;
                for (var i = 0; i < surface.Points.Length; i++)
                {
                    result.Grid.Add(surface.Points[i], result.Points.Count);
                    result.Points.Add(surface.Points[i]);
                    result.Normals.Add(surface.Normals[i]);
                    result.Head.Add(head);
                }
            }
            return result;
        }

        // ---- apply / revert ----

        private JObject Apply(Preview preview)
        {
            if (preview.Applied) throw new InvalidOperationException("This preview is already applied; hair_mask_revert first or make a new preview.");
            var applied = new JArray();
            foreach (var layer in preview.Layers)
            {
                var path = Path.Combine(Folder(preview.Id), Name(layer) + "-applied.png");
                File.WriteAllBytes(path, MaskedPng(layer));
                // Through Material Editor, so the texture is stored in the card.
                _plugin.Dispatch("me.import_texture", new JObject { ["sceneSession"] = _plugin.Session, ["objectId"] = preview.ObjectId,
                    ["rendererId"] = layer.Renderer.GetInstanceID(), ["slot"] = layer.MaterialSlot, ["property"] = layer.Property, ["kind"] = "texture", ["path"] = path });
                applied.Add(new JObject { ["renderer"] = layer.Renderer.name, ["rendererId"] = layer.Renderer.GetInstanceID(),
                    ["materialSlot"] = layer.MaterialSlot, ["property"] = layer.Property, ["path"] = path, ["alphaClipHint"] = AlphaClipHint(layer.Material) });
            }
            preview.Applied = true;
            return new JObject { ["previewId"] = preview.Id, ["applied"] = applied, ["persistence"] = "material_editor",
                ["notes"] = "Check with capture_turntable; undo with hair_mask_revert (previewId)." };
        }

        // Removes the Material Editor texture and puts the original back now; ME alone only restores it on reload.
        private JObject Revert(Preview preview)
        {
            var reverted = new JArray();
            foreach (var layer in preview.Layers)
            {
                try
                {
                    _plugin.Dispatch("me.reset", new JObject { ["sceneSession"] = _plugin.Session, ["objectId"] = preview.ObjectId,
                        ["rendererId"] = layer.Renderer.GetInstanceID(), ["slot"] = layer.MaterialSlot, ["property"] = layer.Property, ["kind"] = "texture" });
                }
                catch (Exception error) { UnityEngine.Debug.LogWarning("[StudioMCP] Mask revert: " + error.GetBaseException().Message); }
                foreach (var material in layer.Renderer.sharedMaterials.Where(m => m != null && m.name == layer.Material.name))
                    material.SetTexture(layer.Property, layer.Target);
                reverted.Add(new JObject { ["renderer"] = layer.Renderer.name, ["property"] = layer.Property });
            }
            preview.Applied = false;
            return new JObject { ["previewId"] = preview.Id, ["reverted"] = reverted };
        }

        // _AlphaMask: the existing mask (or white) with the cut in every channel; _MainTex: the texture with alpha 0 where cut.
        private static byte[] MaskedPng(Layer layer)
        {
            var texture = layer.Target != null ? CharacterCommands.Readable(layer.Target) : new Texture2D(layer.Width, layer.Height, TextureFormat.RGBA32, false);
            try
            {
                var pixels = layer.Target != null ? texture.GetPixels32() : Enumerable.Repeat(new Color32(255, 255, 255, 255), layer.Width * layer.Height).ToArray();
                for (var i = 0; i < pixels.Length && i < layer.Cut.Length; i++)
                {
                    if (!layer.Cut[i]) continue;
                    pixels[i] = layer.Property == "_AlphaMask" ? new Color32(0, 0, 0, 0) : new Color32(pixels[i].r, pixels[i].g, pixels[i].b, 0);
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                return texture.EncodeToPNG();
            }
            finally { UnityEngine.Object.Destroy(texture); }
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

        // The reference texture over grey, kept UV area lighter, cut pixels red, shared pixels yellow.
        private static byte[] OverlayPng(Layer layer)
        {
            var sameSize = layer.Reference != null && layer.Reference.width == layer.Width && layer.Reference.height == layer.Height;
            var texture = sameSize ? CharacterCommands.Readable(layer.Reference) : new Texture2D(layer.Width, layer.Height, TextureFormat.RGBA32, false);
            try
            {
                var pixels = sameSize ? texture.GetPixels32() : new Color32[layer.Width * layer.Height];
                for (var i = 0; i < pixels.Length && i < layer.Cut.Length; i++)
                {
                    var p = pixels[i];
                    var a = sameSize ? p.a / 255f : 0;
                    var color = new Color(Mathf.Lerp(0.24f, p.r / 255f, a), Mathf.Lerp(0.24f, p.g / 255f, a), Mathf.Lerp(0.24f, p.b / 255f, a), 1);
                    var grey = color.grayscale;
                    if (layer.Cut[i]) color = new Color(1, grey * 0.3f, grey * 0.3f, 1);
                    else if (layer.Shared[i]) color = new Color(1, 0.85f, grey * 0.3f, 1);
                    else if (layer.Used[i] && !sameSize) color = new Color(0.55f, 0.6f, 0.75f, 1);
                    pixels[i] = color;
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                return texture.EncodeToPNG();
            }
            finally { UnityEngine.Object.Destroy(texture); }
        }

        // Alpha masking only shows when the shader cuts or blends by alpha.
        private static string AlphaClipHint(Material material)
        {
            if (material.HasProperty("_Cutoff")) return "alpha test (_Cutoff = " + material.GetFloat("_Cutoff").ToString("0.##") + ")";
            if (material.renderQueue >= 2450 && material.renderQueue < 3000) return "alpha-test queue " + material.renderQueue;
            if (material.renderQueue >= 3000) return "transparent queue " + material.renderQueue;
            return "unknown: verify with capture_turntable after applying";
        }

        private string Folder(string id) => Path.Combine(Path.Combine(_plugin.OutputRoot, "masks"), id);
        private static string Name(Layer layer) => Clean(layer.Material.name.Replace(" (Instance)", "")) + layer.Property;
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
