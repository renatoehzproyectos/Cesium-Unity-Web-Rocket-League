// Frame-by-frame check of the C# JSBSim port against native JSBSim 1.2.x golden logs (tools/flight/golden, made by gen_golden.py).
// JSBSim logs the state at the START of each frame together with that frame's forces, so each row is self-contained: its state is fed to
// JsbAircraft.Step and the atmosphere / wind angles / aero forces + moments / propeller forces + moments / rpm are compared with the row.
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using RocketWorld.Sim;

static class P
{
    static int fails;
    static void Main(string[] args)
    {
        string res = args.Length > 0 ? args[0] : "../../Assets/Resources/Flight", gold = args.Length > 1 ? args[1] : "golden";
        foreach (var file in Directory.GetFiles(gold, "*.csv").OrderBy(f => f)) Run(res, file);
        fails += Fly.Run(res);
        Console.WriteLine(fails == 0 ? "ALL OK" : fails + " FAILED"); Environment.Exit(fails == 0 ? 0 : 1);
    }

    static void Run(string res, string file)
    {
        var lines = File.ReadAllLines(file); string[] meta = lines[0].Trim('"').Split(','); double thrSet = double.Parse(meta[3], CultureInfo.InvariantCulture);
        string[] hdr = lines[1].Split(','); var col = hdr.Select((h, i) => new { h, i }).ToDictionary(x => x.h.Trim(), x => x.i);
        double[][] rows = lines.Skip(2).Where(l => l.Length > 5).Select(l => l.Split(',').Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray()).ToArray();
        var ac = JsbAircraft.Load(File.ReadAllText(res + "/boxplane.xml"), File.ReadAllText(res + "/eng_electric160.xml"), File.ReadAllText(res + "/prop_75in2f.xml"));
        double dt = rows[1][0] - rows[0][0];
        string[] names = { "alpha-rad", "beta-rad", "qbar-psf", "rho-slugs_ft3", "a-fps", "fbx-aero-lbs", "fby-aero-lbs", "fbz-aero-lbs", "l-aero-lbsft", "m-aero-lbsft", "n-aero-lbsft",
                           "fbx-prop-lbs", "l-prop-lbsft", "m-prop-lbsft", "n-prop-lbsft", "propeller-rpm" };
        var worst = names.ToDictionary(n => n, n => 0.0);
        ac.propRpm = rows[0][col["propeller-rpm"]];
        for (int k = 0; k < rows.Length; k++)
        {
            double[] s = rows[k], r = rows[k];
            ac.elevatorPosRad = r[col["elevator-pos-rad"]]; ac.aileronPosRad = r[col["aileron-pos-rad"]]; ac.rudderPosRad = r[col["rudder-pos-rad"]];
            ac.throttle = r[0] > 1.0 ? thrSet : 0.0;
            var o = ac.Step(new D3(s[col["u-fps"]], s[col["v-fps"]], s[col["w-fps"]]), new D3(s[col["p-rad_sec"]], s[col["q-rad_sec"]], s[col["r-rad_sec"]]), s[col["h-sl-ft"]], dt);
            double[] mine = { o.alpha, o.beta, o.qbar, o.rho, o.soundFps, o.aeroForce.x, o.aeroForce.y, o.aeroForce.z, o.aeroMoment.x, o.aeroMoment.y, o.aeroMoment.z,
                              o.propForce.x, o.propMoment.x, o.propMoment.y, o.propMoment.z, o.rpm };
            if (k < 1) continue;                                   // frame 0 only runs with the controls not yet applied
            for (int i = 0; i < names.Length; i++)
            {
                // error floor of 10: JSBSim adds Earth's rotation (7e-5 rad/s) to the gyroscopic term, the port does not (<= 0.01 lbf*ft)
                double want = r[col[names[i]]], err = Math.Abs(mine[i] - want) / Math.Max(10.0, Math.Abs(want));
                if (err > worst[names[i]]) { worst[names[i]] = err; if (Environment.GetEnvironmentVariable("DBG") != null && err > 1e-3) Console.WriteLine($"   k={k} t={r[0]:0.0000} {names[i]} mine={mine[i]:G6} native={want:G6}"); }
            }
        }
        double max = worst.Values.Max(); bool ok = max < 2e-3;
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + Path.GetFileNameWithoutExtension(file).PadRight(11) + $" {rows.Length - 2} frames, worst relative error {max:E2}" + (ok ? "" : "  <- " + worst.OrderByDescending(x => x.Value).First().Key));
        if (!ok) { fails++; foreach (var w in worst.OrderByDescending(x => x.Value).Take(4)) Console.WriteLine($"        {w.Key}: {w.Value:E2}"); }
    }
}
