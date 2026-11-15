using System;

namespace RocketWorld.Sim.Roads
{
    /// <summary>
    /// Local east/up/north frame at a lon/lat (the same axes Cesium for Unity uses under a CesiumGlobeAnchor). Horizontal work is planar metres
    /// (error over a 2 km chunk is millimetres); height keeps the Earth's curvature (the drop is d^2/2R: 0.8 m at 1 km), so a chunk root placed with a
    /// globe anchor at the frame origin lines up with the tiles.
    /// </summary>
    public sealed class RoadFrame
    {
        public readonly double lon, lat, h0, radius;
        readonly double _mE, _mN;                 // metres per degree
        public RoadFrame(double lonDeg, double latDeg, double h0)
        {
            lon = lonDeg; lat = latDeg; this.h0 = h0;
            double la = latDeg * Math.PI / 180.0, s = Math.Sin(la), c = Math.Cos(la);
            double a = 6378137.0, e2 = 0.00669437999014;
            double w = Math.Sqrt(1 - e2 * s * s); double n = a / w, m = a * (1 - e2) / (w * w * w);
            _mE = Math.PI / 180.0 * n * c; _mN = Math.PI / 180.0 * m; radius = Math.Sqrt(n * m);
        }
        public void ToPlane(double lonDeg, double latDeg, out double east, out double north)
        { east = (lonDeg - lon) * _mE; north = (latDeg - lat) * _mN; }
        public void ToLonLat(double east, double north, out double lonDeg, out double latDeg)
        { lonDeg = lon + east / _mE; latDeg = lat + north / _mN; }
        /// <summary>Ellipsoidal height to the frame's local up (adds the curvature drop).</summary>
        public double UpFromH(double east, double north, double h) => h - h0 - (east * east + north * north) / (2 * radius);
        public double HFromUp(double east, double north, double up) => up + h0 + (east * east + north * north) / (2 * radius);
    }

    /// <summary>Slippy-map tile helpers for the road tiles (z14 by default).</summary>
    public static class RoadTiles
    {
        public const int Zoom = 14;
        public static long Key(int z, int x, int y) => ((long)z << 56) | ((long)x << 28) | (uint)y;
        public static void TileOf(double lon, double lat, int z, out int x, out int y)
        { Geo.LonLatToTile(lon, lat, z, out double tx, out double ty); int n = 1 << z; x = ((int)Math.Floor(tx) % n + n) % n; y = Math.Max(0, Math.Min(n - 1, (int)Math.Floor(ty))); }
        public static void TileBounds(int z, int x, int y, out double lonW, out double latS, out double lonE, out double latN)
        {
            double n = Math.Pow(2, z); lonW = x / n * 360.0 - 180.0; lonE = (x + 1) / n * 360.0 - 180.0;
            latN = Math.Atan(Math.Sinh(Math.PI * (1 - 2.0 * y / n))) * 180.0 / Math.PI; latS = Math.Atan(Math.Sinh(Math.PI * (1 - 2.0 * (y + 1) / n))) * 180.0 / Math.PI;
        }
    }
}
