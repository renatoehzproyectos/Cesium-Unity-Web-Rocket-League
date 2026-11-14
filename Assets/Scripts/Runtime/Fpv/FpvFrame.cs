using RocketWorld.Sim.Fpv;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// The border between the drone model (right-handed, nose along -z, as in ShramkoGSFPV) and Unity (left-handed, nose along +z).
    /// The mapping is the mirror z -> -z. Positions and vectors flip z; a quaternion (w, x, y, z) becomes Unity (x: -x, y: -y, z: z, w: w),
    /// so a level drone with identity attitude is an identity Unity rotation facing +z. The mapping is its own inverse.
    /// </summary>
    public static class FpvFrame
    {
        public static Vector3 ToUnity(double x, double y, double z) => new Vector3((float)x, (float)y, (float)-z);
        public static void ToSim(Vector3 u, out double x, out double y, out double z) { x = u.x; y = u.y; z = -u.z; }
        public static Quaternion ToUnity(double w, double x, double y, double z) => new Quaternion((float)-x, (float)-y, (float)z, (float)w);
        public static void ToSim(Quaternion q, out double w, out double x, out double y, out double z) { w = q.w; x = -q.x; y = -q.y; z = q.z; }
    }

    /// <summary>
    /// The drone's collision world on PhysX: sphere sweeps and overlap push-outs against the Cesium tile colliders (non-convex mesh colliders,
    /// so push-out uses a ray fan rather than ComputePenetration). <see cref="Prepare"/> runs one cheap broad-phase check per game tick; in open
    /// sky every query of that tick returns at once.
    /// </summary>
    public sealed class FpvWorld : IFpvWorld
    {
        static readonly Vector3[] Fan = BuildFan();
        static Vector3[] BuildFan()
        {
            var l = new System.Collections.Generic.List<Vector3>();
            for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                if (x != 0 || y != 0 || z != 0) l.Add(new Vector3(x, y, z).normalized);
            return l.ToArray();
        }

        public int mask = ~0;
        bool _near = true;
        public int Queries { get; private set; }

        /// <summary>Is any solid within <paramref name="radius"/> of <paramref name="centreUnity"/>? When not, sweeps and push-outs are skipped for this tick.</summary>
        public void Prepare(Vector3 centreUnity, float radius)
        {
            _near = Physics.CheckSphere(centreUnity, radius, mask, QueryTriggerInteraction.Ignore);
            Queries = 0;
        }

        public double Sweep(double[] c0, double[] c1, double[] r, double[] pad, int n, ref FpvContact o)
        {
            if (!_near) return -1;
            double best = -1;
            for (int i = 0; i < n; i++)
            {
                Queries++;
                var a = FpvFrame.ToUnity(c0[i * 3], c0[i * 3 + 1], c0[i * 3 + 2]);
                var b = FpvFrame.ToUnity(c1[i * 3], c1[i * 3 + 1], c1[i * 3 + 2]);
                float R = (float)(r[i] + pad[i]);
                if (Physics.CheckSphere(a, R, mask, QueryTriggerInteraction.Ignore))
                {
                    // started overlapping: the model resolves it by pushing out; it needs the normal of the nearest surface
                    if (PushOut(c0[i * 3], c0[i * 3 + 1], c0[i * 3 + 2], r[i], out var pv))
                    {
                        double l = System.Math.Sqrt(pv.x * pv.x + pv.y * pv.y + pv.z * pv.z);
                        if (l > 1e-9)
                        {
                            if (best != 0) { best = 0; o.sphere = i; o.nx = pv.x / l; o.ny = pv.y / l; o.nz = pv.z / l; }
                            continue;
                        }
                    }
                }
                var d = b - a; float len = d.magnitude;
                if (len < 1e-7f) continue;
                if (Physics.SphereCast(a, R, d / len, out var hit, len, mask, QueryTriggerInteraction.Ignore))
                {
                    double t = hit.distance / len;
                    if (t > 1) t = 1;
                    if (best < 0 || t < best)
                    {
                        best = t; o.sphere = i;
                        FpvFrame.ToSim(hit.normal, out o.nx, out o.ny, out o.nz);
                    }
                }
            }
            return best;
        }

        public bool PushOut(double x, double y, double z, double r, out V3d push)
        {
            push = default;
            if (!_near) return false;
            var c = FpvFrame.ToUnity(x, y, z); float R = (float)r;
            if (!Physics.CheckSphere(c, R, mask, QueryTriggerInteraction.Ignore)) return false;
            // a ray fan from the centre: for a ray that hits a surface at distance t with normal n, the sphere reaches the plane's far side by r - t * (-dir.n)
            float bestPen = 0f; Vector3 bestPush = Vector3.zero;
            for (int i = 0; i < Fan.Length; i++)
            {
                if (!Physics.Raycast(c, Fan[i], out var h, R, mask, QueryTriggerInteraction.Ignore)) continue;
                float dn = h.distance * Mathf.Max(0f, -Vector3.Dot(Fan[i], h.normal));
                float pen = R - dn;
                if (pen > bestPen) { bestPen = pen; bestPush = h.normal * pen; }
            }
            if (bestPen <= 0f) return false;
            FpvFrame.ToSim(bestPush, out push.x, out push.y, out push.z);
            return true;
        }
    }
}
