using System;
using System.Collections.Generic;
using System.Text;

namespace RocketWorld.Sim
{
    /// <summary>
    /// Minimal Mapbox Vector Tile (protobuf) reader for the OpenMapTiles schema. Only what the water system needs:
    /// layer "water" (polygons; skips tunnels and swimming pools) and layer "transportation" (bridge / pier lines).
    /// Spec: https://github.com/mapbox/vector-tile-spec/tree/master/2.1
    /// </summary>
    public static class MvtReader
    {
        public static WaterTile Read(byte[] data)
        {
            var tile = new WaterTile();
            var r = new Reader(data, 0, data.Length);
            while (r.More)
            {
                uint tag = (uint)r.Varint();
                int field = (int)(tag >> 3), wire = (int)(tag & 7);
                if (field == 3 && wire == 2) { int len = (int)r.Varint(); ReadLayer(new Reader(data, r.Pos, r.Pos + len), tile); r.Pos += len; }
                else r.Skip(wire);
            }
            return tile;
        }

        struct Feat { public int type; public int tagStart, tagEnd; public int geoStart, geoEnd; }

        static void ReadLayer(Reader r, WaterTile tile)
        {
            string name = null; int extent = 4096;
            var feats = new List<Feat>();
            var keys = new List<string>();
            var vals = new List<string>();
            byte[] d = r.Data;
            while (r.More)
            {
                uint tag = (uint)r.Varint();
                int field = (int)(tag >> 3), wire = (int)(tag & 7);
                if (wire == 2)
                {
                    int len = (int)r.Varint(); int s = r.Pos; r.Pos += len;
                    if (field == 1) name = Encoding.UTF8.GetString(d, s, len);
                    else if (field == 2) feats.Add(ReadFeature(d, s, s + len));
                    else if (field == 3) keys.Add(Encoding.UTF8.GetString(d, s, len));
                    else if (field == 4) vals.Add(ReadValue(d, s, s + len));
                }
                else if (wire == 0) { ulong v = r.Varint(); if (field == 5) extent = (int)v; }
                else r.Skip(wire);
            }
            if (name != "water" && name != "transportation") return;
            tile.extent = extent;

            foreach (var f in feats)
            {
                string cls = null, brunnel = null;
                var tr = new Reader(d, f.tagStart, f.tagEnd);
                while (tr.More)
                {
                    int k = (int)tr.Varint(); if (!tr.More) break; int v = (int)tr.Varint();
                    if (k >= keys.Count || v >= vals.Count) continue;
                    if (keys[k] == "class") cls = vals[v]; else if (keys[k] == "brunnel") brunnel = vals[v];
                }
                if (name == "water")
                {
                    if (f.type != 3 || brunnel == "tunnel" || cls == "swimming_pool") continue;
                    var rings = DecodeGeometry(d, f.geoStart, f.geoEnd, true);
                    if (rings.Count == 0) continue;
                    var p = new WaterPoly { rings = rings.ToArray(), kind = cls == "ocean" ? WaterKind.Ocean : cls == "lake" ? WaterKind.Lake : cls == "river" ? WaterKind.River : WaterKind.Other };
                    p.Finish();
                    tile.water.Add(p);
                }
                else if (f.type == 2 && (brunnel == "bridge" || cls == "pier"))
                    tile.solidLines.AddRange(DecodeGeometry(d, f.geoStart, f.geoEnd, false));
            }
        }

        static Feat ReadFeature(byte[] d, int s, int e)
        {
            var f = new Feat(); var r = new Reader(d, s, e);
            while (r.More)
            {
                uint tag = (uint)r.Varint();
                int field = (int)(tag >> 3), wire = (int)(tag & 7);
                if (wire == 2)
                {
                    int len = (int)r.Varint(); int st = r.Pos; r.Pos += len;
                    if (field == 2) { f.tagStart = st; f.tagEnd = st + len; }
                    else if (field == 4) { f.geoStart = st; f.geoEnd = st + len; }
                }
                else if (wire == 0) { ulong v = r.Varint(); if (field == 3) f.type = (int)v; }
                else r.Skip(wire);
            }
            return f;
        }

        static string ReadValue(byte[] d, int s, int e)
        {
            var r = new Reader(d, s, e);
            while (r.More)
            {
                uint tag = (uint)r.Varint();
                int field = (int)(tag >> 3), wire = (int)(tag & 7);
                if (field == 1 && wire == 2) { int len = (int)r.Varint(); string str = Encoding.UTF8.GetString(d, r.Pos, len); r.Pos += len; return str; }
                r.Skip(wire);
            }
            return "";
        }

        /// <summary>Command-stream decoder: MoveTo(1) starts a ring / line, LineTo(2) extends it, ClosePath(7) ends a polygon ring.</summary>
        static List<int[]> DecodeGeometry(byte[] d, int s, int e, bool polygon)
        {
            var result = new List<int[]>(); var cur = new List<int>();
            var r = new Reader(d, s, e); int x = 0, y = 0;
            int min = polygon ? 6 : 4;
            while (r.More)
            {
                uint cmd = (uint)r.Varint(); int id = (int)(cmd & 7), count = (int)(cmd >> 3);
                if (id == 7) { if (polygon) Flush(cur, result, min); continue; }
                for (int i = 0; i < count; i++)
                {
                    x += Zig((uint)r.Varint()); y += Zig((uint)r.Varint());
                    if (id == 1) Flush(cur, result, min);    // MoveTo: a new ring / line begins
                    cur.Add(x); cur.Add(y);
                }
            }
            Flush(cur, result, min);
            return result;
        }

        static void Flush(List<int> cur, List<int[]> result, int minCoords)
        {
            if (cur.Count >= minCoords) result.Add(cur.ToArray());
            cur.Clear();
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
                while (Pos < _end)
                {
                    byte b = Data[Pos++]; v |= (ulong)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0) return v;
                    shift += 7; if (shift > 63) throw new FormatException("bad varint");
                }
                throw new FormatException("truncated varint");
            }
            public void Skip(int wire)
            {
                switch (wire)
                {
                    case 0: Varint(); break;
                    case 1: Pos += 8; break;
                    case 2: Pos += (int)Varint(); break;
                    case 5: Pos += 4; break;
                    default: throw new FormatException("unsupported wire type " + wire);
                }
            }
        }
    }
}
