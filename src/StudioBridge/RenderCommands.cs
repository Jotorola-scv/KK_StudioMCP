using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudioMcp.Bridge
{
    // Opt-in native settings are stored in scene extended data and checkpoint sidecars.
    internal sealed class RenderCommands
    {
        internal bool OwnsSettings;
        internal JObject SkyBinding;
        private bool _nativeSkyControllerEnabled;

        internal void ClearSkyBinding()
        {
            if (SkyBinding != null && Studio.Studio.Instance != null)
            {
                NativeEffectsCommands.SkyController().enabled = _nativeSkyControllerEnabled;
                // A skybox captured before binding can belong to a previous scene; let Studio re-apply the current pattern.
                NativeEffectsCommands.RefreshSky();
            }
            SkyBinding = null;
        }

        internal JObject BindSky(JObject args)
        {
            var target = Plugin.Instance.Materials.Resolve(args);
            var mat = target.Material;
            if (target.Scope != "Item" || !mat.shader.name.StartsWith("Skybox/", StringComparison.Ordinal))
                throw new ArgumentException("An inspected Studio item with a native Skybox material is required.");
            var exposure = SceneCommands.Number(args["exposure"]);
            var rotation = SceneCommands.Number(args["rotation"]);
            if (exposure < 0 || exposure > 8 || rotation < 0 || rotation > 360) throw new ArgumentOutOfRangeException("Sky exposure/rotation");
            if (!mat.HasProperty("_Exposure") || !mat.HasProperty("_Rotation")) throw new ArgumentException("Sky material lacks exposure/rotation.");
            if (SkyBinding == null) _nativeSkyControllerEnabled = NativeEffectsCommands.SkyController().enabled;
            NativeEffectsCommands.SkyController().enabled = false;
            mat.SetFloat("_Exposure", exposure); mat.SetFloat("_Rotation", rotation);
            RenderSettings.skybox = mat; Camera.main.clearFlags = CameraClearFlags.Skybox;
            target.Renderer.enabled = false;
            SkyBinding = new JObject { ["objectId"] = target.ObjectId, ["renderer"] = target.Renderer.name,
                ["slot"] = target.Slot, ["exposure"] = exposure, ["rotation"] = rotation };
            return new JObject { ["binding"] = SkyBinding.DeepClone(), ["shader"] = mat.shader.name,
                ["persistence"] = "scene_extended_data", ["nativeControllerEnabled"] = NativeEffectsCommands.SkyController().enabled };
        }

        internal void RestoreSky(JObject binding)
        {
            var obj = Plugin.Instance.Scene.GetObject((int)binding["objectId"]);
            var root = SceneCommands.Root(obj);
            var matches = root.GetComponentsInChildren<Renderer>(true).Where(r => r.name == (string)binding["renderer"]).ToArray();
            if(matches.Length != 1) throw new InvalidOperationException("Saved sky renderer is missing or ambiguous.");
            var args=(JObject)binding.DeepClone(); args["rendererId"] = matches[0].GetInstanceID();
            BindSky(args);
        }
        private static readonly HashSet<string> Keys = new HashSet<string> {
            "ambientLight", "ambientIntensity", "fog", "fogColor", "fogDensity", "fogStartDistance",
            "fogEndDistance", "shadowDistance", "shadowCascades", "antiAliasing", "pixelLightCount",
            "realtimeReflectionProbes", "ambientMode", "ambientSkyColor", "ambientEquatorColor", "ambientGroundColor",
            "cameraBackgroundColor", "cameraClearFlags" };

        internal JObject State() => new JObject {
            ["ambientLight"] = SceneCommands.Json(RenderSettings.ambientLight),
            ["ambientIntensity"] = RenderSettings.ambientIntensity,
            ["fog"] = RenderSettings.fog, ["fogColor"] = SceneCommands.Json(RenderSettings.fogColor),
            ["fogDensity"] = RenderSettings.fogDensity, ["fogStartDistance"] = RenderSettings.fogStartDistance,
            ["fogEndDistance"] = RenderSettings.fogEndDistance, ["shadowDistance"] = QualitySettings.shadowDistance,
            ["shadowCascades"] = QualitySettings.shadowCascades, ["antiAliasing"] = QualitySettings.antiAliasing,
            ["pixelLightCount"] = QualitySettings.pixelLightCount,
            ["realtimeReflectionProbes"] = QualitySettings.realtimeReflectionProbes,
            ["ambientMode"] = (int)RenderSettings.ambientMode,
            ["ambientSkyColor"] = SceneCommands.Json(RenderSettings.ambientSkyColor),
            ["ambientEquatorColor"] = SceneCommands.Json(RenderSettings.ambientEquatorColor),
            ["ambientGroundColor"] = SceneCommands.Json(RenderSettings.ambientGroundColor),
            ["cameraBackgroundColor"] = SceneCommands.Json(Camera.main != null ? Camera.main.backgroundColor : Color.black),
            ["cameraClearFlags"] = Camera.main != null ? (int)Camera.main.clearFlags : 2 };

        internal JObject Diagnostics() => new JObject {
            ["colorSpace"] = QualitySettings.activeColorSpace.ToString(),
            ["frame"] = Time.frameCount,
            ["customSky"] = SkyBinding == null ? null : SkyBinding.DeepClone(),
            ["particles"] = new JArray(UnityEngine.Object.FindObjectsOfType<ParticleSystem>().Take(48).Select(p => new JObject {
                ["name"] = p.name, ["playing"] = p.isPlaying, ["count"] = p.particleCount,
                ["layer"] = p.gameObject.layer, ["active"] = p.gameObject.activeInHierarchy,
                ["rendererEnabled"] = p.GetComponent<ParticleSystemRenderer>() != null && p.GetComponent<ParticleSystemRenderer>().enabled,
                ["cameraLayerIncluded"] = Camera.main != null && (Camera.main.cullingMask & (1 << p.gameObject.layer)) != 0,
                ["visibleToCamera"] = p.gameObject.activeInHierarchy && p.GetComponent<ParticleSystemRenderer>() != null
                    && p.GetComponent<ParticleSystemRenderer>().enabled && Camera.main != null
                    && (Camera.main.cullingMask & (1 << p.gameObject.layer)) != 0 })),
            ["deltaTime"] = Time.unscaledDeltaTime,
            ["probeControllers"] = ProbeControllers(),
            ["camera"] = Camera.main == null ? null : new JObject {
                ["near"] = Camera.main.nearClipPlane, ["far"] = Camera.main.farClipPlane,
                ["hdr"] = Camera.main.allowHDR, ["cullingMask"] = Camera.main.cullingMask,
                ["depthMode"] = Camera.main.depthTextureMode.ToString() },
            ["lights"] = new JArray(UnityEngine.Object.FindObjectsOfType<Light>().Take(128).Select(l => new JObject {
                ["name"] = l.name, ["type"] = l.type.ToString(), ["position"] = SceneCommands.Json(l.transform.position),
                ["enabled"] = l.enabled, ["intensity"] = l.intensity, ["range"] = l.range,
                ["color"] = SceneCommands.Json(l.color), ["cullingMask"] = l.cullingMask,
                ["shadows"] = l.shadows.ToString(), ["renderMode"] = l.renderMode.ToString() })),
            ["probes"] = new JArray(UnityEngine.Object.FindObjectsOfType<ReflectionProbe>().Take(64).Select(p => new JObject {
                ["name"] = p.name, ["enabled"] = p.enabled, ["position"] = SceneCommands.Json(p.transform.position),
                ["size"] = SceneCommands.Json(p.size), ["resolution"] = p.resolution,
                ["intensity"] = p.intensity, ["texture"] = p.texture != null ? p.texture.name : null,
                ["textureWidth"] = p.texture != null ? p.texture.width : 0 })) };

        private static JArray ProbeControllers()
        {
            var result = new JArray();
            if (Studio.Studio.Instance == null) return result;
            foreach (var item in Studio.Studio.Instance.dicObjectCtrl.Values.OfType<Studio.OCIItem>())
                foreach (var component in item.objectItem.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (component == null) continue;
                    var type = component.GetType();
                    if (type.FullName != "TomProbeRuntime.TomProbeControl") continue;
                    var reflection = component.GetComponents<MonoBehaviour>().FirstOrDefault(c =>
                        c != null && c.GetType().FullName == "TomProbeRuntime.TomReflectionProbeController");
                    var native = reflection == null ? null : reflection.GetType().GetProperty("RuntimeProbe")?.GetValue(reflection, null) as ReflectionProbe;
                    result.Add(new JObject { ["objectId"] = item.objectInfo.dicKey, ["active"] = component.isActiveAndEnabled,
                        ["status"] = Convert.ToString(type.GetProperty("Status").GetValue(component, null)),
                        ["completedCaptures"] = Convert.ToInt32(type.GetProperty("CompletedCaptures").GetValue(component, null)),
                        ["runtimeProbe"] = native == null ? null : new JObject {
                            ["enabled"] = native.enabled, ["position"] = SceneCommands.Json(native.transform.position),
                            ["resolution"] = native.resolution, ["cullingMask"] = native.cullingMask,
                            ["boxProjection"] = native.boxProjection, ["hdr"] = native.hdr,
                            ["textureReady"] = native.texture != null,
                            ["textureWidth"] = native.texture != null ? native.texture.width : 0 },
                        ["components"] = new JArray(component.GetComponents<MonoBehaviour>().Where(c => c != null).Select(c => c.GetType().FullName + ":enabled=" + c.enabled)) });
                }
            return result;
        }

        internal static void Validate(JObject values)
        {
            if (values == null) throw new ArgumentException("Render values are required.");
            foreach (var p in values.Properties())
            {
                if (!Keys.Contains(p.Name)) throw new ArgumentException("Unsupported native render setting: " + p.Name);
                if (p.Name == "ambientLight" || p.Name.EndsWith("Color", StringComparison.Ordinal)) { MaterialCommands.ColorValue(p.Value); continue; }
                if (p.Name == "fog" || p.Name == "realtimeReflectionProbes") { if (p.Value.Type != JTokenType.Boolean) throw new ArgumentException(p.Name + " requires a boolean."); continue; }
                var v = SceneCommands.Number(p.Value);
                if (v < 0 || v > 100000) throw new ArgumentOutOfRangeException(p.Name);
                if (p.Name == "antiAliasing" && v != 0 && v != 2 && v != 4 && v != 8) throw new ArgumentException("antiAliasing must be 0, 2, 4 or 8.");
                if (p.Name == "shadowCascades" && v != 0 && v != 2 && v != 4) throw new ArgumentException("shadowCascades must be 0, 2 or 4.");
                if (p.Name == "pixelLightCount" && (v != (int)v || v > 64)) throw new ArgumentOutOfRangeException(p.Name);
                if (p.Name == "ambientMode" && (v != (int)v || !Enum.IsDefined(typeof(UnityEngine.Rendering.AmbientMode), (int)v))) throw new ArgumentOutOfRangeException(p.Name);
                if (p.Name == "cameraClearFlags" && v != 1 && v != 2) throw new ArgumentException("Only Skybox (1) and SolidColor (2) are supported.");
            }
        }

        internal JObject Apply(JObject values)
        {
            Validate(values);
            var before = State();
            foreach (var p in values.Properties())
                switch (p.Name)
                {
                    case "ambientLight": RenderSettings.ambientLight = MaterialCommands.ColorValue(p.Value); break;
                    case "ambientIntensity": RenderSettings.ambientIntensity = (float)p.Value; break;
                    case "fog": RenderSettings.fog = (bool)p.Value; break;
                    case "fogColor": RenderSettings.fogColor = MaterialCommands.ColorValue(p.Value); break;
                    case "fogDensity": RenderSettings.fogDensity = (float)p.Value; break;
                    case "fogStartDistance": RenderSettings.fogStartDistance = (float)p.Value; break;
                    case "fogEndDistance": RenderSettings.fogEndDistance = (float)p.Value; break;
                    case "shadowDistance": QualitySettings.shadowDistance = (float)p.Value; break;
                    case "shadowCascades": QualitySettings.shadowCascades = (int)p.Value; break;
                    case "antiAliasing": QualitySettings.antiAliasing = (int)p.Value; break;
                    case "pixelLightCount": QualitySettings.pixelLightCount = (int)p.Value; break;
                    case "realtimeReflectionProbes": QualitySettings.realtimeReflectionProbes = (bool)p.Value; break;
                    case "ambientMode": RenderSettings.ambientMode = (UnityEngine.Rendering.AmbientMode)(int)p.Value; break;
                    case "ambientSkyColor": RenderSettings.ambientSkyColor = MaterialCommands.ColorValue(p.Value); break;
                    case "ambientEquatorColor": RenderSettings.ambientEquatorColor = MaterialCommands.ColorValue(p.Value); break;
                    case "ambientGroundColor": RenderSettings.ambientGroundColor = MaterialCommands.ColorValue(p.Value); break;
                    case "cameraBackgroundColor": if (Camera.main != null) Camera.main.backgroundColor = MaterialCommands.ColorValue(p.Value); break;
                    case "cameraClearFlags": if (Camera.main != null) Camera.main.clearFlags = (CameraClearFlags)(int)p.Value; break;
                }
            OwnsSettings = true;
            return new JObject { ["before"] = before, ["after"] = State(), ["persistence"] = "scene_extended_data_and_checkpoint_sidecar" };
        }
    }
}
