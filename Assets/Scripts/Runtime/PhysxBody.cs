using RocketWorld.Sim;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// IBody over a Unity Rigidbody (PhysX). State is read once per tick (Pull), edited in sim space during PreTick, then written back (Push).
    /// Impulses change the cached velocity immediately, like Bullet's applyImpulse; forces/torques go through AddForce/AddTorque and are
    /// integrated by Physics.Simulate. The body's local axes are: forward = sim X, right = sim Y, up = sim Z.
    /// </summary>
    public sealed class PhysxBody : IBody
    {
        readonly Rigidbody _rb;
        readonly float _mass;
        readonly Vec3 _invInertiaLocal;
        Vec3 _pos, _vel, _ang;
        Mat3 _basis = Mat3.Identity;
        bool _dirty;
        /// <summary>Cancel PhysX's gyroscopic term (RocketSim runs Bullet without it). Set by PhysxCalibration.</summary>
        public bool cancelGyroscopic;

        public PhysxBody(Rigidbody rb, float mass, Vec3 invInertiaLocal) { _rb = rb; _mass = mass; _invInertiaLocal = invInertiaLocal; }

        public Vec3 Position => _pos;
        public Mat3 Basis => _basis;
        public Vec3 LinearVelocity { get => _vel; set { _vel = value; _dirty = true; } }
        public Vec3 AngularVelocity { get => _ang; set { _ang = value; _dirty = true; } }
        public float Mass => _mass;
        public Vec3 InvInertiaLocal => _invInertiaLocal;

        public void ApplyCentralForce(Vec3 f) => _rb.AddForce(PhysicsSetup.ToUnity(f), ForceMode.Force);
        public void ApplyTorque(Vec3 t) => _rb.AddTorque(PhysicsSetup.ToUnity(t), ForceMode.Force);
        public void ApplyCentralImpulse(Vec3 j) { _vel += j * (1f / _mass); _dirty = true; }
        public void ApplyImpulse(Vec3 j, Vec3 rel)
        {
            _vel += j * (1f / _mass);
            _ang += _basis.Mul(_invInertiaLocal * _basis.MulT(rel.Cross(j)));      // invInertiaWorld * (rel x j)
            _dirty = true;
        }

        public void Pull()
        {
            var q = _rb.rotation;
            _pos = PhysicsSetup.ToSim(_rb.position);
            _basis = new Mat3(PhysicsSetup.ToSim(q * Vector3.forward), PhysicsSetup.ToSim(q * Vector3.right), PhysicsSetup.ToSim(q * Vector3.up));
            _vel = PhysicsSetup.ToSim(_rb.linearVelocity);
            _ang = PhysicsSetup.ToSim(_rb.angularVelocity);
            _dirty = false;
        }

        /// <summary>Write velocities back (only if the model changed them) and add the gyroscopic compensation torque.</summary>
        public void Push()
        {
            if (_dirty) { _rb.linearVelocity = PhysicsSetup.ToUnity(_vel); _rb.angularVelocity = PhysicsSetup.ToUnity(_ang); _dirty = false; }
            if (cancelGyroscopic)
            {
                // PhysX integrates dw/dt = I^-1 (tau - w x I w); Bullet-without-gyro integrates dw/dt = I^-1 tau. Adding tau = w x (I w) cancels the extra term.
                var l = _basis.MulT(_ang);
                var iw = _basis.Mul(new Vec3(l.x / _invInertiaLocal.x, l.y / _invInertiaLocal.y, l.z / _invInertiaLocal.z));
                _rb.AddTorque(PhysicsSetup.ToUnity(_ang.Cross(iw)), ForceMode.Force);
            }
        }

        public void PushVelocity()
        {
            if (!_dirty) return;
            _rb.linearVelocity = PhysicsSetup.ToUnity(_vel); _rb.angularVelocity = PhysicsSetup.ToUnity(_ang); _dirty = false;
        }
    }

    public sealed class PhysxRayCaster : IRayCaster
    {
        public int mask = PhysicsSetup.WorldMask;
        public bool Cast(Vec3 from, Vec3 to, out RayHit hit)
        {
            var o = PhysicsSetup.ToUnity(from);
            var d = PhysicsSetup.ToUnity(to - from);
            float len = d.magnitude;
            if (len > 1e-6f && Physics.Raycast(o, d / len, out var h, len, mask, QueryTriggerInteraction.Ignore))
            {
                hit = new RayHit { point = PhysicsSetup.ToSim(h.point), normal = PhysicsSetup.ToSim(h.normal), fraction = h.distance / len };
                return true;
            }
            hit = default;
            return false;
        }
    }

    /// <summary>Reports car-vs-world contacts to the vehicle (RocketSim's Arena::_BtCallback_OnCarWorldCollision): drives auto-flip and auto-roll.</summary>
    public sealed class CarContactSensor : MonoBehaviour
    {
        public RocketCar car;
        void OnCollisionEnter(Collision c) { Report(c); }
        void OnCollisionStay(Collision c) { Report(c); }
        void Report(Collision c)
        {
            if (car == null) return;
            for (int i = 0; i < c.contactCount; i++) car.ReportWorldContact(c.GetContact(i).normal);   // normal points out of the world surface, like Bullet's normalOnB
        }
    }

    /// <summary>
    /// Detects at startup whether this PhysX build adds gyroscopic torque to rigid bodies (RocketSim/Bullet runs without it), by spinning
    /// a torque-free asymmetric body: with no gyroscopic term its world angular velocity stays constant.
    /// </summary>
    public static class PhysxCalibration
    {
        public static bool Run(Vec3 simInertia, out float deltaRadPerSec)
        {
            var go = new GameObject("rw-calibration") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.position = new Vector3(0f, -50000f, 0f);
            var rb = go.AddComponent<Rigidbody>();
            rb.useGravity = false; rb.mass = RL.CarMassBt;
            rb.automaticCenterOfMass = false; rb.centerOfMass = Vector3.zero;
            rb.automaticInertiaTensor = false; rb.inertiaTensor = new Vector3(simInertia.y, simInertia.z, simInertia.x); rb.inertiaTensorRotation = Quaternion.identity;
            rb.linearDamping = 0f; rb.angularDamping = 0f; rb.maxAngularVelocity = 100f; rb.sleepThreshold = 0f;
            var w0 = new Vector3(3f, 2f, 4f);
            rb.angularVelocity = w0;
            for (int i = 0; i < 30; i++) Physics.Simulate(1f / 120f);
            deltaRadPerSec = (rb.angularVelocity - w0).magnitude;
            Object.Destroy(go);
            return deltaRadPerSec > 0.02f;
        }
    }
}
