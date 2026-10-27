using UnityEngine;

namespace TomBSDFRuntime
{
    public struct TomSceneBufferSnapshot { public RenderTexture color; public int cameraID, sequence; }
    public sealed class TomBSDFSceneService
    {
        public static TomBSDFSceneService Current { get; set; }
        public object Buffers { get; set; }
    }
    public sealed class Buffers
    {
        public bool Ready = true;
        public int Calls;
        public TomSceneBufferSnapshot Snapshot;
        public bool TryGetSnapshot(Camera camera, out TomSceneBufferSnapshot snapshot)
        { Calls++; snapshot = Snapshot; return Ready; }
    }
    public sealed class WrongBuffers
    {
        public bool TryGetSnapshot(Camera camera, out int snapshot) { snapshot = 0; return true; }
    }
}
