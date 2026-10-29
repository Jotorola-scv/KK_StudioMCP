using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudioMcp.Bridge
{
    internal sealed class CaptureCommands
    {
        private readonly Plugin _plugin;
        private readonly Dictionary<string, JObject> _jobs = new Dictionary<string, JObject>();
        private readonly Queue<string> _jobOrder = new Queue<string>();
        internal bool Busy { get; private set; }
        // When the camera last moved (tool call or mouse). Maker renders keep a ghost of earlier views for about 1.7 s after a
        // move; Studio does not, so it only needs a short settle.
        internal static float LastCameraMove = -100f;
        private static float MoveSettle => Plugin.InStudio ? 0.3f : 2f;
        private static Vector3 _trackedPosition;
        private static Quaternion _trackedRotation;

        // Called every frame, so mouse orbits count as camera moves too.
        internal static void TrackCamera()
        {
            var camera = Camera.main;
            if (camera == null) return;
            var t = camera.transform;
            if ((t.position - _trackedPosition).sqrMagnitude > 1e-8f || Quaternion.Angle(t.rotation, _trackedRotation) > 0.01f)
                LastCameraMove = Time.unscaledTime;
            _trackedPosition = t.position;
            _trackedRotation = t.rotation;
        }
        private sealed class NativeApi
        {
            internal Type Alpha;
            internal MethodInfo Pre, Post, Render, ExportDepth, DepthExtension;
            internal bool HasMode(string name) => Enum.IsDefined(Alpha, name);
            internal bool HasDepth => ExportDepth != null && DepthExtension != null && HasMode("None");
        }

        private static MethodInfo Method(Type type, string name, Type result, params Type[] arguments)
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, arguments, null);
            return method != null && !method.ContainsGenericParameters && method.ReturnType == result ? method : null;
        }

        private static NativeApi Resolve(out string reason)
        {
            reason = null;
            var type = MaterialCommands.FindType("Screencap.ScreenshotManager");
            if (type == null) { reason = "Screencap is not installed."; return null; }
            var alpha = MaterialCommands.FindType("Screencap.AlphaMode");
            if (alpha == null || !alpha.IsEnum)
            { reason = "Screencap has no compatible native AlphaMode enum."; return null; }
            var api = new NativeApi
            {
                Alpha = alpha,
                Pre = Method(type, "FirePreCapture", typeof(void)),
                Post = Method(type, "FirePostCapture", typeof(void)),
                Render = Method(type, "CaptureRender", typeof(RenderTexture), typeof(int?), typeof(int?), typeof(int?),
                    typeof(Nullable<>).MakeGenericType(alpha))
                    ?? Method(type, "CaptureRender", typeof(RenderTexture), typeof(int?), typeof(int?), typeof(int?), alpha),
                ExportDepth = Method(type, "ExportOfflineReShadeInputs", typeof(bool), typeof(string), typeof(string), typeof(string),
                    typeof(int?), typeof(int?), typeof(int?)),
                DepthExtension = Method(type, "GetOfflineReShadeDepthExtension", typeof(string))
            };
            if (api.Pre == null || api.Post == null || api.Render == null)
            { reason = "Installed Screencap lacks compatible public capture/hook signatures."; return null; }
            return api;
        }

        internal bool Available { get { string reason; return Resolve(out reason) != null; } }
        internal bool DepthAvailable { get { string reason; var api = Resolve(out reason); return api != null && api.HasDepth; } }
        internal JObject Capabilities
        {
            get
            {
                string reason;
                var api = Resolve(out reason);
                var modes = new JArray();
                if (api != null) foreach (var name in new[] { "None", "blackout", "rgAlpha" }) if (api.HasMode(name)) modes.Add(name);
                return new JObject { ["available"] = api != null, ["reason"] = reason,
                    ["alphaModes"] = modes, ["pairedDepth"] = api != null && api.HasDepth,
                    ["pairedDepthAlphaMode"] = "None" };
            }
        }
        internal CaptureCommands(Plugin plugin) { _plugin = plugin; }

        internal JToken Execute(string method, JObject args)
        {
            if (method == "capture.status")
            {
                JObject job;
                if (!_jobs.TryGetValue((string)args["jobId"], out job)) throw new ArgumentException("Unknown capture job.");
                return job.DeepClone();
            }
            if (method == "capture.turntable") return StartTurntable(args);
            if (method != "capture.start") throw new ArgumentException("Unknown capture method.");
            if (Busy) throw new InvalidOperationException("Another capture is in progress.");
            if ((string)args["sceneSession"] != _plugin.Session) throw new InvalidOperationException("Scene session changed.");
            var width = (int?)args["width"] ?? 640;
            var height = (int?)args["height"] ?? 360;
            var supersampling = (int?)args["supersampling"] ?? 1;
            var withDepth = (bool?)args["withDepth"] ?? false;
            var alphaToken = args["alphaMode"];
            if (alphaToken != null && alphaToken.Type != JTokenType.Null && alphaToken.Type != JTokenType.String)
                throw new ArgumentException("alphaMode must be a named string.");
            var alphaMode = (string)alphaToken ?? "None";
            if (alphaMode != "None" && alphaMode != "blackout" && alphaMode != "rgAlpha")
                throw new ArgumentException("alphaMode must be None, blackout or rgAlpha (native Screencap modes).");
            if (withDepth && alphaMode != "None") throw new ArgumentException("Paired depth export requires alphaMode=None.");
            if (width < 64 || height < 64 || width > 7680 || height > 7680
                || supersampling < 1 || supersampling > 4
                || (long)width * height * supersampling * supersampling > 100000000)
                throw new ArgumentOutOfRangeException("Capture dimensions exceed the configured safety limits.");
            string reason;
            var api = Resolve(out reason);
            if (api == null) throw new NotSupportedException(reason);
            if (!api.HasMode(alphaMode)) throw new NotSupportedException("Installed Screencap does not expose alphaMode=" + alphaMode + ".");
            if (withDepth && !api.HasDepth) throw new NotSupportedException("Installed Screencap has no compatible paired color/depth export API.");
            var nativeMode = Enum.Parse(api.Alpha, alphaMode);
            var id = Guid.NewGuid().ToString("N");
            var record = new JObject { ["jobId"] = id, ["state"] = "waiting",
                ["sceneSession"] = _plugin.Session, ["width"] = width, ["height"] = height,
                ["withDepth"] = withDepth, ["alphaMode"] = alphaMode };
            _jobs.Add(id, record);
            _jobOrder.Enqueue(id);
            while (_jobOrder.Count > 8) _jobs.Remove(_jobOrder.Dequeue());
            Busy = true;
            try
            {
                var settleSeconds = args["settleSeconds"] == null ? 0f : Mathf.Clamp(SceneCommands.Number(args["settleSeconds"]), 0f, 10f);
                if (_plugin.StartCoroutine(Capture(record, width, height, supersampling, api, nativeMode, settleSeconds)) == null)
                    throw new InvalidOperationException("The capture coroutine did not start.");
            }
            catch
            {
                Busy = false;
                record["state"] = "failed";
                record["error"] = "Cannot start the capture coroutine.";
                throw;
            }
            return new JObject { ["jobId"] = id, ["state"] = "waiting" };
        }

        // Waits until every enabled camera has kept its position, rotation and FOV for five frames (they glide after a move,
        // and a follower camera can lag the main one), then for at least settleSeconds; records the frames waited.
        private IEnumerator Settle(JObject record, float settleSeconds, int minFrames)
        {
            var start = Time.frameCount;
            var started = Time.unscaledTime;
            var state = Snapshot();
            for (int f = 0, still = 0; f < 900 && still < 5; f++)
            {
                yield return null;
                var next = Snapshot();
                still = Same(state, next) ? still + 1 : 0;
                state = next;
            }
            var moving = Time.frameCount - start;
            if (moving > 5) LastCameraMove = Time.unscaledTime;
            var until = Mathf.Max(Time.unscaledTime + settleSeconds, LastCameraMove + MoveSettle);
            for (var f = 0; f < minFrames || Time.unscaledTime < until; f++) yield return null;
            var waits = record["waits"] as JArray ?? new JArray();
            waits.Add(new JObject { ["cameraFrames"] = moving, ["totalFrames"] = Time.frameCount - start,
                ["seconds"] = Math.Round(Time.unscaledTime - started, 2) });
            record["waits"] = waits;
        }

        private static List<Vector4> Snapshot()
        {
            var result = new List<Vector4>();
            foreach (var camera in Camera.allCameras)
            {
                var p = camera.transform.position;
                var r = camera.transform.rotation;
                result.Add(new Vector4(p.x, p.y, p.z, camera.fieldOfView));
                result.Add(new Vector4(r.x, r.y, r.z, r.w));
            }
            return result;
        }

        private static bool Same(List<Vector4> a, List<Vector4> b)
        {
            if (a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++) if ((a[i] - b[i]).sqrMagnitude > 1e-9f) return false;
            return true;
        }

        private IEnumerator Capture(JObject record, int width, int height, int supersampling, NativeApi api, object nativeMode, float settleSeconds)
        {
            yield return _plugin.StartCoroutine(Settle(record, settleSeconds, 1));
            yield return new WaitForEndOfFrame();
            RenderTexture render = null;
            RenderTexture preview = null;
            Texture2D exportedColor = null;
            var previous = RenderTexture.active;
            var hooksStarted = false;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if ((string)record["sceneSession"] != _plugin.Session)
                    throw new InvalidOperationException("Scene changed before capture.");
                record["state"] = "running";
                hooksStarted = true;
                api.Pre.Invoke(null, null);
                var path = Path.Combine(_plugin.OutputRoot, (string)record["jobId"] + ".png");
                byte[] png;
                Texture source;
                if ((bool)record["withDepth"])
                {
                    var extension = (string)api.DepthExtension.Invoke(null, null);
                    if (string.IsNullOrEmpty(extension) || extension.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                        throw new InvalidOperationException("Screencap returned an invalid depth extension.");
                    var depthPath = Path.Combine(_plugin.OutputRoot, (string)record["jobId"] + ".depth" + extension);
                    var metadataPath = Path.Combine(_plugin.OutputRoot, (string)record["jobId"] + ".depth.json");
                    var ok = (bool)api.ExportDepth.Invoke(null,
                        new object[] { path, depthPath, metadataPath, (int?)width, (int?)height, (int?)supersampling });
                    if (!ok || !File.Exists(depthPath) || new FileInfo(depthPath).Length == 0 || !File.Exists(metadataPath))
                        throw new InvalidOperationException("Paired depth export failed; no depth validation is claimed.");
                    png = File.ReadAllBytes(path);
                    exportedColor = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!exportedColor.LoadImage(png)) throw new IOException("Cannot decode exported color image.");
                    source = exportedColor;
                    record["depthPath"] = depthPath;
                    record["depthMetadataPath"] = metadataPath;
                    record["depthBytes"] = new FileInfo(depthPath).Length;
                }
                else
                {
                    render = (RenderTexture)api.Render.Invoke(null, new object[] { (int?)width, (int?)height, (int?)supersampling, nativeMode });
                    if (render == null) throw new InvalidOperationException("Screencap returned no image; inspect the game log.");
                    png = Encode(render);
                    File.WriteAllBytes(path, png);
                    source = render;
                }
                var previewWidth = Math.Min(960, source.width);
                var previewHeight = Math.Max(1, (int)Math.Round((double)source.height * previewWidth / source.width));
                if (previewWidth != source.width)
                {
                    preview = RenderTexture.GetTemporary(previewWidth, previewHeight, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(source, preview);
                    record["previewBase64"] = Convert.ToBase64String(Encode(preview));
                }
                else record["previewBase64"] = Convert.ToBase64String(png);
                record["path"] = path;
                record["bytes"] = png.Length;
                record["elapsedMs"] = watch.ElapsedMilliseconds;
                record["state"] = "complete";
            }
            catch (Exception error)
            {
                record["state"] = "failed";
                record["error"] = (error as TargetInvocationException)?.InnerException?.Message ?? error.Message;
            }
            finally
            {
                RenderTexture.active = previous;
                if (preview != null) RenderTexture.ReleaseTemporary(preview);
                if (render != null) RenderTexture.ReleaseTemporary(render);
                if (exportedColor != null) UnityEngine.Object.Destroy(exportedColor);
                try { if (hooksStarted) api.Post.Invoke(null, null); }
                catch (Exception error) { record["state"] = "failed"; record["error"] = error.GetBaseException().Message; }
                finally { Busy = false; }
            }
        }

        // Turntable: one Screencap render per view around a character, tiled into a contact sheet. Added in the maker-support fork.
        private JObject StartTurntable(JObject args)
        {
            if (Busy) throw new InvalidOperationException("Another capture is in progress.");
            if ((string)args["sceneSession"] != _plugin.Session) throw new InvalidOperationException("Scene session changed.");
            string reason;
            var api = Resolve(out reason);
            if (api == null) throw new NotSupportedException(reason);
            var views = args["views"] is JArray list ? list.Select(SceneCommands.Number).ToArray() : new[] { 0f, 45, 90, 135, 180, 225, 270, 315 };
            if (views.Length == 0 || views.Length > 16) throw new ArgumentException("views takes 1-16 yaw angles (0 = front).");
            var pitch = args["pitch"] == null ? 0f : SceneCommands.Number(args["pitch"]);
            if (pitch < -89 || pitch > 89) throw new ArgumentOutOfRangeException("pitch");
            var width = (int?)args["tileWidth"] ?? 360;
            var height = (int?)args["tileHeight"] ?? 480;
            var columns = Math.Max(1, Math.Min(views.Length, (int?)args["columns"] ?? 4));
            var rows = (views.Length + columns - 1) / columns;
            if (width < 64 || height < 64 || width * columns > 4096 || height * rows > 4096)
                throw new ArgumentOutOfRangeException("tileWidth", "The sheet must stay within 4096x4096.");
            var target = (string)args["target"] ?? "full";
            if (target != "full" && target != "head") throw new ArgumentException("target is full or head.");
            var fov = args["fov"] == null ? 23f : SceneCommands.Number(args["fov"]);
            if (fov < 5 || fov > 90) throw new ArgumentOutOfRangeException("fov");

            var character = _plugin.Characters.Character((int?)args["objectId"] ?? 0);
            Bounds bounds;
            if (target == "head")
            {
                // Skinned renderer bounds are padded far beyond the mesh, so frame the head from its baked vertices.
                var face = AnalysisCommands.Bake(character.rendFace) ?? throw new InvalidOperationException("The face mesh cannot be read.");
                bounds = new Bounds(face.Points[0], Vector3.zero);
                foreach (var point in face.Points) bounds.Encapsulate(point);
            }
            else
            {
                // Body and face from their baked vertices (renderer bounds are padded), with a margin for hair and shoes.
                var body = AnalysisCommands.Bake(character.rendBody);
                var face = AnalysisCommands.Bake(character.rendFace);
                var points = (body?.Points ?? new Vector3[0]).Concat(face?.Points ?? new Vector3[0]).ToArray();
                if (points.Length == 0) throw new InvalidOperationException("The body mesh cannot be read.");
                bounds = new Bounds(points[0], Vector3.zero);
                foreach (var point in points) bounds.Encapsulate(point);
                bounds.Expand(new Vector3(0.25f, 0.08f, 0.25f));
            }
            var size = Mathf.Max(bounds.size.y, Mathf.Max(bounds.size.x, bounds.size.z) * height / (float)width);
            var distance = args["distance"] == null ? size * 0.5f * 1.12f / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) : SceneCommands.Number(args["distance"]);
            var markers = args["markers"] as JArray ?? new JArray();
            if (markers.Count > 400) throw new ArgumentException("At most 400 markers.");
            foreach (var m in markers) SceneCommands.Vector(m);
            var markerSize = args["markerSize"] == null ? 0.012f : SceneCommands.Number(args["markerSize"]);
            var settle = Mathf.Clamp((int?)args["settleFrames"] ?? 10, 1, 240);
            // Measured on KKS: tiles stay ghosted for well over 30 frames after a camera move, clean after about 1.7 s (120 frames); default 2 s.
            var settleSeconds = args["settleSeconds"] == null ? 2f : Mathf.Clamp(SceneCommands.Number(args["settleSeconds"]), 0f, 10f);

            var id = Guid.NewGuid().ToString("N");
            var record = new JObject { ["jobId"] = id, ["state"] = "waiting", ["sceneSession"] = _plugin.Session,
                ["views"] = new JArray(views), ["columns"] = columns, ["width"] = width * columns, ["height"] = height * rows };
            _jobs.Add(id, record);
            _jobOrder.Enqueue(id);
            while (_jobOrder.Count > 8) _jobs.Remove(_jobOrder.Dequeue());
            Busy = true;
            var frontYaw = character.transform.eulerAngles.y + 180f;
            var cameras = views.Select(yaw => new JObject { ["pivot"] = SceneCommands.Json(bounds.center),
                ["rotation"] = new JArray(pitch, frontYaw + yaw, 0f), ["distance"] = new JArray(0f, 0f, -distance), ["fov"] = fov }).ToArray();
            if (_plugin.StartCoroutine(Turntable(record, api, cameras, width, height, columns, rows, character.rendFace.gameObject.layer, markers, markerSize, settle, settleSeconds)) == null)
            {
                Busy = false;
                throw new InvalidOperationException("The capture coroutine did not start.");
            }
            return new JObject { ["jobId"] = id, ["state"] = "waiting" };
        }

        private IEnumerator Turntable(JObject record, NativeApi api, JObject[] cameras, int width, int height, int columns, int rows,
            int layer, JArray markers, float markerSize, int settle, float settleSeconds)
        {
            var saved = (JObject)_plugin.Dispatch("camera.get", new JObject());
            var spawned = new List<GameObject>();
            Texture2D sheet = null;
            RenderTexture preview = null;
            var previous = RenderTexture.active;
            var none = Enum.Parse(api.Alpha, "None");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var failed = false;
            try
            {
                // ZTest Always keeps gap markers visible from every side, even behind hair.
                var material = new Material(Shader.Find("Hidden/Internal-Colored")) { color = new Color(1f, 0.1f, 0.25f, 1f) };
                material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                material.renderQueue = 4000;
                foreach (var m in markers)
                {
                    var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    UnityEngine.Object.Destroy(dot.GetComponent<Collider>());
                    dot.layer = layer;
                    dot.transform.position = SceneCommands.Vector(m);
                    dot.transform.localScale = Vector3.one * markerSize;
                    dot.GetComponent<Renderer>().sharedMaterial = material;
                    spawned.Add(dot);
                }
                sheet = new Texture2D(width * columns, height * rows, TextureFormat.RGBA32, false);
                record["state"] = "running";
            }
            catch (Exception error) { failed = true; record["state"] = "failed"; record["error"] = error.GetBaseException().Message; }

            for (var i = 0; i < cameras.Length && !failed; i++)
            {
                try { _plugin.Dispatch("camera.set", cameras[i]); }
                catch (Exception error) { failed = true; record["state"] = "failed"; record["error"] = error.GetBaseException().Message; break; }
                // The maker/Studio cameras glide to a new orbit over many frames, and the game's temporal effects blend
                // frames, so rendering too early ghosts the previous view. Wait until the camera stops, then settle.
                yield return _plugin.StartCoroutine(Settle(record, settleSeconds, settle));
                yield return new WaitForEndOfFrame();
                RenderTexture render = null;
                try
                {
                    api.Pre.Invoke(null, null);
                    try { render = (RenderTexture)api.Render.Invoke(null, new object[] { (int?)width, (int?)height, (int?)1, none }); }
                    finally { api.Post.Invoke(null, null); }
                    if (render == null) throw new InvalidOperationException("Screencap returned no image; inspect the game log.");
                    RenderTexture.active = render;
                    sheet.ReadPixels(new Rect(0, 0, width, height), (i % columns) * width, (rows - 1 - i / columns) * height);
                }
                catch (Exception error) { failed = true; record["state"] = "failed"; record["error"] = (error as TargetInvocationException)?.InnerException?.Message ?? error.Message; }
                finally
                {
                    RenderTexture.active = previous;
                    if (render != null) RenderTexture.ReleaseTemporary(render);
                }
            }
            try
            {
                if (!failed)
                {
                    sheet.Apply(false, false);
                    var png = sheet.EncodeToPNG();
                    var path = Path.Combine(_plugin.OutputRoot, (string)record["jobId"] + "-turntable.png");
                    File.WriteAllBytes(path, png);
                    var previewWidth = Math.Min(1600, sheet.width);
                    if (previewWidth != sheet.width)
                    {
                        preview = RenderTexture.GetTemporary(previewWidth, Math.Max(1, sheet.height * previewWidth / sheet.width), 0, RenderTextureFormat.ARGB32);
                        Graphics.Blit(sheet, preview);
                        record["previewBase64"] = Convert.ToBase64String(Encode(preview));
                    }
                    else record["previewBase64"] = Convert.ToBase64String(png);
                    record["path"] = path;
                    record["bytes"] = png.Length;
                    record["elapsedMs"] = watch.ElapsedMilliseconds;
                    record["state"] = "complete";
                }
            }
            catch (Exception error) { record["state"] = "failed"; record["error"] = error.GetBaseException().Message; }
            finally
            {
                RenderTexture.active = previous;
                if (preview != null) RenderTexture.ReleaseTemporary(preview);
                if (sheet != null) UnityEngine.Object.Destroy(sheet);
                foreach (var dot in spawned) UnityEngine.Object.Destroy(dot);
                try { _plugin.Dispatch("camera.set", saved); }
                catch (Exception error) { UnityEngine.Debug.LogWarning("[StudioMCP] Turntable camera restore: " + error.Message); }
                Busy = false;
            }
        }

        private static byte[] Encode(RenderTexture render)
        {
            var previous = RenderTexture.active;
            Texture2D readable = null;
            try
            {
                RenderTexture.active = render;
                readable = new Texture2D(render.width, render.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, render.width, render.height), 0, 0);
                readable.Apply(false, false);
                return readable.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                if (readable != null) UnityEngine.Object.Destroy(readable);
            }
        }
    }
}
