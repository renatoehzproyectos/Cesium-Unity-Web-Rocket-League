using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using CesiumForUnity;
using RocketWorld.Sim;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Networking;

namespace RocketWorld
{
    /// <summary>
    /// Where is the water? Streams OpenStreetMap vector tiles from OpenFreeMap (free, no API key, no request limit, CORS-enabled, the whole
    /// planet) around the cars and answers "is this point water?" from the real polygons (oceans, seas, lakes, river banks), so the answer
    /// does not depend on how the photogrammetry mesh looks. Tiles are z14 (about 0.5 m per tile unit before OSM's own accuracy).
    /// Also reports bridges / piers (lines over water) so a car driving on one is never turned into a boat.
    /// The first query inside a tile that is still downloading returns <see cref="Cover.Unknown"/> and the caller keeps its current mode.
    /// Data (c) OpenStreetMap contributors, via OpenFreeMap / OpenMapTiles.
    /// </summary>
    public class WaterMap : MonoBehaviour
    {
        public enum Cover { Unknown, Land, Water }

        public CesiumGeoreference georeference;
        public const int Zoom = 14;
        const string TileJsonUrl = "https://tiles.openfreemap.org/planet";                    // TileJSON: the tile URLs are versioned per weekly build
        const string FallbackTemplate = "https://tiles.openfreemap.org/planet/{z}/{x}/{y}.pbf";
        public int maxConcurrent = 3, maxCachedTiles = 48;

        [Serializable] class TileJson { public string[] tiles; public int maxzoom; }
        sealed class Slot { public WaterTile tile; public bool loading; public float retryAt; public float used; }

        readonly Dictionary<long, Slot> _slots = new Dictionary<long, Slot>(64);
        readonly List<long> _queue = new List<long>(16);
        string _template;
        int _active;
        public int Loaded { get { int n = 0; foreach (var s in _slots) if (s.Value.tile != null) n++; return n; } }
        public int Loading => _active;
        public string LastError { get; private set; } = "";

        static long Key(int x, int y) => ((long)x << 32) | (uint)y;

        void Start() { StartCoroutine(LoadTileJson()); }

        IEnumerator LoadTileJson()
        {
            using (var req = UnityWebRequest.Get(TileJsonUrl))
            {
                req.timeout = 10;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var tj = JsonUtility.FromJson<TileJson>(req.downloadHandler.text);
                        if (tj != null && tj.tiles != null && tj.tiles.Length > 0 && tj.tiles[0].Contains("{z}")) _template = tj.tiles[0];
                    }
                    catch (Exception e) { LastError = "tilejson: " + e.Message; }
                }
                else LastError = "tilejson: " + req.error;
            }
            if (_template == null) { _template = FallbackTemplate; Debug.LogWarning("[RocketWorld] water: TileJSON unavailable, using the unversioned tile URL (" + LastError + ")"); }
            else Debug.Log("[RocketWorld] water tiles: " + _template);
        }

        /// <summary>World position (Unity) to longitude / latitude in degrees, through ECEF (exact on any origin shift).</summary>
        public bool LonLat(Vector3 unityPos, out double lon, out double lat)
        {
            lon = lat = 0;
            if (georeference == null) return false;
            double3 e = georeference.TransformUnityPositionToEarthCenteredEarthFixed(new double3(unityPos.x, unityPos.y, unityPos.z));
            Geo.EcefToLonLat(e.x, e.y, e.z, out lon, out lat);
            return !double.IsNaN(lon) && !double.IsNaN(lat);
        }

        /// <summary>Request the 3x3 tiles around a position (callers rate-limit themselves, a few times per second is plenty).</summary>
        public void Prefetch(Vector3 unityPos)
        {
            if (!LonLat(unityPos, out double lon, out double lat)) return;
            Geo.LonLatToTile(lon, lat, Zoom, out double tx, out double ty);
            int cx = (int)Math.Floor(tx), cy = (int)Math.Floor(ty), n = 1 << Zoom;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = ((cx + dx) % n + n) % n, y = cy + dy;
                    if (y < 0 || y >= n) continue;
                    long k = Key(x, y);
                    if (!_slots.TryGetValue(k, out var s)) { _slots[k] = s = new Slot(); }
                    s.used = Time.unscaledTime;
                    if (s.tile == null && !s.loading && Time.unscaledTime >= s.retryAt && !_queue.Contains(k)) _queue.Add(k);
                }
            // nearest tiles first
            _queue.Sort((a, b) => Dist(a, cx, cy).CompareTo(Dist(b, cx, cy)));
        }

        static int Dist(long k, int cx, int cy) { int x = (int)(k >> 32), y = (int)(k & 0xFFFFFFFF); return Math.Abs(x - cx) + Math.Abs(y - cy); }

        void Update()
        {
            if (_template == null) return;
            while (_active < maxConcurrent && _queue.Count > 0)
            {
                long k = _queue[0]; _queue.RemoveAt(0);
                if (_slots.TryGetValue(k, out var s) && s.tile == null && !s.loading) { s.loading = true; _active++; StartCoroutine(Download(k, s)); }
            }
            if (_slots.Count > maxCachedTiles) Evict();
        }

        void Evict()
        {
            long oldest = 0; float t = float.MaxValue;
            foreach (var kv in _slots) if (!kv.Value.loading && kv.Value.used < t) { t = kv.Value.used; oldest = kv.Key; }
            if (t < Time.unscaledTime - 5f) _slots.Remove(oldest);
        }

        IEnumerator Download(long key, Slot slot)
        {
            int x = (int)(key >> 32), y = (int)(key & 0xFFFFFFFF);
            string url = _template.Replace("{z}", Zoom.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 20;
                yield return req.SendWebRequest();
                _active--;
                slot.loading = false;
                if (req.result == UnityWebRequest.Result.Success)
                {
                    try { slot.tile = MvtReader.Read(Inflate(req.downloadHandler.data)); }
                    catch (Exception e) { LastError = "tile " + x + "/" + y + ": " + e.Message; slot.retryAt = Time.unscaledTime + 30f; }
                }
                else if (req.responseCode == 404) slot.tile = WaterTile.Empty;          // no data there: land
                else { LastError = "tile " + x + "/" + y + ": " + req.error; slot.retryAt = Time.unscaledTime + 10f; }
            }
        }

        /// <summary>Browsers inflate Content-Encoding: gzip themselves; native players may hand over the raw gzip bytes.</summary>
        static byte[] Inflate(byte[] d)
        {
            if (d == null || d.Length < 2 || d[0] != 0x1f || d[1] != 0x8b) return d;
            using (var src = new MemoryStream(d)) using (var gz = new GZipStream(src, CompressionMode.Decompress)) using (var dst = new MemoryStream())
            { gz.CopyTo(dst); return dst.ToArray(); }
        }

        bool TileAt(Vector3 unityPos, out WaterTile tile, out double px, out double py)
        {
            tile = null; px = py = 0;
            if (!LonLat(unityPos, out double lon, out double lat)) return false;
            Geo.LonLatToTile(lon, lat, Zoom, out double tx, out double ty);
            int ix = (int)Math.Floor(tx), iy = (int)Math.Floor(ty);
            if (!_slots.TryGetValue(Key(ix, iy), out var s) || s.tile == null) return false;
            s.used = Time.unscaledTime;
            tile = s.tile;
            px = (tx - ix) * tile.extent; py = (ty - iy) * tile.extent;
            return true;
        }

        /// <summary>Is the point water, land, or not known yet?</summary>
        public Cover Query(Vector3 unityPos, out WaterKind kind)
        {
            kind = WaterKind.Other;
            if (!TileAt(unityPos, out var tile, out double px, out double py)) return Cover.Unknown;
            return tile.IsWater(px, py, out kind) ? Cover.Water : Cover.Land;
        }

        /// <summary>True if a bridge or pier passes within <paramref name="meters"/> of the point (so it must not be treated as open water).</summary>
        public bool NearSolid(Vector3 unityPos, float meters)
        {
            if (!TileAt(unityPos, out var tile, out double px, out double py) || tile.solidLines.Count == 0) return false;
            if (!LonLat(unityPos, out _, out double lat)) return false;
            return tile.NearSolid(px, py, meters / Geo.MetresPerUnit(lat, Zoom, tile.extent));
        }
    }
}
