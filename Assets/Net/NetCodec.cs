using System;
using UnityEngine;

namespace RocketWorld.Net
{
    /// <summary>
    /// Compact wire format of one pose (31 bytes instead of ~90 for a serialized <see cref="PoseSnap"/>), so that 15 players fit in one small
    /// datagram per receiver: 14 cars x 31 B = 434 B per batch.
    /// Absolute ECEF position in centimetres (int32: the Earth fits in +-21 000 km), velocity in 0.5 m/s steps (int16), orientation as a
    /// "smallest three" quaternion (3 x int16 + 2 bits in the flags byte), aux in 1/100 (int16).
    /// </summary>
    public static class NetCodec
    {
        public const int EntrySize = 31;
        public const byte BallId = 255;          // the shared ball
        public const byte BusBase = 128;         // 128 + slot: that player's intro bus

        const float QRange = 0.70711f;           // |smallest three| <= 1/sqrt(2)

        public static void Write(byte[] b, int o, byte id, in PoseSnap s)
        {
            b[o] = id;
            b[o + 1] = (byte)s.seq; b[o + 2] = (byte)(s.seq >> 8);

            Quaternion q = s.f.sqrMagnitude > 1e-6f && s.u.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(s.f, s.u) : Quaternion.identity;
            float[] c = { q.x, q.y, q.z, q.w };
            float n = Mathf.Sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2] + c[3] * c[3]);
            if (n < 1e-6f) { c[0] = c[1] = c[2] = 0f; c[3] = 1f; n = 1f; }
            int big = 0; float bigAbs = 0f;
            for (int i = 0; i < 4; i++) { c[i] /= n; float a = Mathf.Abs(c[i]); if (a > bigAbs) { bigAbs = a; big = i; } }
            float sign = c[big] < 0f ? -1f : 1f;

            b[o + 3] = (byte)((s.flags & 0x3F) | (big << 6));
            b[o + 4] = s.veh;
            PutI16(b, o + 5, Mathf.RoundToInt(Mathf.Clamp(s.aux * 100f, -32000f, 32000f)));
            PutI32(b, o + 7, Cm(s.x)); PutI32(b, o + 11, Cm(s.y)); PutI32(b, o + 15, Cm(s.z));
            PutI16(b, o + 19, V(s.v.x)); PutI16(b, o + 21, V(s.v.y)); PutI16(b, o + 23, V(s.v.z));
            int k = 0;
            for (int i = 0; i < 4; i++)
            {
                if (i == big) continue;
                PutI16(b, o + 25 + k * 2, Mathf.RoundToInt(Mathf.Clamp(c[i] * sign / QRange, -1f, 1f) * 32767f));
                k++;
            }
        }

        public static void Read(byte[] b, int o, out byte id, out PoseSnap s)
        {
            id = b[o];
            s = default;
            s.seq = (ushort)(b[o + 1] | (b[o + 2] << 8));
            int fl = b[o + 3]; int big = fl >> 6;
            s.flags = (byte)(fl & 0x3F);
            s.veh = b[o + 4];
            s.aux = GetI16(b, o + 5) / 100f;
            s.x = GetI32(b, o + 7) / 100.0; s.y = GetI32(b, o + 11) / 100.0; s.z = GetI32(b, o + 15) / 100.0;
            s.v = new Vector3(GetI16(b, o + 19) * 0.5f, GetI16(b, o + 21) * 0.5f, GetI16(b, o + 23) * 0.5f);
            float[] c = new float[4]; float sum = 0f; int k = 0;
            for (int i = 0; i < 4; i++)
            {
                if (i == big) continue;
                c[i] = GetI16(b, o + 25 + k * 2) / 32767f * QRange; sum += c[i] * c[i]; k++;
            }
            c[big] = Mathf.Sqrt(Mathf.Max(0f, 1f - sum));
            var q = new Quaternion(c[0], c[1], c[2], c[3]);
            s.f = q * Vector3.forward; s.u = q * Vector3.up;
        }

        static int Cm(double m) { double v = Math.Round(m * 100.0); return (int)Math.Max(int.MinValue + 1, Math.Min(int.MaxValue - 1, v)); }
        static int V(float v) { return Mathf.RoundToInt(Mathf.Clamp(v / 0.5f, -32767f, 32767f)); }

        static void PutI16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        static void PutI32(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        static int GetI16(byte[] b, int o) { return (short)(b[o] | (b[o + 1] << 8)); }
        static int GetI32(byte[] b, int o) { return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24); }
    }
}
