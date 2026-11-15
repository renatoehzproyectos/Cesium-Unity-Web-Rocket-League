using System;
using System.Collections.Generic;
using System.Text;

namespace RocketWorld.Sim.Roads
{
    /// <summary>
    /// Turns one OpenMapTiles vector tile (layer "transportation", as served by OpenFreeMap from OpenStreetMap) into a <see cref="RoadTileData"/>, so roads need no
    /// offline build step. Lines are clipped to the tile (the MVT buffer would otherwise double every road along tile borders), split at junctions, and every
    /// segment end gets a connector whose id is a hash of its position: neighbouring tiles and crossing roads agree on node ids without any source ids.
    /// </summary>
    public static class RoadMvt
    {
        public const string ReleasePrefix = "ofm-";

        public static RoadTileData Convert(byte[] mvt, int z, int x, int y, string release)
        {
            var d = new RoadTileData { z = z, x = x, y = y, release = release ?? "" };
            var raw = new List<RoadSegment>();
            try { Parse(mvt, z, x, y, raw); } catch (Exception) { return null; }     // truncated / not a vector tile: the caller retries later
            SplitAtJunctions(raw, d.segments);
            Finish(d.segments);
            return d;
        }

        // ------------------------------------------------------------------------------------------------ MVT parsing

        struct Feat { public int type, tagStart, tagEnd, geoStart, geoEnd; }

        static void Parse(byte[] data, int z, int x, int y, List<RoadSegment> output)
        {
            if (data == null) return;
            var r = new Reader(data, 0, data.Length);
            while (r.More)
            {
                uint tag = (uint)r.Varint(); int field = (int)(tag >> 3), wire = (int)(tag & 7);
                if (field == 3 && wire == 2) { int len = (int)r.Varint(); Layer(new Reader(data, r.Pos, r.Pos + len), z, x, y, output); r.Pos += len; }
                else r.Skip(wire);
            }
        }

        static void Layer(Reader r, int z, int x, int y, List<RoadSegment> output)
        {
            string name = null; int extent = 4096; var feats = new List<Feat>(); var keys = new List<string>(); var vals = new List<string>(); byte[] d = r.Data;
            while (r.More)
            {
                uint tag = (uint)r.Varint(); int field = (int)(tag >> 3), wire = (int)(tag & 7);
                if (wire == 2)
                {
                    int len = (int)r.Varint(); int s = r.Pos; r.Pos += len;
                    if (field == 1) name = Encoding.UTF8.GetString(d, s, len);
                    else if (field == 2) feats.Add(Feature(d, s, s + len));
                    else if (field == 3) keys.Add(Encoding.UTF8.GetString(d, s, len));
                    else if (field == 4) vals.Add(Value(d, s, s + len));
                }
                else if (wire == 0) { ulong v = r.Varint(); if (field == 5) extent = (int)v; }
                else r.Skip(wire);
            }
            if (name != "transportation") return;
            foreach (var f in feats)
            {
                if (f.type != 2) continue;
                string cls = null, sub = null, brunnel = null, surface = null, layer = null, ramp = null;
                var tr = new Reader(d, f.tagStart, f.tagEnd);
                while (tr.More)
                {
                    int k = (int)tr.Varint(); if (!tr.More) break; int v = (int)tr.Varint();
                    if (k >= keys.Count || v >= vals.Count) continue;
                    switch (keys[k])
                    {
                        case "class": cls = vals[v]; break; case "subclass": sub = vals[v]; break; case "brunnel": brunnel = vals[v]; break;
                        case "surface": surface = vals[v]; break; case "layer": layer = vals[v]; break; case "ramp": ramp = vals[v]; break;
                    }
                }
                var rc = Class(cls, sub); if (rc == RoadClass.Unknown) continue;
                var flags = RoadFlags.None; if (brunnel == "bridge") flags |= RoadFlags.Bridge; else if (brunnel == "tunnel") flags |= RoadFlags.Tunnel;
                if (ramp == "1") flags |= RoadFlags.Link;
                var surf = surface == "paved" ? RoadSurface.Paved : surface == "unpaved" ? RoadSurface.Dirt : RoadSurface.Unknown;
                int lv; sbyte level = int.TryParse(layer, out lv) ? (sbyte)Math.Max(-5, Math.Min(5, lv)) : (sbyte)0;
                foreach (var line in Decode(d, f.geoStart, f.geoEnd))
                    foreach (var piece in Clip(line, extent))
                    {
                        int n = piece.Length / 2; if (n < 2) continue;
                        var s = new RoadSegment { cls = rc, surface = surf, flags = flags, level = level, lon = new double[n], lat = new double[n] };
                        for (int i = 0; i < n; i++) ToLonLat(z, x, y, piece[2 * i] / extent, piece[2 * i + 1] / extent, out s.lon[i], out s.lat[i]);
                        output.Add(s);
                    }
            }
        }

        public static RoadClass Class(string cls, string sub)
        {
            switch (cls)
            {
                case "motorway": return RoadClass.Motorway; case "trunk": return RoadClass.Trunk; case "primary": return RoadClass.Primary;
                case "secondary": return RoadClass.Secondary; case "tertiary": return RoadClass.Tertiary;
                case "minor": return sub == "living_street" ? RoadClass.LivingStreet : sub == "unclassified" ? RoadClass.Unclassified : RoadClass.Residential;
                case "service": return RoadClass.Service; case "track": return RoadClass.Track;
                case "raceway": case "busway": return RoadClass.Other;
                case "path":
                    switch (sub) { case "pedestrian": return RoadClass.Pedestrian; case "cycleway": return RoadClass.Cycleway; case "steps": return RoadClass.Steps; case "footway": case "corridor": case "platform": return RoadClass.Footway; default: return RoadClass.Path; }
                default: return RoadClass.Unknown;       // rail, ferry, aerialway, transit, pier ...
            }
        }

        // ------------------------------------------------------------------------------------------------ geometry

        static void ToLonLat(int z, int x, int y, double fx, double fy, out double lon, out double lat)
        {
            double n = Math.Pow(2, z); lon = (x + fx) / n * 360.0 - 180.0; lat = Math.Atan(Math.Sinh(Math.PI * (1 - 2.0 * (y + fy) / n))) * 180.0 / Math.PI;
        }

        static List<double[]> Decode(byte[] d, int s, int e)
        {
            var result = new List<double[]>(); var cur = new List<double>(); var r = new Reader(d, s, e); int x = 0, y = 0;
            while (r.More)
            {
                uint cmd = (uint)r.Varint(); int id = (int)(cmd & 7), count = (int)(cmd >> 3);
                for (int i = 0; i < count; i++)
                {
                    x += Zig((uint)r.Varint()); y += Zig((uint)r.Varint());
                    if (id == 1) { if (cur.Count >= 4) result.Add(cur.ToArray()); cur.Clear(); }
                    cur.Add(x); cur.Add(y);
                }
            }
            if (cur.Count >= 4) result.Add(cur.ToArray());
            return result;
        }

        /// <summary>Clip a polyline (x,y pairs, tile units) to [0,extent]^2; the pieces that run inside are returned (a line may leave and re-enter).</summary>
        public static List<double[]> Clip(double[] p, double extent)
        {
            var res = new List<double[]>(); var cur = new List<double>();
            for (int i = 0; i + 3 < p.Length; i += 2)
            {
                double x0 = p[i], y0 = p[i + 1], x1 = p[i + 2], y1 = p[i + 3], t0 = 0, t1 = 1, dx = x1 - x0, dy = y1 - y0;
                if (!Edge(-dx, x0, ref t0, ref t1) || !Edge(dx, extent - x0, ref t0, ref t1) || !Edge(-dy, y0, ref t0, ref t1) || !Edge(dy, extent - y0, ref t0, ref t1))
                { Flush(cur, res); continue; }
                double ax = x0 + t0 * dx, ay = y0 + t0 * dy, bx = x0 + t1 * dx, by = y0 + t1 * dy;
                if (cur.Count > 0 && (t0 > 0 || cur[cur.Count - 2] != ax || cur[cur.Count - 1] != ay)) Flush(cur, res);
                if (cur.Count == 0) { cur.Add(ax); cur.Add(ay); }
                cur.Add(bx); cur.Add(by);
                if (t1 < 1) Flush(cur, res);
            }
            Flush(cur, res); return res;
        }

        static void Flush(List<double> cur, List<double[]> res) { if (cur.Count >= 4) res.Add(cur.ToArray()); cur.Clear(); }

        static bool Edge(double p, double q, ref double t0, ref double t1)      // Liang-Barsky
        {
            if (p == 0) return q >= 0;
            double t = q / p;
            if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }

        // ------------------------------------------------------------------------------------------------ graph

        static long Q(double lon, double lat) => ((long)Math.Round(lon * 1e6) << 32) ^ (uint)(int)Math.Round(lat * 1e6);
        public static uint NodeId(double lon, double lat) { long q = Q(lon, lat); uint h = 2166136261; for (int i = 0; i < 8; i++) { h ^= (byte)(q >> (8 * i)); h *= 16777619; } return h == 0 ? 1u : h; }

        /// <summary>OpenMapTiles merges ways, so a side street ends on a vertex in the middle of the main road: cut the main road there so both meet at a shared end node.</summary>
        static void SplitAtJunctions(List<RoadSegment> src, List<RoadSegment> dst)
        {
            var ends = new Dictionary<long, int>();             // positions that are the end of some segment
            foreach (var s in src) { Mark(ends, s, 0); Mark(ends, s, s.lon.Length - 1); }
            foreach (var s in src)
            {
                int start = 0;
                for (int i = 1; i < s.lon.Length - 1; i++)
                    if (ends.TryGetValue(Q(s.lon[i], s.lat[i]), out int lvl) && lvl == s.level * 2 + (s.IsBridge ? 1 : 0)) { dst.Add(Part(s, start, i)); start = i; }
                dst.Add(start == 0 ? s : Part(s, start, s.lon.Length - 1));
            }
        }
        static void Mark(Dictionary<long, int> m, RoadSegment s, int i) { m[Q(s.lon[i], s.lat[i])] = s.level * 2 + (s.IsBridge ? 1 : 0); }

        static RoadSegment Part(RoadSegment s, int a, int b)
        {
            var p = new RoadSegment { cls = s.cls, surface = s.surface, flags = s.flags, level = s.level, widthM = s.widthM, lanes = s.lanes, lon = new double[b - a + 1], lat = new double[b - a + 1] };
            Array.Copy(s.lon, a, p.lon, 0, p.lon.Length); Array.Copy(s.lat, a, p.lat, 0, p.lat.Length); return p;
        }

        static void Finish(List<RoadSegment> segs)
        {
            var used = new HashSet<uint>(); var keep = new List<RoadSegment>(segs.Count);
            foreach (var s in segs)
            {
                int n = s.lon.Length; double len = 0;
                for (int i = 1; i < n; i++) { double de = (s.lon[i] - s.lon[i - 1]) * 111320.0 * Math.Cos(s.lat[i] * Math.PI / 180.0), dn = (s.lat[i] - s.lat[i - 1]) * 110574.0; len += Math.Sqrt(de * de + dn * dn); }
                if (len < 0.5) continue;
                uint a = NodeId(s.lon[0], s.lat[0]), b = NodeId(s.lon[n - 1], s.lat[n - 1]);
                s.connectors = new[] { new RoadConnector(a, 0f), new RoadConnector(b, 1f) };
                uint id = a * 31u + b * 17u + (uint)n * 7u + (uint)s.cls; if (id == 0) id = 1; while (!used.Add(id)) id++;
                s.id = id; keep.Add(s);
            }
            segs.Clear(); segs.AddRange(keep);
        }

        // ------------------------------------------------------------------------------------------------ protobuf

        static Feat Feature(byte[] d, int s, int e)
        {
            var f = new Feat(); var r = new Reader(d, s, e);
            while (r.More)
            {
                uint tag = (uint)r.Varint(); int field = (int)(tag >> 3), wire = (int)(tag & 7);
                if (wire == 2) { int len = (int)r.Varint(); int st = r.Pos; r.Pos += len; if (field == 2) { f.tagStart = st; f.tagEnd = st + len; } else if (field == 4) { f.geoStart = st; f.geoEnd = st + len; } }
                else if (wire == 0) { ulong v = r.Varint(); if (field == 3) f.type = (int)v; }
                else r.Skip(wire);
            }
            return f;
        }

        /// <summary>Tag values come as string, float, double, int, uint, sint or bool: everything is returned as text ("1" for true).</summary>
        static string Value(byte[] d, int s, int e)
        {
            var r = new Reader(d, s, e);
            while (r.More)
            {
                uint tag = (uint)r.Varint(); int field = (int)(tag >> 3), wire = (int)(tag & 7);
                if (field == 1 && wire == 2) { int len = (int)r.Varint(); string str = Encoding.UTF8.GetString(d, r.Pos, len); r.Pos += len; return str; }
                if (wire == 0) { ulong v = r.Varint(); if (field == 6) return ((long)(v >> 1) ^ -(long)(v & 1)).ToString(); return v.ToString(); }
                r.Skip(wire);
            }
            return "";
        }

        static int Zig(uint n) => (int)(n >> 1) ^ -(int)(n & 1);

        struct Reader
        {
            public readonly byte[] Data; public int Pos; readonly int _end;
            public Reader(byte[] d, int s, int e) { Data = d; Pos = s; _end = e; }
            public bool More => Pos < _end;
            public ulong Varint()
            {
                ulong v = 0; int shift = 0;
                while (Pos < _end) { byte b = Data[Pos++]; v |= (ulong)(b & 0x7F) << shift; if ((b & 0x80) == 0) return v; shift += 7; if (shift > 63) throw new FormatException("bad varint"); }
                throw new FormatException("truncated varint");
            }
            public void Skip(int wire)
            {
                switch (wire) { case 0: Varint(); break; case 1: Pos += 8; break; case 2: Pos += (int)Varint(); break; case 5: Pos += 4; break; default: throw new FormatException("wire " + wire); }
                if (Pos > _end) throw new FormatException("overrun");
            }
        }
    }
}
