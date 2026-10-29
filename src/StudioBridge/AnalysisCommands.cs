// Added 2026-10-29 in the maker-support fork of KK_StudioMCP (AGPL-3.0); see README.md.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using KKAPI.Maker;
using UnityEngine;

namespace StudioMcp.Bridge
{
    // Geometry checks on baked meshes: scalp coverage and seams for spliced hair, and clothing poke-through sweeps.
    internal sealed class AnalysisCommands
    {
        private const float CoverRay = 0.06f;      // a scalp point counts as covered when hair lies within 6 cm along its normal
        private const float FloatGap = 0.004f;     // a hair piece whose closest point is > 4 mm from the head floats
        private const float SinkDepth = 0.004f;    // ... and one that dips > 4 mm inside the head is sunk in
        private readonly Plugin _plugin;
        internal AnalysisCommands(Plugin plugin) { _plugin = plugin; }

        internal JToken Execute(string method, JObject args)
        {
            var character = _plugin.Characters.Character((int?)args["objectId"] ?? 0);
            if (method == "analysis.hair") return Hair(character, args);
            if (method == "analysis.clipping_sweep") return Sweep(character, args);
            throw new ArgumentException("Unsupported analysis method: " + method);
        }

        internal sealed class Surface
        {
            internal string Name, Scope;
            internal int Slot;
            internal Renderer Renderer;
            internal Vector3[] Points, Normals;
            internal int[] Triangles;
        }

        // World-space vertices, normals and triangles of the renderer's current (posed, shaped) mesh.
        internal static Surface Bake(Renderer renderer)
        {
            Mesh mesh;
            Matrix4x4 matrix;
            var skinned = renderer as SkinnedMeshRenderer;
            var baked = skinned != null;
            if (baked)
            {
                if (skinned.sharedMesh == null) return null;
                mesh = new Mesh();
                skinned.BakeMesh(mesh);
                matrix = Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one);
            }
            else
            {
                var filter = renderer.GetComponent<MeshFilter>();
                mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || !mesh.isReadable) return null;
                matrix = renderer.transform.localToWorldMatrix;
            }
            try
            {
                var vertices = mesh.vertices;
                var normals = mesh.normals;
                var surface = new Surface { Renderer = renderer, Name = renderer.name, Points = new Vector3[vertices.Length],
                    Normals = new Vector3[vertices.Length], Triangles = mesh.triangles };
                for (var i = 0; i < vertices.Length; i++)
                {
                    surface.Points[i] = matrix.MultiplyPoint3x4(vertices[i]);
                    surface.Normals[i] = i < normals.Length ? matrix.MultiplyVector(normals[i]).normalized : Vector3.zero;
                }
                return surface;
            }
            finally { if (baked) UnityEngine.Object.Destroy(mesh); }
        }

        private static bool Visible(Renderer r) => r.enabled && r.gameObject.activeInHierarchy;

        // ---- spliced hair ----

        private JObject Hair(ChaControl character, JObject args)
        {
            var head = Bake(character.rendFace) ?? throw new InvalidOperationException("The face mesh cannot be read.");
            var extra = args["slots"] is JArray list ? list.Select(t => (int)t).ToList() : new List<int>();
            var hair = HairSurfaces(character, extra);
            if (hair.Count == 0) throw new InvalidOperationException("No visible hair or hair accessories to analyse.");

            // Head frame: character up/forward axes, origin at the face mesh centre.
            var rotation = Quaternion.Inverse(character.transform.rotation);
            var center = character.rendFace.bounds.center;
            var local = head.Points.Select(p => rotation * (p - center)).ToArray();
            float minY = local.Min(p => p.y), maxY = local.Max(p => p.y), minZ = local.Min(p => p.z), maxZ = local.Max(p => p.z);
            var regions = new Dictionary<string, int[]> { ["top"] = new int[2], ["back"] = new int[2], ["left"] = new int[2], ["right"] = new int[2], ["forehead"] = new int[2] };
            var gaps = new List<int>();
            var scalp = new List<int>();

            var colliders = new List<MeshCollider>();
#if !KK
            var backfaces = Physics.queriesHitBackfaces;
#endif
            try
            {
                foreach (var surface in hair)
                {
                    var holder = new GameObject("StudioMCP_hair_probe") { hideFlags = HideFlags.HideAndDontSave };
                    var mesh = new Mesh { vertices = surface.Points, triangles = surface.Triangles };
#if !KK
                    if (surface.Points.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
#endif
                    var collider = holder.AddComponent<MeshCollider>();
                    collider.sharedMesh = mesh;
                    colliders.Add(collider);
                }
#if !KK
                Physics.queriesHitBackfaces = true;
#endif
                var own = new HashSet<Collider>(colliders.Cast<Collider>());
                for (var i = 0; i < local.Length; i++)
                {
                    var region = Region(local[i], rotation * head.Normals[i], minY, maxY, minZ, maxZ);
                    if (region == null) continue;
                    scalp.Add(i);
                    var origin = head.Points[i] + head.Normals[i] * 0.0005f;
                    // Hair cards are single-sided, so also look back from outside; Unity 5.6 (KK) cannot hit back faces.
                    var covered = Physics.RaycastAll(origin, head.Normals[i], CoverRay, ~0, QueryTriggerInteraction.Collide).Any(h => own.Contains(h.collider))
                        || Physics.RaycastAll(origin + head.Normals[i] * CoverRay, -head.Normals[i], CoverRay, ~0, QueryTriggerInteraction.Collide).Any(h => own.Contains(h.collider));
                    regions[region][0]++;
                    if (covered) regions[region][1]++;
                    else if (region != "forehead") gaps.Add(i);
                }
            }
            finally
            {
#if !KK
                Physics.queriesHitBackfaces = backfaces;
#endif
                foreach (var collider in colliders) { UnityEngine.Object.Destroy(collider.sharedMesh); UnityEngine.Object.Destroy(collider.gameObject); }
            }

            var hairGrid = new Grid(0.02f);
            for (var s = 0; s < hair.Count; s++)
                for (var i = 0; i < hair[s].Points.Length; i++) hairGrid.Add(hair[s].Points[i], s, i);
            var clusters = Cluster(gaps.Select(i => head.Points[i]).ToList(), 0.015f).OrderByDescending(c => c.Count).Take(10).Select(c =>
            {
                var centroid = c.Aggregate(Vector3.zero, (a, b) => a + b) / c.Count;
                var region = Region(rotation * (centroid - center), Vector3.zero, minY, maxY, minZ, maxZ, ignoreNormal: true) ?? "scalp";
                var entry = new JObject { ["region"] = region, ["points"] = c.Count, ["position"] = SceneCommands.Json(centroid) };
                int piece, vertex;
                if (hairGrid.Nearest(centroid, 0.08f, out piece, out vertex))
                {
                    var nearest = hair[piece];
                    entry["nearestPiece"] = nearest.Name;
                    entry["nearestScope"] = nearest.Scope;
                    if (nearest.Scope == "Accessory")
                    {
                        entry["slot"] = nearest.Slot;
                        entry["suggestedMove"] = SceneCommands.Json(MoveUnits(character, nearest.Slot, centroid - nearest.Points[vertex]));
                    }
                }
                return entry;
            }).ToList();

            var headGrid = new Grid(0.02f);
            for (var i = 0; i < head.Points.Length; i++) headGrid.Add(head.Points[i], 0, i);
            var pieces = new JArray(hair.Select(surface => Seam(character, surface, head, headGrid)));

            int total = regions.Where(r => r.Key != "forehead").Sum(r => r.Value[0]);
            int covered2 = regions.Where(r => r.Key != "forehead").Sum(r => r.Value[1]);
            return new JObject {
                ["coverage"] = total == 0 ? 0 : Math.Round(100.0 * covered2 / total, 1),
                ["regions"] = new JObject(regions.Select(r => new JProperty(r.Key, new JObject { ["samples"] = r.Value[0],
                    ["coverage"] = r.Value[0] == 0 ? 0 : Math.Round(100.0 * r.Value[1] / r.Value[0], 1) }))),
                ["gaps"] = new JArray(clusters), ["pieces"] = pieces,
                ["markers"] = new JArray(clusters.Select(c => c["position"])),
                ["notes"] = "Coverage excludes the forehead (bangs are a style choice). suggestedMove is in accessory_set position units " +
                    "(move0, add=true) and points from the nearest hair piece toward the gap. Pass markers to capture_turntable to see the gaps." };
        }

        private static List<Surface> HairSurfaces(ChaControl character, List<int> extraSlots)
        {
            var result = new List<Surface>();
            foreach (var renderer in character.GetComponentsInChildren<Renderer>(true))
            {
                if (!Visible(renderer)) continue;
                int slot;
                GameObject part;
                var scope = MaterialCommands.PartOf(character, renderer.transform, out slot, out part);
                var isHair = scope == "Hair" || (scope == "Accessory" && (part.GetComponent<ChaCustomHairComponent>() != null || extraSlots.Contains(slot)));
                if (!isHair) continue;
                var surface = Bake(renderer);
                if (surface == null || surface.Triangles.Length == 0) continue;
                surface.Scope = scope;
                surface.Slot = slot;
                result.Add(surface);
            }
            return result;
        }

        // Scalp regions in the head frame; the face front and everything below the ears are not scalp.
        private static string Region(Vector3 p, Vector3 n, float minY, float maxY, float minZ, float maxZ, bool ignoreNormal = false)
        {
            var y = (p.y - minY) / Mathf.Max(1e-4f, maxY - minY);
            var z = (p.z - minZ) / Mathf.Max(1e-4f, maxZ - minZ);
            if (!ignoreNormal && n.y < -0.3f) return null;
            if (y > 0.86f) return "top";
            if (z < 0.4f && y > 0.38f) return "back";
            if (z > 0.6f && (ignoreNormal || n.z > 0.2f)) return y > 0.72f ? "forehead" : null;
            if (y > 0.64f) return p.x > 0 ? "left" : "right";
            return null;
        }

        // Closest approach to the head, how deep the piece dips inside it, and a move that would fix either.
        private JObject Seam(ChaControl character, Surface surface, Surface head, Grid headGrid)
        {
            var step = Math.Max(1, surface.Points.Length / 4000);
            float closest = float.MaxValue, deepest = 0;
            Vector3 closestDelta = Vector3.zero, sinkNormal = Vector3.zero;
            int inside = 0, sampled = 0;
            for (var i = 0; i < surface.Points.Length; i += step)
            {
                int piece, vertex;
                if (!headGrid.Nearest(surface.Points[i], 0.05f, out piece, out vertex)) continue;
                sampled++;
                var delta = surface.Points[i] - head.Points[vertex];
                var distance = delta.magnitude;
                var signed = Vector3.Dot(delta, head.Normals[vertex]);
                if (signed < 0)
                {
                    inside++;
                    if (-signed > deepest) { deepest = -signed; sinkNormal = head.Normals[vertex]; }
                }
                else if (distance < closest) { closest = distance; closestDelta = -delta; }
            }
            var entry = new JObject { ["piece"] = surface.Name, ["scope"] = surface.Scope, ["sampled"] = sampled,
                ["closestGapMm"] = closest == float.MaxValue ? null : (JToken)Math.Round(closest * 1000, 1),
                ["insidePercent"] = sampled == 0 ? 0 : Math.Round(100.0 * inside / sampled, 1), ["maxDepthMm"] = Math.Round(deepest * 1000, 1) };
            if (surface.Scope == "Accessory") entry["slot"] = surface.Slot;
            string verdict = sampled == 0 ? "far from head" : "ok";
            Vector3? fix = null;
            if (sampled > 0 && inside == 0 && closest > FloatGap) { verdict = "floating"; fix = closestDelta.normalized * (closest - 0.001f); }
            else if (deepest > SinkDepth) { verdict = "sunk in"; fix = sinkNormal * (deepest - 0.001f); }
            entry["verdict"] = verdict;
            if (fix != null && surface.Scope == "Accessory") entry["suggestedMove"] = SceneCommands.Json(MoveUnits(character, surface.Slot, fix.Value));
            return entry;
        }

        // accessory_set position values are local to N_move's parent and scaled by 100 (UpdateAccessoryMoveFromInfo applies * 0.01).
        private static Vector3 MoveUnits(ChaControl character, int slot, Vector3 world)
        {
            var move = AccessoryCommands.MoveTransform(character, slot, 0);
            var space = move != null && move.parent != null ? move.parent : character.GetAccessoryObjects()[slot].transform;
            var local = space.InverseTransformVector(world) * 100f;
            return new Vector3((float)Math.Round(local.x, 2), (float)Math.Round(local.y, 2), (float)Math.Round(local.z, 2));
        }

        private static List<List<Vector3>> Cluster(List<Vector3> points, float radius)
        {
            var clusters = new List<List<Vector3>>();
            var grid = new Grid(radius);
            for (var i = 0; i < points.Count; i++) grid.Add(points[i], 0, i);
            var seen = new bool[points.Count];
            for (var i = 0; i < points.Count; i++)
            {
                if (seen[i]) continue;
                var cluster = new List<Vector3>();
                var queue = new Queue<int>();
                queue.Enqueue(i);
                seen[i] = true;
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    cluster.Add(points[current]);
                    foreach (var other in grid.Within(points[current], radius))
                        if (!seen[other]) { seen[other] = true; queue.Enqueue(other); }
                }
                clusters.Add(cluster);
            }
            return clusters;
        }

        // ---- clothing poke-through ----

        private JObject Sweep(ChaControl character, JObject args)
        {
            var sliders = args["sliders"] as JObject ?? new JObject { ["bust_size"] = new JArray(0, 1), ["waist_position"] = new JArray(0, 1),
                ["upper_thigh_width"] = new JArray(0, 1), ["butt"] = new JArray(0, 1) };
            var steps = new List<KeyValuePair<int, float>>();
            foreach (var slider in sliders.Properties())
            {
                var index = CharacterCommands.BodyIndex(slider.Name);
                var values = slider.Value as JArray ?? throw new ArgumentException(slider.Name + " needs an array of values.");
                foreach (var value in values)
                {
                    var number = SceneCommands.Number(value);
                    if (number < -1 || number > 2) throw new ArgumentOutOfRangeException(slider.Name, "Sweep values must be within -1..2.");
                    steps.Add(new KeyValuePair<int, float>(index, number));
                }
            }
            if (steps.Count == 0 || steps.Count > 16) throw new ArgumentException("A sweep needs 1-16 slider values in total.");
            var tolerance = args["toleranceMm"] == null ? 1.5f : SceneCommands.Number(args["toleranceMm"]) / 1000f;

            var original = (float[])character.fileBody.shapeValueBody.Clone();
            var results = new JArray();
            try
            {
                results.Add(Step(character, "current", null, tolerance));
                foreach (var step in steps)
                {
                    Array.Copy(original, character.fileBody.shapeValueBody, original.Length);
                    character.SetShapeBodyValue(step.Key, step.Value);
                    // Applies the bone changes now, so the baked meshes below see this step's body.
                    character.UpdateShapeBody();
                    results.Add(Step(character, CharacterCommands.BodyKey(step.Key), step.Value, tolerance));
                }
            }
            finally
            {
                for (var i = 0; i < original.Length; i++) character.SetShapeBodyValue(i, original[i]);
                character.UpdateShapeBody();
            }
            return new JObject { ["steps"] = results, ["toleranceMm"] = tolerance * 1000,
                ["notes"] = "Counts body vertices that sit more than toleranceMm outside the nearest clothing surface directly above them. " +
                    "Uses the current clothing states; physics (bust jiggle, skirts) is at rest. Body sliders are restored afterwards." };
        }

        private static JObject Step(ChaControl character, string slider, float? value, float tolerance)
        {
            var body = Bake(character.rendBody) ?? throw new InvalidOperationException("The body mesh cannot be read.");
            var clothes = new List<Surface>();
            foreach (var renderer in character.GetComponentsInChildren<Renderer>(true))
            {
                if (!Visible(renderer)) continue;
                int slot;
                GameObject part;
                if (MaterialCommands.PartOf(character, renderer.transform, out slot, out part) != "Clothing") continue;
                var surface = Bake(renderer);
                if (surface == null) continue;
                surface.Scope = "Clothing";
                surface.Slot = slot;
                clothes.Add(surface);
            }
            var grid = new Grid(0.015f);
            for (var s = 0; s < clothes.Count; s++)
                for (var i = 0; i < clothes[s].Points.Length; i++) grid.Add(clothes[s].Points[i], s, i);
            var perPiece = new int[clothes.Count];
            float deepest = 0;
            var pokes = new List<Vector3>();
            var stepSize = Math.Max(1, body.Points.Length / 12000);
            for (var i = 0; i < body.Points.Length; i += stepSize)
            {
                int piece, vertex;
                if (!grid.Nearest(body.Points[i], 0.03f, out piece, out vertex)) continue;
                var cloth = clothes[piece];
                var delta = body.Points[i] - cloth.Points[vertex];
                var outward = Vector3.Dot(delta, cloth.Normals[vertex]);
                // Only body points directly under the cloth: beyond a hem the offset is mostly sideways.
                if (outward <= tolerance || (delta - cloth.Normals[vertex] * outward).magnitude > 0.01f) continue;
                perPiece[piece]++;
                pokes.Add(body.Points[i]);
                deepest = Mathf.Max(deepest, outward);
            }
            var hotspots = Cluster(pokes, 0.02f).OrderByDescending(c => c.Count).Take(5)
                .Select(c => new JObject { ["points"] = c.Count, ["position"] = SceneCommands.Json(c.Aggregate(Vector3.zero, (a, b) => a + b) / c.Count) });
            return new JObject { ["slider"] = slider, ["value"] = value, ["pokeThrough"] = pokes.Count, ["maxDepthMm"] = Math.Round(deepest * 1000, 1),
                ["byPiece"] = new JArray(clothes.Select((c, i) => new JObject { ["piece"] = c.Name, ["slot"] = c.Slot, ["points"] = perPiece[i] })
                    .Where(o => (int)o["points"] > 0)),
                ["hotspots"] = new JArray(hotspots) };
        }

        // Uniform hash grid for nearest-point queries over a few hundred thousand points.
        private sealed class Grid
        {
            private readonly float _cell;
            private readonly Dictionary<long, List<int[]>> _cells = new Dictionary<long, List<int[]>>();
            private readonly List<Vector3> _points = new List<Vector3>();
            internal Grid(float cell) { _cell = cell; }

            private long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

            internal void Add(Vector3 p, int owner, int index)
            {
                var key = Key(Mathf.FloorToInt(p.x / _cell), Mathf.FloorToInt(p.y / _cell), Mathf.FloorToInt(p.z / _cell));
                List<int[]> list;
                if (!_cells.TryGetValue(key, out list)) _cells[key] = list = new List<int[]>();
                list.Add(new[] { owner, index, _points.Count });
                _points.Add(p);
            }

            internal IEnumerable<int> Within(Vector3 p, float radius)
            {
                var r = Mathf.CeilToInt(radius / _cell);
                int cx = Mathf.FloorToInt(p.x / _cell), cy = Mathf.FloorToInt(p.y / _cell), cz = Mathf.FloorToInt(p.z / _cell);
                for (var x = -r; x <= r; x++) for (var y = -r; y <= r; y++) for (var z = -r; z <= r; z++)
                {
                    List<int[]> list;
                    if (!_cells.TryGetValue(Key(cx + x, cy + y, cz + z), out list)) continue;
                    foreach (var e in list) if ((_points[e[2]] - p).sqrMagnitude <= radius * radius) yield return e[1];
                }
            }

            internal bool Nearest(Vector3 p, float radius, out int owner, out int index)
            {
                owner = index = -1;
                var best = radius * radius;
                var r = Mathf.CeilToInt(radius / _cell);
                int cx = Mathf.FloorToInt(p.x / _cell), cy = Mathf.FloorToInt(p.y / _cell), cz = Mathf.FloorToInt(p.z / _cell);
                for (var x = -r; x <= r; x++) for (var y = -r; y <= r; y++) for (var z = -r; z <= r; z++)
                {
                    List<int[]> list;
                    if (!_cells.TryGetValue(Key(cx + x, cy + y, cz + z), out list)) continue;
                    foreach (var e in list)
                    {
                        var d = (_points[e[2]] - p).sqrMagnitude;
                        if (d < best) { best = d; owner = e[0]; index = e[1]; }
                    }
                }
                return owner >= 0;
            }
        }
    }
}
