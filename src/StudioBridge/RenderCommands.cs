using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudioMcp.Bridge
{
    // Native Unity settings are runtime settings. Checkpoints keep a separate sidecar for these.
    internal sealed class RenderCommands
    {
        private static readonly HashSet<string> Keys = new HashSet<string> {
            "ambientLight", "ambientIntensity", "fog", "fogColor", "fogDensity", "fogStartDistance",
            "fogEndDistance", "shadowDistance", "shadowCascades", "antiAliasing", "pixelLightCount" };

        internal JObject State() => new JObject {
            ["ambientLight"] = SceneCommands.Json(RenderSettings.ambientLight),
            ["ambientIntensity"] = RenderSettings.ambientIntensity,
            ["fog"] = RenderSettings.fog, ["fogColor"] = SceneCommands.Json(RenderSettings.fogColor),
            ["fogDensity"] = RenderSettings.fogDensity, ["fogStartDistance"] = RenderSettings.fogStartDistance,
            ["fogEndDistance"] = RenderSettings.fogEndDistance, ["shadowDistance"] = QualitySettings.shadowDistance,
            ["shadowCascades"] = QualitySettings.shadowCascades, ["antiAliasing"] = QualitySettings.antiAliasing,
            ["pixelLightCount"] = QualitySettings.pixelLightCount };

        internal static void Validate(JObject values)
        {
            if (values == null) throw new ArgumentException("Render values are required.");
            foreach (var p in values.Properties())
            {
                if (!Keys.Contains(p.Name)) throw new ArgumentException("Unsupported native render setting: " + p.Name);
                if (p.Name == "ambientLight" || p.Name == "fogColor") { MaterialCommands.ColorValue(p.Value); continue; }
                if (p.Name == "fog") { if (p.Value.Type != JTokenType.Boolean) throw new ArgumentException("fog requires a boolean."); continue; }
                var v = SceneCommands.Number(p.Value);
                if (v < 0 || v > 100000) throw new ArgumentOutOfRangeException(p.Name);
                if (p.Name == "antiAliasing" && v != 0 && v != 2 && v != 4 && v != 8) throw new ArgumentException("antiAliasing must be 0, 2, 4 or 8.");
                if (p.Name == "shadowCascades" && v != 0 && v != 2 && v != 4) throw new ArgumentException("shadowCascades must be 0, 2 or 4.");
                if (p.Name == "pixelLightCount" && (v != (int)v || v > 64)) throw new ArgumentOutOfRangeException(p.Name);
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
                }
            return new JObject { ["before"] = before, ["after"] = State(), ["persistence"] = "runtime_and_checkpoint_sidecar" };
        }
    }
}
