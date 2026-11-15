using System;
using System.IO;
using System.Text;

namespace RocketWorld.Sim.Roads
{
    /// <summary>
    /// RWR1: compact binary road tile (one slippy-map tile, default z14), written by tools/roads/build_tiles.py from Overture GeoParquet and read at runtime.
    /// Layout (little endian): "RWR1", u8 version, u8 z, u16 0, i32 x, i32 y, u8 len + release ascii, varuint segmentCount, then per segment:
    /// u32 id, u8 class, u8 surface, u8 flags, i8 level, u16 widthDm, u8 lanes, u8 connectorCount (id u32 + at u16), varuint pointCount,
    /// first point as i32 lon/lat in 1e-7 deg, then zigzag-varint deltas. 1e-7 deg = 1.1 cm.
    /// </summary>
    public static class RoadTileFormat
    {
        public const byte Version = 1;
        public const double Q = 1e7;

        public static byte[] Write(RoadTileData d)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Encoding.ASCII.GetBytes("RWR1")); w.Write(Version); w.Write((byte)d.z); w.Write((ushort)0); w.Write(d.x); w.Write(d.y);
                var rel = Encoding.ASCII.GetBytes(d.release ?? ""); w.Write((byte)rel.Length); w.Write(rel);
                VarU(w, (uint)d.segments.Count);
                foreach (var s in d.segments)
                {
                    w.Write(s.id); w.Write((byte)s.cls); w.Write((byte)s.surface); w.Write((byte)s.flags); w.Write(s.level);
                    w.Write((ushort)Math.Max(0, Math.Min(65535, Math.Round(s.widthM * 10f)))); w.Write(s.lanes);
                    int nc = Math.Min(255, s.connectors.Length); w.Write((byte)nc);
                    for (int i = 0; i < nc; i++) { w.Write(s.connectors[i].id); w.Write((ushort)Math.Max(0, Math.Min(65535, Math.Round(s.connectors[i].at * 65535f)))); }
                    int n = s.lon.Length; VarU(w, (uint)n);
                    int px = 0, py = 0;
                    for (int i = 0; i < n; i++)
                    {
                        int qx = (int)Math.Round(s.lon[i] * Q), qy = (int)Math.Round(s.lat[i] * Q);
                        if (i == 0) { w.Write(qx); w.Write(qy); } else { VarS(w, qx - px); VarS(w, qy - py); }
                        px = qx; py = qy;
                    }
                }
                return ms.ToArray();
            }
        }

        /// <summary>Returns null on any malformed input (truncated download, wrong magic, newer format): the caller treats that as "no data" and retries later.</summary>
        public static RoadTileData Read(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 20) return null;
            try
            {
                using (var ms = new MemoryStream(bytes))
                using (var r = new BinaryReader(ms))
                {
                    if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RWR1" || r.ReadByte() != Version) return null;
                    var d = new RoadTileData { z = r.ReadByte() }; r.ReadUInt16(); d.x = r.ReadInt32(); d.y = r.ReadInt32();
                    d.release = Encoding.ASCII.GetString(r.ReadBytes(r.ReadByte()));
                    uint ns = VarU(r), maxSeg = (uint)bytes.Length;   // each segment needs >= 1 byte: rejects absurd counts
                    if (ns > maxSeg) return null;
                    for (uint k = 0; k < ns; k++)
                    {
                        var s = new RoadSegment { id = r.ReadUInt32(), cls = (RoadClass)r.ReadByte(), surface = (RoadSurface)r.ReadByte(), flags = (RoadFlags)r.ReadByte(), level = r.ReadSByte() };
                        s.widthM = r.ReadUInt16() / 10f; s.lanes = r.ReadByte();
                        int nc = r.ReadByte(); s.connectors = new RoadConnector[nc];
                        for (int i = 0; i < nc; i++) s.connectors[i] = new RoadConnector(r.ReadUInt32(), r.ReadUInt16() / 65535f);
                        uint n = VarU(r); if (n < 2 || n > 100000) return null;
                        s.lon = new double[n]; s.lat = new double[n];
                        int px = 0, py = 0;
                        for (int i = 0; i < n; i++)
                        {
                            int qx, qy;
                            if (i == 0) { qx = r.ReadInt32(); qy = r.ReadInt32(); } else { qx = px + VarS(r); qy = py + VarS(r); }
                            s.lon[i] = qx / Q; s.lat[i] = qy / Q; px = qx; py = qy;
                        }
                        d.segments.Add(s);
                    }
                    return d;
                }
            }
            catch (Exception) { return null; }
        }

        static void VarU(BinaryWriter w, uint v) { while (v >= 0x80) { w.Write((byte)(v | 0x80)); v >>= 7; } w.Write((byte)v); }
        static void VarS(BinaryWriter w, int v) => VarU(w, (uint)((v << 1) ^ (v >> 31)));
        static uint VarU(BinaryReader r) { uint v = 0; int sh = 0; while (true) { byte b = r.ReadByte(); v |= (uint)(b & 0x7F) << sh; if ((b & 0x80) == 0) return v; sh += 7; if (sh > 35) throw new InvalidDataException(); } }
        static int VarS(BinaryReader r) { uint u = VarU(r); return (int)(u >> 1) ^ -(int)(u & 1); }
    }
}
