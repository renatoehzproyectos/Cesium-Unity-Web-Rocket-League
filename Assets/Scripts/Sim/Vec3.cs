using System;

namespace RocketWorld.Sim
{
    /// <summary>Engine-free 3-vector with Bullet (btVector3) semantics. Float on purpose: the model must round like RocketSim.</summary>
    public struct Vec3
    {
        public float x, y, z;
        public Vec3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static readonly Vec3 Zero = new Vec3(0, 0, 0);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.x, -a.y, -a.z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.x * s, a.y * s, a.z * s);
        public static Vec3 operator *(float s, Vec3 a) => new Vec3(a.x * s, a.y * s, a.z * s);
        public static Vec3 operator /(Vec3 a, float s) => new Vec3(a.x / s, a.y / s, a.z / s);
        /// <summary>Component-wise product (btVector3 * btVector3).</summary>
        public static Vec3 operator *(Vec3 a, Vec3 b) => new Vec3(a.x * b.x, a.y * b.y, a.z * b.z);

        public float Dot(Vec3 b) => x * b.x + y * b.y + z * b.z;
        public Vec3 Cross(Vec3 b) => new Vec3(y * b.z - z * b.y, z * b.x - x * b.z, x * b.y - y * b.x);
        public float Length2() => Dot(this);
        public float Length() => (float)Math.Sqrt(Length2());
        public Vec3 Normalized() => this / Length();

        /// <summary>btVector3::safeNormalized: (1,0,0) when the vector is (almost) zero.</summary>
        public Vec3 SafeNormalized()
        {
            float l2 = Length2();
            const float eps = 1.1920929e-7f;                 // SIMD_EPSILON == FLT_EPSILON
            return l2 >= eps * eps ? this / (float)Math.Sqrt(l2) : new Vec3(1, 0, 0);
        }
        /// <summary>RocketSim's Vec::Normalized(): zero vector in, zero vector out.</summary>
        public Vec3 NormalizedOrZero() { float l = Length(); return l > 1.4210855e-14f ? this / l : Zero; }
        public bool IsZero() => x == 0 && y == 0 && z == 0;
        public bool FuzzyZero() => Length2() < 1.4210855e-14f;   // SIMD_EPSILON^2
        public override string ToString() => $"({x:0.####}, {y:0.####}, {z:0.####})";
    }

    /// <summary>
    /// Rotation as three world-space column vectors (the body's X/Y/Z axes): for RocketSim X = forward, Y = right, Z = up.
    /// Same layout as RocketSim's RotMat (column-major) / btMatrix3x3 basis.
    /// </summary>
    public struct Mat3
    {
        public Vec3 c0, c1, c2;
        public Mat3(Vec3 c0, Vec3 c1, Vec3 c2) { this.c0 = c0; this.c1 = c1; this.c2 = c2; }
        public static Mat3 Identity => new Mat3(new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1));
        public Vec3 Column(int i) => i == 0 ? c0 : i == 1 ? c1 : c2;
        /// <summary>basis * v</summary>
        public Vec3 Mul(Vec3 v) => c0 * v.x + c1 * v.y + c2 * v.z;
        /// <summary>basis^T * v</summary>
        public Vec3 MulT(Vec3 v) => new Vec3(c0.Dot(v), c1.Dot(v), c2.Dot(v));

        /// <summary>Rotates v around the unit axis by angle (btQuaternion(axis, angle) rotation).</summary>
        public static Vec3 Rotate(Vec3 v, Vec3 axis, float angle)
        {
            float c = (float)Math.Cos(angle), s = (float)Math.Sin(angle);
            return v * c + axis.Cross(v) * s + axis * (axis.Dot(v) * (1 - c));
        }

        /// <summary>btMatrix3x3::getEulerYPR on the matrix whose columns are c0,c1,c2 (rows are m_el).</summary>
        public void GetEulerYPR(out float yaw, out float pitch, out float roll)
        {
            // m_el[r].component(c) == column c's component r
            float m00 = c0.x, m10 = c0.y, m20 = c0.z, m21 = c1.z, m22 = c2.z;
            yaw = (float)Math.Atan2(m10, m00);
            pitch = (float)Math.Asin(-m20);
            roll = (float)Math.Atan2(m21, m22);
            const float halfPi = 1.57079632679489661923f;
            if (Math.Abs(pitch) == halfPi)
            {
                yaw += yaw > 0 ? -(float)Math.PI : (float)Math.PI;
                roll += roll > 0 ? -(float)Math.PI : (float)Math.PI;
            }
        }
    }
}
