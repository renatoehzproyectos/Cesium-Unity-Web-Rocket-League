using System;
using System.Collections.Generic;

namespace RocketWorld.Sim.Roads
{
    /// <summary>Numbers and bookkeeping for proximity download, the persistent cache and memory limits (engine-free so tests can run it).</summary>
    public static class RoadCachePolicy
    {
        public const int ChunkZoom = 15;            // build unit (z14 files are split in four)

        /// <summary>Tiles (z14) whose area touches a circle of radius r around a point, nearest first.</summary>
        public static List<(int x, int y, double d)> TilesAround(double lon, double lat, double radiusM, int z = RoadTiles.Zoom)
        {
            var list = new List<(int, int, double)>();
            double dLat = radiusM / 111320.0, dLon = radiusM / (111320.0 * Math.Max(0.05, Math.Cos(lat * Math.PI / 180.0)));
            RoadTiles.TileOf(lon - dLon, Math.Min(85, lat + dLat), z, out int x0, out int y0); RoadTiles.TileOf(lon + dLon, Math.Max(-85, lat - dLat), z, out int x1, out int y1);
            int n = 1 << z; if (x1 < x0) x1 += n;
            double mE = 111320.0 * Math.Cos(lat * Math.PI / 180.0), mN = 110574.0;
            for (int y = y0; y <= y1; y++)
                for (int xx = x0; xx <= x1; xx++)
                {
                    int x = xx % n; RoadTiles.TileBounds(z, x, y, out double w, out double s, out double e, out double nn);
                    double lo = lon, mid = (w + e) / 2; while (lo - mid > 180) lo -= 360; while (mid - lo > 180) lo += 360;
                    double cx = Math.Max(w, Math.Min(e, lo)), cy = Math.Max(s, Math.Min(nn, lat));
                    double dx = (cx - lo) * mE, dy = (cy - lat) * mN, d = Math.Sqrt(dx * dx + dy * dy);
                    if (d <= radiusM) list.Add((x, y, d));
                }
            list.Sort((a, b) => a.Item3.CompareTo(b.Item3)); return list;
        }

        /// <summary>Download radius (m) by speed: 1.2 km at rest up to 3 km at speed, scaled by the memory level, never below one tile ring.</summary>
        public static double FetchRadius(float speed, int memLevel, bool mobile)
        {
            double r = 1200 + Math.Min(Math.Max(speed, 0f), 80f) * 22.0; if (mobile) r *= 0.7; return Math.Max(700, r * MemMul(memLevel));
        }
        public static double MemMul(int level) => level <= 0 ? 1.0 : level == 1 ? 0.75 : level == 2 ? 0.5 : 0.35;

        /// <summary>Radius (m) inside which chunk meshes are kept resident; beyond +25 % they are freed (hysteresis).</summary>
        public static double ResidentRadius(int memLevel, bool mobile) => (mobile ? 900 : 1600) * MemMul(memLevel);

        /// <summary>Radius (m) inside which a chunk gets its MeshCollider (same idea as TileColliderBudget.addRadius, a bit larger so roads are ready first).</summary>
        public static double ColliderRadius(double tileAdd, double mul) => tileAdd * mul + 40.0;

        public static long DiskBudgetBytes(bool mobile, int memLevel) => (mobile ? 48L : 160L) * 1024 * 1024 / (memLevel >= 2 ? 2 : 1);

        /// <summary>A cached raw tile is current only when its release equals the manifest's (processed data is tied to the release AND <see cref="RoadChunkBuilder.ProcVersion"/>).</summary>
        public static bool IsCurrent(string cachedRelease, string manifestRelease) => !string.IsNullOrEmpty(manifestRelease) && cachedRelease == manifestRelease;

        public static string FileName(string kind, string release, int z, int x, int y) => $"{kind}_{release}_{z}_{x}_{y}.bin";
    }

    /// <summary>LRU over cache entries with pinned keys (saved zones are never evicted). Sizes in bytes; <see cref="Evict"/> returns what to delete.</summary>
    public sealed class ByteLru
    {
        sealed class E { public long size; public long used; public bool pinned; }
        readonly Dictionary<string, E> _d = new Dictionary<string, E>(); long _clock, _total;
        public long Total => _total; public int Count => _d.Count;
        public void Touch(string key, long size, bool pinned = false)
        {
            if (!_d.TryGetValue(key, out var e)) { e = new E(); _d[key] = e; } else _total -= e.size;
            e.size = size; e.used = ++_clock; e.pinned |= pinned; _total += size;
        }
        public void Pin(string key, bool p) { if (_d.TryGetValue(key, out var e)) e.pinned = p; }
        public void Remove(string key) { if (_d.TryGetValue(key, out var e)) { _total -= e.size; _d.Remove(key); } }
        public List<string> Evict(long maxBytes)
        {
            var o = new List<string>(); if (_total <= maxBytes) return o;
            var all = new List<KeyValuePair<string, E>>(_d); all.Sort((a, b) => a.Value.used.CompareTo(b.Value.used));
            foreach (var kv in all) { if (_total <= maxBytes) break; if (kv.Value.pinned) continue; o.Add(kv.Key); _total -= kv.Value.size; _d.Remove(kv.Key); }
            return o;
        }
    }
}
