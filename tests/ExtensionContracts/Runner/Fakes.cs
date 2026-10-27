using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace StudioMcp.Bridge
{
    internal static class MaterialCommands
    {
        internal static Func<string, Type> Resolve;
        internal static Type FindType(string name) => Resolve(name);
    }
    internal sealed class Plugin
    {
        internal string Session = "contract-session", OutputRoot;
        internal IEnumerator Pending;
        internal bool FailStart, NullStart;
        internal object StartCoroutine(IEnumerator value)
        {
            if (FailStart) throw new InvalidOperationException("fixture start failure");
            if (NullStart) return null;
            Pending = value;
            return value;
        }
        internal void Drain() { while (Pending.MoveNext()) { } }
    }
}
namespace Screencap
{
    public enum AlphaMode { None, blackout, rgAlpha, UnexposedNativeMode }
    public enum LegacyAlphaMode { None, blackout }
    public static class ScreenshotManager
    {
        public static int Pre, Post;
        public static AlphaMode? LastMode;
        public static bool FailPre, FailRender, FailPost, NullRender, FailDepth;
        public static string Extension = ".rfloat";
        public static void Reset()
        { Pre = Post = 0; LastMode = null; FailPre = FailRender = FailPost = NullRender = FailDepth = false; Extension = ".rfloat"; }
        public static void FirePreCapture() { Pre++; if (FailPre) throw new Exception("fixture pre failure"); }
        public static void FirePostCapture() { Post++; if (FailPost) throw new Exception("fixture post failure"); }
        public static RenderTexture CaptureRender(int? width, int? height, int? downscaling, AlphaMode? mode)
        {
            LastMode = mode;
            if (FailRender) throw new Exception("fixture render failure");
            return NullRender ? null : RenderTexture.GetTemporary(width.Value, height.Value, 0, RenderTextureFormat.ARGB32);
        }
        public static bool ExportOfflineReShadeInputs(string color, string depth, string metadata, int? width, int? height, int? downscaling)
        {
            if (FailDepth) return false;
            File.WriteAllBytes(color, new byte[] { 1 }); File.WriteAllBytes(depth, new byte[] { 2 }); File.WriteAllText(metadata, "{}");
            return true;
        }
        public static string GetOfflineReShadeDepthExtension() => Extension;
    }
    public static class LegacyScreenshotManager
    {
        public static void FirePreCapture() => ScreenshotManager.FirePreCapture();
        public static void FirePostCapture() => ScreenshotManager.FirePostCapture();
        public static RenderTexture CaptureRender(int? width, int? height, int? downscaling, LegacyAlphaMode? mode)
            => ScreenshotManager.CaptureRender(width, height, downscaling, (AlphaMode?)(int?)mode);
    }
}
