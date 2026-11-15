using System;
using System.Collections.Generic;
using System.Linq;
using RocketWorld.Sim;
using RocketWorld.Sim.Roads;

static class T
{
    static int fails, checks;
    static void Ok(bool c, string m) { checks++; if (!c) { fails++; Console.WriteLine("FAIL: " + m); } }
    static Random rnd = new Random(7);

    static int Main(string[] args)
    {
        if (args.Length > 0) return Interop(args[0]);
        Format(); Center(); Profile(); Mesh(); Carve(); Chunk(); Cache();
        Console.WriteLine($"{checks - fails}/{checks} checks passed"); return fails == 0 ? 0 : 1;
    }

    /// <summary>Reads a tile written by build_tiles.py (tools/roads/run.sh) and checks the values the script put in.</summary>
    static int Interop(string path)
    {
        var d = RoadTileFormat.Read(System.IO.File.ReadAllBytes(path));
        Ok(d != null && d.z == 14 && d.x == 8000 && d.y == 5000 && d.release == "2026-10-15.0" && d.segments.Count == 1, "python tile header");
        var s = d.segments[0];
        Ok(s.cls == RoadClass.Primary && s.surface == RoadSurface.Asphalt && s.IsBridge && s.level == 1 && Math.Abs(s.widthM - 7.5f) < 0.06f && s.lanes == 2, "python tile attributes");
        Ok(s.connectors.Length == 2 && s.lon.Length == 3 && Math.Abs(s.lon[2] + 3.6990) < 1e-7 && Math.Abs(s.lat[1] - 40.4174) < 1e-7, "python tile geometry");
        Console.WriteLine($"{checks - fails}/{checks} interop checks passed"); return fails == 0 ? 0 : 1;
    }

    static RoadSegment Seg(uint id, RoadClass c, double lon0, double lat0, params double[] dEN)   // dEN: pairs of east/north offsets in metres
    {
        var f = new RoadFrame(lon0, lat0, 0); var lon = new List<double> { lon0 }; var lat = new List<double> { lat0 };
        for (int i = 0; i < dEN.Length; i += 2) { f.ToLonLat(dEN[i], dEN[i + 1], out double a, out double b); lon.Add(a); lat.Add(b); }
        return new RoadSegment { id = id, cls = c, lon = lon.ToArray(), lat = lat.ToArray(), widthM = 7f };
    }

    static void Format()
    {
        var d = new RoadTileData { z = 14, x = 8000, y = 5000, release = "2026-10-15.0" };
        var s = Seg(42, RoadClass.Primary, -3.7038, 40.4168, 100, 50, 250, 20); s.flags = RoadFlags.Bridge; s.level = 1; s.lanes = 2; s.connectors = new[] { new RoadConnector(7, 0f), new RoadConnector(9, 1f) };
        d.segments.Add(s);
        var r = RoadTileFormat.Read(RoadTileFormat.Write(d));
        Ok(r != null && r.segments.Count == 1 && r.release == d.release && r.x == 8000, "format header");
        var q = r.segments[0];
        Ok(q.id == 42 && q.IsBridge && q.level == 1 && q.lanes == 2 && q.connectors.Length == 2 && q.connectors[1].id == 9, "format attrs");
        Ok(Math.Abs(q.lon[2] - s.lon[2]) < 1e-7 && Math.Abs(q.lat[1] - s.lat[1]) < 1e-7, "format coords 1e-7");
        var bytes = RoadTileFormat.Write(d); Ok(RoadTileFormat.Read(bytes.Take(bytes.Length - 5).ToArray()) == null, "truncated -> null");
        Ok(RoadTileFormat.Read(new byte[40]) == null, "garbage -> null");
    }

    static void Center()
    {
        double[] e = { 0, 50, 100, 100 }, n = { 0, 0, 0, 80 };
        var c = RoadCenterline.Build(e, n);
        Ok(c != null && Math.Abs(c.e[0]) < 1e-6 && Math.Abs(c.e[c.Count - 1] - 100) < 1e-6 && Math.Abs(c.n[c.Count - 1] - 80) < 1e-6, "ends kept");
        double minD = 1e9; for (int i = 0; i < c.Count; i++) minD = Math.Min(minD, Math.Sqrt((c.e[i] - 50) * (c.e[i] - 50) + c.n[i] * c.n[i])); Ok(minD < 2.1, "passes through vertex");
        double maxStep = 0; for (int i = 1; i < c.Count; i++) maxStep = Math.Max(maxStep, c.s[i] - c.s[i - 1]); Ok(maxStep < 4.5, "station spacing <= ~4 m: " + maxStep);
        double maxTurn = 0; for (int i = 1; i < c.Count; i++) { double a = Math.Acos(Math.Max(-1, Math.Min(1, c.tx[i] * c.tx[i - 1] + c.tz[i] * c.tz[i - 1]))); maxTurn = Math.Max(maxTurn, a); }
        Ok(maxTurn < 0.45, "corner rounded (max turn per station " + maxTurn + ")");
    }

    static float Truth(double s) => (float)(0.05 * s + 1.5 * Math.Sin(s / 90.0));   // 5 % grade + gentle vertical curve

    static void Profile()
    {
        var c = RoadCenterline.Build(new double[] { 0, 600 }, new double[] { 0, 0 });
        int n = c.Count; var inp = new ProfileInput { hc = new float[n], hl = new float[n], hr = new float[n], halfOffset = 2.1f, maxGrade = 0.14f };
        for (int i = 0; i < n; i++)
        {
            float t = Truth(c.s[i]); Func<float> noise = () => (float)(rnd.NextDouble() - 0.5) * 0.3f;
            inp.hc[i] = t + noise(); inp.hl[i] = t + noise() - 0.1f; inp.hr[i] = t + noise() + 0.1f;      // cross slope: right side 0.1 higher over 2.1 m each side -> ~ 4.7 %
            if (i % 9 == 4) inp.hc[i] += 3.5f;           // tree / truck on the centre line
            if (i % 23 == 5) { inp.hc[i] += 14f; inp.hl[i] += 14f; inp.hr[i] += 14f; }   // building roof in every ray
            if (i > 60 && i < 66) { inp.hc[i] = inp.hl[i] = inp.hr[i] = float.NaN; }      // tile hole
        }
        var r = RoadProfile.Solve(c, inp);
        Ok(r != null, "profile solved");
        double maxErr = 0, maxDz = 0; for (int i = 0; i < n; i++) { maxErr = Math.Max(maxErr, Math.Abs(r.up[i] - RoadProfile.SurfaceBias - Truth(c.s[i]))); if (i > 0) maxDz = Math.Max(maxDz, Math.Abs(r.up[i] - r.up[i - 1]) / (c.s[i] - c.s[i - 1])); }
        Ok(maxErr < 0.35, "profile error vs truth < 0.35 m despite 3.5 m trees and 14 m roofs: " + maxErr);
        Ok(maxDz <= 0.14 + 1e-3, "grade <= limit " + maxDz);
        double acc = 0; for (int i = 1; i < n - 1; i++) acc = Math.Max(acc, Math.Abs(r.up[i - 1] - 2 * r.up[i] + r.up[i + 1])); Ok(acc < 0.12, "second difference small (no kinks): " + acc);
        float mb = r.bank.Skip(10).Take(n - 20).Average(); Ok(mb > 0.03f && mb < 0.07f, "cross slope ~4.7 %: " + mb);

        var sparse = new ProfileInput { hc = Enumerable.Repeat(float.NaN, n).ToArray(), hl = Enumerable.Repeat(float.NaN, n).ToArray(), hr = Enumerable.Repeat(float.NaN, n).ToArray() };
        Ok(RoadProfile.Solve(c, sparse) == null, "no data -> not ready");
        var br = new ProfileInput { ignoreInterior = true, startPin = 10f, endPin = 12f, maxGrade = 0.1f, hc = inp.hc };
        var rb = RoadProfile.Solve(c, br);
        Ok(rb != null && Math.Abs(rb.up[0] - 10f) < 0.1 && Math.Abs(rb.up[n - 1] - 12f) < 0.1 && Math.Abs(rb.up[n / 2] - 11f) < 0.2, "bridge ignores deck samples, interpolates between pinned ends");
    }

    static void Mesh()
    {
        var f = new RoadFrame(2.35, 48.85, 0);
        var segs = new List<BuiltSegment>();
        var a = Seg(1, RoadClass.Primary, 2.35, 48.85, 100, 0); a.connectors = new[] { new RoadConnector(100, 0f), new RoadConnector(101, 1f) };
        var b = Seg(2, RoadClass.Residential, 2.35, 48.85, 0, 100); b.connectors = new[] { new RoadConnector(100, 0f), new RoadConnector(102, 1f) };
        var c = Seg(3, RoadClass.Secondary, 2.35, 48.85, -100, 0); c.connectors = new[] { new RoadConnector(100, 0f), new RoadConnector(103, 1f) };
        foreach (var s in new[] { a, b, c })
        {
            var pe = new double[s.lon.Length]; var pn = new double[s.lon.Length]; for (int i = 0; i < pe.Length; i++) f.ToPlane(s.lon[i], s.lat[i], out pe[i], out pn[i]);
            var line = RoadCenterline.Build(pe, pn); segs.Add(new BuiltSegment { src = s, line = line, up = new float[line.Count], bank = new float[line.Count], halfWidth = RoadClassInfo.Width(s) / 2, nodeA = s.connectors[0].id, nodeB = s.connectors[1].id });
        }
        var m = RoadMeshBuilder.Build(segs);
        int down = 0; for (int i = 0; i < m.t.Count; i += 3) { var n = Normal(m, m.t[i], m.t[i + 1], m.t[i + 2]); if (n.y <= 0) down++; }
        Ok(down == 0, "all triangles face up: " + down + " down of " + m.TriangleCount);
        var fp = new RoadFootprint(m.v.ToArray(), m.c.ToArray());
        bool gapFree = true; for (double r = 0; r < 2.4; r += 0.4) for (double ang = 0; ang < 6.28; ang += 0.2) if (!fp.TryHeight((float)(r * Math.Cos(ang)), (float)(r * Math.Sin(ang)), 0, 1, 1, out _)) gapFree = false;
        Ok(gapFree, "junction centre has no gaps");

        // sharp bend must not fold: all quad strips keep positive area
        var tight = RoadCenterline.Build(new double[] { 0, 20, 22, 20 }, new double[] { 0, 0, 3, 6 });
        var ts = new BuiltSegment { src = a, line = tight, up = new float[tight.Count], bank = new float[tight.Count], halfWidth = 4f };
        var tm = RoadMeshBuilder.Build(new[] { ts }); int bad = 0;
        for (int i = 0; i < tm.t.Count; i += 3) if (Normal(tm, tm.t[i], tm.t[i + 1], tm.t[i + 2]).y <= 0) bad++;
        Ok(bad == 0, "tight bend does not fold: " + bad);
    }

    static (float x, float y, float z) Normal(RoadMeshData m, int a, int b, int c)
    {
        float[] v = m.v.ToArray(); float ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2], wx = v[c * 3] - v[a * 3], wy = v[c * 3 + 1] - v[a * 3 + 1], wz = v[c * 3 + 2] - v[a * 3 + 2];
        return (uy * wz - uz * wy, uz * wx - ux * wz, ux * wy - uy * wx);
    }

    static void Carve()
    {
        // road: strip x in [-4,4], z in [0,100], height 0; tile: 1 m grid at height ~0 with noise, a 0.6 m bump on the road, a kerb wall at x=4..4.2 and a roof 5 m above
        var rm = new RoadMeshData(); int a = rm.Add(-4, 0, 0), b = rm.Add(4, 0, 0), c = rm.Add(-4, 0, 100), d = rm.Add(4, 0, 100); rm.Tri(a, c, b); rm.Tri(b, c, d);
        var fp = new RoadFootprint(rm.v.ToArray(), rm.c.ToArray());
        var v = new List<float>(); var t = new List<int>(); int W = 21, H = 101;
        for (int i = 0; i < H; i++) for (int j = 0; j < W; j++) { float x = j - 10, z = i; float y = (float)((rnd.NextDouble() - 0.5) * 0.1); if (Math.Abs(x) <= 3 && z > 40 && z < 44) y += 0.6f; if (x >= 5) y += 0.3f; v.Add(x); v.Add(y); v.Add(z); }
        for (int i = 0; i < H - 1; i++) for (int j = 0; j < W - 1; j++) { int p = i * W + j; t.AddRange(new[] { p, p + W, p + 1, p + 1, p + W, p + W + 1 }); }
        int roof = v.Count / 3; foreach (var q in new[] { (-2f, 20f), (2f, 20f), (-2f, 24f), (2f, 24f) }) { v.Add(q.Item1); v.Add(5f); v.Add(q.Item2); }
        t.AddRange(new[] { roof, roof + 2, roof + 1, roof + 1, roof + 2, roof + 3 });
        var va = v.ToArray(); var ta = t.ToArray();
        var kept = RoadCarver.Carve(va, ta, fp, 1.0f, 2.2f, out var res);
        Ok(res.removed > 150, "road triangles removed: " + res.removed);
        Ok(kept.Length / 3 > ta.Length / 3 - res.removed - 1 && kept.Length / 3 < ta.Length / 3, "kept count consistent");
        bool holes = false;       // every removed triangle must lie inside the footprint (so the road collider covers it)
        var keptSet = new HashSet<int>(); for (int i = 0; i < kept.Length; i += 3) keptSet.Add(kept[i] * 1000003 ^ kept[i + 1] * 7919 ^ kept[i + 2]);
        for (int i = 0; i < ta.Length; i += 3) if (!keptSet.Contains(ta[i] * 1000003 ^ ta[i + 1] * 7919 ^ ta[i + 2]))
            { float cx = (va[ta[i] * 3] + va[ta[i + 1] * 3] + va[ta[i + 2] * 3]) / 3, cz = (va[ta[i] * 3 + 2] + va[ta[i + 1] * 3 + 2] + va[ta[i + 2] * 3 + 2]) / 3; if (Math.Abs(cx) > 4 || cz < 0 || cz > 100) holes = true; }
        Ok(!holes, "no removed triangle outside the road footprint");
        float maxRoadY = 0; for (int i = 0; i < kept.Length; i++) { int vi = kept[i]; if (Math.Abs(va[vi * 3]) < 3.9f && va[vi * 3 + 2] > 1 && va[vi * 3 + 2] < 99 && va[vi * 3 + 1] < 2) maxRoadY = Math.Max(maxRoadY, va[vi * 3 + 1]); }
        Ok(maxRoadY <= 0.05f, "no bump above the road survives: " + maxRoadY);
        bool roofKept = kept.Contains(roof), kerb = kept.Contains(0 * W + 14 + 0) || kept.Any(x => va[x * 3] >= 5);
        Ok(roofKept, "overhead structure (5 m up) is kept"); Ok(kerb, "terrain beside the road is kept");
    }

    static void Chunk()
    {
        var f = new RoadFrame(-3.7, 40.42, 0); var nodes = new NodeHeights();
        var s1 = Seg(11, RoadClass.Secondary, -3.7, 40.42, 300, 0); s1.connectors = new[] { new RoadConnector(500, 0f), new RoadConnector(501, 1f) };
        var s2 = Seg(12, RoadClass.Secondary, -3.7, 40.42, 300, 0); // placeholder replaced below
        f.ToLonLat(300, 0, out double lx, out double ly); s2 = new RoadSegment { id = 12, cls = RoadClass.Secondary, widthM = 7, lon = new[] { lx, f.lon + 0.004 }, lat = new[] { ly, ly + 0.0009 }, connectors = new[] { new RoadConnector(501, 0f), new RoadConnector(502, 1f) } };
        var rng = new Random(3);
        RoadChunkBuilder.GroundSampler g = (e, n) => { if (rng.NextDouble() < 0.1) return float.NaN; float h = (float)(0.04 * e + 0.5 * Math.Sin(n / 30)); if (rng.NextDouble() < 0.1) h += 3f; return h + (float)(rng.NextDouble() - 0.5) * 0.2f; };
        var bld = new RoadChunkBuilder(f, new List<RoadSegment> { s1, s2 }, g, nodes);
        while (!bld.Step(1)) { }
        var res = bld.Finish();
        Ok(res.segments.Count == 2 && res.pending == 0, "both segments solved");
        Ok(nodes.TryGet(501, out float hn), "shared node published");
        var a = res.segments[0]; var b = res.segments[1];
        Ok(Math.Abs(a.up[a.up.Length - 1] - b.up[0]) < 0.05f, "shared node same height on both sides: " + (a.up[a.up.Length - 1] - b.up[0]));
        // processed cache: second build without sampler samples must give the same profile
        var blob = RoadChunkBuilder.SaveProcessed(bld.Solved, "r1"); var back = RoadChunkBuilder.LoadProcessed(blob, "r1");
        Ok(back != null && back.Count == 2 && RoadChunkBuilder.LoadProcessed(blob, "r2") == null, "processed cache roundtrip and release check");
        var bld2 = new RoadChunkBuilder(f, new List<RoadSegment> { s1, s2 }, (e, n) => { throw new Exception("sampler must not be called"); }, new NodeHeights(), back);
        while (!bld2.Step(5)) { } var res2 = bld2.Finish();
        Ok(res2.fromCache == 2 && Math.Abs(res2.segments[0].up[10] - a.up[10]) < 1e-5, "cache reuse without sampling");
        var nh = new NodeHeights(); nh.Load(nodes.Save()); Ok(nh.TryGet(501, out float h2) && Math.Abs(h2 - hn) < 1e-6, "node heights persist");
        var inc = new RoadChunkBuilder(f, new List<RoadSegment> { s1, s2 }, (e, n) => float.NaN, new NodeHeights()); while (!inc.Step(2)) { }
        Ok(inc.Finish().pending == 2, "no ground yet -> segments stay pending (no collider from guesses)");
    }

    static void Cache()
    {
        var t = RoadCachePolicy.TilesAround(-3.7038, 40.4168, 1500);
        Ok(t.Count >= 1 && t.Count <= 9 && t[0].d == 0, "tiles around: " + t.Count + " nearest dist " + t[0].d);
        var far = RoadCachePolicy.TilesAround(179.999, 0, 3000); Ok(far.Count >= 2 && far.Any(q => q.x < 10) && far.Any(q => q.x > (1 << 14) - 10), "antimeridian wraps");
        var l = new ByteLru(); l.Touch("a", 100); l.Touch("b", 100, pinned: true); l.Touch("c", 100); l.Touch("a", 100);
        var ev = l.Evict(250); Ok(ev.Count == 1 && ev[0] == "c" && l.Total == 200, "LRU skips pinned, evicts oldest: " + string.Join(",", ev));
        Ok(RoadCachePolicy.IsCurrent("2026-10-15.0", "2026-10-15.0") && !RoadCachePolicy.IsCurrent("2026-09-17.0", "2026-10-15.0") && !RoadCachePolicy.IsCurrent("x", ""), "version check");
        Ok(RoadCachePolicy.FetchRadius(0, 3, true) < RoadCachePolicy.FetchRadius(60, 0, false), "radius shrinks on phones / low memory");
    }
}
