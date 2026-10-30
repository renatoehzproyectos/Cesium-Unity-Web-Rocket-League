// Minimal UnityEngine surface so the Runtime vehicle code can be compile-checked outside Unity (not shipped).
using System;
namespace UnityEngine
{
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 down => new Vector3(0, -1, 0); public static Vector3 up => new Vector3(0, 1, 0); public static Vector3 forward => new Vector3(0, 0, 1); public static Vector3 right => new Vector3(1, 0, 0); public static Vector3 zero => new Vector3();
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z); public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float s) => new Vector3(a.x * s, a.y * s, a.z * s); public static Vector3 operator *(float s, Vector3 a) => a * s; public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z); public static Vector3 operator /(Vector3 a, float s) => a * (1 / s);
        public float magnitude => (float)Math.Sqrt(x * x + y * y + z * z); public Vector3 normalized => this / magnitude;
        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x); public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 ProjectOnPlane(Vector3 v, Vector3 n) => v - n * Dot(v, n); public static Vector3 ClampMagnitude(Vector3 v, float m) => v; public static Vector3 RotateTowards(Vector3 a, Vector3 b, float c, float d) => a; }
    public struct Quaternion { public static Quaternion identity => new Quaternion(); public static Quaternion Inverse(Quaternion q) => q; public static Quaternion AngleAxis(float a, Vector3 axis) => new Quaternion(); public static Quaternion Euler(float x, float y, float z) => new Quaternion();
        public static Vector3 operator *(Quaternion q, Vector3 v) => v; public static Quaternion operator *(Quaternion a, Quaternion b) => a; public Vector3 eulerAngles => new Vector3(); }
    public static class Mathf { public const float Rad2Deg = 57.29578f; public static float Clamp(float v, float a, float b) => Math.Max(a, Math.Min(b, v)); public static float Clamp01(float v) => Clamp(v, 0, 1); public static float Abs(float v) => Math.Abs(v);
        public static float MoveTowards(float a, float b, float d) => Math.Abs(b - a) <= d ? b : a + Math.Sign(b - a) * d; public static float Pow(float a, float b) => (float)Math.Pow(a, b); public static float Sqrt(float a) => (float)Math.Sqrt(a); public static float Atan2(float a, float b) => (float)Math.Atan2(a, b);
        public static float Min(float a, float b) => Math.Min(a, b); public static float Max(float a, float b) => Math.Max(a, b); public static float Lerp(float a, float b, float t) => a + (b - a) * t; public static float Exp(float a) => (float)Math.Exp(a); public static float Sin(float a) => (float)Math.Sin(a); public static float Cos(float a) => (float)Math.Cos(a); }
    public enum ForceMode { Force } public enum QueryTriggerInteraction { Ignore }
    public struct RaycastHit { public float distance; public Vector3 point, normal; }
    public static class Physics { public static bool Raycast(Vector3 o, Vector3 d, out RaycastHit h, float dist, int mask, QueryTriggerInteraction q) { h = default; return false; } public static bool Raycast(Vector3 o, Vector3 d, float dist, int mask, QueryTriggerInteraction q) => false; }
    public class Rigidbody { public void AddRelativeTorque(Vector3 f, ForceMode m) { } public Vector3 position, linearVelocity, angularVelocity, inertiaTensor; public Quaternion rotation, inertiaTensorRotation; public float mass; public void AddForce(Vector3 f, ForceMode m) { } public void AddTorque(Vector3 f, ForceMode m) { } public void AddForceAtPosition(Vector3 f, Vector3 p, ForceMode m) { } }
    public class TextAsset { public string text; } public static class Resources { public static T Load<T>(string n) where T : class => null; }
    public static class Debug { public static void Log(object o) { } }
}
namespace RocketWorld
{
    public static class PhysicsSetup { public static int TerrainMask => 0; }
    public class CarInput { public struct Controls { public float throttle, steer, pitch, yaw, roll; public bool jump, boost, handbrake, leverSet; public float lever, vert; public bool hyper, wheelBrake; } }
    public class RocketCar { public float HyperMul => 1f; }
    public class VehicleDriver { public RocketCar car; public VehicleViz Viz; public float HeightMeters(UnityEngine.Rigidbody rb) => 0f; public void ApplyHyperDrive(UnityEngine.Rigidbody rb, UnityEngine.Vector3 d, float v, float dt, bool s) { } }
}
