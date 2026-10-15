using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CesiumForUnity;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Copyable per-frame tile log. Starts the moment the tileset is created and appends ONE line per rendered frame:
    /// time (s and ms since the tileset started loading), frame time / fps, load progress, tile counts (loaded / visible / hidden /
    /// new / gone), vertices and triangles, quality settings, car state, and - when "full tile list" is on - every tile's id, state
    /// (V visible, H loaded-but-hidden) and distance to the car. A tile's name, vertex count, triangle count and size are logged
    /// once, on the line where it first appears. The log is capped (oldest lines dropped) so the WebGL heap cannot blow up.
    /// </summary>
    public class TileLog : MonoBehaviour
    {
        public GameRoot root;
        public bool fullTileList = true;
        public const int MaxChars = 6_000_000;

        class Info { public string name; public int verts, tris; public float size; public Vector3 centreLocal; public Transform t; public int stamp; public bool active; }

        Cesium3DTileset _ts;
        float _t0;
        int _frame;
        readonly Dictionary<int, Info> _known = new Dictionary<int, Info>(512);
        readonly List<int> _goneIds = new List<int>(32);
        readonly List<string> _lines = new List<string>(4096);
        readonly StringBuilder _sb = new StringBuilder(8192), _newSb = new StringBuilder(512), _tileSb = new StringBuilder(4096);
        string _header = "";
        long _chars;
        int _dropped;
        float _t90 = -1f, _t100 = -1f, _nextHud;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public string Hud { get; private set; } = "";
        public int Lines => _lines.Count + _dropped;
        public int StoredLines => _lines.Count;
        public long Chars => _chars;
        public float Elapsed => _ts == null ? 0f : Time.realtimeSinceStartup - _t0;

        public void Begin(Cesium3DTileset ts)
        {
            _ts = ts; _t0 = Time.realtimeSinceStartup; _frame = 0; _t90 = _t100 = -1f;
            _known.Clear(); _lines.Clear(); _chars = 0; _dropped = 0;
            var s = root.settings;
            _header = "# RocketWorld tile log | " + System.DateTime.UtcNow.ToString("o") + " | " + DeviceProfile.Describe()
                + " | provider " + s.provider + " | sse " + s.sse.ToString("0.#", Inv) + " | radius " + s.loadRadius.ToString("0", Inv) + " m | far " + s.viewDistance.ToString("0", Inv)
                + " m | simultaneous loads " + DeviceProfile.SimultaneousLoads + " | cache " + DeviceProfile.TileCacheBytes / (1024 * 1024) + " MB\n"
                + "# line: F=frame t=s since load start ms=same in ms dt=frame ms fps load=% tiles=loaded vis=visible hid=hidden new gone verts tris visVerts visTris sse radius far turbo cacheMB car=x,y,z kmh cam=x,y,z"
                + " | NEW[id=name/verts/tris/sizeM] GONE[ids] | T[id:V|H:distM,...]";
        }

        public void Clear() { _lines.Clear(); _chars = 0; _dropped = 0; _known.Clear(); _t0 = Time.realtimeSinceStartup; _frame = 0; _t90 = _t100 = -1f; }

        public string Text() => _header + "\n" + string.Join("\n", _lines);

        void Add(string line)
        {
            _lines.Add(line); _chars += line.Length + 1;
            if (_chars > MaxChars)
            {
                int drop = 0; long freed = 0;
                while (drop < _lines.Count - 1 && _chars - freed > MaxChars * 0.85f) { freed += _lines[drop].Length + 1; drop++; }
                _lines.RemoveRange(0, drop); _chars -= freed; _dropped += drop;
            }
        }

        void LateUpdate()
        {
            if (_ts == null || root == null || root.car == null) return;
            _frame++;
            float now = Time.realtimeSinceStartup - _t0;
            var tr = _ts.transform;
            int n = tr.childCount, vis = 0, hid = 0, nNew = 0;
            long verts = 0, tris = 0, vVerts = 0, vTris = 0;
            var carPos = root.car.position;
            _newSb.Length = 0; _tileSb.Length = 0;

            for (int i = 0; i < n; i++)
            {
                var c = tr.GetChild(i);
                var go = c.gameObject;
                int id = go.GetInstanceID();
                if (!_known.TryGetValue(id, out var inf))
                {
                    inf = new Info { name = c.name, t = c };
                    Bounds b = default; bool has = false;
                    foreach (var mf in c.GetComponentsInChildren<MeshFilter>(true))
                    {
                        var m = mf.sharedMesh; if (m == null) continue;
                        inf.verts += m.vertexCount;
                        for (int sm = 0; sm < m.subMeshCount; sm++) inf.tris += (int)(m.GetIndexCount(sm) / 3);
                        var r = mf.GetComponent<Renderer>();
                        if (r != null) { if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds); }
                    }
                    if (has) { inf.size = b.size.magnitude; inf.centreLocal = c.InverseTransformPoint(b.center); }
                    _known[id] = inf; nNew++;
                    _newSb.Append(id).Append('=').Append(inf.name).Append('/').Append(inf.verts).Append('/').Append(inf.tris).Append('/').Append(inf.size.ToString("0", Inv)).Append(';');
                }
                inf.stamp = _frame;
                inf.active = go.activeSelf;
                verts += inf.verts; tris += inf.tris;
                if (inf.active) { vis++; vVerts += inf.verts; vTris += inf.tris; } else hid++;
                if (fullTileList)
                {
                    float d = Vector3.Distance(c.TransformPoint(inf.centreLocal), carPos);
                    _tileSb.Append(id).Append(':').Append(inf.active ? 'V' : 'H').Append(':').Append(d.ToString("0", Inv)).Append(',');
                }
            }

            _goneIds.Clear();
            foreach (var kv in _known) if (kv.Value.stamp != _frame) _goneIds.Add(kv.Key);
            for (int i = 0; i < _goneIds.Count; i++) _known.Remove(_goneIds[i]);

            float prog = _ts.ComputeLoadProgress();
            if (_t90 < 0f && prog >= 90f) { _t90 = now; Add("# MILESTONE load>=90% at " + now.ToString("0.000", Inv) + " s"); }
            if (_t100 < 0f && prog >= 99.9f) { _t100 = now; Add("# MILESTONE load=100% at " + now.ToString("0.000", Inv) + " s (frame " + _frame + ")"); }

            float dt = Time.unscaledDeltaTime * 1000f;
            var cp = root.car.position;
            var cam = root.cam != null ? root.cam.transform.position : Vector3.zero;
            var sb = _sb; sb.Length = 0;
            sb.Append('F').Append(_frame).Append(" t=").Append(now.ToString("0.000", Inv)).Append("s ms=").Append(Mathf.RoundToInt(now * 1000f))
              .Append(" dt=").Append(dt.ToString("0.0", Inv)).Append(" fps=").Append((1000f / Mathf.Max(dt, 0.01f)).ToString("0", Inv))
              .Append(" load=").Append(prog.ToString("0.0", Inv))
              .Append(" tiles=").Append(n).Append(" vis=").Append(vis).Append(" hid=").Append(hid).Append(" new=").Append(nNew).Append(" gone=").Append(_goneIds.Count)
              .Append(" verts=").Append(verts).Append(" tris=").Append(tris).Append(" visVerts=").Append(vVerts).Append(" visTris=").Append(vTris)
              .Append(" sse=").Append(root.EffectiveSse.ToString("0.#", Inv)).Append(" radius=").Append(root.EffectiveRadius.ToString("0", Inv)).Append(" dq=").Append(root.settings.distantQuality.ToString("0.0", Inv)).Append(" radMul=").Append(root.RadiusMul.ToString("0.00", Inv)).Append(" far=").Append(root.EffectiveFar.ToString("0", Inv))
              .Append(" turbo=").Append(root.TurboActive ? 1 : 0).Append(" cacheMB=").Append(DeviceProfile.TileCacheBytes / (1024 * 1024))
              .Append(" car=").Append(cp.x.ToString("0.0", Inv)).Append(',').Append(cp.y.ToString("0.0", Inv)).Append(',').Append(cp.z.ToString("0.0", Inv))
              .Append(' ').Append(root.carCtl != null ? (root.carCtl.SpeedMS * 3.6f).ToString("0", Inv) : "0")
              .Append(" cam=").Append(cam.x.ToString("0.0", Inv)).Append(',').Append(cam.y.ToString("0.0", Inv)).Append(',').Append(cam.z.ToString("0.0", Inv));
            if (nNew > 0) sb.Append(" NEW[").Append(_newSb).Append(']');
            if (_goneIds.Count > 0) { sb.Append(" GONE["); for (int i = 0; i < _goneIds.Count; i++) sb.Append(_goneIds[i]).Append(';'); sb.Append(']'); }
            if (fullTileList) sb.Append(" T[").Append(_tileSb).Append(']');
            Add(sb.ToString());

            if (Time.unscaledTime >= _nextHud)
            {
                _nextHud = Time.unscaledTime + 0.1f;
                Hud = "t " + now.ToString("0.000", Inv) + " s · load " + prog.ToString("0.0", Inv) + "% · tiles " + n + " (" + vis + " vis)"
                    + (_t90 >= 0 ? " · 90% @ " + _t90.ToString("0.00", Inv) + " s" : "") + (_t100 >= 0 ? " · 100% @ " + _t100.ToString("0.00", Inv) + " s" : "");
            }
        }
    }
}
