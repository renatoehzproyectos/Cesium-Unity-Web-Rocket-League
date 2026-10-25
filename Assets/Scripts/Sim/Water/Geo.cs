using System;

namespace RocketWorld.Sim
{
    /// <summary>WGS84 / Web-Mercator helpers (engine-free, double precision).</summary>
    public static class Geo
    {
        const double A = 6378137.0, F = 1.0 / 298.257223563, E2 = F * (2 - F);

        /// <summary>Earth-Centered Earth-Fixed (m) to geodetic longitude / latitude (degrees), iterative Bowring.</summary>
        public static void EcefToLonLat(double x, double y, double z, out double lonDeg, out double latDeg)
        {
            double p = Math.Sqrt(x * x + y * y);
            double lat = Math.Atan2(z, p * (1 - E2));
            for (int i = 0; i < 6; i++)
            {
                double s = Math.Sin(lat), n = A / Math.Sqrt(1 - E2 * s * s);
                lat = Math.Atan2(z + E2 * n * s, p);
            }
            lonDeg = Math.Atan2(y, x) * 180.0 / Math.PI;
            latDeg = lat * 180.0 / Math.PI;
        }

        /// <summary>ECEF to geodetic longitude / latitude (deg) and ellipsoidal height (m).</summary>
        public static void EcefToLonLatHeight(double x, double y, double z, out double lonDeg, out double latDeg, out double h)
        {
            EcefToLonLat(x, y, z, out lonDeg, out latDeg);
            double lat = latDeg * Math.PI / 180.0, p = Math.Sqrt(x * x + y * y), s = Math.Sin(lat), n = A / Math.Sqrt(1 - E2 * s * s);
            h = Math.Abs(Math.Cos(lat)) > 1e-6 ? p / Math.Cos(lat) - n : Math.Abs(z) / Math.Abs(s) - n * (1 - E2);
        }

        /// <summary>Geodetic (deg, height m) to ECEF. Used by tests and by anything that needs the inverse of <see cref="EcefToLonLat"/>.</summary>
        public static void LonLatToEcef(double lonDeg, double latDeg, double h, out double x, out double y, out double z)
        {
            double lon = lonDeg * Math.PI / 180.0, lat = latDeg * Math.PI / 180.0, s = Math.Sin(lat), c = Math.Cos(lat);
            double n = A / Math.Sqrt(1 - E2 * s * s);
            x = (n + h) * c * Math.Cos(lon); y = (n + h) * c * Math.Sin(lon); z = (n * (1 - E2) + h) * s;
        }

        /// <summary>Fractional slippy-map tile coordinates at zoom <paramref name="zoom"/> (y grows southwards, like MVT).</summary>
        public static void LonLatToTile(double lonDeg, double latDeg, int zoom, out double tx, out double ty)
        {
            double n = Math.Pow(2, zoom);
            double lat = Math.Max(-85.0511287798, Math.Min(85.0511287798, latDeg)) * Math.PI / 180.0;
            tx = (lonDeg + 180.0) / 360.0 * n;
            ty = (1.0 - Math.Log(Math.Tan(lat) + 1.0 / Math.Cos(lat)) / Math.PI) / 2.0 * n;
        }

        /// <summary>Metres per tile unit (extent 4096) at this latitude and zoom: tells how fine the data is.</summary>
        public static double MetresPerUnit(double latDeg, int zoom, int extent = 4096)
            => 40075016.686 * Math.Cos(latDeg * Math.PI / 180.0) / Math.Pow(2, zoom) / extent;
    }
}
