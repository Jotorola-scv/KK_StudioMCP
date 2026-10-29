using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using KKAPI.Maker;
using KKAPI.Studio.SaveLoad;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace StudioMcp.Bridge
{
    [BepInPlugin("com.tomtom.studiomcp", "Studio MCP Bridge", "0.1.0")]
    [BepInProcess("CharaStudio.exe")]
#if KK
    [BepInProcess("Koikatu.exe")]
    [BepInProcess("Koikatsu Party.exe")]
#else
    [BepInProcess("KoikatsuSunshine.exe")]
#endif
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance;
        private readonly Queue<Work> _pending = new Queue<Work>();
        private readonly Dictionary<string, string> _completed = new Dictionary<string, string>();
        private readonly Queue<string> _completionOrder = new Queue<string>();
        private readonly HashSet<string> _inflight = new HashSet<string>();
        private WebSocketServer _server;
        private ConfigEntry<bool> _allowWrites;
        private string _token;
        private bool _originalBackground;
        internal SceneCommands Scene;
        internal MaterialCommands Materials;
        internal CaptureCommands Capture;
        internal RenderCommands Rendering;
        internal ToneCommands Tone;
        internal BatchCommands Batches;
        internal MakerCommands Maker;
        internal CharacterCommands Characters;
        internal AccessoryCommands Accessories;
        internal MotionCommands Motion;
        internal BoneCommands Bones;
        internal AnalysisCommands Analysis;
        internal MaskCommands Masks;
        // The main game exposes only the character maker: one character, no Studio scene.
        internal static readonly bool InStudio = Paths.ProcessName == "CharaStudio";
        internal string Session = Guid.NewGuid().ToString("N");
        internal string OutputRoot;
        internal bool WritesEnabled => _allowWrites.Value
            || Environment.GetEnvironmentVariable("STUDIOMCP_ALLOW_WRITES") == "1";

        private sealed class Work
        {
            internal JObject Request;
            internal Action<string> Reply;
            internal DateTime Expires;
        }

        private void Awake()
        {
            Instance = this;
#if KK
            const int defaultPort = 8847;
#else
            const int defaultPort = 8846;
#endif
            // Studio and the main game can run together, so the maker bridge needs its own port.
            var port = InStudio ? Config.Bind("Server", "Port", defaultPort).Value
                : Config.Bind("Maker", "Port", defaultPort + 2, "Loopback port used by the main game's character maker.").Value;
            _token = Config.Bind("Server", "Token", Guid.NewGuid().ToString("N")).Value;
            _allowWrites = Config.Bind("Server", "AllowWrites", false);
            OutputRoot = Path.Combine(Path.Combine(Paths.GameRootPath, "UserData"), "StudioMCP");
            Directory.CreateDirectory(OutputRoot);
            Scene = new SceneCommands(this);
            Materials = new MaterialCommands(this);
            Capture = new CaptureCommands(this);
            Rendering = new RenderCommands();
            Tone = new ToneCommands();
            Maker = new MakerCommands(this);
            Characters = new CharacterCommands(this);
            Accessories = new AccessoryCommands(this);
            Motion = new MotionCommands(this);
            Bones = new BoneCommands(this);
            Analysis = new AnalysisCommands(this);
            Masks = new MaskCommands(this);
            if (InStudio) StudioSaveLoadApi.RegisterExtraBehaviour<BridgeSceneController>("com.tomtom.studiomcp.scene");
            Batches = new BatchCommands(this);
            _originalBackground = Application.runInBackground;
            Application.runInBackground = true;
            if (InStudio) StudioSaveLoadApi.SceneLoad += SceneLoaded;
            else
            {
                // Renderer handles die when the maker reloads its character, like a Studio scene load.
                MakerAPI.MakerBaseLoaded += MakerChanged;
                MakerAPI.ReloadCustomInterface += MakerChanged;
                MakerAPI.MakerExiting += MakerChanged;
            }
            _server = new WebSocketServer("ws://127.0.0.1:" + port);
            _server.AddWebSocketService<BridgeSocket>("/bridge");
            _server.Start();
            File.WriteAllText(Path.Combine(OutputRoot, InStudio ? "connection.json" : "maker-connection.json"), new JObject
            {
                ["url"] = "ws://127.0.0.1:" + port + "/bridge", ["token"] = _token,
                ["processId"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                ["game"] = defaultPort == 8846 ? "KKS" : "KK", ["mode"] = InStudio ? "studio" : "maker"
            }.ToString());
            Logger.LogInfo("Bridge listening on loopback port " + port + "; writes=" + WritesEnabled);
        }

        private void SceneLoaded(object sender, SceneLoadEventArgs args)
        {
            Session = Guid.NewGuid().ToString("N");
            Batches.Clear();
        }

        private void MakerChanged(object sender, EventArgs args) => SceneLoaded(sender, null);

        internal void Accept(string message, Action<string> reply)
        {
            string id = null;
            try
            {
                if (message.Length > 256 * 1024) throw new ArgumentException("Request is too large.");
                var request = JObject.Parse(message);
                id = (string)request["id"];
                if (string.IsNullOrEmpty(id) || id.Length > 80) throw new ArgumentException("A request id is required.");
                if (!string.Equals((string)request["token"], _token, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("Invalid bridge token.");
                lock (_pending)
                {
                    string cached;
                    if (_completed.TryGetValue(id, out cached)) { reply(cached); return; }
                    if (_inflight.Contains(id)) throw new InvalidOperationException("Request is already in progress.");
                    if (_pending.Count >= 32) throw new InvalidOperationException("Bridge queue is full.");
                    _inflight.Add(id);
                    _pending.Enqueue(new Work { Request = request, Reply = reply, Expires = DateTime.UtcNow.AddSeconds(30) });
                }
            }
            catch (Exception error) { reply(Error(id, error).ToString(Formatting.None)); }
        }

        private void Update()
        {
            var budget = System.Diagnostics.Stopwatch.StartNew();
            for (var count = 0; count < 8 && budget.ElapsedMilliseconds < 4; count++)
            {
                Work work;
                lock (_pending) { if (_pending.Count == 0) break; work = _pending.Dequeue(); }
                var id = (string)work.Request["id"];
                JObject response;
                try
                {
                    if (DateTime.UtcNow > work.Expires) throw new TimeoutException("Request expired before execution.");
                    var method = (string)work.Request["method"];
                    var args = work.Request["args"] as JObject ?? new JObject();
                    if (IsWrite(method))
                    {
                        if (!WritesEnabled) throw new UnauthorizedAccessException("Writes are disabled in bridge configuration.");
                        if ((string)args["sceneSession"] != Session) throw new InvalidOperationException("Scene session changed; inspect the scene again.");
                        if (Capture.Busy) throw new InvalidOperationException("Capture is in progress; scene writes are temporarily blocked.");
                        if (!method.StartsWith("batch.", StringComparison.Ordinal)) Batches.Clear();
                    }
                    var result = Dispatch(method, args);
                    response = new JObject { ["id"] = id, ["ok"] = true, ["result"] = result };
                }
                catch (Exception error)
                {
                    var invoked = error as System.Reflection.TargetInvocationException;
                    response = Error(id, invoked?.InnerException ?? error);
                }
                var json = response.ToString(Formatting.None);
                lock (_pending)
                {
                    _inflight.Remove(id);
                    // Large read-only image responses are retrievable by job id; don't duplicate them in the RPC cache.
                    if (json.Length < 65536)
                    {
                        _completed[id] = json;
                        _completionOrder.Enqueue(id);
                    }
                    while (_completionOrder.Count > 128) _completed.Remove(_completionOrder.Dequeue());
                }
                try { work.Reply(json); } catch (Exception error) { Logger.LogWarning(error.Message); }
            }
        }

        internal JToken Dispatch(string method, JObject args)
        {
            if (method == "status") return new JObject
            {
                ["sceneSession"] = Session, ["mode"] = InStudio ? "studio" : "maker",
                ["ready"] = InStudio ? Studio.Studio.Instance != null : MakerAPI.InsideAndLoaded,
                ["writesEnabled"] = WritesEnabled, ["captureBusy"] = Capture.Busy,
                ["plugins"] = new JArray(Chainloader.PluginInfos.Values.Where(p =>
                    p.Metadata.Name.IndexOf("Material", StringComparison.OrdinalIgnoreCase) >= 0
                    || p.Metadata.Name.IndexOf("Screencap", StringComparison.OrdinalIgnoreCase) >= 0
                    || p.Metadata.GUID == "com.tomtom.studiomcp").Select(p => new JObject
                { ["id"] = p.Metadata.GUID, ["name"] = p.Metadata.Name, ["version"] = p.Metadata.Version.ToString() })),
                ["materialEditor"] = Materials.Available, ["screencap"] = Capture.Available,
                ["depthCapture"] = Capture.DepthAvailable, ["captureOptions"] = Capture.Capabilities
            };
            if (method.StartsWith("me.", StringComparison.Ordinal)) return Materials.Execute(method, args);
            if (method.StartsWith("capture.", StringComparison.Ordinal)) return Capture.Execute(method, args);
            if (method.StartsWith("batch.", StringComparison.Ordinal)) return Batches.Execute(method, args);
            if (method == "render.get") return Rendering.State();
            if (method == "render.diagnostics") return Rendering.Diagnostics();
            if (method.StartsWith("chara.accessory_", StringComparison.Ordinal)) return Accessories.Execute(method, args);
            if (method.StartsWith("chara.bones_", StringComparison.Ordinal)) return Bones.Execute(method, args);
            if (method.StartsWith("chara.expression_", StringComparison.Ordinal) || method.StartsWith("chara.dynamic_bone", StringComparison.Ordinal)
                || method.StartsWith("maker.pose_", StringComparison.Ordinal)) return Motion.Execute(method, args);
            if (method == "analysis.hair_mask" || method.StartsWith("mask.", StringComparison.Ordinal)) return Masks.Execute(method, args);
            if (method.StartsWith("analysis.", StringComparison.Ordinal)) return Analysis.Execute(method, args);
            if (method.StartsWith("chara.", StringComparison.Ordinal) || method == "mesh.export") return Characters.Execute(method, args);
            if (!InStudio || method.StartsWith("maker.", StringComparison.Ordinal)) return Maker.Execute(method, args);
            if (method == "render.set") return Rendering.Apply((JObject)args["values"]);
            if (method == "skybox.bind") return Rendering.BindSky(args);
            if (method == "tonemap.get") return Tone.State();
            if (method == "tonemap.set") return Tone.Apply(args["values"] as JObject);
            if (method == "native_effects.get") return NativeEffectsCommands.State();
            if (method == "native_effects.set") return NativeEffectsCommands.Apply((JObject)args["values"]);
            if (method == "dof_compat.get") return DofCompatibilityCommands.State();
            if (method == "dof_compat.set") return DofCompatibilityCommands.Apply(args);
            if (method == "dof_compat.snapshot") return DofCompatibilityCommands.Snapshot(OutputRoot, Capture.Busy);
            if (method == "native_sky.get") return NativeEffectsCommands.SkyState();
            if (method == "native_sky.set") return NativeEffectsCommands.SkyApply(args);
            return Scene.Execute(method, args);
        }

        internal static bool IsWrite(string method)
        {
            return method == "scene.add_item" || method == "scene.add_character" || method == "scene.checkpoint"
                || method == "scene.restore" || method == "object.set_transform" || method == "camera.set"
                || method == "camera.slot_save" || method == "camera.slot_load"
                || method == "scene.add_light" || method == "light.set" || method == "me.shader"
                || method == "me.set" || method == "me.reset" || method == "me.import_texture" || method == "me.texture_transform"
                || method == "me.render_queue"
                || method == "render.set" || method == "skybox.bind" || method == "native_effects.set" || method == "native_sky.set"
                || method == "object.set_visible" || method == "maker.clothes_state" || method == "chara.shape_set" || method == "chara.accessory_set" || method == "chara.bones_set"
                || method == "chara.expression_set" || method == "chara.dynamic_bone_set" || method == "maker.pose_set" || method == "analysis.clipping_sweep" || method == "mask.apply" || method == "mask.revert" || method == "tonemap.set" || method == "dof_compat.set" || method.StartsWith("batch.", StringComparison.Ordinal);
        }

        private static JObject Error(string id, Exception error) => new JObject
        { ["id"] = id, ["ok"] = false, ["error"] = new JObject
          { ["type"] = error.GetType().Name, ["message"] = error.Message } };

        private void OnDestroy()
        {
            if (InStudio) StudioSaveLoadApi.SceneLoad -= SceneLoaded;
            else
            {
                MakerAPI.MakerBaseLoaded -= MakerChanged;
                MakerAPI.ReloadCustomInterface -= MakerChanged;
                MakerAPI.MakerExiting -= MakerChanged;
            }
            _server?.Stop();
            if (!_originalBackground) Application.runInBackground = false;
            Instance = null;
        }
    }

    internal sealed class BridgeSocket : WebSocketBehavior
    {
        protected override void OnMessage(MessageEventArgs args)
        {
            if (!args.IsText) return;
            Plugin.Instance.Accept(args.Data, response => SendAsync(response, null));
        }
    }
}
