// Frame-by-frame parity of JsbFdm (the generic C# port) against native JSBSim logs (gen_golden.py).
// usage: dotnet run -- <jsbsim_root> <aircraft> <golden.csv>...
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RocketWorld.Sim;

static class P
{
    static int Main(string[] a)
    {
        string root = a[0], ac = a[1]; int fails = 0;
        for (int gi = 2; gi < a.Length; gi++) fails += Run(root, ac, a[gi]);
        Console.WriteLine(fails == 0 ? "PARITY OK" : "PARITY FAILED (" + fails + ")"); return fails == 0 ? 0 : 1;
    }
    static int Run(string root, string ac, string csv)
    {
        string dir = Path.Combine(root, "aircraft", ac);
        Func<string, string> eng = n => File.ReadAllText(Path.Combine(root, "engine", n + ".xml"));
        Func<string, string> sys = n => { string p = Path.Combine(dir, "Systems", n + ".xml"); if (!File.Exists(p)) p = Path.Combine(root, "systems", n + ".xml"); return File.ReadAllText(p); };
        var f = JsbFdm.Load(File.ReadAllText(Path.Combine(dir, ac + ".xml")), eng, sys, n => { string p = Path.Combine(dir, "Engines", n + ".xml"); return File.Exists(p) ? File.ReadAllText(p) : eng(n); });
        var lines = File.ReadAllLines(csv); var h = lines[0].Split(','); var col = new Dictionary<string, int>(); for (int i = 0; i < h.Length; i++) col[h[i]] = i;
        f.StartEngines(1.0);   // native: set-running=-1 happens while the throttle is still at its default (1.0)
        var err = new Dictionary<string, double>(); var scale = new Dictionary<string, double>(); var worstT = new Dictionary<string, double>();
        void Cmp(string name, double mine, double nat, double t)
        {
            if (t < double.Parse(Environment.GetEnvironmentVariable("RW_SKIP") ?? "0.02")) return;   // frame 0 uses the IC accelerations which are not logged
            double e = Math.Abs(mine - nat); if (!scale.ContainsKey(name)) { scale[name] = 0; err[name] = 0; worstT[name] = 0; }
            scale[name] = Math.Max(scale[name], Math.Abs(nat)); if (e > err[name]) { err[name] = e; worstT[name] = t; }
        }
        // JSBSim runs Propagate FIRST in each frame: forces of frame i are computed from the state at the END of the integration step, i.e. the state columns of row i+1.
        for (int li = 1; li < lines.Length - 1; li++)
        {
            var r = lines[li].Split(','); Func<string, double> g = k => double.Parse(r[col[k]], CultureInfo.InvariantCulture); double t = g("t"); int nE = (int)g("nE");
            f["fcs/elevator-cmd-norm"] = g("el"); f["fcs/aileron-cmd-norm"] = g("ai"); f["fcs/rudder-cmd-norm"] = g("ru"); f["fcs/speedbrake-cmd-norm"] = g("sb"); f["fcs/flap-cmd-norm"] = g("flap"); f["gear/gear-cmd-norm"] = g("gear");
            for (int i = 0; i < nE; i++) f["fcs/throttle-cmd-norm[" + i + "]"] = g("thr");
            foreach (var k in col.Keys) if (k.StartsWith("in:")) f[k.Substring(3)] = g(k);
            var rs = lines[li + 1].Split(','); Func<string, double> gs = k => double.Parse(rs[col[k]], CultureInfo.InvariantCulture);
            var s = new JsbFdm.State { uvw = new D3(gs("velocities/u-fps"), gs("velocities/v-fps"), gs("velocities/w-fps")), pqr = new D3(gs("velocities/p-rad_sec"), gs("velocities/q-rad_sec"), gs("velocities/r-rad_sec")),
                altSlFt = gs("position/h-sl-ft"), aglFt = gs("position/h-agl-ft"), phi = gs("attitude/phi-rad"), theta = gs("attitude/theta-rad"), psi = gs("attitude/psi-rad"),
                uvwDot = new D3(g("accelerations/udot-ft_sec2"), g("accelerations/vdot-ft_sec2"), g("accelerations/wdot-ft_sec2")) };
            var o = f.Step(s, 1.0 / 120);
            if (li == 1 || li == 2) { /* engines start identically in both (set-running) */ }
            if (col.ContainsKey("forces/fbx-external-lbs")) { Cmp("ext Fx", o.extForce.x, g("forces/fbx-external-lbs"), t); Cmp("ext Fy", o.extForce.y, g("forces/fby-external-lbs"), t); Cmp("ext Fz", o.extForce.z, g("forces/fbz-external-lbs"), t); Cmp("ext L", o.extMoment.x, g("moments/l-external-lbsft"), t); Cmp("ext M", o.extMoment.y, g("moments/m-external-lbsft"), t); Cmp("ext N", o.extMoment.z, g("moments/n-external-lbsft"), t); }
            Cmp("aero Fx", o.aeroForce.x, g("forces/fbx-aero-lbs"), t); Cmp("aero Fy", o.aeroForce.y, g("forces/fby-aero-lbs"), t); Cmp("aero Fz", o.aeroForce.z, g("forces/fbz-aero-lbs"), t);
            Cmp("aero L", o.aeroMoment.x, g("moments/l-aero-lbsft"), t); Cmp("aero M", o.aeroMoment.y, g("moments/m-aero-lbsft"), t); Cmp("aero N", o.aeroMoment.z, g("moments/n-aero-lbsft"), t);
            if (col.ContainsKey("forces/fbx-prop-lbs")) { Cmp("prop Fx", o.propForce.x, g("forces/fbx-prop-lbs"), t); Cmp("prop Fy", o.propForce.y, g("forces/fby-prop-lbs"), t); Cmp("prop Fz", o.propForce.z, g("forces/fbz-prop-lbs"), t);
            Cmp("prop L", o.propMoment.x, g("moments/l-prop-lbsft"), t); Cmp("prop M", o.propMoment.y, g("moments/m-prop-lbsft"), t); Cmp("prop N", o.propMoment.z, g("moments/n-prop-lbsft"), t); }
            Cmp("alpha", o.alpha, gs("aero/alpha-rad"), t); Cmp("qbar", o.qbar, gs("aero/qbar-psf"), t); Cmp("mach", o.mach, gs("velocities/mach"), t); Cmp("vc-kts", o.vcKts, gs("velocities/vc-kts"), t);
            Cmp("alphadot", f["aero/alphadot-rad_sec"], gs("aero/alphadot-rad_sec"), t); Cmp("beta", f["aero/beta-rad"], gs("aero/beta-rad"), t); Cmp("h_b-mac", f["aero/h_b-mac-ft"], gs("aero/h_b-mac-ft"), t); Cmp("bi2vel", f["aero/bi2vel"], gs("aero/bi2vel"), t);
            
            Cmp("weight", f["inertia/weight-lbs"], g("inertia/weight-lbs"), t); Cmp("cg-x", f["inertia/cg-x-in"], g("inertia/cg-x-in"), t);
            foreach (var k in new[] { "fcs/elevator-pos-rad", "fcs/left-aileron-pos-rad", "fcs/aileron-pos-rad", "fcs/nozzle-pos-deg", "fcs/fan-pos-norm", "fcs/roll-post-left-lbs", "fcs/rudder-pos-rad", "fcs/flap-pos-deg", "fcs/speedbrake-pos-norm", "gear/gear-pos-norm" }) if (col.ContainsKey(k)) Cmp(k, f[k], g(k), t);
            for (int i = 0; i < nE; i++) foreach (var q in new[] { "thrust-lbs", "n1", "n2" }) { string k = "propulsion/engine[" + i + "]/" + q; if (col.ContainsKey(k)) Cmp(k, f[k.Replace("[0]", "")], g(k), t); }
        }
        int bad = 0; Console.WriteLine("== " + ac + "  " + Path.GetFileName(csv) + "  (" + (lines.Length - 1) + " frames)");
        foreach (var k in err.Keys) { double rel = err[k] / Math.Max(1e-9, Math.Max(scale[k], 1.0)); bool ok = rel < double.Parse(Environment.GetEnvironmentVariable("RW_TOL") ?? "1e-4", CultureInfo.InvariantCulture); if (!ok) bad++; Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {k,-34} max|err| {err[k]:0.######e+0}  (scale {scale[k]:0.###})  rel {rel:0.##e+0}  worst t={worstT[k]:0.00}"); }
        return bad;
    }
}
