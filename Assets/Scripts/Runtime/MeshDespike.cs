using System.Collections.Generic;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Builds a cleaned copy of a photogrammetry tile mesh for use as a MeshCollider only (the visual mesh is never touched):
    ///  1. Spike removal: a vertex that sticks out above/below ALL its neighbours by more than a slope-aware threshold is pulled back to the
    ///     neighbours' average height (several passes, so 2-3 vertex wide needles disappear too).
    ///  2. Light smoothing: a gentle vertical-only Laplacian on gently sloped vertices (walls, curbs and ramps are left alone).
    /// Works in the tile's local space; "up" is world up converted to local space. Vertices are welded by position first, because Cesium
    /// meshes duplicate vertices along seams and per-face normals/UVs, which would otherwise hide the real neighbourhood.
    /// </summary>
    public static class MeshDespike
    {
        // ---- tuning ----
        public static float SpikeHeight = 0.5f;      // m above/below every neighbour that is always suspicious
        public static float SpikeSlope = 1.5f;       // extra allowed height per metre of neighbour distance (1.5 = ~56 degrees)
        public static int SpikePasses = 3;
        public static int SmoothPasses = 1;
        public static float SmoothLambda = 0.35f;    // 0 = no smoothing, 1 = full average
        public static float SmoothMaxSlope = 1.0f;   // only smooth where local relief / neighbour distance is below this (45 degrees)
        public static float WeldCell = 0.02f;        // m, vertex welding grid

        static readonly Dictionary<Vector3Int, int> _weld = new Dictionary<Vector3Int, int>(8192);
        static readonly List<Vector3> _verts = new List<Vector3>(8192);
        static readonly List<int> _tris = new List<int>(32768);

        /// <summary>Returns a new cleaned mesh, or null if the source is not readable / has nothing to clean (caller then uses the original).</summary>
        public static Mesh Build(Mesh src, Vector3 upLocal)
        {
            if (src == null || !src.isReadable || src.vertexCount < 4 || src.subMeshCount < 1) return null;
            upLocal = upLocal.sqrMagnitude < 1e-6f ? Vector3.up : upLocal.normalized;

            _verts.Clear(); src.GetVertices(_verts);
            int n = _verts.Count;

            // weld by position
            _weld.Clear();
            var map = new int[n];
            var wp = new List<Vector3>(n);
            float inv = 1f / WeldCell;
            for (int i = 0; i < n; i++)
            {
                var p = _verts[i];
                var key = new Vector3Int(Mathf.RoundToInt(p.x * inv), Mathf.RoundToInt(p.y * inv), Mathf.RoundToInt(p.z * inv));
                if (!_weld.TryGetValue(key, out int w)) { w = wp.Count; _weld.Add(key, w); wp.Add(p); }
                map[i] = w;
            }
            int wn = wp.Count;

            // adjacency (CSR, duplicates are harmless) over all submeshes
            var triAll = new List<int>(src.triangles.Length);
            for (int s = 0; s < src.subMeshCount; s++)
            {
                if (src.GetTopology(s) != MeshTopology.Triangles) continue;
                _tris.Clear(); src.GetTriangles(_tris, s); triAll.AddRange(_tris);
            }
            if (triAll.Count < 3) return null;
            var deg = new int[wn + 1];
            for (int t = 0; t < triAll.Count; t += 3)
                for (int k = 0; k < 3; k++) deg[map[triAll[t + k]] + 1] += 2;
            for (int i = 0; i < wn; i++) deg[i + 1] += deg[i];
            var fill = new int[wn];
            var adj = new int[deg[wn]];
            for (int t = 0; t < triAll.Count; t += 3)
            {
                int a = map[triAll[t]], b = map[triAll[t + 1]], c = map[triAll[t + 2]];
                if (a == b || b == c || a == c) continue;
                adj[deg[a] + fill[a]++] = b; adj[deg[a] + fill[a]++] = c;
                adj[deg[b] + fill[b]++] = a; adj[deg[b] + fill[b]++] = c;
                adj[deg[c] + fill[c]++] = a; adj[deg[c] + fill[c]++] = b;
            }

            var pos = wp.ToArray();
            var h = new float[wn];
            var nh = new float[wn];
            bool changed = false;

            for (int i = 0; i < wn; i++) h[i] = Vector3.Dot(pos[i], upLocal);

            // 1. spikes
            for (int pass = 0; pass < SpikePasses; pass++)
            {
                int fixedCount = 0;
                System.Array.Copy(h, nh, wn);
                for (int i = 0; i < wn; i++)
                {
                    int cnt = fill[i]; if (cnt == 0) continue;
                    float max = float.MinValue, min = float.MaxValue, sum = 0, dist = 0;
                    for (int k = 0; k < cnt; k++)
                    {
                        int j = adj[deg[i] + k];
                        float hj = h[j]; sum += hj;
                        if (hj > max) max = hj; if (hj < min) min = hj;
                        var d = pos[j] - pos[i]; d -= upLocal * Vector3.Dot(d, upLocal);   // horizontal distance
                        dist += d.magnitude;
                    }
                    float allow = SpikeHeight + SpikeSlope * (dist / cnt);
                    if (h[i] > max + allow || h[i] < min - allow) { nh[i] = sum / cnt; fixedCount++; }
                }
                var tmp = h; h = nh; nh = tmp;
                if (fixedCount == 0) break;
                changed = true;
            }

            // 2. gentle smoothing of gently sloped vertices (vertical only)
            for (int pass = 0; pass < SmoothPasses && SmoothLambda > 0f; pass++)
            {
                System.Array.Copy(h, nh, wn);
                for (int i = 0; i < wn; i++)
                {
                    int cnt = fill[i]; if (cnt == 0) continue;
                    float max = float.MinValue, min = float.MaxValue, sum = 0, dist = 0;
                    for (int k = 0; k < cnt; k++)
                    {
                        int j = adj[deg[i] + k]; float hj = h[j]; sum += hj;
                        if (hj > max) max = hj; if (hj < min) min = hj;
                        var d = pos[j] - pos[i]; d -= upLocal * Vector3.Dot(d, upLocal); dist += d.magnitude;
                    }
                    float md = Mathf.Max(0.05f, dist / cnt);
                    if ((max - min) / md > SmoothMaxSlope) continue;      // wall / curb / steep: keep sharp
                    nh[i] = h[i] + SmoothLambda * (sum / cnt - h[i]);
                }
                var tmp = h; h = nh; nh = tmp;
                changed = true;
            }
            if (!changed) return null;

            // write back: move each original vertex along up by its welded vertex's height change
            var outV = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                int w = map[i];
                float orig = Vector3.Dot(_verts[i], upLocal);
                outV[i] = _verts[i] + upLocal * (h[w] - orig);
            }
            var m = new Mesh { name = src.name + "_collider", indexFormat = src.indexFormat };
            m.vertices = outV;
            m.subMeshCount = src.subMeshCount;
            for (int s = 0; s < src.subMeshCount; s++)
                m.SetIndices(src.GetIndices(s), src.GetTopology(s), s, false);
            m.RecalculateBounds();
            return m;
        }
    }
}
