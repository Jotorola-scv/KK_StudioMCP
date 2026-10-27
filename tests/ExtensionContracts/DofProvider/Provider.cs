using System;
using System.Text.Json;

// Minimal public contract doubles, not copies of the rendering plugin.
namespace TomSceneEffectsPlugin
{
    public static class Plugin
    {
        public static int Scene = -1, Global, Writes;
        public static bool Ready = true, FailScene, FailRead;
        public static string JsonOverride;
        public static string StateJson()
        {
            if (FailRead) throw new InvalidOperationException("fixture state failure");
            return JsonOverride ?? JsonSerializer.Serialize(new { available = Ready, scenePolicy = Scene, globalMode = Global });
        }
        public static void ApplyScenePolicy(int value)
        { if (FailScene) throw new InvalidOperationException("fixture setter failure"); Scene = value; Writes++; }
        public static void ApplyGlobalMode(int value) { Global = value; Writes++; }
        public static void Reset() { Scene = -1; Global = Writes = 0; Ready = true; FailScene = FailRead = false; JsonOverride = null; }
    }
    public static class ReadOnlyVersion
    {
        public static string StateJson() => Plugin.StateJson();
        public static void ApplyGlobalMode(int value) => Plugin.ApplyGlobalMode(value);
    }
    public static class WrongStateSignature { public static int StateJson() => 0; }
}
