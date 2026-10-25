using CesiumForUnity;
using RocketWorld.Sim;
using UnityEngine;
using UnityEngine.Profiling;

namespace RocketWorld
{
    /// <summary>
    /// View-driven tile streaming. Tiles outside the camera view are not rendered (a hole until they are looked at again) and no longer
    /// requested; a tile that finished loading stays in Cesium's LRU cache, so looking back at it shows it instantly, without a download.
    ///
    /// How it is built on Cesium for Unity: the main camera drives tile selection (frustum culling hides what is off screen, nothing outside
    /// every camera is loaded); the old "load everything in a radius" streaming cameras shrink to a small physics ring so the colliders under the
    /// car still exist; the cache budget is raised well above the default and follows the memory actually used (it shrinks under pressure, grows
    /// when there is room), so a finished tile is only dropped when the device needs the memory. Switch off with <c>?viewcull=0</c>.
    /// </summary>
    public class TileViewPolicy : MonoBehaviour
    {
        public GameRoot root;
        public Cesium3DTileset tileset;
        public long Budget { get; private set; }
        public long MaxBudget { get; private set; }
        public float Ring { get; private set; } = 150f;
        public long AllocatedBytes { get; private set; }
        long _min, _heapLimit; float _next;

        public string Info => $"tile cache {Budget / (1024 * 1024)}/{MaxBudget / (1024 * 1024)} MB   heap {AllocatedBytes / (1024 * 1024)} MB   ring {Ring:0} m";

        void Start()
        {
            long baseBytes = DeviceProfile.TileCacheBytes;
            _heapLimit = (long)(DeviceProfile.MemoryGB * 1024f * 1024f * 1024f * (DeviceProfile.IsMobile ? 0.30f : 0.40f));   // what we allow the whole page's heap to use
            _min = baseBytes;
            MaxBudget = TileCachePolicy.MaxBudget(baseBytes, DeviceProfile.IsMobile, _heapLimit);
            if (MaxBudget < baseBytes) MaxBudget = baseBytes;
            Budget = System.Math.Min(MaxBudget, baseBytes * 2);
            Apply();
            Debug.Log("[RocketWorld] view-driven tiles: cache " + Budget / (1024 * 1024) + " MB (max " + MaxBudget / (1024 * 1024) + " MB), physics ring " + Ring + " m");
        }

        void Update()
        {
            if (tileset == null) return;
            var car = root != null ? root.carCtl : null;
            Ring = TileCachePolicy.PhysicsRing(car != null ? car.SpeedMS : 0f);
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;
            AllocatedBytes = Profiler.GetTotalAllocatedMemoryLong();
            long b = TileCachePolicy.Next(Budget, AllocatedBytes, _heapLimit, _min, MaxBudget);
            if (b != Budget) { Budget = b; Apply(); }
        }

        void Apply() { if (tileset != null) tileset.maximumCachedBytes = Budget; }
    }
}
