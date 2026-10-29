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
        private const float MaxPoke = 0.02f;       // clothing more than 2 cm under the skin is a different layer, not a poke-through
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
            internal Vector2[] Uv;
            internal int[] Triangles;
            internal int[][] SubTriangles;
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
                // BakeMesh copies UVs and submeshes too, so this works even when the source mesh is not CPU-readable.
                var surface = new Surface { Renderer = renderer, Name = renderer.name, Points = new Vector3[vertices.Length],
                    Normals = new Vector3[vertices.Length], Triangles = mesh.triangles, Uv = mesh.uv,
                    SubTriangles = Enumerable.Range(0, mesh.subMeshCount).Select(s => mesh.GetTopology(s) == MeshTopology.Triangles ? mesh.GetTriangles(s) : new int[0]).ToArray() };
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
            var frame = new HeadFrame(head.Points.Select(p => rotation * (p - center)).ToArray());
            var ears = Ears(character);
            // The back of the skull is low-poly, so sample triangle centres as well as vertices.
            var points = new List<Vector3>(head.Points);
            var normals = new List<Vector3>(head.Normals);
            for (var t = 0; t + 2 < head.Triangles.Length; t += 3)
            {
                int a = head.Triangles[t], b = head.Triangles[t + 1], c = head.Triangles[t + 2];
                points.Add((head.Points[a] + head.Points[b] + head.Points[c]) / 3f);
                normals.Add((head.Normals[a] + head.Normals[b] + head.Normals[c]).normalized);
            }
            var regions = new Dictionary<string, int[]> { ["top"] = new int[2], ["back"] = new int[2], ["left"] = new int[2], ["right"] = new int[2], ["forehead"] = new int[2] };
            var gaps = new List<int>();
            var covers = new int[hair.Count];

            var colliders = new List<MeshCollider>();
#if !KK
            var backfaces = Physics.queriesHitBackfaces;
#endif
            try
            {
                colliders = Colliders(hair);
#if !KK
                Physics.queriesHitBackfaces = true;
#endif
                var own = new Dictionary<Collider, int>();
                for (var c = 0; c < colliders.Count; c++) own[colliders[c]] = c;
                for (var i = 0; i < points.Count; i++)
                {
                    var region = frame.Region(rotation * (points[i] - center), rotation * normals[i]);
                    if (region == null || ears.Any(e => e.Contains(points[i]))) continue;
                    var origin = points[i] + normals[i] * 0.0005f;
                    int piece;
                    // Hair cards are single-sided, so also look back from outside; Unity 5.6 (KK) cannot hit back faces.
                    var covered = Hit(origin, normals[i], CoverRay, own, out piece) >= 0
                        || Hit(origin + normals[i] * CoverRay, -normals[i], CoverRay, own, out piece) >= 0;
                    regions[region][0]++;
                    if (covered) { regions[region][1]++; covers[piece]++; }
                    else if (region != "forehead") gaps.Add(i);
                }
            }
            finally
            {
#if !KK
                Physics.queriesHitBackfaces = backfaces;
#endif
                Destroy(colliders);
            }

            var clusters = Cluster(gaps.Select(i => points[i]).ToList(), 0.015f).OrderByDescending(c => c.Count).Take(10).Select(c =>
            {
                var centroid = c.Aggregate(Vector3.zero, (a, b) => a + b) / c.Count;
                var region = frame.Region(rotation * (centroid - center), null) ?? "scalp";
                // The nearest piece is not always the one that slipped, so offer the three closest with their fixes.
                var candidates = new JArray(hair.Select((surface, index) => new { surface, index, vertex = Closest(surface, centroid) })
                    .Where(x => x.vertex >= 0).OrderBy(x => (x.surface.Points[x.vertex] - centroid).sqrMagnitude).Take(3).Select(x =>
                    {
                        var candidate = new JObject { ["piece"] = x.surface.Name, ["scope"] = x.surface.Scope,
                            ["distanceMm"] = Math.Round((x.surface.Points[x.vertex] - centroid).magnitude * 1000, 1) };
                        if (x.surface.Scope == "Accessory")
                        {
                            candidate["slot"] = x.surface.Slot;
                            candidate["suggestedMove"] = SceneCommands.Json(MoveUnits(character, x.surface.Slot, centroid - x.surface.Points[x.vertex]));
                        }
                        return candidate;
                    }));
                return new JObject { ["region"] = region, ["points"] = c.Count, ["position"] = SceneCommands.Json(centroid), ["candidates"] = candidates };
            }).ToList();

            var headGrid = new Grid(0.02f);
            for (var i = 0; i < head.Points.Length; i++) headGrid.Add(head.Points[i], 0, i);
            var pieces = new JArray(hair.Select((surface, index) =>
            {
                var entry = Seam(character, surface, head, headGrid);
                // How many scalp samples this piece is the first to cover: compare runs to see which piece slipped.
                entry["covers"] = covers[index];
                return entry;
            }));

            int total = regions.Where(r => r.Key != "forehead").Sum(r => r.Value[0]);
            int covered2 = regions.Where(r => r.Key != "forehead").Sum(r => r.Value[1]);
            var uncovered = (bool?)args["showUncovered"] ?? false;
            return new JObject {
                ["coverage"] = total == 0 ? 0 : Math.Round(100.0 * covered2 / total, 1),
                ["regions"] = new JObject(regions.Select(r => new JProperty(r.Key, new JObject { ["samples"] = r.Value[0],
                    ["coverage"] = r.Value[0] == 0 ? 0 : Math.Round(100.0 * r.Value[1] / r.Value[0], 1) }))),
                ["gaps"] = new JArray(clusters), ["pieces"] = pieces, ["consistency"] = Consistency(character, hair),
                // showUncovered marks every uncovered scalp sample instead of one dot per gap cluster.
                ["markers"] = uncovered ? new JArray(gaps.Take(400).Select(i => SceneCommands.Json(points[i])))
                    : new JArray(clusters.Select(c => c["position"])),
                ["notes"] = "Coverage excludes the forehead (bangs are a style choice) and the ears. Hair roots buried in the scalp are " +
                    "normal and hidden; only floating pieces get a fix. Each gap lists the three closest pieces; a piece whose covers count " +
                    "dropped since the last run is the one that slipped. suggestedMove is in accessory_set position units (move0, add=true). " +
                    "Pass markers to capture_turntable to see the gaps." };
        }

        private static int Closest(Surface surface, Vector3 target)
        {
            var best = -1;
            var distance = float.MaxValue;
            for (var i = 0; i < surface.Points.Length; i++)
            {
                var d = (surface.Points[i] - target).sqrMagnitude;
                if (d < distance) { distance = d; best = i; }
            }
            return best;
        }

        // Temporary world-space mesh colliders, so physics raycasts can test the baked meshes.
        private static List<MeshCollider> Colliders(List<Surface> surfaces)
        {
            var result = new List<MeshCollider>();
            foreach (var surface in surfaces)
            {
                var holder = new GameObject("StudioMCP_probe") { hideFlags = HideFlags.HideAndDontSave };
                var mesh = new Mesh { vertices = surface.Points, triangles = surface.Triangles };
#if !KK
                if (surface.Points.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
#endif
                var collider = holder.AddComponent<MeshCollider>();
                collider.sharedMesh = mesh;
                result.Add(collider);
            }
            return result;
        }

        private static void Destroy(List<MeshCollider> colliders)
        {
            foreach (var collider in colliders) { UnityEngine.Object.Destroy(collider.sharedMesh); UnityEngine.Object.Destroy(collider.gameObject); }
        }

        // Distance to the nearest of our colliders along a ray, or -1.
        private static float Hit(Vector3 origin, Vector3 direction, float distance, Dictionary<Collider, int> own, out int owner)
        {
            owner = -1;
            var best = float.MaxValue;
            foreach (var hit in Physics.RaycastAll(origin, direction, distance, ~0, QueryTriggerInteraction.Collide))
            {
                int index;
                if (!own.TryGetValue(hit.collider, out index) || hit.distance >= best) continue;
                best = hit.distance;
                owner = index;
            }
            return owner < 0 ? -1 : best;
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

        // Scalp regions around the skull, in the character's frame (+z forward, +x to the character's right).
        private sealed class HeadFrame
        {
            private readonly float _minY, _height, _halfWidth, _centerZ;

            internal HeadFrame(Vector3[] local)
            {
                _minY = local.Min(p => p.y);
                _height = Mathf.Max(1e-4f, local.Max(p => p.y) - _minY);
                // The upper skull has no ears or face: its width and centre define the cranium.
                var upper = local.Where(p => (p.y - _minY) / _height > 0.8f).ToArray();
                _halfWidth = upper.Max(p => Mathf.Abs(p.x));
                _centerZ = upper.Average(p => p.z);
            }

            internal string Region(Vector3 p, Vector3? n)
            {
                var y = (p.y - _minY) / _height;
                if (Mathf.Abs(p.x) > _halfWidth * 1.08f) return null;       // anything past the skull's width
                if (n != null && n.Value.y < -0.3f) return null;            // under the chin, jaw and ear lobes
                if (y > 0.88f) return "top";
                var azimuth = Mathf.Abs(Mathf.Atan2(p.x, p.z - _centerZ) * Mathf.Rad2Deg);
                if (azimuth < 55) return y > 0.78f ? "forehead" : null;      // the face itself is not scalp
                if (azimuth > 125) return y > 0.3f ? "back" : null;         // down to the nape
                return y > 0.62f ? (p.x > 0 ? "right" : "left") : null;     // temples above the ears
            }
        }

        // A capsule around each ear (upper tip to lobe) plus its base; KK/KKS name these bones cf_J_Ear*.
        private sealed class Ear
        {
            internal Vector3 Up, Low, Base;
            internal bool Contains(Vector3 p) => (p - Base).magnitude < 0.022f || Segment(p, Up, Low) < 0.02f;
            private static float Segment(Vector3 p, Vector3 a, Vector3 b)
            {
                var ab = b - a;
                var t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude));
                return (p - (a + ab * t)).magnitude;
            }
        }

        private static List<Ear> Ears(ChaControl character)
        {
            var bones = character.objHeadBone.GetComponentsInChildren<Transform>(true);
            var result = new List<Ear>();
            foreach (var side in new[] { "L", "R" })
            {
                var up = bones.FirstOrDefault(t => t.name == "cf_J_EarUp_" + side);
                var low = bones.FirstOrDefault(t => t.name == "cf_J_EarLow_" + side);
                var root = bones.FirstOrDefault(t => t.name == "cf_J_EarBase_ry_" + side) ?? low;
                if (up != null && low != null) result.Add(new Ear { Up = up.position, Low = low.position, Base = root.position });
            }
            return result;
        }

        // Spliced hair reads as one hairstyle only when the pieces share color and gloss with the base hair.
        private static JObject Consistency(ChaControl character, List<Surface> hair)
        {
            var baseColor = character.fileHair.parts[0].baseColor;
            var baseVisible = hair.Any(h => h.Scope == "Hair");
            var slots = hair.Where(h => h.Scope == "Accessory").Select(h => h.Slot).Distinct().OrderBy(x => x).ToList();
            var infos = slots.ToDictionary(slot => slot, slot => AccessoryCommands.HairInfo(character, slot));
            var colors = slots.ToDictionary(slot => slot, slot => character.nowCoordinate.accessory.parts[slot].color[0]);
            // Pieces that follow the hair color match the base hair by definition; the rest are compared with their median.
            var own = slots.Where(slot => infos[slot] == null || !(bool)infos[slot]["colorMatch"]).Select(slot => colors[slot]).ToList();
            Func<Func<Color, float>, float> median = channel => own.Count == 0 ? 0 : own.Select(channel).OrderBy(v => v).ElementAt(own.Count / 2);
            var reference = baseVisible || own.Count == 0 ? baseColor : new Color(median(c => c.r), median(c => c.g), median(c => c.b), 1);
            var pieces = new JArray();
            foreach (var slot in slots)
            {
                var info = infos[slot];
                var color = colors[slot];
                var matched = info != null && (bool)info["colorMatch"];
                var difference = Mathf.Max(Mathf.Abs(color.r - reference.r), Mathf.Max(Mathf.Abs(color.g - reference.g), Mathf.Abs(color.b - reference.b)));
                var entry = new JObject { ["slot"] = slot, ["colorMatch"] = matched, ["mainColor"] = SceneCommands.Json(color),
                    ["colorDifference"] = Math.Round(matched ? 0 : difference, 3) };
                if (info != null) entry["hairGloss"] = info["hairGloss"];
                pieces.Add(entry);
            }
            var glossOn = pieces.Count(e => e["hairGloss"] != null && (bool)e["hairGloss"]);
            var glossKnown = pieces.Count(e => e["hairGloss"] != null);
            var majority = glossOn * 2 >= glossKnown;
            return new JObject { ["baseHairColor"] = SceneCommands.Json(baseColor), ["baseHairVisible"] = baseVisible,
                ["reference"] = baseVisible || own.Count == 0 ? "base hair" : "median of the hair pieces",
                ["referenceColor"] = SceneCommands.Json(reference), ["pieces"] = pieces,
                ["colorMismatch"] = new JArray(pieces.Where(e => (double)e["colorDifference"] > 0.08).Select(e => e["slot"])),
                ["glossMismatch"] = new JArray(pieces.Where(e => e["hairGloss"] != null && (bool)e["hairGloss"] != majority).Select(e => e["slot"])) };
        }

        // Closest approach to the head, how deep the piece dips inside it, and a move that would fix either.
        private JObject Seam(ChaControl character, Surface surface, Surface head, Grid headGrid)
        {
            var step = Math.Max(1, surface.Points.Length / 4000);
            float closest = float.MaxValue, deepest = 0;
            var closestDelta = Vector3.zero;
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
                    deepest = Mathf.Max(deepest, -signed);
                }
                else if (distance < closest) { closest = distance; closestDelta = -delta; }
            }
            var entry = new JObject { ["piece"] = surface.Name, ["scope"] = surface.Scope, ["sampled"] = sampled,
                ["closestGapMm"] = closest == float.MaxValue ? null : (JToken)Math.Round(closest * 1000, 1),
                ["insidePercent"] = sampled == 0 ? 0 : Math.Round(100.0 * inside / sampled, 1), ["maxDepthMm"] = Math.Round(deepest * 1000, 1) };
            if (surface.Scope == "Accessory") entry["slot"] = surface.Slot;
            // Roots buried in the scalp are hidden by the head and are how spliced hair normally sits; only a gap is visible.
            string verdict = sampled == 0 ? "far from head" : inside > 0 ? "rooted" : "ok";
            Vector3? fix = null;
            if (sampled > 0 && inside == 0 && closest > FloatGap) { verdict = "floating"; fix = closestDelta.normalized * (closest - 0.001f); }
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
            // Outfits built from accessories (common in ported clothes) are garments too.
            var garmentSlots = args["accessorySlots"] is JArray accessorySlots ? accessorySlots.Select(t => (int)t).ToList() : new List<int>();

            var original = (float[])character.fileBody.shapeValueBody.Clone();
            var results = new JArray();
            try
            {
                Vector3[] baseline;
                results.Add(Step(character, "current", null, tolerance, garmentSlots, null, out baseline));
                var previous = -1;
                foreach (var step in steps)
                {
                    if (previous >= 0) character.SetShapeBodyValue(previous, original[previous]);
                    character.SetShapeBodyValue(step.Key, step.Value);
                    previous = step.Key;
                    // Applies the bone changes now, so the baked meshes below see this step's body.
                    character.UpdateShapeBody();
                    Vector3[] moved;
                    results.Add(Step(character, CharacterCommands.BodyKey(step.Key), step.Value, tolerance, garmentSlots, baseline, out moved));
                }
            }
            finally
            {
                for (var i = 0; i < original.Length; i++) character.SetShapeBodyValue(i, original[i]);
                character.UpdateShapeBody();
            }
            return new JObject { ["steps"] = results, ["toleranceMm"] = tolerance * 1000,
                ["notes"] = "Counts body vertices where a garment's outer surface lies 'toleranceMm'-2 cm under the skin and no other garment covers the point. " +
                    "Uses the current clothing states; physics (bust jiggle, skirts) is at rest. Body sliders are restored afterwards." };
        }

        private static JObject Step(ChaControl character, string slider, float? value, float tolerance, List<int> garmentSlots,
            Vector3[] baseline, out Vector3[] bodyPoints)
        {
            var body = Bake(character.rendBody) ?? throw new InvalidOperationException("The body mesh cannot be read.");
            bodyPoints = body.Points;
            var clothes = new List<Surface>();
            foreach (var renderer in character.GetComponentsInChildren<Renderer>(true))
            {
                if (!Visible(renderer)) continue;
                int slot;
                GameObject part;
                var scope = MaterialCommands.PartOf(character, renderer.transform, out slot, out part);
                if (scope != "Clothing" && !(scope == "Accessory" && garmentSlots.Contains(slot))) continue;
                var surface = Bake(renderer);
                if (surface == null) continue;
                surface.Scope = scope;
                surface.Slot = slot;
                clothes.Add(surface);
            }
            var perPiece = new int[clothes.Count];
            float deepest = 0;
            var pokes = new List<Vector3>();
            var colliders = Colliders(clothes);
            try
            {
                var own = new Dictionary<Collider, int>();
                for (var c = 0; c < colliders.Count; c++) own[colliders[c]] = c;
                var stepSize = Math.Max(1, body.Points.Length / 12000);
                for (var i = 0; i < body.Points.Length; i += stepSize)
                {
                    var n = body.Normals[i];
                    if (n == Vector3.zero) continue;
                    var p = body.Points[i];
                    int piece, cover;
                    // Cloth whose outer face lies just under the skin: the body shows through it.
                    var depth = Hit(p + n * 0.0003f, -n, MaxPoke, own, out piece);
                    if (depth < 0 || depth <= tolerance) continue;
                    // Another garment over this point still hides it.
                    if (Hit(p + n * 0.03f, -n, 0.03f - 0.0006f, own, out cover) >= 0) continue;
                    perPiece[piece]++;
                    pokes.Add(p);
                    deepest = Mathf.Max(deepest, depth);
                }
            }
            finally { Destroy(colliders); }
            var hotspots = Cluster(pokes, 0.02f).OrderByDescending(c => c.Count).Take(5)
                .Select(c => new JObject { ["points"] = c.Count, ["position"] = SceneCommands.Json(c.Aggregate(Vector3.zero, (a, b) => a + b) / c.Count) });
            // How far this step moved the body from the current shape: zero means the slider had no effect here.
            var shift = 0f;
            if (baseline != null && baseline.Length == body.Points.Length)
                for (var i = 0; i < baseline.Length; i++) shift = Mathf.Max(shift, (body.Points[i] - baseline[i]).magnitude);
            return new JObject { ["slider"] = slider, ["value"] = value, ["bodyShiftMm"] = baseline == null ? null : (JToken)Math.Round(shift * 1000, 1),
                ["pokeThrough"] = pokes.Count, ["maxDepthMm"] = Math.Round(deepest * 1000, 1),
                ["byPiece"] = new JArray(clothes.Select((c, i) => new JObject { ["piece"] = c.Name, ["scope"] = c.Scope, ["slot"] = c.Slot, ["points"] = perPiece[i] })
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
