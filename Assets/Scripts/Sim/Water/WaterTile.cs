using System.Collections.Generic;

namespace RocketWorld.Sim
{
    /// <summary>What a water polygon is, from the OpenMapTiles <c>water.class</c> attribute (only used to tune the swell).</summary>
    public enum WaterKind { Other = 0, Ocean = 1, Lake = 2, River = 3 }

    /// <summary>One water feature: one or more rings in tile units (0..extent, y down). Even-odd fill, so island holes work.</summary>
    public sealed class WaterPoly
    {
        public WaterKind kind;
        public int[][] rings;                       // each ring: x0,y0,x1,y1,... (implicitly closed)
        public int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        public void Finish()
        {
            foreach (var r in rings)
                for (int i = 0; i + 1 < r.Length; i += 2)
                {
                    if (r[i] < minX) minX = r[i]; if (r[i] > maxX) maxX = r[i];
                    if (r[i + 1] < minY) minY = r[i + 1]; if (r[i + 1] > maxY) maxY = r[i + 1];
                }
        }

        public bool Contains(double px, double py)
        {
            if (px < minX || px > maxX || py < minY || py > maxY) return false;
            bool inside = false;
            foreach (var r in rings)
            {
                int n = r.Length / 2;
                if (n < 3) continue;
                int j = n - 1;
                for (int i = 0; i < n; i++)
                {
                    double xi = r[2 * i], yi = r[2 * i + 1], xj = r[2 * j], yj = r[2 * j + 1];
                    if ((yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi) inside = !inside;
                    j = i;
                }
            }
            return inside;
        }
    }

    /// <summary>A decoded vector tile reduced to what the boat needs: water polygons and "solid over water" lines (bridges, piers).</summary>
    public sealed class WaterTile
    {
        public static readonly WaterTile Empty = new WaterTile();
        public int extent = 4096;
        public readonly List<WaterPoly> water = new List<WaterPoly>();
        public readonly List<int[]> solidLines = new List<int[]>();     // x0,y0,x1,y1,... polylines

        /// <summary>True if the point (tile units) is inside any water feature. Overlapping features are a union.</summary>
        public bool IsWater(double px, double py, out WaterKind kind)
        {
            for (int i = 0; i < water.Count; i++)
                if (water[i].Contains(px, py)) { kind = water[i].kind; return true; }
            kind = WaterKind.Other;
            return false;
        }

        /// <summary>True if a bridge / pier centreline passes within <paramref name="radius"/> tile units of the point.</summary>
        public bool NearSolid(double px, double py, double radius)
        {
            double r2 = radius * radius;
            for (int l = 0; l < solidLines.Count; l++)
            {
                var s = solidLines[l];
                for (int i = 0; i + 3 < s.Length; i += 2)
                    if (SegDist2(px, py, s[i], s[i + 1], s[i + 2], s[i + 3]) <= r2) return true;
            }
            return false;
        }

        static double SegDist2(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            double t = len2 < 1e-9 ? 0 : ((px - ax) * dx + (py - ay) * dy) / len2;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            double cx = ax + t * dx - px, cy = ay + t * dy - py;
            return cx * cx + cy * cy;
        }
    }
}
