using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
                if (_plugin.StartCoroutine(Capture(record, width, height, supersampling, api, nativeMode)) == null)
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

        private IEnumerator Capture(JObject record, int width, int height, int supersampling, NativeApi api, object nativeMode)
        {
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
