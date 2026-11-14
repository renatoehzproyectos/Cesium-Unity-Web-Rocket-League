using NUnit.Framework;
using RocketWorld.Sim.Fpv;

namespace RocketWorld.Tests
{
    /// <summary>The FPV model (ShramkoGSFPV port). The bit-for-bit comparison with the original runs in tools/fpv (dotnet run); these are the behaviours that must never break.</summary>
    public class FpvModelTests
    {
        sealed class Floor : IFpvWorld
        {
            public double Sweep(double[] c0, double[] c1, double[] r, double[] pad, int n, ref FpvContact o)
            {
                double best = -1;
                for (int i = 0; i < n; i++)
                {
                    double R = r[i] + pad[i], a = c0[i * 3 + 1] - R, b = c1[i * 3 + 1] - R, t = -1;
                    if (a <= 0) t = 0; else if (b < 0) t = a / (a - b);
                    if (t >= 0 && (best < 0 || t < best)) { best = t; o.sphere = i; o.nx = 0; o.ny = 1; o.nz = 0; }
                }
                return best;
            }
            public bool PushOut(double x, double y, double z, double r, out V3d p) { p = default; double d = y - r; if (d >= 0) return false; p.y = -d; return true; }
        }

        static FpvSim Armed(string preset, FlightMode mode, IFpvWorld w = null, double y = 50)
        {
            var sim = new FpvSim(FpvCompile.Compile(FpvPresets.Get(preset)), w);
            sim.Reset(0, y, 0, 0);
            sim.SetChannels(new double[] { 0, 0, -1, 0, -1, FpvModes.Channel(mode), 0, 0 }); sim.Step();
            sim.SetChannels(new double[] { 0, 0, -1, 0, 1, FpvModes.Channel(mode), 0, 0 }); sim.Step();
            return sim;
        }

        [Test] public void ArmsOnlyWithTheThrottleDown()
        {
            var sim = new FpvSim(FpvCompile.Compile(FpvPresets.Get("pavo20pro-3s")), null);
            sim.Reset(0, 10, 0, 0);
            sim.SetChannels(new double[] { 0, 0, 0.2, 0, -1, -1, 0, 0 }); sim.Step();
            sim.SetChannels(new double[] { 0, 0, 0.2, 0, 1, -1, 0, 0 }); sim.Step();
            Assert.IsFalse(sim.Armed, "throttle up: the model must refuse to arm");
        }

        [Test] public void ParkedUntilArmed()
        {
            var sim = new FpvSim(FpvCompile.Compile(FpvPresets.Get("pavo20pro-3s")), null);
            sim.Reset(0, 10, 0, 0);
            for (int i = 0; i < 500; i++) sim.Step();
            Assert.AreEqual(10, sim.s[FpvS.py], 1e-12);
        }

        [Test] public void HoverStickHoldsHeightOnEveryPreset()
        {
            foreach (var pre in FpvPresets.All)
            {
                var prm = FpvCompile.Compile(pre);
                FpvCompile.HoverSolve(prm, 1, out _, out _, out double stick, out _);
                var sim = Armed(pre.id, FlightMode.Angle);
                double[] ch = { 0, 0, stick * 2 - 1, 0, 1, 1, 0, 0 };
                for (int i = 0; i < 4000; i++) { sim.SetChannels(ch); sim.Step(); }
                Assert.AreEqual(0, sim.s[FpvS.vy], 1.0, pre.id + " still climbs or sinks at the hover stick");   // heavy drones spool up and sink first, and the pack sags a little, so judge a small speed, not the height
            }
        }

        [Test] public void FullRollStickReachesTheRateOfTheCurve()
        {
            var pre = FpvPresets.Get("pavo20pro-3s"); var prm = FpvCompile.Compile(pre);
            var sim = Armed(pre.id, FlightMode.Acro);
            FpvCompile.HoverSolve(prm, 1, out _, out _, out double stick, out _);
            double want = FpvRates.SetpointRate(prm.rates.type, 1, prm.rates.roll, prm.rates.rateLimit);
            double[] ch = { 1, 0, stick * 2 - 1, 0, 1, -1, 0, 0 };
            double peak = 0;
            for (int i = 0; i < 600; i++) { sim.SetChannels(ch); sim.Step(); peak = System.Math.Max(peak, -sim.s[FpvS.wz] * 180 / System.Math.PI); }
            Assert.AreEqual(want, peak, want * 0.1);
        }

        [Test] public void FreeFallOntoTheFloorCrashesAndStaysAbove()
        {
            var prm = FpvCompile.Compile(FpvPresets.Get("pavo20pro-3s"));
            var sim = Armed("pavo20pro-3s", FlightMode.Acro, new Floor(), 4);
            double[] ch = { 0, 0, -1, 0, 1, -1, 0, 0 };
            double minY = 1e9;
            for (int i = 0; i < 4000; i++) { sim.SetChannels(ch); sim.Step(); minY = System.Math.Min(minY, sim.s[FpvS.py]); }
            Assert.IsTrue(sim.Crashed);
            Assert.Greater(minY, prm.bodyBottom - 0.02);
        }

        [Test] public void ThrottleCurveAndRatesAreMonotone()
        {
            var tc = new ThrottleCurve(65, 20);
            double prev = -1;
            for (double u = 0; u <= 1; u += 0.01) { double v = FpvRates.Throttle(u, tc); Assert.GreaterOrEqual(v, prev); prev = v; }
            var a = new AxisRates(7, 67, 0);
            Assert.AreEqual(0, FpvRates.SetpointRate(RatesType.Actual, 0, a, 1998), 1e-9);
            Assert.AreEqual(670, FpvRates.SetpointRate(RatesType.Actual, 1, a, 1998), 1e-9);
        }

        [Test] public void FrameMappingIsItsOwnInverse()
        {
            FpvFrame.ToSim(FpvFrame.ToUnity(1, 2, 3), out double x, out double y, out double z);
            Assert.AreEqual(3, z, 1e-6); Assert.AreEqual(1, x, 1e-6); Assert.AreEqual(2, y, 1e-6);
            // identity attitude faces Unity +z (the sim's nose is -z)
            var q = FpvFrame.ToUnity(1, 0, 0, 0);
            Assert.AreEqual(UnityEngine.Quaternion.identity, q);
        }
    }
}
