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
    [BepInPlugin("com.tomtom.studiomcp", "Studio MCP Bridge", "0.2.0")]
    [BepInProcess("CharaStudio.exe")]
#if KK
    [BepInProcess("Koikatu.exe")]
    [BepInProcess("Koikatsu Party.exe")]
#else
    [BepInProcess("KoikatsuSunshine.exe")]
#endif
    [BepInDependency(PluginAccess.PoseEditorId, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginAccess.TimelineId, BepInDependency.DependencyFlags.SoftDependency)]
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
        internal SampleCommands Samples;
        internal VisibilityCommands Visibility;
        // Frame of the last maker reload event, so loads can wait for plugins to settle.
        internal int LastReloadFrame;
        // The title has appeared, so a scene without a TitleScene is a game in progress rather than the startup logos.
        internal bool SeenTitle;
        // Bumped whenever a bridge method or its arguments change shape, so clients can tell which build they talk to.
        internal const int SchemaVersion = 2;
        private static string _build;
        // The main game exposes only the character maker: one character, no Studio scene.
        internal static readonly bool InStudio = Paths.ProcessName == "CharaStudio";
#if KK
        internal const string Game = "KK";
#else
        internal const string Game = "KKS";
#endif
        internal KinematicCommands Kinematics;
        internal PoseEditorCommands PoseEditor;
        internal TimelineCommands Timeline;
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
            Samples = new SampleCommands(this);
            Visibility = new VisibilityCommands(this);
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) => { if (scene.name == "Title") SeenTitle = true; };
            if (InStudio) StudioSaveLoadApi.RegisterExtraBehaviour<BridgeSceneController>("com.tomtom.studiomcp.scene");
            Batches = new BatchCommands(this);
            Kinematics = new KinematicCommands(this);
            PoseEditor = new PoseEditorCommands(this);
            Timeline = new TimelineCommands(this);
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
            LastReloadFrame = Time.frameCount;
            Batches.Clear();
            Visibility.Forget();
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
            CaptureCommands.TrackCamera();
            Visibility.Tick();
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
                        if (!WritesEnabled && !ReadsOnly(method, args)) throw new UnauthorizedAccessException("Writes are disabled in bridge configuration.");
                        if ((string)args["sceneSession"] != Session) throw new InvalidOperationException("Scene session changed; inspect the scene again.");
                        if (Capture.Busy) throw new InvalidOperationException("Capture is in progress; scene writes are temporarily blocked.");
                        if (Samples.Busy) throw new InvalidOperationException("A sample job is running; writes are blocked until it ends (sample_job_cancel stops it).");
                        if (Maker.CharacterLoading) throw new InvalidOperationException("A character card is loading; wait for character_load to finish.");
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
                    || p.Metadata.GUID == PluginAccess.PoseEditorId || p.Metadata.GUID == PluginAccess.TimelineId
                    || p.Metadata.GUID == "com.tomtom.studiomcp").Select(p => new JObject
                { ["id"] = p.Metadata.GUID, ["name"] = p.Metadata.Name, ["version"] = p.Metadata.Version.ToString() })),
                ["materialEditor"] = Materials.Available, ["screencap"] = Capture.Available,
                ["depthCapture"] = Capture.DepthAvailable, ["captureOptions"] = Capture.Capabilities,
                ["game"] = Game, ["bridgeVersion"] = Info.Metadata.Version.ToString(), ["bridgeBuild"] = Build(), ["schemaVersion"] = SchemaVersion,
                ["maker"] = InStudio ? null : MakerState(),
                ["characterControls"] = InStudio, ["poseEditor"] = new JObject { ["available"] = InStudio && PoseEditorCommands.Available },
                ["timeline"] = InStudio ? TimelineCommands.State() : null,
                ["jobs"] = new JObject { ["capture"] = Capture.Busy, ["sample"] = Samples.Busy, ["characterLoad"] = Maker.CharacterLoading },
                ["unsupportedHere"] = InStudio
                    ? new JArray("maker.*", "coordinate/character cards", "pose_list/pose_set", "sample cases with pose")
                    : new JArray("scene.*", "object.*", "light.*", "camera slots", "render.set", "skybox", "tonemap", "native effects/sky", "dof_compat", "add_character", "character.*", "pose.*", "timeline.*")
            };
            if (method.StartsWith("sample.", StringComparison.Ordinal)) return Samples.Execute(method, args);
            if (method.StartsWith("chara.visibility_", StringComparison.Ordinal) || method.StartsWith("chara.preview_", StringComparison.Ordinal))
                return Visibility.Execute(method, args);
            if (method.StartsWith("character.", StringComparison.Ordinal) || method.StartsWith("pose.", StringComparison.Ordinal) || method.StartsWith("timeline.", StringComparison.Ordinal))
            {
                if (!InStudio) throw new NotSupportedException(method + " is Studio-only; it is unavailable in the character maker.");
                if (method.StartsWith("character.", StringComparison.Ordinal)) return Kinematics.Execute(method, args);
                return method.StartsWith("pose.", StringComparison.Ordinal) ? PoseEditor.Execute(method, args) : Timeline.Execute(method, args);
            }
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

        // Process up, maker scene loaded, character loading, ready: tools must not assume the maker is usable just because the game runs.
        private JObject MakerState()
        {
            var character = MakerAPI.InsideAndLoaded ? MakerAPI.GetCharacterControl() : null;
            var state = !MakerAPI.InsideAndLoaded ? "not_in_maker" : character == null || !character.loadEnd || Maker.CharacterLoading ? "character_loading" : "ready";
            var game = Maker.GameState();
            return new JObject { ["state"] = state, ["gameState"] = game, ["scene"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                ["canEnterMaker"] = game == "title" || game == "starting", ["seenTitle"] = SeenTitle,
                ["character"] = character != null ? character.fileParam.fullname : null,
                ["framesSinceReload"] = Time.frameCount - LastReloadFrame };
        }

        // First 12 hex digits of the bridge DLL's MD5, to tell builds of the same version apart.
        private static string Build()
        {
            if (_build != null) return _build;
            try
            {
                using (var md5 = System.Security.Cryptography.MD5.Create())
                    _build = BitConverter.ToString(md5.ComputeHash(File.ReadAllBytes(typeof(Plugin).Assembly.Location))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
            }
            catch (Exception) { _build = "unknown"; }
            return _build;
        }

        // A sample of the present frame (rig_export) changes nothing, so it also works on a read-only install.
        private static bool ReadsOnly(string method, JObject args)
        {
            return method == "sample.start" && (string)args["mode"] == "current" && args["cases"] == null && args["resume"] == null;
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
                || method == "object.set_visible" || method == "maker.clothes_state" || method == "maker.coordinate_save" || method == "maker.coordinate_load" || method == "maker.character_load" || method == "sample.start"
                || method == "chara.visibility_set" || method == "chara.visibility_restore" || method == "chara.preview_set" || method == "chara.preview_restore" || method == "chara.shape_set" || method == "chara.accessory_set" || method == "chara.bones_set"
                || method == "chara.expression_set" || method == "chara.dynamic_bone_set" || method == "maker.pose_set" || method == "analysis.clipping_sweep" || method == "mask.apply" || method == "mask.revert" || method == "tonemap.set" || method == "dof_compat.set"
                || method == "character.kinematics" || method == "character.control_set"
                || method == "pose.bone_set" || method == "pose.bone_reset" || method == "pose.blendshape_set" || method == "pose.blendshape_reset"
                || method == "timeline.control" || method == "timeline.settings" || method == "timeline.track_create"
                || method == "timeline.track_set" || method == "timeline.track_remove" || method == "timeline.keyframe_set" || method == "timeline.keyframe_remove"
                || method.StartsWith("batch.", StringComparison.Ordinal);
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
