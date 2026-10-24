using System.Collections.Generic;
using System.Text;

namespace RocketWorld.Tests
{
    /// <summary>
    /// Hand-made Mapbox Vector Tile with known geometry (a lake with an island, a tunnel and a swimming pool that must be ignored, an ocean
    /// polygon, a bridge, a pier and a ferry route). Engine-free and NUnit-free: shared by the Unity EditMode tests (WaterTests) and the
    /// console harness (tools/water). Tile units, extent 4096:
    ///   lake  (1000,1000)-(3000,3000), island hole (1800,1800)-(2200,2200)     ocean (3400,100)-(4000,600)
    ///   bridge along y=2000 from x=500 to 3500     pier x=2000, y=2500..2900     ferry y=1200 (NOT solid)
    /// </summary>
    public static class WaterTestTiles
    {
    // ---- tiny protobuf writer ----
    static void V(List<byte> o, ulong v) { while (v >= 0x80) { o.Add((byte)(v | 0x80)); v >>= 7; } o.Add((byte)v); }
    static void Tag(List<byte> o, int f, int w) => V(o, (ulong)((f << 3) | w));
    static void Bytes(List<byte> o, int f, byte[] b) { Tag(o, f, 2); V(o, (ulong)b.Length); o.AddRange(b); }
    static void Str(List<byte> o, int f, string s) => Bytes(o, f, Encoding.UTF8.GetBytes(s));
    static uint Z(int v) => (uint)((v << 1) ^ (v >> 31));

    // MVT geometry deltas are relative to a cursor that persists across rings of ONE feature (ClosePath does not move it).
    static List<uint> Ring(int[] pts, bool close, ref int cx, ref int cy)
    {
        var g = new List<uint> { (1 << 3) | 1, Z(pts[0] - cx), Z(pts[1] - cy) };    // MoveTo (relative to the cursor)
        int px = pts[0], py = pts[1];
        g.Add(((uint)(pts.Length / 2 - 1) << 3) | 2);                                 // LineTo n-1
        for (int i = 2; i < pts.Length; i += 2) { g.Add(Z(pts[i] - px)); g.Add(Z(pts[i + 1] - py)); px = pts[i]; py = pts[i + 1]; }
        if (close) g.Add((1 << 3) | 7);
        cx = px; cy = py;
        return g;
    }
    static List<uint> Ring(int[] pts, bool close) { int cx = 0, cy = 0; return Ring(pts, close, ref cx, ref cy); }
    static byte[] Feature(int type, int[] tags, List<uint> geo)
    {
        var o = new List<byte>(); var t = new List<byte>(); foreach (var x in tags) V(t, (ulong)x); Bytes(o, 2, t.ToArray());
        Tag(o, 3, 0); V(o, (ulong)type);
        var g = new List<byte>(); foreach (var x in geo) V(g, x); Bytes(o, 4, g.ToArray());
        return o.ToArray();
    }
    static byte[] Layer(string name, string[] keys, string[] vals, List<byte[]> feats)
    {
        var o = new List<byte>(); Str(o, 1, name);
        foreach (var f in feats) Bytes(o, 2, f);
        foreach (var k in keys) Str(o, 3, k);
        foreach (var v in vals) { var vv = new List<byte>(); Str(vv, 1, v); Bytes(o, 4, vv.ToArray()); }
        Tag(o, 5, 0); V(o, 4096); Tag(o, 15, 0); V(o, 2);
        return o.ToArray();
    }

    public static byte[] BuildTile()
    {
        // keys: 0 class, 1 brunnel   vals: 0 lake, 1 tunnel, 2 swimming_pool, 3 ocean, 4 bridge, 5 pier, 6 ferry
        var keys = new[] { "class", "brunnel" };
        var vals = new[] { "lake", "tunnel", "swimming_pool", "ocean", "bridge", "pier", "ferry" };
        var water = new List<byte[]>();
        // lake 1000..3000 with an island hole 1800..2200
        int cx = 0, cy = 0;
        var lake = Ring(new[] { 1000, 1000, 3000, 1000, 3000, 3000, 1000, 3000 }, true, ref cx, ref cy);
        lake.AddRange(Ring(new[] { 1800, 1800, 1800, 2200, 2200, 2200, 2200, 1800 }, true, ref cx, ref cy));   // opposite winding = hole
        water.Add(Feature(3, new[] { 0, 0 }, lake));
        water.Add(Feature(3, new[] { 0, 1, 1, 1 }, Ring(new[] { 3500, 3500, 3900, 3500, 3900, 3900, 3500, 3900 }, true)));   // tunnel: ignored
        water.Add(Feature(3, new[] { 0, 2 }, Ring(new[] { 100, 100, 300, 100, 300, 300, 100, 300 }, true)));                  // pool: ignored
        water.Add(Feature(3, new[] { 0, 3 }, Ring(new[] { 3400, 100, 4000, 100, 4000, 600, 3400, 600 }, true)));              // ocean
        var roads = new List<byte[]>();
        roads.Add(Feature(2, new[] { 1, 4 }, Ring(new[] { 500, 2000, 3500, 2000 }, false)));    // bridge along y = 2000
        roads.Add(Feature(2, new[] { 0, 5 }, Ring(new[] { 2000, 2500, 2000, 2900 }, false)));   // pier  (class=pier)
        roads.Add(Feature(2, new[] { 0, 6 }, Ring(new[] { 1200, 1200, 2800, 1200 }, false)));   // ferry route: NOT solid
        var tile = new List<byte>();
        Bytes(tile, 3, Layer("water", keys, vals, water));
        Bytes(tile, 3, Layer("transportation", keys, vals, roads));
        Bytes(tile, 3, Layer("building", keys, vals, new List<byte[]>()));                       // other layers are skipped
        return tile.ToArray();
    }

    }
}
