// Turbine engine (port of FGTurbine) + direct thruster (FGThruster / FGForce custom transform) + fuel feed, and the gear-contact loader.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace RocketWorld.Sim
{
    public sealed partial class JsbGear
    {
        public bool retractable; public int index; public string brakeGroupName = "NONE";
        public double wheelRadiusFt = 0.0;
        public Cell posNorm;                       // gear/gear-pos-norm (shared)
        public static JsbGear FromXml(XmlElement c, int index)
        {
            Func<string, double> n = t => double.Parse(c.SelectSingleNode(t).InnerText.Trim(), CultureInfo.InvariantCulture);
            Func<string, bool> has = t => c.SelectSingleNode(t) != null;
            var g = new JsbGear { name = c.GetAttribute("name"), index = index, locIn = JsbFdm.Loc(c.SelectSingleNode("location")) };
            g.spring = has("spring_coeff") ? n("spring_coeff") : 0; g.damp = has("damping_coeff") ? n("damping_coeff") : 0;
            g.dampRebound = has("damping_coeff_rebound") ? n("damping_coeff_rebound") : g.damp;
            g.staticF = has("static_friction") ? n("static_friction") : 0.8; g.dynamicF = has("dynamic_friction") ? n("dynamic_friction") : 0.5; g.rollingF = has("rolling_friction") ? n("rolling_friction") : 0.02;
            g.maxSteerDeg = has("max_steer") ? n("max_steer") : 0;
            g.brakeGroup = has("brake_group") ? c.SelectSingleNode("brake_group").InnerText.Trim().ToUpperInvariant() : "NONE";
            g.retractable = has("retractable") && n("retractable") > 0;
            return g;
        }
        public void Bind(JsbFdm f) { posNorm = f.Cell("gear/gear-pos-norm"); }
        /// <summary>FGLGear: a retractable leg only carries load when it is (nearly) fully down.</summary>
        public bool Extended => !retractable || posNorm == null || posNorm.v > 0.99;
    }

    public sealed class JsbEngine
    {
        public sealed class Ctx { public double dt, qbar, rhoRatio, tempR, tatC, mach; public D3 uvw, pqr; }
        public JsbFdm fdm; public int num; public string type;
        // thruster
        public D3 locIn, actingIn; public double pitch0, yaw0, roll0, reverser; public D3 moment;
        double _thrust; D3 _fb;
        readonly List<int> _feeds = new List<int>();
        string P(string s) => "propulsion/engine[" + num + "]/" + s;

        // turbine state / parameters (FGTurbine)
        double milThrust = 10000, maxThrust = 10000, bpr, bleed, tsfc = 0.8, atsfc = 1.7, idleN1 = 30, idleN2 = 60, maxN1 = 100, maxN2 = 100;
        int augmented, augMethod, injected; double n1Spin = 1, n2Spin = 3, n1Start = 1.4, n2Start = 2, n1Down = 2, n2Down = 2, igniteN1 = 5.21, igniteN2 = 25.18;
        public double N1, N2, N2norm, ffPph, nozzle = 1, epr = 1, idleFF; double augCmd, throttle, n1Factor, n2Factor; bool running, cutoff = true, starved, aug, starter;
        public bool Starved => starved; public bool Running => running; public bool Afterburner => aug;
        enum Phase { Off, Run, SpinUp, Start, Stall, Seize, Trim } Phase phase = Phase.Off;
        JsbFdm.Ev idleThr, milThr, augThr, n1Up, n1Dn, n2Up, n2Dn, tsfcFn; double fuelUsed;
        // electric / generic power engine for rotor thrusters: not used here
        Cell _thrustCell;

        public static JsbEngine Load(JsbFdm f, XmlElement en, int num)
        {
            var e = new JsbEngine { fdm = f, num = num };
            var ed = new XmlDocument(); ed.LoadXml(f.readEngine(en.GetAttribute("file"))); var er = ed.DocumentElement; e.type = er.Name;
            if (e.type != "turbine_engine") throw new FormatException("engine type <" + e.type + "> is not supported by the generic port");
            // functions are named relative; evaluate with this engine's property prefix
            Func<string, XmlElement> fn = nm => { foreach (XmlElement x in er.SelectNodes("function")) if (x.GetAttribute("name") == nm) return x; return null; };
            Func<string, double, double> num_ = (t, d) => er.SelectSingleNode(t) != null ? double.Parse(er.SelectSingleNode(t).InnerText.Trim(), CultureInfo.InvariantCulture) : d;
            e.milThrust = num_("milthrust", 10000); e.maxThrust = num_("maxthrust", 10000); e.bpr = num_("bypassratio", 0); e.bleed = num_("bleed", 0);
            e.idleN1 = num_("idlen1", 30); e.idleN2 = num_("idlen2", 60); e.maxN1 = num_("maxn1", 100); e.maxN2 = num_("maxn2", 100);
            e.n1Spin = num_("n1spinup", 1); e.n2Spin = num_("n2spinup", 3); e.n1Start = num_("n1startrate", 1.4); e.n2Start = num_("n2startrate", 2);
            e.n1Down = num_("n1spindown", 2); e.n2Down = num_("n2spindown", 2); e.igniteN1 = num_("ignitionn1", 5.21); e.igniteN2 = num_("ignitionn2", 25.18);
            e.augmented = (int)num_("augmented", 0); e.augMethod = (int)num_("augmethod", 0); e.injected = (int)num_("injected", 0);
            var tsf = er.SelectSingleNode("tsfc"); if (tsf != null && double.TryParse(tsf.InnerText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double tv)) e.tsfc = tv;
            var atsf = er.SelectSingleNode("atsfc"); if (atsf != null && double.TryParse(atsf.InnerText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double av)) e.atsfc = av;
            // the engine's table functions reference global properties; `[#]` in names is the engine number
            e.idleThr = FnOf(f, fn("IdleThrust")); e.milThr = FnOf(f, fn("MilThrust")); e.augThr = FnOf(f, fn("AugThrust"));
            e.n1Up = FnOf(f, fn("N1SpoolUp")); e.n1Dn = FnOf(f, fn("N1SpoolDown")); e.n2Up = FnOf(f, fn("N2SpoolUp")); e.n2Dn = FnOf(f, fn("N2SpoolDown"));
            foreach (XmlElement fd in en.SelectNodes("feed")) e._feeds.Add(int.Parse(fd.InnerText.Trim()));
            var th = (XmlElement)en.SelectSingleNode("thruster");
            if (th.GetAttribute("file") != "direct") throw new FormatException("only direct thrusters are supported here (got '" + th.GetAttribute("file") + "')");
            e.locIn = JsbFdm.Loc(th.SelectSingleNode("location")); e.actingIn = e.locIn;
            var or = th.SelectSingleNode("orient"); if (or != null) { double k = ((XmlElement)or).GetAttribute("unit") == "RAD" ? 1 : JsbFdm.Deg; e.roll0 = double.Parse(or.SelectSingleNode("roll").InnerText, CultureInfo.InvariantCulture) * k; e.pitch0 = double.Parse(or.SelectSingleNode("pitch").InnerText, CultureInfo.InvariantCulture) * k; e.yaw0 = double.Parse(or.SelectSingleNode("yaw").InnerText, CultureInfo.InvariantCulture) * k; }
            e._thrustCell = f.Cell(e.P("thrust-lbs"));
            f.Cell(e.P("pitch-angle-rad")).v = e.pitch0; f.Cell(e.P("yaw-angle-rad")).v = e.yaw0; f.Cell(e.P("reverser-angle-rad")).v = 0;
            f.Cell("fcs/throttle-cmd-norm[" + num + "]"); f.Cell("fcs/throttle-pos-norm[" + num + "]");
            return e;
        }
        static JsbFdm.Ev FnOf(JsbFdm f, XmlElement x) => x == null ? null : f.Func(x);
        public bool trimPending = true;
        public void Finish() { }

        /// <summary>JSBSim "propulsion/set-running = -1": the engine starts at the N1 / N2 of the current throttle setting (FGTurbine tpTrim path).</summary>
        public void SetRunning(double? thr = null)
        {
            trimPending = false; n1Factor = maxN1 - idleN1; n2Factor = maxN2 - idleN2; throttle = thr ?? fdm.Cell("fcs/throttle-pos-norm[" + num + "]").v;
            N2 = idleN2 + throttle * n2Factor; N1 = idleN1 + throttle * n1Factor; N2norm = throttle; phase = Phase.Run; cutoff = false; running = true;
            idleFF = Math.Pow(milThrust, 0.2) * 107.0; ffPph = 0; _ffInit = true;
        }
        public void Shutdown() { cutoff = true; phase = Phase.Off; running = false; }
        public void Start() { cutoff = false; starter = true; if (phase == Phase.Off) phase = Phase.SpinUp; }

        double Seek(double v, double target, double acc, double dec, double dt)
        {
            if (v > target) { v -= dt * dec; if (v < target) v = target; } else if (v < target) { v += dt * acc; if (v > target) v = target; }
            return v;
        }
        double SpoolDelay(double factor, Ctx c) { double delay = factor * 90.0 / (bpr + 3.0), n = Math.Min(1.0, N2norm + 0.1); return delay / (1 + 3 * Math.Pow(1 - n, 3) + (1 - c.rhoRatio)); }

        public void Run(Ctx c)
        {
            // idle fuel flow (FGTurbine::Load): 
            if (trimPending) { trimPending = false; SetRunning(); }
            starved = false; double need = 0; foreach (var tIdx in _feeds) if (tIdx < fdm.tanks.Count && fdm.tanks[tIdx].contents > 0.01) { need = 1; break; }
            if (_feeds.Count > 0 && need == 0) starved = true;
            throttle = fdm.Cell("fcs/throttle-pos-norm[" + num + "]").v; if (throttle > 1.0) { augCmd = throttle - 1.0; throttle -= augCmd; } else augCmd = 0;
            if (starved) phase = Phase.Off; if (cutoff && phase != Phase.SpinUp) phase = Phase.Off;
            if ((starter || c.qbar > 30.0) && !running && !cutoff && N2 > 15.0) phase = Phase.Start;
            double thrust = 0, dt = c.dt;
            switch (phase)
            {
                case Phase.Run: thrust = RunPhase(c); break;
                case Phase.SpinUp: running = false; ffPph = 0; N2 = Seek(N2, igniteN2, n2Spin, N2 / 2, dt); N1 = Seek(N1, igniteN1, n1Spin, N1 / 2, dt); nozzle = 1; epr = 1; if (!starter) phase = Phase.Off; break;
                case Phase.Start:
                    if (N2 > 15.0 && !starved)
                    {
                        if (N2 < idleN2) { N2 = Seek(N2, idleN2, n2Start, N2 / 2, dt); N1 = Seek(N1, idleN1, n1Start, N1 / 2, dt); ffPph = idleFF * N2 / idleN2; if (!starter && c.qbar < 30) phase = Phase.Off; }
                        else { phase = Phase.Run; running = true; starter = false; }
                    }
                    else { phase = Phase.Off; starter = false; }
                    break;
                default:   // Off
                    running = false; ffPph = Seek(ffPph, 0, 1000, 10000, dt); N1 = Seek(N1, c.qbar / 10, N1 / 2 + 0.1, N1 / n1Down, dt); N2 = Seek(N2, c.qbar / 15, N2 / 2 + 0.1, N2 / n2Down, dt);
                    nozzle = Seek(nozzle, 1, 0.8, 0.8, dt); epr = Seek(epr, 1, 0.2, 0.2, dt); aug = false; break;
            }
            // direct thruster: thrust * cos(reverser) along the (vectored) thrust axis
            double rev = fdm.Cell(P("reverser-angle-rad")).v, th = Math.Cos(rev) * thrust; _thrust = th; _thrustCell.v = th;
            double pitch = fdm.Cell(P("pitch-angle-rad")).v, yaw = fdm.Cell(P("yaw-angle-rad")).v, roll = roll0;
            double cp = Math.Cos(pitch), sp = Math.Sin(pitch), cy = Math.Cos(yaw), sy = Math.Sin(yaw);
            _fb = new D3(th * cp * cy, th * cp * sy, -th * sp);       // FGForce::UpdateCustomTransformMatrix first column
            moment = new D3();
            fdm.Cell(P("n1")).v = N1; fdm.Cell(P("n2")).v = N2; fdm.Cell(P("fuel-flow-rate-pps")).v = ffPph / 3600.0; fdm.Cell(P("afterburner")).v = aug ? 1 : 0; fdm.Cell(P("nozzle-pos-norm")).v = nozzle; fdm.Cell(P("epr")).v = epr;
        }
        bool _ffInit;
        double RunPhase(Ctx c)
        {
            if (_ffInit) { _ffInit = false; double i0 = milThrust * idleThr(), m0 = (milThrust - i0) * milThr(); double t0 = i0 + m0 * N2norm * N2norm; ffPph = t0 * tsfc * Math.Sqrt(c.tempR / 389.7) * (0.84 + (1 - N2norm) * (1 - N2norm)); }
            double dt = c.dt, idleth = milThrust * idleThr(), milth = (milThrust - idleth) * milThr();
            running = true; starter = false; n1Factor = maxN1 - idleN1; n2Factor = maxN2 - idleN2;
            double up2 = n2Up != null ? n2Up() : SpoolDelay(1.0, c), dn2 = n2Dn != null ? n2Dn() : SpoolDelay(3.0, c), up1 = n1Up != null ? n1Up() : SpoolDelay(1.0, c), dn1 = n1Dn != null ? n1Dn() : SpoolDelay(2.4, c);
            N2 = Seek(N2, idleN2 + throttle * n2Factor, up2, dn2, dt); N1 = Seek(N1, idleN1 + throttle * n1Factor, up1, dn1, dt);
            N2norm = (N2 - idleN2) / n2Factor; double thrust = idleth + milth * N2norm * N2norm;
            if (!aug)
            {
                double corr = tsfc * Math.Sqrt(c.tempR / 389.7) * (0.84 + (1 - N2norm) * (1 - N2norm));
                ffPph = Seek(ffPph, thrust * corr, 1000, 10000, dt); if (ffPph < idleFF) ffPph = idleFF;
                nozzle = Seek(nozzle, 1.0 - N2norm, 0.8, 0.8, dt); thrust *= (1.0 - bleed); epr = 1.0 + thrust / milThrust;
            }
            if (augMethod == 1) aug = throttle > 0.99 && N2 > 97.0;
            if (augmented == 1 && aug && augMethod < 2) { thrust = augThr() * maxThrust; ffPph = Seek(ffPph, thrust * atsfc, 5000, 10000, dt); nozzle = Seek(nozzle, 1, 0.8, 0.8, dt); }
            if (augMethod == 2)
            {
                if (augCmd > 0) { aug = true; double tdiff = maxThrust * augThr() - thrust; thrust += tdiff * Math.Min(augCmd, 1.0); ffPph = Seek(ffPph, thrust * atsfc, 5000, 10000, dt); nozzle = Seek(nozzle, 1, 0.8, 0.8, dt); }
                else aug = false;
            }
            if (cutoff || starved) phase = Phase.Off;
            return thrust;
        }
        public D3 BodyForce() => _fb;
        public double Thrust => _thrust;
        /// <summary>consume fuel from the feed tanks (FGPropulsion::GetSteadyState / Run).</summary>
        public void Burn(double dt)
        {
            double fuel = ffPph / 3600.0 * dt; if (fuel <= 0) return; fuelUsed += fuel;
            foreach (var ti in _feeds) { if (ti >= fdm.tanks.Count) continue; var t = fdm.tanks[ti]; double take = Math.Min(t.contents, fuel); t.contents -= take; fuel -= take; if (fuel <= 1e-12) break; }
        }
    }
}
