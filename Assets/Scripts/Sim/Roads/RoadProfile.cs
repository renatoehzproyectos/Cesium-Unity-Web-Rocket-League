using System;
using System.Collections.Generic;

namespace RocketWorld.Sim.Roads
{
    public sealed class ProfileInput
    {
        public float[] hc, hl, hr;                 // sampled ground height (frame-local up) at the centre / left / right of each station; NaN = no hit
        public float halfOffset = 2f;              // lateral distance between the centre and the left / right samples (m)
        public float startPin = float.NaN, endPin = float.NaN;   // heights already fixed by a shared node (NaN = free)
        public bool ignoreInterior;                // bridge / tunnel: the tiles' deck or roof is not the road, use only the pinned ends
        public float maxGrade = 0.25f;
    }

    public sealed class ProfileResult { public float[] up; public float[] bank; public float coverage; public float maxResidual; }

    /// <summary>
    /// Turns noisy photogrammetry ground samples into a smooth road profile:
    ///  1. outlier rejection (running median / MAD), so a car, a tree or a roof under a ray does not count;
    ///  2. asymmetric least squares (Eilers): a penalised smoother that is pulled harder by points BELOW the curve than above, so the profile follows
    ///     the lower envelope of the data (trees and parked vehicles only ever add height); gaps are bridged by the smoothness term (a minimum-curvature curve);
    ///  3. pinned ends (shared node heights) as heavy weights, so segments that meet at an intersection meet at the same height;
    ///  4. grade limit and a last light smoothing, then a cross slope (tilt) from the left/right samples.
    /// </summary>
    public static class RoadProfile
    {
        public static float SurfaceBias = 0.02f;   // m added so the collider sits on the surface, not in the noise floor
        public static double VerticalCurveLength = 24.0;   // m: smoothness scale of the fit
        public static float MinCoverage = 0.3f;    // below this share of stations with hits the profile is "not ready"

        public static ProfileResult Solve(Centerline c, ProfileInput inp)
        {
            int n = c.Count; double ds = c.Length / (n - 1);
            var y = new float[n]; var w = new float[n]; int valid = 0;
            for (int i = 0; i < n; i++)
            {
                y[i] = float.NaN;
                if (!inp.ignoreInterior)
                {
                    float a = inp.hc != null ? inp.hc[i] : float.NaN, l = inp.hl != null ? inp.hl[i] : float.NaN, r = inp.hr != null ? inp.hr[i] : float.NaN;
                    // lowest of the available lateral samples is the least likely to be a roadside object; the centre is preferred when consistent
                    float v = Min3(a, l, r); if (!float.IsNaN(a) && !float.IsNaN(v) && a - v < 0.35f) v = a;
                    y[i] = v;
                }
            }
            RejectOutliers(y, 4);
            for (int i = 0; i < n; i++) if (!float.IsNaN(y[i])) valid++;
            bool pinS = !float.IsNaN(inp.startPin), pinE = !float.IsNaN(inp.endPin);
            float cov = valid / (float)n;
            if (!inp.ignoreInterior && cov < MinCoverage && !(pinS && pinE && cov > 0f)) return null;
            if (inp.ignoreInterior && !(pinS && pinE)) return null;      // a bridge needs both ends to exist first
            if (valid == 0 && !(pinS || pinE)) return null;

            double lam = Math.Pow(VerticalCurveLength / ds, 4);
            if (inp.ignoreInterior) lam *= 4;                             // decks are straighter than ground
            var z = new float[n];
            // initial fit = plain weighted smoother, then AsLS reweighting
            for (int i = 0; i < n; i++) w[i] = float.IsNaN(y[i]) ? 0f : 1f;
            if (pinS) { y[0] = inp.startPin; w[0] = 1e4f; }
            if (pinE) { y[n - 1] = inp.endPin; w[n - 1] = 1e4f; }
            const float P = 0.05f;
            var wi = new float[n]; Array.Copy(w, wi, n);
            float tol = 0.2f;
            for (int it = 0; it < 8; it++)
            {
                SolveSmooth(y, wi, lam, z);
                if (it == 0)    // noise level from the first (symmetric) fit: points further above the curve than this are objects, not ground
                {
                    var rs = new List<float>(n); for (int i = 0; i < n; i++) if (w[i] > 0 && w[i] < 1e4f) rs.Add(Math.Abs(y[i] - z[i]));
                    if (rs.Count > 3) tol = Math.Max(0.15f, Math.Min(0.6f, 2.5f * 1.4826f * Median(rs)));
                }
                bool changed = false;
                for (int i = 0; i < n; i++)
                {
                    if (w[i] == 0f || w[i] >= 1e4f) continue;
                    float nw = y[i] > z[i] + tol ? P : 1f; if (nw != wi[i]) { wi[i] = nw; changed = true; }
                }
                if (!changed && it > 0) break;
            }
            LimitGrade(z, (float)ds, inp.maxGrade, pinS, pinE);
            var res = new ProfileResult { up = z, coverage = cov, bank = new float[n] };
            float maxR = 0; for (int i = 0; i < n; i++) if (w[i] > 0 && w[i] < 1e4f) maxR = Math.Max(maxR, Math.Abs(y[i] - z[i]));
            res.maxResidual = maxR;
            for (int i = 0; i < n; i++) z[i] += SurfaceBias;
            if (inp.hl != null && inp.hr != null && !inp.ignoreInterior) Bank(c, inp, res, ds);
            return res;
        }

        static float Min3(float a, float b, float c) { float m = float.NaN; foreach (var v in new[] { a, b, c }) if (!float.IsNaN(v) && (float.IsNaN(m) || v < m)) m = v; return m; }

        /// <summary>NaN-out samples that differ from the median of their neighbourhood by more than max(0.8 m, 4 MAD).</summary>
        public static void RejectOutliers(float[] y, int half)
        {
            int n = y.Length; var o = (float[])y.Clone(); var buf = new List<float>(2 * half + 1);
            for (int i = 0; i < n; i++)
            {
                if (float.IsNaN(o[i])) continue;
                buf.Clear(); for (int k = Math.Max(0, i - half); k <= Math.Min(n - 1, i + half); k++) if (!float.IsNaN(o[k])) buf.Add(o[k]);
                if (buf.Count < 3) continue;
                float med = Median(buf); for (int k = 0; k < buf.Count; k++) buf[k] = Math.Abs(buf[k] - med);
                float mad = Median(buf);
                if (Math.Abs(o[i] - med) > Math.Max(0.8f, 4f * 1.4826f * mad)) y[i] = float.NaN;
            }
        }
        static float Median(List<float> l) { var a = l.ToArray(); Array.Sort(a); int m = a.Length / 2; return a.Length % 2 == 1 ? a[m] : 0.5f * (a[m - 1] + a[m]); }

        /// <summary>Solves (W + lam D'D) z = W y, D = second difference, with a banded Cholesky (pentadiagonal, SPD).</summary>
        public static void SolveSmooth(float[] y, float[] w, double lam, float[] z)
        {
            int n = y.Length;
            if (n < 3) { for (int i = 0; i < n; i++) z[i] = float.IsNaN(y[i]) ? 0 : y[i]; return; }
            var A = new double[n, 3];    // A[i,d] = element (i, i-d)
            var b = new double[n];
            for (int i = 0; i < n; i++) { A[i, 0] = w[i] + 1e-9; b[i] = w[i] > 0 && !float.IsNaN(y[i]) ? w[i] * y[i] : 0; }
            for (int r = 0; r < n - 2; r++)     // row r of D: [1 -2 1] on (r, r+1, r+2)
            {
                int i0 = r, i1 = r + 1, i2 = r + 2;
                A[i0, 0] += lam; A[i1, 0] += 4 * lam; A[i2, 0] += lam;
                A[i1, 1] += -2 * lam; A[i2, 1] += -2 * lam; A[i2, 2] += lam;
            }
            var L = new double[n, 3];
            for (int i = 0; i < n; i++)
                for (int j = Math.Max(0, i - 2); j <= i; j++)
                {
                    double s = A[i, i - j];
                    for (int k = Math.Max(0, i - 2); k < j; k++) s -= L[i, i - k] * L[j, j - k];
                    if (i == j) L[i, 0] = Math.Sqrt(Math.Max(s, 1e-12)); else L[i, i - j] = s / L[j, 0];
                }
            var t = new double[n];
            for (int i = 0; i < n; i++) { double s = b[i]; for (int k = Math.Max(0, i - 2); k < i; k++) s -= L[i, i - k] * t[k]; t[i] = s / L[i, 0]; }
            for (int i = n - 1; i >= 0; i--) { double s = t[i]; for (int k = i + 1; k <= Math.Min(n - 1, i + 2); k++) s -= L[k, k - i] * z[k]; z[i] = (float)(s / L[i, 0]); }
        }

        /// <summary>Forward/backward clamp of the slope, then one light smoothing that keeps the pinned ends.</summary>
        public static void LimitGrade(float[] z, float ds, float maxGrade, bool pinS, bool pinE)
        {
            int n = z.Length; float lim = maxGrade * ds;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 1; i < n; i++) { float d = z[i] - z[i - 1]; if (Math.Abs(d) > lim) z[i] = z[i - 1] + Math.Sign(d) * lim; }
                for (int i = n - 2; i >= 0; i--) { float d = z[i] - z[i + 1]; if (Math.Abs(d) > lim) z[i] = z[i + 1] + Math.Sign(d) * lim; }
            }
            var o = (float[])z.Clone();
            for (int i = 1; i < n - 1; i++) z[i] = 0.25f * o[i - 1] + 0.5f * o[i] + 0.25f * o[i + 1];
        }

        static void Bank(Centerline c, ProfileInput inp, ProfileResult res, double ds)
        {
            int n = c.Count; var s = new float[n]; var w = new float[n]; var z = new float[n];
            for (int i = 0; i < n; i++)
            {
                float l = inp.hl[i], r = inp.hr[i];
                if (!float.IsNaN(l) && !float.IsNaN(r)) { s[i] = Math.Max(-0.12f, Math.Min(0.12f, (r - l) / (2f * inp.halfOffset))); w[i] = 1f; } else { s[i] = 0; w[i] = 0; }
            }
            SolveSmooth(s, w, Math.Pow(120.0 / ds, 4), z);       // cross slope changes slowly: long smoothing scale
            for (int i = 0; i < n; i++) res.bank[i] = Math.Max(-0.08f, Math.Min(0.08f, z[i]));
        }
    }
}
