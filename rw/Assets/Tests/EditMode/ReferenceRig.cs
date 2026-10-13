using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RocketWorld.Sim.Tests
{
    // Test/reference doubles for RocketWorld.Sim: a Bullet-style rigid body (same integration order as btDiscreteDynamicsWorld),
    // a flat floor and a triangle-mesh floor. Used by the EditMode tests and by tools/parity (compared against native RocketSim).
    sealed class Quat { public float x, y, z, w; }

    sealed class BulletLikeBody : IBody
    {
        public Vec3 pos; public Mat3 basis = Mat3.Identity;
        public Vec3 vel, angVel, totalForce, totalTorque;
        public float mass = RL.CarMassBt; public Vec3 invInertiaLocal;
        public Vec3 gravity = new Vec3(0, 0, RL.GravityZ * RL.UuToBt);
        public Vec3 Position => pos; public Mat3 Basis => basis;
        public Vec3 LinearVelocity { get => vel; set => vel = value; }
        public Vec3 AngularVelocity { get => angVel; set => angVel = value; }
        public float Mass => mass; public Vec3 InvInertiaLocal => invInertiaLocal;
        public void ApplyCentralForce(Vec3 f) { totalForce += f; }
        public void ApplyTorque(Vec3 t) { totalTorque += t; }
        public void ApplyCentralImpulse(Vec3 j) { vel += j * (1f / mass); }
        Vec3 InvInertiaWorldMul(Vec3 v) => basis.Mul(invInertiaLocal * basis.MulT(v));
        public void ApplyImpulse(Vec3 j, Vec3 rel) { vel += j * (1f / mass); angVel += InvInertiaWorldMul(rel.Cross(j)); }

        public void Step(float dt)
        {
            totalForce += gravity * mass;                                  // btRigidBody::applyGravity
            Vec3 extForceImpulse = totalForce * (1f / mass) * dt;          // solverBody.m_externalForceImpulse
            Vec3 extTorqueImpulse = InvInertiaWorldMul(totalTorque) * dt;  // m_externalTorqueImpulse (gyroscopic term disabled by RocketSim)
            vel += extForceImpulse; angVel += extTorqueImpulse;
            totalForce = Vec3.Zero; totalTorque = Vec3.Zero;
            // btTransformUtil::integrateTransform
            pos += vel * dt;
            float a2 = angVel.Length2(), ang = 0;
            if (a2 > 1.1920929e-7f) ang = (float)Math.Sqrt(a2);
            const float thresh = (float)(Math.PI / 2 * 0.5);
            if (ang * dt > thresh) ang = thresh / dt;
            Vec3 axis;
            if (ang < 0.001f) axis = angVel * (0.5f * dt - (dt * dt * dt) * 0.020833333333f * ang * ang);
            else axis = angVel * ((float)Math.Sin(0.5f * ang * dt) / ang);
            var dorn = new Quat { x = axis.x, y = axis.y, z = axis.z, w = (float)Math.Cos(ang * dt * 0.5f) };
            var q = FromBasis(basis);
            var r = Mul(dorn, q); Normalize(r);
            basis = ToBasis(r);
        }

        static Quat Mul(Quat a, Quat b) => new Quat
        {
            w = a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z,
            x = a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            y = a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z,
            z = a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x,
        };
        static void Normalize(Quat q) { float n = (float)Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w); q.x /= n; q.y /= n; q.z /= n; q.w /= n; }
        // columns c0,c1,c2 == rotation matrix columns
        static Quat FromBasis(Mat3 m)
        {
            float m00 = m.c0.x, m10 = m.c0.y, m20 = m.c0.z, m01 = m.c1.x, m11 = m.c1.y, m21 = m.c1.z, m02 = m.c2.x, m12 = m.c2.y, m22 = m.c2.z;
            float tr = m00 + m11 + m22; var q = new Quat();
            if (tr > 0) { float s = (float)Math.Sqrt(tr + 1f) * 2; q.w = 0.25f * s; q.x = (m21 - m12) / s; q.y = (m02 - m20) / s; q.z = (m10 - m01) / s; }
            else if (m00 > m11 && m00 > m22) { float s = (float)Math.Sqrt(1f + m00 - m11 - m22) * 2; q.w = (m21 - m12) / s; q.x = 0.25f * s; q.y = (m01 + m10) / s; q.z = (m02 + m20) / s; }
            else if (m11 > m22) { float s = (float)Math.Sqrt(1f + m11 - m00 - m22) * 2; q.w = (m02 - m20) / s; q.x = (m01 + m10) / s; q.y = 0.25f * s; q.z = (m12 + m21) / s; }
            else { float s = (float)Math.Sqrt(1f + m22 - m00 - m11) * 2; q.w = (m10 - m01) / s; q.x = (m02 + m20) / s; q.y = (m12 + m21) / s; q.z = 0.25f * s; }
            return q;
        }
        static Mat3 ToBasis(Quat q)
        {
            float x = q.x, y = q.y, z = q.z, w = q.w;
            return new Mat3(
                new Vec3(1 - 2 * (y * y + z * z), 2 * (x * y + z * w), 2 * (x * z - y * w)),
                new Vec3(2 * (x * y - z * w), 1 - 2 * (x * x + z * z), 2 * (y * z + x * w)),
                new Vec3(2 * (x * z + y * w), 2 * (y * z - x * w), 1 - 2 * (x * x + y * y)));
        }
    }

    sealed class FlatFloor : IRayCaster
    {
        public bool Cast(Vec3 from, Vec3 to, out RayHit hit)
        {
            hit = default;
            if (from.z <= 0 || to.z >= 0) return false;
            float t = from.z / (from.z - to.z);
            hit = new RayHit { point = from + (to - from) * t, normal = new Vec3(0, 0, 1), fraction = t };
            return true;
        }
    }

    sealed class MeshFloor : IRayCaster
    {
        readonly List<Vec3[]> _tris = new List<Vec3[]>();
        public MeshFloor(string file)
        {
            var inv = CultureInfo.InvariantCulture;
            foreach (var l in File.ReadLines(file))
            {
                var v = l.Split(' ', StringSplitOptions.RemoveEmptyEntries); if (v.Length < 9) continue;
                Vec3 P(int i) => new Vec3(float.Parse(v[i], inv), float.Parse(v[i + 1], inv), float.Parse(v[i + 2], inv)) * RL.UuToBt;
                _tris.Add(new[] { P(0), P(3), P(6) });
            }
        }
        public bool Cast(Vec3 from, Vec3 to, out RayHit hit)
        {
            hit = default; float best = float.MaxValue; Vec3 d = to - from;
            foreach (var t in _tris)
            {
                Vec3 e1 = t[1] - t[0], e2 = t[2] - t[0], pv = d.Cross(e2); float det = e1.Dot(pv);
                if (Math.Abs(det) < 1e-9f) continue;
                float inv = 1f / det; Vec3 tv = from - t[0]; float u = tv.Dot(pv) * inv; if (u < 0 || u > 1) continue;
                Vec3 qv = tv.Cross(e1); float v = d.Dot(qv) * inv; if (v < 0 || u + v > 1) continue;
                float s = e2.Dot(qv) * inv; if (s < 0 || s > 1 || s >= best) continue;
                best = s; var n = e1.Cross(e2).Normalized(); if (n.Dot(d) > 0) n = -n;
                hit = new RayHit { point = from + d * s, normal = n, fraction = s };
            }
            return best != float.MaxValue;
        }
    }

}
