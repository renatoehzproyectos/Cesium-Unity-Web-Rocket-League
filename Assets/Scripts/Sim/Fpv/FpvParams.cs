// Compile a drone preset into the flat numbers the flight model uses.
// C# port of packages/sim-core/src/params.ts (+ contracts.ts level defaults) from ShramkoGSFPV (MIT, Andrii Shramko).
// Left out on purpose: the wind field (RocketWorld has none: still air), payload changes, the "slide" bump of lesson worlds.
using System;

namespace RocketWorld.Sim.Fpv
{
    public enum FlightMode { Acro, Angle, Horizon }
    public enum GravityMode { Honest, SameTwr, AutoThrottle }

    /// <summary>Channel values the page writes to ch[5] for each flight mode (acro -1, horizon 0, angle +1).</summary>
    public static class FpvModes
    {
        public static double Channel(FlightMode m) => m == FlightMode.Acro ? -1 : m == FlightMode.Horizon ? 0 : 1;
        public static FlightMode FromChannel(double v) => v > 0.5 ? FlightMode.Angle : v > -0.5 ? FlightMode.Horizon : FlightMode.Acro;
        public static string Name(FlightMode m) => m == FlightMode.Acro ? "ACRO" : m == FlightMode.Horizon ? "HORIZON" : "ANGLE";
    }

    /// <summary>Betaflight 4.5.1 self-level tuning (angle / horizon).</summary>
    public struct LevelParams
    {
        public double limitDeg, gain, ffGain, ffSmoothMs, horizonStrength, horizonLimitSticks, horizonLimitDeg, horizonDelayMs;
        public static LevelParams Defaults => new LevelParams
        {
            limitDeg = 60, gain = 50, ffGain = 0.5, ffSmoothMs = 80, horizonStrength = 75, horizonLimitSticks = 0.75, horizonLimitDeg = 135, horizonDelayMs = 500
        };
    }

    /// <summary>One drone class as published by ShramkoGSFPV (packages/sim-core/presets/*.json), every number with its source there.</summary>
    public sealed class FpvPreset
    {
        public string id, name, cls;
        public double wheelbaseMm, motorKv, propDiameterMm, auwG, twr;
        public double batteryCells, batteryCapacityMah;
        public double inertiaRollPitch, inertiaYaw, motorTauMs, motorKappaM, motorIdle, rpmLoadedFraction;
        public double cdaHorizontalCm2, cdaVerticalCm2;
        public double colliderDuctRadiusMm, colliderDuctOffsetMm, colliderBodyRadiusMm;
        public double vBounceMs, vCrashMs, packResistancePerCellOhm, efficiency;
        public double cameraUptiltDeg, cameraFovDeg;
        public RatesConfig rates;
        public double[] pidRoll, pidPitch, pidYaw;       // P, I, D, F
        public ThrottleCurve throttle;
        public double rotorDragPerS, rotorInertiaKgm2;
        public double wakeLoss, wakeShake;               // 0 = no prop wash
        public double levelFfGain = double.NaN, levelFfSmoothMs = double.NaN;   // NaN = Betaflight default
    }

    /// <summary>The pilot's overrides over a preset (null = the preset's own value).</summary>
    public sealed class FpvOverrides
    {
        public double? twr, tauMs, cdaScale, gravity, vCrash, vBounce, uptiltDeg, fovDeg, ductDrag, propInertia, idle, wake;
        public GravityMode? gravityMode;
        public RatesConfig rates;
        public double[] pidRoll, pidPitch, pidYaw;
        public ThrottleCurve? throttle;
        public bool? crashOn;
        public LevelParams? level;
    }

    public sealed class FpvParams
    {
        public string presetId;
        public double mass;
        public double[] inertia;                 // body x (pitch), y (yaw), z (roll), kg m^2
        public double twr, tmaxMotorNom, vNom, cells, capacityAs, rPack, eta, omegaMaxPerVolt, tau, kappa, idle;
        public double[] motorPos, motorYaw, cda, spheres;
        public double boundRadius, vBounce, vCrash;
        public RatesConfig rates;
        public double[][] pid;                   // roll, pitch, yaw
        public ThrottleCurve throttle;
        public double gravity;
        public GravityMode gravityMode;
        public double rho, cameraUptiltDeg, cameraFovDeg;
        public LevelParams level;
        public double levelOutputHz;
        public bool crashOn;
        public double ductDrag, rotorInertia, itermWindup, bodyBottom;
        public double wakeLoss, wakeShake, wakeArea;
    }

    public static class FpvCompile
    {
        public const double EarthG = 9.81;
        public const double WakeLossMax = 0.9;

        /// <summary>Open-circuit voltage per cell, piecewise linear 4.2 / 3.7 / 3.3 V at SoC 1 / 0.5 / 0.</summary>
        public static double CellVoc(double soc) => soc >= 0.5 ? 3.7 + (soc - 0.5) * 1.0 : 3.3 + soc * 0.8;

        public static FpvParams Compile(FpvPreset p, FpvOverrides o = null)
        {
            o = o ?? new FpvOverrides();
            double mass = p.auwG / 1000;
            double twr0 = o.twr ?? p.twr;
            double gravity = o.gravity ?? EarthG;
            GravityMode gm = o.gravityMode ?? GravityMode.Honest;
            double tmaxTotal = twr0 * mass * EarthG;
            if (gm == GravityMode.SameTwr) tmaxTotal *= gravity / EarthG;
            double tmaxMotorNom = tmaxTotal / 4;

            double cells = p.batteryCells;
            double capacityAs = p.batteryCapacityMah / 1000 * 3600;
            double rPack = p.packResistancePerCellOhm * cells;
            double eta = p.efficiency;
            double omegaMaxPerVolt = p.motorKv * p.rpmLoadedFraction * 0.10471975511965977;
            double kappa = p.motorKappaM;

            // V_nom: loaded pack voltage at full throttle with a fresh pack (fixed point)
            double vOc = cells * CellVoc(1), v = vOc;
            for (int i = 0; i < 60; i++)
            {
                double omega = omegaMaxPerVolt * v;
                double current = (4 * kappa * tmaxMotorNom * omega) / (eta * v);
                v = vOc - current * rPack;
            }
            double vNom = v;

            double off = p.colliderDuctOffsetMm / 1000, ductR = p.colliderDuctRadiusMm / 1000, bodyR = p.colliderBodyRadiusMm / 1000;
            double arm = p.wheelbaseMm / 2000 / Math.Sqrt(2);
            // Betaflight quad X order: M1 rear-right, M2 front-right, M3 rear-left, M4 front-left. Body frame: x right, y up, z back.
            var motorPos = new[] { arm, 0, arm, arm, 0, -arm, -arm, 0, arm, -arm, 0, -arm };
            var motorYaw = new double[] { 1, -1, -1, 1 };
            double[] spheres = ductR > 0
                ? new[] { off, 0, off, ductR, off, 0, -off, ductR, -off, 0, off, ductR, -off, 0, -off, ductR, 0, 0, 0, bodyR }
                : new[] { 0, 0, 0, bodyR };
            double boundRadius = 0, bodyBottom = 0;
            for (int i = 0; i < spheres.Length; i += 4)
            {
                double d = Math.Sqrt(spheres[i] * spheres[i] + spheres[i + 1] * spheres[i + 1] + spheres[i + 2] * spheres[i + 2]) + spheres[i + 3];
                if (d > boundRadius) boundRadius = d;
                double below = spheres[i + 3] - spheres[i + 1];
                if (below > bodyBottom) bodyBottom = below;
            }

            var lv = LevelParams.Defaults;
            if (!double.IsNaN(p.levelFfGain)) lv.ffGain = p.levelFfGain;
            if (!double.IsNaN(p.levelFfSmoothMs)) lv.ffSmoothMs = p.levelFfSmoothMs;
            if (o.level.HasValue) lv = o.level.Value;

            double cdaScale = o.cdaScale ?? 1;
            double cdaH = p.cdaHorizontalCm2 / 10000 * cdaScale, cdaV = p.cdaVerticalCm2 / 10000 * cdaScale;

            // prop wash (wake.ts wakeOf): the preset's wake_loss / wake_shake times the pilot's multiplier, the loss capped
            double wakeScale = o.wake ?? 1;
            if (!(wakeScale >= 0) || double.IsInfinity(wakeScale)) throw new ArgumentOutOfRangeException("wake", "must be a number >= 0");
            if (p.wakeLoss < 0 || p.wakeLoss > WakeLossMax || p.wakeShake < 0) throw new ArgumentException("preset " + p.id + ": wake_loss must be 0.." + WakeLossMax + " and wake_shake >= 0");
            double wl = Math.Min(WakeLossMax, p.wakeLoss * wakeScale), ws = p.wakeShake * wakeScale, wa = 0;
            if (wl == 0 && ws == 0) { wl = 0; ws = 0; }
            else { double r = p.propDiameterMm / 2000; wa = 4 * Math.PI * r * r; }

            return new FpvParams
            {
                presetId = p.id, mass = mass,
                inertia = new[] { p.inertiaRollPitch, p.inertiaYaw, p.inertiaRollPitch },
                twr = twr0, tmaxMotorNom = tmaxMotorNom, vNom = vNom, cells = cells, capacityAs = capacityAs, rPack = rPack, eta = eta,
                omegaMaxPerVolt = omegaMaxPerVolt, tau = (o.tauMs ?? p.motorTauMs) / 1000, kappa = kappa, idle = o.idle ?? p.motorIdle,
                motorPos = motorPos, motorYaw = motorYaw, cda = new[] { cdaH, cdaV, cdaH }, spheres = spheres, boundRadius = boundRadius,
                vBounce = o.vBounce ?? p.vBounceMs, vCrash = o.vCrash ?? p.vCrashMs,
                rates = (o.rates ?? p.rates).Clone(),
                pid = new[] { o.pidRoll ?? p.pidRoll, o.pidPitch ?? p.pidPitch, o.pidYaw ?? p.pidYaw },
                throttle = o.throttle ?? p.throttle,
                gravity = gravity, gravityMode = gm, rho = 1.225,
                cameraUptiltDeg = o.uptiltDeg ?? p.cameraUptiltDeg, cameraFovDeg = o.fovDeg ?? p.cameraFovDeg,
                level = lv, levelOutputHz = 50, crashOn = o.crashOn ?? true,
                ductDrag = o.ductDrag ?? p.rotorDragPerS, rotorInertia = p.rotorInertiaKgm2 * (o.propInertia ?? 1),
                itermWindup = 0.85, bodyBottom = bodyBottom, wakeLoss = wl, wakeShake = ws, wakeArea = wa
            };
        }

        /// <summary>Hover motor output and stick including battery sag at a state of charge (fixed point; NaN if it cannot hover).</summary>
        public static void HoverSolve(FpvParams sp, double soc, out double motor, out double throttle, out double stick, out double volts)
        {
            SolveHover(sp, soc, out double m, out volts);
            if (!(m <= 1)) { motor = throttle = stick = double.NaN; return; }
            motor = m;
            throttle = (m - sp.idle) / (1 - sp.idle);
            if (sp.gravityMode == GravityMode.AutoThrottle)
            {
                SolveHover(sp, 1, out double fresh, out _);
                stick = AutoThrottleStick(throttle, (fresh - sp.idle) / (1 - sp.idle));
            }
            else stick = FpvRates.InvertThrottle(throttle, sp.throttle);
        }

        static void SolveHover(FpvParams sp, double soc, out double motor, out double volts)
        {
            double need = sp.mass * sp.gravity / 4;
            double vOc = sp.cells * CellVoc(soc), v = vOc, w = 0;
            for (int i = 0; i < 80; i++)
            {
                double tmax = sp.tmaxMotorNom * (v / sp.vNom) * (v / sp.vNom);
                w = Math.Sqrt(need / tmax);
                double omega = w * sp.omegaMaxPerVolt * v;
                double current = 4 * sp.kappa * need * omega / (sp.eta * v);
                v = vOc - current * sp.rPack;
            }
            motor = w; volts = v;
        }

        /// <summary>Inverse of the auto-throttle map (stick 0..0.5 -> 0..hover, 0.5..1 -> hover..1).</summary>
        public static double AutoThrottleStick(double t, double hover)
        {
            double h = hover < 0.02 ? 0.02 : hover > 0.98 ? 0.98 : hover;
            double u = t <= h ? 0.5 * t / h : 0.5 + 0.5 * (t - h) / (1 - h);
            return u < 0 ? 0 : u > 1 ? 1 : u;
        }

        /// <summary>The motor output a fresh pack hovers at (Sim.hoverThr); 1 when the craft cannot hover.</summary>
        public static double HoverThrOf(FpvParams p)
        {
            SolveHover(p, 1, out double m, out _);
            return m >= 0 && m <= 1 ? m : 1;
        }
    }
}
