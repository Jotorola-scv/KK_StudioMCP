using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudioMcp.Bridge
{
    // The Studio camera's existing AmplifyColor tonemapper. Studio saves only the LUT blend, so the
    // tonemapper, exposure and white point are opt-in bridge settings stored in scene extended data.
    internal sealed class ToneCommands
    {
        private static readonly string[] Modes = { "Disabled", "Photographic", "FilmicHable", "FilmicACES" };
        internal bool Owns;
        private JObject _defaults;

        private static Component Effect()
        {
            var type = MaterialCommands.FindType("AmplifyColorBase");
            var effect = type == null || Camera.main == null ? null : Camera.main.GetComponent(type);
            if (effect == null) throw new NotSupportedException("The Studio camera has no AmplifyColor effect.");
            return effect;
        }

        private static FieldInfo Field(Component effect, string name) =>
            effect.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new NotSupportedException("AmplifyColor field is unavailable: " + name);

        private static JObject Read(Component effect) => new JObject {
            ["tonemapper"] = Field(effect, "Tonemapper").GetValue(effect).ToString(),
            ["exposure"] = (float)Field(effect, "Exposure").GetValue(effect),
            ["linearWhitePoint"] = (float)Field(effect, "LinearWhitePoint").GetValue(effect) };

        internal JObject Saved() => Read(Effect());

        internal JObject State()
        {
            var effect = Effect();
            if (_defaults == null) _defaults = Read(effect);
            var state = Read(effect);
            state["blendAmount"] = (float)Field(effect, "BlendAmount").GetValue(effect);
            state["owned"] = Owns;
            return state;
        }

        internal static void Validate(JObject values)
        {
            if (values == null || !values.Properties().Any()) throw new ArgumentException("Tonemap values are required.");
            foreach (var p in values.Properties())
            {
                if (p.Name == "tonemapper")
                {
                    if (p.Value.Type != JTokenType.String || !Modes.Contains((string)p.Value))
                        throw new ArgumentException("tonemapper must be one of " + string.Join(", ", Modes) + ".");
                    continue;
                }
                if (p.Name != "exposure" && p.Name != "linearWhitePoint") throw new ArgumentException("Unsupported tonemap setting: " + p.Name);
                var v = SceneCommands.Number(p.Value);
                if (p.Name == "exposure" && (v < 0.05f || v > 16)) throw new ArgumentOutOfRangeException(p.Name);
                if (p.Name == "linearWhitePoint" && (v < 0.5f || v > 64)) throw new ArgumentOutOfRangeException(p.Name);
            }
        }

        internal JObject Apply(JObject values)
        {
            Validate(values);
            var before = State();
            Write(Effect(), values);
            Owns = true;
            return new JObject { ["before"] = before, ["after"] = State(), ["persistence"] = "scene_extended_data" };
        }

        private static void Write(Component effect, JObject values)
        {
            if (values["tonemapper"] != null)
            {
                var mode = Field(effect, "Tonemapper");
                mode.SetValue(effect, Enum.Parse(mode.FieldType, (string)values["tonemapper"]));
            }
            if (values["exposure"] != null) Field(effect, "Exposure").SetValue(effect, (float)values["exposure"]);
            if (values["linearWhitePoint"] != null) Field(effect, "LinearWhitePoint").SetValue(effect, (float)values["linearWhitePoint"]);
        }

        // A loaded scene without saved tonemapping must not inherit the previous scene's values.
        internal void Reset()
        {
            Owns = false;
            if (_defaults == null) return;
            try { Write(Effect(), _defaults); }
            catch (NotSupportedException) { }
        }
    }
}
