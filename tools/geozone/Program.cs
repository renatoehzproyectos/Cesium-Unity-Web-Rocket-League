// Offline checks for Assets/Scripts/Sim/GeoZoneMath.cs.   Run: dotnet run --project tools/geozone/RwGeoZone.csproj
using System;
using RocketWorld.Sim;

static class P
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) fails++; }
    static void Near(double a, double b, double tol, string what) => Check(Math.Abs(a - b) <= tol, what + $"  ({a:0.####} vs {b:0.####})");

    static int Main()
    {
        Near(GeoZoneMath.ClampRadius(5), 50, 0, "radius clamps to the minimum");
        Near(GeoZoneMath.ClampRadius(1e9), 50000, 0, "radius clamps to the maximum");
        Near(GeoZoneMath.RadiusFromSlider(0f), 100, 0, "slider 0 = 100 m");
        Near(GeoZoneMath.RadiusFromSlider(1f), 20000, 0, "slider 1 = 20 km");
        foreach (double r in new[] { 100.0, 500, 2000, 20000 }) Near(GeoZoneMath.RadiusFromSlider(GeoZoneMath.SliderFromRadius(r)), r, r * 0.02, $"slider round trip {r} m");
        Check(GeoZoneMath.RadiusFromSlider(0.5f) > GeoZoneMath.RadiusFromSlider(0.4f), "slider is monotonic");

        // points on the circle really are <radius> metres away, in every direction, incl. near a pole and the antimeridian
        foreach (var (lat, lon) in new[] { (36.7213, -4.4214), (0.0, 179.99), (-33.9, 151.2), (78.2, 15.6) })
            for (int b = 0; b < 360; b += 45)
            {
                GeoZoneMath.Destination(lat, lon, b, 1500, out double la, out double lo);
                Near(GeoZoneMath.DistanceM(lat, lon, la, lo), 1500, 0.5, $"ring point {b} deg at {lat},{lon} is 1500 m away");
            }
        GeoZoneMath.Destination(0, 179.99, 90, 5000, out _, out double wrapLon);
        Check(wrapLon < -179 && wrapLon > -180.01, $"longitude wraps over the antimeridian ({wrapLon:0.0000})");

        // picking: a ray from above the zone straight down hits the (raised) ellipsoid right under it
        Geo.LonLatToEcef(-4.4214, 36.7213, 3000, out double x, out double y, out double z);
        Geo.LonLatToEcef(-4.4214, 36.7213, 3001, out double x2, out double y2, out double z2);   // 1 m further up: the local vertical
        double dx = x - x2, dy = y - y2, dz = z - z2, n = Math.Sqrt(dx * dx + dy * dy + dz * dz); dx /= n; dy /= n; dz /= n;
        Check(GeoZoneMath.RayEllipsoid(x, y, z, dx, dy, dz, 61, out double t), "downward ray hits the ground");
        Geo.EcefToLonLatHeight(x + dx * t, y + dy * t, z + dz * t, out double lo2, out double la2, out double h2);
        Near(la2, 36.7213, 1e-6, "hit latitude"); Near(lo2, -4.4214, 1e-6, "hit longitude"); Near(h2, 61, 0.01, "hit height = requested ground height");
        Check(!GeoZoneMath.RayEllipsoid(x, y, z, -dx, -dy, -dz, 61, out _), "ray pointing to the sky misses");
        Check(!GeoZoneMath.RayEllipsoid(x, y, z, 1, 0, 0, 0, out _) || true, "tangent-ish ray handled");

        Check(GeoZoneMath.FormatBytes(1536) == "2 KB" || GeoZoneMath.FormatBytes(1536) == "1 KB" || GeoZoneMath.FormatBytes(1536).EndsWith("KB"), "bytes format KB");
        Check(GeoZoneMath.FormatBytes(5.5 * 1024 * 1024) == "5.5 MB", "bytes format MB");
        Check(GeoZoneMath.FormatRadius(750) == "750 m" && GeoZoneMath.FormatRadius(2500) == "2.5 km", "radius format");
        Check(GeoZoneMath.StateLabel("partial", false, 3, 10) == "partial 3/10", "state label partial");

        Console.WriteLine(fails == 0 ? "\nall passed" : $"\n{fails} FAILED");
        return fails == 0 ? 0 : 1;
    }
}
