using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace RocketWorld.Net
{
    /// <summary>
    /// One state snapshot (a car or the ball), frame independent: ECEF position (doubles) + ECEF forward / up / velocity.
    /// seq = snapshot counter (drops out-of-order packets on the unreliable channel); ack = last input sequence number the host applied.
    /// </summary>
    public struct PoseSnap : INetworkSerializable
    {
        public ushort seq, ack;
        public double x, y, z;
        public Vector3 f, u, v;
        public byte flags;                 // 1 on ground, 2 boosting, 4 running / exists
        public float aux;                  // plane snapshots: rear ramp angle (degrees)

        public bool OnGround => (flags & 1) != 0;
        public bool Boosting => (flags & 2) != 0;
        public bool Running => (flags & 4) != 0;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref seq); s.SerializeValue(ref ack);
            s.SerializeValue(ref x); s.SerializeValue(ref y); s.SerializeValue(ref z);
            s.SerializeValue(ref f); s.SerializeValue(ref u); s.SerializeValue(ref v);
            s.SerializeValue(ref flags); s.SerializeValue(ref aux);
        }

        public static PoseSnap Lerp(PoseSnap a, PoseSnap b, float k)
        {
            var r = a;
            r.x = a.x + (b.x - a.x) * k; r.y = a.y + (b.y - a.y) * k; r.z = a.z + (b.z - a.z) * k;
            r.f = Vector3.Slerp(a.f, b.f, k); r.u = Vector3.Slerp(a.u, b.u, k); r.v = Vector3.Lerp(a.v, b.v, k);
            r.flags = k < 0.5f ? a.flags : b.flags;
            r.aux = a.aux + (b.aux - a.aux) * k;
            return r;
        }

        public PoseSnap Extrapolate(float dt)
        {
            var r = this;
            r.x += v.x * dt; r.y += v.y * dt; r.z += v.z * dt;
            return r;
        }

        public double DistanceTo(PoseSnap o)
        {
            double dx = x - o.x, dy = y - o.y, dz = z - o.z;
            return System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    /// <summary>Receive-time ordered snapshot buffer with interpolation (remote objects) and short extrapolation (own car).</summary>
    public sealed class PoseBuffer
    {
        const int Cap = 32;
        readonly List<float> _t = new List<float>(Cap + 1);
        readonly List<PoseSnap> _s = new List<PoseSnap>(Cap + 1);
        ushort _lastSeq; bool _has;

        public int Count => _t.Count;

        public void Add(float recvTime, PoseSnap s)
        {
            if (_has && (short)(s.seq - _lastSeq) <= 0) return;           // older / duplicate packet
            _has = true; _lastSeq = s.seq;
            _t.Add(recvTime); _s.Add(s);
            if (_t.Count > Cap) { _t.RemoveAt(0); _s.RemoveAt(0); }
        }

        public bool Sample(float target, float maxExtrapolate, out PoseSnap o)
        {
            o = default;
            int n = _t.Count;
            if (n == 0) return false;
            if (target <= _t[0]) { o = _s[0]; return true; }
            if (target >= _t[n - 1]) { o = _s[n - 1].Extrapolate(Mathf.Min(target - _t[n - 1], maxExtrapolate)); return true; }
            for (int i = n - 2; i >= 0; i--)
            {
                if (_t[i] > target) continue;
                float span = Mathf.Max(1e-4f, _t[i + 1] - _t[i]);
                o = PoseSnap.Lerp(_s[i], _s[i + 1], (target - _t[i]) / span);
                return true;
            }
            o = _s[0];
            return true;
        }
    }
}
