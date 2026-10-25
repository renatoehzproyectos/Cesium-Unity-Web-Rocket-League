// Port of the parts of JSBSim (https://github.com/JSBSim-Team/jsbsim, LGPL-2.1) that a game needs to FLY an aircraft defined in a
// JSBSim XML file: the standard atmosphere (FGStandardAtmosphere), the auxiliary wind angles (FGAuxiliary), the aerodynamics axes with the
// function/table evaluator (FGAerodynamics, FGFunction, FGTable), the aerosurface_scale FCS component, the electric engine and the
// fixed-pitch propeller (FGElectric, FGPropeller incl. P-factor and gyroscopic moment) and the landing-gear strut (FGLGear).
// Units are JSBSim's: feet, slugs, lbf, seconds, radians, body axes x forward / y right / z down. Engine-free (no UnityEngine).
// The rigid-body integration is NOT here: the game's physics engine integrates, this class returns forces and moments.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace RocketWorld.Sim
{
    public struct D3
    {
        public double x, y, z;
        public D3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
        public static D3 operator +(D3 a, D3 b) => new D3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static D3 operator -(D3 a, D3 b) => new D3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static D3 operator *(D3 a, double s) => new D3(a.x * s, a.y * s, a.z * s);
        public static double Dot(D3 a, D3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static D3 Cross(D3 a, D3 b) => new D3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public double Length => Math.Sqrt(x * x + y * y + z * z);
        public override string ToString() => $"({x:0.###}, {y:0.###}, {z:0.###})";
    }

    /// <summary>JSBSim FGStandardAtmosphere (US 1976 layers, no bias / no humidity).</summary>
    public static class JsbAtmosphere
    {
        const double FtToM = 0.3048, KgToSlug = 0.06852168;
        public const double SLPressure = 2116.228, SHRatio = 1.4;
        public static readonly double Rstar = 8.31432 * KgToSlug / (FtToM * FtToM * 1.8);
        public static readonly double Mair = 28.9645 * KgToSlug / 1000.0;
        public static readonly double Rdry = Rstar / Mair;
        static readonly double G0 = 9.80665 / FtToM, EarthRadius = 6356766.0 / FtToM;
        static readonly double[] H = { 0.0, 36089.2388, 65616.7979, 104986.8766, 154199.4751, 167322.8346, 232939.6325, 278385.8268, 298556.4304 };
        static readonly double[] T = { 518.67, 389.97, 389.97, 411.57, 487.17, 487.17, 386.37, 336.5028, 336.5028 };
        static readonly double[] Lapse = new double[8];
        static readonly double[] PBreak = new double[9];
        static JsbAtmosphere()
        {
            for (int b = 0; b < 8; b++) Lapse[b] = (T[b + 1] - T[b]) / (H[b + 1] - H[b]);
            PBreak[0] = SLPressure;
            for (int b = 0; b < 8; b++)
            {
                double dh = H[b + 1] - H[b], tmb = T[b];
                if (Lapse[b] != 0.0) PBreak[b + 1] = PBreak[b] * Math.Pow(tmb / (tmb + Lapse[b] * dh), G0 / (Rdry * Lapse[b]));
                else PBreak[b + 1] = PBreak[b] * Math.Exp(-G0 * dh / (Rdry * tmb));
            }
        }
        static double Geopotential(double geom) => geom * EarthRadius / (EarthRadius + geom);
        public static double Temperature(double altFt)
        {
            double h = Geopotential(altFt);
            if (h < 0) return T[0] + h * Lapse[0];
            if (h >= H[8]) return T[8];
            int i = 0; while (i < 7 && h >= H[i + 1]) i++;
            return T[i] + (h - H[i]) / (H[i + 1] - H[i]) * (T[i + 1] - T[i]);
        }
        public static double Pressure(double altFt)
        {
            double h = Geopotential(altFt), baseAlt = H[0]; int b;
            for (b = 0; b < 7; b++) { if (h < H[b + 1]) break; baseAlt = H[b + 1]; }
            double tmb = T[b], dh = h - baseAlt, l = Lapse[b];
            return l != 0.0 ? PBreak[b] * Math.Pow(tmb / (tmb + l * dh), G0 / (Rdry * l)) : PBreak[b] * Math.Exp(-G0 * dh / (Rdry * tmb));
        }
        public static void At(double altFt, out double tempR, out double pressPsf, out double rho, out double soundFps)
        {
            tempR = Temperature(altFt); pressPsf = Pressure(altFt);
            rho = pressPsf / (Rstar / Mair * tempR);
            soundFps = Math.Sqrt(SHRatio * (Rstar / Mair) * tempR);
        }
    }

    /// <summary>1-D linear table with clamped ends (FGTable).</summary>
    public sealed class JsbTable
    {
        readonly double[] _x, _y;
        public JsbTable(double[] x, double[] y) { _x = x; _y = y; }
        public double Eval(double v)
        {
            int n = _x.Length;
            if (v <= _x[0]) return _y[0];
            if (v >= _x[n - 1]) return _y[n - 1];
            int i = 1; while (_x[i] < v) i++;
            return _y[i - 1] + (v - _x[i - 1]) / (_x[i] - _x[i - 1]) * (_y[i] - _y[i - 1]);
        }
        public static JsbTable Parse(XmlElement tbl)
        {
            var data = tbl.SelectSingleNode("tableData");
            var nums = new List<double>();
            foreach (var tok in data.InnerText.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) nums.Add(double.Parse(tok, CultureInfo.InvariantCulture));
            if (nums.Count % 2 != 0) throw new FormatException("only 1-D tables are supported (<tableData> needs x y pairs)");
            var x = new double[nums.Count / 2]; var y = new double[nums.Count / 2];
            for (int i = 0; i < x.Length; i++) { x[i] = nums[2 * i]; y[i] = nums[2 * i + 1]; }
            return new JsbTable(x, y);
        }
    }

    /// <summary>The subset of JSBSim's &lt;function&gt; grammar used by this aircraft; unknown elements throw so nothing is silently ignored.</summary>
    public sealed class JsbFunction
    {
        readonly Func<JsbAircraft, double> _eval;
        JsbFunction(Func<JsbAircraft, double> e) { _eval = e; }
        public double Eval(JsbAircraft env) => _eval(env);

        public static JsbFunction Parse(XmlElement fn)
        {
            var kids = new List<XmlElement>();
            foreach (XmlNode n in fn.ChildNodes) if (n is XmlElement e && e.Name != "description") kids.Add(e);
            if (kids.Count == 0) throw new FormatException("empty function " + fn.GetAttribute("name"));
            if (fn.Name == "function" && kids.Count == 1) return Node(kids[0]);
            return Node(fn);
        }

        static List<JsbFunction> Args(XmlElement e)
        {
            var l = new List<JsbFunction>();
            foreach (XmlNode n in e.ChildNodes) if (n is XmlElement c && c.Name != "description" && c.Name != "independentVar" && c.Name != "tableData") l.Add(Node(c));
            return l;
        }

        static JsbFunction Node(XmlElement e)
        {
            switch (e.Name)
            {
                case "value": { double v = double.Parse(e.InnerText.Trim(), CultureInfo.InvariantCulture); return new JsbFunction(env => v); }
                case "property": { string p = e.InnerText.Trim(); bool neg = p.StartsWith("-"); if (neg) p = p.Substring(1); return new JsbFunction(env => (neg ? -1 : 1) * env.Property(p)); }
                case "product": { var a = Args(e); return new JsbFunction(env => { double r = 1; foreach (var f in a) r *= f.Eval(env); return r; }); }
                case "sum": { var a = Args(e); return new JsbFunction(env => { double r = 0; foreach (var f in a) r += f.Eval(env); return r; }); }
                case "difference": { var a = Args(e); return new JsbFunction(env => { double r = a[0].Eval(env); for (int i = 1; i < a.Count; i++) r -= a[i].Eval(env); return r; }); }
                case "quotient": { var a = Args(e); return new JsbFunction(env => a[0].Eval(env) / a[1].Eval(env)); }
                case "abs": { var a = Args(e); return new JsbFunction(env => Math.Abs(a[0].Eval(env))); }
                case "min": { var a = Args(e); return new JsbFunction(env => { double r = double.MaxValue; foreach (var f in a) r = Math.Min(r, f.Eval(env)); return r; }); }
                case "max": { var a = Args(e); return new JsbFunction(env => { double r = double.MinValue; foreach (var f in a) r = Math.Max(r, f.Eval(env)); return r; }); }
                case "pow": { var a = Args(e); return new JsbFunction(env => Math.Pow(a[0].Eval(env), a[1].Eval(env))); }
                case "sin": { var a = Args(e); return new JsbFunction(env => Math.Sin(a[0].Eval(env))); }
                case "cos": { var a = Args(e); return new JsbFunction(env => Math.Cos(a[0].Eval(env))); }
                case "table":
                    {
                        var iv = e.SelectNodes("independentVar");
                        if (iv.Count != 1) throw new FormatException("only 1-D tables are supported in functions");
                        string p = iv[0].InnerText.Trim(); var t = JsbTable.Parse(e);
                        return new JsbFunction(env => t.Eval(env.Property(p)));
                    }
                case "function": return Parse(e);
                default: throw new FormatException("unsupported function element <" + e.Name + ">");
            }
        }
    }

    public sealed class JsbGear
    {
        public string name; public D3 locIn;                       // structural frame, inches
        public double spring, damp, dampRebound, staticF, dynamicF, rollingF, maxSteerDeg; public string brakeGroup;
        /// <summary>FGLGear::ComputeVerticalStrutForce (linear damping): force along the strut, lbf, always &lt;= 0 (pushes up).</summary>
        public double StrutForce(double compressFt, double compressSpeedFps)
        {
            double sp = -compressFt * spring, dp = compressSpeedFps >= 0 ? -compressSpeedFps * damp : -compressSpeedFps * dampRebound;
            return Math.Min(sp + dp, 0.0);
        }
        /// <summary>FGLGear::ComputeSideForceCoefficient with the default tire constants (stiffness 0.06, shape 2.8, curvature 1.03).</summary>
        public double SideForceCoeff(double slipDeg)
        {
            double s = 0.06 * slipDeg;
            return staticF * Math.Sin(2.8 * Math.Atan(s - 1.03 * (s - Math.Atan(s))));
        }
        /// <summary>FGLGear::ComputeBrakeForceCoefficient.</summary>
        public double BrakeForceCoeff(double brake) => rollingF + (brakeGroup == "NONE" ? 0.0 : brake * (staticF - rollingF));
    }

    public sealed class JsbAircraft
    {
        const double FtPerIn = 1.0 / 12.0, LbToSlug = 1.0 / 32.174049, Deg = Math.PI / 180.0;
        public string name;
        public double wingArea, wingSpan, chord;
        public double massSlug, ixx, iyy, izz, ixz;
        public D3 cgIn, aeroRpIn;
        public List<JsbGear> gears = new List<JsbGear>();
        public double maxPowerWatts;
        // propeller
        public double propDiameterFt, propIxx; public JsbTable cThrust, cPower, ctMach, cpMach; public D3 propLocIn; public double propSense = 1, pFactor;
        // surfaces: pos = f(cmd) by aerosurface_scale (zero centred)
        double _elMin, _elMax, _aiMin, _aiMax, _ruMin, _ruMax;
        readonly List<JsbFunction>[] _axes = { new List<JsbFunction>(), new List<JsbFunction>(), new List<JsbFunction>(), new List<JsbFunction>(), new List<JsbFunction>(), new List<JsbFunction>() };   // LIFT DRAG SIDE ROLL PITCH YAW

        // --- state ---
        public double propRpm;                                    // propeller speed, persists between steps (this is the engine state)
        public double elevatorPosRad, aileronPosRad, rudderPosRad, throttle;
        D3 _propActing;                                           // acting location of the thrust (P-factor shifts it), structural inches
        // per-step auxiliary values, readable by <property> nodes
        double _alpha, _beta, _qbar, _bi2vel, _ci2vel; D3 _pqr;
        public double Alpha => _alpha; public double Beta => _beta; public double Qbar => _qbar;

        public double Property(string p)
        {
            switch (p)
            {
                case "aero/qbar-area": return wingArea * _qbar;
                case "aero/alpha-rad": return _alpha;
                case "aero/beta-rad": return _beta;
                case "aero/alpha-deg": return _alpha / Deg;
                case "aero/beta-deg": return _beta / Deg;
                case "aero/ci2vel": return _ci2vel;
                case "aero/bi2vel": return _bi2vel;
                case "velocities/p-aero-rad_sec": return _pqr.x;
                case "velocities/q-aero-rad_sec": return _pqr.y;
                case "velocities/r-aero-rad_sec": return _pqr.z;
                case "fcs/elevator-pos-rad": return elevatorPosRad;
                case "fcs/aileron-pos-rad": return aileronPosRad;
                case "fcs/rudder-pos-rad": return rudderPosRad;
                case "metrics/bw-ft": return wingSpan;
                case "metrics/cbarw-ft": return chord;
                default: throw new KeyNotFoundException("unknown JSBSim property '" + p + "'");
            }
        }

        /// <summary>structural (inches, x aft, y right, z up) to body (feet, x fwd, y right, z down) relative to the CG: FGMassBalance::StructuralToBody.</summary>
        public D3 StructuralToBody(D3 r) => new D3(FtPerIn * (cgIn.x - r.x), FtPerIn * (r.y - cgIn.y), FtPerIn * (cgIn.z - r.z));

        public D3 PropLocationBody => StructuralToBody(propLocIn);
        public double Weight => massSlug * 32.174049;

        // ------------------------------------------------------------------------------------------ loading
        static double Num(XmlElement parent, string tag, string defUnit = null)
        {
            var e = parent.SelectSingleNode(tag) as XmlElement;
            if (e == null) throw new FormatException("missing <" + tag + ">");
            return Conv(double.Parse(e.InnerText.Trim(), CultureInfo.InvariantCulture), e.GetAttribute("unit"), defUnit);
        }
        static double Conv(double v, string unit, string def)
        {
            switch (string.IsNullOrEmpty(unit) ? def : unit)
            {
                case null: case "": case "FT": case "FT2": case "LBS": case "SLUG*FT2": case "RAD": case "LBS/FT": case "LBS/FT/SEC": case "WATTS": return v;
                case "IN": return v / 12.0;
                case "DEG": return v * Deg;
                case "HP": return v * 745.7;
                case "M": return v / 0.3048;
                case "KG": return v * 2.20462;
                default: throw new FormatException("unsupported unit " + unit);
            }
        }
        static D3 Loc(XmlElement e)           // inches, structural frame (JSBSim file convention for <location unit="IN">)
        {
            string u = e.GetAttribute("unit"); double k = u == "FT" ? 12.0 : u == "M" ? 39.3701 : 1.0;
            return new D3(Num(e, "x") * k, Num(e, "y") * k, Num(e, "z") * k);
        }

        public static JsbAircraft Load(string aircraftXml, string engineXml, string propXml)
        {
            var a = new JsbAircraft(); var d = new XmlDocument(); d.LoadXml(aircraftXml); var root = d.DocumentElement;
            a.name = root.GetAttribute("name");
            var m = (XmlElement)root.SelectSingleNode("metrics");
            a.wingArea = Num(m, "wingarea"); a.wingSpan = Num(m, "wingspan"); a.chord = Num(m, "chord");
            foreach (XmlElement l in m.SelectNodes("location")) if (l.GetAttribute("name") == "AERORP") a.aeroRpIn = Loc(l);
            var mb = (XmlElement)root.SelectSingleNode("mass_balance");
            a.ixx = Num(mb, "ixx"); a.iyy = Num(mb, "iyy"); a.izz = Num(mb, "izz"); a.ixz = mb.SelectSingleNode("ixz") != null ? Num(mb, "ixz") : 0;
            a.massSlug = Num(mb, "emptywt") * LbToSlug;
            foreach (XmlElement pm in mb.SelectNodes("pointmass")) a.massSlug += Num(pm, "weight") * LbToSlug;
            foreach (XmlElement l in mb.SelectNodes("location")) if (l.GetAttribute("name") == "CG") a.cgIn = Loc(l);
            foreach (XmlElement c in root.SelectNodes("ground_reactions/contact"))
            {
                if (c.GetAttribute("type") != "BOGEY") continue;
                var g = new JsbGear { name = c.GetAttribute("name"), locIn = Loc((XmlElement)c.SelectSingleNode("location")) };
                g.spring = Num(c, "spring_coeff"); g.damp = Num(c, "damping_coeff");
                g.dampRebound = c.SelectSingleNode("damping_coeff_rebound") != null ? Num(c, "damping_coeff_rebound") : g.damp;
                g.staticF = Num(c, "static_friction"); g.dynamicF = Num(c, "dynamic_friction"); g.rollingF = Num(c, "rolling_friction");
                g.maxSteerDeg = c.SelectSingleNode("max_steer") != null ? Num(c, "max_steer", "DEG") / Deg : 0;
                g.brakeGroup = c.SelectSingleNode("brake_group") != null ? c.SelectSingleNode("brake_group").InnerText.Trim() : "NONE";
                a.gears.Add(g);
            }
            // flight control: only aerosurface_scale (zero centred) is supported
            foreach (XmlElement s in root.SelectNodes("flight_control//aerosurface_scale"))
            {
                string o = s.SelectSingleNode("output").InnerText.Trim(); var r = (XmlElement)s.SelectSingleNode("range");
                double mn = Num(r, "min"), mx = Num(r, "max");
                if (o == "fcs/elevator-pos-rad") { a._elMin = mn; a._elMax = mx; }
                else if (o == "fcs/aileron-pos-rad") { a._aiMin = mn; a._aiMax = mx; }
                else if (o == "fcs/rudder-pos-rad") { a._ruMin = mn; a._ruMax = mx; }
            }
            // propulsion
            var th = (XmlElement)root.SelectSingleNode("propulsion/engine/thruster");
            a.propLocIn = Loc((XmlElement)th.SelectSingleNode("location")); a._propActing = a.propLocIn;
            // <sense> and <p_factor> live in the parent <engine>?  (JSBSim: "must be specified at the parent level" = in <thruster>)
            var sense = th.SelectSingleNode("sense") ?? th.ParentNode.SelectSingleNode("sense");
            var pf = th.SelectSingleNode("p_factor") ?? th.ParentNode.SelectSingleNode("p_factor");
            a.propSense = sense != null && double.Parse(sense.InnerText.Trim(), CultureInfo.InvariantCulture) < 0 ? -1.0 : 1.0;
            a.pFactor = pf != null ? double.Parse(pf.InnerText.Trim(), CultureInfo.InvariantCulture) : 0.0;
            var ed = new XmlDocument(); ed.LoadXml(engineXml);
            a.maxPowerWatts = Num(ed.DocumentElement, "power", "WATTS");
            var pd = new XmlDocument(); pd.LoadXml(propXml); var pe = pd.DocumentElement;
            a.propIxx = Math.Max(Num(pe, "ixx"), 1e-6); a.propDiameterFt = Math.Max(Num(pe, "diameter", "FT"), 0.001);
            if (Num(pe, "minpitch") != Num(pe, "maxpitch")) throw new FormatException("only fixed-pitch propellers are supported");
            foreach (XmlElement t in pe.SelectNodes("table"))
            {
                string n = t.GetAttribute("name").ToUpperInvariant();
                if (n == "C_THRUST") a.cThrust = JsbTable.Parse(t); else if (n == "C_POWER") a.cPower = JsbTable.Parse(t);
                else if (n == "CT_MACH") a.ctMach = JsbTable.Parse(t); else if (n == "CP_MACH") a.cpMach = JsbTable.Parse(t);
            }
            if (a.cThrust == null || a.cPower == null) throw new FormatException("propeller needs C_THRUST and C_POWER tables");
            // aerodynamics
            string[] axes = { "LIFT", "DRAG", "SIDE", "ROLL", "PITCH", "YAW" };
            foreach (XmlElement ax in root.SelectNodes("aerodynamics/axis"))
            {
                int i = Array.IndexOf(axes, ax.GetAttribute("name")); if (i < 0) throw new FormatException("unknown axis " + ax.GetAttribute("name"));
                foreach (XmlElement f in ax.SelectNodes("function")) a._axes[i].Add(JsbFunction.Parse(f));
            }
            return a;
        }

        // ------------------------------------------------------------------------------------------ controls
        /// <summary>Command inputs, normalised: elevator / aileron / rudder in [-1, 1], throttle in [0, 1]. Positions follow JSBSim's zero-centred aerosurface_scale.</summary>
        public void SetControls(double elevator, double aileron, double rudder, double thr)
        {
            elevatorPosRad = Surf(Clamp(elevator), _elMin, _elMax); aileronPosRad = Surf(Clamp(aileron), _aiMin, _aiMax); rudderPosRad = Surf(Clamp(rudder), _ruMin, _ruMax);
            throttle = Math.Max(0, Math.Min(1, thr));
        }
        static double Clamp(double v) => Math.Max(-1.0, Math.Min(1.0, v));
        static double Surf(double c, double mn, double mx) => c == 0 ? 0 : (c > 0 ? c * mx : c * mn / -1.0);

        // ------------------------------------------------------------------------------------------ one JSBSim frame of aero + propulsion
        public struct Result
        {
            public D3 force, moment;                   // lbf, lbf*ft, body axes, about the CG (aero + propeller)
            public D3 aeroForce, aeroMoment, propForce, propMoment;
            public double alpha, beta, qbar, rho, soundFps, thrust, rpm, powerHp;
        }

        public Result Step(D3 uvw, D3 pqr, double altitudeFt, double dt)
        {
            var r = new Result();
            double temp, press, rho, a; JsbAtmosphere.At(altitudeFt, out temp, out press, out rho, out a);
            // FGAuxiliary (no wind)
            double u2 = uvw.x * uvw.x, v2 = uvw.y * uvw.y, w2 = uvw.z * uvw.z, uw = u2 + w2, vt = Math.Sqrt(uw + v2);
            _alpha = 0; _beta = 0;
            if (vt > 0.001) { _beta = Math.Atan2(uvw.y, Math.Sqrt(uw)); if (uw >= 1e-6) _alpha = Math.Atan2(uvw.z, uvw.x); }
            _qbar = 0.5 * rho * vt * vt; _pqr = pqr;
            if (vt != 0) { _bi2vel = wingSpan / (2 * vt); _ci2vel = chord / (2 * vt); } else { _bi2vel = _ci2vel = 0; }
            // FGAerodynamics: wind-axes forces, body-axes moments about the aero reference point
            double lift = 0, drag = 0, side = 0, roll = 0, pitch = 0, yaw = 0;
            foreach (var f in _axes[0]) lift += f.Eval(this); foreach (var f in _axes[1]) drag += f.Eval(this); foreach (var f in _axes[2]) side += f.Eval(this);
            foreach (var f in _axes[3]) roll += f.Eval(this); foreach (var f in _axes[4]) pitch += f.Eval(this); foreach (var f in _axes[5]) yaw += f.Eval(this);
            double ca = Math.Cos(_alpha), sa = Math.Sin(_alpha), cb = Math.Cos(_beta), sb = Math.Sin(_beta);
            D3 fw = new D3(-drag, side, -lift);                                    // native wind-axes (X = -drag, Z = -lift)
            D3 fb = new D3(ca * cb * fw.x - ca * sb * fw.y - sa * fw.z, sb * fw.x + cb * fw.y, sa * cb * fw.x - sa * sb * fw.y + ca * fw.z);   // Tw2b
            D3 rp = StructuralToBody(aeroRpIn);
            r.aeroForce = fb; r.aeroMoment = new D3(roll, pitch, yaw) + D3.Cross(rp, fb);
            // FGElectric + FGPropeller
            double hp = maxPowerWatts * throttle / 745.7, enginePower = hp * 550.0;
            if (propRpm <= 0.1) enginePower = Math.Max(enginePower, 0.0);
            D3 dxyz = StructuralToBody(propLocIn);
            D3 local = uvw + D3.Cross(pqr, dxyz);
            double vel = local.x, rps = propRpm / 60.0, diam = propDiameterFt, area = 0.25 * diam * diam * Math.PI;
            double vtip = rps * diam * Math.PI, tipMach = Math.Sqrt(vtip * vtip + vel * vel) / a;     // helical tip Mach
            double j = rps > 0.01 ? vel / (diam * rps) : vel / diam;
            double cp = cPower.Eval(j) * (cpMach != null ? cpMach.Eval(tipMach) : 1.0), rpsL = rps < 0.01 ? 0.01 : rps;
            double powerReq = cp * rpsL * rpsL * rpsL * Math.Pow(diam, 5) * rho;
            double powerAvail = enginePower - powerReq;
            double thrust = cThrust.Eval(j) * (ctMach != null ? ctMach.Eval(tipMach) : 1.0) * rps * rps * Math.Pow(diam, 4) * rho;
            double vel2sum = vel * Math.Abs(vel) + 2.0 * thrust / (rho * area), vind = vel2sum > 0 ? 0.5 * (-vel + Math.Sqrt(vel2sum)) : 0.5 * (-vel - Math.Sqrt(-vel2sum));
            if (pFactor > 0.0001)
            {
                double tang = Math.Sqrt(local.y * local.y + local.z * local.z);
                if (tang > 0.0001)
                {
                    double angle = Math.Atan2(tang, vel + vind), factor = propSense * pFactor * angle / tang;
                    _propActing = new D3(propLocIn.x, propLocIn.y + factor * local.z, propLocIn.z + factor * local.y);
                }
            }
            double omega = rps * 2.0 * Math.PI;
            D3 torque = new D3(-propSense * enginePower / Math.Max(0.01, omega), 0, 0);
            D3 hAng = new D3(propIxx * omega * propSense, 0, 0);
            double excess = omega > 0.01 ? powerAvail / omega : powerAvail / 1.0;
            propRpm = (rps + ((excess / propIxx) / (2.0 * Math.PI)) * dt) * 60.0; if (propRpm < 0) propRpm = 0;
            D3 vMn = D3.Cross(pqr, hAng) + torque;
            D3 pf3 = new D3(thrust, 0, 0);
            r.propForce = pf3; r.propMoment = vMn + D3.Cross(StructuralToBody(_propActing), pf3);
            r.force = r.aeroForce + r.propForce; r.moment = r.aeroMoment + r.propMoment;
            r.alpha = _alpha; r.beta = _beta; r.qbar = _qbar; r.rho = rho; r.soundFps = a; r.thrust = thrust; r.rpm = propRpm; r.powerHp = hp;
            return r;
        }
    }
}
