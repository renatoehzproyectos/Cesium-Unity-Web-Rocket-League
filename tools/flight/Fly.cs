// Free-flight sanity run of the ported model with a plain 6-DOF flat-earth integrator (RK2): trim, hands-off flight, stall.
using System;
using System.IO;
using RocketWorld.Sim;

static class Fly
{
    public static int Run(string res)
    {
        int fails = 0;
        Action<bool, string> check = (ok, msg) => { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + msg); if (!ok) fails++; };
        var ac = JsbAircraft.Load(File.ReadAllText(res + "/boxplane.xml"), File.ReadAllText(res + "/eng_electric160.xml"), File.ReadAllText(res + "/prop_75in2f.xml"));
        double W = ac.Weight, m = ac.massSlug, g = 32.174049, alt = 3000;
        Console.WriteLine($"INFO  mass {m * 14.5939:0} kg, weight {W:0} lbf, wing {ac.wingSpan * 0.3048:0.0} m span");
        // trim at 100 kt: level flight, theta = alpha. Bisect alpha for lift = weight, elevator for zero pitching moment, then thrust needed.
        double v = 100 * 1.68781;
        double alpha = 0.05, el = 0;
        for (int it = 0; it < 60; it++)
        {
            double lo = -0.05, hi = 0.25;
            for (int b = 0; b < 50; b++) { double mid = 0.5 * (lo + hi); alpha = mid; var o = Eval(ac, v, alpha, el, 0, alt); double nz = o.aeroForce.z * Math.Cos(alpha) - o.aeroForce.x * Math.Sin(alpha) + W; if (nz > 0) lo = mid; else hi = mid; }
            double elo = -0.35, ehi = 0.30;
            for (int b = 0; b < 50; b++) { double mid = 0.5 * (elo + ehi); var o = Eval(ac, v, alpha, mid, 0, alt); if (o.aeroMoment.y > 0) elo = mid; else ehi = mid; el = 0.5 * (elo + ehi); }
        }
        var tr = Eval(ac, v, alpha, el, 0, alt);
        double needThrust = W * Math.Sin(alpha) - tr.aeroForce.x;      // body-x balance with theta = alpha
        Console.WriteLine($"INFO  trim 100 kt: alpha {alpha * 57.2958:0.0} deg, elevator {el:0.000} rad, thrust needed {needThrust:0} lbf (L/D {W / needThrust:0.0})");
        check(alpha > 0 && alpha < 0.1 && el > -0.3 && el < 0.3, "trim exists with the elevator inside its travel");
        // thrust available: spin the prop up at full throttle at this speed
        double thrustFull = 0; ac.propRpm = 2000; ac.throttle = 1;
        for (int i = 0; i < 1200; i++) { var o = ac.Step(new D3(v * Math.Cos(alpha), 0, v * Math.Sin(alpha)), new D3(0, 0, 0), alt, 1.0 / 120); thrustFull = o.thrust; }
        Console.WriteLine($"INFO  full-throttle thrust at 100 kt: {thrustFull:0} lbf at {ac.propRpm:0} rpm");
        check(thrustFull > needThrust * 1.5, "enough thrust for cruise and climb");
        // hands-off flight 30 s with throttle that balances drag (found by bisection on the steady-state thrust)
        double tlo = 0, thi = 1;
        for (int b = 0; b < 30; b++)
        {
            double tm = 0.5 * (tlo + thi); ac.throttle = tm; ac.propRpm = 2200; double th = 0;
            for (int i = 0; i < 1200; i++) th = ac.Step(new D3(v * Math.Cos(alpha), 0, v * Math.Sin(alpha)), new D3(0, 0, 0), alt, 1.0 / 120).thrust;
            if (th > needThrust) thi = tm; else tlo = tm;
        }
        double thrTrim = 0.5 * (tlo + thi); Console.WriteLine($"INFO  trim throttle {thrTrim:0.00}");
        var s = new State { u = v * Math.Cos(alpha), w = v * Math.Sin(alpha), theta = alpha, h = alt, qq = new Quat(Math.Cos(alpha / 2), 0, Math.Sin(alpha / 2), 0) };
        ac.SetControls(ElevatorCmd(el), 0, 0, thrTrim); ac.propRpm = 2200;
        double hMin = 1e9, hMax = -1e9, vMin = 1e9, vMax = -1e9;
        for (int i = 0; i < 120 * 40; i++)
        {
            Integrate(ac, ref s, 1.0 / 120, m, g);
            if (i > 120 * 5) { hMin = Math.Min(hMin, s.h); hMax = Math.Max(hMax, s.h); double vt = Math.Sqrt(s.u * s.u + s.v * s.v + s.w * s.w); vMin = Math.Min(vMin, vt); vMax = Math.Max(vMax, vt); }
            if (i % (120 * 10) == 0) Console.WriteLine($"INFO  t={i / 120.0,4:0}s alt {s.h:0} ft, speed {Math.Sqrt(s.u * s.u + s.v * s.v + s.w * s.w) / 1.68781:0} kt, pitch {s.PitchDeg:0.0} deg, bank {s.BankDeg:0.0} deg");
        }
        check(hMax - hMin < 600 && vMin / 1.68781 > 55 && vMax / 1.68781 < 130 && Math.Abs(s.BankDeg) < 45, $"hands-off for 40 s stays bounded (a slow spiral drift is normal for a light aircraft) (altitude band {hMax - hMin:0} ft, speed {vMin / 1.68781:0}-{vMax / 1.68781:0} kt)");
        // stall: closed-form from the model's own table
        double clMax = 0; for (double a = 0; a < 0.36; a += 0.005) { var o = Eval(ac, 100, a, 0, 0, 0); clMax = Math.Max(clMax, -o.aeroForce.z / (0.5 * 0.002377 * 100 * 100 * ac.wingArea)); }
        double vs = Math.Sqrt(2 * W / (0.002377 * ac.wingArea * clMax)) / 1.68781;
        Console.WriteLine($"INFO  CLmax {clMax:0.00}, stall speed {vs:0} kt");
        check(vs > 38 && vs < 55, "stall speed is in the light-aircraft range (38-55 kt)");
        return fails;
    }

    // inverse of the zero-centred aerosurface_scale (up travel +0.30 rad, down -0.35 rad)
    static double ElevatorCmd(double posRad) => posRad >= 0 ? posRad / 0.30 : posRad / 0.35;
    static JsbAircraft.Result Eval(JsbAircraft ac, double v, double alpha, double elPos, double thr, double alt)
    {
        ac.elevatorPosRad = elPos; ac.aileronPosRad = 0; ac.rudderPosRad = 0; ac.throttle = thr; double r = ac.propRpm; ac.propRpm = 0;
        var o = ac.Step(new D3(v * Math.Cos(alpha), 0, v * Math.Sin(alpha)), new D3(0, 0, 0), alt, 1.0 / 120); ac.propRpm = r; return o;
    }

    struct Quat { public double w, x, y, z; public Quat(double w, double x, double y, double z) { this.w = w; this.x = x; this.y = y; this.z = z; } }
    struct State
    {
        public double u, v, w, p, q, r, h; public double theta; public Quat qq;
        public double PitchDeg { get { double sp = 2 * (qq.w * qq.y - qq.z * qq.x); return Math.Asin(Math.Max(-1, Math.Min(1, sp))) * 57.2958; } }
        public double BankDeg { get { return Math.Atan2(2 * (qq.w * qq.x + qq.y * qq.z), 1 - 2 * (qq.x * qq.x + qq.y * qq.y)) * 57.2958; } }
    }
    static void Integrate(JsbAircraft ac, ref State s, double dt, double m, double g)
    {
        var o = ac.Step(new D3(s.u, s.v, s.w), new D3(s.p, s.q, s.r), s.h, dt);
        // gravity in body axes from the attitude quaternion (NED: down = +z)
        double qw = s.qq.w, qx = s.qq.x, qy = s.qq.y, qz = s.qq.z;
        D3 gb = new D3(2 * (qx * qz - qw * qy), 2 * (qy * qz + qw * qx), 1 - 2 * (qx * qx + qy * qy)) * g;
        D3 a = new D3(o.force.x / m + gb.x - (s.q * s.w - s.r * s.v), o.force.y / m + gb.y - (s.r * s.u - s.p * s.w), o.force.z / m + gb.z - (s.p * s.v - s.q * s.u));
        double pd = (o.moment.x - (ac.izz - ac.iyy) * s.q * s.r) / ac.ixx, qd = (o.moment.y - (ac.ixx - ac.izz) * s.r * s.p) / ac.iyy, rd = (o.moment.z - (ac.iyy - ac.ixx) * s.p * s.q) / ac.izz;
        // vertical speed: h' = -(down component of body velocity in NED) = u*sin(theta) - ... via rotation of body velocity
        double down = (2 * (qx * qz - qw * qy)) * s.u + (2 * (qy * qz + qw * qx)) * s.v + (1 - 2 * (qx * qx + qy * qy)) * s.w;
        s.h -= down * dt;
        s.u += a.x * dt; s.v += a.y * dt; s.w += a.z * dt; s.p += pd * dt; s.q += qd * dt; s.r += rd * dt;
        double wx = s.p, wy = s.q, wz = s.r;
        double nw = qw + 0.5 * dt * (-qx * wx - qy * wy - qz * wz), nx = qx + 0.5 * dt * (qw * wx + qy * wz - qz * wy), ny = qy + 0.5 * dt * (qw * wy - qx * wz + qz * wx), nz = qz + 0.5 * dt * (qw * wz + qx * wy - qy * wx);
        double n = Math.Sqrt(nw * nw + nx * nx + ny * ny + nz * nz); s.qq = new Quat(nw / n, nx / n, ny / n, nz / n);
    }
}
