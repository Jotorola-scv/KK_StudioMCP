// Added 2026-10-30 in the maker-support fork of KK_StudioMCP (AGPL-3.0); see README.md.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudioMcp.Bridge
{
    // Same-frame rig samples for weight and clipping diagnosis. Each renderer's rest mesh (vertices, normals, tangents, UVs,
    // submesh indices, skin weights, bind poses, bone paths) is written once per job; each sample then stores, read at the
    // end of one frame, the bone world matrices, the baked vertices and optionally a camera image of that same frame.
    // A job runs a list of cases (pose, time, clothes states, shapes) with progress, cancel and resume, and restores the
    // character afterwards. Files are little-endian binary arrays described by JSON (dtype, shape, file, offset).
    internal sealed class SampleCommands
    {
        private const string Format = "studiomcp-rig/1";
        private static readonly string[] Modes = { "current", "frozen", "continuous" };
        private static readonly string[] Physics = { "reset", "settle", "off" };
        private readonly Plugin _plugin;
        private readonly Dictionary<string, Job> _jobs = new Dictionary<string, Job>();
        private Job _running;
        internal SampleCommands(Plugin plugin) { _plugin = plugin; }
        internal bool Busy => _running != null;

        private sealed class Job
        {
            internal string Id, Folder, Session, State = "waiting", Error, Current;
            internal JObject Options;
            internal JArray Cases;
            internal JObject Renderers = new JObject();
            internal readonly HashSet<string> Completed = new HashSet<string>();
            internal readonly List<string> Files = new List<string>();
            internal readonly JArray Warnings = new JArray();
            internal int Total, Done, Skipped;
            internal long Bytes, SampleBytes;
            internal bool Cancel;
            internal JObject Settle;
            internal DateTime Started = DateTime.Now;
            internal int ObjectId => (int?)Options["objectId"] ?? 0;
        }

        internal JToken Execute(string method, JObject args)
        {
            switch (method)
            {
                case "sample.start": return Start(args);
                case "sample.status": return Status(Find(args));
                case "sample.cancel":
                    var job = Find(args);
                    if (_running == job) job.Cancel = true;
                    return Status(job);
                case "sample.cleanup": return Cleanup(args);
                default: throw new ArgumentException("Unknown sample method: " + method);
            }
        }

        private Job Find(JObject args)
        {
            var id = (string)args["jobId"];
            if (string.IsNullOrEmpty(id)) return _running ?? throw new ArgumentException("jobId is required (no job is running).");
            Job job;
            if (_jobs.TryGetValue(id, out job)) return job;
            throw new ArgumentException("Unknown sample job " + id + "; after a game restart, pass it as resume to sample_job_start.");
        }

        private string Root() { return Path.Combine(_plugin.OutputRoot, "samples"); }

        private JObject Start(JObject args)
        {
            if (_running != null) throw new InvalidOperationException("Sample job " + _running.Id + " is running; cancel it or wait.");
            var character = _plugin.Characters.Character((int?)args["objectId"] ?? 0);
            var resume = (string)args["resume"];
            Job job;
            if (!string.IsNullOrEmpty(resume))
            {
                if (resume.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("resume must be a jobId.");
                job = Load(resume);
                // A resumed job keeps its stored cases and options: sample ids and rest data depend on them.
            }
            else
            {
                var name = new string(((string)args["name"] ?? "rig").Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
                job = new Job { Id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) };
                job.Folder = Path.Combine(Root(), job.Id);
                job.Cases = Cases(args["cases"]);
                job.Options = new JObject();
                foreach (var property in args.Properties())
                    if (property.Name != "cases" && property.Name != "sceneSession") job.Options[property.Name] = property.Value;
            }
            var options = job.Options;
            var mode = (string)options["mode"] ?? "frozen";
            if (!Modes.Contains(mode)) throw new ArgumentException("mode must be current, frozen or continuous.");
            options["mode"] = mode;
            var physics = (string)options["physics"] ?? "reset";
            if (!Physics.Contains(physics)) throw new ArgumentException("physics must be reset, settle or off.");
            options["physics"] = physics;
            Range(options, "warmupFrames", mode == "current" ? 0 : 30, 0, 600);
            Range(options, "maxWarmupFrames", 600, 1, 7200);
            Range(options, "fixedFps", mode == "current" ? 0 : 60, 0, 240);
            var settleMm = options["settleMm"] == null || options["settleMm"].Type == JTokenType.Null ? 0.002f : SceneCommands.Number(options["settleMm"]);
            if (settleMm < 0 || settleMm > 10) throw new ArgumentOutOfRangeException("settleMm", "settleMm must be 0-10 (0 waits warmupFrames only).");
            options["settleMm"] = settleMm;
            Range(options, "samples", mode == "continuous" ? 10 : 1, 1, 600);
            Range(options, "intervalFrames", 5, 1, 600);
            Range(options, "imageWidth", 640, 64, 3840);
            Range(options, "imageHeight", 480, 64, 2160);
            var images = (string)options["images"] ?? "none";
            if (images != "none" && images != "all") throw new ArgumentException("images must be none or all.");
            options["images"] = images;
            if (mode != "continuous") options["samples"] = 1;
            if (mode == "current" && job.Cases.Count > 1) throw new ArgumentException("mode current samples the present frame once; use frozen or continuous for several cases.");
            foreach (JObject item in job.Cases) ValidateCase(character, item);
            Renderers(character, options, false);   // validates the filter now rather than inside the coroutine
            job.Session = _plugin.Session;
            job.Total = job.Cases.Count * (int)options["samples"];
            Directory.CreateDirectory(job.Folder);
            _jobs[job.Id] = job;
            _running = job;
            job.State = "running";
            WriteManifest(job, character);
            _plugin.StartCoroutine(Run(job, character));
            return Status(job);
        }

        private static void Range(JObject options, string key, int fallback, int min, int max)
        {
            var value = options[key] == null || options[key].Type == JTokenType.Null ? fallback : (int)options[key];
            if (value < min || value > max) throw new ArgumentOutOfRangeException(key, key + " must be " + min + "-" + max + ".");
            options[key] = value;
        }

        private static JArray Cases(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return new JArray(new JObject());
            var cases = token as JArray ?? throw new ArgumentException("cases must be an array of {pose, time, clothes, shapes}.");
            if (cases.Count == 0 || cases.Count > 5000) throw new ArgumentException("cases must have 1-5000 entries.");
            foreach (var item in cases) if (!(item is JObject)) throw new ArgumentException("Each case must be an object.");
            return cases;
        }

        private void ValidateCase(ChaControl character, JObject item)
        {
            if (item["pose"] != null && item["pose"].Type != JTokenType.Null)
            {
                if (Plugin.InStudio) throw new NotSupportedException("Case poses use the maker pose list; in Studio pose the character first and sample without pose.");
                PoseIndex(item["pose"]);
            }
            if (item["time"] != null)
            {
                var time = SceneCommands.Number(item["time"]);
                if (time < 0 || time > 1) throw new ArgumentOutOfRangeException("time", "Use a normalized 0..1 animation time.");
            }
            if (item["clothes"] is JObject clothes)
                foreach (var property in clothes.Properties())
                {
                    MakerCommands.ClothesKind(int.TryParse(property.Name, out var slot) ? (JToken)slot : property.Name);
                    if (property.Value.Type != JTokenType.Integer || (int)property.Value < 0 || (int)property.Value > 3)
                        throw new ArgumentException("clothes states are 0 (on), 1 (half), 2 (half 2) or 3 (off).");
                }
            else if (item["clothes"] != null) throw new ArgumentException("clothes must be an object such as {\"top\": 1}.");
            foreach (var part in ShapeParts(item))
            {
                // Validate through the real setter's parser without applying anything.
                var values = part.Value as JObject ?? throw new ArgumentException("shapes must be an object of slider values.");
                foreach (var property in values.Properties()) SceneCommands.Number(property.Value);
            }
        }

        private static IEnumerable<KeyValuePair<string, JToken>> ShapeParts(JObject item)
        {
            var shapes = item["shapes"] as JObject;
            if (shapes == null) yield break;
            if (shapes["body"] is JObject || shapes["face"] is JObject)
            {
                if (shapes["body"] != null) yield return new KeyValuePair<string, JToken>("body", shapes["body"]);
                if (shapes["face"] != null) yield return new KeyValuePair<string, JToken>("face", shapes["face"]);
            }
            else yield return new KeyValuePair<string, JToken>("body", shapes);
        }

        // Pose index in the maker list: an index, -1 for the default pose, "asset/state" or a state name.
        private static int PoseIndex(JToken pose)
        {
            var maker = KKAPI.Maker.MakerAPI.GetMakerBase() ?? throw new InvalidOperationException("Character maker is not open.");
            var list = ((IEnumerable)MotionCommands.Member(maker, "lstPose")).Cast<object>().ToList();
            if (pose.Type == JTokenType.Integer)
            {
                var index = (int)pose;
                if (index < -1 || index >= list.Count) throw new ArgumentException("pose index must be -1..." + (list.Count - 1) + " (see pose_list).");
                return index;
            }
            var text = (string)pose;
            for (var i = 0; i < list.Count; i++)
            {
                var state = MotionCommands.PoseState(list[i]);
                var asset = MotionCommands.PoseAsset(list[i]);
                if (text == state || text == asset + "/" + state || text == Path.GetFileNameWithoutExtension(asset ?? "") + "/" + state) return i;
            }
            throw new ArgumentException("Unknown pose " + text + "; use an index, \"asset/state\" or a state name from pose_list.");
        }

        // The renderers to sample: an optional filter (rendererIds, scopes, accessorySlots, hairSlots, clothes), plus the
        // body as a collision reference unless includeBody is false. Hidden renderers count only with includeInactive.
        internal static List<Renderer> Renderers(ChaControl character, JObject filter, bool forVisibility)
        {
            var scopes = Strings(filter["scopes"]);
            foreach (var scope in scopes)
                if (scope != "Character" && scope != "Clothing" && scope != "Hair" && scope != "Accessory")
                    throw new ArgumentException("scopes may contain Character, Clothing, Hair and Accessory.");
            var ids = Ints(filter["rendererIds"]);
            var accessories = Ints(filter["accessorySlots"]);
            var hair = Ints(filter["hairSlots"]);
            var clothes = new HashSet<int>();
            if (filter["clothes"] is JArray kinds) foreach (var kind in kinds) clothes.Add(MakerCommands.ClothesKind(kind));
            var all = (bool?)filter["allAccessories"] ?? false;
            var any = scopes.Count > 0 || ids.Count > 0 || accessories.Count > 0 || hair.Count > 0 || clothes.Count > 0 || all;
            if (forVisibility && !any) throw new ArgumentException("Give rendererIds, scopes, accessorySlots, hairSlots, clothes or allAccessories.");
            var includeInactive = forVisibility || ((bool?)filter["includeInactive"] ?? false);
            var includeHelpers = (bool?)filter["includeHelpers"] ?? forVisibility;
            var includeBody = !forVisibility && ((bool?)filter["includeBody"] ?? true);
            var result = new List<Renderer>();
            foreach (var renderer in character.GetComponentsInChildren<Renderer>(true))
            {
                if (!includeInactive && (!renderer.enabled || !renderer.gameObject.activeInHierarchy)) continue;
                if (!includeHelpers && renderer.sharedMaterials.Length > 0
                    && renderer.sharedMaterials.All(m => m == null || CharacterCommands.HelperShaders.Contains(m.shader.name))) continue;
                if (!(renderer is SkinnedMeshRenderer) && renderer.GetComponent<MeshFilter>() == null) continue;
                int slot;
                GameObject part;
                var scope = MaterialCommands.PartOf(character, renderer.transform, out slot, out part);
                var take = !any || ids.Contains(renderer.GetInstanceID()) || scopes.Contains(scope)
                    || (scope == "Accessory" && (all || accessories.Contains(slot)))
                    || (scope == "Hair" && hair.Contains(slot)) || (scope == "Clothing" && clothes.Contains(slot))
                    || (includeBody && renderer == character.rendBody);
                if (take) result.Add(renderer);
            }
            return result;
        }

        private static List<string> Strings(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return new List<string>();
            var array = token as JArray ?? throw new ArgumentException("Expected an array of names.");
            return array.Select(t => (string)t).ToList();
        }

        private static HashSet<int> Ints(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return new HashSet<int>();
            var array = token as JArray ?? throw new ArgumentException("Expected an array of integers.");
            return new HashSet<int>(array.Select(t => (int)t));
        }

        // ---- the job coroutine ----------------------------------------------------------------------------------------

        private sealed class Saved
        {
            internal float AnimatorSpeed = 1;
            internal int Pose = int.MinValue;
            internal string PoseBundle, PoseAsset, PoseState;
            internal float PoseTime;
            internal byte[] Clothes;
            internal readonly Dictionary<string, JObject> Shapes = new Dictionary<string, JObject>();
            internal readonly List<Behaviour> DisabledBones = new List<Behaviour>();
            internal int CaptureFramerate;
            internal bool RestoreBlink, Blink;
        }

        private IEnumerator Run(Job job, ChaControl character)
        {
            var options = job.Options;
            var mode = (string)options["mode"];
            var physics = (string)options["physics"];
            var samples = (int)options["samples"];
            var saved = new Saved();
            var finished = false;
            try
            {
                if (!Step(job, () => Save(job, character, saved))) yield break;
                for (var caseIndex = 0; caseIndex < job.Cases.Count; caseIndex++)
                {
                    var item = (JObject)job.Cases[caseIndex];
                    var ids = Enumerable.Range(0, samples).Select(s => SampleId(caseIndex, s, mode)).ToList();
                    if (ids.All(job.Completed.Contains)) { job.Skipped += ids.Count; continue; }
                    if (job.Cancel) break;
                    job.Current = "case " + caseIndex + (item["pose"] != null ? " pose " + item["pose"] : "");
                    if (!Step(job, () => ApplyCase(job, character, item, mode, physics, saved))) break;
                    // Reset physics only once the Animator has applied the new pose: a reset from the previous pose can
                    // start a strand on the other side of a collider, where it then rests.
                    if (mode != "current" && physics == "reset")
                    {
                        for (var frame = 0; frame < 3 && !job.Cancel && Alive(job, character); frame++) yield return null;
                        if (!Step(job, () => character.resetDynamicBoneAll = true)) break;
                    }
                    for (var frame = 0; frame < (int)options["warmupFrames"]; frame++)
                    {
                        if (job.Cancel || !Alive(job, character)) break;
                        yield return null;
                    }
                    // Physics and the game's corrective bones keep easing long after a pose is set (0.5 s is centimetres off), so a
                    // frozen sample waits for them. On a fixed time step they really come to rest: 0.002 mm/frame takes about 200 frames.
                    job.Settle = null;
                    if (mode == "frozen" && (float)options["settleMm"] > 0 && !job.Cancel && Alive(job, character))
                    {
                        var watch = new BoneWatch();
                        if (!Step(job, () => watch.Bones = Renderers(character, options, false).OfType<SkinnedMeshRenderer>().SelectMany(r => r.bones).Where(b => b != null).Distinct().ToArray())) break;
                        var limit = Math.Max(0, (int)options["maxWarmupFrames"] - (int)options["warmupFrames"]);
                        var still = 0;
                        var waited = 0;
                        while (waited < limit && still < 10 && !job.Cancel && Alive(job, character))
                        {
                            yield return null;
                            waited++;
                            still = watch.Move() < (float)options["settleMm"] ? still + 1 : 0;
                        }
                        job.Settle = new JObject { ["settled"] = still >= 10, ["frames"] = waited, ["lastMoveMm"] = Math.Round(watch.Last, 5), ["thresholdMm"] = options["settleMm"] };
                        if (still < 10 && !job.Cancel && job.Warnings.Count < 20) job.Warnings.Add("case " + caseIndex + " still moved " + Math.Round(watch.Last, 4) + " mm per frame after " + (waited + (int)options["warmupFrames"]) + " frames.");
                    }
                    for (var s = 0; s < samples && !job.Cancel; s++)
                    {
                        if (s > 0) for (var frame = 0; frame < (int)options["intervalFrames"]; frame++) yield return null;
                        yield return new WaitForEndOfFrame();
                        if (!Alive(job, character)) break;
                        var id = ids[s];
                        if (job.Completed.Contains(id)) continue;
                        if (!Step(job, () => Sample(job, character, id, caseIndex, s, item))) break;
                    }
                    if (job.Error != null || !Alive(job, character)) break;
                    if (mode == "frozen" && character.animBody != null) character.animBody.speed = saved.AnimatorSpeed;
                }
                finished = true;
            }
            finally
            {
                // An exception outside Step ends the coroutine here; it must not look like a completed job.
                if (!finished && job.Error == null) job.Error = "The job stopped unexpectedly; see the game log.";
                Restore(job, character, saved);
                if (job.Error == null && !Alive(job, character)) job.Error = "The character was reloaded during the job; resume it with the same jobId.";
                job.State = job.Error != null ? "failed" : job.Cancel ? "cancelled" : "complete";
                job.Current = null;
                try { WriteManifest(job, character); } catch (Exception error) { job.Warnings.Add("manifest: " + error.Message); }
                _running = null;
            }
        }

        // The largest distance any watched bone moved since the previous call, in millimetres.
        private sealed class BoneWatch
        {
            internal Transform[] Bones;
            internal float Last = float.PositiveInfinity;
            private Vector3[] _previous;

            internal float Move()
            {
                var current = new Vector3[Bones.Length];
                var largest = 0f;
                for (var i = 0; i < Bones.Length; i++)
                {
                    if (Bones[i] == null) continue;
                    current[i] = Bones[i].position;
                    if (_previous != null) largest = Mathf.Max(largest, (current[i] - _previous[i]).magnitude);
                }
                var first = _previous == null;
                _previous = current;
                Last = first ? float.PositiveInfinity : largest * 1000f;
                return Last;
            }
        }

        private static string SampleId(int caseIndex, int sample, string mode)
        {
            return "c" + caseIndex.ToString("D4", CultureInfo.InvariantCulture) + (mode == "continuous" ? "-s" + sample.ToString("D3", CultureInfo.InvariantCulture) : "");
        }

        private bool Alive(Job job, ChaControl character) { return character != null && _plugin.Session == job.Session; }

        private static bool Step(Job job, Action action)
        {
            try { action(); return true; }
            catch (Exception error)
            {
                var inner = error is TargetInvocationException && error.InnerException != null ? error.InnerException : error;
                job.Error = (job.Current != null ? job.Current + ": " : "") + inner.Message;
                return false;
            }
        }

        private void Save(Job job, ChaControl character, Saved saved)
        {
            saved.AnimatorSpeed = character.animBody != null ? character.animBody.speed : 1;
            // Physics integrates real frame times, so two runs of the same pose take different paths and never quite agree.
            // A fixed time step makes every frame identical. The maker has nothing else that minds a game clock on a fixed step.
            saved.CaptureFramerate = Time.captureFramerate;
            Time.captureFramerate = (int)job.Options["fixedFps"];
            if ((string)job.Options["mode"] == "frozen")
            {
                saved.Blink = character.fileStatus.eyesBlink;
                saved.RestoreBlink = true;
                character.ChangeEyesBlinkFlag(false);
            }
            saved.Clothes = (byte[])character.fileStatus.clothesState.Clone();
            if (!Plugin.InStudio && job.Cases.Any(c => c["pose"] != null && c["pose"].Type != JTokenType.Null))
            {
                var draw = UnityEngine.Object.FindObjectOfType<ChaCustom.CvsDrawCtrl>();
                var dropdown = draw == null ? null : draw.GetType().GetField("ddPose").GetValue(draw);
                if (dropdown != null) saved.Pose = (int)dropdown.GetType().GetProperty("value").GetValue(dropdown, null);
                // The animation that is really playing; the dropdown can differ from it (the default pose).
                var maker = KKAPI.Maker.MakerAPI.GetMakerBase();
                saved.PoseBundle = maker.animeAssetBundleName;
                saved.PoseAsset = maker.animeAssetName;
                saved.PoseState = maker.animeStateName;
                if (character.animBody != null) saved.PoseTime = Mathf.Repeat(character.animBody.GetCurrentAnimatorStateInfo(0).normalizedTime, 1f);
            }
            foreach (var part in new[] { "body", "face" })
                if (job.Cases.Cast<JObject>().Any(c => ShapeParts(c).Any(p => p.Key == part)))
                    saved.Shapes[part] = (JObject)_plugin.Characters.Execute("chara.shape_get", new JObject { ["objectId"] = job.ObjectId, ["part"] = part });
        }

        private void ApplyCase(Job job, ChaControl character, JObject item, string mode, string physics, Saved saved)
        {
            if (item["pose"] != null && item["pose"].Type != JTokenType.Null)
                _plugin.Motion.Execute("maker.pose_set", new JObject { ["index"] = PoseIndex(item["pose"]),
                    ["time"] = item["time"] == null ? 0f : SceneCommands.Number(item["time"]) });
            if (item["clothes"] is JObject clothes)
                foreach (var property in clothes.Properties())
                    character.SetClothesState(MakerCommands.ClothesKind(int.TryParse(property.Name, out var slot) ? (JToken)slot : property.Name),
                        (byte)(int)property.Value, false);
            foreach (var part in ShapeParts(item))
                _plugin.Characters.Execute("chara.shape_set", new JObject { ["objectId"] = job.ObjectId, ["part"] = part.Key, ["values"] = part.Value });
            if (mode == "current") return;
            if (mode == "frozen" && character.animBody != null) character.animBody.speed = 0;
            if (physics == "off")
                foreach (var bone in DynamicBones(character))
                {
                    if (bone.enabled) { bone.enabled = false; saved.DisabledBones.Add(bone); }
                    // Back to the physics chain's initial local pose, so "off" is pure skinning of the animated pose.
                    bone.GetType().GetMethod("InitTransforms", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(bone, null);
                }
        }

        private static List<Behaviour> DynamicBones(ChaControl character)
        {
            var result = new List<Behaviour>();
            foreach (var name in new[] { "DynamicBone", "DynamicBone_Ver01", "DynamicBone_Ver02" })
            {
                var type = MaterialCommands.FindType(name);
                if (type != null) result.AddRange(character.GetComponentsInChildren(type, true).Cast<Behaviour>());
            }
            return result;
        }

        private void Restore(Job job, ChaControl character, Saved saved)
        {
            // Global, so it comes back even if the character is gone.
            Time.captureFramerate = saved.CaptureFramerate;
            if (character == null) return;
            try
            {
                if (saved.RestoreBlink) character.ChangeEyesBlinkFlag(saved.Blink);
                if (character.animBody != null) character.animBody.speed = saved.AnimatorSpeed;
                foreach (var bone in saved.DisabledBones) if (bone != null) bone.enabled = true;
                if (saved.DisabledBones.Count > 0) character.resetDynamicBoneAll = true;
                if (saved.PoseState != null)
                {
                    var maker = KKAPI.Maker.MakerAPI.GetMakerBase();
                    if (maker.animeAssetBundleName != saved.PoseBundle || maker.animeAssetName != saved.PoseAsset)
                    {
                        maker.animeAssetBundleName = saved.PoseBundle;
                        maker.animeAssetName = saved.PoseAsset;
                        maker.LoadAnimation(saved.PoseBundle, saved.PoseAsset);
                    }
                    maker.PlayAnimation(saved.PoseState, saved.PoseTime);
                    var draw = UnityEngine.Object.FindObjectOfType<ChaCustom.CvsDrawCtrl>();
                    if (draw != null && saved.Pose != int.MinValue) MotionCommands.SyncPoseDropdown(draw, saved.Pose);
                }
                for (var i = 0; i < saved.Clothes.Length && i < character.fileStatus.clothesState.Length; i++)
                    if (character.fileStatus.clothesState[i] != saved.Clothes[i]) character.SetClothesState(i, saved.Clothes[i], false);
                foreach (var part in saved.Shapes)
                {
                    var values = new JObject();
                    foreach (JObject shape in (JArray)part.Value["shapes"]) values[((int)shape["index"]).ToString(CultureInfo.InvariantCulture)] = shape["value"];
                    _plugin.Characters.Execute("chara.shape_set", new JObject { ["objectId"] = job.ObjectId, ["part"] = part.Key, ["values"] = values });
                }
            }
            catch (Exception error) { job.Warnings.Add("restore: " + error.Message); }
        }

        // ---- one sample, read at the end of a frame -----------------------------------------------------------------

        private void Sample(Job job, ChaControl character, string id, int caseIndex, int sampleIndex, JObject item)
        {
            var options = job.Options;
            var relative = "samples/" + id + ".bin";
            var meta = new JObject { ["sampleId"] = id, ["case"] = caseIndex, ["sample"] = sampleIndex, ["frameCount"] = Time.frameCount,
                ["unscaledTime"] = Time.unscaledTime, ["mode"] = options["mode"], ["physics"] = options["physics"],
                ["warmupFrames"] = options["warmupFrames"], ["fixedFps"] = options["fixedFps"], ["settle"] = job.Settle, ["caseInput"] = item.DeepClone() };
            if (character.animBody != null)
            {
                var info = character.animBody.GetCurrentAnimatorStateInfo(0);
                meta["animator"] = new JObject { ["stateHash"] = info.fullPathHash, ["normalizedTime"] = info.normalizedTime,
                    ["loopTime"] = Mathf.Repeat(info.normalizedTime, 1f), ["speed"] = character.animBody.speed,
                    ["dynamicBoneReset"] = (string)options["physics"] == "reset", ["dynamicBonesEnabled"] = (string)options["physics"] != "off" };
                if (!Plugin.InStudio && KKAPI.Maker.MakerAPI.InsideAndLoaded) meta["animator"]["state"] = KKAPI.Maker.MakerAPI.GetMakerBase().animeStateName;
            }
            meta["clothesState"] = new JArray(character.fileStatus.clothesState.Select(b => (int)b));
            meta["shapes"] = new JObject { ["body"] = new JArray(character.fileBody.shapeValueBody), ["face"] = new JArray(character.fileFace.shapeValueFace) };
            try { meta["abmx"] = _plugin.Bones.Execute("chara.bones_get", new JObject { ["objectId"] = job.ObjectId })["modifiers"]; }
            catch (Exception) { meta["abmx"] = null; }
            var renderers = new JObject();
            var path = Path.Combine(job.Folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var bin = new Bin(path, relative))
            {
                foreach (var renderer in Renderers(character, options, false))
                {
                    var key = Rest(job, character, renderer);
                    var entry = new JObject { ["rendererId"] = renderer.GetInstanceID(),
                        ["visible"] = renderer.enabled && renderer.gameObject.activeInHierarchy,
                        ["localToWorld"] = Matrix(renderer.transform.localToWorldMatrix) };
                    var skinned = renderer as SkinnedMeshRenderer;
                    if (skinned != null) SampleSkinned(job, key, skinned, bin, entry, (bool?)options["normals"] ?? false, (bool?)options["verify"] ?? true);
                    renderers[key] = entry;
                }
                meta["bytes"] = bin.Length;
            }
            meta["file"] = relative;
            meta["renderers"] = renderers;
            Track(job, relative);
            if ((string)options["images"] == "all")
            {
                var image = "images/" + id + ".png";
                var imagePath = Path.Combine(job.Folder, image.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(imagePath));
                File.WriteAllBytes(imagePath, RenderImage((int)options["imageWidth"], (int)options["imageHeight"]));
                meta["image"] = image;
                Track(job, image);
            }
            var metaFile = "samples/" + id + ".json";
            File.WriteAllText(Path.Combine(job.Folder, metaFile.Replace('/', Path.DirectorySeparatorChar)), meta.ToString(Formatting.Indented));
            Track(job, metaFile);
            job.Completed.Add(id);
            job.Done++;
            job.SampleBytes = (job.SampleBytes * (job.Done - 1) + (long)meta["bytes"]) / job.Done;
            if (job.Done % 20 == 0) WriteManifest(job, character);
        }

        private static void SampleSkinned(Job job, string key, SkinnedMeshRenderer skinned, Bin bin, JObject entry, bool normals, bool verify)
        {
            var bones = skinned.bones;
            var matrices = new float[bones.Length * 16];
            var missing = 0;
            for (var i = 0; i < bones.Length; i++)
            {
                var m = bones[i] != null ? bones[i].localToWorldMatrix : Matrix4x4.identity;
                if (bones[i] == null) missing++;
                for (var k = 0; k < 16; k++) matrices[i * 16 + k] = m[k / 4, k % 4];
            }
            entry["bones"] = bin.Floats(matrices, bones.Length, 4, 4);
            if (missing > 0) entry["missingBones"] = missing;
            var weights = new JArray();
            for (var i = 0; skinned.sharedMesh != null && i < skinned.sharedMesh.blendShapeCount; i++) weights.Add(skinned.GetBlendShapeWeight(i));
            entry["blendShapeWeights"] = weights;
            var baked = new Mesh();
            try
            {
                skinned.BakeMesh(baked);
                var vertices = baked.vertices;
                entry["baked"] = bin.Vectors(vertices);
                if (normals) entry["bakedNormals"] = bin.Vectors(baked.normals);
                // BakeMesh output is in the renderer's space with its scale applied but not its position/rotation.
                var toWorld = Matrix4x4.TRS(skinned.transform.position, skinned.transform.rotation, Vector3.one);
                entry["bakedToWorld"] = Matrix(toWorld);
                if (verify) entry["verify"] = Verify(job, key, skinned, bones, vertices, toWorld);
            }
            finally { UnityEngine.Object.Destroy(baked); }
        }

        // Replays linear blend skinning from the rest data and this frame's bone matrices and compares it with BakeMesh.
        private static JObject Verify(Job job, string key, SkinnedMeshRenderer skinned, Transform[] bones, Vector3[] baked, Matrix4x4 toWorld)
        {
            var mesh = skinned.sharedMesh;
            if (mesh == null || !mesh.isReadable) return new JObject { ["skipped"] = "source mesh is not CPU-readable" };
            var rest = mesh.vertices;
            if (rest.Length != baked.Length) return new JObject { ["skipped"] = "vertex count differs from the baked mesh" };
            var weights = mesh.boneWeights;
            var bind = mesh.bindposes;
            if (weights.Length != rest.Length || bind.Length != bones.Length) return new JObject { ["skipped"] = "no skin weights or bind poses" };
            var deform = (Vector3[])rest.Clone();
            for (var shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                var weight = skinned.GetBlendShapeWeight(shape);
                if (Math.Abs(weight) < 1e-5f) continue;
                var delta = BlendDelta(mesh, shape, weight);
                for (var v = 0; v < deform.Length; v++) deform[v] += delta[v];
            }
            var skin = new Matrix4x4[bones.Length];
            for (var i = 0; i < bones.Length; i++) skin[i] = (bones[i] != null ? bones[i].localToWorldMatrix : Matrix4x4.identity) * bind[i];
            double sum = 0, max = 0;
            var worst = -1;
            for (var v = 0; v < deform.Length; v++)
            {
                var w = weights[v];
                var p = skin[w.boneIndex0].MultiplyPoint3x4(deform[v]) * w.weight0 + skin[w.boneIndex1].MultiplyPoint3x4(deform[v]) * w.weight1
                      + skin[w.boneIndex2].MultiplyPoint3x4(deform[v]) * w.weight2 + skin[w.boneIndex3].MultiplyPoint3x4(deform[v]) * w.weight3;
                var error = (p - toWorld.MultiplyPoint3x4(baked[v])).magnitude * 1000.0;
                sum += error;
                if (error > max) { max = error; worst = v; }
            }
            return new JObject { ["maxErrorMm"] = Math.Round(max, 4), ["meanErrorMm"] = Math.Round(sum / Math.Max(1, deform.Length), 5),
                ["worstVertex"] = worst, ["model"] = "sum(w_i * boneWorld_i * bindpose_i) * (rest + blendShapeDelta) vs bakedToWorld * baked" };
        }

        // Unity's blend shape interpolation: linear up to the first frame weight, then between neighbouring frames.
        internal static Vector3[] BlendDelta(Mesh mesh, int shape, float weight)
        {
            var count = mesh.vertexCount;
            var frames = mesh.GetBlendShapeFrameCount(shape);
            var result = new Vector3[count];
            var a = new Vector3[count];
            var b = new Vector3[count];
            var normals = new Vector3[count];
            var tangents = new Vector3[count];
            var first = mesh.GetBlendShapeFrameWeight(shape, 0);
            if (frames == 1 || weight <= first)
            {
                mesh.GetBlendShapeFrameVertices(shape, 0, a, normals, tangents);
                var f = weight / first;
                for (var v = 0; v < count; v++) result[v] = a[v] * f;
                return result;
            }
            var upper = 1;
            while (upper < frames - 1 && mesh.GetBlendShapeFrameWeight(shape, upper) < weight) upper++;
            var w0 = mesh.GetBlendShapeFrameWeight(shape, upper - 1);
            var w1 = mesh.GetBlendShapeFrameWeight(shape, upper);
            mesh.GetBlendShapeFrameVertices(shape, upper - 1, a, normals, tangents);
            mesh.GetBlendShapeFrameVertices(shape, upper, b, normals, tangents);
            var t = (weight - w0) / (w1 - w0);
            for (var v = 0; v < count; v++) result[v] = Vector3.LerpUnclamped(a[v], b[v], t);
            return result;
        }

        // ---- rest data, written once per renderer and job ----------------------------------------------------------

        private string Rest(Job job, ChaControl character, Renderer renderer)
        {
            int slot;
            GameObject part;
            var scope = MaterialCommands.PartOf(character, renderer.transform, out slot, out part);
            var skinned = renderer as SkinnedMeshRenderer;
            var mesh = skinned != null ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
            var readable = mesh != null && mesh.isReadable;
            Mesh baked = null;
            // A non-readable skinned mesh still bakes; its topology and UVs then come from the baked copy.
            if (!readable && skinned != null) { baked = new Mesh(); skinned.BakeMesh(baked); }
            try
            {
                var source = readable ? mesh : baked;
                var topology = source == null ? "none" : TopologyHash(source);
                var key = new string((scope + "_" + slot + "_" + renderer.name).Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray())
                    + "_" + topology.Substring(0, Math.Min(8, topology.Length)) + "_" + Hex(System.Text.Encoding.UTF8.GetBytes(PathOf(renderer.transform, character.transform))).Substring(0, 6);
                if (job.Renderers[key] != null) return key;
                var relative = "rest/" + key + ".bin";
                var path = Path.Combine(job.Folder, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var info = new JObject { ["rendererId"] = renderer.GetInstanceID(), ["renderer"] = renderer.name, ["scope"] = scope,
                    ["partSlot"] = slot, ["path"] = PathOf(renderer.transform, character.transform), ["skinned"] = skinned != null,
                    ["sourceMesh"] = mesh != null ? mesh.name : null, ["readable"] = readable, ["topologyHash"] = topology,
                    ["topologySource"] = readable ? "sharedMesh" : baked != null ? "bakedMesh" : "none",
                    ["localBounds"] = mesh == null ? null : new JObject { ["center"] = SceneCommands.Json(mesh.bounds.center), ["size"] = SceneCommands.Json(mesh.bounds.size) },
                    ["materials"] = new JArray(renderer.sharedMaterials.Select(m => m == null ? null : m.name)) };
                var missing = new JArray();
                using (var bin = new Bin(path, relative))
                {
                    var arrays = new JObject();
                    if (source != null)
                    {
                        info["vertexCount"] = source.vertexCount;
                        if (readable) arrays["vertices"] = bin.Vectors(source.vertices);
                        else missing.Add("vertices (rest positions: source mesh is not CPU-readable)");
                        var normals = source.normals;
                        if (readable && normals.Length == source.vertexCount) arrays["normals"] = bin.Vectors(normals);
                        else missing.Add("normals");
                        var tangents = source.tangents;
                        if (readable && tangents.Length == source.vertexCount) arrays["tangents"] = bin.Floats(tangents.SelectMany(t => new[] { t.x, t.y, t.z, t.w }).ToArray(), tangents.Length, 4);
                        else missing.Add("tangents");
                        var uv = source.uv;
                        if (uv.Length == source.vertexCount) { arrays["uv"] = bin.Floats(uv.SelectMany(t => new[] { t.x, t.y }).ToArray(), uv.Length, 2); info["uvHash"] = Hash(uv.SelectMany(t => new[] { t.x, t.y })); }
                        else missing.Add("uv");
                        var submeshes = new JArray();
                        for (var sub = 0; sub < source.subMeshCount; sub++)
                        {
                            var topologyKind = source.GetTopology(sub);
                            var indices = source.GetIndices(sub);
                            submeshes.Add(new JObject { ["topology"] = topologyKind.ToString(), ["indices"] = bin.Ints(indices, indices.Length) });
                        }
                        arrays["submeshes"] = submeshes;
                        if (readable) info["restHash"] = Hash(source.vertices.SelectMany(v => new[] { v.x, v.y, v.z }));
                    }
                    if (skinned != null)
                    {
                        var weights = readable ? mesh.boneWeights : new BoneWeight[0];
                        if (weights.Length > 0)
                        {
                            arrays["boneIndices"] = bin.Ints(weights.SelectMany(w => new[] { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3 }).ToArray(), weights.Length, 4);
                            arrays["boneWeights"] = bin.Floats(weights.SelectMany(w => new[] { w.weight0, w.weight1, w.weight2, w.weight3 }).ToArray(), weights.Length, 4);
                        }
                        else missing.Add("boneWeights");
                        var bind = mesh != null ? mesh.bindposes : new Matrix4x4[0];
                        if (bind.Length > 0) arrays["bindposes"] = bin.Floats(bind.SelectMany(m => Enumerable.Range(0, 16).Select(k => m[k / 4, k % 4])).ToArray(), bind.Length, 4, 4);
                        else missing.Add("bindposes");
                        info["bones"] = new JArray(skinned.bones.Select(b => b == null ? null : PathOf(b, character.transform)));
                        info["rootBone"] = skinned.rootBone == null ? null : PathOf(skinned.rootBone, character.transform);
                        var shapes = new JArray();
                        for (var i = 0; mesh != null && i < mesh.blendShapeCount; i++)
                        {
                            var shape = new JObject { ["index"] = i, ["name"] = mesh.GetBlendShapeName(i) };
                            var frames = new JArray();
                            for (var f = 0; f < mesh.GetBlendShapeFrameCount(i); f++)
                            {
                                var frame = new JObject { ["weight"] = mesh.GetBlendShapeFrameWeight(i, f) };
                                if (readable)
                                {
                                    var delta = new Vector3[mesh.vertexCount];
                                    mesh.GetBlendShapeFrameVertices(i, f, delta, new Vector3[mesh.vertexCount], new Vector3[mesh.vertexCount]);
                                    frame["deltaVertices"] = bin.Vectors(delta);
                                }
                                frames.Add(frame);
                            }
                            shape["frames"] = frames;
                            shapes.Add(shape);
                        }
                        info["blendShapes"] = shapes;
                        if (!readable && mesh != null && mesh.blendShapeCount > 0) missing.Add("blendShape deltas");
                    }
                    info["arrays"] = arrays;
                    info["bytes"] = bin.Length;
                }
                info["missing"] = missing;
                job.Renderers[key] = info;
                Track(job, relative);
                // Recorded right away: cleanup only deletes what the manifest lists, even after a crash.
                WriteManifest(job, character);
                return key;
            }
            finally { if (baked != null) UnityEngine.Object.Destroy(baked); }
        }

        private static string PathOf(Transform transform, Transform root)
        {
            var names = new List<string>();
            for (var t = transform; t != null && t != root; t = t.parent) names.Add(t.name);
            names.Reverse();
            return (transform.IsChildOf(root) ? "" : "/") + string.Join("/", names.ToArray());
        }

        private static string TopologyHash(Mesh mesh)
        {
            var values = new List<int> { mesh.vertexCount, mesh.subMeshCount };
            for (var sub = 0; sub < mesh.subMeshCount; sub++) { var indices = mesh.GetIndices(sub); values.Add(indices.Length); values.AddRange(indices); }
            var bytes = new byte[values.Count * 4];
            Buffer.BlockCopy(values.ToArray(), 0, bytes, 0, bytes.Length);
            return Hex(bytes);
        }

        private static string Hash(IEnumerable<float> values)
        {
            var array = values.ToArray();
            var bytes = new byte[array.Length * 4];
            Buffer.BlockCopy(array, 0, bytes, 0, bytes.Length);
            return Hex(bytes);
        }

        private static string Hex(byte[] bytes)
        {
            using (var md5 = MD5.Create()) return BitConverter.ToString(md5.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant().Substring(0, 16);
        }

        private static JArray Matrix(Matrix4x4 m) { return new JArray(Enumerable.Range(0, 16).Select(k => m[k / 4, k % 4])); }

        // Renders the main camera into an off-screen target in the current frame, so the image matches the sample.
        private static byte[] RenderImage(int width, int height)
        {
            var camera = Camera.main ?? throw new InvalidOperationException("No main camera to render.");
            var target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply(false, false);
                return texture.EncodeToPNG();
            }
            finally
            {
                camera.targetTexture = previousTarget;
                camera.ResetAspect();
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.Destroy(texture);
            }
        }

        // ---- manifest, status, resume and cleanup --------------------------------------------------------------------

        private static void Track(Job job, string relative)
        {
            if (!job.Files.Contains(relative)) job.Files.Add(relative);
            var path = Path.Combine(job.Folder, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) job.Bytes += new FileInfo(path).Length;
        }

        private void WriteManifest(Job job, ChaControl character)
        {
            var manifest = new JObject { ["format"] = Format, ["jobId"] = job.Id, ["state"] = job.State, ["error"] = job.Error,
                ["created"] = job.Started.ToString("yyyy-MM-dd HH:mm:ss"), ["game"] = Plugin.Game, ["mode"] = Plugin.InStudio ? "studio" : "maker",
                ["character"] = character != null ? character.fileParam.fullname : null,
                ["coordinates"] = new JObject { ["space"] = "Unity world space, left-handed, Y up, metres; not mirrored (mesh_export OBJ mirrors x, this does not)",
                    ["matrices"] = "float32[16] row-major: m00 m01 m02 m03 m10 ... m33; points transform as M * [x y z 1]",
                    ["winding"] = "Unity: triangles are clockwise when seen from their front side",
                    ["baked"] = "renderer space with its scale applied; world = bakedToWorld * baked",
                    ["skinning"] = "world = sum_i weight_i * bones[boneIndex_i] * bindposes[boneIndex_i] * (rest + blend shape deltas)",
                    ["blendShapes"] = "weights 0-100; deltas interpolated between frames by frame weight as Unity does" },
                ["arrays"] = "{file, offset (bytes), dtype (<f4 float32 or <i4 int32, little-endian), shape}; numpy: np.fromfile(file, dtype, count, offset=offset).reshape(shape)",
                ["options"] = job.Options, ["cases"] = job.Cases, ["renderers"] = job.Renderers,
                ["completed"] = new JArray(job.Completed.OrderBy(x => x, StringComparer.Ordinal)), ["files"] = new JArray(job.Files),
                ["warnings"] = job.Warnings };
            var path = Path.Combine(job.Folder, "manifest.json");
            File.WriteAllText(path + ".tmp", manifest.ToString(Formatting.Indented));
            if (File.Exists(path)) File.Delete(path);
            File.Move(path + ".tmp", path);
            if (!job.Files.Contains("manifest.json")) job.Files.Add("manifest.json");
        }

        private Job Load(string id)
        {
            Job job;
            if (_jobs.TryGetValue(id, out job)) { job.Error = null; job.Cancel = false; job.Done = job.Skipped = 0; job.Started = DateTime.Now; return job; }
            var folder = Path.Combine(Root(), id);
            var path = Path.Combine(folder, "manifest.json");
            if (!File.Exists(path)) throw new FileNotFoundException("No sample job " + id + " in " + Root());
            var manifest = JObject.Parse(File.ReadAllText(path));
            if ((string)manifest["format"] != Format) throw new InvalidOperationException("Job " + id + " has an unknown format.");
            job = new Job { Id = id, Folder = folder, Options = (JObject)manifest["options"], Cases = (JArray)manifest["cases"],
                Renderers = (JObject)manifest["renderers"] ?? new JObject() };
            foreach (var file in (JArray)manifest["files"]) job.Files.Add((string)file);
            // A sample counts as done only if both its data and its metadata were written.
            var samples = Path.Combine(folder, "samples");
            foreach (var file in Directory.Exists(samples) ? Directory.GetFiles(samples, "*.json") : new string[0])
                if (File.Exists(Path.ChangeExtension(file, ".bin")))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    job.Completed.Add(name);
                    var image = (string)JObject.Parse(File.ReadAllText(file))["image"];
                    foreach (var relative in new[] { "samples/" + name + ".json", "samples/" + name + ".bin", image })
                        if (relative != null && !job.Files.Contains(relative)) job.Files.Add(relative);
                }
            job.Done = 0;
            job.Bytes = job.Files.Select(f => Path.Combine(folder, f.Replace('/', Path.DirectorySeparatorChar))).Where(File.Exists).Sum(f => new FileInfo(f).Length);
            return job;
        }

        private JObject Status(Job job)
        {
            var remaining = Math.Max(0, job.Total - job.Completed.Count);
            return new JObject { ["jobId"] = job.Id, ["state"] = job.State, ["error"] = job.Error, ["current"] = job.Current,
                ["folder"] = job.Folder, ["manifest"] = Path.Combine(job.Folder, "manifest.json"),
                ["total"] = job.Total, ["completed"] = job.Completed.Count, ["sampledThisRun"] = job.Done, ["skippedAsDone"] = job.Skipped,
                ["remaining"] = remaining, ["bytesWritten"] = job.Bytes,
                ["estimatedRemainingBytes"] = job.SampleBytes > 0 ? (JToken)(job.SampleBytes * remaining) : null,
                ["lastSamples"] = new JArray(job.Completed.OrderBy(x => x, StringComparer.Ordinal).Reverse().Take(5)),
                ["warnings"] = job.Warnings, ["elapsedSeconds"] = Math.Round((DateTime.Now - job.Started).TotalSeconds, 1) };
        }

        // Deletes only the files this job recorded in its manifest, then its own empty folders.
        private JObject Cleanup(JObject args)
        {
            var id = (string)args["jobId"];
            if (string.IsNullOrEmpty(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("jobId is required.");
            if (_running != null && _running.Id == id) throw new InvalidOperationException("Cancel the job before cleaning it up.");
            var job = Load(id);
            var keepManifest = (bool?)args["keepManifest"] ?? false;
            var deleted = 0;
            long bytes = 0;
            foreach (var relative in job.Files)
            {
                if (keepManifest && relative == "manifest.json") continue;
                var path = Path.GetFullPath(Path.Combine(job.Folder, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(Path.GetFullPath(job.Folder), StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
                bytes += new FileInfo(path).Length;
                File.Delete(path);
                deleted++;
            }
            foreach (var sub in new[] { "samples", "rest", "images", "" })
            {
                var folder = Path.Combine(job.Folder, sub);
                if (Directory.Exists(folder) && !Directory.GetFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
            _jobs.Remove(id);
            return new JObject { ["jobId"] = id, ["deletedFiles"] = deleted, ["deletedBytes"] = bytes, ["folderRemoved"] = !Directory.Exists(job.Folder) };
        }

        // Appends typed arrays to one file and returns their JSON descriptors.
        private sealed class Bin : IDisposable
        {
            private readonly BinaryWriter _writer;
            private readonly string _relative;
            internal Bin(string path, string relative) { _writer = new BinaryWriter(File.Create(path)); _relative = relative; }
            internal long Length => _writer.BaseStream.Position;

            internal JObject Floats(float[] values, params int[] shape)
            {
                var descriptor = Descriptor("<f4", shape);
                foreach (var value in values) _writer.Write(value);
                return descriptor;
            }

            internal JObject Ints(int[] values, params int[] shape)
            {
                var descriptor = Descriptor("<i4", shape);
                foreach (var value in values) _writer.Write(value);
                return descriptor;
            }

            internal JObject Vectors(Vector3[] values)
            {
                var descriptor = Descriptor("<f4", new[] { values.Length, 3 });
                foreach (var v in values) { _writer.Write(v.x); _writer.Write(v.y); _writer.Write(v.z); }
                return descriptor;
            }

            private JObject Descriptor(string dtype, int[] shape)
            {
                return new JObject { ["file"] = _relative, ["offset"] = _writer.BaseStream.Position, ["dtype"] = dtype, ["shape"] = new JArray(shape) };
            }

            public void Dispose() { _writer.Close(); }
        }
    }
}
