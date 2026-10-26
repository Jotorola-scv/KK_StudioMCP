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
        internal bool Available => MaterialCommands.FindType("Screencap.ScreenshotManager") != null;
        internal bool DepthAvailable => MaterialCommands.FindType("Screencap.ScreenshotManager")
            ?.GetMethod("ExportOfflineReShadeInputs", BindingFlags.Public | BindingFlags.Static) != null;
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
            if (!Available) throw new NotSupportedException("Screencap is not installed.");
            if (Busy) throw new InvalidOperationException("Another capture is in progress.");
            if ((string)args["sceneSession"] != _plugin.Session) throw new InvalidOperationException("Scene session changed.");
            var width = (int?)args["width"] ?? 640;
            var height = (int?)args["height"] ?? 360;
            var supersampling = (int?)args["supersampling"] ?? 1;
            var withDepth = (bool?)args["withDepth"] ?? false;
            if (withDepth && !DepthAvailable) throw new NotSupportedException("Installed Screencap has no paired color/depth export API.");
            if (width < 64 || height < 64 || width > 7680 || height > 7680
                || supersampling < 1 || supersampling > 4
                || (long)width * height * supersampling * supersampling > 100000000)
                throw new ArgumentOutOfRangeException("Capture dimensions exceed the configured safety limits.");
            var id = Guid.NewGuid().ToString("N");
            var record = new JObject { ["jobId"] = id, ["state"] = "waiting",
                ["sceneSession"] = _plugin.Session, ["width"] = width, ["height"] = height,
                ["withDepth"] = withDepth };
            _jobs.Add(id, record);
            _jobOrder.Enqueue(id);
            while (_jobOrder.Count > 8) _jobs.Remove(_jobOrder.Dequeue());
            Busy = true;
            _plugin.StartCoroutine(Capture(record, width, height, supersampling));
            return new JObject { ["jobId"] = id, ["state"] = "waiting" };
        }

        private IEnumerator Capture(JObject record, int width, int height, int supersampling)
        {
            yield return new WaitForEndOfFrame();
            RenderTexture render = null;
            RenderTexture preview = null;
            Texture2D exportedColor = null;
            var previous = RenderTexture.active;
            MethodInfo post = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var api = MaterialCommands.FindType("Screencap.ScreenshotManager");
                var pre = api.GetMethod("FirePreCapture", BindingFlags.Public | BindingFlags.Static);
                post = api.GetMethod("FirePostCapture", BindingFlags.Public | BindingFlags.Static);
                if ((string)record["sceneSession"] != _plugin.Session)
                    throw new InvalidOperationException("Scene changed before capture.");
                record["state"] = "running";
                pre.Invoke(null, null);
                var alpha = MaterialCommands.FindType("Screencap.AlphaMode");
                var opaque = Enum.Parse(alpha, "None");
                var path = Path.Combine(_plugin.OutputRoot, (string)record["jobId"] + ".png");
                byte[] png;
                Texture source;
                if ((bool)record["withDepth"])
                {
                    var extension = (string)api.GetMethod("GetOfflineReShadeDepthExtension").Invoke(null, null);
                    if (string.IsNullOrEmpty(extension) || extension.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                        throw new InvalidOperationException("Screencap returned an invalid depth extension.");
                    var depthPath = Path.Combine(_plugin.OutputRoot, (string)record["jobId"] + ".depth" + extension);
                    var metadataPath = Path.Combine(_plugin.OutputRoot, (string)record["jobId"] + ".depth.json");
                    var ok = (bool)api.GetMethod("ExportOfflineReShadeInputs").Invoke(null,
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
                    render = (RenderTexture)api.GetMethod("CaptureRender", BindingFlags.Public | BindingFlags.Static)
                        .Invoke(null, new object[] { (int?)width, (int?)height, (int?)supersampling, opaque });
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
                try { post?.Invoke(null, null); }
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
