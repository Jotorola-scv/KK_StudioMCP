using System;

// Test doubles only. No Unity or game process is loaded by this harness.
namespace UnityEngine
{
    public class Object
    {
        public bool destroyed;
        public static implicit operator bool(Object value) => value != null && !value.destroyed;
        public static void Destroy(Object value) { value.destroyed = true; if (value is Texture2D) Texture2D.Live--; }
    }
    public class Texture : Object { public int width, height; }
    public enum TextureFormat { RGBA32 }
    public enum RenderTextureFormat { ARGB32 }
    public class Camera : Object { public static Camera main; public int id = 17; public int GetInstanceID() => id; }
    public class WaitForEndOfFrame { }
    public struct Rect { public Rect(float x, float y, float width, float height) { } }
    public class RenderTexture : Texture
    {
        public static RenderTexture active;
        public static int Released;
        public int antiAliasing = 1;
        public bool created = true, sRGB;
        public RenderTextureFormat format;
        public bool IsCreated() => created;
        public static RenderTexture GetTemporary(int width, int height, int depth, RenderTextureFormat format)
            => new RenderTexture { width = width, height = height, format = format };
        public static void ReleaseTemporary(RenderTexture value) { Released++; value.destroyed = true; }
    }
    public class Texture2D : Texture
    {
        public static int Live;
        public static bool FailRead, FailEncode;
        public Texture2D(int width, int height, TextureFormat format, bool mip, bool linear = false)
        { this.width = width; this.height = height; Live++; }
        public void ReadPixels(Rect area, int x, int y) { if (FailRead) throw new InvalidOperationException("fixture read failure"); }
        public void Apply(bool mip, bool unreadable) { }
        public byte[] EncodeToPNG()
        { if (FailEncode) throw new InvalidOperationException("fixture encode failure"); return new byte[] { 1, 2, 3 }; }
        public bool LoadImage(byte[] bytes) => bytes.Length != 0;
    }
    public static class Graphics
    {
        public static bool Fail;
        public static void Blit(Texture source, RenderTexture target) { if (Fail) throw new InvalidOperationException("fixture blit failure"); }
    }
}
