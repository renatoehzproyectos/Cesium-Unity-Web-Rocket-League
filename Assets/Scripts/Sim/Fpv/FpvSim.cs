// Quadcopter flight model, fixed 1 ms step, engine-free.
//
// C# port of packages/sim-core/src/sim.ts from ShramkoGSFPV (MIT, Andrii Shramko; see LICENSE-ShramkoGSFPV.txt):
// motors with lag and battery sag, X mixer with airmode, Betaflight-style rate PID (gyro / D-term filters, feed-forward,
// I-term relax, anti-windup), angle / horizon self-level, rotor-momentum drag, prop wash, and sphere-vs-world contact with
// speed-dependent bounce, Coulomb friction and crash detection. tools/fpv replays golden traces of the original on this port.
//
// Frames (kept exactly as the original so the numbers match): world Y up, right-handed; body: x right, y up (thrust),
// z back (the nose points to -z). Angular velocity w is in the body frame. Pilot / PID axes (Betaflight): roll + = roll right,
// pitch + = nose down, yaw + = yaw right (body: roll = -w.z, pitch = -w.x, yaw = -w.y).
// RocketWorld's Unity frame is left-handed (z forward): FpvFrame converts at the border, never inside the model.
//
// Channels ch[0..7] in [-1, 1]: 0 roll (right +), 1 pitch (stick forward +), 2 throttle (-1 low .. +1 full), 3 yaw (right +),
// 4 arm switch (> 0.5 on), 5 flight mode (-1 acro, 0 horizon, +1 angle), 6..7 spare.
//
// Left out against the original: the wind field, the invisible spawn platform, the lesson "slide" bump, replay hashing.
using System;
using System.Collections.Generic;

namespace RocketWorld.Sim.Fpv
{
    public enum FpvEventType { Arm, Disarm, Contact, Crash, Rest, Respawn }

    public struct FpvEvent
    {
        public FpvEventType type;
        public int tick;
        public double speed;            // contact / crash: approach speed of the touching point, m/s
        public bool bounce;             // contact: above vBounce (else a slide)
        public double nx, ny, nz;       // surface outward normal
        public double px, py, pz;       // crash: contact point (world)
        public string reason;           // disarm: "switch" / "crash"
    }

    /// <summary>Result of a sweep: which sphere touched first and the outward normal there.</summary>
    public struct FpvContact { public int sphere; public double nx, ny, nz; }

    public struct V3d { public double x, y, z; }

    /// <summary>Collision provider. The model does not depend on any collision implementation.</summary>
    public interface IFpvWorld
    {
        /// <summary>
        /// n spheres move linearly from c0 to c1 (packed x, y, z world). r = radii, pad = extra conservative inflation per sphere.
        /// Returns the earliest fraction t in [0, 1] at which any sphere touches solid, or -1 if the whole move is free
        /// (0 when a sphere starts overlapping). On contact, <paramref name="o"/> gets the sphere index and the outward surface normal.
        /// </summary>
        double Sweep(double[] c0, double[] c1, double[] r, double[] pad, int n, ref FpvContact o);
        /// <summary>Push-out vector for a sphere overlapping solid; false when free.</summary>
        bool PushOut(double x, double y, double z, double r, out V3d push);
    }

    /// <summary>State layout in one array (same indices as the original, so golden traces compare slot by slot).</summary>
    public static class FpvS
    {
        public const int px = 0, py = 1, pz = 2, vx = 3, vy = 4, vz = 5, qw = 6, qx = 7, qy = 8, qz = 9, wx = 10, wy = 11, wz = 12;
        public const int m0 = 13, m1 = 14, m2 = 15, m3 = 16, iR = 17, iP = 18, iY = 19, dfR = 20, dfP = 21, dfY = 22;
        public const int ffR = 23, ffP = 24, ffY = 25, spR = 26, spP = 27, spY = 28, splR = 29, splP = 30, soc = 31, volt = 32, amps = 33;
        public const int armed = 34, crashed = 35, armSw = 36, mixRange = 37, restT = 38, crashT = 39;
        public const int g1R = 40, g1P = 41, g1Y = 42, g2R = 43, g2P = 44, g2Y = 45, d2R = 46, d2P = 47, d2Y = 48, hold = 49;
        public const int lvR1 = 50, lvR2 = 51, lvR3 = 52, lvP1 = 53, lvP2 = 54, lvP3 = 55, afR1 = 56, afR2 = 57, afR3 = 58, afP1 = 59, afP2 = 60, afP3 = 61;
        public const int hzS = 62, tgR = 63, tgP = 64;
        public const int Size = 65;
    }

    public sealed class FpvSim
    {
        public const double DT = 0.001;
        public const int DtUs = 1000;

        // ---- Betaflight 4.5.1 constants (see the original's comments) ----
        static double PT1(double fc) { double rc = 1 / (2 * Math.PI * fc); return DT / (rc + DT); }
        const double DtermDynMin = 75, DtermDynMax = 150;
        static readonly double KDterm2 = PT1(150);
        static readonly double KGyro1 = PT1(250), KGyro2 = PT1(500), KFf = PT1(30), KRelax = PT1(15);
        const double Pt3CutoffCorrection = 1.961459177;
        static double PT3(double fc) { double rc = 1 / (2 * Math.PI * fc * Pt3CutoffCorrection); return DT / (rc + DT); }
        const double PScale = 0.032029, IScale = 0.244381, DScale = 0.000529;
        const double FScale = 0.013754 * 0.01;
        const double ItermLimit = 400, RelaxThreshold = 40;
        const double Mu = 0.4, Skin = 0.001, PinM = 0.00025;
        const double CrashAngDamp = 3, CrashLinDamp = 0.5;
        static readonly double[] MixR = { -1, -1, 1, 1 }, MixP = { 1, -1, 1, -1 }, MixY = { -1, 1, 1, -1 };

        public readonly FpvParams p;
        public readonly double[] s = new double[FpvS.Size];
        public readonly double[] ch = new double[8];
        public int tick;
        public IFpvWorld world;
        public readonly List<FpvEvent> events = new List<FpvEvent>();
        public int startOverlaps;
        public bool pinnedPass = true;
        /// <summary>Motor output of the last step after mixer and idle, before the motor lag (0..1).</summary>
        public readonly double[] motorCmd = new double[4];
        public double hoverThr;
        /// <summary>Prop-wash intensity of the last step, 0..1.</summary>
        public double washNow;

        readonly double[] c0, c1, rr, pad, thrust = new double[4];
        readonly int nS;
        FpvContact cout;
        V3d push, pinOut;
        readonly double maxR, maxP, kAtt, kAff, kHz, sqrtTwr;

        public FpvSim(FpvParams p, IFpvWorld world)
        {
            this.p = p; this.world = world;
            nS = p.spheres.Length / 4;
            c0 = new double[nS * 3]; c1 = new double[nS * 3]; rr = new double[nS]; pad = new double[nS];
            for (int i = 0; i < nS; i++) rr[i] = p.spheres[i * 4 + 3];
            var rc = p.rates;
            maxR = FpvRates.SetpointRate(rc.type, 1, rc.roll, rc.rateLimit, rc.quickRcExpo);
            maxP = FpvRates.SetpointRate(rc.type, 1, rc.pitch, rc.rateLimit, rc.quickRcExpo);
            var lv = p.level;
            kAtt = p.levelOutputHz > 0 ? PT3(p.levelOutputHz) : 0;
            kAff = lv.ffSmoothMs > 0 ? PT3(1000 / (2 * Math.PI * lv.ffSmoothMs)) : 1;
            kHz = lv.horizonDelayMs > 0 ? DT / (lv.horizonDelayMs / 1000 + DT) : 1;
            sqrtTwr = Math.Sqrt(p.twr);
            hoverThr = FpvCompile.HoverThrOf(p);
            s[FpvS.soc] = 1;   // a new model starts with a fresh pack; Reset keeps the battery
            Reset(0, 0, 0, 0);
            ch[2] = -1; ch[4] = -1;
        }

        public bool Armed => s[FpvS.armed] > 0;
        /// <summary>0 flying, 1 crashed (tumbling), 2 crashed and settled.</summary>
        public int CrashState => (int)s[FpvS.crashed];
        public bool Crashed => s[FpvS.crashed] > 0;

        /// <summary>
        /// Place the craft level and still at (x, y, z) with heading yawDeg (0 = nose along -z, + = turned right). Every state slot goes
        /// back to its start value except the battery, which <paramref name="soc"/> replaces. The craft is parked until a fresh off -> on
        /// edge of the arm switch, unless keepArmed and the arm channel is on.
        /// </summary>
        public void Reset(double x, double y, double z, double yawDeg, double? soc = null, bool keepArmed = false)
        {
            double keep = s[FpvS.soc];
            if (soc.HasValue && !double.IsNaN(soc.Value)) keep = soc.Value < 0 ? 0 : soc.Value > 1 ? 1 : soc.Value;
            Array.Clear(s, 0, s.Length);
            Array.Clear(motorCmd, 0, 4);
            s[FpvS.px] = x; s[FpvS.py] = y; s[FpvS.pz] = z;
            double h = -yawDeg * Math.PI / 180 * 0.5;   // rotation about +y turns left, so a right heading is a negative angle
            s[FpvS.qw] = Math.Cos(h); s[FpvS.qx] = 0; s[FpvS.qy] = Math.Sin(h); s[FpvS.qz] = 0;
            s[FpvS.soc] = keep;
            s[FpvS.volt] = p.cells * FpvCompile.CellVoc(keep);
            s[FpvS.armSw] = 1;   // an off -> on transition is needed to arm, unless kept armed below
            if (keepArmed && ch[4] > 0.5) { s[FpvS.armed] = 1; events.Add(new FpvEvent { type = FpvEventType.Arm, tick = tick }); }
            else s[FpvS.hold] = 1;   // held in place until the first arm
        }

        public void Respawn(double x, double y, double z, double yawDeg, double? soc = null, bool keepArmed = false)
        {
            events.Add(new FpvEvent { type = FpvEventType.Respawn, tick = tick });
            Reset(x, y, z, yawDeg, soc, keepArmed);
        }

        public void SetChannels(double[] c) { for (int i = 0; i < 8; i++) ch[i] = i < c.Length ? (double)(float)c[i] : 0; }

        /// <summary>Overwrite the whole kinematic state (position, attitude, velocity), e.g. after a Cesium origin shift. Body rates stay.</summary>
        public void SetPose(double x, double y, double z, double qw, double qx, double qy, double qz, double vx, double vy, double vz)
        {
            s[FpvS.px] = x; s[FpvS.py] = y; s[FpvS.pz] = z; s[FpvS.qw] = qw; s[FpvS.qx] = qx; s[FpvS.qy] = qy; s[FpvS.qz] = qz; s[FpvS.vx] = vx; s[FpvS.vy] = vy; s[FpvS.vz] = vz;
        }

        static double Fz(double v) => v;   // clarity: channel quantisation happens in SetChannels

        double WashIntensity(double tmax, double ux, double uy, double uz, double rx, double ry, double rz, double bx, double by, double bz)
        {
            double tNow = 0;
            for (int i = 0; i < 4; i++) tNow += tmax * s[FpvS.m0 + i] * s[FpvS.m0 + i];
            double vi = InducedVelocity(tNow, p.rho, p.wakeArea);
            if (!(vi > 0)) return 0;
            double ax = s[FpvS.vx], ay = s[FpvS.vy], az = s[FpvS.vz];
            double up = ux * ax + uy * ay + uz * az, right = rx * ax + ry * ay + rz * az, back = bx * ax + by * ay + bz * az;
            return WakeIntensity(-up, Math.Sqrt(right * right + back * back), vi);
        }

        public void Step()
        {
            var s = this.s; var p = this.p; var ch = this.ch;
            tick++;

            // ---- arming (edge of the arm switch, throttle low, not crashed) ----
            int sw = ch[4] > 0.5 ? 1 : 0;
            double thrStick = (ch[2] + 1) * 0.5;
            if (sw == 1 && s[FpvS.armSw] == 0 && s[FpvS.armed] == 0)
            {
                if (thrStick <= 0.05 && s[FpvS.crashed] == 0)
                {
                    s[FpvS.armed] = 1; s[FpvS.iR] = s[FpvS.iP] = s[FpvS.iY] = 0;
                    events.Add(new FpvEvent { type = FpvEventType.Arm, tick = tick });
                }
            }
            if (sw == 0 && s[FpvS.armed] == 1)
            {
                s[FpvS.armed] = 0;
                events.Add(new FpvEvent { type = FpvEventType.Disarm, tick = tick, reason = "switch" });
            }
            s[FpvS.armSw] = sw;
            if (s[FpvS.hold] > 0)
            {
                if (s[FpvS.armed] > 0) s[FpvS.hold] = 0;
                else return;   // parked at the spawn: no forces until armed
            }

            // ---- body rotation matrix from q ----
            double qw = s[FpvS.qw], qx = s[FpvS.qx], qy = s[FpvS.qy], qz = s[FpvS.qz];
            double r00 = 1 - 2 * (qy * qy + qz * qz), r01 = 2 * (qx * qy - qw * qz), r02 = 2 * (qx * qz + qw * qy);
            double r10 = 2 * (qx * qy + qw * qz), r11 = 1 - 2 * (qx * qx + qz * qz), r12 = 2 * (qy * qz - qw * qx);
            double r20 = 2 * (qx * qz - qw * qy), r21 = 2 * (qy * qz + qw * qx), r22 = 1 - 2 * (qx * qx + qy * qy);

            double wx = s[FpvS.wx], wy = s[FpvS.wy], wz = s[FpvS.wz];
            bool armed = s[FpvS.armed] > 0 && s[FpvS.crashed] == 0;

            // ---- setpoints ----
            var rc = p.rates;
            double acroR = FpvRates.SetpointRate(rc.type, ch[0], rc.roll, rc.rateLimit, rc.quickRcExpo);
            double acroP = FpvRates.SetpointRate(rc.type, ch[1], rc.pitch, rc.rateLimit, rc.quickRcExpo);
            double spY = FpvRates.SetpointRate(rc.type, ch[3], rc.yaw, rc.rateLimit, rc.quickRcExpo);
            var mode = FpvModes.FromChannel(ch[5]);
            double spR = acroR, spP = acroP;
            if (mode == FlightMode.Acro) LevelIdle(acroR, acroP);
            else
            {
                var lv = p.level;
                double rollDeg = Math.Atan2(-r10, r11) * (180 / Math.PI);   // right side below the horizon = roll right
                double pitchDeg = Math.Atan2(r12, Math.Sqrt(r02 * r02 + r22 * r22)) * (180 / Math.PI);   // nose down +
                double lvR = LevelRate(0, acroR, rollDeg, lv);
                double lvP = LevelRate(1, acroP, pitchDeg, lv);
                if (mode == FlightMode.Angle) { spR = lvR; spP = lvP; s[FpvS.hzS] = 0; }
                else
                {
                    double hs = HorizonShare(rollDeg, pitchDeg, lv);
                    spR = acroR * (1 - hs) + lvR * hs;
                    spP = acroP * (1 - hs) + lvP * hs;
                }
            }

            double thr = p.gravityMode == GravityMode.AutoThrottle
                ? AutoThrottle(thrStick, (hoverThr - p.idle) / (1 - p.idle))
                : FpvRates.Throttle(thrStick, p.throttle);

            // ---- rate PID in Betaflight units ----
            const double RAD2DEG = 180 / Math.PI;
            double gR = GyroFilter(0, -wz * RAD2DEG), gP = GyroFilter(1, -wx * RAD2DEG), gY = GyroFilter(2, -wy * RAD2DEG);
            double dynCi = ItermGain(s[FpvS.mixRange], p.itermWindup);
            double fcD = DtermDynMin + (DtermDynMax - DtermDynMin) * (thr < 0 ? 0 : thr > 1 ? 1 : thr);
            double kD1 = PT1(fcD);
            bool ffRP = mode != FlightMode.Angle;
            double pidR = AxisPid(0, spR, acroR, ffRP, gR, p.pid[0], dynCi, armed, 500, kD1);
            double pidP = AxisPid(1, spP, acroP, ffRP, gP, p.pid[1], dynCi, armed, 500, kD1);
            double pidY = AxisPid(2, spY, spY, true, gY, p.pid[2], dynCi, armed, 400, kD1);

            // ---- mixer with airmode ----
            var o = motorCmd;
            if (armed)
            {
                double mn = double.PositiveInfinity, mx = double.NegativeInfinity;
                for (int i = 0; i < 4; i++)
                {
                    double m = pidR * MixR[i] + pidP * MixP[i] + pidY * MixY[i];
                    o[i] = m;
                    if (m < mn) mn = m;
                    if (m > mx) mx = m;
                }
                double range = mx - mn;
                double t = thr;
                if (range > 1)
                {
                    double inv = 1 / range;
                    for (int i = 0; i < 4; i++) o[i] *= inv;
                    mn *= inv; mx *= inv;
                }
                t = t < -mn ? -mn : t > 1 - mx ? 1 - mx : t;
                s[FpvS.mixRange] = range;
                for (int i = 0; i < 4; i++)
                {
                    double v = o[i] + t;
                    v = v < 0 ? 0 : v > 1 ? 1 : v;
                    o[i] = p.idle + (1 - p.idle) * v;
                }
            }
            else { for (int i = 0; i < 4; i++) o[i] = 0; s[FpvS.mixRange] = 0; }

            // ---- motors ----
            double volt = s[FpvS.volt];
            double vr = volt / p.vNom;
            double tmax = p.tmaxMotorNom * vr * vr;
            var th = thrust;
            double tSum = 0, wSum = 0, tauX = 0, tauY = 0, tauZ = 0, power = 0;
            var mp = p.motorPos;
            double a = p.tau > 0 ? DT / p.tau : 1;
            double aa = a > 1 ? 1 : a;
            double jr = p.rotorInertia * p.omegaMaxPerVolt * volt / DT;
            double keep = 1, wash = 0;
            if (p.wakeLoss > 0 || p.wakeShake > 0)
            {
                wash = WashIntensity(tmax, r01, r11, r21, r00, r10, r20, r02, r12, r22);
                keep = 1 - p.wakeLoss * wash;
            }
            washNow = wash;
            for (int i = 0; i < 4; i++)
            {
                int k = FpvS.m0 + i;
                double dw = (o[i] - s[k]) * aa;
                s[k] += dw;
                double w = s[k];
                double T0 = tmax * w * w;
                double T = T0 * keep;
                th[i] = T; tSum += T; wSum += w;
                tauX += -mp[i * 3 + 2] * T;
                tauZ += mp[i * 3] * T;
                tauY += p.motorYaw[i] * (p.kappa * T + jr * dw);
                power += p.kappa * T0 * (w * p.omegaMaxPerVolt * volt);
            }
            if (wash > 0 && p.wakeShake > 0)
            {
                double amp = p.wakeShake * wash * (tSum * 0.25) * Math.Sqrt(mp[0] * mp[0] + mp[2] * mp[2]);
                tauZ += amp * WakeNoise(0, tick);
                tauX += amp * WakeNoise(1, tick);
            }

            // ---- battery ----
            double amps = volt > 0.1 ? power / (p.eta * volt) : 0;
            double soc = s[FpvS.soc] - amps * DT / p.capacityAs;
            if (soc < 0) soc = 0;
            s[FpvS.soc] = soc; s[FpvS.amps] = amps;
            s[FpvS.volt] = p.cells * FpvCompile.CellVoc(soc) - amps * p.rPack;

            // ---- forces (world); still air: airspeed = velocity ----
            double vx = s[FpvS.vx], vy = s[FpvS.vy], vz = s[FpvS.vz];
            double bvx = r00 * vx + r10 * vy + r20 * vz, bvy = r01 * vx + r11 * vy + r21 * vz, bvz = r02 * vx + r12 * vy + r22 * vz;
            double speed = Math.Sqrt(vx * vx + vy * vy + vz * vz);
            double kd = -0.5 * p.rho * speed;
            double kLin = p.ductDrag * p.mass * wSum * vr * sqrtTwr * 0.25;
            double dbx = kd * p.cda[0] * bvx - kLin * bvx;
            double dby = kd * p.cda[1] * bvy + tSum;
            double dbz = kd * p.cda[2] * bvz - kLin * bvz;
            double fx = r00 * dbx + r01 * dby + r02 * dbz;
            double fy = r10 * dbx + r11 * dby + r12 * dbz - p.mass * p.gravity;
            double fz = r20 * dbx + r21 * dby + r22 * dbz;

            // ---- integrate (semi-implicit Euler) ----
            double px0 = s[FpvS.px], py0 = s[FpvS.py], pz0 = s[FpvS.pz];
            double q0w = qw, q0x = qx, q0y = qy, q0z = qz;
            double invM = 1 / p.mass;
            double nvx = vx + fx * invM * DT, nvy = vy + fy * invM * DT, nvz = vz + fz * invM * DT;
            s[FpvS.vx] = nvx; s[FpvS.vy] = nvy; s[FpvS.vz] = nvz;
            s[FpvS.px] = px0 + nvx * DT; s[FpvS.py] = py0 + nvy * DT; s[FpvS.pz] = pz0 + nvz * DT;

            double I0 = p.inertia[0], I1 = p.inertia[1], I2 = p.inertia[2];
            double lbx = I0 * wx, lby = I1 * wy, lbz = I2 * wz;
            double lwx = r00 * lbx + r01 * lby + r02 * lbz + (r00 * tauX + r01 * tauY + r02 * tauZ) * DT;
            double lwy = r10 * lbx + r11 * lby + r12 * lbz + (r10 * tauX + r11 * tauY + r12 * tauZ) * DT;
            double lwz = r20 * lbx + r21 * lby + r22 * lbz + (r20 * tauX + r21 * tauY + r22 * tauZ) * DT;
            double pwx = (r00 * lwx + r10 * lwy + r20 * lwz) / I0;
            double pwy = (r01 * lwx + r11 * lwy + r21 * lwz) / I1;
            double pwz = (r02 * lwx + r12 * lwy + r22 * lwz) / I2;
            IntegrateQuat(pwx, pwy, pwz);
            double aw = s[FpvS.qw], ax = s[FpvS.qx], ay = s[FpvS.qy], az = s[FpvS.qz];
            double n00 = 1 - 2 * (ay * ay + az * az), n01 = 2 * (ax * ay - aw * az), n02 = 2 * (ax * az + aw * ay);
            double n10 = 2 * (ax * ay + aw * az), n11 = 1 - 2 * (ax * ax + az * az), n12 = 2 * (ay * az - aw * ax);
            double n20 = 2 * (ax * az - aw * ay), n21 = 2 * (ay * az + aw * ax), n22 = 1 - 2 * (ax * ax + ay * ay);
            s[FpvS.wx] = (n00 * lwx + n10 * lwy + n20 * lwz) / I0;
            s[FpvS.wy] = (n01 * lwx + n11 * lwy + n21 * lwz) / I1;
            s[FpvS.wz] = (n02 * lwx + n12 * lwy + n22 * lwz) / I2;

            // ---- contact ----
            if (world != null) Contact(world, px0, py0, pz0, q0w, q0x, q0y, q0z);

            // crashed body settling: air and scraping losses, rest detection and 4 s cap
            if (s[FpvS.crashed] > 0)
            {
                s[FpvS.crashT] += DT;
                double kw = 1 - CrashAngDamp * DT, kv = 1 - CrashLinDamp * DT;
                s[FpvS.wx] *= kw; s[FpvS.wy] *= kw; s[FpvS.wz] *= kw;
                s[FpvS.vx] *= kv; s[FpvS.vz] *= kv;
                double v2 = s[FpvS.vx] * s[FpvS.vx] + s[FpvS.vy] * s[FpvS.vy] + s[FpvS.vz] * s[FpvS.vz];
                double w2 = s[FpvS.wx] * s[FpvS.wx] + s[FpvS.wy] * s[FpvS.wy] + s[FpvS.wz] * s[FpvS.wz];
                if (v2 < 0.0025 && w2 < 0.25) s[FpvS.restT] += DT; else s[FpvS.restT] = 0;
                if (s[FpvS.restT] >= 0.5 || s[FpvS.crashT] >= 4)
                {
                    if (s[FpvS.crashed] == 1) { s[FpvS.crashed] = 2; events.Add(new FpvEvent { type = FpvEventType.Rest, tick = tick }); }
                    s[FpvS.vx] = s[FpvS.vy] = s[FpvS.vz] = 0;
                    s[FpvS.wx] = s[FpvS.wy] = s[FpvS.wz] = 0;
                    s[FpvS.px] = px0; s[FpvS.py] = py0; s[FpvS.pz] = pz0;
                    s[FpvS.qw] = q0w; s[FpvS.qx] = q0x; s[FpvS.qy] = q0y; s[FpvS.qz] = q0z;
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ control loops

        /// <summary>I-term gain from the last motor-mix range (Betaflight 4.5 dynCi): full below windup, fading to 0 at a saturated mix.</summary>
        public static double ItermGain(double mixRange, double windup)
        {
            if (windup >= 1) return mixRange > 1 ? 0 : 1;
            double g = (1 - mixRange) / (1 - windup);
            return g < 0 ? 0 : g > 1 ? 1 : g;
        }

        static double AutoThrottle(double u, double hover)
        {
            double h = hover < 0.02 ? 0.02 : hover > 0.98 ? 0.98 : hover;
            return u <= 0.5 ? u / 0.5 * h : h + (u - 0.5) / 0.5 * (1 - h);
        }

        double Pt3(int k, double x, double g)
        {
            s[k] += (x - s[k]) * g;
            s[k + 1] += (s[k] - s[k + 1]) * g;
            s[k + 2] += (s[k + 1] - s[k + 2]) * g;
            return s[k + 2];
        }

        double LevelRate(int ax, double acro, double curDeg, LevelParams lv)
        {
            double max = ax == 0 ? maxR : maxP;
            double target = max > 0 ? lv.limitDeg * acro / max : 0;
            int tK = FpvS.tgR + ax;
            double ffRaw = lv.ffGain * (target - s[tK]) / DT;
            s[tK] = target;
            double ff = Pt3(FpvS.afR1 + ax * 3, ffRaw, kAff);
            double rate = (target - curDeg) * lv.gain / 10 + ff;
            int k = FpvS.lvR1 + ax * 3;
            if (kAtt > 0) return Pt3(k, rate, kAtt);
            s[k] = s[k + 1] = s[k + 2] = rate;
            return rate;
        }

        void LevelIdle(double acroR, double acroP)
        {
            s[FpvS.lvR1] = s[FpvS.lvR2] = s[FpvS.lvR3] = acroR;
            s[FpvS.lvP1] = s[FpvS.lvP2] = s[FpvS.lvP3] = acroP;
            s[FpvS.afR1] = s[FpvS.afR2] = s[FpvS.afR3] = 0;
            s[FpvS.afP1] = s[FpvS.afP2] = s[FpvS.afP3] = 0;
            double lim = p.level.limitDeg;
            s[FpvS.tgR] = maxR > 0 ? lim * acroR / maxR : 0;
            s[FpvS.tgP] = maxP > 0 ? lim * acroP / maxP : 0;
            s[FpvS.hzS] = 0;
        }

        double HorizonShare(double rollDeg, double pitchDeg, LevelParams lv)
        {
            double incl = Math.Max(rollDeg < 0 ? -rollDeg : rollDeg, pitchDeg < 0 ? -pitchDeg : pitchDeg);
            double a0 = ch[0] < 0 ? -ch[0] : ch[0], a1 = ch[1] < 0 ? -ch[1] : ch[1];
            double stick = Math.Min(1, Math.Max(a0, a1));
            double fa = lv.horizonLimitDeg > 0 ? Math.Max((lv.horizonLimitDeg - incl) / lv.horizonLimitDeg, 0) : 0;
            double fs = lv.horizonLimitSticks > 0 ? Math.Max((lv.horizonLimitSticks - stick) / lv.horizonLimitSticks, 0) : 0;
            double h = fa * fs * Math.Min(lv.horizonStrength / 100, 1);
            s[FpvS.hzS] += (h - s[FpvS.hzS]) * kHz;
            if (s[FpvS.hzS] < h) h = s[FpvS.hzS];
            return h;
        }

        double GyroFilter(int ax, double raw)
        {
            int a = FpvS.g1R + ax, b = FpvS.g2R + ax;
            s[a] += (raw - s[a]) * KGyro1;
            s[b] += (s[a] - s[b]) * KGyro2;
            return s[b];
        }

        double AxisPid(int ax, double sp, double ffIn, bool ffOn, double gyro, double[] g, double dynCi, bool armed, double limit, double kD1)
        {
            int iK = FpvS.iR + ax, dK = FpvS.dfR + ax, fK = FpvS.ffR + ax, spK = FpvS.spR + ax;
            double e = sp - gyro;
            double d1 = s[dK] + (gyro - s[dK]) * kD1;
            s[dK] = d1;
            int d2K = FpvS.d2R + ax;
            double prevD = s[d2K];
            double dNow = prevD + (d1 - prevD) * KDterm2;
            s[d2K] = dNow;
            double dTerm = -g[2] * DScale * ((dNow - prevD) / DT);
            double spDelta = (ffIn - s[spK]) / DT;
            s[spK] = ffIn;
            double ff = 0;
            if (ffOn) { double ffPrev = s[fK]; ff = ffPrev + (g[3] * FScale * spDelta - ffPrev) * KFf; }
            s[fK] = ff;
            double iErr = e;
            if (ax < 2)
            {
                int lK = FpvS.splR + ax;
                double lpf = s[lK] + (sp - s[lK]) * KRelax;
                s[lK] = lpf;
                double hp = sp - lpf;
                double relax = 1 - (hp < 0 ? -hp : hp) / RelaxThreshold;
                iErr = e * (relax > 0 ? relax : 0);
            }
            if (armed && dynCi > 0)
            {
                double I = s[iK] + g[1] * IScale * dynCi * iErr * DT;
                I = I < -ItermLimit ? -ItermLimit : I > ItermLimit ? ItermLimit : I;
                s[iK] = I;
            }
            if (!armed) s[iK] = 0;
            double sum = g[0] * PScale * e + s[iK] + dTerm + ff;
            sum = sum < -limit ? -limit : sum > limit ? limit : sum;
            return sum / 1000;
        }

        // ------------------------------------------------------------------------------------------------ contact

        void Contact(IFpvWorld world, double px0, double py0, double pz0, double q0w, double q0x, double q0y, double q0z)
        {
            var s = this.s; var p = this.p;
            int n = nS; var sp = p.spheres;
            SpherePoses(sp, n, px0, py0, pz0, q0w, q0x, q0y, q0z, c0);
            SpherePoses(sp, n, s[FpvS.px], s[FpvS.py], s[FpvS.pz], s[FpvS.qw], s[FpvS.qx], s[FpvS.qy], s[FpvS.qz], c1);
            double wmag = Math.Sqrt(s[FpvS.wx] * s[FpvS.wx] + s[FpvS.wy] * s[FpvS.wy] + s[FpvS.wz] * s[FpvS.wz]);
            double dth = wmag * DT;
            for (int i = 0; i < n; i++)
            {
                double lx = sp[i * 4], ly = sp[i * 4 + 1], lz = sp[i * 4 + 2];
                double d = Math.Sqrt(lx * lx + ly * ly + lz * lz);
                pad[i] = d * dth * dth / 8 + 1e-9;
            }
            double t = world.Sweep(c0, c1, rr, pad, n, ref cout);
            if (t < 0) return;

            int k = cout.sphere;
            bool pinned = false;
            if (t == 0)
            {
                // started overlapping: stay at the start pose and push every overlapping sphere out
                startOverlaps++;
                s[FpvS.px] = px0; s[FpvS.py] = py0; s[FpvS.pz] = pz0;
                s[FpvS.qw] = q0w; s[FpvS.qx] = q0x; s[FpvS.qy] = q0y; s[FpvS.qz] = q0z;
                for (int it = 0; it < 3; it++)
                {
                    bool moved = false;
                    for (int i = 0; i < n; i++)
                    {
                        double cx = c0[i * 3] + (s[FpvS.px] - px0), cy = c0[i * 3 + 1] + (s[FpvS.py] - py0), cz = c0[i * 3 + 2] + (s[FpvS.pz] - pz0);
                        if (world.PushOut(cx, cy, cz, rr[i] + Skin, out push))
                        {
                            double ppx = push.x, ppy = push.y, ppz = push.z;
                            s[FpvS.px] += ppx; s[FpvS.py] += ppy; s[FpvS.pz] += ppz;
                            double pl = Math.Sqrt(ppx * ppx + ppy * ppy + ppz * ppz);
                            if (pl > 1e-12)
                            {
                                double ux = ppx / pl, uy = ppy / pl, uz = ppz / pl;
                                double vn0 = s[FpvS.vx] * ux + s[FpvS.vy] * uy + s[FpvS.vz] * uz;
                                if (vn0 < 0) { s[FpvS.vx] -= vn0 * ux; s[FpvS.vy] -= vn0 * uy; s[FpvS.vz] -= vn0 * uz; }
                            }
                            moved = true;
                        }
                    }
                    if (!moved) break;
                }
            }
            else
            {
                double mx = c1[k * 3] - c0[k * 3], my = c1[k * 3 + 1] - c0[k * 3 + 1], mz = c1[k * 3 + 2] - c0[k * 3 + 2];
                pinned = t * Math.Sqrt(mx * mx + my * my + mz * mz) <= PinM;
                s[FpvS.px] = px0 + (s[FpvS.px] - px0) * t;
                s[FpvS.py] = py0 + (s[FpvS.py] - py0) * t;
                s[FpvS.pz] = pz0 + (s[FpvS.pz] - pz0) * t;
                double bw = s[FpvS.qw], bx = s[FpvS.qx], by = s[FpvS.qy], bz = s[FpvS.qz];
                if (q0w * bw + q0x * bx + q0y * by + q0z * bz < 0) { bw = -bw; bx = -bx; by = -by; bz = -bz; }
                double iw = q0w + (bw - q0w) * t, ix = q0x + (bx - q0x) * t, iy = q0y + (by - q0y) * t, iz = q0z + (bz - q0z) * t;
                double il = 1 / Math.Sqrt(iw * iw + ix * ix + iy * iy + iz * iz);
                iw *= il; ix *= il; iy *= il; iz *= il;
                s[FpvS.qw] = iw; s[FpvS.qx] = ix; s[FpvS.qy] = iy; s[FpvS.qz] = iz;
                // keep a small skin so tangential motion along the surface is not reported as contact
                double sx = cout.nx * Skin, sy = cout.ny * Skin, sz = cout.nz * Skin;
                bool clear = true;
                for (int i = 0; i < n && clear; i++)
                {
                    double cx = c0[i * 3] + (c1[i * 3] - c0[i * 3]) * t + sx;
                    double cy = c0[i * 3 + 1] + (c1[i * 3 + 1] - c0[i * 3 + 1]) * t + sy;
                    double cz = c0[i * 3 + 2] + (c1[i * 3 + 2] - c0[i * 3 + 2]) * t + sz;
                    if (world.PushOut(cx, cy, cz, rr[i], out push)) clear = false;
                }
                if (clear) { s[FpvS.px] += sx; s[FpvS.py] += sy; s[FpvS.pz] += sz; }
            }

            double nx = cout.nx, ny = cout.ny, nz = cout.nz;
            double qw = s[FpvS.qw], qx = s[FpvS.qx], qy = s[FpvS.qy], qz = s[FpvS.qz];
            double lx2 = sp[k * 4], ly2 = sp[k * 4 + 1], lz2 = sp[k * 4 + 2], rk = sp[k * 4 + 3];
            double r00 = 1 - 2 * (qy * qy + qz * qz), r01 = 2 * (qx * qy - qw * qz), r02 = 2 * (qx * qz + qw * qy);
            double r10 = 2 * (qx * qy + qw * qz), r11 = 1 - 2 * (qx * qx + qz * qz), r12 = 2 * (qy * qz - qw * qx);
            double r20 = 2 * (qx * qz - qw * qy), r21 = 2 * (qy * qz + qw * qx), r22 = 1 - 2 * (qx * qx + qy * qy);
            double ox = r00 * lx2 + r01 * ly2 + r02 * lz2 - nx * rk;
            double oy = r10 * lx2 + r11 * ly2 + r12 * lz2 - ny * rk;
            double oz = r20 * lx2 + r21 * ly2 + r22 * lz2 - nz * rk;
            double bwx = s[FpvS.wx], bwy = s[FpvS.wy], bwz = s[FpvS.wz];
            double wwx = r00 * bwx + r01 * bwy + r02 * bwz, wwy = r10 * bwx + r11 * bwy + r12 * bwz, wwz = r20 * bwx + r21 * bwy + r22 * bwz;
            double cvx = s[FpvS.vx] + (wwy * oz - wwz * oy), cvy = s[FpvS.vy] + (wwz * ox - wwx * oz), cvz = s[FpvS.vz] + (wwx * oy - wwy * ox);
            double vn = cvx * nx + cvy * ny + cvz * nz;   // < 0 approaching
            double approach = -vn;

            Impact(approach, nx, ny, nz, ox, oy, oz);
            if (approach > 0) Impulse(approach, vn, cvx, cvy, cvz, nx, ny, nz, ox, oy, oz, r00, r01, r02, r10, r11, r12, r20, r21, r22);
            if (pinned && pinnedPass) PinnedContacts(world, k, r00, r01, r02, r10, r11, r12, r20, r21, r22);
        }

        /// <summary>A contact point approaching a surface at <paramref name="approach"/> m/s: a crash, or a contact event.</summary>
        void Impact(double approach, double nx, double ny, double nz, double ox, double oy, double oz)
        {
            bool flying = s[FpvS.crashed] == 0;
            if (flying && p.crashOn && approach >= p.vCrash)
            {
                s[FpvS.crashed] = 1; s[FpvS.crashT] = 0; s[FpvS.restT] = 0;
                if (s[FpvS.armed] > 0)
                {
                    s[FpvS.armed] = 0;
                    events.Add(new FpvEvent { type = FpvEventType.Disarm, tick = tick, reason = "crash" });
                }
                for (int i = 0; i < 4; i++) s[FpvS.m0 + i] = 0;
                events.Add(new FpvEvent { type = FpvEventType.Crash, tick = tick, speed = approach, nx = nx, ny = ny, nz = nz, px = s[FpvS.px] + ox, py = s[FpvS.py] + oy, pz = s[FpvS.pz] + oz });
            }
            else if (flying && approach > 0.05)
                events.Add(new FpvEvent { type = FpvEventType.Contact, tick = tick, speed = approach, bounce = approach >= p.vBounce, nx = nx, ny = ny, nz = nz });
        }

        /// <summary>Impulse at contact offset o along n with speed-dependent restitution and Coulomb friction.</summary>
        void Impulse(double approach, double vn, double cvx, double cvy, double cvz, double nx, double ny, double nz, double ox, double oy, double oz,
            double r00, double r01, double r02, double r10, double r11, double r12, double r20, double r21, double r22)
        {
            double e;
            if (s[FpvS.crashed] > 0) e = 0.3 / (1 + approach * 0.5);
            else if (approach < p.vBounce) e = 0.1;
            else e = 0.35 / (1 + (approach - p.vBounce) * 0.5);

            double I0 = p.inertia[0], I1 = p.inertia[1], I2 = p.inertia[2];
            double effN = InvEff(nx, ny, nz, ox, oy, oz, r00, r01, r02, r10, r11, r12, r20, r21, r22, I0, I1, I2);
            double jn = (1 + e) * approach / (1 / p.mass + effN);
            double ix = nx * jn, iy = ny * jn, iz = nz * jn;
            double tvx = cvx - vn * nx, tvy = cvy - vn * ny, tvz = cvz - vn * nz;
            double tv = Math.Sqrt(tvx * tvx + tvy * tvy + tvz * tvz);
            if (tv > 1e-9)
            {
                double tx = tvx / tv, ty = tvy / tv, tz = tvz / tv;
                double effT = InvEff(tx, ty, tz, ox, oy, oz, r00, r01, r02, r10, r11, r12, r20, r21, r22, I0, I1, I2);
                double jt = tv / (1 / p.mass + effT);
                if (jt > Mu * jn) jt = Mu * jn;
                ix -= tx * jt; iy -= ty * jt; iz -= tz * jt;
            }
            ApplyImpulse(s, p.mass, ix, iy, iz, ox, oy, oz, r00, r01, r02, r10, r11, r12, r20, r21, r22, I0, I1, I2);
        }

        /// <summary>
        /// The first contact pinned the body (it stopped it before it advanced PinM), so the whole tick's motion was undone but only that one
        /// sphere got an impulse. Every other sphere touching a surface at this pose and moving into it is an impact too.
        /// </summary>
        void PinnedContacts(IFpvWorld world, int first, double r00, double r01, double r02, double r10, double r11, double r12, double r20, double r21, double r22)
        {
            var sp = p.spheres;
            for (int i = 0; i < nS; i++)
            {
                if (i == first) continue;
                double lx = sp[i * 4], ly = sp[i * 4 + 1], lz = sp[i * 4 + 2], ri = rr[i];
                double cx = r00 * lx + r01 * ly + r02 * lz, cy = r10 * lx + r11 * ly + r12 * lz, cz = r20 * lx + r21 * ly + r22 * lz;
                if (!world.PushOut(s[FpvS.px] + cx, s[FpvS.py] + cy, s[FpvS.pz] + cz, ri + Skin, out pinOut)) continue;
                double ul = Math.Sqrt(pinOut.x * pinOut.x + pinOut.y * pinOut.y + pinOut.z * pinOut.z);
                if (ul <= 1e-12) continue;
                double nx = pinOut.x / ul, ny = pinOut.y / ul, nz = pinOut.z / ul;
                double ox = cx - nx * ri, oy = cy - ny * ri, oz = cz - nz * ri;
                PointVelocity(ox, oy, oz, r00, r01, r02, r10, r11, r12, r20, r21, r22, out push);
                double cvx = push.x, cvy = push.y, cvz = push.z;
                double vn = cvx * nx + cvy * ny + cvz * nz;
                if (vn >= 0) continue;
                Impact(-vn, nx, ny, nz, ox, oy, oz);
                Impulse(-vn, vn, cvx, cvy, cvz, nx, ny, nz, ox, oy, oz, r00, r01, r02, r10, r11, r12, r20, r21, r22);
            }
        }

        void PointVelocity(double ox, double oy, double oz, double r00, double r01, double r02, double r10, double r11, double r12, double r20, double r21, double r22, out V3d o)
        {
            double bwx = s[FpvS.wx], bwy = s[FpvS.wy], bwz = s[FpvS.wz];
            double wwx = r00 * bwx + r01 * bwy + r02 * bwz, wwy = r10 * bwx + r11 * bwy + r12 * bwz, wwz = r20 * bwx + r21 * bwy + r22 * bwz;
            o.x = s[FpvS.vx] + (wwy * oz - wwz * oy);
            o.y = s[FpvS.vy] + (wwz * ox - wwx * oz);
            o.z = s[FpvS.vz] + (wwx * oy - wwy * ox);
        }

        static double InvEff(double nx, double ny, double nz, double ox, double oy, double oz,
            double r00, double r01, double r02, double r10, double r11, double r12, double r20, double r21, double r22, double I0, double I1, double I2)
        {
            double cx = oy * nz - oz * ny, cy = oz * nx - ox * nz, cz = ox * ny - oy * nx;
            double bx = (r00 * cx + r10 * cy + r20 * cz) / I0;
            double by = (r01 * cx + r11 * cy + r21 * cz) / I1;
            double bz = (r02 * cx + r12 * cy + r22 * cz) / I2;
            double wx = r00 * bx + r01 * by + r02 * bz, wy = r10 * bx + r11 * by + r12 * bz, wz = r20 * bx + r21 * by + r22 * bz;
            double kx = wy * oz - wz * oy, ky = wz * ox - wx * oz, kz = wx * oy - wy * ox;
            return kx * nx + ky * ny + kz * nz;
        }

        static void ApplyImpulse(double[] s, double m, double ix, double iy, double iz, double ox, double oy, double oz,
            double r00, double r01, double r02, double r10, double r11, double r12, double r20, double r21, double r22, double I0, double I1, double I2)
        {
            s[FpvS.vx] += ix / m; s[FpvS.vy] += iy / m; s[FpvS.vz] += iz / m;
            double tx = oy * iz - oz * iy, ty = oz * ix - ox * iz, tz = ox * iy - oy * ix;
            s[FpvS.wx] += (r00 * tx + r10 * ty + r20 * tz) / I0;
            s[FpvS.wy] += (r01 * tx + r11 * ty + r21 * tz) / I1;
            s[FpvS.wz] += (r02 * tx + r12 * ty + r22 * tz) / I2;
        }

        /// <summary>q &lt;- q * exp(w dt / 2), renormalised.</summary>
        void IntegrateQuat(double wx, double wy, double wz)
        {
            double wm = Math.Sqrt(wx * wx + wy * wy + wz * wz);
            double half = wm * DT * 0.5;
            double dw = 1, dx = 0, dy = 0, dz = 0;
            if (wm > 1e-12) { double sn = Math.Sin(half) / wm; dw = Math.Cos(half); dx = wx * sn; dy = wy * sn; dz = wz * sn; }
            double qw = s[FpvS.qw], qx = s[FpvS.qx], qy = s[FpvS.qy], qz = s[FpvS.qz];
            double nw = qw * dw - qx * dx - qy * dy - qz * dz;
            double nx = qw * dx + qx * dw + qy * dz - qz * dy;
            double ny = qw * dy - qx * dz + qy * dw + qz * dx;
            double nz = qw * dz + qx * dy - qy * dx + qz * dw;
            double inv = 1 / Math.Sqrt(nw * nw + nx * nx + ny * ny + nz * nz);
            s[FpvS.qw] = nw * inv; s[FpvS.qx] = nx * inv; s[FpvS.qy] = ny * inv; s[FpvS.qz] = nz * inv;
        }

        /// <summary>World centres of the body spheres for pose (p, q).</summary>
        public static void SpherePoses(double[] sp, int n, double px, double py, double pz, double qw, double qx, double qy, double qz, double[] o)
        {
            double r00 = 1 - 2 * (qy * qy + qz * qz), r01 = 2 * (qx * qy - qw * qz), r02 = 2 * (qx * qz + qw * qy);
            double r10 = 2 * (qx * qy + qw * qz), r11 = 1 - 2 * (qx * qx + qz * qz), r12 = 2 * (qy * qz - qw * qx);
            double r20 = 2 * (qx * qz - qw * qy), r21 = 2 * (qy * qz + qw * qx), r22 = 1 - 2 * (qx * qx + qy * qy);
            for (int i = 0; i < n; i++)
            {
                double lx = sp[i * 4], ly = sp[i * 4 + 1], lz = sp[i * 4 + 2];
                o[i * 3] = px + r00 * lx + r01 * ly + r02 * lz;
                o[i * 3 + 1] = py + r10 * lx + r11 * ly + r12 * lz;
                o[i * 3 + 2] = pz + r20 * lx + r21 * ly + r22 * lz;
            }
        }

        /// <summary>Attitude for the OSD (degrees): roll right +, pitch nose down +, yaw heading right + (0 = -z).</summary>
        public static void Attitude(double[] s, out double roll, out double pitch, out double yaw)
        {
            double qw = s[FpvS.qw], qx = s[FpvS.qx], qy = s[FpvS.qy], qz = s[FpvS.qz];
            double r10 = 2 * (qx * qy + qw * qz), r11 = 1 - 2 * (qx * qx + qz * qz);
            double r02 = 2 * (qx * qz + qw * qy), r12 = 2 * (qy * qz - qw * qx), r22 = 1 - 2 * (qx * qx + qy * qy);
            yaw = Math.Atan2(-r02, r22) * (180 / Math.PI);
            pitch = Math.Atan2(r12, Math.Sqrt(r02 * r02 + r22 * r22)) * (180 / Math.PI);
            roll = Math.Atan2(-r10, r11) * (180 / Math.PI);
        }

        // ------------------------------------------------------------------------------------------------ prop wash (wake.ts)

        public const double WakeLo = 0.25, WakeHi = 2.0;
        const int WakeNoiseTicks = 25;
        const uint WakeSeed = 0x51a57e11u;

        public static double InducedVelocity(double thrust, double rho, double area)
            => !(thrust > 0) || !(area > 0) ? 0 : Math.Sqrt(thrust / (2 * rho * area));

        public static double WakeIntensity(double down, double side, double vi)
        {
            if (!(vi > 0) || !(down > 0)) return 0;
            double x = down / vi;
            if (x <= WakeLo || x >= WakeHi) return 0;
            double t = (x - WakeLo) / (WakeHi - WakeLo);
            double b = 4 * t * (1 - t);
            double mu = side / vi;
            return b * b / (1 + 4 * mu * mu);
        }

        static uint Mix32(uint x)
        {
            unchecked
            {
                x ^= x >> 16; x *= 0x85ebca6bu; x ^= x >> 13; x *= 0xc2b2ae35u; x ^= x >> 16;
                return x;
            }
        }

        static double Knot(uint seed, int stream, int k)
        {
            unchecked
            {
                uint a = Mix32(seed ^ ((uint)(stream + 1) * 0x9e3779b9u));
                uint b = Mix32(a ^ ((uint)k * 0x27d4eb2fu));
                return b * (2.0 / 4294967295.0) - 1;
            }
        }

        public static double WakeNoise(int axis, int tick)
        {
            int k = (int)Math.Floor((double)tick / WakeNoiseTicks);
            double f = (tick - k * WakeNoiseTicks) / (double)WakeNoiseTicks;
            double p0 = Knot(WakeSeed, axis, k - 1), p1 = Knot(WakeSeed, axis, k), p2 = Knot(WakeSeed, axis, k + 1), p3 = Knot(WakeSeed, axis, k + 2);
            return p1 + 0.5 * f * (p2 - p0 + f * (2 * p0 - 5 * p1 + 4 * p2 - p3 + f * (3 * (p1 - p2) + p3 - p0)));
        }
    }
}
