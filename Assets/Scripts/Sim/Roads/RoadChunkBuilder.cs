using System;
using System.Collections.Generic;
using System.IO;

namespace RocketWorld.Sim.Roads
{
    /// <summary>Heights (ellipsoidal, m) of shared connectors. The first solver that fixes a node publishes it; every segment that later meets the node is pinned to it, so seams between chunks, tiles and sessions stay continuous.</summary>
    public sealed class NodeHeights
    {
        readonly Dictionary<uint, float> _h = new Dictionary<uint, float>();
        public int Count => _h.Count;
        public bool TryGet(uint id, out float h) => _h.TryGetValue(id, out h);
        public void Set(uint id, float h) { if (id != 0) _h[id] = h; }
        public void Clear() => _h.Clear();
        public byte[] Save() { using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms)) { w.Write(_h.Count); foreach (var kv in _h) { w.Write(kv.Key); w.Write(kv.Value); } return ms.ToArray(); } }
        public void Load(byte[] b) { try { using (var r = new BinaryReader(new MemoryStream(b))) { int n = r.ReadInt32(); for (int i = 0; i < n; i++) { uint k = r.ReadUInt32(); _h[k] = r.ReadSingle(); } } } catch (Exception) { } }
    }

    public sealed class ChunkResult
    {
        public RoadFrame frame; public RoadMeshData mesh; public RoadFootprint footprint; public List<BuiltSegment> segments = new List<BuiltSegment>();
        public int pending;          // segments that could not be solved yet (no ground samples): the chunk is partial and is rebuilt later
        public int fromCache;
        public float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
    }

    /// <summary>
    /// Segments of one chunk -> smooth centreline, fitted height profile, ribbon mesh, footprint. Incremental: <see cref="Step"/> processes a few segments per call
    /// (WebGL has one thread), <see cref="Finish"/> assembles the mesh. Ground heights come from the <see cref="Sampler"/> (ray casts against the tiles at runtime;
    /// a function in tests) or from the processed cache, which skips sampling entirely.
    /// </summary>
    public sealed class RoadChunkBuilder
    {
        /// <summary>(east, north) in the chunk frame -> ground height in frame-local up, or NaN when there is no tile under that point (yet).</summary>
        public delegate float GroundSampler(double east, double north);

        public readonly RoadFrame Frame; public readonly GroundSampler Sampler; public readonly NodeHeights Nodes;
        public readonly Dictionary<uint, float[]> Cached;       // segment id -> [up..., bank...]
        public readonly Dictionary<uint, float[]> Solved = new Dictionary<uint, float[]>();
        readonly List<RoadSegment> _in; int _next;
        readonly ChunkResult _res = new ChunkResult();
        public static int ProcVersion = 2;                      // bump when the processing changes so old processed caches are ignored
        public int Total => _in.Count; public int Done => _next; public bool Complete => _next >= _in.Count;

        public RoadChunkBuilder(RoadFrame f, List<RoadSegment> segs, GroundSampler sampler, NodeHeights nodes, Dictionary<uint, float[]> cached = null)
        { Frame = f; _in = segs; Sampler = sampler; Nodes = nodes; Cached = cached; _res.frame = f; }

        public bool Step(int maxSegments)
        {
            for (int k = 0; k < maxSegments && _next < _in.Count; k++) Process(_in[_next++]);
            return Complete;
        }

        void Process(RoadSegment s)
        {
            if (!RoadClassInfo.Drivable(s.cls) || s.lon.Length < 2) return;
            int m = s.lon.Length; var pe = new double[m]; var pn = new double[m];
            for (int i = 0; i < m; i++) Frame.ToPlane(s.lon[i], s.lat[i], out pe[i], out pn[i]);
            var line = RoadCenterline.Build(pe, pn); if (line == null) return;
            int n = line.Count; float hw = RoadClassInfo.Width(s) * 0.5f;
            uint nA = 0, nB = 0; foreach (var c in s.connectors) { if (c.at < 0.02f) nA = c.id; else if (c.at > 0.98f) nB = c.id; }
            float[] up, bank;
            if (Cached != null && Cached.TryGetValue(s.id, out var cv) && cv.Length == 2 * n) { up = new float[n]; bank = new float[n]; Array.Copy(cv, 0, up, 0, n); Array.Copy(cv, n, bank, 0, n); _res.fromCache++; }
            else
            {
                var inp = new ProfileInput { hc = new float[n], hl = new float[n], hr = new float[n], halfOffset = Math.Max(1f, hw * 0.6f), ignoreInterior = s.IsBridge || s.IsTunnel, maxGrade = RoadClassInfo.MaxGrade(s.cls) };
                if (!inp.ignoreInterior)
                    for (int i = 0; i < n; i++)
                    {
                        double e = line.e[i], nn = line.n[i]; float rx = line.tz[i], rz = -line.tx[i];
                        inp.hc[i] = Sampler(e, nn); inp.hl[i] = Sampler(e - rx * inp.halfOffset, nn - rz * inp.halfOffset); inp.hr[i] = Sampler(e + rx * inp.halfOffset, nn + rz * inp.halfOffset);
                    }
                inp.startPin = Pin(nA, line.e[0], line.n[0]); inp.endPin = Pin(nB, line.e[n - 1], line.n[n - 1]);
                if (inp.ignoreInterior)       // a bridge / tunnel with free ends takes the ground height at its ends
                {
                    if (float.IsNaN(inp.startPin)) inp.startPin = Sampler(line.e[0], line.n[0]);
                    if (float.IsNaN(inp.endPin)) inp.endPin = Sampler(line.e[n - 1], line.n[n - 1]);
                }
                var r = RoadProfile.Solve(line, inp);
                if (r == null) { _res.pending++; return; }
                up = r.up; bank = r.bank;
                var store = new float[2 * n]; Array.Copy(up, 0, store, 0, n); Array.Copy(bank, 0, store, n, n); Solved[s.id] = store;
            }
            if (nA != 0 && !Nodes.TryGet(nA, out _)) Nodes.Set(nA, (float)Frame.HFromUp(line.e[0], line.n[0], up[0]));
            if (nB != 0 && !Nodes.TryGet(nB, out _)) Nodes.Set(nB, (float)Frame.HFromUp(line.e[n - 1], line.n[n - 1], up[n - 1]));
            _res.segments.Add(new BuiltSegment { src = s, line = line, up = up, bank = bank, halfWidth = hw, nodeA = nA, nodeB = nB, noCarve = s.IsTunnel, view = RoadClassInfo.ViewKind(s) });
        }

        float Pin(uint node, double e, double n)
        {
            if (node != 0 && Nodes.TryGet(node, out float h)) return (float)Frame.UpFromH(e, n, h) - RoadProfile.SurfaceBias;   // the solver adds the bias back
            return float.NaN;
        }

        public ChunkResult Finish()
        {
            _res.mesh = RoadMeshBuilder.Build(_res.segments);
            var m = _res.mesh;
            for (int i = 0; i < m.v.Count; i += 3) { _res.minX = Math.Min(_res.minX, m.v[i]); _res.maxX = Math.Max(_res.maxX, m.v[i]); _res.minY = Math.Min(_res.minY, m.v[i + 1]); _res.maxY = Math.Max(_res.maxY, m.v[i + 1]); _res.minZ = Math.Min(_res.minZ, m.v[i + 2]); _res.maxZ = Math.Max(_res.maxZ, m.v[i + 2]); }
            _res.footprint = new RoadFootprint(m.v.ToArray(), m.c.ToArray());
            return _res;
        }

        /// <summary>Processed cache blob: version, segment count, then (id, station count, up[], bank[]) per solved segment.</summary>
        public static byte[] SaveProcessed(Dictionary<uint, float[]> d, string release)
        {
            using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms))
            {
                w.Write(ProcVersion); w.Write(release ?? ""); w.Write(d.Count);
                foreach (var kv in d) { w.Write(kv.Key); w.Write(kv.Value.Length / 2); foreach (var f in kv.Value) w.Write(f); }
                return ms.ToArray();
            }
        }

        public static Dictionary<uint, float[]> LoadProcessed(byte[] b, string release)
        {
            try
            {
                using (var r = new BinaryReader(new MemoryStream(b)))
                {
                    if (r.ReadInt32() != ProcVersion || r.ReadString() != (release ?? "")) return null;
                    int n = r.ReadInt32(); var d = new Dictionary<uint, float[]>(n);
                    for (int i = 0; i < n; i++) { uint id = r.ReadUInt32(); int c = r.ReadInt32(); if (c < 2 || c > 100000) return null; var a = new float[2 * c]; for (int k = 0; k < a.Length; k++) a[k] = r.ReadSingle(); d[id] = a; }
                    return d;
                }
            }
            catch (Exception) { return null; }
        }
    }
}
