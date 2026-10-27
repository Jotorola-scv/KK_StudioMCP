using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using StudioMcp.Bridge;
using UnityEngine;
using Dof = TomSceneEffectsPlugin.Plugin;
using Service = TomBSDFRuntime.TomBSDFSceneService;
using Native = Screencap.ScreenshotManager;

internal static class Program
{
    private static readonly List<string> Passed = new List<string>();
    private static Dictionary<string, Type> Types;
    private static Plugin Plugin;
    private static CaptureCommands Capture;
    private static string Root, Output;

    private static void Check(bool condition, string detail = "assertion failed")
    { if (!condition) throw new Exception(detail); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Test(string name, Action action)
    {
        Output = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Output);
        Dof.Reset(); Native.Reset(); Service.Current = null;
        Camera.main = new Camera(); RenderTexture.active = null; RenderTexture.Released = 0;
        Texture2D.Live = 0; Texture2D.FailRead = Texture2D.FailEncode = false; Graphics.Fail = false;
        Types = new Dictionary<string, Type>
        {
            ["TomSceneEffectsPlugin.Plugin"] = typeof(Dof),
            ["TomBSDFRuntime.TomBSDFSceneService"] = typeof(Service),
            ["Screencap.ScreenshotManager"] = typeof(Native),
            ["Screencap.AlphaMode"] = typeof(Screencap.AlphaMode)
        };
        MaterialCommands.Resolve = key => Types.TryGetValue(key, out var type) ? type : null;
        Plugin = new Plugin { OutputRoot = Output };
        Capture = new CaptureCommands(Plugin);
        action();
        Check(Texture2D.Live == 0, "Readable texture leaked");
        Passed.Add(name);
    }

    private static JObject Request(string mode = null, bool depth = false)
    {
        var request = new JObject { ["sceneSession"] = Plugin.Session, ["withDepth"] = depth };
        if (mode != null) request["alphaMode"] = mode;
        return request;
    }
    private static JObject RunCapture(JObject request)
    {
        var job = Capture.Execute("capture.start", request);
        Check(Capture.Busy);
        Plugin.Drain();
        Check(!Capture.Busy);
        return (JObject)Capture.Execute("capture.status", new JObject { ["jobId"] = job["jobId"] });
    }
    private static void NoCapture()
    {
        Check(!Capture.Busy && Plugin.Pending == null && Native.Pre == 0 && Native.Post == 0);
        Check(Directory.GetFiles(Output).Length == 0);
    }
    private static TomBSDFRuntime.Buffers Buffers()
    {
        var buffers = new TomBSDFRuntime.Buffers
        {
            Snapshot = new TomBSDFRuntime.TomSceneBufferSnapshot
            {
                color = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGB32),
                cameraID = Camera.main.GetInstanceID(), sequence = 23
            }
        };
        Service.Current = new Service { Buffers = buffers };
        return buffers;
    }

    private static void Main(string[] args)
    {
        Root = Path.GetFullPath(args.Length == 0 ? ".local/extension-contracts" : args[0]);
        Directory.CreateDirectory(Root);
#if KK
        Test("KK DOF stays unavailable", () => Check((string)DofCompatibilityCommands.State()["code"] == "kks_only"));
        Test("KK setter is rejected", () => Throws<NotSupportedException>(() => DofCompatibilityCommands.Apply(new JObject { ["scenePolicy"] = "Native" })));
        Test("KK snapshot stays unavailable", () => Check((string)DofCompatibilityCommands.Snapshot(Output, false)["code"] == "kks_only"));
#else
        Test("Missing DOF provider is normal unavailable", () =>
        { Types.Remove("TomSceneEffectsPlugin.Plugin"); Check(!(bool)DofCompatibilityCommands.State()["available"]); });
        Test("Unapproved provider assembly is rejected", () =>
        { Types["TomSceneEffectsPlugin.Plugin"] = typeof(Program); Check(!(bool)DofCompatibilityCommands.State()["available"]); });
        Test("Wrong StateJson signature is rejected", () =>
        { Types["TomSceneEffectsPlugin.Plugin"] = typeof(TomSceneEffectsPlugin.WrongStateSignature); Check((string)DofCompatibilityCommands.State()["code"] == "provider_incompatible"); });
        Test("State fields preserved with capability metadata", () =>
        { var state = DofCompatibilityCommands.State(); Check((bool)state["available"] && (int)state["scenePolicy"] == -1 && (bool)state["mcp"]["scenePolicySupported"]); Check(Dof.Writes == 0); });
        Test("Provider not ready is not enabled by adapter", () =>
        { Dof.Ready = false; Throws<InvalidOperationException>(() => DofCompatibilityCommands.Apply(new JObject { ["globalMode"] = "Native" })); Check(Dof.Writes == 0); });
        Test("Invalid provider JSON is unavailable", () =>
        { Dof.JsonOverride = "not json"; Check(!(bool)DofCompatibilityCommands.State()["available"]); });
        Test("Missing available field is incompatible", () =>
        { Dof.JsonOverride = "{}"; Check((string)DofCompatibilityCommands.State()["code"] == "provider_incompatible"); });
        Test("Provider read exception is unavailable", () =>
        { Dof.FailRead = true; Check(!(bool)DofCompatibilityCommands.State()["available"]); });
        Test("Empty setter rejected", () => Throws<ArgumentException>(() => DofCompatibilityCommands.Apply(new JObject())));
        foreach (var token in new JToken[] { 1, true, new JArray("Native"), "", "Inherit" })
        {
            var value = token;
            Test("Invalid global token " + value.Type + ":" + value, () =>
            { Throws<ArgumentException>(() => DofCompatibilityCommands.Apply(new JObject { ["globalMode"] = value })); Check(Dof.Writes == 0); });
        }
        Test("Inherit maps to minus one", () =>
        { Dof.Scene = 1; var result = DofCompatibilityCommands.Apply(new JObject { ["scenePolicy"] = "Inherit" }); Check(Dof.Scene == -1 && Dof.Writes == 1 && (int)result["after"]["scenePolicy"] == -1); });
        Test("Native maps to zero", () =>
        { Dof.Global = 1; DofCompatibilityCommands.Apply(new JObject { ["globalMode"] = "native" }); Check(Dof.Global == 0 && Dof.Writes == 1); });
        Test("BeforeTransparents maps to one", () =>
        { DofCompatibilityCommands.Apply(new JObject { ["scenePolicy"] = "BeforeTransparents" }); Check(Dof.Scene == 1); });
        Test("Both requested setters prevalidated before writes", () =>
        { Types["TomSceneEffectsPlugin.Plugin"] = typeof(TomSceneEffectsPlugin.ReadOnlyVersion); Throws<NotSupportedException>(() => DofCompatibilityCommands.Apply(new JObject { ["scenePolicy"] = "Native", ["globalMode"] = "BeforeTransparents" })); Check(Dof.Writes == 0); });
        Test("Older provider can retain supported global setter", () =>
        { Types["TomSceneEffectsPlugin.Plugin"] = typeof(TomSceneEffectsPlugin.ReadOnlyVersion); Check(!(bool)DofCompatibilityCommands.State()["mcp"]["scenePolicySupported"]); DofCompatibilityCommands.Apply(new JObject { ["globalMode"] = "Native" }); Check(Dof.Writes == 1); });
        Test("Both settings are forwarded once", () =>
        { DofCompatibilityCommands.Apply(new JObject { ["scenePolicy"] = "Inherit", ["globalMode"] = "BeforeTransparents" }); Check(Dof.Global == 1 && Dof.Scene == -1 && Dof.Writes == 2); });
        Test("Partial provider failure is not blindly replayed", () =>
        { Dof.FailScene = true; Throws<InvalidOperationException>(() => DofCompatibilityCommands.Apply(new JObject { ["scenePolicy"] = "Native", ["globalMode"] = "BeforeTransparents" })); Check(Dof.Global == 1 && Dof.Writes == 1); });

        Test("Missing snapshot provider is unavailable", () =>
        { Types.Remove("TomBSDFRuntime.TomBSDFSceneService"); Check(!(bool)DofCompatibilityCommands.Snapshot(Output, false)["available"]); Check(Directory.GetFiles(Output).Length == 0); });
        Test("Snapshot rejects wrong provider assembly", () =>
        { Types["TomBSDFRuntime.TomBSDFSceneService"] = typeof(Program); Check(!(bool)DofCompatibilityCommands.Snapshot(Output, false)["available"]); });
        Test("Snapshot does not create service", () =>
        { Check((string)DofCompatibilityCommands.Snapshot(Output, false)["code"] == "service_unavailable"); Check(Service.Current == null); });
        Test("Snapshot without camera is unavailable", () =>
        { Camera.main = null; Check((string)DofCompatibilityCommands.Snapshot(Output, false)["code"] == "camera_unavailable"); });
        Test("Snapshot without buffers is unavailable", () =>
        { Service.Current = new Service(); Check((string)DofCompatibilityCommands.Snapshot(Output, false)["code"] == "buffers_unavailable"); });
        Test("Incompatible snapshot signature rejected", () =>
        { Service.Current = new Service { Buffers = new TomBSDFRuntime.WrongBuffers() }; Check((string)DofCompatibilityCommands.Snapshot(Output, false)["code"] == "provider_incompatible"); });
        Test("No completed snapshot does not allocate", () =>
        { var buffers = Buffers(); buffers.Ready = false; Check((string)DofCompatibilityCommands.Snapshot(Output, false)["code"] == "snapshot_unavailable"); Check(buffers.Calls == 1 && Directory.GetFiles(Output).Length == 0); });
        Test("Missing color is unavailable", () =>
        { var buffers = Buffers(); buffers.Snapshot.color = null; Check((string)DofCompatibilityCommands.Snapshot(Output, false)["code"] == "color_unavailable"); });
        Test("Released color is unavailable", () =>
        { var buffers = Buffers(); buffers.Snapshot.color.created = false; Check((string)DofCompatibilityCommands.Snapshot(Output, false)["code"] == "color_unavailable"); });
        Test("Different camera snapshot rejected", () =>
        { var buffers = Buffers(); buffers.Snapshot.cameraID++; Check((string)DofCompatibilityCommands.Snapshot(Output, false)["code"] == "camera_mismatch"); });
        Test("Oversized readback rejected", () =>
        { var buffers = Buffers(); buffers.Snapshot.color.width = 7680; buffers.Snapshot.color.height = 7680; Check((string)DofCompatibilityCommands.Snapshot(Output, false)["code"] == "readback_limit"); });
        Test("Unresolved MSAA readback rejected", () =>
        { var buffers = Buffers(); buffers.Snapshot.color.antiAliasing = 4; Check((string)DofCompatibilityCommands.Snapshot(Output, false)["code"] == "readback_limit"); });
        Test("Snapshot retains provider ownership and active target", () =>
        {
            var buffers = Buffers(); var previous = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGB32); RenderTexture.active = previous;
            var result = DofCompatibilityCommands.Snapshot(Output, false);
            Check((bool)result["available"] && !(bool)result["freshCapture"] && (int)result["sequence"] == 23);
            Check(File.Exists((string)result["path"]) && RenderTexture.active == previous && !buffers.Snapshot.color.destroyed);
            Check(buffers.Calls == 1 && RenderTexture.Released == 0 && Native.Pre == 0 && Dof.Writes == 0);
        });
        Test("Snapshot does not require DOF provider", () =>
        { Buffers(); Types.Remove("TomSceneEffectsPlugin.Plugin"); var result = DofCompatibilityCommands.Snapshot(Output, false); Check((bool)result["available"] && !(bool)result["provider"]["available"]); });
        foreach (var encodingFailure in new[] { false, true })
        {
            var encode = encodingFailure;
            Test(encode ? "Snapshot encode failure cleans up" : "Snapshot read failure cleans up", () =>
            {
                var buffers = Buffers(); var previous = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGB32); RenderTexture.active = previous;
                Texture2D.FailRead = !encode; Texture2D.FailEncode = encode;
                Throws<InvalidOperationException>(() => DofCompatibilityCommands.Snapshot(Output, false));
                Check(RenderTexture.active == previous && !buffers.Snapshot.color.destroyed && Directory.GetFiles(Output).Length == 0);
            });
        }
        Test("Snapshot write failure cleans readable texture", () =>
        { Buffers(); Throws<DirectoryNotFoundException>(() => DofCompatibilityCommands.Snapshot(Path.Combine(Output, "absent"), false)); });
#endif
        Test("Snapshot blocked during capture", () =>
        { Throws<InvalidOperationException>(() => DofCompatibilityCommands.Snapshot(Output, true)); Check(Directory.GetFiles(Output).Length == 0); });
        Test("Capture capability allowlist", () =>
        { Check(Capture.Available && Capture.DepthAvailable); var modes = (JArray)Capture.Capabilities["alphaModes"]; Check(modes.Count == 3 && !modes.Any(x => (string)x == "UnexposedNativeMode")); });
        Test("Missing Screencap does not start job", () =>
        { Types.Remove("Screencap.ScreenshotManager"); Check(!Capture.Available && !Capture.DepthAvailable); Throws<NotSupportedException>(() => Capture.Execute("capture.start", Request())); NoCapture(); });
        Test("Incompatible Screencap does not start job", () =>
        { Types["Screencap.ScreenshotManager"] = typeof(Program); Check(!Capture.Available); Throws<NotSupportedException>(() => Capture.Execute("capture.start", Request())); NoCapture(); });
        Test("Unsupported native alpha mode prevalidated", () =>
        {
            Types["Screencap.ScreenshotManager"] = typeof(Screencap.LegacyScreenshotManager); Types["Screencap.AlphaMode"] = typeof(Screencap.LegacyAlphaMode);
            Check(Capture.Available && !Capture.DepthAvailable && ((JArray)Capture.Capabilities["alphaModes"]).Count == 2);
            Throws<NotSupportedException>(() => Capture.Execute("capture.start", Request("rgAlpha"))); NoCapture();
        });
        Test("Legacy Screencap retains ordinary capture", () =>
        {
            Types["Screencap.ScreenshotManager"] = typeof(Screencap.LegacyScreenshotManager); Types["Screencap.AlphaMode"] = typeof(Screencap.LegacyAlphaMode);
            Check((string)RunCapture(Request())["state"] == "complete" && Native.LastMode == Screencap.AlphaMode.None);
        });
        Test("Legacy paired-depth absence prevalidated", () =>
        {
            Types["Screencap.ScreenshotManager"] = typeof(Screencap.LegacyScreenshotManager); Types["Screencap.AlphaMode"] = typeof(Screencap.LegacyAlphaMode);
            Throws<NotSupportedException>(() => Capture.Execute("capture.start", Request("None", true))); NoCapture();
        });
        foreach (var token in new JToken[] { "Unknown", "1", 1, false, new JArray("None") })
        {
            var value = token;
            Test("Reject alpha token " + value.Type + ":" + value, () =>
            { var request = Request(); request["alphaMode"] = value; Throws<ArgumentException>(() => Capture.Execute("capture.start", request)); NoCapture(); });
        }
        foreach (var mode in new[] { "blackout", "rgAlpha" })
        {
            var alpha = mode;
            Test("Depth rejects alpha=" + alpha + " before hooks", () =>
            { Throws<ArgumentException>(() => Capture.Execute("capture.start", Request(alpha, true))); NoCapture(); });
        }
        Test("Capture rejects stale session", () =>
        { var request = Request(); request["sceneSession"] = "stale"; Throws<InvalidOperationException>(() => Capture.Execute("capture.start", request)); NoCapture(); });
        Test("Capture size limit retained", () =>
        { var request = Request(); request["width"] = 9000; Throws<ArgumentOutOfRangeException>(() => Capture.Execute("capture.start", request)); NoCapture(); });
        foreach (var mode in new[] { "None", "blackout", "rgAlpha" })
        {
            var alpha = mode;
            Test("Forward native mode " + alpha, () =>
            { var result = RunCapture(Request(alpha)); Check((string)result["state"] == "complete" && Native.LastMode.ToString() == alpha && Native.Pre == 1 && Native.Post == 1 && RenderTexture.Released == 1); });
        }
        Test("Default capture is explicitly None", () =>
        { RunCapture(Request()); Check(Native.LastMode == Screencap.AlphaMode.None); });
        Test("Scene change before frame fires no hooks", () =>
        { var job = Capture.Execute("capture.start", Request()); Plugin.Session = "changed"; Plugin.Drain(); var result = Capture.Execute("capture.status", new JObject { ["jobId"] = job["jobId"] }); Check((string)result["state"] == "failed" && Native.Pre == 0 && Native.Post == 0 && !Capture.Busy); });
        Test("Second capture rejected while busy", () =>
        { Capture.Execute("capture.start", Request()); Throws<InvalidOperationException>(() => Capture.Execute("capture.start", Request())); Plugin.Drain(); Check(!Capture.Busy); });
        Test("Coroutine start failure clears busy", () =>
        { Plugin.FailStart = true; Throws<InvalidOperationException>(() => Capture.Execute("capture.start", Request())); Check(!Capture.Busy && Native.Pre == 0); });
        Test("Null coroutine clears busy", () =>
        { Plugin.NullStart = true; Throws<InvalidOperationException>(() => Capture.Execute("capture.start", Request())); NoCapture(); });
        foreach (var failure in new[] { "pre", "render", "null", "read", "encode", "post", "preview" })
        {
            var point = failure;
            Test("Capture failure cleanup: " + point, () =>
            {
                Native.FailPre = point == "pre"; Native.FailRender = point == "render"; Native.NullRender = point == "null"; Native.FailPost = point == "post";
                Texture2D.FailRead = point == "read"; Texture2D.FailEncode = point == "encode"; Graphics.Fail = point == "preview";
                var previous = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGB32); RenderTexture.active = previous;
                var request = Request(); if (point == "preview") request["width"] = 1024;
                var result = RunCapture(request);
                Check((string)result["state"] == "failed" && Native.Pre == 1 && Native.Post == 1 && RenderTexture.active == previous);
                Check(RenderTexture.Released == ((point == "pre" || point == "render" || point == "null") ? 0 : point == "preview" ? 2 : 1));
            });
        }
        Test("Paired-depth API unchanged", () =>
        { var result = RunCapture(Request("None", true)); Check((string)result["state"] == "complete" && File.Exists((string)result["depthPath"]) && Native.Pre == 1 && Native.Post == 1); });
        Test("Failed paired-depth export clears busy", () =>
        { Native.FailDepth = true; var result = RunCapture(Request("None", true)); Check((string)result["state"] == "failed" && Native.Post == 1); });
        Test("Invalid native depth extension rejected", () =>
        { Native.Extension = "../bad"; var result = RunCapture(Request("None", true)); Check((string)result["state"] == "failed" && Directory.GetFiles(Output).Length == 0); });
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            status = "passed", tests = Passed.Count, passed = Passed,
#if KK
            profile = "KK guard + capture contracts",
#else
            profile = "KKS provider and capture contracts",
#endif
            scope = "Linked production MCP adapters with minimal contract doubles; no game, real plugins, rendering or persistence acceptance."
        }));
    }
}
