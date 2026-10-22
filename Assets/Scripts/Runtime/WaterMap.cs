using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace RocketWorld
{
    /// <summary>
    /// Invisible water mask. Nothing is drawn: it only answers "is this lat/lon water?".
    /// Source: the standard OpenStreetMap raster tiles (z17, ~1 m/px). Water is painted in one flat colour (#aad3df), so each tile is
    /// downloaded once, classified pixel by pixel, cleaned with a morphological close (fills label text) + open (drops pools / thin streams)
    /// and cached as a bit mask. Natural water only: sea, lakes, wide rivers. Roads/bridges over water are not water, so bridges stay solid.
    /// Override the tile server with ?watertiles=https://host/{z}/{x}/{y}.png (the public OSM server's usage policy forbids heavy use).
    /// </summary>
    public sealed class WaterMap : MonoBehaviour
    {
        public GameRoot root;
        public const int Zoom = 17;
        public string urlTemplate = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
        const int MaxTiles = 48, MaxInflight = 4;
        static readonly Color32 WaterColor = new Color32(170, 211, 223, 255);     // OSM Carto "water"
        const int ColorTol = 5;

        sealed class Tile { public bool[] water; public int size; public bool queued, pending, failed; public float lastUse, retryAt; public int x, y; }
        readonly Dictionary<long, Tile> _tiles = new Dictionary<long, Tile>();
        readonly List<Tile> _queue = new List<Tile>();
        int _inflight;
        long _lastCenter = long.MinValue;

        public bool Enabled => root != null && root.settings != null && root.settings.waterPhysics;
        /// <summary>Tiles around the car are loaded (the mask is trustworthy there).</summary>
        public bool CenterReady { get; private set; }

        void Start()
        {
            var q = WebBridge.Query("watertiles");
            if (!string.IsNullOrEmpty(q) && q.Contains("{x}") && q.Contains("{y}") && q.Contains("{z}")) urlTemplate = q;
        }

        static long Key(int x, int y) => ((long)x << 32) | (uint)y;

        public static void ToTile(double lat, double lon, out int tx, out int ty, out double fx, out double fy)
        {
            double n = 1 << Zoom, latRad = lat * Math.PI / 180.0;
            double x = (lon + 180.0) / 360.0 * n;
            double y = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n;
            tx = (int)Math.Floor(x); ty = (int)Math.Floor(y); fx = x - tx; fy = y - ty;
        }

        Tile Get(int x, int y, bool create)
        {
            long k = Key(x, y);
            if (_tiles.TryGetValue(k, out var t)) { t.lastUse = Time.unscaledTime; return t; }
            if (!create) return null;
            int n = 1 << Zoom; if (y < 0 || y >= n) return null;
            x = ((x % n) + n) % n;
            t = new Tile { x = x, y = y, lastUse = Time.unscaledTime, queued = true };
            _tiles[k] = t; _queue.Add(t);
            if (_tiles.Count > MaxTiles) Evict();
            return t;
        }

        void Evict()
        {
            long worstK = 0; float worst = float.MaxValue; bool any = false;
            foreach (var kv in _tiles) { if (kv.Value.pending || kv.Value.queued) continue; if (kv.Value.lastUse < worst) { worst = kv.Value.lastUse; worstK = kv.Key; any = true; } }
            if (any) _tiles.Remove(worstK);
        }

        /// <summary>True when (lat, lon) in degrees is on water. Unknown / not-yet-downloaded tiles count as land. Also keeps the 3x3 tiles around it loaded.</summary>
        public bool IsWater(double lat, double lon)
        {
            if (!Enabled) return false;
            ToTile(lat, lon, out int tx, out int ty, out double fx, out double fy);
            long ck = Key(tx, ty);
            if (ck != _lastCenter)
            {
                _lastCenter = ck;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) Get(tx + dx, ty + dy, true);
            }
            var t = Get(tx, ty, true);
            CenterReady = t != null && t.water != null;
            if (t == null || t.water == null) return false;
            int px = Mathf.Clamp((int)(fx * t.size), 0, t.size - 1), py = Mathf.Clamp((int)(fy * t.size), 0, t.size - 1);
            return t.water[py * t.size + px];
        }

        void Update()
        {
            if (!Enabled) return;
            for (int i = 0; i < _queue.Count && _inflight < MaxInflight;)
            {
                var t = _queue[i];
                if (t.failed && Time.unscaledTime < t.retryAt) { i++; continue; }
                _queue.RemoveAt(i); t.queued = false; t.pending = true; _inflight++;
                StartCoroutine(Fetch(t));
            }
        }

        IEnumerator Fetch(Tile t)
        {
            string url = urlTemplate.Replace("{z}", Zoom.ToString()).Replace("{x}", t.x.ToString()).Replace("{y}", t.y.ToString());
            using (var req = UnityWebRequestTexture.GetTexture(url))
            {
                req.downloadHandler = new DownloadHandlerTexture(true);       // readable
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    var tex = DownloadHandlerTexture.GetContent(req);
                    if (tex != null && tex.width == tex.height && tex.width >= 64)
                    {
                        t.size = tex.width;
                        t.water = Classify(tex.GetPixels32(), tex.width);
                        t.failed = false;
                    }
                    else { t.failed = true; t.retryAt = Time.unscaledTime + 60f; }
                    if (tex != null) Destroy(tex);
                }
                else { t.failed = true; t.retryAt = Time.unscaledTime + 20f; }
            }
            t.pending = false; _inflight--;
            if (t.failed && t.water == null) { t.queued = true; _queue.Add(t); }
        }

        static bool IsWaterPixel(Color32 c) =>
            Mathf.Abs(c.r - WaterColor.r) <= ColorTol && Mathf.Abs(c.g - WaterColor.g) <= ColorTol && Mathf.Abs(c.b - WaterColor.b) <= ColorTol;

        /// <summary>pixels are bottom-up (Unity textures); the mask is top-down like the tile's y axis.</summary>
        static bool[] Classify(Color32[] px, int n)
        {
            var m = new bool[n * n];
            for (int y = 0; y < n; y++)
            {
                int src = (n - 1 - y) * n, dst = y * n;
                for (int x = 0; x < n; x++) m[dst + x] = IsWaterPixel(px[src + x]);
            }
            int rClose = Mathf.Max(1, n / 128), rOpen = Mathf.Max(1, n / 128);     // 2 px at 256
            m = Erode(Dilate(m, n, rClose), n, rClose);        // close: fills holes from map labels
            m = Dilate(Erode(m, n, rOpen), n, rOpen);          // open: drops pools and streams narrower than ~2*r px
            return m;
        }

        static bool[] Dilate(bool[] a, int n, int r) { var t = new bool[a.Length]; var o = new bool[a.Length]; Pass(a, t, n, r, true, true); Pass(t, o, n, r, false, true); return o; }
        static bool[] Erode(bool[] a, int n, int r) { var t = new bool[a.Length]; var o = new bool[a.Length]; Pass(a, t, n, r, true, false); Pass(t, o, n, r, false, false); return o; }

        // separable box max (dilate) / min (erode) filter; out-of-tile samples clamp to the edge
        static void Pass(bool[] src, bool[] dst, int n, int r, bool horizontal, bool dilate)
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    bool res = !dilate;
                    for (int k = -r; k <= r; k++)
                    {
                        int xx = horizontal ? Mathf.Clamp(x + k, 0, n - 1) : x, yy = horizontal ? y : Mathf.Clamp(y + k, 0, n - 1);
                        bool v = src[yy * n + xx];
                        if (dilate) { if (v) { res = true; break; } } else { if (!v) { res = false; break; } }
                    }
                    dst[y * n + x] = res;
                }
        }
    }
}
