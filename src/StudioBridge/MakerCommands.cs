// Added 2026-10-29 in the maker-support fork of KK_StudioMCP (AGPL-3.0); see README.md.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ChaCustom;
using KKAPI.Maker;
using MessagePack;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudioMcp.Bridge
{
    // Main-game character maker: one ChaControl, the maker orbit camera, and runtime clothing states.
    internal sealed class MakerCommands
    {
        private static readonly string[] ClothesKinds = { "top", "bottom", "bra", "shorts", "gloves", "pantyhose", "socks", "shoes_inner", "shoes_outer" };
        private readonly Plugin _plugin;
        private readonly Dictionary<string, JObject> _saves = new Dictionary<string, JObject>();
        private bool _saving;
        private readonly Dictionary<string, JObject> _loads = new Dictionary<string, JObject>();
        internal bool CharacterLoading;
        private readonly Dictionary<string, JObject> _enters = new Dictionary<string, JObject>();
        private JObject _entering;
        internal MakerCommands(Plugin plugin) { _plugin = plugin; }

        internal static ChaControl Character()
        {
            if (!MakerAPI.InsideAndLoaded) throw new InvalidOperationException("Character maker is not open; maker_enter opens it from the title screen.");
            var character = MakerAPI.GetCharacterControl();
            if (character == null || !character.loadEnd) throw new InvalidOperationException("Maker character is still loading.");
            return character;
        }

        internal JToken Execute(string method, JObject args)
        {
            switch (method)
            {
                case "maker.enter": return StartEnter(args);
                case "maker.enter_status":
                    JObject enter;
                    if (!_enters.TryGetValue((string)args["jobId"] ?? "", out enter)) throw new ArgumentException("Unknown maker enter job.");
                    return enter.DeepClone();
                case "maker.snapshot": return Snapshot();
                case "maker.clothes_state":
                    var character = Character();
                    var kind = ClothesKind(args["kind"]);
                    if (args["state"] == null || args["state"].Type != JTokenType.Integer || (int)args["state"] < 0 || (int)args["state"] > 3)
                        throw new ArgumentException("state requires 0 (on), 1 (half), 2 (half 2) or 3 (off).");
                    character.SetClothesState(kind, (byte)(int)args["state"], false);
                    return Clothes(character);
                case "maker.coordinate_save": return StartCoordinateSave(args);
                case "maker.coordinate_list": return ListCoordinates(args);
                case "maker.character_list": return ListCharacters(args);
                case "maker.character_load": return StartCharacterLoad(args);
                case "maker.character_load_status":
                    JObject load;
                    if (!_loads.TryGetValue((string)args["jobId"] ?? "", out load)) throw new ArgumentException("Unknown character load job.");
                    return load.DeepClone();
                case "maker.coordinate_load": return LoadCoordinate(args);
                case "maker.coordinate_status":
                    JObject save;
                    if (!_saves.TryGetValue((string)args["jobId"] ?? "", out save)) throw new ArgumentException("Unknown coordinate save job.");
                    return save.DeepClone();
                case "camera.get": return CameraState();
                case "camera.set":
                    ValidateCamera(args);
                    var camera = CameraControl();
                    if (args["pivot"] != null) camera.TargetPos = SceneCommands.Vector(args["pivot"]);
                    if (args["rotation"] != null) camera.CameraAngle = SceneCommands.Vector(args["rotation"]);
                    if (args["distance"] != null) camera.CameraDir = SceneCommands.Vector(args["distance"]);
                    if (args["fov"] != null) camera.CameraFov = SceneCommands.Number(args["fov"]);
                    CaptureCommands.LastCameraMove = Time.unscaledTime;
                    return CameraState();
                default: throw new NotSupportedException(method + " is Studio-only; it is unavailable in the character maker.");
            }
        }

        internal Action PrepareUndo(string method, JObject args)
        {
            if (method != "camera.set") throw new ArgumentException("Maker batches support me.set and camera.set only.");
            ValidateCamera(args);
            var before = CameraState();
            return () => Execute(method, before);
        }

        // Where the main game is: starting (before the title), title, maker_loading, maker, or other (a playthrough, free H, ...).
        internal string GameState()
        {
            if (MakerAPI.InsideMaker) return MakerAPI.InsideAndLoaded ? "maker" : "maker_loading";
            if (UnityEngine.Object.FindObjectOfType<TitleScene>() != null) return "title";
            return _plugin.SeenTitle ? "other" : "starting";
        }

        // Opens the maker the way the title menu does. Waits for the title if the game is still starting, and refuses to
        // leave any other scene, because that would throw away a playthrough in progress.
        private JObject StartEnter(JObject args)
        {
            if (Plugin.InStudio) throw new NotSupportedException("Studio has no character maker; start the main game.");
            var sex = (string)args["sex"] ?? "female";
            if (sex != "female" && sex != "male") throw new ArgumentException("sex must be female or male.");
            if (_entering != null) return (JObject)_entering.DeepClone();
            var state = GameState();
            if (state == "other") throw new InvalidOperationException("The game is in scene " + SceneName() + ", not the title screen. Return to the title first; maker_enter will not discard a game in progress.");
            var timeout = Mathf.Clamp(args["timeoutSeconds"] == null || args["timeoutSeconds"].Type == JTokenType.Null ? 180f : SceneCommands.Number(args["timeoutSeconds"]), 5f, 600f);
            var id = Guid.NewGuid().ToString("N");
            var record = new JObject { ["jobId"] = id, ["state"] = "waiting", ["sex"] = sex, ["startedFrom"] = state };
            _enters[id] = record;
            _entering = record;
            _plugin.StartCoroutine(EnterMaker(record, sex, timeout));
            return (JObject)record.DeepClone();
        }

        private static string SceneName() { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; }

        private sealed class EnterState
        {
            internal float Start;
            internal bool Clicked;
            internal int ReadyFrames;
        }

        private IEnumerator EnterMaker(JObject record, string sex, float timeout)
        {
            var state = new EnterState { Start = Time.unscaledTime };
            while (true)
            {
                bool done;
                try { done = EnterStep(record, sex, timeout, state); }
                catch (Exception error)
                {
                    record["state"] = "failed";
                    record["error"] = error.Message;
                    done = true;
                }
                if (done) break;
                yield return null;
            }
            _entering = null;
        }

        private bool EnterStep(JObject record, string sex, float timeout, EnterState progress)
        {
            var state = GameState();
            record["gameState"] = state;
            record["scene"] = SceneName();
            if (state == "maker")
            {
                var character = MakerAPI.GetCharacterControl();
                if (character != null && character.loadEnd && Time.frameCount - _plugin.LastReloadFrame >= 30)
                {
                    record["state"] = "ready";
                    record["sceneSession"] = _plugin.Session;
                    record["alreadyInMaker"] = !progress.Clicked;
                    record["character"] = character.fileParam.fullname;
                    record["makerSex"] = character.sex == 0 ? "male" : "female";
                    record["seconds"] = Math.Round(Time.unscaledTime - progress.Start, 2);
                    return true;
                }
            }
            else if (state == "other" && !progress.Clicked)
            {
                record["state"] = "failed";
                record["error"] = "The game moved to scene " + SceneName() + " instead of the title screen.";
                return true;
            }
            else if (state == "title" && !progress.Clicked)
            {
                // The menu appears after the title's fade; give it a moment once its buttons accept input.
                var title = UnityEngine.Object.FindObjectOfType<TitleScene>();
                progress.ReadyFrames = TitleReady(title) ? progress.ReadyFrames + 1 : 0;
                if (progress.ReadyFrames >= 10)
                {
                    if (sex == "male") title.OnCustomMale();
                    else title.OnCustomFemale();
                    progress.Clicked = true;
                    record["state"] = "loading";
                }
            }
            if (Time.unscaledTime - progress.Start <= timeout) return false;
            record["state"] = "failed";
            record["error"] = "The maker was not ready within " + timeout + " s; the game is in state " + state + ", scene " + SceneName() + ".";
            return true;
        }

        // Same conditions as a real click: the raycaster is on (no dialog or transition) and a menu button is interactable.
        private static bool TitleReady(TitleScene title)
        {
            if (title == null) return false;
            var raycaster = typeof(TitleScene).GetField("graphicRaycaster", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(title) as Behaviour;
            if (raycaster == null || !raycaster.enabled) return false;
            return title.GetComponentsInChildren<UnityEngine.UI.Button>().Any(b => b.IsInteractable());
        }

        // Saves the current outfit as a coordinate card like the maker's save button: the game's mannequin thumbnail, then
        // ChaFileCoordinate.SaveFile, which ExtendedSave/KKAPI hook so plugin data (Material Editor, ...) is written too.
        private JObject StartCoordinateSave(JObject args)
        {
            var character = Character();
            if (_saving) throw new InvalidOperationException("Another coordinate save is in progress.");
            var file = Resources.FindObjectsOfTypeAll<CustomCoordinateFile>().FirstOrDefault(x => x.gameObject.scene.IsValid());
            if (file == null) throw new InvalidOperationException("The maker's coordinate save panel is unavailable.");
            var coordinate = character.chaFile.coordinate[character.fileStatus.coordinateType];
            var name = (string)args["name"];
            if (string.IsNullOrEmpty(name)) name = string.IsNullOrEmpty(coordinate.coordinateName) ? "MCP" : coordinate.coordinateName;
            var fileName = (string)args["fileName"];
            if (string.IsNullOrEmpty(fileName))
                fileName = "KKCoorde" + (character.sex == 0 ? "M" : "F") + "_" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
            if (fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) fileName = fileName.Substring(0, fileName.Length - 4);
            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.Trim('.', ' ').Length == 0)
                throw new ArgumentException("fileName must be a plain file name without folders.");
            var path = Path.Combine(Path.GetFullPath(UserData.Path + "coordinate/"), fileName + ".png");
            var exists = File.Exists(path);
            if (exists && !((bool?)args["overwrite"] ?? false)) throw new IOException(path + " already exists; pass overwrite=true to replace it.");
            var id = Guid.NewGuid().ToString("N");
            var record = new JObject { ["jobId"] = id, ["state"] = "waiting", ["path"] = path, ["coordinateName"] = name,
                ["coordinateType"] = character.fileStatus.coordinateType, ["overwritten"] = exists };
            _saves[id] = record;
            _saving = true;
            // Not a diagnostic look in the card's thumbnail.
            _plugin.Visibility.RestoreAllPreviews();
            _plugin.Visibility.Suspend();
            _plugin.StartCoroutine(SaveCoordinate(record, file, character, coordinate, name, path));
            return (JObject)record.DeepClone();
        }

        private IEnumerator SaveCoordinate(JObject record, CustomCoordinateFile file, ChaControl character, ChaFileCoordinate coordinate, string name, string path)
        {
            try { file.CreateCoordinateFileBefore(); }
            catch (Exception error) { Fail(record, error, file, character); yield break; }
            yield return new WaitForEndOfFrame();
            IEnumerator reapply = null;
            try
            {
                var png = file.CreateCoordinatePng();
                coordinate.pngData = png;
                coordinate.coordinateName = name;
                coordinate.SaveFile(path);
                record["bytes"] = new FileInfo(path).Length;
                if (png != null) record["thumbnailBase64"] = Convert.ToBase64String(png);
                record["extendedData"] = ExtendedDataKeys(path);
                record["listRefreshed"] = AddToList(file, path, name, (bool)record["overwritten"]);
                reapply = ReapplyMaterialEditor(character);
            }
            catch (Exception error) { Fail(record, error, file, character); yield break; }
            _plugin.StartCoroutine(file.CreateCoordinateFileAfterCoroutine());
            if (reapply != null) yield return _plugin.StartCoroutine(reapply);
            // The mannequin rebuilt the head and body renderers, so ids from before the save are stale.
            _plugin.Session = Guid.NewGuid().ToString("N");
            _plugin.Batches.Clear();
            _plugin.Visibility.Forget();
            record["sceneSession"] = _plugin.Session;
            record["state"] = "complete";
            _saving = false;
            _plugin.Visibility.Resume();
        }

        // The game's thumbnail swaps the character for its mannequin and back, which rebuilds the head without Material Editor's
        // edits: the face changes until the next reload. ME keeps them in memory (and in the card), so ask it to apply them again.
        private static IEnumerator ReapplyMaterialEditor(ChaControl character)
        {
            var type = MaterialCommands.FindType("KK_Plugins.MaterialEditor.MaterialEditorCharaController");
            var controller = type == null ? null : character.GetComponent(type);
            if (controller == null) return null;
            var load = type.GetMethods().Where(m => m.Name == "LoadData" && m.GetParameters().All(x => x.ParameterType == typeof(bool)))
                .OrderByDescending(m => m.GetParameters().Length).FirstOrDefault();
            if (load == null) return null;
            return load.Invoke(controller, load.GetParameters().Select(x => (object)true).ToArray()) as IEnumerator;
        }

        private void Fail(JObject record, Exception error, CustomCoordinateFile file, ChaControl character)
        {
            _saving = false;
            _plugin.Visibility.Resume();
            record["state"] = "failed";
            record["error"] = (error is TargetInvocationException && error.InnerException != null ? error.InnerException : error).Message;
            try { character.ChangeSettingMannequin(false); } catch { }
            try { _plugin.StartCoroutine(file.CreateCoordinateFileAfterCoroutine()); } catch { }
        }

        // Reads a card to list which plugins stored data in it.
        private static JArray ExtendedDataKeys(string path)
        {
            var loaded = new ChaFileCoordinate();
            if (!loaded.LoadFile(path)) throw new IOException("The coordinate card could not be read: " + path);
            return ExtendedDataKeys(loaded);
        }

        private static JArray ExtendedDataKeys(ChaFileCoordinate coordinate)
        {
            var data = ExtensibleSaveFormat.ExtendedSave.GetAllExtendedData(coordinate);
            return new JArray(data == null ? new string[0] : data.Keys.OrderBy(x => x).ToArray());
        }

        private static string CoordinateFolder() { return Path.GetFullPath(UserData.Path + "coordinate/"); }
        private static string CharacterFolder(int sex) { return Path.GetFullPath(UserData.Path + (sex == 0 ? "chara/male/" : "chara/female/")); }

        // The outfit name from the card's header; parsing whole cards is far too slow for folders of many GB.
        private static string CoordinateName(string path)
        {
            try
            {
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    PngFile.SkipPng(reader);
                    if (reader.BaseStream.Position >= reader.BaseStream.Length) return null;
                    reader.ReadInt32();
                    if (reader.ReadString().IndexOf("KoiKatuClothes", StringComparison.Ordinal) < 0) return null;
                    reader.ReadString();
                    return reader.ReadString();
                }
            }
            catch (Exception) { return null; }
        }

        private static string CharacterName(string path, int sex)
        {
            try
            {
                var card = new ChaFileControl();
                return card.LoadCharaFile(path, (byte)sex, true, true) ? card.parameter.fullname : null;
            }
            catch (Exception) { return null; }
        }

        private static JObject ListCoordinates(JObject args)
        {
            return ListCards(CoordinateFolder(), args, CoordinateName, "coordinateName");
        }

        private static JObject ListCharacters(JObject args)
        {
            var sex = args["sex"] != null && args["sex"].Type != JTokenType.Null ? (int)args["sex"] : Character().sex;
            var result = ListCards(CharacterFolder(sex), args, path => CharacterName(path, sex), "name");
            result["sex"] = sex;
            return result;
        }

        // Cards in a folder and its subfolders, newest first; query matches the file name, or the name inside the card.
        // This runs on the game's main thread, so reading names stops after a few seconds and the result says so.
        private static JObject ListCards(string folder, JObject args, Func<string, string> nameOf, string nameKey)
        {
            var query = (string)args["query"];
            var offset = Math.Max(0, (int?)args["offset"] ?? 0);
            var limit = Mathf.Clamp((int?)args["limit"] ?? 50, 1, 200);
            var files = Directory.Exists(folder)
                ? new DirectoryInfo(folder).GetFiles("*.png", SearchOption.AllDirectories).OrderByDescending(x => x.LastWriteTime).ToList()
                : new List<FileInfo>();
            var budget = System.Diagnostics.Stopwatch.StartNew();
            var cards = new JArray();
            var total = 0;
            var unread = 0;
            foreach (var file in files)
            {
                var filtered = !string.IsNullOrEmpty(query);
                var named = filtered && file.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                var inPage = total >= offset && total < offset + limit;
                string name = null;
                var read = (inPage || (filtered && !named)) && budget.ElapsedMilliseconds < 8000;
                if (read)
                {
                    name = nameOf(file.FullName);
                    if (name == null) continue;     // not a card of this kind
                    if (filtered && !named && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                else if (filtered && !named) { unread++; continue; }
                if (total >= offset && total < offset + limit)
                {
                    var entry = new JObject { ["path"] = file.FullName, ["file"] = file.FullName.Substring(folder.Length),
                        ["bytes"] = file.Length, ["modified"] = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") };
                    entry[nameKey] = name;
                    cards.Add(entry);
                }
                total++;
            }
            var result = new JObject { ["folder"] = folder, ["filesInFolder"] = files.Count, ["total"] = total, ["offset"] = offset, ["cards"] = cards };
            if (unread > 0)
            {
                result["searchComplete"] = false;
                result["note"] = unread + " cards were matched by file name only, because reading their names took too long; narrow the query to a file name.";
            }
            return result;
        }

        // Loads a character card into the maker like its load button, with the same part toggles and KKAPI plugin toggles
        // (Material Editor, overlays, ABMX, ...). Never saves anything. Completes when the character and plugins settle.
        private JObject StartCharacterLoad(JObject args)
        {
            var character = Character();
            if (CharacterLoading) throw new InvalidOperationException("A character load is already in progress.");
            var path = (string)args["path"];
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path requires a character card (see character_list).");
            if (!Path.IsPathRooted(path)) path = Path.Combine(CharacterFolder(character.sex), path);
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(path + ".png")) path += ".png";
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("Character card not found: " + path);
            var probe = new ChaFileControl();
            if (!probe.LoadCharaFile(path, character.sex, true, true))
                throw new IOException("Not a " + (character.sex == 0 ? "male" : "female") + " character card for this game: " + path);
            if (probe.parameter.sex != character.sex)
                throw new ArgumentException("This is a " + (probe.parameter.sex == 0 ? "male" : "female") + " card but the maker has a " + (character.sex == 0 ? "male" : "female") + " character.");
            var flags = new JObject();
            foreach (var key in new[] { "face", "body", "hair", "parameters", "coordinates" }) flags[key] = (bool?)args[key] ?? true;
            if (!flags.Properties().Any(p => (bool)p.Value)) throw new ArgumentException("Nothing to load: every part is false.");
            var pluginToggles = args["pluginToggles"] as JObject;
            if (args["pluginToggles"] != null && args["pluginToggles"].Type != JTokenType.Null && pluginToggles == null)
                throw new ArgumentException("pluginToggles must be an object such as {\"Material Editor\": true}.");

            // Plugins read the maker's load toggles (KKAPI GetCharacterLoadFlags and MakerLoadToggle) while they load.
            var changed = new List<KeyValuePair<object, bool>>();
            var file = Resources.FindObjectsOfTypeAll<CustomCharaFile>().FirstOrDefault(x => x.gameObject.scene.IsValid());
            var window = file == null ? null : typeof(CustomCharaFile).GetField("fileWindow", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(file);
            if (window != null)
            {
                SetToggle(window, "tglChaLoadFace", (bool)flags["face"], changed);
                SetToggle(window, "tglChaLoadBody", (bool)flags["body"], changed);
                SetToggle(window, "tglChaLoadHair", (bool)flags["hair"], changed);
                SetToggle(window, "tglChaLoadParam", (bool)flags["parameters"], changed);
                SetToggle(window, "tglChaLoadCoorde", (bool)flags["coordinates"], changed);
            }
            var plugins = PluginToggles(pluginToggles, changed);
            var extended = ExtensibleSaveFormat.ExtendedSave.GetAllExtendedData(probe);
            var id = Guid.NewGuid().ToString("N");
            var record = new JObject { ["jobId"] = id, ["state"] = "loading", ["path"] = path, ["name"] = probe.parameter.fullname,
                ["loaded"] = flags, ["pluginToggles"] = plugins,
                ["extendedData"] = new JArray(extended == null ? new string[0] : extended.Keys.OrderBy(k => k).ToArray()) };
            try
            {
                var face = (bool)flags["face"];
                var body = (bool)flags["body"];
                var hair = (bool)flags["hair"];
                var coordinates = (bool)flags["coordinates"];
                // The game's LoadFileLimited always returns false, even on success; the probe above checked the card.
#if KK
                character.chaFile.LoadFileLimited(path, character.sex, face, body, hair, (bool)flags["parameters"], coordinates);
#else
                // The maker itself passes extension=false here.
                character.chaFile.LoadFileLimited(path, character.sex, face, body, hair, (bool)flags["parameters"], coordinates, false);
#endif
                character.ChangeCoordinateType();
                character.Reload(!coordinates, !face && !coordinates, !hair, !body);
                Singleton<CustomBase>.Instance.updateCustomUI = true;
            }
            catch
            {
                RestoreToggles(changed);
                throw;
            }
            _loads[id] = record;
            CharacterLoading = true;
            _plugin.StartCoroutine(WaitCharacter(record, changed, (float?)args["timeoutSeconds"] ?? 60f));
            return (JObject)record.DeepClone();
        }

        // Ready = the character finished loading and no maker reload event fired for 30 frames.
        private IEnumerator WaitCharacter(JObject record, List<KeyValuePair<object, bool>> changed, float timeout)
        {
            var start = Time.unscaledTime;
            var frames = 0;
            while (true)
            {
                yield return null;
                frames++;
                bool done;
                try { done = CharacterStep(record, timeout, start, frames); }
                catch (Exception error)
                {
                    record["state"] = "failed";
                    record["error"] = error.Message;
                    done = true;
                }
                if (done) break;
            }
            RestoreToggles(changed);
            CharacterLoading = false;
        }

        private bool CharacterStep(JObject record, float timeout, float start, int frames)
        {
            var character = MakerAPI.InsideAndLoaded ? MakerAPI.GetCharacterControl() : null;
            if (character != null && character.loadEnd && Time.frameCount - _plugin.LastReloadFrame >= 30 && frames >= 30)
            {
                record["state"] = "ready";
                record["sceneSession"] = _plugin.Session;
                record["characterName"] = character.fileParam.fullname;
                record["frames"] = frames;
                record["seconds"] = Math.Round(Time.unscaledTime - start, 2);
                record["clothes"] = Clothes(character);
                return true;
            }
            if (Time.unscaledTime - start <= timeout && MakerAPI.InsideAndLoaded) return false;
            record["state"] = "failed";
            record["error"] = MakerAPI.InsideAndLoaded ? "The character did not settle within " + timeout + " s." : "The maker closed during the load.";
            return true;
        }

        private static JArray PluginToggles(JObject wanted, List<KeyValuePair<object, bool>> changed)
        {
            var result = new JArray();
            var type = MaterialCommands.FindType("KKAPI.Maker.UI.MakerLoadToggle");
            var field = type == null ? null : type.GetField("Toggles", BindingFlags.NonPublic | BindingFlags.Static);
            var list = field == null ? null : field.GetValue(null) as IEnumerable;
            if (list == null)
            {
                if (wanted != null && wanted.HasValues) throw new NotSupportedException("KKAPI load toggles are unavailable.");
                return result;
            }
            var used = new HashSet<string>();
            foreach (var toggle in list)
            {
                var text = (string)toggle.GetType().GetProperty("Text").GetValue(toggle, null);
                var value = toggle.GetType().GetProperty("Value");
                var current = (bool)value.GetValue(toggle, null);
                var match = wanted == null ? null : wanted.Properties().FirstOrDefault(p => text.IndexOf(p.Name, StringComparison.OrdinalIgnoreCase) >= 0);
                if (match != null)
                {
                    used.Add(match.Name);
                    var target = (bool)match.Value;
                    if (target != current)
                    {
                        changed.Add(new KeyValuePair<object, bool>(new PluginToggle(toggle, value), current));
                        value.SetValue(toggle, target, null);
                        current = target;
                    }
                }
                result.Add(new JObject { ["text"] = text, ["value"] = current });
            }
            var unknown = wanted == null ? new string[0] : wanted.Properties().Select(p => p.Name).Where(n => !used.Contains(n)).ToArray();
            if (unknown.Length > 0)
            {
                RestoreToggles(changed);
                throw new ArgumentException("No KKAPI load toggle matches " + string.Join(", ", unknown) + "; available: "
                    + string.Join(", ", result.Select(t => (string)t["text"]).ToArray()));
            }
            return result;
        }

        private sealed class PluginToggle
        {
            internal readonly object Toggle;
            internal readonly PropertyInfo Value;
            internal PluginToggle(object toggle, PropertyInfo value) { Toggle = toggle; Value = value; }
        }

        // Loads a coordinate card into the current outfit like the maker's load button, with its clothes/accessories toggles.
        private JObject LoadCoordinate(JObject args)
        {
            var character = Character();
            var path = (string)args["path"];
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path requires a coordinate card (see coordinate_list).");
            if (!Path.IsPathRooted(path)) path = Path.Combine(CoordinateFolder(), path);
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(path + ".png")) path += ".png";
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("Coordinate card not found: " + path);
            var clothes = (bool?)args["clothes"] ?? true;
            var accessories = (bool?)args["accessories"] ?? true;
            if (!clothes && !accessories) throw new ArgumentException("Nothing to load: clothes and accessories are both false.");
            var probe = new ChaFileCoordinate();
            if (!probe.LoadFile(path)) throw new IOException("Not a coordinate card for this game (error " + probe.GetLastErrorCode() + "): " + path);

            // Plugins read the maker's load toggles (KKAPI GetCoordinateLoadFlags) while they apply their data, so match them for a while.
            var window = Resources.FindObjectsOfTypeAll<CustomCoordinateFile>().Where(x => x.gameObject.scene.IsValid())
                .Select(x => typeof(CustomCoordinateFile).GetField("fileWindow", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(x))
                .FirstOrDefault(x => x != null);
            var toggles = new List<KeyValuePair<object, bool>>();
            if (window != null)
            {
                SetToggle(window, "tglCoordeLoadClothes", clothes, toggles);
                SetToggle(window, "tglCoordeLoadAcs", accessories, toggles);
            }
            try
            {
                var now = character.nowCoordinate;
                var keepClothes = clothes ? null : MessagePackSerializer.Serialize(now.clothes);
                var keepAccessories = accessories ? null : MessagePackSerializer.Serialize(now.accessory);
                if (!now.LoadFile(path)) throw new IOException("The coordinate card could not be loaded: " + path);
                if (keepClothes != null) now.clothes = MessagePackSerializer.Deserialize<ChaFileClothes>(keepClothes);
                if (keepAccessories != null) now.accessory = MessagePackSerializer.Deserialize<ChaFileAccessory>(keepAccessories);
                character.Reload(false, true, true, true);
                character.AssignCoordinate((ChaFileDefine.CoordinateType)character.chaFile.status.coordinateType);
                Singleton<CustomBase>.Instance.updateCustomUI = true;
            }
            finally
            {
                if (toggles.Count > 0) _plugin.StartCoroutine(RestoreTogglesLater(toggles));
            }
            // Renderers were rebuilt, so ids from before the load are stale.
            _plugin.Session = Guid.NewGuid().ToString("N");
            _plugin.Batches.Clear();
            return new JObject { ["sceneSession"] = _plugin.Session, ["path"] = path, ["coordinateName"] = probe.coordinateName,
                ["coordinateType"] = character.fileStatus.coordinateType, ["loadedClothes"] = clothes, ["loadedAccessories"] = accessories,
                ["extendedData"] = ExtendedDataKeys(probe), ["clothes"] = Clothes(character) };
        }

        private static void SetToggle(object window, string field, bool value, List<KeyValuePair<object, bool>> changed)
        {
            var toggle = window.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance)?.GetValue(window);
            var isOn = toggle == null ? null : toggle.GetType().GetProperty("isOn");
            if (isOn == null || (bool)isOn.GetValue(toggle, null) == value) return;
            changed.Add(new KeyValuePair<object, bool>(toggle, !value));
            isOn.SetValue(toggle, value, null);
        }

        private static IEnumerator RestoreTogglesLater(List<KeyValuePair<object, bool>> toggles)
        {
            for (var i = 0; i < 30; i++) yield return null;
            RestoreToggles(toggles);
        }

        private static void RestoreToggles(List<KeyValuePair<object, bool>> toggles)
        {
            foreach (var pair in toggles)
                try
                {
                    var plugin = pair.Key as PluginToggle;
                    if (plugin != null) plugin.Value.SetValue(plugin.Toggle, pair.Value, null);
                    else pair.Key.GetType().GetProperty("isOn").SetValue(pair.Key, pair.Value, null);
                }
                catch { }
            toggles.Clear();
        }

        // Adds a new card to the maker's coordinate list the way its save button does. Rebuilding the list would re-read every
        // card (many GB in some folders); an overwritten card's entry or a panel not opened yet catches up when the list loads.
        private static bool AddToList(CustomCoordinateFile file, string path, string name, bool exists)
        {
            if (exists) return false;
            try
            {
                var list = typeof(CustomCoordinateFile).GetField("listCtrl", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(file) as CustomFileListCtrl;
                if (list == null) return false;
                var fileName = Path.GetFileNameWithoutExtension(path);
#if KK
                list.AddList(list.GetNoUseIndex(), name, string.Empty, string.Empty, path, fileName, DateTime.Now);
                list.ReCreate();
#else
                var info = new CustomFileInfo(new FolderAssist.FileInfo(path, fileName)) { index = list.GetNoUseIndex(), name = name };
                info.UpdateInfo(name, DateTime.Now);
                list.Add(info);
#endif
                return true;
            }
            catch (Exception) { return false; }
        }

        private JObject Snapshot()
        {
            var character = Character();
            var accessories = new JArray();
            var objects = character.GetAccessoryObjects();
            var parts = character.nowCoordinate.accessory.parts;
            for (var i = 0; i < objects.Length; i++)
            {
                if (objects[i] == null) continue;
                var entry = new JObject { ["slot"] = i, ["object"] = objects[i].name,
                    ["rendererCount"] = objects[i].GetComponentsInChildren<Renderer>(true).Length };
                if (i < parts.Length) { entry["type"] = parts[i].type; entry["id"] = parts[i].id; entry["parent"] = parts[i].parentKey; }
                if (i < character.infoAccessory.Length && character.infoAccessory[i] != null) entry["name"] = character.infoAccessory[i].Name;
                accessories.Add(entry);
            }
            var hair = new JArray();
            for (var i = 0; i < character.objHair.Length; i++)
                if (character.objHair[i] != null) hair.Add(new JObject { ["slot"] = i, ["object"] = character.objHair[i].name,
                    ["id"] = character.fileHair.parts[i].id, ["name"] = character.infoHair[i]?.Name });
            return new JObject { ["sceneSession"] = _plugin.Session, ["objectId"] = 0,
                ["name"] = character.fileParam.fullname, ["sex"] = character.sex,
                ["coordinateType"] = character.fileStatus.coordinateType,
                ["rendererCount"] = character.GetComponentsInChildren<Renderer>(true).Length,
                ["clothes"] = Clothes(character), ["hair"] = hair, ["accessories"] = accessories, ["camera"] = CameraState() };
        }

        private static JArray Clothes(ChaControl character)
        {
            var clothes = new JArray();
            for (var i = 0; i < character.objClothes.Length; i++)
            {
                var part = character.objClothes[i];
                clothes.Add(new JObject { ["slot"] = i, ["kind"] = i < ClothesKinds.Length ? ClothesKinds[i] : "slot" + i,
                    ["id"] = character.nowCoordinate.clothes.parts[i].id, ["name"] = character.infoClothes[i]?.Name,
                    ["object"] = part?.name, ["state"] = character.fileStatus.clothesState[i],
                    ["rendererCount"] = part == null ? 0 : part.GetComponentsInChildren<Renderer>(true).Length });
            }
            return clothes;
        }

        internal static int ClothesKind(JToken value)
        {
            if (value != null && value.Type == JTokenType.Integer && (int)value >= 0 && (int)value < ClothesKinds.Length) return (int)value;
            var index = value != null && value.Type == JTokenType.String ? Array.IndexOf(ClothesKinds, (string)value) : -1;
            if (index < 0) throw new ArgumentException("kind requires a slot 0-8 or one of: " + string.Join(", ", ClothesKinds));
            return index;
        }

        private static BaseCameraControl_Ver2 CameraControl()
        {
            var camera = Camera.main != null ? Camera.main.GetComponent<BaseCameraControl_Ver2>() : null;
            if (camera == null) throw new InvalidOperationException("Maker camera controller is unavailable.");
            return camera;
        }

        private static JObject CameraState()
        {
            var camera = CameraControl();
            return new JObject { ["pivot"] = SceneCommands.Json(camera.TargetPos), ["rotation"] = SceneCommands.Json(camera.CameraAngle),
                ["fov"] = camera.CameraFov, ["distance"] = SceneCommands.Json(camera.CameraDir),
                ["eye"] = SceneCommands.Json(Camera.main.transform.position) };
        }

        private static void ValidateCamera(JObject args)
        {
            foreach (var key in new[] { "pivot", "rotation", "distance" })
                if (args[key] != null) SceneCommands.Vector(args[key]);
            if (args["distance"] != null)
            {
                var distance = SceneCommands.Vector(args["distance"]).magnitude;
                if (distance < 0.01f || distance > 1000) throw new ArgumentOutOfRangeException("distance");
            }
            if (args["fov"] != null && (SceneCommands.Number(args["fov"]) < 1 || SceneCommands.Number(args["fov"]) > 170))
                throw new ArgumentOutOfRangeException("fov");
        }
    }
}
