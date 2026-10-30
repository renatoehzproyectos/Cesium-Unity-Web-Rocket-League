// Generic, engine-free interpreter for JSBSim aircraft files (https://github.com/JSBSim-Team/jsbsim, LGPL-2.1).
// Unlike JsbModel.cs (the Cessna-only port) this loads an UNMODIFIED JSBSim <fdm_config>: property tree, <function> grammar with 1-3 D tables,
// flight-control channels (summer, gains, filters, kinematic, switch, deadband, fcs_function, sensor, actuator, integrator), turbine engines with
// direct thrusters (pitch / yaw / reverser vectoring), fuel tanks (mass + CG shift), external reactions, the auxiliary wind-axis properties
// and the landing-gear strut model. The rigid-body integration and ground contact detection stay with the host physics engine.
// Units are JSBSim's: ft, slug, lbf, s, rad; body axes x fwd / y right / z down; structural axes (inches) x aft / y right / z up.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace RocketWorld.Sim
{
    public sealed class Cell { public double v; }

    public sealed class JsbFdm
    {
        public const double Deg = Math.PI / 180.0, FtPerIn = 1.0 / 12.0, G0 = 32.174049, SlPress = 2116.228, RhoSL = 0.002376968;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---------------------------------------------------------------- property tree
        readonly Dictionary<string, Cell> _p = new Dictionary<string, Cell>();
        public static string Norm(string n)
        {
            n = n.Trim().Replace("[0]", "");
            if (n.StartsWith("/")) n = n.Substring(1);
            return n;
        }
        public Cell Cell(string name)
        {
            name = Norm(name);
            if (!_p.TryGetValue(name, out var c)) { c = new Cell(); _p[name] = c; }
            return c;
        }
        public bool Has(string name) => _p.ContainsKey(Norm(name));
        public double this[string name] { get => Cell(name).v; set => Cell(name).v = value; }
        public IEnumerable<string> PropertyNames => _p.Keys;

        // ---------------------------------------------------------------- expressions
        public delegate double Ev();
        sealed class Param { public Ev f; public bool constant; public double val; }

        Ev Value(string s)
        {
            s = s.Trim();
            if (double.TryParse(s, NumberStyles.Float, Inv, out double d)) return () => d;
            bool neg = s.StartsWith("-"); if (neg) s = s.Substring(1);
            var c = Cell(s); return neg ? (Ev)(() => -c.v) : () => c.v;
        }
        bool IsNumber(string s) => double.TryParse(s.Trim(), NumberStyles.Float, Inv, out _);

        static IEnumerable<XmlElement> Kids(XmlElement e) { foreach (XmlNode n in e.ChildNodes) if (n is XmlElement k && k.Name != "description") yield return k; }

        public Ev Func(XmlElement e)
        {
            // <function> wraps exactly one operation; named functions are published as properties
            Ev inner;
            var kids = new List<XmlElement>(Kids(e));
            if (e.Name == "function" || e.Name == "fcs_function") { var k2 = kids.FindAll(k => k.Name != "output"); inner = k2.Count == 1 ? Op(k2[0]) : Op(e, k2); }
            else inner = Op(e);
            string nm = e.GetAttribute("name");
            if (e.Name == "function" && nm.Length > 0 && nm.Contains("/") ) { var cell = Cell(nm); var f0 = inner; return () => cell.v = f0(); }
            return inner;
        }
        Ev Op(XmlElement e, List<XmlElement> kids = null)
        {
            var a = new List<Ev>(); string name = e.Name;
            if (name == "function") return Func(e);
            if (kids == null) { kids = new List<XmlElement>(); foreach (var k in Kids(e)) if (k.Name != "independentVar" && k.Name != "tableData") kids.Add(k); }
            switch (name)
            {
                case "value": { double v = double.Parse(e.InnerText.Trim(), Inv); return () => v; }
                case "v": goto case "value";
                case "property": case "p": return Value(e.InnerText);
                case "table": return Table(e);
            }
            foreach (var k in kids) a.Add(Op(k));
            switch (name)
            {
                case "product": return () => { double r = 1; for (int i = 0; i < a.Count; i++) r *= a[i](); return r; };
                case "sum": return () => { double r = 0; for (int i = 0; i < a.Count; i++) r += a[i](); return r; };
                case "difference": return () => { double r = a[0](); for (int i = 1; i < a.Count; i++) r -= a[i](); return r; };
                case "quotient": return () => a[0]() / a[1]();
                case "pow": return () => Math.Pow(a[0](), a[1]());
                case "abs": return () => Math.Abs(a[0]());
                case "sqrt": return () => Math.Sqrt(a[0]());
                case "sin": return () => Math.Sin(a[0]());
                case "cos": return () => Math.Cos(a[0]());
                case "tan": return () => Math.Tan(a[0]());
                case "asin": return () => Math.Asin(a[0]());
                case "acos": return () => Math.Acos(a[0]());
                case "atan": return () => Math.Atan(a[0]());
                case "atan2": return () => Math.Atan2(a[0](), a[1]());
                case "exp": return () => Math.Exp(a[0]());
                case "ln": return () => Math.Log(a[0]());
                case "log10": return () => Math.Log10(a[0]());
                case "floor": return () => Math.Floor(a[0]());
                case "ceil": return () => Math.Ceiling(a[0]());
                case "sign": return () => Math.Sign(a[0]()) == 0 ? 0 : Math.Sign(a[0]());
                case "mod": return () => a[0]() % a[1]();
                case "fraction": return () => { double x = a[0](); return x - Math.Truncate(x); };
                case "integer": return () => Math.Truncate(a[0]());
                case "min": return () => { double r = double.MaxValue; foreach (var f in a) r = Math.Min(r, f()); return r; };
                case "max": return () => { double r = double.MinValue; foreach (var f in a) r = Math.Max(r, f()); return r; };
                case "avg": return () => { double r = 0; foreach (var f in a) r += f(); return r / a.Count; };
                case "lt": return () => a[0]() < a[1]() ? 1 : 0;
                case "le": return () => a[0]() <= a[1]() ? 1 : 0;
                case "gt": return () => a[0]() > a[1]() ? 1 : 0;
                case "ge": return () => a[0]() >= a[1]() ? 1 : 0;
                case "eq": return () => a[0]() == a[1]() ? 1 : 0;
                case "ne": return () => a[0]() != a[1]() ? 1 : 0;
                case "ifthen": return () => a[0]() != 0 ? a[1]() : a[2]();
                case "and": return () => { foreach (var f in a) if (f() == 0) return 0; return 1; };
                case "or": return () => { foreach (var f in a) if (f() != 0) return 1; return 0; };
                case "not": return () => a[0]() == 0 ? 1 : 0;
                default: throw new FormatException("unsupported <" + name + "> in function");
            }
        }

        Ev Table(XmlElement t)
        {
            var ivs = new List<XmlElement>(); foreach (XmlElement iv in t.SelectNodes("independentVar")) ivs.Add(iv);
            var tds = t.SelectNodes("tableData");
            Func<XmlElement, double[][]> parseRows = td =>
            {
                var rows = new List<double[]>();
                foreach (var line in td.InnerText.Split('\n'))
                {
                    var tok = line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries); if (tok.Length == 0) continue;
                    var r = new double[tok.Length]; for (int i = 0; i < r.Length; i++) r[i] = double.Parse(tok[i], Inv); rows.Add(r);
                }
                return rows.ToArray();
            };
            Func<XmlElement, Ev> ind = iv => Value(iv.InnerText);
            if (ivs.Count == 1)
            {
                var rows = parseRows((XmlElement)tds[0]); var x = new List<double>(); var y = new List<double>();
                foreach (var r in rows) { if (r.Length == 2) { x.Add(r[0]); y.Add(r[1]); } else for (int i = 0; i + 1 < r.Length; i += 2) { x.Add(r[i]); y.Add(r[i + 1]); } }
                double[] X = x.ToArray(), Y = y.ToArray(); var v = ind(ivs[0]);
                return () => Interp1(X, Y, v());
            }
            if (ivs.Count == 2) return Table2((XmlElement)tds[0], ivs, parseRows);
            if (ivs.Count == 3)
            {
                // <independentVar lookup="row|column|table">; each tableData has breakPoint = table key
                Ev rowV = null, colV = null, tabV = null;
                foreach (var iv in ivs) { string lk = iv.GetAttribute("lookup"); if (lk == "row") rowV = ind(iv); else if (lk == "column") colV = ind(iv); else tabV = ind(iv); }
                var keys = new List<double>(); var subs = new List<Ev>();
                var order = ivs; // sub-tables reuse Table2 with row/column
                foreach (XmlElement td in tds) { keys.Add(double.Parse(td.GetAttribute("breakPoint"), Inv)); subs.Add(Table2(td, ivs, parseRows, true, rowV, colV)); }
                double[] K = keys.ToArray();
                return () => { double tv = tabV(); if (tv <= K[0]) return subs[0](); if (tv >= K[K.Length - 1]) return subs[K.Length - 1](); int i = 1; while (K[i] < tv) i++; double f = (tv - K[i - 1]) / (K[i] - K[i - 1]); double lo = subs[i - 1](), hi = subs[i](); return lo + f * (hi - lo); };
            }
            throw new FormatException("tables of " + ivs.Count + " dimensions are not supported");
        }
        Ev Table2(XmlElement td, List<XmlElement> ivs, Func<XmlElement, double[][]> parse, bool sub = false, Ev rowV = null, Ev colV = null)
        {
            if (!sub) { foreach (var iv in ivs) { string lk = iv.GetAttribute("lookup"); if (lk == "row") rowV = Value(iv.InnerText); else if (lk == "column") colV = Value(iv.InnerText); } if (rowV == null || colV == null) { rowV = Value(ivs[0].InnerText); colV = Value(ivs[1].InnerText); } }
            var rows = parse(td); var cols = rows[0]; // header: column break points
            int nr = rows.Length - 1; var rk = new double[nr]; var d = new double[nr][];
            for (int i = 0; i < nr; i++) { rk[i] = rows[i + 1][0]; d[i] = new double[cols.Length]; for (int j = 0; j < cols.Length; j++) d[i][j] = rows[i + 1][j + 1]; }
            return () =>
            {
                double r = rowV(), c = colV(); Bracket(rk, r, out int i0, out int i1, out double fr); Bracket(cols, c, out int j0, out int j1, out double fc);
                double a = d[i0][j0] + fc * (d[i0][j1] - d[i0][j0]), b = d[i1][j0] + fc * (d[i1][j1] - d[i1][j0]); return a + fr * (b - a);
            };
        }
        static void Bracket(double[] k, double v, out int lo, out int hi, out double f)
        {
            int n = k.Length; if (v <= k[0]) { lo = hi = 0; f = 0; return; } if (v >= k[n - 1]) { lo = hi = n - 1; f = 0; return; }
            int i = 1; while (k[i] < v) i++; lo = i - 1; hi = i; f = (v - k[lo]) / (k[hi] - k[lo]);
        }
        static double Interp1(double[] x, double[] y, double v) { Bracket(x, v, out int a, out int b, out double f); return y[a] + f * (y[b] - y[a]); }

        // ---------------------------------------------------------------- conditions (switch)
        Func<bool> Cond(XmlElement e)
        {
            string logic = e.GetAttribute("logic"); bool or = logic.ToUpperInvariant() == "OR"; var terms = new List<Func<bool>>();
            foreach (var line in e.InnerText.Split('\n'))
            {
                // only direct text lines (nested <condition> text is also in InnerText, so use child text nodes)
            }
            foreach (XmlNode n in e.ChildNodes)
            {
                if (n is XmlText || n is XmlCDataSection)
                    foreach (var line in n.Value.Split('\n')) { var s = line.Trim(); if (s.Length == 0) continue; terms.Add(Cmp(s)); }
                else if (n is XmlElement c && c.Name == "condition") terms.Add(Cond(c));
            }
            return () => { if (or) { foreach (var t in terms) if (t()) return true; return false; } foreach (var t in terms) if (!t()) return false; return true; };
        }
        Func<bool> Cmp(string s)
        {
            var tok = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries); if (tok.Length != 3) throw new FormatException("bad condition '" + s + "'");
            var l = Value(tok[0]); var r = Value(tok[2]);
            switch (tok[1].ToLowerInvariant())
            {
                case "eq": case "==": return () => l() == r();
                case "ne": case "!=": return () => l() != r();
                case "gt": case ">": return () => l() > r();
                case "ge": case ">=": return () => l() >= r();
                case "lt": case "<": return () => l() < r();
                case "le": case "<=": return () => l() <= r();
            }
            throw new FormatException("bad operator in '" + s + "'");
        }

        // ---------------------------------------------------------------- FCS
        abstract class Comp
        {
            public string name; public List<Ev> inputs = new List<Ev>(); public List<Cell> outs = new List<Cell>(); public double output, input;
            public bool clip; public Ev cmin, cmax; public JsbFdm fdm;
            public abstract void Run(double dt);
            protected void Clip() { if (!clip) return; double lo = cmin(), hi = cmax(); if (hi - lo < 0) return; output = Math.Max(lo, Math.Min(hi, output)); }
            protected void Set() { foreach (var c in outs) c.v = output; fdm.Mirror(outs, output); }
        }
        // writing fcs/<s>-pos-rad|deg keeps its siblings (rad, deg, mag-rad) consistent, like FGFCS' tied properties
        readonly Dictionary<Cell, string> _mirrorBase = new Dictionary<Cell, string>();
        void Mirror(List<Cell> outs, double v)
        {
            foreach (var c in outs)
            {
                if (!_mirrorBase.TryGetValue(c, out string s)) continue;
                bool rad = s.EndsWith("-pos-rad"); string b = s.Substring(0, s.Length - (rad ? 8 : 8));
                double r = rad ? v : v * Deg, dd = rad ? v / Deg : v;
                Cell("fcs/" + b + "-pos-rad").v = r; Cell("fcs/" + b + "-pos-deg").v = dd; Cell("fcs/mag-" + b + "-pos-rad").v = Math.Abs(r);
            }
        }
        sealed class Summer : Comp { public double bias; public override void Run(double dt) { output = bias; foreach (var f in inputs) output += f(); Clip(); Set(); } }
        sealed class Gain : Comp
        {
            public Ev gain; public Ev sched; public bool aero, zc = true; public double inMin = -1, inMax = 1, outMin, outMax;
            public override void Run(double dt)
            {
                input = inputs[0]();
                if (aero)
                {
                    if (zc) output = input == 0 ? 0 : input > 0 ? input / inMax * outMax : input / inMin * outMin; else output = outMin + (input - inMin) / (inMax - inMin) * (outMax - outMin);
                    output *= gain();
                }
                else output = gain() * (sched != null ? sched() : 1.0) * input;
                Clip(); Set();
            }
        }
        sealed class Kinemat : Comp
        {
            public List<double> det = new List<double>(), tt = new List<double>(); public bool scale = true;
            public override void Run(double dt)
            {
                double dt0 = dt; input = inputs[0](); if (scale) input *= det[det.Count - 1];
                if (outs.Count > 0) output = outs[0].v;
                input = Math.Max(det[0], Math.Min(det[det.Count - 1], input));
                while (dt0 > 0 && Math.Abs(input - output) > 1e-9 * Math.Max(1, Math.Abs(input)))
                {
                    int ind = 1; while (ind < det.Count && (input < output ? det[ind] < output : det[ind] <= output)) ind++;
                    if (ind >= det.Count) ind = det.Count - 1;
                    if (tt[ind] <= 0) { output = input; break; }
                    double rate = (det[ind] - det[ind - 1]) / tt[ind], thisIn = Math.Max(det[ind - 1], Math.Min(det[ind], input)), thisDt = Math.Abs((thisIn - output) / rate);
                    if (dt0 < thisDt) { thisDt = dt0; if (output < input) output += thisDt * rate; else output -= thisDt * rate; } else output = thisIn;
                    dt0 -= thisDt;
                }
                Clip(); Set();
            }
        }
        sealed class Filter : Comp
        {
            public string type; public Ev[] C = new Ev[7]; bool init = true; double pi1, pi2, po1, po2;
            public override void Run(double dt)
            {
                double c1 = C[1]?.Invoke() ?? 0, c2 = C[2]?.Invoke() ?? 0, c3 = C[3]?.Invoke() ?? 0, c4 = C[4]?.Invoke() ?? 0, c5 = C[5]?.Invoke() ?? 0, c6 = C[6]?.Invoke() ?? 0;
                if (init) { po2 = pi2 = po1 = pi1 = output = input; init = false; }
                else
                {
                    input = inputs[0]();
                    switch (type)
                    {
                        case "lag_filter": { double den = 2 + dt * c1, ca = dt * c1 / den, cb = (2 - dt * c1) / den; output = (input + pi1) * ca + po1 * cb; break; }
                        case "lead_lag_filter": { double den = 2 * c3 + dt * c4, ca = (2 * c1 + dt * c2) / den, cb = (dt * c2 - 2 * c1) / den, cc = (2 * c3 - dt * c4) / den; output = input * ca + pi1 * cb + po1 * cc; break; }
                        case "washout_filter": { double den = 2 + dt * c1, ca = 2 / den, cb = (2 - dt * c1) / den; output = input * ca - pi1 * ca + po1 * cb; break; }
                        case "second_order_filter":
                            {
                                double den = 4 * c4 + 2 * c5 * dt + c6 * dt * dt, ca = (4 * c1 + 2 * c2 * dt + c3 * dt * dt) / den, cb = (2 * c3 * dt * dt - 8 * c1) / den, cc = (4 * c1 - 2 * c2 * dt + c3 * dt * dt) / den, cd = (2 * c6 * dt * dt - 8 * c4) / den, ce = (4 * c4 - 2 * c5 * dt + c6 * dt * dt) / den;
                                output = input * ca + pi1 * cb + pi2 * cc - po1 * cd - po2 * ce; break;
                            }
                    }
                }
                po2 = po1; po1 = output; pi2 = pi1; pi1 = input; Clip(); Set();
            }
        }
        sealed class Integrator : Comp
        {
            public Ev c1; public int mode; double pi, pO; bool first = true; // mode 0 rect, 1 trap(default JSBSim = trapezoidal? 0 rect,1 trap,2 AB2,3 AB3)
            double pi2, pi3;
            public override void Run(double dt)
            {
                input = inputs[0]() * c1();
                if (first) { pi = pi2 = pi3 = input; first = false; }
                switch (mode) { case 0: output += input * dt; break; case 1: output += 0.5 * dt * (input + pi); break; case 2: output += dt / 2 * (3 * input - pi); break; default: output += dt / 12 * (23 * input - 16 * pi + 5 * pi2); break; }
                pi3 = pi2; pi2 = pi; pi = input; Clip(); Set();
            }
        }
        sealed class Pid : Comp
        {
            public Ev kp, ki, kd, pvdot, trigger; public int mode = 2; public bool standard; double prev, prev2, iTotal;
            public override void Run(double dt)
            {
                input = inputs[0](); double dval = pvdot != null ? pvdot() : (input - prev) / dt, test = trigger != null ? trigger() : 0, delta = 0;
                if (Math.Abs(test) >= 1e-6 ? false : true)
                    switch (mode) { case 0: delta = input; break; case 1: delta = 0.5 * (input + prev); break; case 2: delta = 1.5 * input - 0.5 * prev; break; default: delta = (23 * input - 16 * prev + 5 * prev2) / 12.0; break; }
                if (test < 0) iTotal = 0;
                iTotal += ki() * dt * delta;
                output = standard ? kp() * (input + iTotal + kd() * dval) : kp() * input + iTotal + kd() * dval;
                prev2 = test < 0 ? 0 : prev; prev = input; Clip(); Set();
            }
        }
        sealed class Deadband : Comp { public Ev width; public double gain = 1; public override void Run(double dt) { input = inputs[0](); double h = 0.5 * width(); output = input < -h ? (input + h) * gain : input > h ? (input - h) * gain : 0; Clip(); Set(); } }
        sealed class FcsFunc : Comp { public Ev f; public override void Run(double dt) { output = f(); Clip(); Set(); } }
        sealed class Switch : Comp
        {
            public List<Func<bool>> tests = new List<Func<bool>>(); public List<Ev> vals = new List<Ev>(); public Ev def;
            public override void Run(double dt) { output = def != null ? def() : 0; for (int i = 0; i < tests.Count; i++) if (tests[i]()) { output = vals[i](); break; } Clip(); Set(); }
        }
        sealed class Sensor : Comp  // no noise / drift / quantization: lag only
        {
            public Ev lag; double prev; bool first = true;
            public override void Run(double dt)
            {
                input = inputs[0](); double l = lag != null ? lag() : 0;
                if (first || l <= 0) output = input; else { double ca = dt * l / (2 + dt * l), cb = (2 - dt * l) / (2 + dt * l); output = (input + prev) * ca + output * cb; }
                prev = input; first = false; Clip(); Set();
            }
        }
        sealed class Actuator : Comp  // lag, rate limit, bias; no hysteresis / deadband
        {
            public Ev lag, rate; public double bias; double prevIn; bool first = true;
            public override void Run(double dt)
            {
                input = inputs[0]();
                if (first) { output = input; first = false; prevIn = input; }
                double v = input;
                if (lag != null) { double l = lag(); if (l > 0) { double ca = dt * l / (2 + dt * l), cb = (2 - dt * l) / (2 + dt * l); v = (input + prevIn) * ca + output * cb; } }
                if (rate != null) { double r = rate(), d = v - output; v = output + Math.Max(-r * dt, Math.Min(r * dt, d)); }
                prevIn = input; output = v; Clip(); Set();
            }
        }

        readonly List<Comp> _fcs = new List<Comp>();
        string PropName(string n) => n.Contains("/") ? n : "fcs/" + n.ToLowerInvariant().Replace(' ', '-');

        void AddComponent(XmlElement e)
        {
            Comp c; string n = e.Name;
            switch (n)
            {
                case "summer": { var s = new Summer(); var b = e.SelectSingleNode("bias"); if (b != null) s.bias = double.Parse(b.InnerText.Trim(), Inv); c = s; break; }
                case "pure_gain": case "scheduled_gain": case "aerosurface_scale":
                    {
                        var g = new Gain(); var ge = e.SelectSingleNode("gain"); g.gain = ge != null ? Value(ge.InnerText) : (() => 1.0);
                        if (n == "aerosurface_scale")
                        {
                            g.aero = true; var d = e.SelectSingleNode("domain"); if (d != null && d.SelectSingleNode("max") != null && d.SelectSingleNode("min") != null) { g.inMax = double.Parse(d.SelectSingleNode("max").InnerText, Inv); g.inMin = double.Parse(d.SelectSingleNode("min").InnerText, Inv); }
                            var r = e.SelectSingleNode("range"); g.outMax = double.Parse(r.SelectSingleNode("max").InnerText, Inv); g.outMin = double.Parse(r.SelectSingleNode("min").InnerText, Inv);
                            var z = e.SelectSingleNode("zero_centered"); if (z != null) { var t = z.InnerText.Trim(); if (t == "0" || t == "false") g.zc = false; }
                        }
                        if (n == "scheduled_gain") g.sched = Table((XmlElement)e.SelectSingleNode("table"));
                        c = g; break;
                    }
                case "kinematic":
                    {
                        var k = new Kinemat(); foreach (XmlElement s in e.SelectNodes("traverse/setting")) { k.det.Add(double.Parse(s.SelectSingleNode("position").InnerText, Inv)); k.tt.Add(double.Parse(s.SelectSingleNode("time").InnerText, Inv)); }
                        if (e.SelectSingleNode("noscale") != null) k.scale = false; c = k; break;
                    }
                case "lag_filter": case "lead_lag_filter": case "washout_filter": case "second_order_filter":
                    { var f = new Filter { type = n }; for (int i = 1; i <= 6; i++) { var x = e.SelectSingleNode("c" + i); if (x != null) f.C[i] = Value(x.InnerText); } c = f; break; }
                case "integrator":
                    {
                        var ig = new Integrator { c1 = e.SelectSingleNode("c1") != null ? Value(e.SelectSingleNode("c1").InnerText) : (() => 1.0) };
                        var t = e.SelectSingleNode("c1") as XmlElement; if (t != null && t.GetAttribute("type").Length > 0) { var ty = t.GetAttribute("type"); ig.mode = ty == "RECT" ? 0 : ty == "TRAP" ? 1 : ty == "AB2" ? 2 : 3; } else ig.mode = 1;
                        var ty2 = e.SelectSingleNode("c1")?.Attributes?["type"]; c = ig; break;
                    }
                case "pid":
                    {
                        var pd = new Pid { standard = e.GetAttribute("type") == "standard", kp = e.SelectSingleNode("kp") != null ? Value(e.SelectSingleNode("kp").InnerText) : (() => 0.0), ki = e.SelectSingleNode("ki") != null ? Value(e.SelectSingleNode("ki").InnerText) : (() => 0.0), kd = e.SelectSingleNode("kd") != null ? Value(e.SelectSingleNode("kd").InnerText) : (() => 0.0) };
                        var kie = e.SelectSingleNode("ki") as XmlElement; if (kie != null) { string ty = kie.GetAttribute("type"); pd.mode = ty == "rect" ? 0 : ty == "trap" ? 1 : ty == "ab3" ? 3 : 2; }
                        if (e.SelectSingleNode("pvdot") != null) pd.pvdot = Value(e.SelectSingleNode("pvdot").InnerText); if (e.SelectSingleNode("trigger") != null) pd.trigger = Value(e.SelectSingleNode("trigger").InnerText);
                        c = pd; break;
                    }
                case "deadband": { var d = new Deadband { width = e.SelectSingleNode("width") != null ? Value(e.SelectSingleNode("width").InnerText) : (() => 0.0) }; if (e.SelectSingleNode("gain") != null) d.gain = double.Parse(e.SelectSingleNode("gain").InnerText, Inv); c = d; break; }
                case "fcs_function": c = new FcsFunc { f = Func((XmlElement)e.SelectSingleNode("function")) }; break;
                case "switch":
                    {
                        var s = new Switch();
                        foreach (XmlElement t in e.ChildNodes.OfType())
                        {
                            if (t.Name == "default") s.def = Value(t.GetAttribute("value"));
                            else if (t.Name == "test") { s.tests.Add(Cond(t)); s.vals.Add(Value(t.GetAttribute("value"))); }
                        }
                        c = s; break;
                    }
                case "sensor": { var s = new Sensor(); var l = e.SelectSingleNode("lag"); if (l != null) s.lag = Value(l.InnerText); c = s; break; }
                case "actuator":
                    {
                        var a = new Actuator(); var l = e.SelectSingleNode("lag"); if (l != null) a.lag = Value(l.InnerText); var r = e.SelectSingleNode("rate_limit"); if (r != null) a.rate = Value(r.InnerText);
                        var b = e.SelectSingleNode("bias"); if (b != null) a.bias = double.Parse(b.InnerText, Inv); c = a; break;
                    }
                default: throw new FormatException("unsupported FCS component <" + n + ">");
            }
            c.fdm = this; c.name = e.GetAttribute("name");
            foreach (XmlElement i in e.SelectNodes("input")) c.inputs.Add(Value(i.InnerText));
            foreach (XmlElement o in e.SelectNodes("output")) c.outs.Add(Cell(o.InnerText));
            var clipE = e.SelectSingleNode("clipto"); if (clipE != null) { c.clip = true; c.cmin = Value(clipE.SelectSingleNode("min").InnerText); c.cmax = Value(clipE.SelectSingleNode("max").InnerText); }
            { var self = Cell(PropName(c.name)); c.outs.Add(self); }
            // component output (own name) first, extra <output>s after; kinematic reads its own output back (index 0)
            foreach (var o in c.outs) { string key = null; foreach (var kv in _p) if (kv.Value == o) { key = kv.Key; break; } if (key != null && (key.EndsWith("-pos-rad") || key.EndsWith("-pos-deg")) && key.StartsWith("fcs/") && !key.StartsWith("fcs/mag-")) _mirrorBase[o] = key.Substring(4); }
            _fcs.Add(c);
        }

        // ---------------------------------------------------------------- loading
        public string name;
        public double wingArea, wingSpan, chord, htailArea, htailArm, vtailArea, vtailArm;
        public D3 aeroRpIn, cgIn, eyeIn;
        public double emptyWt, ixx, iyy, izz, ixz, ixy, iyz; public bool negatedProducts;
        readonly List<(D3 loc, double w)> _pointMasses = new List<(D3, double)>();
        public sealed class Tank { public D3 loc; public double capacity, contents; public string fuelType; }
        public List<Tank> tanks = new List<Tank>();
        public List<JsbGear> gears = new List<JsbGear>();
        public List<JsbEngine> engines = new List<JsbEngine>();
        sealed class Axis { public List<Ev> fns = new List<Ev>(); }
        readonly Axis[] _axes = new Axis[6]; static readonly string[] AxisNames = { "LIFT", "DRAG", "SIDE", "ROLL", "PITCH", "YAW" };
        public sealed class ExtForce { public string name; public Ev mag; public D3 locIn, dir; public Ev dx, dy, dz; public bool isMoment; }
        public List<ExtForce> extForces = new List<ExtForce>();
        public Func<string, string> readSystem;      // resolves a <system file="x"/> include, returns its XML text
        public Func<string, string> readEngine;      // resolves <engine file="x"/>, returns engine XML text
        public Func<string, string> readThruster;    // resolves <thruster file="x"/>

        static double Num(XmlNode p, string tag, string defUnit = null)
        {
            var e = p.SelectSingleNode(tag) as XmlElement; if (e == null) throw new FormatException("missing <" + tag + ">");
            return Conv(double.Parse(e.InnerText.Trim(), Inv), e.GetAttribute("unit"), defUnit);
        }
        public static double Conv(double v, string unit, string def)
        {
            switch (string.IsNullOrEmpty(unit) ? def : unit)
            {
                case null: case "": case "FT": case "FT2": case "LBS": case "SLUG*FT2": case "RAD": case "LBS/FT": case "LBS/FT/SEC": case "WATTS": case "SLUG": return v;
                case "IN": return v / 12.0; case "DEG": return v * Deg; case "HP": return v * 745.7; case "M": return v / 0.3048; case "KG": return v * 2.20462; case "M2": return v * 10.7639;
                case "KG*M2": return v * 0.737562; case "N": return v * 0.224809; case "IN2": return v / 144.0; default: throw new FormatException("unsupported unit " + unit);
            }
        }
        public static D3 Loc(XmlNode e)       // -> structural inches
        {
            var el = (XmlElement)e; string u = el.GetAttribute("unit"); double k = u == "FT" ? 12.0 : u == "M" ? 39.3701 : 1.0;
            return new D3(Num(e, "x") * k, Num(e, "y") * k, Num(e, "z") * k);
        }

        public static JsbFdm Load(string aircraftXml, Func<string, string> engineFile, Func<string, string> systemFile, Func<string, string> thrusterFile = null)
        {
            var f = new JsbFdm { readEngine = engineFile, readSystem = systemFile, readThruster = thrusterFile ?? engineFile }; f.LoadInternal(aircraftXml); return f;
        }

        void LoadInternal(string xml)
        {
            var d = new XmlDocument(); d.LoadXml(xml); var root = d.DocumentElement; name = root.GetAttribute("name");
            for (int i = 0; i < 6; i++) _axes[i] = new Axis();
            // user-defined properties (<property value="x"> name </property>) can sit in any section: pre-declare all of them
            foreach (XmlElement pr in root.SelectNodes("//property[@value]")) Cell(pr.InnerText).v = double.Parse(pr.GetAttribute("value"), Inv);
            var m = root.SelectSingleNode("metrics");
            wingArea = Num(m, "wingarea"); wingSpan = Num(m, "wingspan"); chord = Num(m, "chord");
            htailArea = m.SelectSingleNode("htailarea") != null ? Num(m, "htailarea") : 0; htailArm = m.SelectSingleNode("htailarm") != null ? Num(m, "htailarm") : 0;
            vtailArea = m.SelectSingleNode("vtailarea") != null ? Num(m, "vtailarea") : 0; vtailArm = m.SelectSingleNode("vtailarm") != null ? Num(m, "vtailarm") : 0;
            foreach (XmlElement l in m.SelectNodes("location")) { if (l.GetAttribute("name") == "AERORP") aeroRpIn = Loc(l); else if (l.GetAttribute("name") == "EYEPOINT") eyeIn = Loc(l); }
            this["metrics/Sw-sqft"] = wingArea; this["metrics/bw-ft"] = wingSpan; this["metrics/cbarw-ft"] = chord; this["metrics/Sh-sqft"] = htailArea; this["metrics/lh-arm-ft"] = htailArm; this["metrics/Sv-sqft"] = vtailArea; this["metrics/lv-arm-ft"] = vtailArm;
            var mb = root.SelectSingleNode("mass_balance"); negatedProducts = ((XmlElement)mb).GetAttribute("negated_crossproduct_inertia") == "true";
            ixx = Num(mb, "ixx"); iyy = Num(mb, "iyy"); izz = Num(mb, "izz");
            ixz = mb.SelectSingleNode("ixz") != null ? Num(mb, "ixz") : 0; ixy = mb.SelectSingleNode("ixy") != null ? Num(mb, "ixy") : 0; iyz = mb.SelectSingleNode("iyz") != null ? Num(mb, "iyz") : 0;
            emptyWt = Num(mb, "emptywt");
            foreach (XmlElement l in mb.SelectNodes("location")) if (l.GetAttribute("name") == "CG") cgIn = Loc(l);
            foreach (XmlElement pm in mb.SelectNodes("pointmass")) _pointMasses.Add((Loc(pm.SelectSingleNode("location")), Num(pm, "weight")));
            foreach (XmlElement c in root.SelectNodes("ground_reactions/contact")) gears.Add(JsbGear.FromXml(c, gears.Count));
            // external reactions
            foreach (XmlElement fe in root.SelectNodes("external_reactions/force"))
            {
                var ef = new ExtForce { name = fe.GetAttribute("name") }; var fn = fe.SelectSingleNode("function") as XmlElement;
                ef.mag = fn != null ? Func(fn) : Value(fe.GetAttribute("name") + "-magnitude");
                var lo = fe.SelectSingleNode("location"); ef.locIn = lo != null ? Loc(lo) : cgIn;
                var di = fe.SelectSingleNode("direction");
                if (di != null) { foreach (var ax in new[] { "x", "y", "z" }) { var el = di.SelectSingleNode(ax) as XmlElement; Ev ev = el != null ? Value(el.InnerText) : (() => 0.0); if (ax == "x") ef.dx = ev; else if (ax == "y") ef.dy = ev; else ef.dz = ev; } }
                extForces.Add(ef);
            }
            foreach (XmlElement me in root.SelectNodes("external_reactions/moment"))
            {
                var ef = new ExtForce { name = me.GetAttribute("name"), isMoment = true, locIn = cgIn }; ef.mag = Func((XmlElement)me.SelectSingleNode("function"));
                var di = me.SelectSingleNode("direction");
                foreach (var ax in new[] { "x", "y", "z" }) { var el = di.SelectSingleNode(ax) as XmlElement; Ev ev = el != null ? Value(el.InnerText) : (() => 0.0); if (ax == "x") ef.dx = ev; else if (ax == "y") ef.dy = ev; else ef.dz = ev; }
                extForces.Add(ef);
            }
            // propulsion
            var pro = root.SelectSingleNode("propulsion");
            if (pro != null)
            {
                foreach (XmlElement t in pro.SelectNodes("tank")) { var tk = new Tank { loc = Loc(t.SelectSingleNode("location")), capacity = Num(t, "capacity"), contents = Num(t, "contents") }; tanks.Add(tk); }
                foreach (XmlElement en in pro.SelectNodes("engine")) { var eng = JsbEngine.Load(this, en, engines.Count); engines.Add(eng); }
            }
            // flight control (inline channels, then <system> includes in document order after propulsion)
            foreach (XmlElement fc in root.SelectNodes("flight_control")) foreach (XmlElement ch in fc.SelectNodes("channel")) foreach (XmlElement comp in ch.ChildNodes.OfType()) AddComponent(comp);
            foreach (XmlElement sy in root.SelectNodes("system"))
            {
                XmlElement sroot = sy;
                if (sy.GetAttribute("file").Length > 0) { var sd = new XmlDocument(); sd.LoadXml(readSystem(sy.GetAttribute("file"))); sroot = sd.DocumentElement; foreach (XmlElement pr in sroot.SelectNodes("//property[@value]")) Cell(pr.InnerText).v = double.Parse(pr.GetAttribute("value"), Inv); }
                foreach (XmlElement ch in sroot.SelectNodes("channel")) foreach (XmlElement comp in ch.ChildNodes.OfType()) AddComponent(comp);
            }
            // aerodynamics
            var ae = root.SelectSingleNode("aerodynamics");
            if (ae != null)
            {
                foreach (XmlElement fn in ae.SelectNodes("function")) Func(fn);          // axis-independent helpers publish their value property when evaluated: register for pre-evaluation
                foreach (XmlElement fn in ae.SelectNodes("function")) _preAero.Add(Func(fn));
                foreach (XmlElement ax in ae.SelectNodes("axis"))
                {
                    int i = Array.IndexOf(AxisNames, ax.GetAttribute("name")); if (i < 0) throw new FormatException("axis " + ax.GetAttribute("name"));
                    foreach (XmlElement fn in ax.SelectNodes("function")) _axes[i].fns.Add(Func(fn));
                }
            }
            // top-level <property> / <function> blocks inside <system> were handled; engine-level pre-declared properties are done in JsbEngine
            
            ComputeMass();
            foreach (var g in gears) { g.Bind(this); }
            Cell("gear/gear-cmd-norm"); this["gear/gear-pos-norm"] = 1.0;
        }
        /// <summary>JSBSim propulsion/set-running = -1: all engines running at the N1 / N2 of throttle <paramref name="throttle"/>.</summary>
        public void StartEngines(double throttle) { foreach (var e in engines) e.SetRunning(throttle); }
        readonly List<Ev> _preAero = new List<Ev>();

        // ---------------------------------------------------------------- mass and balance
        public double massSlug; public D3 cgNowIn;
        public double WeightLbs => massSlug * G0;
        public void ComputeMass()
        {
            double w = emptyWt; D3 mom = cgIn * emptyWt;
            foreach (var p in _pointMasses) { w += p.w; mom = mom + p.loc * p.w; }
            foreach (var t in tanks) { w += t.contents; mom = mom + t.loc * t.contents; }
            massSlug = w / G0; cgNowIn = mom * (1.0 / w);
            this["inertia/weight-lbs"] = w; this["inertia/mass-slugs"] = massSlug; this["inertia/cg-x-in"] = cgNowIn.x; this["inertia/cg-y-in"] = cgNowIn.y; this["inertia/cg-z-in"] = cgNowIn.z;
            double tot = 0; foreach (var t in tanks) tot += t.contents; this["propulsion/total-fuel-lbs"] = tot;
        }
        /// <summary>structural (in, x aft / y right / z up) -> body (ft, x fwd / y right / z down) about the CURRENT cg (FGMassBalance::StructuralToBody).</summary>
        public D3 StructuralToBody(D3 r) => new D3(FtPerIn * (cgNowIn.x - r.x), FtPerIn * (r.y - cgNowIn.y), FtPerIn * (cgNowIn.z - r.z));

        // ---------------------------------------------------------------- state in / results out
        public struct State
        {
            public D3 uvw, pqr, uvwDot;           // body, ft/s, rad/s, ft/s2
            public double altSlFt, aglFt, phi, theta, psi;
            public D3 windNedFps;                  // optional steady wind (north, east, down)
        }
        public struct Result
        {
            public D3 force, moment;               // lbf, lbf*ft body about CG: aero + propulsion + external reactions
            public D3 aeroForce, aeroMoment, propForce, propMoment, extForce, extMoment;
            public double alpha, beta, qbar, mach, rho, vcKts, vtFps;
        }
        public double dt = 1.0 / 120.0;

        /// <summary>One JSBSim frame: atmosphere + auxiliary, FCS, propulsion, aerodynamics, external reactions. Gear is handled by the caller via <see cref="gears"/>.</summary>
        public Result Step(in State s, double dtSec)
        {
            dt = dtSec; var r = new Result();
            JsbAtmosphere.At(s.altSlFt, out double tempR, out double press, out double rho, out double a);
            // wind: aerodynamic velocity = body velocity - wind in body axes
            D3 uvwA = s.uvw;
            if (s.windNedFps.x != 0 || s.windNedFps.y != 0 || s.windNedFps.z != 0) uvwA = s.uvw - NedToBody(s, s.windNedFps);
            double u2 = uvwA.x * uvwA.x, v2 = uvwA.y * uvwA.y, w2 = uvwA.z * uvwA.z, uw = u2 + w2, vt = Math.Sqrt(uw + v2);
            double alpha = 0, beta = 0, adot = 0, bdot = 0;
            if (vt > 0.001) { beta = Math.Atan2(uvwA.y, Math.Sqrt(uw)); if (uw >= 1e-6) { alpha = Math.Atan2(uvwA.z, uvwA.x); adot = (uvwA.x * s.uvwDot.z - uvwA.z * s.uvwDot.x) / uw; double mUW = uw; bdot = (uvwA.y * 0 + 0) * 0; double denom = vt * vt * Math.Sqrt(uw); } }
            if (vt > 0.001 && uw >= 1e-6) { double mUW = uw, vtv = vt; bdot = (s.uvwDot.y * mUW - uvwA.y * (uvwA.x * s.uvwDot.x + uvwA.z * s.uvwDot.z)) / (vtv * vtv * Math.Sqrt(mUW)); }
            double mach = vt / a, qbar = 0.5 * rho * vt * vt;
            double sinPhi = Math.Sin(s.phi), cosPhi = Math.Cos(s.phi), sinT = Math.Sin(s.theta), cosT = Math.Cos(s.theta), sinP = Math.Sin(s.psi), cosP = Math.Cos(s.psi);
            // Tb2l (body -> local NED); local-z (down) of the AERORP
            D3 rp = StructuralToBody(aeroRpIn);
            double zl = -sinT * rp.x + sinPhi * cosT * rp.y + cosPhi * cosT * rp.z;
            double rhoRatio = rho / RhoSL, tat = tempR * (1 + 0.2 * mach * mach);
            SetL("aero/alpha-rad", alpha); SetL("aero/alpha-deg", alpha / Deg); SetL("aero/beta-rad", beta); SetL("aero/beta-deg", beta / Deg); SetL("aero/mag-beta-rad", Math.Abs(beta));
            SetL("aero/alphadot-rad_sec", adot); SetL("aero/betadot-rad_sec", bdot); SetL("aero/alphadot-deg_sec", adot / Deg); SetL("aero/betadot-deg_sec", bdot / Deg);
            SetL("aero/qbar-psf", qbar); SetL("aero/qbarUW-psf", 0.5 * rho * uw); SetL("aero/qbarUV-psf", 0.5 * rho * (u2 + v2));
            SetL("aero/bi2vel", vt > 0 ? wingSpan / (2 * vt) : 0); SetL("aero/ci2vel", vt > 0 ? chord / (2 * vt) : 0); SetL("aero/h_b-cg-ft", s.aglFt / wingSpan); SetL("aero/h_b-mac-ft", (s.aglFt - zl) / wingSpan);
            SetL("aero/Re", rho * vt * chord / 3.7373e-7);
            SetL("velocities/mach", mach); SetL("velocities/vt-fps", vt); Set("velocities/u-fps", s.uvw.x); Set("velocities/v-fps", s.uvw.y); Set("velocities/w-fps", s.uvw.z);
            SetL("velocities/u-aero-fps", uvwA.x); SetL("velocities/v-aero-fps", uvwA.y); SetL("velocities/w-aero-fps", uvwA.z);
            Set("velocities/p-rad_sec", s.pqr.x); Set("velocities/q-rad_sec", s.pqr.y); Set("velocities/r-rad_sec", s.pqr.z);
            SetL("velocities/p-aero-rad_sec", s.pqr.x); Set("velocities/q-aero-rad_sec", s.pqr.y); Set("velocities/r-aero-rad_sec", s.pqr.z);
            double qcalc = Math.Max(0, PitotTotal(mach, press) - press); double vc = a * 0; // calibrated airspeed (standard sea level)
            double vcas = CalibratedFps(qcalc); SetL("velocities/vc-fps", vcas); SetL("velocities/vc-kts", vcas / 1.68781); SetL("velocities/vt-kts", vt / 1.68781);
            SetL("velocities/ve-fps", vt * Math.Sqrt(rhoRatio)); SetL("velocities/ve-kts", vt * Math.Sqrt(rhoRatio) / 1.68781);
            double hdot = (-(-sinT * s.uvw.x + sinPhi * cosT * s.uvw.y + cosPhi * cosT * s.uvw.z)); SetL("velocities/h-dot-fps", hdot);
            Set("attitude/phi-rad", s.phi); Set("attitude/theta-rad", s.theta); Set("attitude/psi-rad", s.psi);
            Set("attitude/roll-rad", s.phi); Set("attitude/pitch-rad", s.theta); Set("attitude/heading-true-rad", s.psi);
            Set("position/h-sl-ft", s.altSlFt); Set("position/h-agl-ft", s.aglFt); Set("position/terrain-elevation-asl-ft", s.altSlFt - s.aglFt);
            Set("atmosphere/T-R", tempR); Set("atmosphere/P-psf", press); Set("atmosphere/rho-slugs_ft3", rho); Set("atmosphere/a-fps", a); Set("atmosphere/density-altitude", DensityAltitude(rho));
            Set("atmosphere/rho-sl-slugs_ft3", RhoSL); Set("atmosphere/theta", tempR / 518.67); Set("atmosphere/sigma", rhoRatio); Set("atmosphere/delta", press / SlPress); Set("atmosphere/pressure-altitude", s.altSlFt);
            Set("atmosphere/P-sl-psf", SlPress); Set("atmosphere/T-sl-R", 518.67); SetL("propulsion/tat-r", tat);
            SetL("accelerations/udot-ft_sec2", s.uvwDot.x); SetL("accelerations/vdot-ft_sec2", s.uvwDot.y); SetL("accelerations/wdot-ft_sec2", s.uvwDot.z);
            SetL("accelerations/Nz", -(s.uvwDot.z) / G0 ); SetL("accelerations/n-pilot-z-norm", 1.0);
            Set("simulation/sim-time-sec", this["simulation/sim-time-sec"] + dtSec); Set("simulation/dt", dtSec);
            Set("inertia/weight-lbs", WeightLbs);

            // --- FCS (copy commanded throttle to position first, like FGFCS::Run)
            for (int i = 0; i < engines.Count; i++) Cell("fcs/throttle-pos-norm[" + i + "]").v = Cell("fcs/throttle-cmd-norm[" + i + "]").v;
            foreach (var c in _fcs) c.Run(dtSec);
            foreach (var kv in _late) Cell(kv.Key).v = kv.Value;    // Auxiliary runs AFTER the FCS in JSBSim: flight controls see the previous frame's aero/* values
            // --- propulsion
            var ctx = new JsbEngine.Ctx { dt = dtSec, qbar = qbar, rhoRatio = rhoRatio, tempR = tempR, tatC = (tat - 491.67) / 1.8, uvw = uvwA, pqr = s.pqr, mach = mach };
            D3 pF = new D3(), pM = new D3(), cg = cgNowIn;
            foreach (var en in engines) { en.Run(ctx); D3 f = en.BodyForce(); pF = pF + f; pM = pM + D3.Cross(StructuralToBody(en.actingIn), f) + en.moment; }
            foreach (var en in engines) en.Burn(dtSec);
            ComputeMass();
            Set("propulsion/total-fuel-lbs", this["propulsion/total-fuel-lbs"]);
            // --- aerodynamics (wind axes forces, body axes moments about the aero reference point)
            foreach (var f in _preAero) f();
            double lift = 0, drag = 0, side = 0, roll = 0, pitch = 0, yaw = 0;
            foreach (var f in _axes[0].fns) lift += f(); foreach (var f in _axes[1].fns) drag += f(); foreach (var f in _axes[2].fns) side += f();
            foreach (var f in _axes[3].fns) roll += f(); foreach (var f in _axes[4].fns) pitch += f(); foreach (var f in _axes[5].fns) yaw += f();
            double ca = Math.Cos(alpha), sa = Math.Sin(alpha), cb = Math.Cos(beta), sb = Math.Sin(beta);
            D3 fw = new D3(-drag, side, -lift);
            D3 fb = new D3(ca * cb * fw.x - ca * sb * fw.y - sa * fw.z, sb * fw.x + cb * fw.y, sa * cb * fw.x - sa * sb * fw.y + ca * fw.z);
            D3 aM = new D3(roll, pitch, yaw) + D3.Cross(StructuralToBody(aeroRpIn), fb);
            // --- external reactions (BODY frame, direction normalised like FGExternalForce)
            D3 eF = new D3(), eM = new D3();
            foreach (var ef in extForces)
            {
                double mg = ef.mag(); D3 dir = new D3(ef.dx(), ef.dy(), ef.dz()); double dl = dir.Length; if (dl > 0) dir = dir * (1.0 / dl);
                D3 f = dir * mg; if (ef.isMoment) { eM = eM + f; continue; } eF = eF + f; eM = eM + D3.Cross(StructuralToBody(ef.locIn), f);
            }
            r.aeroForce = fb; r.aeroMoment = aM; r.propForce = pF; r.propMoment = pM; r.extForce = eF; r.extMoment = eM;
            r.force = fb + pF + eF; r.moment = aM + pM + eM; r.alpha = alpha; r.beta = beta; r.qbar = qbar; r.mach = mach; r.rho = rho; r.vcKts = vcas / 1.68781; r.vtFps = vt;
            return r;
        }
        void Set(string n, double v) { Cell(n).v = v; }
        readonly Dictionary<string, double> _late = new Dictionary<string, double>();
        void SetL(string n, double v) { _late[Norm(n)] = v; }
        static D3 NedToBody(in State s, D3 n)
        {
            double sp = Math.Sin(s.phi), cp = Math.Cos(s.phi), st = Math.Sin(s.theta), ct = Math.Cos(s.theta), ss = Math.Sin(s.psi), cs = Math.Cos(s.psi);
            return new D3(ct * cs * n.x + ct * ss * n.y - st * n.z, (sp * st * cs - cp * ss) * n.x + (sp * st * ss + cp * cs) * n.y + sp * ct * n.z, (cp * st * cs + sp * ss) * n.x + (cp * st * ss - sp * cs) * n.y + cp * ct * n.z);
        }
        static double DensityAltitude(double rho)
        {
            double lo = -20000, hi = 120000;   // invert rho(h) by bisection (rho decreases with h)
            for (int i = 0; i < 60; i++) { double mid = 0.5 * (lo + hi); JsbAtmosphere.At(mid, out _, out _, out double r, out _); if (r > rho) lo = mid; else hi = mid; }
            return 0.5 * (lo + hi);
        }
        public static double PitotTotal(double mach, double p)
        {
            const double g = JsbAtmosphere.SHRatio, aa = (g - 1) / 2, b = g / (g - 1), c = 2 * b, dd = 1 / (g - 1);
            double coeff = Math.Pow(0.5 * (g + 1), b) * Math.Pow((g + 1) / (g - 1), dd);
            if (mach < 0) return p; if (mach < 1) return p * Math.Pow(1 + aa * mach * mach, b);
            return p * coeff * Math.Pow(mach, c) / Math.Pow(c * mach * mach - 1, dd);
        }
        static double CalibratedFps(double qc)
        {
            const double g = JsbAtmosphere.SHRatio; double a0 = Math.Sqrt(g * (JsbAtmosphere.Rstar / JsbAtmosphere.Mair) * 518.67);
            double A = qc / SlPress + 1, bb = (g - 1) / g, aa = 2 / (g - 1), M = Math.Sqrt(aa * (Math.Pow(A, bb) - 1));
            if (M > 1) { double c = 2 / bb, d = 0.5 * aa, coeff = Math.Pow(0.5 * (g + 1), -0.25 * c) * Math.Pow(0.5 * (g + 1) / g, -0.5 * d); for (int i = 0; i < 10; i++) M = coeff * Math.Sqrt(A * Math.Pow(1 - 1.0 / (c * M * M), d)); }
            return M * a0;
        }
    }

    static class XmlExt { public static IEnumerable<XmlElement> OfType(this XmlNodeList l) { foreach (XmlNode n in l) if (n is XmlElement e) yield return e; } }
}
