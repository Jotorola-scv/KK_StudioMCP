using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudioMcp.Bridge
{
    // Use Studio's own SceneInfo and effect controllers; never install a second post stack.
    internal static class NativeEffectsCommands
    {
        private static readonly string[] Fields = {
            "aceBlend", "enableAOE", "aoeColor", "aoeRadius", "enableBloom", "bloomIntensity",
            "bloomBlur", "bloomThreshold", "enableDepth", "depthFocalSize", "depthAperture",
            "enableVignette", "enableFog", "fogColor", "fogHeight", "fogStartDistance",
            "enableSunShafts", "sunThresholdColor", "sunColor" };

        internal static JObject State()
        {
            var studio = Studio.Studio.Instance;
            if (studio == null) throw new InvalidOperationException("Studio is not ready.");
            var values = new JObject();
            foreach (var name in Fields)
            {
                var f = typeof(Studio.SceneInfo).GetField(name);
                if (f == null) continue;
                var value = f.GetValue(studio.sceneInfo);
                values[name] = value is Color ? SceneCommands.Json((Color)value) : JToken.FromObject(value);
            }
            values["characterLightIntensity"] = studio.sceneInfo.charaLight.intensity;
            return new JObject { ["values"] = values,
                ["cameraComponents"] = new JArray(Camera.main.GetComponents<MonoBehaviour>().Where(c => c != null)
                    .Select(c => new JObject { ["type"] = c.GetType().FullName, ["assembly"] = c.GetType().Assembly.GetName().Name,
                        ["enabled"] = c.enabled, ["active"] = c.isActiveAndEnabled })),
                ["persistence"] = "native_Studio_SceneInfo" };
        }

        internal static JObject Apply(JObject values)
        {
            if (values == null || !values.Properties().Any()) throw new ArgumentException("Native effect values are required.");
            var before = State();
            foreach (var p in values.Properties())
            {
                if (p.Name == "characterLightIntensity") {
                    var intensity = SceneCommands.Number(p.Value);
                    if (intensity < 0 || intensity > 2) throw new ArgumentOutOfRangeException(p.Name);
                    continue;
                }
                if (!Fields.Contains(p.Name)) throw new ArgumentException("Unsupported native effect: " + p.Name);
                var f = typeof(Studio.SceneInfo).GetField(p.Name);
                if (f == null) throw new NotSupportedException("Native field not available: " + p.Name);
                if (f.FieldType == typeof(bool)) { if (p.Value.Type != JTokenType.Boolean) throw new ArgumentException(p.Name + " requires a boolean."); }
                else if (f.FieldType == typeof(Color)) MaterialCommands.ColorValue(p.Value);
                else {
                    var n = SceneCommands.Number(p.Value);
                    var maximum = p.Name == "fogHeight" || p.Name == "fogStartDistance" ? 1000 : p.Name == "aoeRadius" ? 5 : 10;
                    if (n < 0 || n > maximum || p.Name == "aceBlend" && n > 1) throw new ArgumentOutOfRangeException(p.Name);
                }
            }
            var info = Studio.Studio.Instance.sceneInfo;
            foreach (var p in values.Properties())
            {
                if (p.Name == "characterLightIntensity") { info.charaLight.intensity = (float)p.Value; continue; }
                var f = typeof(Studio.SceneInfo).GetField(p.Name);
                f.SetValue(info, f.FieldType == typeof(Color) ? (object)MaterialCommands.ColorValue(p.Value)
                    : f.FieldType == typeof(bool) ? (object)(bool)p.Value : (float)p.Value);
            }
            Studio.Studio.Instance.cameraLightCtrl.Reflect();
            // These bounded UI model refreshes also apply the actual native effect components.
            foreach (var name in new[] { "amplifyColorEffectInfo", "amplifyOcculusionEffectInfo", "bloomInfo", "dofInfo", "vignetteInfo", "fogInfo", "sunShaftsInfo" })
                Refresh(name);
            return new JObject { ["before"] = before, ["after"] = State() };
        }

        private static void Refresh(string name)
        {
            var ctrl = Studio.Studio.Instance.systemButtonCtrl;
            var field = ctrl.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new MissingFieldException(name);
            var model = field.GetValue(ctrl);
            model.GetType().GetMethod("UpdateInfo", BindingFlags.Public | BindingFlags.Instance).Invoke(model, null);
        }

        internal static void RefreshSky()
        {
#if KKS
            Refresh("skyInfo");
#endif
        }

        internal static Behaviour SkyController()
        {
#if KKS
            var ctrl = Studio.Studio.Instance.systemButtonCtrl;
            var model = ctrl.GetType().GetField("skyInfo", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ctrl);
            return (Behaviour)model.GetType().GetField("controller").GetValue(model);
#else
            throw new NotSupportedException("Custom skies are qualified only on KKS.");
#endif
        }

        internal static JObject SkyState()
        {
#if KKS
            var info = Studio.Studio.Instance.sceneInfo.skyInfo;
            return new JObject { ["enabled"] = info.Enable, ["pattern"] = info.Pattern,
                ["material"] = RenderSettings.skybox != null ? RenderSettings.skybox.name : null,
                ["shader"] = RenderSettings.skybox != null ? RenderSettings.skybox.shader.name : null,
                ["catalog"] = new JArray(Studio.Info.Instance.dicSkyPatternInfo.Select(p => new JObject {
                    ["id"] = p.Key, ["name"] = p.Value.name, ["bundle"] = p.Value.bundlePath, ["asset"] = p.Value.fileName })) };
#else
            throw new NotSupportedException("Native sky presets are qualified only on KKS.");
#endif
        }

        internal static JObject SkyApply(JObject args)
        {
#if KKS
            var id = (int)args["pattern"];
            if (!Studio.Info.Instance.dicSkyPatternInfo.ContainsKey(id)) throw new ArgumentException("Unknown native sky ID.");
            if (args["enabled"].Type != JTokenType.Boolean) throw new ArgumentException("enabled requires a boolean.");
            var info = Studio.Studio.Instance.sceneInfo.skyInfo;
            Plugin.Instance.Rendering.ClearSkyBinding();
            info.Pattern = id; info.Enable = (bool)args["enabled"];
            Refresh("skyInfo");
            return SkyState();
#else
            throw new NotSupportedException("Native sky presets are qualified only on KKS.");
#endif
        }
    }
}
