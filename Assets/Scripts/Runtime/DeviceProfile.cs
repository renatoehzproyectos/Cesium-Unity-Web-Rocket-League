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
        /// <summary>"Safe mode" (?safe=1, set automatically by index.html after a memory crash): the smallest budgets of all.</summary>
        public static bool Safe { get; private set; }

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
            Safe = WebBridge.Query("safe") == "1";
        }

        const long MB = 1024 * 1024;

        /// <summary>
        /// Tile cache budget. The WebGL heap is the scarce resource (deviceMemory is capped at 8 by browsers). On phones the cache is only part of the
        /// bill: decoded tile images, meshes and collider cooking live on top of it, so these numbers are deliberately small.
        /// </summary>
        public static long TileCacheBytes => Safe ? 64L * MB
                                           : IsMobile ? (MemoryGB <= 3f ? 96L : MemoryGB >= 6f ? 192L : 128L) * MB
                                           : MemoryGB <= 4f ? 256L * MB
                                           : MemoryGB >= 8f ? 512L * MB : 384L * MB;

        /// <summary>Parallel tile downloads/decodes. Every one in flight holds its decoded images in memory, so phones get far fewer.</summary>
        public static uint SimultaneousLoads => Safe ? 6u : IsMobile ? 12u : Cores >= 8 ? 64u : 40u;
        public static uint DescendantLimit => Safe ? 24u : IsMobile ? 48u : 120u;

        /// <summary>Radius (m) round the car where tiles get a physics collider, and the radius at which they are dropped again.</summary>
        public static float ColliderAddRadius => Safe ? 90f : IsMobile ? 150f : 260f;
        public static float ColliderRemoveRadius => Safe ? 150f : IsMobile ? 250f : 420f;
        public static int ColliderBurst => IsMobile ? 8 : 24;

        /// <summary>Highest render scale offered (and applied) on this device. 2x on a phone screen is millions of extra pixels.</summary>
        public static float MaxRenderScale => Safe ? 0.75f : IsMobile ? 1f : 2f;

        /// <summary>Wasm heap size (MB) at which MemoryGuard starts to shed load. Browsers refuse to grow the heap well before the 2 GB build limit on phones.</summary>
        public static int HeapSoftLimitMB => Safe ? 600 : IsMobile ? (MemoryGB <= 3f ? 700 : MemoryGB >= 6f ? 1200 : 900) : 1700;

        /// <summary>First-run defaults (only used when the user has no saved settings).</summary>
        public static void ApplyFirstRunDefaults(GameSettings s)
        {
            if (IsMobile) s.renderScale = Mathf.Min(s.renderScale, MaxRenderScale);
        }

        public static string Describe() =>
            $"{(IsMobile ? "mobile" : "desktop")}{(Safe ? " (safe)" : "")} · {MemoryGB:0.#} GB · {Cores} cores · dpr {Dpr:0.##} · cache {TileCacheBytes / MB} MB · isolated {(CrossOriginIsolated ? "yes" : "NO")}";
    }
}
