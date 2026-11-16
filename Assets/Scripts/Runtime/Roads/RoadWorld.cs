using System;
using System.Collections;
using System.Collections.Generic;
using CesiumForUnity;
using RocketWorld.Sim;
using RocketWorld.Sim.Roads;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Networking;

namespace RocketWorld
{
    /// <summary>
    /// Independent, smooth road colliders under the Photorealistic 3D Tiles (the tiles stay the only visuals).
    /// Pipeline: manifest (release) -> proximity download of z14 road tiles (Overture-derived, see tools/roads) -> persistent cache -> z15 chunks ->
    /// centreline smoothing + height fit against the tile colliders (RoadProfile) -> ribbon mesh with intersection patches -> MeshCollider on PhysicsSetup.RoadLayer.
    /// TileColliderBudget asks <see cref="TryCarve"/> to cut the road corridor out of its collider copies (buildings, walls, kerbs and overpasses stay).
    /// Everything is time-sliced on the main thread (WebGL) and bounded by DeviceProfile / MemoryGuard levels. If no manifest is reachable the system
    /// switches itself off and the game behaves exactly as before.
    /// </summary>
    public class RoadWorld : MonoBehaviour
    {
        [Serializable] class Manifest { public string release; public int schema; public int tileZoom; public string tiles; public string attribution; }
        sealed class TileSt { public RoadTileData data; public bool loading; public float retryAt, used; public int fails; public bool pinned; }
        sealed class Chunk
        {
            public long key; public int x, y; public double lon, lat; public RoadFrame frame; public List<RoadSegment> segs = new List<RoadSegment>();
            public double minE = 1e9, maxE = -1e9, minN = 1e9, maxN = -1e9;
            public Dictionary<uint, float[]> solved = new Dictionary<uint, float[]>(); public bool procTried;
            public RoadChunkBuilder builder; public ChunkResult res; public GameObject go; public Mesh mesh, vmesh; public MeshCollider col; public bool viewOn, viewDebug;
            public bool dirty = true, saved; public float retryAt; public int pending, retries, lastAdded; public float refUp; public long tileKey;
            public Bounds LocalBox => new Bounds(new Vector3((res.minX + res.maxX) * .5f, (res.minY + res.maxY) * .5f, (res.minZ + res.maxZ) * .5f), new Vector3(res.maxX - res.minX, res.maxY - res.minY, res.maxZ - res.minZ));
        }

        public GameRoot root; public CesiumGeoreference georeference; public TileColliderBudget budget; public Transform focus; public List<Transform> extraFoci;
        public int memLevel;                      // MemoryGuard level, set by GameRoot.ApplyMemoryLevel
        public bool debugView;                    // ?roads=debug draws the collider meshes
        public bool Enabled { get; private set; }
        public int Epoch { get; private set; }
        public string Release => _release ?? "";
        public string Status { get; private set; } = "roads: starting";

        string _base, _tilesTemplate, _release; int _tileZoom = RoadTiles.Zoom;
        readonly RoadStore _store = new RoadStore(); readonly NodeHeights _nodes = new NodeHeights(); bool _nodesDirty; float _nodesSaveAt;
        readonly Dictionary<long, TileSt> _tiles = new Dictionary<long, TileSt>(); readonly Dictionary<long, Chunk> _chunks = new Dictionary<long, Chunk>();
        readonly Dictionary<uint, long> _segOwner = new Dictionary<uint, long>();
        readonly List<(Bounds b, int epoch)> _changes = new List<(Bounds, int)>(32);
        readonly List<(double lon, double lat)> _fociLL = new List<(double, double)>(4); readonly List<Vector3> _fociPos = new List<Vector3>(4);
        readonly List<(int x, int y, bool pin)> _fetchQueue = new List<(int, int, bool)>(32); readonly List<Chunk> _buildQueue = new List<Chunk>(16);
        readonly List<(SavedZones.Zone z, List<long> keys)> _zones = new List<(SavedZones.Zone, List<long>)>();
        int _active; float _nextScan, _speedT; Vector3 _lastFocus; float _speed; Chunk _job;
        Material _debugMat; public int ColliderCount { get; private set; } public int PendingSegments { get; private set; }

        public string Info => Enabled ? $"roads {_release}: {_tiles.Count} tiles, {_chunks.Count} chunks, {ColliderCount} colliders, {PendingSegments} pending, queue {_fetchQueue.Count + _buildQueue.Count}, cache {_store.Bytes / 1048576f:0.0} MB" : Status;

        // ---------------------------------------------------------------------------------------------- start

        void Start()
        {
            string q = WebBridge.Query("roads");
            if (q == "0") { Status = "roads: off (?roads=0)"; return; }
            debugView = q == "debug";
            _base = !string.IsNullOrEmpty(q) && q.StartsWith("http") ? q : WebBridge.BaseUrl() + "roads/";
            if (!_base.EndsWith("/")) _base += "/";
            string nodes = "nodes_" + "all.bin"; var nb = _store.Read(nodes); if (nb != null) _nodes.Load(nb);
            SavedZones.Changed += OnZones;
            StartCoroutine(LoadManifest());
        }

        void OnDestroy() { SavedZones.Changed -= OnZones; SaveNodes(true); foreach (var c in _chunks.Values) Free(c); }

        IEnumerator LoadManifest()
        {
            string cacheName = "manifest.bin"; Manifest m = null;
            using (var req = UnityWebRequest.Get(_base + "manifest.json"))
            {
                req.timeout = 10; yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) { try { m = JsonUtility.FromJson<Manifest>(req.downloadHandler.text); if (m != null && !string.IsNullOrEmpty(m.release)) _store.Write(cacheName, req.downloadHandler.data); } catch (Exception) { m = null; } }
            }
            if (m == null || string.IsNullOrEmpty(m.release))     // offline: last manifest we saw
            {
                var cb = _store.Read(cacheName); if (cb != null) { try { m = JsonUtility.FromJson<Manifest>(System.Text.Encoding.UTF8.GetString(cb)); } catch (Exception) { } }
            }
            if (m == null || string.IsNullOrEmpty(m.release) || m.schema != 1)
            {
                // No tiles of our own published: build them on the fly from OpenStreetMap (OpenFreeMap vector tiles, free, no key), nothing to generate or upload.
                yield return LoadOfm();
                if (!Enabled) { Status = "roads: no road data source"; Debug.Log("[RocketWorld] " + Status); }
                else OnZones();
                yield break;
            }
            _release = m.release; _tileZoom = m.tileZoom > 0 ? m.tileZoom : RoadTiles.Zoom;
            _tilesTemplate = string.IsNullOrEmpty(m.tiles) ? "tiles/{release}/{z}/{x}/{y}.rwr" : m.tiles;
            if (_tileZoom != RoadTiles.Zoom) { Status = "roads: unsupported tile zoom " + _tileZoom; yield break; }
            Enabled = true; Debug.Log("[RocketWorld] roads release " + _release + (string.IsNullOrEmpty(m.attribution) ? "" : " - " + m.attribution));
            OnZones();
        }

        // ---------------------------------------------------------------------------------------------- automatic source (OpenStreetMap through OpenFreeMap)

        const string OfmTileJson = "https://tiles.openfreemap.org/planet", OfmFallback = "https://tiles.openfreemap.org/planet/{z}/{x}/{y}.pbf";
        bool _ofm; string _ofmTemplate;
        /// <summary>Credit for the data in use (shown in Settings): OSM is ODbL, the tile schema is OpenMapTiles.</summary>
        public string Attribution => _ofm ? "Roads: (c) OpenStreetMap contributors (ODbL), OpenMapTiles, via OpenFreeMap" : "";

        IEnumerator LoadOfm()
        {
            const string cacheName = "ofm_tilejson.bin"; string template = null;
            using (var req = UnityWebRequest.Get(OfmTileJson))
            {
                req.timeout = 10; yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) { template = OfmTemplate(req.downloadHandler.text); if (template != null) _store.Write(cacheName, req.downloadHandler.data); }
            }
            if (template == null) { var cb = _store.Read(cacheName); if (cb != null) template = OfmTemplate(System.Text.Encoding.UTF8.GetString(cb)); }   // offline: last URL we saw
            if (template == null) template = OfmFallback;                                                                                                      // unversioned URL still serves the latest planet
            _ofmTemplate = template; _ofm = true; _tileZoom = RoadTiles.Zoom;
            _release = RoadMvt.ReleasePrefix + DateTime.UtcNow.ToString("yyyy-MM");                  // monthly refresh: road networks change slowly, and one name keeps the cache small
            Enabled = true; Debug.Log("[RocketWorld] roads: OpenStreetMap via OpenFreeMap, release " + _release + " (" + template + ")");
        }

        static string OfmTemplate(string json)
        {
            try { var tj = JsonUtility.FromJson<OfmTileJsonData>(json); if (tj != null && tj.tiles != null && tj.tiles.Length > 0 && tj.tiles[0].Contains("{z}")) return tj.tiles[0]; } catch (Exception) { }
            return null;
        }
        [Serializable] class OfmTileJsonData { public string[] tiles; }

        static byte[] Inflate(byte[] d)          // browsers inflate gzip themselves; native players may hand over the raw bytes
        {
            if (d == null || d.Length < 2 || d[0] != 0x1f || d[1] != 0x8b) return d;
            using (var src = new System.IO.MemoryStream(d)) using (var gz = new System.IO.Compression.GZipStream(src, System.IO.Compression.CompressionMode.Decompress)) using (var dst = new System.IO.MemoryStream())
            { gz.CopyTo(dst); return dst.ToArray(); }
        }

        // ---------------------------------------------------------------------------------------------- zones

        void OnZones()
        {
            if (!Enabled) return;
            _zones.Clear();
            foreach (var z in SavedZones.All)
            {
                var list = RoadCachePolicy.TilesAround(z.lon, z.lat, z.radiusM, _tileZoom); var keys = new List<long>(list.Count);
                foreach (var t in list)
                {
                    long k = RoadTiles.Key(_tileZoom, t.x, t.y); keys.Add(k); _store.Pin(RoadStore.Name("raw", _release, _tileZoom, t.x, t.y));
                    if (_tiles.TryGetValue(k, out var st)) st.pinned = true; else _fetchQueue.Add((t.x, t.y, true));
                }
                _zones.Add((z, keys));
            }
        }

        /// <summary>Save the current place as a zone and start downloading its roads for offline use.</summary>
        public SavedZones.Zone SaveZoneHere(string name, float radiusM = 0f)
        {
            if (_fociLL.Count == 0) return null;
            if (radiusM <= 0f) radiusM = SavedZones.DefaultRadius;
            var z = SavedZones.Add(name, _fociLL[0].lat, _fociLL[0].lon, radiusM);
            if (root != null && root.hud != null) root.hud.ShowToast("Zone saved: " + z.name + (Enabled ? " (roads downloading)" : ""));
            return z;
        }

        /// <summary>True once there is a position to save a zone at.</summary>
        public bool CanSaveZone => Enabled && _fociLL.Count > 0;

        /// <summary>How many road tiles a zone of this radius would download here (for the radius slider).</summary>
        public int EstimateZoneTiles(float radiusM)
        {
            if (_fociLL.Count == 0) return 0;
            if (Mathf.Approximately(radiusM, _estR) && Time.unscaledTime < _estUntil) return _estN;     // OnGUI runs several times per frame
            _estR = radiusM; _estUntil = Time.unscaledTime + 0.5f;
            return _estN = RoadCachePolicy.TilesAround(_fociLL[0].lon, _fociLL[0].lat, radiusM, _tileZoom).Count;
        }
        float _estR = -1f, _estUntil; int _estN;

        /// <summary>"name: loaded/total tiles" per saved zone (offline download progress).</summary>
        public string ZoneProgress()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var z in _zones) { int n = 0; foreach (var k in z.keys) if (_tiles.TryGetValue(k, out var t) && t.data != null) n++; sb.Append(z.z.name).Append(' ').Append(n).Append('/').Append(z.keys.Count).Append("  "); }
            return sb.ToString();
        }

        // ---------------------------------------------------------------------------------------------- update

        void Update()
        {
            if (!Enabled || focus == null || georeference == null) return;
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.f9Key.wasPressedThisFrame) SaveZoneHere(null);
#endif
            _store.Tick();
            if (_nodesDirty && Time.unscaledTime > _nodesSaveAt) SaveNodes(false);
            if (Time.unscaledTime >= _nextScan) { _nextScan = Time.unscaledTime + (budget != null && budget.burst ? 0.1f : 0.4f); Scan(); }
            RunJob();
            Status = Info;
        }

        bool Mobile => DeviceProfile.IsMobile;

        void Scan()
        {
            // foci in lon/lat (through ECEF: valid across origin shifts)
            _fociLL.Clear(); _fociPos.Clear(); AddFocus(focus); if (extraFoci != null) foreach (var t in extraFoci) AddFocus(t);
            if (_fociLL.Count == 0) return;
            float dt = Mathf.Max(0.05f, Time.unscaledTime - _speedT); _speedT = Time.unscaledTime;
            Vector3 d = focus.position - _lastFocus; _lastFocus = focus.position; _speed = d.magnitude > 400f ? 0f : Mathf.Lerp(_speed, d.magnitude / dt, 0.5f);

            double fetchR = RoadCachePolicy.FetchRadius(_speed, memLevel, Mobile), resR = RoadCachePolicy.ResidentRadius(memLevel, Mobile);
            // 1) wanted tiles around every focus (the player far, bots near), nearest first
            for (int f = 0; f < _fociLL.Count; f++)
                foreach (var t in RoadCachePolicy.TilesAround(_fociLL[f].lon, _fociLL[f].lat, f == 0 ? fetchR : 600, _tileZoom))
                {
                    long k = RoadTiles.Key(_tileZoom, t.x, t.y);
                    if (_tiles.TryGetValue(k, out var st)) { st.used = Time.unscaledTime; continue; }
                    bool queued = false; for (int i = 0; i < _fetchQueue.Count; i++) if (_fetchQueue[i].x == t.x && _fetchQueue[i].y == t.y) { queued = true; var e = _fetchQueue[i]; if (!e.pin) { _fetchQueue.RemoveAt(i); _fetchQueue.Insert(0, e); } break; }
                    if (!queued) _fetchQueue.Insert(0, (t.x, t.y, false));
                }
            int conc = Mobile ? 2 : 4;
            while (_active < conc && _fetchQueue.Count > 0)
            {
                var e = _fetchQueue[0]; _fetchQueue.RemoveAt(0); long k = RoadTiles.Key(_tileZoom, e.x, e.y);
                if (_tiles.TryGetValue(k, out var s0) && (s0.loading || s0.data != null || Time.unscaledTime < s0.retryAt)) continue;
                var st = _tiles.TryGetValue(k, out var ex) ? ex : (_tiles[k] = new TileSt()); st.loading = true; st.pinned |= e.pin; _active++; StartCoroutine(FetchTile(e.x, e.y, k, st));
            }
            // 2) chunk lifecycle by distance
            var drop = new List<long>();
            foreach (var kv in _tiles)
            {
                var st = kv.Value; if (st.data == null) continue;
                double dmin = double.MaxValue; for (int f = 0; f < _fociLL.Count; f++) dmin = Math.Min(dmin, TileDist(st.data, _fociLL[f]));
                if (!st.pinned && dmin > fetchR * 1.6 + 800 && Time.unscaledTime - st.used > 20f) { drop.Add(kv.Key); continue; }
            }
            foreach (var k in drop) DropTile(k);
            _buildQueue.Clear(); int colliders = 0, pending = 0;
            float colOn = (float)RoadCachePolicy.ColliderRadius(budget != null ? budget.addRadius : 260f, budget != null ? budget.radiusMul : 1f);
            float colOff = (budget != null ? budget.removeRadius * budget.radiusMul : 420f) + 60f;
            foreach (var c in _chunks.Values)
            {
                if (c.segs.Count == 0) continue;
                float dist = ChunkDist(c);
                if (c.res != null && c.pending > 0) pending += c.pending;
                if (dist > resR * 1.25f && c.go != null && c != _job) { Free(c); continue; }
                bool needs = c.res == null || (c.dirty && (c.pending == 0 || (budget != null && budget.Added != c.lastAdded)));
                if (dist <= resR && needs && c != _job && Time.unscaledTime >= c.retryAt) _buildQueue.Add(c);
                if (c.res != null && c.go != null)
                {
                    bool want = dist < (c.col != null && c.col.enabled ? colOff : colOn);
                    if (want && (c.col == null || !c.col.enabled) && c.mesh != null && c.res.mesh.TriangleCount > 0) EnableCollider(c);
                    else if (!want && c.col != null && c.col.enabled) DisableCollider(c);
                    if (c.col != null && c.col.enabled) colliders++;
                    bool showV = (Pavement || debugView) && c.vmesh != null && c.vmesh.vertexCount > 0 && dist < (c.viewOn ? ViewRadius + 120f : ViewRadius);
                    if (showV != c.viewOn || (showV && c.viewDebug != debugView)) { c.viewOn = showV; c.viewDebug = debugView; SetView(c, showV); }
                }
            }
            ColliderCount = colliders; PendingSegments = pending;
            _buildQueue.Sort((a, b) => ChunkDist(a).CompareTo(ChunkDist(b)));
            if (_store.Ok) _store.Enforce(RoadCachePolicy.DiskBudgetBytes(Mobile, memLevel));
        }

        void AddFocus(Transform t)
        {
            if (t == null || !t.gameObject.activeInHierarchy) return;
            double3 e = georeference.TransformUnityPositionToEarthCenteredEarthFixed(new double3(t.position.x, t.position.y, t.position.z));
            Geo.EcefToLonLat(e.x, e.y, e.z, out double lon, out double lat); if (double.IsNaN(lon) || double.IsNaN(lat)) return;
            _fociLL.Add((lon, lat)); _fociPos.Add(t.position);
        }

        double TileDist(RoadTileData d, (double lon, double lat) p)
        {
            RoadTiles.TileBounds(d.z, d.x, d.y, out double w, out double s, out double e, out double n);
            double cx = Math.Max(w, Math.Min(e, p.lon)), cy = Math.Max(s, Math.Min(n, p.lat));
            return Math.Sqrt(Math.Pow((cx - p.lon) * 111320.0 * Math.Cos(p.lat * Math.PI / 180), 2) + Math.Pow((cy - p.lat) * 110574.0, 2));
        }

        float ChunkDist(Chunk c)
        {
            float best = float.MaxValue;
            for (int f = 0; f < _fociLL.Count; f++)
            {
                c.frame.ToPlane(_fociLL[f].lon, _fociLL[f].lat, out double e, out double n);
                double dx = Math.Max(c.minE - e, Math.Max(0, e - c.maxE)), dz = Math.Max(c.minN - n, Math.Max(0, n - c.maxN));
                best = Mathf.Min(best, (float)Math.Sqrt(dx * dx + dz * dz));
            }
            return best;
        }

        // ---------------------------------------------------------------------------------------------- download + cache

        IEnumerator FetchTile(int x, int y, long key, TileSt st)
        {
            string name = RoadStore.Name("raw", _release, _tileZoom, x, y); RoadTileData data = null;
            var cached = _store.Read(name); if (cached != null) data = RoadTileFormat.Read(cached);
            if (data != null && RoadCachePolicy.IsCurrent(data.release, _release)) { Done(st, data, key); yield break; }
            string url = (_ofm ? _ofmTemplate : _base + _tilesTemplate.Replace("{release}", _release)).Replace("{z}", _tileZoom.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 20; yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    RoadTileData d; byte[] file = req.downloadHandler.data;
                    if (_ofm) { d = RoadMvt.Convert(Inflate(file), _tileZoom, x, y, _release); file = d != null ? RoadTileFormat.Write(d) : null; }
                    else d = RoadTileFormat.Read(file);
                    if (d != null) { _store.Write(name, file); Done(st, d, key); yield break; }
                }
                else if (req.responseCode == 404 || req.responseCode == 204)       // no roads in this tile (sea, desert): remember it so it is never asked again
                {
                    var empty = new RoadTileData { z = _tileZoom, x = x, y = y, release = _release }; _store.Write(name, RoadTileFormat.Write(empty)); Done(st, empty, key); yield break;
                }
            }
            string stale = _store.FindStale("raw", _tileZoom, x, y, _release);      // offline or server trouble: an older release is better than no road
            if (stale != null) { var sb = _store.Read(stale); var sd = sb != null ? RoadTileFormat.Read(sb) : null; if (sd != null) { Done(st, sd, key); yield break; } }
            _active--; st.loading = false; st.fails++; st.retryAt = Time.unscaledTime + Mathf.Min(120f, 5f * (1 << Mathf.Min(st.fails, 5)));
        }

        void Done(TileSt st, RoadTileData d, long key)
        {
            _active = Mathf.Max(0, _active - 1); st.loading = false; st.data = d; st.used = Time.unscaledTime; st.fails = 0;
            // split into z15 chunks by the first vertex; a segment that also appears in the neighbour tile file is built once
            foreach (var s in d.segments)
            {
                if (_segOwner.ContainsKey(s.id) || !RoadClassInfo.Drivable(s.cls)) continue;
                RoadTiles.TileOf(s.lon[0], s.lat[0], RoadCachePolicy.ChunkZoom, out int cx, out int cy);
                long ck = RoadTiles.Key(RoadCachePolicy.ChunkZoom, cx, cy);
                if (!_chunks.TryGetValue(ck, out var c))
                {
                    RoadTiles.TileBounds(RoadCachePolicy.ChunkZoom, cx, cy, out double w, out double so, out double e, out double n);
                    c = new Chunk { key = ck, x = cx, y = cy, lon = (w + e) / 2, lat = (so + n) / 2, tileKey = key }; c.frame = new RoadFrame(c.lon, c.lat, 0); _chunks[ck] = c;
                }
                c.segs.Add(s); _segOwner[s.id] = ck; c.dirty = true;
                for (int i = 0; i < s.lon.Length; i++) { c.frame.ToPlane(s.lon[i], s.lat[i], out double e, out double n); c.minE = Math.Min(c.minE, e); c.maxE = Math.Max(c.maxE, e); c.minN = Math.Min(c.minN, n); c.maxN = Math.Max(c.maxN, n); }
            }
        }

        void DropTile(long key)
        {
            if (!_tiles.TryGetValue(key, out var st)) return;
            if (st.data != null) foreach (var s in st.data.segments) if (_segOwner.TryGetValue(s.id, out long ck) && _chunks.TryGetValue(ck, out var c)) { c.segs.Remove(s); _segOwner.Remove(s.id); c.dirty = true; if (c.segs.Count == 0) { Free(c); _chunks.Remove(ck); } }
            _tiles.Remove(key);
        }

        // ---------------------------------------------------------------------------------------------- build job (time sliced)

        void RunJob()
        {
            float ms = budget != null && budget.burst ? (Mobile ? 6f : 10f) : (Mobile ? 1.5f : 3f);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds < ms)
            {
                if (_job == null)
                {
                    if (_buildQueue.Count == 0) return;
                    _job = _buildQueue[0]; _buildQueue.RemoveAt(0); if (!StartJob(_job)) { _job = null; continue; }
                }
                if (_job.builder.Step(budget != null && budget.burst ? 6 : 2)) { FinishJob(_job); _job = null; }
            }
        }

        bool StartJob(Chunk c)
        {
            EnsureObject(c); if (c.go == null) return false;
            if (!c.procTried)       // processed profiles of an earlier session: no sampling needed
            {
                c.procTried = true; var pb = _store.Read(RoadStore.Name("proc", _release, RoadCachePolicy.ChunkZoom, c.x, c.y));
                var pd = pb != null ? RoadChunkBuilder.LoadProcessed(pb, _release) : null;
                if (pd != null) foreach (var kv in pd) if (!c.solved.ContainsKey(kv.Key)) c.solved[kv.Key] = kv.Value;
            }
            if (float.IsNaN(c.refUp)) { c.refUp = FocusUp(c); }
            if (c.solved.Count > 0 && !ValidateCache(c)) c.solved.Clear();      // the tiles changed since the cache was written: refit
            c.builder = new RoadChunkBuilder(c.frame, new List<RoadSegment>(c.segs), (e, n) => Sample(c, e, n), _nodes, c.solved);
            return true;
        }

        void FinishJob(Chunk c)
        {
            var res = c.builder.Finish(); foreach (var kv in c.builder.Solved) c.solved[kv.Key] = kv.Value;
            c.res = res; c.pending = res.pending; c.dirty = false; c.saved = false;
            c.lastAdded = budget != null ? budget.Added : 0;
            if (res.pending > 0) { c.dirty = true; c.retryAt = Time.unscaledTime + Mathf.Min(30f, 1.5f * (1 << Mathf.Min(c.retries++, 5))); } else c.retries = 0;
            if (c.solved.Count > 0 && res.pending == 0) _store.Write(RoadStore.Name("proc", _release, RoadCachePolicy.ChunkZoom, c.x, c.y), RoadChunkBuilder.SaveProcessed(c.solved, _release));
            _nodesDirty = true; _nodesSaveAt = Time.unscaledTime + 20f;
            if (c.mesh == null) { c.mesh = new Mesh { name = "RoadChunk" }; c.mesh.MarkDynamic(); }
            var vs = new List<Vector3>(res.mesh.VertexCount); for (int i = 0; i < res.mesh.v.Count; i += 3) vs.Add(new Vector3(res.mesh.v[i], res.mesh.v[i + 1], res.mesh.v[i + 2]));
            c.mesh.Clear(); c.mesh.indexFormat = vs.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            c.mesh.SetVertices(vs); c.mesh.SetTriangles(res.mesh.t, 0, false); c.mesh.RecalculateBounds();
            BuildViewMesh(c, vs, res.mesh);
            if (c.col != null && c.col.enabled) { c.col.sharedMesh = null; c.col.sharedMesh = c.mesh; Changed(c); }   // rebuilt while active: new footprint
        }

        float FocusUp(Chunk c)
        {
            int best = 0; float bd = float.MaxValue;
            for (int i = 0; i < _fociPos.Count; i++) { c.frame.ToPlane(_fociLL[i].lon, _fociLL[i].lat, out double e, out double n); float d = (float)(e * e + n * n); if (d < bd) { bd = d; best = i; } }
            return c.go.transform.InverseTransformPoint(_fociPos[best]).y;
        }

        float Sample(Chunk c, double e, double n)
        {
            var t = c.go.transform; Vector3 o = t.TransformPoint(new Vector3((float)e, c.refUp + 150f, (float)n));
            if (!Physics.Raycast(o, -t.up, out var h, 420f, PhysicsSetup.TileOnlyMask, QueryTriggerInteraction.Ignore)) return float.NaN;
            if (t.InverseTransformDirection(h.normal).y < 0.35f) return float.NaN;       // wall / roof edge, not ground
            return t.InverseTransformPoint(h.point).y;
        }

        /// <summary>Compare up to 8 cached stations with the live tiles; if the median disagreement is over 1.2 m the cache is stale.</summary>
        bool ValidateCache(Chunk c)
        {
            var diffs = new List<float>(8); int probed = 0;
            foreach (var s in c.segs)
            {
                if (probed >= 8) break; if (!c.solved.TryGetValue(s.id, out var cv) || s.IsBridge || s.IsTunnel) continue;
                int n = cv.Length / 2, i = n / 2; if (n < 3) continue;
                double[] pe = new double[s.lon.Length], pn = new double[s.lon.Length]; for (int k = 0; k < pe.Length; k++) c.frame.ToPlane(s.lon[k], s.lat[k], out pe[k], out pn[k]);
                var line = RoadCenterline.Build(pe, pn); if (line == null || line.Count != n) continue; probed++;
                float h = Sample(c, line.e[i], line.n[i]); if (!float.IsNaN(h)) diffs.Add(Mathf.Abs(h - cv[i]));
            }
            if (diffs.Count < 3) return true;       // no live ground yet: trust the cache
            diffs.Sort(); return diffs[diffs.Count / 2] < 1.2f;
        }

        // ---------------------------------------------------------------------------------------------- objects

        void EnsureObject(Chunk c)
        {
            if (c.go != null) return;
            var go = new GameObject("RoadChunk " + c.x + "_" + c.y) { layer = PhysicsSetup.RoadLayer };
            go.transform.SetParent(georeference.transform, false);
            Geo.LonLatToEcef(c.lon, c.lat, 0, out double x, out double y, out double z);
            double3 p = georeference.TransformEarthCenteredEarthFixedPositionToUnity(new double3(x, y, z));
            double la = c.lat * Math.PI / 180.0, lo = c.lon * Math.PI / 180.0;
            var up = V(georeference.TransformEarthCenteredEarthFixedDirectionToUnity(new double3(Math.Cos(la) * Math.Cos(lo), Math.Cos(la) * Math.Sin(lo), Math.Sin(la))));
            var north = V(georeference.TransformEarthCenteredEarthFixedDirectionToUnity(new double3(-Math.Sin(la) * Math.Cos(lo), -Math.Sin(la) * Math.Sin(lo), Math.Cos(la))));
            go.transform.SetPositionAndRotation(new Vector3((float)p.x, (float)p.y, (float)p.z), Quaternion.LookRotation(north, up));
            go.AddComponent<CesiumGlobeAnchor>();                      // keeps the chunk valid when Cesium re-centres the origin
            c.go = go;
        }

        // ---------------------------------------------------------------------------------------------- pavement (what the player sees)

        /// <summary>Draw the generated road as real pavement (the "Road with substance designer" textures) over the tile's own, bumpy road.</summary>
        public bool Pavement => root == null || root.settings == null || root.settings.roadPavement;
        /// <summary>Pavement is only drawn this close: far away it would z-fight with the tile (depth precision) and cost draw calls for nothing.</summary>
        public float ViewRadius => Mobile ? 450f : 750f;
        /// <summary>The render mesh floats this far over the collider so the photogrammetry cannot z-fight through it.</summary>
        public const float VisualLift = 0.05f;

        /// <summary>Debug: paint the road yellow and translucent (?roads=debug, or the Settings toggle) to see where the collider is.</summary>
        public bool ShowSurface { get => debugView; set => debugView = value; }

        Material _mPlain, _mMarked, _mDebug; bool _matsTried;

        void EnsureMaterials()
        {
            if (_matsTried) return; _matsTried = true;
            _mPlain = LoadPavement("RoadPlain", "plain"); _mMarked = LoadPavement("RoadMarked", "marked");
        }

        /// <summary>The materials are assets made by the editor setup (ProjectSetup), so their shader and keywords survive WebGL stripping.
        /// Without them (setup not run yet) a plain URP Lit with the albedo texture is made, so the road is still drawn.</summary>
        static Material LoadPavement(string name, string tex)
        {
            var m = Resources.Load<Material>("Materials/" + name); if (m != null) return m;
            var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Universal Render Pipeline/Simple Lit") ?? Shader.Find("Sprites/Default"); if (sh == null) return null;
            m = new Material(sh) { name = "RW_" + name };
            var t = Resources.Load<Texture2D>("Roads/" + tex + "_d");
            if (t != null) { m.mainTexture = t; if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", t); }
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.3f);
            return m;
        }

        void BuildViewMesh(Chunk c, List<Vector3> vs, RoadMeshData m)
        {
            if (m.tm.Count + m.tp.Count == 0) { if (c.vmesh != null) c.vmesh.Clear(); return; }
            if (c.vmesh == null) { c.vmesh = new Mesh { name = "RoadPavement" }; c.vmesh.MarkDynamic(); }
            var vm = c.vmesh; vm.Clear(); vm.indexFormat = vs.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            vm.SetVertices(vs);
            var uvs = new List<Vector2>(vs.Count); for (int i = 0; i + 1 < m.uv.Count; i += 2) uvs.Add(new Vector2(m.uv[i], m.uv[i + 1]));
            vm.SetUVs(0, uvs);
            vm.subMeshCount = 2; vm.SetTriangles(m.tp, 0, false); vm.SetTriangles(m.tm, 1, false);    // 0 plain asphalt, 1 marked asphalt
            vm.RecalculateNormals(); vm.RecalculateTangents(); vm.RecalculateBounds();
        }

        void SetView(Chunk c, bool show)
        {
            var t = c.go.transform.Find("RoadView");
            if (!show) { if (t != null) t.gameObject.SetActive(false); return; }
            EnsureMaterials();
            if (debugView && _mDebug == null)
            {
                var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
                if (sh != null) _mDebug = new Material(sh) { color = new Color(1f, 0.85f, 0.1f, 0.5f) };
            }
            if (t == null)
            {
                var v = new GameObject("RoadView");                                    // default layer: drawn by every camera; no collider, so the car's rays never see it
                v.transform.SetParent(c.go.transform, false); v.transform.localPosition = new Vector3(0f, VisualLift, 0f);
                v.AddComponent<MeshFilter>(); var mr = v.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
                t = v.transform;
            }
            t.GetComponent<MeshFilter>().sharedMesh = c.vmesh;
            var r = t.GetComponent<MeshRenderer>();
            if (debugView && _mDebug != null) r.sharedMaterials = new[] { _mDebug, _mDebug };
            else if (_mPlain != null && _mMarked != null) r.sharedMaterials = new[] { _mPlain, _mMarked };
            t.gameObject.SetActive(true);
        }

        static Vector3 V(double3 d) => new Vector3((float)d.x, (float)d.y, (float)d.z).normalized;

        void EnableCollider(Chunk c)
        {
            if (c.col == null) { c.col = c.go.AddComponent<MeshCollider>(); c.col.sharedMaterial = PhysicsSetup.RoadMaterial; }
            c.col.sharedMesh = c.mesh; c.col.enabled = true; Physics.SyncTransforms(); Changed(c);
        }
        void DisableCollider(Chunk c) { c.col.enabled = false; c.col.sharedMesh = null; Changed(c); }

        void Free(Chunk c)
        {
            bool had = c.col != null && c.col.enabled;
            if (had) Changed(c);
            if (c.go != null) Destroy(c.go); if (c.mesh != null) Destroy(c.mesh); if (c.vmesh != null) Destroy(c.vmesh);
            c.go = null; c.mesh = null; c.vmesh = null; c.viewOn = false; c.col = null; c.res = null; c.builder = null; c.dirty = true; c.procTried = true;
        }

        void Changed(Chunk c)
        {
            Epoch++; if (c.go == null || c.res == null) return;
            var lb = c.LocalBox; var m = c.go.transform.localToWorldMatrix; var b = new Bounds(m.MultiplyPoint3x4(lb.center), Vector3.zero);
            for (int i = 0; i < 8; i++) b.Encapsulate(m.MultiplyPoint3x4(lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            b.Expand(6f); _changes.Add((b, Epoch)); if (_changes.Count > 32) _changes.RemoveAt(0);
        }

        void SaveNodes(bool force) { if (!_nodesDirty && !force) return; _nodesDirty = false; _store.Write("nodes_all.bin", _nodes.Save()); }

        public void Boost() { _nextScan = 0f; }

        // ---------------------------------------------------------------------------------------------- tile collider integration

        /// <summary>World-space regions whose road footprint changed after <paramref name="sinceEpoch"/>; false when nothing changed (or too much: re-carve everything).</summary>
        public bool ChangedSince(int sinceEpoch, List<Bounds> into)
        {
            if (sinceEpoch >= Epoch) return false; into.Clear();
            if (_changes.Count == 0 || _changes[0].epoch > sinceEpoch + 1) { into.Add(new Bounds(Vector3.zero, Vector3.one * 1e9f)); return true; }
            foreach (var c in _changes) if (c.epoch > sinceEpoch) into.Add(c.b);
            return into.Count > 0;
        }

        /// <summary>
        /// Cut the road corridors out of a tile collider copy. <paramref name="src"/> is the (despiked) mesh in <paramref name="tile"/>'s local space; returns a new
        /// mesh with the road surface removed and bumps inside the corridor pressed to the road height, or null when no active road touches it.
        /// </summary>
        public Mesh TryCarve(Transform tile, Mesh src, Bounds worldBounds)
        {
            if (!Enabled || src == null || !src.isReadable) return null;
            List<Chunk> hits = null;
            foreach (var c in _chunks.Values)
            {
                if (c.col == null || !c.col.enabled || c.res == null || c.res.footprint.IsEmpty) continue;
                var lb = c.LocalBox; var m = c.go.transform.localToWorldMatrix; var wb = new Bounds(m.MultiplyPoint3x4(lb.center), Vector3.zero);
                for (int i = 0; i < 8; i++) wb.Encapsulate(m.MultiplyPoint3x4(lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
                if (!wb.Intersects(worldBounds)) continue; (hits ?? (hits = new List<Chunk>())).Add(c);
            }
            if (hits == null) return null;
            var verts = new List<Vector3>(src.vertexCount); src.GetVertices(verts);
            var tris = new List<int>(); var tmp = new List<int>();
            for (int s = 0; s < src.subMeshCount; s++) { if (src.GetTopology(s) != MeshTopology.Triangles) continue; tmp.Clear(); src.GetTriangles(tmp, s); tris.AddRange(tmp); }
            if (tris.Count < 3) return null;
            var ta = tris.ToArray(); bool any = false; var f = new float[verts.Count * 3]; var tl = tile.localToWorldMatrix;
            foreach (var c in hits)
            {
                var toChunk = c.go.transform.worldToLocalMatrix * tl; var back = toChunk.inverse;
                for (int i = 0; i < verts.Count; i++) { var p = toChunk.MultiplyPoint3x4(verts[i]); f[i * 3] = p.x; f[i * 3 + 1] = p.y; f[i * 3 + 2] = p.z; }
                ta = RoadCarver.Carve(f, ta, c.res.footprint, CarveBelow, CarveAbove, out var r);
                if (r.removed == 0 && r.pressed == 0) continue; any = true;
                for (int i = 0; i < verts.Count; i++) verts[i] = back.MultiplyPoint3x4(new Vector3(f[i * 3], f[i * 3 + 1], f[i * 3 + 2]));
            }
            if (!any) return null;
            var m2 = new Mesh { name = "tile-collider-carved", indexFormat = verts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            m2.SetVertices(verts); m2.SetTriangles(ta, 0, false); return m2;
        }
        public static float CarveBelow = 1.0f, CarveAbove = 2.2f;     // m: photogrammetry that far under / over the road surface is "road", farther is a real structure

        // ---------------------------------------------------------------------------------------------- query for the car / HUD

        public struct RoadInfo { public bool onRoad; public RoadClass cls; public RoadSurface surface; public float width; public bool bridge, tunnel; }

        /// <summary>Road under a world position (planar nearest centreline within the road's half width). Cheap enough for a few calls per second.</summary>
        public RoadInfo Query(Vector3 world)
        {
            var best = new RoadInfo(); float bestD = float.MaxValue;
            foreach (var c in _chunks.Values)
            {
                if (c.res == null || c.go == null) continue;
                Vector3 l = c.go.transform.InverseTransformPoint(world); if (l.x < c.res.minX - 20 || l.x > c.res.maxX + 20 || l.z < c.res.minZ - 20 || l.z > c.res.maxZ + 20) continue;
                foreach (var s in c.res.segments)
                {
                    var ln = s.line;
                    for (int i = 0; i < ln.Count; i++)
                    {
                        float dx = (float)ln.e[i] - l.x, dz = (float)ln.n[i] - l.z, d = Mathf.Sqrt(dx * dx + dz * dz);
                        if (d < s.halfWidth + 2f && Mathf.Abs(s.up[i] - l.y) < 4f && d < bestD)
                        { bestD = d; best = new RoadInfo { onRoad = d <= s.halfWidth, cls = s.src.cls, surface = s.src.surface, width = s.halfWidth * 2, bridge = s.src.IsBridge, tunnel = s.src.IsTunnel }; }
                    }
                }
            }
            return best;
        }
    }
}
