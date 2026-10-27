using System;
using System.Collections;
using System.Linq;
using ExtensibleSaveFormat;
using KKAPI.Studio.SaveLoad;
using KKAPI.Utilities;
using Newtonsoft.Json.Linq;
using Studio;

namespace StudioMcp.Bridge
{
    // Opt-in native environment persistence; imports must not replace the destination environment.
    public sealed class BridgeSceneController : SceneCustomFunctionController
    {
        private int _generation;
        protected override void OnSceneSave()
        {
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.Rendering.OwnsSettings && plugin.Scene.LightOverrides.Count == 0 && plugin.Rendering.SkyBinding == null && !plugin.Tone.Owns) { SetExtendedData(null); return; }
            var data = new PluginData { version = 1 };
            if (plugin.Rendering.OwnsSettings) data.data["render"] = plugin.Rendering.State().ToString(Newtonsoft.Json.Formatting.None);
            if (plugin.Tone.Owns) data.data["tonemap"] = plugin.Tone.Saved().ToString(Newtonsoft.Json.Formatting.None);
            if (plugin.Rendering.SkyBinding != null) data.data["skyBinding"] = plugin.Rendering.SkyBinding.ToString(Newtonsoft.Json.Formatting.None);
            data.data["lights"] = new JArray(plugin.Scene.LightOverrides.Select(p => new JObject {
                ["id"] = p.Key, ["values"] = p.Value.DeepClone() })).ToString(Newtonsoft.Json.Formatting.None);
            data.data["names"] = new JArray(Studio.Studio.Instance.dicObjectCtrl.Select(p => new JObject {
                ["id"] = p.Key, ["name"] = p.Value.treeNodeObject.textName })).ToString(Newtonsoft.Json.Formatting.None);
            SetExtendedData(data);
        }

        protected override void OnSceneLoad(SceneOperationKind operation, ReadOnlyDictionary<int, ObjectCtrlInfo> loadedItems)
        {
            var importing = operation == SceneOperationKind.Import;
            if (!importing) ++_generation;
            var plugin = Plugin.Instance;
            if (plugin == null) return;
            if (!importing)
            {
                plugin.Rendering.OwnsSettings = false;
                plugin.Rendering.ClearSkyBinding();
                plugin.Scene.LightOverrides.Clear();
                plugin.Tone.Reset();
                plugin.Rendering.ResetCamera();
            }
            if (operation != SceneOperationKind.Load && !importing) return;
            var data = GetExtendedData();
            object raw;
            if (data == null || data.version != 1) return;
            try
            {
                if (!importing && data.data.TryGetValue("skyBinding", out raw) && raw is string)
                {
                    var sky = JObject.Parse((string)raw);
                    ObjectCtrlInfo owner;
                    if (loadedItems.TryGetValue((int)sky["objectId"], out owner)) {
                        sky["objectId"] = owner.objectInfo.dicKey;
                        StartCoroutine(ApplySkyAfterLoad(sky, _generation));
                    }
                }
                if (!importing && data.data.TryGetValue("render", out raw) && raw is string)
                {
                    var values = JObject.Parse((string)raw);
                    RenderCommands.Validate(values);
                    StartCoroutine(ApplyAfterLoad(values, _generation));
                }
                // Studio never writes the tonemapper fields, so the saved values can apply immediately.
                if (!importing && data.data.TryGetValue("tonemap", out raw) && raw is string)
                    plugin.Tone.Apply(JObject.Parse((string)raw));
                if (data.data.TryGetValue("lights", out raw) && raw is string)
                    foreach (var entry in JArray.Parse((string)raw))
                    {
                        ObjectCtrlInfo item;
                        if (loadedItems.TryGetValue((int)entry["id"], out item) && item is OCILight)
                            plugin.Scene.ApplyLightOverrides((OCILight)item, (JObject)entry["values"]);
                    }
                if (data.data.TryGetValue("names", out raw) && raw is string)
                    foreach (var entry in JArray.Parse((string)raw))
                    {
                        ObjectCtrlInfo item;
                        if (loadedItems.TryGetValue((int)entry["id"], out item)) item.treeNodeObject.textName = (string)entry["name"];
                    }
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning("[StudioMCP] Invalid saved environment: " + ex.Message); }
        }

        private IEnumerator ApplyAfterLoad(JObject values, int generation)
        {
            // Let native Studio scene/camera initialization finish, once, without fighting later edits.
            for (var i = 0; i < 5; ++i) yield return null;
            if (_generation == generation && Plugin.Instance != null) Plugin.Instance.Rendering.Apply(values);
        }

        private IEnumerator ApplySkyAfterLoad(JObject values, int generation)
        {
            for (var i = 0; i < 8; ++i) yield return null;
            if (_generation == generation && Plugin.Instance != null) {
                try { Plugin.Instance.Rendering.RestoreSky(values); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning("[StudioMCP] Saved sky binding: " + ex.Message); }
            }
        }
    }
}
