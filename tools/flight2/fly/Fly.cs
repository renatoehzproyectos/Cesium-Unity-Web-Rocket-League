// Headless 6-DOF flight tests of the JsbFdm aircraft (flat Earth, gear contacts on a runway at h = 0, simple Euler-quaternion integrator).
// usage: dotnet run -- <resources/Flight/jsb dir> <f15e|a320|f35b-takeoff|f35b-vtol>
using System;
using System.IO;
using RocketWorld.Sim;

static class Fly
{
    static string dir;
    static string Txt(string n) => File.ReadAllText(Path.Combine(dir, n + ".xml"));
    // state
    static D3 uvw, pqr; static double phi, theta, psi, n, e, h;            // h up, ft
    static JsbFdm f; static double dt = 1.0 / 120, t;
    static D3 uvwDotPrev;

    static void Ned(out double[,] c)
    {
        double sp = Math.Sin(phi), cp = Math.Cos(phi), st = Math.Sin(theta), ct = Math.Cos(theta), ss = Math.Sin(psi), cs = Math.Cos(psi);
        c = new double[3, 3] { { ct * cs, sp * st * cs - cp * ss, cp * st * cs + sp * ss }, { ct * ss, sp * st * ss + cp * cs, cp * st * ss - sp * cs }, { -st, sp * ct, cp * ct } };   // body -> NED
    }
    static D3 ToBody(double[,] c, D3 v) => new D3(c[0, 0] * v.x + c[1, 0] * v.y + c[2, 0] * v.z, c[0, 1] * v.x + c[1, 1] * v.y + c[2, 1] * v.z, c[0, 2] * v.x + c[1, 2] * v.y + c[2, 2] * v.z);
    static D3 ToNed(double[,] c, D3 v) => new D3(c[0, 0] * v.x + c[0, 1] * v.y + c[0, 2] * v.z, c[1, 0] * v.x + c[1, 1] * v.y + c[1, 2] * v.z, c[2, 0] * v.x + c[2, 1] * v.y + c[2, 2] * v.z);

    static void Step(double el, double ail, double rud, double lever, double brake, double steer, double extraTrim = 0)
    {
        f["fcs/elevator-cmd-norm"] = el; f["fcs/aileron-cmd-norm"] = ail; f["fcs/rudder-cmd-norm"] = rud; f["fcs/pitch-trim-cmd-norm"] = extraTrim;
        Ned(out var C);
        var s = new JsbFdm.State { uvw = uvw, pqr = pqr, uvwDot = uvwDotPrev, altSlFt = h, aglFt = Math.Max(0.0, h), phi = phi, theta = theta, psi = psi };
        var o = f.Step(s, dt);
        D3 F = o.force, M = o.moment;
        // gear
        foreach (var g in f.gears)
        {
            if (!g.Extended) continue;
            D3 r = f.StructuralToBody(g.locIn), rN = ToNed(C, r); double hc = h - rN.z;       // contact height above ground (ft): h up, rN.z down-positive
            if (hc >= 0) continue;
            D3 vp = uvw + D3.Cross(pqr, r); D3 vpN = ToNed(C, vp);
            double comp = -hc, vc = vpN.z;                                                    // closing speed (down positive = compressing)
            double strut = -g.StrutForce(comp, vc);                                           // lbf up
            // ground frame: wheel heading
            double steerA = (g.maxSteerDeg > 0.01 ? steer * g.maxSteerDeg * Math.PI / 180 / (1 + Math.Pow(Math.Sqrt(vpN.x * vpN.x + vpN.y * vpN.y) / 30, 2)) : 0), hd = psi + steerA;
            double vxw = vpN.x * Math.Cos(hd) + vpN.y * Math.Sin(hd), vyw = -vpN.x * Math.Sin(hd) + vpN.y * Math.Cos(hd);
            double slip = -Math.Atan2(vyw, Math.Abs(vxw)) * 180 / Math.PI;
            double side = Math.Sqrt(vxw * vxw + vyw * vyw) > 3 ? g.SideForceCoeff(slip) : Math.Max(-1, Math.Min(1, -vyw / 3)) * g.staticF;
            double b = g.brakeGroup == "NONE" ? 0 : brake; double rollc = -Math.Max(-1, Math.Min(1, vxw / 2.5)) * g.BrakeForceCoeff(b);
            D3 fN = new D3((rollc * Math.Cos(hd) - side * Math.Sin(hd)) * strut, (rollc * Math.Sin(hd) + side * Math.Cos(hd)) * strut, -strut);   // NED: up = -z
            D3 fB = ToBody(C, fN); F = F + fB; M = M + D3.Cross(r, fB);
        }
        double m = f.massSlug, gA = 32.174049;
        D3 gB = ToBody(C, new D3(0, 0, gA));
        D3 uvwDot = new D3(F.x / m + gB.x - (pqr.y * uvw.z - pqr.z * uvw.y), F.y / m + gB.y - (pqr.z * uvw.x - pqr.x * uvw.z), F.z / m + gB.z - (pqr.x * uvw.y - pqr.y * uvw.x));
        double ixx = f.ixx, iyy = f.iyy, izz = f.izz;
        D3 pqrDot = new D3((M.x - (izz - iyy) * pqr.y * pqr.z) / ixx, (M.y - (ixx - izz) * pqr.z * pqr.x) / iyy, (M.z - (iyy - ixx) * pqr.x * pqr.y) / izz);
        uvw = uvw + uvwDot * dt; pqr = pqr + pqrDot * dt; uvwDotPrev = uvwDot;
        double sp = Math.Sin(phi), cp = Math.Cos(phi), tt = Math.Tan(theta), ct = Math.Cos(theta);
        phi += (pqr.x + sp * tt * pqr.y + cp * tt * pqr.z) * dt; theta += (cp * pqr.y - sp * pqr.z) * dt; psi += ((sp * pqr.y + cp * pqr.z) / ct) * dt;
        D3 vN = ToNed(C, uvw); n += vN.x * dt; e += vN.y * dt; h -= vN.z * dt; t += dt;
        if (h < -5) h = -5;
    }
    static string St() => $"t={t,5:0.0}s  {Math.Sqrt(uvw.x * uvw.x + uvw.y * uvw.y + uvw.z * uvw.z) / 1.68781,5:0} kt  alt {h,7:0} ft  pitch {theta * 57.3,5:0.0}  roll {phi * 57.3,5:0.0}  M {f["velocities/mach"]:0.00}  alpha {f["aero/alpha-deg"],5:0.0}  fuel {f["propulsion/total-fuel-lbs"]:0} lb";

    static int Main(string[] a)
    {
        dir = a[0]; string sc = a[1];
        if (sc == "f15e") { Load("f15e"); Takeoff("F-15E", 1.0, 0.0, 0, 142, -0.30, 160, 1.0); }
        else if (sc == "a320") { Load("a320"); f["fcs/flap-cmd-norm"] = 0.25; Takeoff("A320", 1.0, 0.0, 0, 135, -0.28, 150, 1.0, true); }
        else if (sc == "f35b") { Load("f35b"); Takeoff("F-35B (CTOL)", 1.0, 0.0, 0, 130, -0.30, 150, 1.0); }
        else if (sc == "f35b-vtol") Vtol();
        else if (sc == "delorean") Dmc();
        else if (sc == "heli") Heli();
        return 0;
    }
    static void Load(string ac)
    {
        f = JsbFdm.Load(Txt(ac), Txt, Txt, Txt); h = 0; theta = 0; phi = 0; psi = 0; uvw = new D3(); pqr = new D3();
        // settle on the gear: place the CG at its static height
        f["gear/gear-cmd-norm"] = 1; f["gear/gear-pos-norm"] = 1;
        double gz = 0; foreach (var g in f.gears) gz = Math.Max(gz, f.StructuralToBody(g.locIn).z); h = gz - 0.2;   // wheels ~ on the ground
        f.StartEngines(0.0);
    }
    static void Takeoff(string name, double lever, double steer, double brake, double vr, double pull, double vclimb, double afterLever, bool airliner = false)
    {
        Console.WriteLine("=== " + name + " take-off from rest, lever " + lever + " ===");
        double x0 = n; bool rotated = false, lifted = false; double trim = 0; double tLift = 0; double vLift = 0; double dLift = 0; double maxA = 0;
        double vkt() => Math.Sqrt(uvw.x * uvw.x + uvw.y * uvw.y + uvw.z * uvw.z) / 1.68781;
        for (int i = 0; i < 120 * 150; i++)
        {
            double thr = lever <= 0.75 ? lever / 0.75 : 1 + (lever - 0.75) / 0.25; if (airliner) thr = lever;
            for (int k = 0; k < f.engines.Count; k++) f["fcs/throttle-cmd-norm[" + k + "]"] = name.StartsWith("F-35") ? 0.0 : (name.StartsWith("F-15") ? thr : lever);
            f["fcs/lever-norm"] = lever; f["fcs/stovl-cmd-norm"] = 0;
            double el = 0, hold = 0;
            if (!rotated && vkt() >= vr) { rotated = true; }
            if (rotated) { double tgt = (lifted ? 12.0 : 9.0) * Math.PI / 180; if (airliner) tgt = (lifted ? 10.0 : 8.0) * Math.PI / 180; double kp = airliner ? 0.8 : 1.4, kq = airliner ? 0.9 : 0.7; el = -(kp * (tgt - theta) - kq * pqr.y); el = Math.Max(-0.6, Math.Min(0.6, el)); }
            if (lifted && h > 60) { f["gear/gear-cmd-norm"] = 0; }
            if (rotated && !airliner || (rotated && airliner)) { // pitch hold assist: trim nulls q once airborne
                 if (trim > 0.6) trim = 0.6; if (trim < -0.6) trim = -0.6; }
            if (lifted && h > 800 && name.StartsWith("F-15")) { }
            if (airliner && lifted && h > 1500) f["fcs/flap-cmd-norm"] = 0;
            Step(el, 0, 0, lever, brake, steer, trim);
            if (!lifted && h > 1.2 + 0.2 && rotated) { lifted = true; tLift = t; vLift = vkt(); dLift = (n - x0) * 0.3048; }
            maxA = Math.Max(maxA, f["aero/alpha-deg"]);
            if (i % (120 * 10) == 0) Console.WriteLine(St());
            if (h > 3000) break;
        }
        Console.WriteLine(St());
        Console.WriteLine($"liftoff at {tLift:0.0}s, {vLift:0} kt, ground roll {dLift:0} m; max alpha {maxA:0.0} deg");
    }
    static void Dmc()
    {
        Console.WriteLine("=== DeLorean: road, then hover ===");
        Load("delorean"); double t60 = 0, d0 = 0; bool got = false;
        for (int i = 0; i < 120 * 40; i++)
        {
            f["fcs/throttle-cmd-norm"] = t < 14 ? 1.0 : (t < 20 ? 0.0 : 0.0); f["fcs/hover-cmd-norm"] = t > 22 ? 1 : 0; f["fcs/vert-cmd-norm"] = t > 24 && t < 28 ? 1 : 0; f["fcs/steer-cmd-norm"] = t > 8 && t < 11 ? 0.3 : 0;
            double sp = Math.Sqrt(uvw.x * uvw.x + uvw.y * uvw.y);
            if (!got && sp > 60 * 1.46667) { got = true; t60 = t; }
            Step(0, 0, 0, 0, t > 16 && t < 20 ? 1.0 : 0.0, t > 8 && t < 11 ? 0.3 : 0);
            if (i % (120 * 2) == 0) Console.WriteLine(St().Replace("pitch", "pitch") + $"  speed {sp * 0.3048:0.0} m/s  gear {f["gear/gear-pos-norm"]:0.00}  hover {f["fcs/hover-pos-norm"]:0.00}");
        }
        Console.WriteLine($"0-60 mph in {t60:0.0} s");
    }
    static void Heli()
    {
        Console.WriteLine("=== Helicopter: lift-off from the ground, hover, forward flight ===");
        Load("heli"); double lever = 0.2;
        for (int i = 0; i < 120 * 60; i++)
        {
            if (t > 3 && t < 12) lever = Math.Min(0.62, lever + 0.5 * dt); else if (t >= 12 && t < 26) lever = 0.55; else if (t >= 26) lever = 0.55;
            f["fcs/collective-cmd-norm"] = lever; f["fcs/vert-cmd-norm"] = 0; f["fcs/hold-on"] = 1;
            double el = t > 30 && t < 50 ? 0.6 : 0.0;
            Step(el, 0, 0, lever, 0, 0);
            if (i % (120 * 3) == 0) Console.WriteLine(St() + $"  rotor {f["rotor/omega-rad_sec"] / 33.5 * 100:0}%  T {f["rotor/thrust-lbs"]:0} lb  P {f["rotor/p-rotor"] / 550:0} hp  coll {f["fcs/collective-total"]:0.00}");
        }
    }
    static void Vtol()
    {
        Console.WriteLine("=== F-35B vertical take-off, hover, transition ===");
        Load("f35b"); f["fcs/stovl-cmd-norm"] = 1; f["fcs/nozzle-pos-deg"] = 90; f["fcs/fan-pos-norm"] = 1; f["fcs/flap-cmd-norm"] = 1; f.StartEngines(0.9);
        double vert = 0, lever = 0, el = 0;
        for (int i = 0; i < 120 * 90; i++)
        {
            double tt = t;
            if (tt < 4) vert = 0; else if (tt < 14) vert = 1; else if (tt < 24) vert = 0; else if (tt > 24) vert = (h > 120 ? -0.1 : 0);
            lever = tt > 32 ? Math.Min(0.8, (tt - 32) * 0.1) : 0; el = tt > 28 && tt < 32 ? 0.5 : 0;
            f["fcs/vert-cmd-norm"] = vert; f["fcs/lever-norm"] = lever; f["fcs/stovl-cmd-norm"] = tt > 60 ? 0 : 1;
            Step(el, 0, 0, lever, 0, 0);
            if (i % (120 * 4) == 0) Console.WriteLine(St() + $"  nozzle {f["fcs/nozzle-pos-deg"]:0}  fan {f["propulsion/engine[1]/thrust-lbs"]:0}  main {f["propulsion/engine[0]/thrust-lbs"]:0}  gear {f["gear/gear-pos-norm"]:0.0}");
        }
    }
}
