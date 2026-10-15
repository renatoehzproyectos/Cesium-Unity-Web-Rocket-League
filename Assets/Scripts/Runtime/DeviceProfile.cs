using System.Globalization;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>What the browser tells us about the device, turned into streaming/quality budgets (the 60 FPS budget lives here).</summary>
    public static class DeviceProfile
    {
        public static float MemoryGB { get; private set; } = 8f;
        public static int Cores { get; private set; } = 4;
        public static bool IsMobile { get; private set; }
        public static float Dpr { get; private set; } = 1f;
        public static bool CrossOriginIsolated { get; private set; } = true;

        static bool _loaded;

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            var p = (WebBridge.DeviceInfo() ?? "").Split(';');
            if (p.Length >= 5)
            {
                var inv = CultureInfo.InvariantCulture;
                if (float.TryParse(p[0], NumberStyles.Float, inv, out var m)) MemoryGB = m;
                if (int.TryParse(p[1], out var c)) Cores = c;
                IsMobile = p[2] == "1";
                if (float.TryParse(p[3], NumberStyles.Float, inv, out var d)) Dpr = d;
                CrossOriginIsolated = p[4] == "1";
            }
        }

        /// <summary>Tile cache budget. The WebGL heap is the scarce resource; deviceMemory is capped at 8 by browsers.</summary>
        public static long TileCacheBytes => IsMobile ? (MemoryGB <= 3f ? 128L : 192L) * 1024 * 1024
                                           : MemoryGB <= 4f ? 256L * 1024 * 1024
                                           : MemoryGB >= 8f ? 512L * 1024 * 1024 : 384L * 1024 * 1024;

        public static uint SimultaneousLoads => IsMobile ? 20u : Cores >= 8 ? 40u : 24u;   // HTTP/2 multiplexes, so many parallel requests are cheap

        /// <summary>First-run defaults (only used when the user has no saved settings).</summary>
        public static void ApplyFirstRunDefaults(GameSettings s)
        {
            if (!IsMobile) return;
            s.sse = 24f; s.renderScale = Mathf.Min(s.renderScale, 0.75f); s.viewDistance = 900f;
        }

        public static string Describe() =>
            $"{(IsMobile ? "mobile" : "desktop")} · {MemoryGB:0.#} GB · {Cores} cores · dpr {Dpr:0.##} · cache {TileCacheBytes / (1024 * 1024)} MB · isolated {(CrossOriginIsolated ? "yes" : "NO")}";
    }
}
