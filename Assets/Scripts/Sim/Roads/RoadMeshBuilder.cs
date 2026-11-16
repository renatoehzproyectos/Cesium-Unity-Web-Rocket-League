using System;
using System.Collections.Generic;

namespace RocketWorld.Sim.Roads
{
    public sealed class BuiltSegment
    {
        public RoadSegment src; public Centerline line; public float[] up, bank; public float halfWidth;
        public uint nodeA, nodeB;            // connectors at the two ends (0 = none)
        public bool noCarve;                 // tunnels: the road is under the terrain, never cut the tiles
        public int view;                     // RoadClassInfo.ViewKind: 0 collider only, 1 plain asphalt, 2 marked asphalt
    }

    /// <summary>
    /// Ribbon mesh for the collider: one quad strip per segment (flat across, tilted by the cross slope), an inner-side clamp on tight bends so the
    /// strip never folds over itself, and a convex-hull patch at every node where two or more segments meet, so intersections have no gaps.
    /// All triangles face up (Unity clockwise winding) so wheel rays from above hit them.
    /// </summary>
    public static class RoadMeshBuilder
    {
        /// <summary>Real-world size of the pavement textures (the "Road with substance designer" tile, 512x1024 px): the marked texture spans the whole road width and is twice as long as wide;
        /// the plain one repeats every PlainU metres across and PlainV metres along.</summary>
        public const float MarkedAspect = 2f, PlainU = 6.2f, PlainV = 12.5f;

        public static void AddSegment(RoadMeshData m, BuiltSegment s, List<float> endL, List<float> endR, out int[] endIdx)
        {
            var c = s.line; int n = c.Count; var li = new int[n]; var ri = new int[n];
            for (int i = 0; i < n; i++)
            {
                float tx = c.tx[i], tz = c.tz[i], rx = tz, rz = -tx;       // right of the heading
                float hl = s.halfWidth, hr = s.halfWidth;
                if (i > 0 && i < n - 1)
                {
                    double d = c.s[i + 1] - c.s[i - 1];
                    double cross = c.tx[i - 1] * c.tz[i + 1] - c.tz[i - 1] * c.tx[i + 1];     // >0: turning left (counter clockwise in east/north)
                    double ang = Math.Asin(Math.Max(-1, Math.Min(1, cross))); double kappa = Math.Abs(ang) / Math.Max(d * 0.5, 0.5);
                    if (kappa > 1e-4) { float lim = (float)(0.85 / kappa); if (cross > 0) hl = Math.Min(hl, lim); else hr = Math.Min(hr, lim); }
                }
                float b = s.bank[i];
                float W = 2f * s.halfWidth, v0 = ((s.src.id * 2654435761u) >> 8 & 0xFFFF) / 65535f;       // start the texture at a different phase on every segment
                float ul, ur, vv;
                if (s.view == 2) { ul = 0f; ur = 1f; vv = ((float)c.s[i] + v0 * W * MarkedAspect) / (W * MarkedAspect); }
                else { ul = 0f; ur = W / PlainU; vv = ((float)c.s[i] + v0 * PlainV) / PlainV; }
                li[i] = m.Add((float)c.e[i] - rx * hl, s.up[i] - b * hl, (float)c.n[i] - rz * hl, ul, vv);
                ri[i] = m.Add((float)c.e[i] + rx * hr, s.up[i] + b * hr, (float)c.n[i] + rz * hr, ur, vv);
            }
            bool carve = !s.noCarve;
            for (int i = 0; i < n - 1; i++) { m.Tri(li[i], li[i + 1], ri[i], carve, s.view); m.Tri(ri[i], li[i + 1], ri[i + 1], carve, s.view); }
            endIdx = new[] { li[0], ri[0], li[n - 1], ri[n - 1] };
        }

        /// <summary>Builds the whole chunk mesh. Node patches are generated from the end corners of every segment sharing a node id.</summary>
        public static RoadMeshData Build(IList<BuiltSegment> segs)
        {
            var m = new RoadMeshData();
            var nodes = new Dictionary<uint, List<int>>(); var nodeCarve = new Dictionary<uint, bool>();
            foreach (var s in segs)
            {
                AddSegment(m, s, null, null, out var e);
                if (s.nodeA != 0) Node(nodes, nodeCarve, s.nodeA, e[0], e[1], s.noCarve);
                if (s.nodeB != 0) Node(nodes, nodeCarve, s.nodeB, e[2], e[3], s.noCarve);
            }
            foreach (var kv in nodes) if (kv.Value.Count >= 4) Patch(m, kv.Value, nodeCarve[kv.Key]);
            return m;
        }

        static void Node(Dictionary<uint, List<int>> d, Dictionary<uint, bool> carve, uint id, int l, int r, bool noCarve)
        {
            if (!d.TryGetValue(id, out var list)) { d[id] = list = new List<int>(8); carve[id] = true; }
            list.Add(l); list.Add(r); if (noCarve) carve[id] = false;
        }

        /// <summary>Convex hull (monotone chain) of the corner points in plan, fan-triangulated from the centroid, heights = distance-weighted from the corners.</summary>
        static void Patch(RoadMeshData m, List<int> corners, bool carve)
        {
            int k = corners.Count; var p = new List<(float x, float z, float y, int i)>(k);
            for (int i = 0; i < k; i++) { int v = corners[i] * 3; p.Add((m.v[v], m.v[v + 2], m.v[v + 1], corners[i])); }
            p.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.z.CompareTo(b.z));
            var h = new List<(float x, float z, float y, int i)>();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = h.Count;
                for (int i = 0; i < k; i++)
                {
                    var q = pass == 0 ? p[i] : p[k - 1 - i];
                    while (h.Count >= start + 2 && Cross(h[h.Count - 2], h[h.Count - 1], q) <= 1e-6f) h.RemoveAt(h.Count - 1);
                    h.Add(q);
                }
                h.RemoveAt(h.Count - 1);
            }
            if (h.Count < 3) return;
            float cx = 0, cz = 0, cy = 0; foreach (var q in h) { cx += q.x; cz += q.z; cy += q.y; }
            cx /= h.Count; cz /= h.Count; cy /= h.Count;
            // own vertices for the patch: the corners belong to the road strips (their texture coordinates run along the road), the patch is mapped from above
            int c = m.Add(cx, cy, cz, cx / PlainU, cz / PlainV); var ni = new int[h.Count];
            for (int i = 0; i < h.Count; i++) ni[i] = m.Add(h[i].x, h[i].y, h[i].z, h[i].x / PlainU, h[i].z / PlainV);
            // hull from the monotone chain is counter clockwise in (x, z); Unity wants clockwise seen from above -> reverse the fan order
            for (int i = 0; i < h.Count; i++) m.Tri(c, ni[(i + 1) % h.Count], ni[i], carve, carve ? 1 : 0);
        }
        static float Cross((float x, float z, float y, int i) o, (float x, float z, float y, int i) a, (float x, float z, float y, int i) b)
            => (a.x - o.x) * (b.z - o.z) - (a.z - o.z) * (b.x - o.x);
    }

    /// <summary>2D grid over the carve triangles of a chunk: "is there road under this point, and at what height".</summary>
    public sealed class RoadFootprint
    {
        readonly float[] _v; readonly int[] _t; readonly float _cell; readonly Dictionary<long, List<int>> _g = new Dictionary<long, List<int>>();
        public RoadFootprint(float[] verts, int[] tris, float cell = 8f)
        {
            _v = verts; _t = tris; _cell = cell;
            for (int i = 0; i < tris.Length; i += 3)
            {
                float x0 = Math.Min(_v[tris[i] * 3], Math.Min(_v[tris[i + 1] * 3], _v[tris[i + 2] * 3])), x1 = Math.Max(_v[tris[i] * 3], Math.Max(_v[tris[i + 1] * 3], _v[tris[i + 2] * 3]));
                float z0 = Math.Min(_v[tris[i] * 3 + 2], Math.Min(_v[tris[i + 1] * 3 + 2], _v[tris[i + 2] * 3 + 2])), z1 = Math.Max(_v[tris[i] * 3 + 2], Math.Max(_v[tris[i + 1] * 3 + 2], _v[tris[i + 2] * 3 + 2]));
                for (int cx = (int)Math.Floor(x0 / cell); cx <= (int)Math.Floor(x1 / cell); cx++)
                    for (int cz = (int)Math.Floor(z0 / cell); cz <= (int)Math.Floor(z1 / cell); cz++)
                    { long k = Key(cx, cz); if (!_g.TryGetValue(k, out var l)) _g[k] = l = new List<int>(4); l.Add(i); }
            }
        }
        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
        public bool IsEmpty => _g.Count == 0;

        /// <summary>Road height under (x, z) closest to <paramref name="refY"/> within [refY - below, refY + above] (several layers can overlap: bridges).</summary>
        public bool TryHeight(float x, float z, float refY, float below, float above, out float y)
        {
            y = 0; if (!_g.TryGetValue(Key((int)Math.Floor(x / _cell), (int)Math.Floor(z / _cell)), out var l)) return false;
            bool found = false; float best = float.MaxValue;
            for (int q = 0; q < l.Count; q++)
            {
                int i = l[q]; int a = _t[i] * 3, b = _t[i + 1] * 3, c = _t[i + 2] * 3;
                float ax = _v[a], az = _v[a + 2], bx = _v[b], bz = _v[b + 2], cx = _v[c], cz = _v[c + 2];
                float d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz); if (Math.Abs(d) < 1e-9f) continue;
                float u = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / d, v = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / d, w = 1 - u - v;
                const float E = -1e-4f; if (u < E || v < E || w < E) continue;
                float yy = u * _v[a + 1] + v * _v[b + 1] + w * _v[c + 1];
                if (yy < refY - below || yy > refY + above) continue;
                float dist = Math.Abs(yy - refY); if (dist < best) { best = dist; y = yy; found = true; }
            }
            return found;
        }
    }

    /// <summary>
    /// Cuts the road corridor out of a tile collider copy. A tile triangle is removed only when it is surface-like (not a wall / curb), all of its
    /// vertices, its centroid and its edge midpoints lie inside the road footprint, and it is within [-below, +above] of the road height there. So
    /// everything removed is covered by the road collider (no holes), while buildings, walls, kerbs and overpasses (outside the height band) stay.
    /// Partly covered triangles keep existing but their vertices inside the road are pressed down to the road height: no photogrammetry bump pokes through.
    /// </summary>
    public static class RoadCarver
    {
        public struct Result { public int removed, pressed; }

        public static int[] Carve(float[] v, int[] tris, RoadFootprint fp, float below, float above, out Result res)
        {
            res = new Result(); int nv = v.Length / 3;
            var roadY = new float[nv]; var inside = new bool[nv]; var steepTouch = new bool[nv];
            for (int i = 0; i < nv; i++) inside[i] = fp.TryHeight(v[i * 3], v[i * 3 + 2], v[i * 3 + 1], below, above, out roadY[i]);
            for (int t = 0; t < tris.Length; t += 3) if (Steep(v, tris[t], tris[t + 1], tris[t + 2])) { steepTouch[tris[t]] = steepTouch[tris[t + 1]] = steepTouch[tris[t + 2]] = true; }
            var keep = new List<int>(tris.Length);
            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if (inside[a] && inside[b] && inside[c] && !steepTouch[a] && !steepTouch[b] && !steepTouch[c] && CoveredInside(v, a, b, c, fp, below, above)) { res.removed++; continue; }
                keep.Add(a); keep.Add(b); keep.Add(c);
            }
            for (int i = 0; i < nv; i++)
                if (inside[i] && !steepTouch[i] && v[i * 3 + 1] > roadY[i] + 0.02f) { v[i * 3 + 1] = roadY[i] + 0.02f; res.pressed++; }
            return keep.ToArray();
        }

        static bool Steep(float[] v, int a, int b, int c)
        {
            float ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2], wx = v[c * 3] - v[a * 3], wy = v[c * 3 + 1] - v[a * 3 + 1], wz = v[c * 3 + 2] - v[a * 3 + 2];
            float nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx, l = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            return l < 1e-9f || Math.Abs(ny) / l < 0.7f;      // steeper than ~45 degrees (either winding): wall, kerb, ramp edge
        }

        static bool CoveredInside(float[] v, int a, int b, int c, RoadFootprint fp, float below, float above)
        {
            float ax = v[a * 3], ay = v[a * 3 + 1], az = v[a * 3 + 2], bx = v[b * 3], by = v[b * 3 + 1], bz = v[b * 3 + 2], cx = v[c * 3], cy = v[c * 3 + 1], cz = v[c * 3 + 2];
            return P(fp, (ax + bx + cx) / 3, (ay + by + cy) / 3, (az + bz + cz) / 3, below, above) && P(fp, (ax + bx) / 2, (ay + by) / 2, (az + bz) / 2, below, above)
                && P(fp, (bx + cx) / 2, (by + cy) / 2, (bz + cz) / 2, below, above) && P(fp, (ax + cx) / 2, (ay + cy) / 2, (az + cz) / 2, below, above);
        }
        static bool P(RoadFootprint fp, float x, float y, float z, float below, float above) => fp.TryHeight(x, z, y, below, above, out _);
    }
}
