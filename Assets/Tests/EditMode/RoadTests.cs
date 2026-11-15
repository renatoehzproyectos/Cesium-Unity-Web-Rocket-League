using NUnit.Framework;
using RocketWorld.Sim.Roads;

namespace RocketWorld.Tests
{
    /// <summary>Engine-free road core. The full suite (profile fit under noise, carving, junctions, python interop) is tools/roads/run.sh.</summary>
    public class RoadTests
    {
        [Test] public void TileFormatRoundTrips()
        {
            var d = new RoadTileData { z = 14, x = 1, y = 2, release = "r" };
            d.segments.Add(new RoadSegment { id = 5, cls = RoadClass.Primary, lon = new[] { 1.0, 1.001 }, lat = new[] { 2.0, 2.001 }, widthM = 7f });
            var r = RoadTileFormat.Read(RoadTileFormat.Write(d));
            Assert.IsNotNull(r); Assert.AreEqual(1, r.segments.Count); Assert.AreEqual(RoadClass.Primary, r.segments[0].cls);
        }
        [Test] public void CorruptTileIsRejected() { Assert.IsNull(RoadTileFormat.Read(new byte[64])); }

        [Test] public void ProfileIgnoresTreesAndRoofs()
        {
            var c = RoadCenterline.Build(new double[] { 0, 400 }, new double[] { 0, 0 }); int n = c.Count;
            var inp = new ProfileInput { hc = new float[n], hl = new float[n], hr = new float[n], maxGrade = 0.14f };
            for (int i = 0; i < n; i++) { float t = (float)(0.04 * c.s[i]); inp.hc[i] = inp.hl[i] = inp.hr[i] = t; if (i % 7 == 3) inp.hc[i] += 4f; if (i % 19 == 2) inp.hc[i] = inp.hl[i] = inp.hr[i] = t + 15f; }
            var r = RoadProfile.Solve(c, inp); Assert.IsNotNull(r);
            for (int i = 0; i < n; i++) Assert.Less(System.Math.Abs(r.up[i] - RoadProfile.SurfaceBias - 0.04 * c.s[i]), 0.35);
        }

        [Test] public void CarveKeepsStructuresOutsideTheHeightBand()
        {
            var m = new RoadMeshData(); int a = m.Add(-4, 0, 0), b = m.Add(4, 0, 0), c = m.Add(-4, 0, 10), d = m.Add(4, 0, 10); m.Tri(a, c, b); m.Tri(b, c, d);
            var fp = new RoadFootprint(m.v.ToArray(), m.c.ToArray());
            var v = new float[] { -1, 6, 4, 1, 6, 4, -1, 6, 6, 1, 6, 6 };       // an overpass 6 m above the road
            var kept = RoadCarver.Carve(v, new[] { 0, 2, 1, 1, 2, 3 }, fp, 1f, 2.2f, out var res);
            Assert.AreEqual(0, res.removed); Assert.AreEqual(6, kept.Length);
        }

        [Test] public void LruNeverEvictsPinned()
        {
            var l = new ByteLru(); l.Touch("a", 100, true); l.Touch("b", 100); var ev = l.Evict(50);
            Assert.AreEqual(1, ev.Count); Assert.AreEqual("b", ev[0]);
        }
    }
}
