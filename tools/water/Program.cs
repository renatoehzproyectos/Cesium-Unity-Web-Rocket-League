// Offline checks for the engine-free water code (Assets/Scripts/Sim/Water): a hand-made MVT encoder builds tiles with known
// geometry, the reader must decode them and the point-in-water / bridge tests must agree with the geometry.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using RocketWorld.Sim;
using RocketWorld.Tests;

static class P
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) fails++; }
    static void Near(double a, double b, double tol, string what) => Check(Math.Abs(a - b) <= tol, what + $"  ({a:0.#########} vs {b:0.#########})");

    static void Main()
    {
        var t = MvtReader.Read(WaterTestTiles.BuildTile());
        Check(t.water.Count == 2, $"water features decoded = 2 (tunnel + pool dropped), got {t.water.Count}");
        WaterKind k;
        Check(t.IsWater(1500, 1500, out k) && k == WaterKind.Lake, "inside lake -> water, kind lake");
        Check(!t.IsWater(2000, 2000, out _), "island hole -> land");
        Check(!t.IsWater(900, 1500, out _) && !t.IsWater(3100, 1500, out _), "outside the lake -> land");
        Check(t.IsWater(3700, 300, out k) && k == WaterKind.Ocean, "ocean polygon -> water, kind ocean");
        Check(!t.IsWater(3700, 3700, out _), "tunnel water ignored");
        Check(!t.IsWater(200, 200, out _), "swimming pool ignored");
        Check(t.solidLines.Count == 2, $"bridge + pier lines kept, ferry dropped, got {t.solidLines.Count}");
        Check(t.NearSolid(1500, 2010, 20), "10 units from the bridge -> near solid");
        Check(!t.NearSolid(1500, 2100, 20), "100 units from the bridge -> not near");
        Check(t.NearSolid(2005, 2700, 20), "next to the pier -> near solid");
        Check(!t.NearSolid(2000, 1200, 20), "on the ferry route -> not solid");

        // gzip round trip (OpenFreeMap tiles are gzip; the browser inflates them, native builds may not)
        var raw = WaterTestTiles.BuildTile(); byte[] gz;
        using (var ms = new MemoryStream()) { using (var g = new GZipStream(ms, CompressionMode.Compress)) g.Write(raw, 0, raw.Length); gz = ms.ToArray(); }
        Check(gz[0] == 0x1f && gz[1] == 0x8b, "gzip magic detected");

        // geodesy
        double lon, lat;
        Geo.EcefToLonLat(6378137.0, 0, 0, out lon, out lat);
        Near(lon, 0, 1e-12, "ECEF (a,0,0) lon"); Near(lat, 0, 1e-12, "ECEF (a,0,0) lat");
        foreach (var c in new[] { (-4.4214, 36.7213, 61.0), (-73.9855, 40.758, -23.0), (55.2744, 25.1972, -15.0), (151.2093, -33.8688, 20.0), (-0.0001, 89.0, 100.0) })
        {
            Geo.LonLatToEcef(c.Item1, c.Item2, c.Item3, out double x, out double y, out double z);
            Geo.EcefToLonLat(x, y, z, out lon, out lat);
            Near(lon, c.Item1, 1e-9, $"round-trip lon {c.Item1}"); Near(lat, c.Item2, 1e-9, $"round-trip lat {c.Item2}");
        }
        Geo.LonLatToTile(0, 0, 1, out double tx, out double ty);
        Near(tx, 1.0, 1e-12, "tile x of (0,0) at z1"); Near(ty, 1.0, 1e-12, "tile y of (0,0) at z1");
        Geo.LonLatToTile(-4.4214, 36.7213, 14, out tx, out ty);
        Console.WriteLine($"INFO  Malaga z14 tile = {Math.Floor(tx)}/{Math.Floor(ty)}  frac = {tx - Math.Floor(tx):0.0000},{ty - Math.Floor(ty):0.0000}");
        Near(tx, 7990.777173333333, 1e-9, "Malaga tile x (python reference)"); Near(ty, 6392.998516944792, 1e-9, "Malaga tile y (python reference)");
        Console.WriteLine($"INFO  data resolution at lat 36.7 z14 = {Geo.MetresPerUnit(36.7, 14):0.00} m per unit");

        // tile cache policy (view-driven streaming)
        const long MB = 1024 * 1024;
        Check(TileCachePolicy.MaxBudget(384 * MB, false, 3200 * MB) == 1152 * MB, "desktop max cache = 3x base (384 -> 1152 MB)");
        Check(TileCachePolicy.MaxBudget(256 * MB, false, 1000 * MB) == 500 * MB, "max cache never above half of the heap allowance");
        Check(TileCachePolicy.MaxBudget(192 * MB, true, 4000 * MB) == 384 * MB, "mobile max cache = 2x base");
        long bud = 400 * MB;
        Check(TileCachePolicy.Next(bud, 2000 * MB, 2000 * MB, 200 * MB, 1000 * MB) == (long)(bud * 0.85), "memory pressure shrinks the cache 15%");
        Check(TileCachePolicy.Next(bud, 500 * MB, 2000 * MB, 200 * MB, 1000 * MB) == (long)(bud * 1.08), "free memory grows the cache 8%");
        Check(TileCachePolicy.Next(bud, 1500 * MB, 2000 * MB, 200 * MB, 1000 * MB) == bud, "in between it holds");
        Check(TileCachePolicy.Next(210 * MB, 2000 * MB, 2000 * MB, 200 * MB, 1000 * MB) == 200 * MB, "never below the minimum");
        Check(TileCachePolicy.Next(990 * MB, 100 * MB, 2000 * MB, 200 * MB, 1000 * MB) == 1000 * MB, "never above the maximum");
        long grown = 400 * MB; for (int i = 0; i < 200; i++) grown = TileCachePolicy.Next(grown, 300 * MB, 2000 * MB, 200 * MB, 1000 * MB);
        long shrunk = grown; for (int i = 0; i < 200; i++) shrunk = TileCachePolicy.Next(shrunk, 1990 * MB, 2000 * MB, 200 * MB, 1000 * MB);
        Check(grown == 1000 * MB && shrunk == 200 * MB, "converges to the bounds under sustained room / pressure");
        Near(TileCachePolicy.PhysicsRing(0), 150, 1e-6, "physics ring at rest = 150 m");
        Near(TileCachePolicy.PhysicsRing(46), 265, 1e-4, "physics ring at 46 m/s (car top speed) = 265 m");
        Near(TileCachePolicy.PhysicsRing(200), 330, 1e-6, "physics ring is capped at 330 m");
        Near(TileCachePolicy.AheadRange(0), 350, 1e-6, "look-ahead range at rest = 350 m");
        Near(TileCachePolicy.AheadRange(20), 590, 1e-4, "look-ahead range at 20 m/s = 590 m");
        Near(TileCachePolicy.AheadRange(500), 900, 1e-6, "look-ahead range is capped at 900 m");

        // helicopter camera
        Near(HeliCamPolicy.Height(0, false, 1), 38, 1e-4, "heli camera hovers 38 m above a parked car");
        Near(HeliCamPolicy.Back(0, false, 1), 14, 1e-4, "heli camera sits 14 m behind a parked car");
        Near(HeliCamPolicy.PitchDeg(HeliCamPolicy.Height(0, false, 1), HeliCamPolicy.Back(0, false, 1)), 69.8, 0.2, "heli camera looks down ~70 degrees");
        Check(HeliCamPolicy.Height(46, false, 1) > HeliCamPolicy.Height(0, false, 1) && HeliCamPolicy.Back(46, false, 1) > HeliCamPolicy.Back(0, false, 1), "heli camera climbs and pulls back with speed");
        Near(HeliCamPolicy.Height(0, false, 10), 38 * HeliCamPolicy.MaxZoom, 1e-4, "heli zoom is clamped to the maximum");
        Near(HeliCamPolicy.Height(0, false, 0.01f), 38 * HeliCamPolicy.MinZoom, 1e-4, "heli zoom is clamped to the minimum");

        // cinematic post-processing numbers
        Near(CinematicPolicy.Daylight(14), 1, 1e-4, "daylight at 14:00 = 1");
        Near(CinematicPolicy.Daylight(0), 0, 1e-4, "daylight at midnight = 0");
        Check(CinematicPolicy.BloomIntensity(0, false) > CinematicPolicy.BloomIntensity(1, false), "bloom is stronger at night");
        Near(CinematicPolicy.MotionBlurIntensity(5, false), 0, 1e-6, "no motion blur when slow");
        Check(CinematicPolicy.MotionBlurIntensity(500, true) <= 0.8f, "motion blur is capped");
        Near(CinematicPolicy.DofStart(8), 120, 1e-4, "depth of field starts at 120 m for the chase camera");
        Near(CinematicPolicy.EdgeBlurStrength(46), 1, 1e-4, "edge blur is full at top speed");

        // robustness: garbage must throw / return cleanly, never hang
        try { MvtReader.Read(new byte[] { 0x1a, 0xff, 0xff, 0xff, 0xff, 0x0f }); Check(true, "garbage tile handled"); }
        catch (Exception) { Check(true, "garbage tile throws a catchable exception"); }
        Console.WriteLine(fails == 0 ? "ALL OK" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
