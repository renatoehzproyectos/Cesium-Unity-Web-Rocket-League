using UnityEngine;
using UnityEngine.Profiling;

namespace RocketWorld
{
    /// <summary>
    /// Watches the wasm heap and backs off (smaller tile cache, fewer colliders, lower render scale, a GC) before the browser refuses to
    /// grow it: on phones a failed allocation ends as "RuntimeError: memory access out of bounds" and the whole page stops.
    /// </summary>
    public class MemoryGuard : MonoBehaviour
    {
        public GameRoot root;
        public readonly MemoryGuardLogic logic = new MemoryGuardLogic();
        public int Level => logic.Level;
        public int HeapMB { get; private set; }
        float _next, _lastClean;

        void Update()
        {
            if (root == null || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            int web = WebBridge.HeapMB();
            // outside the browser there is no wasm heap: use Unity's own reserved memory so the Editor shows something sensible
            HeapMB = web > 0 ? web : (int)(Profiler.GetTotalReservedMemoryLong() / (1024 * 1024));
            if (!logic.Feed(HeapMB, DeviceProfile.HeapSoftLimitMB, 1f)) return;
            root.ApplyMemoryLevel(logic.Level);
            if (logic.Level >= 2 && Time.unscaledTime - _lastClean > 20f)
            {
                _lastClean = Time.unscaledTime;
                System.GC.Collect();
                Resources.UnloadUnusedAssets();
            }
        }
    }
}
