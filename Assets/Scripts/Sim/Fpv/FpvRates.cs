// Stick -> rotation-rate setpoint curves and the throttle curve of the FPV drone model.
//
// C# port of packages/sim-core/src/rates.ts from ShramkoGSFPV (MIT, Andrii Shramko; see LICENSE-ShramkoGSFPV.txt).
// The formulas were read from Betaflight 4.5.1 (rc.c) and re-implemented there, not copied; the same holds here.
// Engine-free (no UnityEngine): the whole Sim assembly must stay compilable without Unity.
using System;

namespace RocketWorld.Sim.Fpv
{
    /// <summary>Betaflight's <c>rates_type</c>.</summary>
    public enum RatesType { Betaflight, Raceflight, Kiss, Actual, Quick }

    /// <summary>R / S / E of one axis, as a pilot types them in the Betaflight CLI.</summary>
    public struct AxisRates
    {
        public double rcRate, rate, expo;
        public AxisRates(double rcRate, double rate, double expo) { this.rcRate = rcRate; this.rate = rate; this.expo = expo; }
    }

    public sealed class RatesConfig
    {
        public RatesType type = RatesType.Actual;
        public AxisRates roll = new AxisRates(7, 67, 0), pitch = new AxisRates(7, 67, 0), yaw = new AxisRates(7, 67, 0);
        public double rateLimit = 1998;
        /// <summary>Betaflight's <c>quickrates_rc_expo</c> (QUICK only).</summary>
        public bool quickRcExpo;

        public RatesConfig Clone() => (RatesConfig)MemberwiseClone();
    }

    public struct ThrottleCurve
    {
        public double mid, expo;   // thr_mid, thr_expo, 0..100
        public ThrottleCurve(double mid, double expo) { this.mid = mid; this.expo = expo; }
    }

    public static class FpvRates
    {
        public const double SetpointRateLimit = 1998;
        const double RcRateIncremental = 14.54;

        static double Clampf(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>Setpoint in deg/s for stick x in [-1, 1], before the rate-limit clamp.</summary>
        public static double RawRate(RatesType type, double x, double R, double S, double E, bool quickRcExpo = false)
        {
            double a = x < 0 ? -x : x;
            switch (type)
            {
                case RatesType.Betaflight:
                {
                    double x1 = x;
                    if (E != 0) { double e = E / 100; x1 = x * (a * a * a) * e + x * (1 - e); }
                    double r = R / 100;
                    if (r > 2) r += RcRateIncremental * (r - 2);
                    double w = 200 * r * x1;
                    if (S != 0) w *= 1 / Clampf(1 - a * (S / 100), 0.01, 1);
                    return w;
                }
                case RatesType.Actual:
                {
                    double e = E / 100;
                    double x5 = x * x * x * x * x;
                    double ex = a * (x5 * e + x * (1 - e));
                    double c = R * 10;
                    double m = Math.Max(0, S * 10 - c);
                    return x * c + m * ex;
                }
                case RatesType.Kiss:
                {
                    double e = E / 100;
                    double s = 1 / Clampf(1 - a * (S / 100), 0.01, 1);
                    double k = (x * x * x * e + x * (1 - e)) * (R / 1000);
                    return Clampf(2000 * s * k, -SetpointRateLimit, SetpointRateLimit);
                }
                case RatesType.Raceflight:
                {
                    double x1 = (1 + 0.01 * E * (x * x - 1)) * x;
                    double w = 10 * R * x1;
                    return w * (1 + a * S * 0.01);
                }
                default: // Quick
                {
                    double r = R * 2;
                    if (r <= 0) return 0;
                    double m = S * 10 > r ? S * 10 : r;
                    double e = E / 100;
                    double k = (m / r - 1) / (m / r);
                    if (quickRcExpo)
                    {
                        double curve = x * x * x * e + x * (1 - e);
                        double sf = 1 / Clampf(1 - a * k, 0.01, 1);
                        return Clampf(curve * r * sf, -SetpointRateLimit, SetpointRateLimit);
                    }
                    else
                    {
                        double curve = a * a * a * e + a * (1 - e);
                        double sf = 1 / Clampf(1 - curve * k, 0.01, 1);
                        return Clampf(x * r * sf, -SetpointRateLimit, SetpointRateLimit);
                    }
                }
            }
        }

        /// <summary>The setpoint the PID loop gets: the curve, then the rate limit.</summary>
        public static double SetpointRate(RatesType type, double x, AxisRates ax, double rateLimit, bool quickRcExpo = false)
        {
            double lim = Math.Min(rateLimit, SetpointRateLimit);
            return Clampf(RawRate(type, x, ax.rcRate, ax.rate, ax.expo, quickRcExpo), -lim, lim);
        }

        /// <summary>Throttle curve: 11 nodes every 10 %, linear in between.</summary>
        public static double Throttle(double u, ThrottleCurve tc)
        {
            double uu = Clampf(u, 0, 1);
            int i = Math.Min(9, (int)Math.Floor(uu * 10));
            double f = uu * 10 - i;
            double n0 = ThrNode(i / 10.0, tc), n1 = ThrNode((i + 1) / 10.0, tc);
            return n0 + (n1 - n0) * f;
        }

        static double ThrNode(double u, ThrottleCurve tc)
        {
            double m = tc.mid / 100, e = tc.expo / 100;
            double d = u - m;
            double y = d > 0 ? 1 - m : m;
            if (y == 0) return u;
            double q = d / y;
            return m + d * (1 - e + e * q * q);
        }

        /// <summary>Stick (0..1) that gives throttle-curve output u (bisection; HUD figures, never the physics).</summary>
        public static double InvertThrottle(double u, ThrottleCurve tc)
        {
            double lo = 0, hi = 1;
            for (int i = 0; i < 60; i++) { double mid = (lo + hi) / 2; if (Throttle(mid, tc) < u) lo = mid; else hi = mid; }
            return (lo + hi) / 2;
        }
    }
}
