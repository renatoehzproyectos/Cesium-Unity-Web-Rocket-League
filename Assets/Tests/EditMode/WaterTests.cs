using System;
using NUnit.Framework;
using RocketWorld.Sim;

namespace RocketWorld.Tests
{
    /// <summary>Water system, engine-free parts: vector-tile decoding, point-in-water, bridges, geodesy. Same checks as tools/water.</summary>
    public class WaterTests
    {
        static WaterTile Tile() => MvtReader.Read(WaterTestTiles.BuildTile());

        [Test] public void DecodesOnlyNavigableWater()
        {
            var t = Tile();
            Assert.AreEqual(2, t.water.Count);                                  // lake + ocean; the tunnel and the swimming pool are dropped
            Assert.IsFalse(t.IsWater(3700, 3700, out _));
            Assert.IsFalse(t.IsWater(200, 200, out _));
        }

        [Test] public void PointInWaterHonoursIslandsAndKinds()
        {
            var t = Tile(); WaterKind k;
            Assert.IsTrue(t.IsWater(1500, 1500, out k)); Assert.AreEqual(WaterKind.Lake, k);
            Assert.IsFalse(t.IsWater(2000, 2000, out _));                       // island hole
            Assert.IsFalse(t.IsWater(900, 1500, out _));
            Assert.IsTrue(t.IsWater(3700, 300, out k)); Assert.AreEqual(WaterKind.Ocean, k);
        }

        [Test] public void BridgesAndPiersAreSolidFerriesAreNot()
        {
            var t = Tile();
            Assert.AreEqual(2, t.solidLines.Count);
            Assert.IsTrue(t.NearSolid(1500, 2010, 20));
            Assert.IsFalse(t.NearSolid(1500, 2100, 20));
            Assert.IsTrue(t.NearSolid(2005, 2700, 20));
            Assert.IsFalse(t.NearSolid(2000, 1200, 20));
        }

        [Test] public void GarbageDoesNotHang()
        {
            try { MvtReader.Read(new byte[] { 0x1a, 0xff, 0xff, 0xff, 0xff, 0x0f }); }
            catch (Exception) { /* a catchable exception is fine; WaterMap retries later */ }
        }

        [Test] public void GeodesyRoundTripsAndTileMathMatchesReference()
        {
            foreach (var c in new[] { (-4.4214, 36.7213, 61.0), (-73.9855, 40.758, -23.0), (151.2093, -33.8688, 20.0), (-0.0001, 89.0, 100.0) })
            {
                Geo.LonLatToEcef(c.Item1, c.Item2, c.Item3, out double x, out double y, out double z);
                Geo.EcefToLonLat(x, y, z, out double lon, out double lat);
                Assert.AreEqual(c.Item1, lon, 1e-9); Assert.AreEqual(c.Item2, lat, 1e-9);
            }
            Geo.LonLatToTile(-4.4214, 36.7213, 14, out double tx, out double ty);      // python reference: 7990.777173333, 6392.998516945
            Assert.AreEqual(7990.777173333, tx, 1e-8); Assert.AreEqual(6392.998516945, ty, 1e-8);
        }
    }
}
