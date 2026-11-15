using System;
using System.Collections.Generic;

namespace RocketWorld.Sim.Roads
{
    /// <summary>A smooth centreline resampled at (almost) fixed arc-length stations, in frame-local planar metres.</summary>
    public sealed class Centerline
    {
        public double[] e, n, s;          // position and arc length
        public float[] tx, tz;            // unit tangent (east, north)
        public int Count => e.Length;
        public double Length => s[s.Length - 1];
    }

    public static class RoadCenterline
    {
        public const double Spacing = 4.0;      // m between stations on straights

        /// <summary>
        /// Drops near-duplicate vertices, runs a centripetal Catmull-Rom spline THROUGH the source vertices (the data's positions are kept exactly,
        /// only the corners between them are rounded) and resamples it. Station count depends only on the geometry, so a cached height profile
        /// keeps matching it. Returns null for degenerate lines.
        /// </summary>
        public static Centerline Build(double[] pe, double[] pn)
        {
            var xs = new List<double>(pe.Length); var ys = new List<double>(pe.Length);
            for (int i = 0; i < pe.Length; i++)
            {
                if (xs.Count > 0) { double dx = pe[i] - xs[xs.Count - 1], dy = pn[i] - ys[ys.Count - 1]; if (dx * dx + dy * dy < 0.25 * 0.25) { if (i == pe.Length - 1) { xs[xs.Count - 1] = pe[i]; ys[ys.Count - 1] = pn[i]; } continue; } }
                xs.Add(pe[i]); ys.Add(pn[i]);
            }
            if (xs.Count < 2) return null;
            var oe = new List<double>(); var on = new List<double>();
            int m = xs.Count;
            for (int i = 0; i < m - 1; i++)
            {
                // phantom end points by reflection keep the end tangents along the first / last chord
                double x0 = i > 0 ? xs[i - 1] : 2 * xs[0] - xs[1], y0 = i > 0 ? ys[i - 1] : 2 * ys[0] - ys[1];
                double x3 = i + 2 < m ? xs[i + 2] : 2 * xs[m - 1] - xs[m - 2], y3 = i + 2 < m ? ys[i + 2] : 2 * ys[m - 1] - ys[m - 2];
                double x1 = xs[i], y1 = ys[i], x2 = xs[i + 1], y2 = ys[i + 1];
                double chord = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
                // turn angle at both ends decides how finely to sample this piece
                double a1 = Turn(x0, y0, x1, y1, x2, y2), a2 = Turn(x1, y1, x2, y2, x3, y3);
                int k = (int)Math.Ceiling(Math.Max(chord / Spacing, (Math.Abs(a1) + Math.Abs(a2)) / 0.12));
                k = Math.Max(1, Math.Min(k, 400));
                double t0 = 0, t1 = Math.Pow(Dist(x0, y0, x1, y1), 0.5), t2 = t1 + Math.Pow(chord, 0.5), t3 = t2 + Math.Pow(Dist(x2, y2, x3, y3), 0.5);
                if (t1 - t0 < 1e-9) t0 = t1 - 1e-3; if (t3 - t2 < 1e-9) t3 = t2 + 1e-3;
                for (int j = (i == 0 ? 0 : 1); j <= k; j++)
                {
                    double t = t1 + (t2 - t1) * j / k;
                    double ax = Lerp(x0, x1, t0, t1, t), ay = Lerp(y0, y1, t0, t1, t), bx = Lerp(x1, x2, t1, t2, t), by = Lerp(y1, y2, t1, t2, t), cx = Lerp(x2, x3, t2, t3, t), cy = Lerp(y2, y3, t2, t3, t);
                    double b1x = Lerp(ax, bx, t0, t2, t), b1y = Lerp(ay, by, t0, t2, t), b2x = Lerp(bx, cx, t1, t3, t), b2y = Lerp(by, cy, t1, t3, t);
                    oe.Add(Lerp(b1x, b2x, t1, t2, t)); on.Add(Lerp(b1y, b2y, t1, t2, t));
                }
            }
            return Resample(oe, on);
        }

        static double Dist(double x0, double y0, double x1, double y1) => Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        static double Lerp(double a, double b, double ta, double tb, double t) => tb - ta < 1e-12 ? a : a + (b - a) * (t - ta) / (tb - ta);
        static double Turn(double x0, double y0, double x1, double y1, double x2, double y2)
        {
            double a = Math.Atan2(y1 - y0, x1 - x0), b = Math.Atan2(y2 - y1, x2 - x1), d = b - a;
            while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI; return d;
        }

        /// <summary>Equal arc-length resampling of a dense polyline (spacing <see cref="Spacing"/>, shrunk to fit the exact length so both ends are kept).</summary>
        static Centerline Resample(List<double> xs, List<double> ys)
        {
            int m = xs.Count; var cum = new double[m];
            for (int i = 1; i < m; i++) cum[i] = cum[i - 1] + Dist(xs[i - 1], ys[i - 1], xs[i], ys[i]);
            double len = cum[m - 1]; if (len < 0.5) return null;
            int cnt = Math.Max(2, (int)Math.Ceiling(len / Spacing) + 1);
            var c = new Centerline { e = new double[cnt], n = new double[cnt], s = new double[cnt], tx = new float[cnt], tz = new float[cnt] };
            int seg = 0;
            for (int i = 0; i < cnt; i++)
            {
                double d = len * i / (cnt - 1);
                while (seg < m - 2 && cum[seg + 1] < d) seg++;
                double l = cum[seg + 1] - cum[seg], f = l < 1e-9 ? 0 : (d - cum[seg]) / l;
                c.e[i] = xs[seg] + (xs[seg + 1] - xs[seg]) * f; c.n[i] = ys[seg] + (ys[seg + 1] - ys[seg]) * f; c.s[i] = d;
            }
            for (int i = 0; i < cnt; i++)
            {
                int a = Math.Max(0, i - 1), b = Math.Min(cnt - 1, i + 1);
                double dx = c.e[b] - c.e[a], dy = c.n[b] - c.n[a], l = Math.Sqrt(dx * dx + dy * dy);
                c.tx[i] = (float)(dx / l); c.tz[i] = (float)(dy / l);
            }
            return c;
        }
    }
}
