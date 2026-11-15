using NUnit.Framework;
using RocketWorld.Sim;

namespace RocketWorld.Tests
{
    public class GeoZoneMathTests
    {
        [Test] public void RadiusIsClampedAndSliderIsLogarithmicAndInvertible()
        {
            Assert.AreEqual(50.0, GeoZoneMath.ClampRadius(1), 1e-9);
            Assert.AreEqual(50000.0, GeoZoneMath.ClampRadius(1e9), 1e-9);
            Assert.AreEqual(100.0, GeoZoneMath.RadiusFromSlider(0f), 1e-9);
            Assert.AreEqual(20000.0, GeoZoneMath.RadiusFromSlider(1f), 1e-9);
            Assert.AreEqual(2000.0, GeoZoneMath.RadiusFromSlider(GeoZoneMath.SliderFromRadius(2000.0)), 40.0);
        }
        [Test] public void RingPointsAreExactlyOneRadiusAway()
        {
            for (int b = 0; b < 360; b += 45)
            {
                GeoZoneMath.Destination(36.7213, -4.4214, b, 1500, out double la, out double lo);
                Assert.AreEqual(1500.0, GeoZoneMath.DistanceM(36.7213, -4.4214, la, lo), 0.5);
            }
        }
        [Test] public void DownwardRayHitsTheRaisedEllipsoidUnderTheCamera()
        {
            Geo.LonLatToEcef(-4.4214, 36.7213, 3000, out double x, out double y, out double z);
            Geo.LonLatToEcef(-4.4214, 36.7213, 3001, out double x2, out double y2, out double z2);
            double dx = x - x2, dy = y - y2, dz = z - z2, n = System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
            Assert.IsTrue(GeoZoneMath.RayEllipsoid(x, y, z, dx / n, dy / n, dz / n, 61, out double t));
            Geo.EcefToLonLatHeight(x + dx / n * t, y + dy / n * t, z + dz / n * t, out double lon, out double lat, out double h);
            Assert.AreEqual(36.7213, lat, 1e-6); Assert.AreEqual(-4.4214, lon, 1e-6); Assert.AreEqual(61.0, h, 0.01);
            Assert.IsFalse(GeoZoneMath.RayEllipsoid(x, y, z, -dx / n, -dy / n, -dz / n, 61, out _));
        }
    }
}
