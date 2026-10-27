using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudioMcp.Bridge
{
    // Named public facades only; never expose arbitrary reflection to MCP clients.
    internal static class DofCompatibilityCommands
    {
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
        private const long MaxSnapshotPixels = 16777216;

        private static JObject Unavailable(string code, string reason) => new JObject
        { ["available"] = false, ["code"] = code, ["reason"] = reason };

        private static MethodInfo Static(Type type, string name, Type result, params Type[] arguments)
        {
            var method = type.GetMethod(name, PublicStatic, null, arguments, null);
            return method != null && !method.ContainsGenericParameters && method.ReturnType == result ? method : null;
        }

        private static Type Provider()
        {
#if KK
            return null;
#else
            var type = MaterialCommands.FindType("TomSceneEffectsPlugin.Plugin");
            return type != null && type.Assembly.GetName().Name == "KKS_TomSceneEffects" ? type : null;
#endif
        }

        private static JObject ReadState(Type type)
        {
            var read = Static(type, "StateJson", typeof(string));
            if (read == null) return Unavailable("provider_incompatible", "DOF provider has no compatible public StateJson().");
            try
            {
                var state = JObject.Parse((string)read.Invoke(null, null));
                if (state["available"] == null || state["available"].Type != JTokenType.Boolean)
                    return Unavailable("provider_incompatible", "DOF state has no boolean available field.");
                state["mcp"] = new JObject
                {
                    ["assembly"] = type.Assembly.GetName().Name,
                    ["assemblyVersion"] = type.Assembly.GetName().Version.ToString(),
                    ["scenePolicySupported"] = Static(type, "ApplyScenePolicy", typeof(void), typeof(int)) != null,
                    ["globalModeSupported"] = Static(type, "ApplyGlobalMode", typeof(void), typeof(int)) != null
                };
                return state;
            }
            catch (Exception error)
            {
                return Unavailable("provider_unavailable", "Cannot read DOF provider state: " + error.GetBaseException().Message);
            }
        }

        internal static JObject State()
        {
            var type = Provider();
            if (type != null) return ReadState(type);
#if KK
            return Unavailable("kks_only", "The optional shared DOF control is KKS-only.");
#else
            return Unavailable("provider_missing", "Optional KKS Tom Scene Effects provider not installed.");
#endif
        }

        private static int? Mode(JToken value, bool scene)
        {
            if (value == null || value.Type == JTokenType.Null) return null;
            if (value.Type != JTokenType.String) throw new ArgumentException("DOF mode must be a named string.");
            var name = ((string)value).Trim();
            if (scene && string.Equals(name, "Inherit", StringComparison.OrdinalIgnoreCase)) return -1;
            if (string.Equals(name, "Native", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(name, "BeforeTransparents", StringComparison.OrdinalIgnoreCase)) return 1;
            throw new ArgumentException("Use Native or BeforeTransparents; scenePolicy also allows Inherit.");
        }

        internal static JObject Apply(JObject args)
        {
            var scene = Mode(args["scenePolicy"], true);
            var global = Mode(args["globalMode"], false);
            if (!scene.HasValue && !global.HasValue) throw new ArgumentException("scenePolicy or globalMode required.");
            var type = Provider();
            if (type == null) throw new NotSupportedException((string)State()["reason"]);
            // Resolve all requested methods before making the first provider-owned change.
            var setScene = scene.HasValue ? Static(type, "ApplyScenePolicy", typeof(void), typeof(int)) : null;
            var setGlobal = global.HasValue ? Static(type, "ApplyGlobalMode", typeof(void), typeof(int)) : null;
            if ((scene.HasValue && setScene == null) || (global.HasValue && setGlobal == null))
                throw new NotSupportedException("Installed DOF provider does not support the requested public setter signature.");
            var before = ReadState(type);
            if (!(bool)before["available"])
                throw new InvalidOperationException("DOF provider is unavailable; inspect dof_compatibility_get before writing.");
            try
            {
                if (global.HasValue) setGlobal.Invoke(null, new object[] { global.Value });
                if (scene.HasValue) setScene.Invoke(null, new object[] { scene.Value });
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("DOF provider setter failed; changes may be partially applied. Inspect state before retrying. "
                    + error.GetBaseException().Message, error);
            }
            var after = ReadState(type);
            if (!(bool)after["available"])
                throw new InvalidOperationException("DOF setters ran, but state readback is unavailable. Inspect state before retrying.");
            return new JObject { ["before"] = before, ["after"] = after };
        }

        internal static JObject Snapshot(string outputRoot, bool captureBusy)
        {
            if (captureBusy) throw new InvalidOperationException("Capture is in progress; buffer readback is temporarily blocked.");
#if KK
            return Unavailable("kks_only", "The optional BSDF scene-buffer snapshot is KKS-only.");
#else
            var camera = Camera.main;
            if (!camera) return Unavailable("camera_unavailable", "No current main camera.");
            var type = MaterialCommands.FindType("TomBSDFRuntime.TomBSDFSceneService");
            if (type == null || type.Assembly.GetName().Name != "TomBSDF.Runtime")
                return Unavailable("provider_missing", "Optional BSDF scene service unavailable.");
            object snapshot;
            FieldInfo colorField;
            try
            {
                var current = type.GetProperty("Current", PublicStatic);
                var buffersProperty = type.GetProperty("Buffers", PublicInstance);
                if (current == null || current.GetGetMethod() == null || buffersProperty == null || buffersProperty.GetGetMethod() == null)
                    return Unavailable("provider_incompatible", "BSDF service has no compatible Current/Buffers getters.");
                var service = current.GetValue(null, null);
                if (service == null) return Unavailable("service_unavailable", "No existing BSDF scene service; none was created.");
                var buffers = buffersProperty.GetValue(service, null);
                if (buffers == null) return Unavailable("buffers_unavailable", "No existing scene-buffer provider.");
                var methods = buffers.GetType().GetMethods(PublicInstance).Where(method =>
                {
                    var p = method.GetParameters();
                    return method.Name == "TryGetSnapshot" && !method.ContainsGenericParameters && method.ReturnType == typeof(bool)
                        && p.Length == 2 && p[0].ParameterType == typeof(Camera) && p[1].IsOut && p[1].ParameterType.IsByRef;
                }).ToArray();
                if (methods.Length != 1) return Unavailable("provider_incompatible", "No compatible public TryGetSnapshot(Camera, out snapshot).");
                var methodInfo = methods[0];
                var snapshotType = methodInfo.GetParameters()[1].ParameterType.GetElementType();
                colorField = snapshotType.GetField("color", PublicInstance);
                if (!snapshotType.IsValueType || colorField == null || !typeof(RenderTexture).IsAssignableFrom(colorField.FieldType))
                    return Unavailable("provider_incompatible", "Snapshot has no compatible public color field.");
                var arguments = new object[] { camera, Activator.CreateInstance(snapshotType) };
                if (!(bool)methodInfo.Invoke(buffers, arguments))
                    return Unavailable("snapshot_unavailable", "No completed main-camera snapshot; no new buffer or render was requested.");
                snapshot = arguments[1];
            }
            catch (Exception error)
            {
                return Unavailable("provider_unavailable", "Cannot inspect BSDF snapshot: " + error.GetBaseException().Message);
            }

            var color = (RenderTexture)colorField.GetValue(snapshot);
            if (!color || !color.IsCreated()) return Unavailable("color_unavailable", "Completed snapshot has no live color buffer.");
            var owner = snapshot.GetType().GetField("cameraID", PublicInstance);
            if (owner != null && (owner.FieldType != typeof(int) || (int)owner.GetValue(snapshot) != camera.GetInstanceID()))
                return Unavailable("camera_mismatch", "Snapshot belongs to a different camera.");
            if (color.width <= 0 || color.height <= 0 || color.width > 7680 || color.height > 7680
                || (long)color.width * color.height > MaxSnapshotPixels || color.antiAliasing != 1)
                return Unavailable("readback_limit", "Readback requires a resolved color buffer of at most 7680 per axis and 16777216 pixels.");

            var width = color.width;
            var height = color.height;
            var previous = RenderTexture.active;
            Texture2D read = null;
            var path = Path.Combine(outputRoot, "dof-buffer-" + Guid.NewGuid().ToString("N") + ".png");
            var created = false;
            try
            {
                RenderTexture.active = color;
                read = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                read.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                read.Apply(false, false);
                var bytes = read.EncodeToPNG();
                if (bytes == null || bytes.Length == 0) throw new IOException("Snapshot PNG encoding returned no data.");
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                {
                    created = true;
                    stream.Write(bytes, 0, bytes.Length);
                }
                var result = new JObject
                {
                    ["available"] = true, ["path"] = path, ["width"] = width, ["height"] = height,
                    ["bytes"] = bytes.Length, ["format"] = color.format.ToString(), ["sourceSrgb"] = color.sRGB,
                    ["freshCapture"] = false,
                    ["scope"] = "Last completed main-camera opaque color buffer, clipped to LDR PNG; no tone mapping, new render or buffer demand. Not a viewport screenshot.",
                    ["provider"] = State()
                };
                var sequence = snapshot.GetType().GetField("sequence", PublicInstance);
                if (sequence != null && sequence.FieldType == typeof(int)) result["sequence"] = (int)sequence.GetValue(snapshot);
                return result;
            }
            catch
            {
                if (created) try { File.Delete(path); } catch (IOException) { }
                throw;
            }
            finally
            {
                RenderTexture.active = previous;
                if (read) UnityEngine.Object.Destroy(read);
            }
#endif
        }
    }
}
