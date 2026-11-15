using System;
using System.Globalization;

namespace RocketWorld.Sim
{
    /// <summary>
    /// Engine-free numbers for saved geographic zones (unit-tested): radius limits and slider mapping, points on the zone's circle, picking a
    /// point on the globe from a camera ray, and the texts shown in the zone panel. Reuses <see cref="Geo"/> for WGS84.
    /// </summary>
    public static class GeoZoneMath
    {
        public const double MinRadius = 50, MaxRadius = 50000, SliderMin = 100, SliderMax = 20000;   // keep in step with rw-geocache.js (clampRadius)
        const double A = 6378137.0, F = 1.0 / 298.257223563, MeanR = 6371008.8;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static double ClampRadius(double r) => double.IsNaN(r) ? 500 : Math.Max(MinRadius, Math.Min(MaxRadius, r));

        /// <summary>Slider position 0..1 -> radius in metres, logarithmic (fine control for small zones, reach for large ones).</summary>
        public static double RadiusFromSlider(float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Math.Round(Math.Exp(Math.Log(SliderMin) + (Math.Log(SliderMax) - Math.Log(SliderMin)) * t) / 10.0) * 10.0;
        }
        public static float SliderFromRadius(double r)
        {
            r = Math.Max(SliderMin, Math.Min(SliderMax, r));
            return (float)((Math.Log(r) - Math.Log(SliderMin)) / (Math.Log(SliderMax) - Math.Log(SliderMin)));
        }

        /// <summary>Point reached from (lat, lon) going <paramref name="distM"/> metres along <paramref name="bearingDeg"/> (clockwise from north), spherical earth.</summary>
        public static void Destination(double latDeg, double lonDeg, double bearingDeg, double distM, out double lat2, out double lon2)
        {
            double d = distM / MeanR, br = bearingDeg * Math.PI / 180.0, p1 = latDeg * Math.PI / 180.0, l1 = lonDeg * Math.PI / 180.0;
            double p2 = Math.Asin(Math.Sin(p1) * Math.Cos(d) + Math.Cos(p1) * Math.Sin(d) * Math.Cos(br));
            double l2 = l1 + Math.Atan2(Math.Sin(br) * Math.Sin(d) * Math.Cos(p1), Math.Cos(d) - Math.Sin(p1) * Math.Sin(p2));
            lat2 = p2 * 180.0 / Math.PI;
            lon2 = ((l2 * 180.0 / Math.PI + 540.0) % 360.0) - 180.0;
        }

        /// <summary>Great-circle distance in metres.</summary>
        public static double DistanceM(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * Math.PI / 180.0, p2 = lat2 * Math.PI / 180.0, dp = p2 - p1, dl = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2 * MeanR * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
        }

        /// <summary>
        /// First intersection of a ray (ECEF origin and direction) with the WGS84 ellipsoid raised by <paramref name="height"/> metres
        /// (the ground is not known far from the car: the height of the zone is the best flat guess). False when the ray misses it.
        /// </summary>
        public static bool RayEllipsoid(double ox, double oy, double oz, double dx, double dy, double dz, double height, out double t)
        {
            t = 0;
            double a = A + height, b = A * (1 - F) + height;
            double qa = (dx * dx + dy * dy) / (a * a) + dz * dz / (b * b);
            double qb = 2 * ((ox * dx + oy * dy) / (a * a) + oz * dz / (b * b));
            double qc = (ox * ox + oy * oy) / (a * a) + oz * oz / (b * b) - 1;
            if (qa <= 0) return false;
            double disc = qb * qb - 4 * qa * qc;
            if (disc < 0) return false;
            double s = Math.Sqrt(disc), t0 = (-qb - s) / (2 * qa), t1 = (-qb + s) / (2 * qa);
            if (t0 > 0) { t = t0; return true; }
            if (t1 > 0) { t = t1; return true; }
            return false;
        }

        public static string FormatBytes(double b)
        {
            if (b < 1024) return b.ToString("0", Inv) + " B";
            if (b < 1024 * 1024) return (b / 1024).ToString("0", Inv) + " KB";
            if (b < 1024.0 * 1024 * 1024) return (b / (1024 * 1024)).ToString("0.#", Inv) + " MB";
            return (b / (1024.0 * 1024 * 1024)).ToString("0.##", Inv) + " GB";
        }

        public static string FormatRadius(double m) => m < 1000 ? m.ToString("0", Inv) + " m" : (m / 1000).ToString("0.#", Inv) + " km";

        /// <summary>Short label for a zone's data state ("ready", "partial", "stale", "pending", "live").</summary>
        public static string StateLabel(string state, bool liveOnly, int stored, int expected)
        {
            switch (state)
            {
                case "ready": return liveOnly ? "ready (3D tiles: live)" : "ready";
                case "partial": return "partial " + stored + "/" + expected;
                case "stale": return "needs update";
                case "pending": return liveOnly ? "live (3D tiles follow the HTTP cache)" : "not stored yet";
                default: return state ?? "";
            }
        }
    }
}
