// Replays tools/fpv/golden/*.json (made by gen_golden.mts from the ORIGINAL TypeScript model) on the C# port and compares.
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using RocketWorld.Sim.Fpv;

class HalfSpace : IFpvWorld
{
    readonly double nx, ny, nz, d;
    public HalfSpace(double nx, double ny, double nz, double d) { this.nx = nx; this.ny = ny; this.nz = nz; this.d = d; }
    public double Sweep(double[] c0, double[] c1, double[] r, double[] pad, int n, ref FpvContact o)
    {
        double best = -1;
        for (int i = 0; i < n; i++)
        {
            double R = r[i] + pad[i];
            double a = nx * c0[i * 3] + ny * c0[i * 3 + 1] + nz * c0[i * 3 + 2] - d - R;
            double b = nx * c1[i * 3] + ny * c1[i * 3 + 1] + nz * c1[i * 3 + 2] - d - R;
            double t = -1;
            if (a <= 0) t = 0; else if (b < 0) t = a / (a - b);
            if (t >= 0 && (best < 0 || t < best)) { best = t; o.sphere = i; o.nx = nx; o.ny = ny; o.nz = nz; }
        }
        return best;
    }
    public bool PushOut(double x, double y, double z, double r, out V3d o)
    {
        o = default;
        double dist = nx * x + ny * y + nz * z - d - r;
        if (dist >= 0) return false;
        o.x = -nx * dist; o.y = -ny * dist; o.z = -nz * dist; return true;
    }
}

class Union : IFpvWorld
{
    readonly IFpvWorld a, b;
    public Union(IFpvWorld a, IFpvWorld b) { this.a = a; this.b = b; }
    public double Sweep(double[] c0, double[] c1, double[] r, double[] pad, int n, ref FpvContact o)
    {
        double ta = a.Sweep(c0, c1, r, pad, n, ref o);
        var na = o; var tmp = new FpvContact();
        double tb = b.Sweep(c0, c1, r, pad, n, ref tmp);
        if (tb >= 0 && (ta < 0 || tb < ta)) { o = tmp; return tb; }
        if (ta >= 0) o = na;
        return ta;
    }
    public bool PushOut(double x, double y, double z, double r, out V3d o) => a.PushOut(x, y, z, r, out o) || b.PushOut(x, y, z, r, out o);
}

static class P
{
    static IFpvWorld World(JsonElement w)
    {
        if (w.ValueKind == JsonValueKind.Null) return null;
        string k = w.GetProperty("kind").GetString();
        if (k == "floor") return new HalfSpace(0, 1, 0, w.GetProperty("y").GetDouble());
        if (k == "wall") return new HalfSpace(-1, 0, 0, -w.GetProperty("x").GetDouble());
        return new Union(new HalfSpace(0, 1, 0, w.GetProperty("y").GetDouble()), new HalfSpace(-1, 0, 0, -w.GetProperty("x").GetDouble()));
    }

    static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "../../../golden");
        int fails = 0;
        foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(x => x))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var root = doc.RootElement; var sc = root.GetProperty("scenario");
            var ov = new FpvOverrides();
            var o = sc.GetProperty("overrides");
            if (o.TryGetProperty("gravity", out var g)) ov.gravity = g.GetDouble();
            if (o.TryGetProperty("gravityMode", out var gm)) ov.gravityMode = GravityMode.Honest;
            var prm = FpvCompile.Compile(FpvPresets.Get(sc.GetProperty("preset").GetString()), ov);
            var sim = new FpvSim(prm, World(sc.GetProperty("world")));
            var st = sc.GetProperty("start");
            sim.Reset(st[0].GetDouble(), st[1].GetDouble(), st[2].GetDouble(), st[3].GetDouble());
            int every = sc.GetProperty("every").GetInt32();
            var samples = root.GetProperty("samples").EnumerateArray().ToArray();
            int si = 0, tick = 0; double worst = 0; int worstSlot = -1, worstTick = 0;
            foreach (var seg in sc.GetProperty("segs").EnumerateArray())
            {
                var ch = seg.GetProperty("ch").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                int n = seg.GetProperty("ticks").GetInt32();
                for (int i = 0; i < n; i++)
                {
                    sim.SetChannels(ch); sim.Step(); tick++;
                    if (tick % every != 0) continue;
                    var gs = samples[si++].GetProperty("s").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    for (int k = 0; k < gs.Length; k++)
                    {
                        double err = Math.Abs(gs[k] - sim.s[k]) / Math.Max(1e-3, Math.Abs(gs[k]));
                        if (err > worst) { worst = err; worstSlot = k; worstTick = tick; }
                    }
                }
            }
            var ge = root.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()).ToArray();
            var ce = sim.events.Select(e => e.type.ToString().ToLowerInvariant()).ToArray();
            bool evOk = ge.SequenceEqual(ce);
            bool ok = evOk && worst < 1e-6;
            if (!ok) fails++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {Path.GetFileNameWithoutExtension(f),-20} samples {si}  worst rel err {worst:E2} (slot {worstSlot} @tick {worstTick})  events {(evOk ? "match" : "DIFFER: ts=[" + string.Join(",", ge) + "] cs=[" + string.Join(",", ce) + "]")}");
        }
        return fails == 0 ? 0 : 1;
    }
}
