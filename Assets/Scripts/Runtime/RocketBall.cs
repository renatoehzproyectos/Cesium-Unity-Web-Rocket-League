using System.Collections.Generic;
using CesiumForUnity;
using RocketWorld.Sim;
using Unity.Collections;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// The Rocket League ball, as a PhysX Rigidbody that reproduces RocketSim's ball:
    ///  - mass 30, solid-sphere inertia, gravity -650 uu/s^2, linear damping (1-0.03)^t, no angular damping, max speed 6000 uu/s, max spin 6 rad/s;
    ///  - ball vs world: friction 0.35, restitution 0.6 (a "Maximum" physics material, so it wins over the world's 0.3 / 0.3);
    ///  - ball vs car: friction 2.0, restitution 0 (overridden per contact with Physics.ContactModifyEvent), plus RocketSim's extra hit impulse
    ///    (<see cref="RLBall.ExtraHitVelocity"/>) which is added to the ball's velocity just BEFORE the PhysX step of the tick in which the car
    ///    touches it, exactly where RocketSim's contact callback adds it before Bullet solves.
    /// RocketSim never updates the ball's orientation; PhysX integrates it from the angular velocity, so the ball spins and rolls on all three
    /// axes (pitch / yaw / roll) with the same spin-up as RocketSim (alpha = 2.5 mu g / r) and the same 6 rad/s cap.
    /// The ball does not exist until the player presses the Ball button (default B); pressing it again brings it back in front of the car.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public class RocketBall : MonoBehaviour
    {
        public GameRoot root;
        /// <summary>Multiplayer client: the ball is a visual only, its pose comes from host snapshots.</summary>
        public bool puppet;
        /// <summary>Fired (host / solo) when a car really hits the ball. The multiplayer layer turns it into score.</summary>
        public static event System.Action<RocketCar> BallHit;

        public const float VisualRadius = RLBall.RadiusUu * RL.UuToBt;                  // 1.825 m
        public const float ColliderRadius = RLBall.CollisionRadiusUu * RL.UuToBt;       // 1.863 m (rest height 93.15 uu)
        const float SummonDistance = 8f;                                                // m in front of the car
        const float LostDistance = 450f;                                                // m from the car (tile colliders only exist near it)
        const float Tick = 1f / 120f;

        public bool Exists => _go != null && _go.activeSelf;
        public Vector3 Position => _rb != null ? _rb.position : Vector3.zero;
        public Quaternion Rotation => _rb != null ? _rb.rotation : Quaternion.identity;
        public Vector3 Velocity => _rb != null ? _rb.linearVelocity : Vector3.zero;

        GameObject _go;
        Rigidbody _rb;
        SphereCollider _col;
        Transform _model;
        BallSparks _sparks;
        Vector3 _prevPos, _currPos;
        Quaternion _prevRot = Quaternion.identity, _currRot = Quaternion.identity;
        int _tick;
        float _hitCooldown;
        Vector3 _velBefore;
        bool _touchedCar, _hitFlag;
        Vector3 _contactPoint, _predictedContact;
        int _ballColId;
        readonly HashSet<int> _carCols = new HashSet<int>();
        RocketCar _hitCar, _touchCar;
        bool _subscribed;

        // ------------------------------------------------------------------------------------------------ public API

        /// <summary>Put the ball (back) in front of the car, at rest on the ground.</summary>
        public void Summon() { Summon(root != null ? root.carCtl : null); }

        public void Summon(RocketCar car)
        {
            if (puppet) return;
            if (car == null || car.CurrentPhase != RocketCar.Phase.Running || !car.simulated) return;
            if (!EnsureBuilt(car)) return;

            var t = car.transform;
            var fwd = Vector3.ProjectOnPlane(root.carVisual != null ? root.carVisual.forward : t.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-3f) fwd = Vector3.ProjectOnPlane(t.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-3f) fwd = Vector3.forward;
            fwd.Normalize();

            var p = t.position + fwd * SummonDistance;
            float y = t.position.y + 1f;
            foreach (float up in new[] { 1.2f, 6f })        // start inside the "room" first (so the plane's ceiling is not hit), then higher (hills)
            {
                var o = new Vector3(p.x, t.position.y + up, p.z);
                if (Physics.Raycast(o, Vector3.down, out var hit, 60f, PhysicsSetup.WorldMask, QueryTriggerInteraction.Ignore)) { y = hit.point.y + ColliderRadius + 0.05f; break; }
            }
            p.y = y;

            _go.SetActive(true);
            _rb.isKinematic = false;
            _go.transform.SetPositionAndRotation(p, Quaternion.identity);
            _rb.position = p; _rb.rotation = Quaternion.identity;
            _rb.linearVelocity = Vector3.zero; _rb.angularVelocity = Vector3.zero;
            Physics.SyncTransforms();
            _prevPos = _currPos = p; _prevRot = _currRot = Quaternion.identity;
            foreach (var c in RocketCar.All) c.ballExtraTick = _tick - 10;
            _hitCooldown = 0f;
            if (root.budget != null) root.budget.focus2 = _go.transform;
            ApplyVisual(1f);
            // one soft puff so it is obvious where it appeared
            if (_sparks != null) _sparks.Spawn(p, 0.35f);
        }

        public void Despawn()
        {
            if (_go != null) _go.SetActive(false);
            if (root != null && root.budget != null) root.budget.focus2 = null;
        }

        // ------------------------------------------------------------------------------------------------ build

        bool EnsureBuilt(RocketCar car)
        {
            if (_go != null) return true;
            if (root == null || root.georeference == null || car.HitCollider == null) return false;

            _go = new GameObject("Ball") { layer = PhysicsSetup.BallLayer };
            _go.transform.SetParent(root.georeference.transform, false);
            _go.AddComponent<CesiumGlobeAnchor>();                    // keeps it valid across origin shifts (like the car)

            float inertia = 0.4f * RLBall.MassBt * VisualRadius * VisualRadius;                    // solid sphere, as Bullet's btSphereShape
            _rb = _go.AddComponent<Rigidbody>();
            _rb.mass = RLBall.MassBt;
            _rb.automaticCenterOfMass = false; _rb.centerOfMass = Vector3.zero;
            _rb.automaticInertiaTensor = false; _rb.inertiaTensor = new Vector3(inertia, inertia, inertia); _rb.inertiaTensorRotation = Quaternion.identity;
            _rb.linearDamping = -Mathf.Log(1f - RLBall.Drag);                                      // Bullet: v *= (1 - 0.03)^dt
            _rb.angularDamping = 0f;                                                               // measured: the spin never decays in the air
            _rb.maxLinearVelocity = RLBall.MaxSpeedUu * RL.UuToBt;                                 // 120 m/s
            _rb.maxAngularVelocity = RLBall.MaxAngSpeed;                                           // 6 rad/s: friction can spin it up to this and no further
            _rb.maxDepenetrationVelocity = 10f;
            _rb.interpolation = RigidbodyInterpolation.None;                                       // visual is interpolated by hand, like the car
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;                 // 120 m/s is 1 m per tick
            _rb.sleepThreshold = 0f;
            _rb.solverIterations = 10; _rb.solverVelocityIterations = 1;
            _rb.useGravity = true;

            _col = _go.AddComponent<SphereCollider>();
            _col.radius = ColliderRadius;
            _col.sharedMaterial = PhysicsSetup.BallMaterial;
            _col.hasModifiableContacts = true;
            _ballColId = _col.GetInstanceID();
            RegisterCar(car);
            _go.AddComponent<BallContactSensor>().ball = this;

            BuildVisual();
            _sparks = new GameObject("BallSparks").AddComponent<BallSparks>();
            _sparks.transform.SetParent(transform, false);

            if (puppet) { _rb.isKinematic = true; _col.enabled = false; }
            else if (!_subscribed)
            {
                Physics.ContactModifyEvent += OnContactModify;
                Physics.ContactModifyEventCCD += OnContactModify;
                _subscribed = true;
            }
            return true;
        }

        void RegisterCar(RocketCar car)
        {
            if (car == null || car.HitCollider == null) return;
            if (_carCols.Add(car.HitCollider.GetInstanceID())) car.HitCollider.hasModifiableContacts = true;
        }

        /// <summary>Switch between simulating the ball (solo / host) and showing host snapshots (client). Rebuilds the ball object.</summary>
        public void SetPuppet(bool p)
        {
            if (puppet == p) return;
            puppet = p;
            if (_go != null)
            {
                if (_subscribed) { Physics.ContactModifyEvent -= OnContactModify; Physics.ContactModifyEventCCD -= OnContactModify; _subscribed = false; }
                Destroy(_go); _go = null; _rb = null; _col = null; _model = null; _carCols.Clear();
            }
            if (root != null && root.budget != null) root.budget.focus2 = null;
        }

        /// <summary>Client: pose from the host's ball snapshot.</summary>
        public void NetSet(bool exists, Vector3 pos, Quaternion rot)
        {
            if (!puppet || root == null) return;
            if (!exists) { if (_go != null && _go.activeSelf) _go.SetActive(false); return; }
            if (_go == null && !EnsureBuilt(root.carCtl)) return;
            if (!_go.activeSelf) _go.SetActive(true);
            _go.transform.SetPositionAndRotation(pos, rot);
            if (_model != null) _model.SetPositionAndRotation(pos, rot);
        }

        void OnDestroy()
        {
            if (_subscribed) { Physics.ContactModifyEvent -= OnContactModify; Physics.ContactModifyEventCCD -= OnContactModify; }
        }

        void BuildVisual()
        {
            _model = new GameObject("BallModel").transform;
            _model.SetParent(_go.transform, false);
            var prefab = Resources.Load<GameObject>("Ball/Ball");
            var tex = Resources.Load<Texture2D>("Ball/ball_d");
            var baseMat = Resources.Load<Material>("Materials/Ball");
            Material mat = null;
            if (baseMat != null)
            {
                mat = new Material(baseMat) { name = "RW_Ball" };                 // instance of the asset: its shader variants are in the build
                if (tex != null)
                {
                    tex.wrapMode = TextureWrapMode.Repeat;                         // the model's UVs run through [0..1] x [1..2]
                    if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                    if (mat.HasProperty("_EmissionMap")) mat.SetTexture("_EmissionMap", tex);
                    if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", Color.white * 0.9f);
                }
            }

            GameObject inst;
            if (prefab != null)
            {
                inst = Instantiate(prefab, Vector3.zero, Quaternion.identity);
                var rs = inst.GetComponentsInChildren<Renderer>(true);
                if (rs.Length > 0)
                {
                    var b = rs[0].bounds;
                    foreach (var r in rs) b.Encapsulate(r.bounds);
                    float k = VisualRadius * 2f / Mathf.Max(b.size.x, b.size.y, b.size.z, 1e-4f);
                    inst.transform.localScale = Vector3.one * k;
                    inst.transform.position = -b.center * k;                       // centre the model on the rigid body
                }
                foreach (var r in rs)
                {
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
                    if (mat != null) { var ms = new Material[r.sharedMaterials.Length]; for (int i = 0; i < ms.Length; i++) ms[i] = mat; r.sharedMaterials = ms; }
                }
            }
            else
            {
                inst = GameObject.CreatePrimitive(PrimitiveType.Sphere);            // fallback if the model is missing
                Destroy(inst.GetComponent<Collider>());
                inst.transform.localScale = Vector3.one * VisualRadius * 2f;
                var r = inst.GetComponent<Renderer>();
                if (mat != null) { mat.color = new Color(0.95f, 0.65f, 0.12f); r.sharedMaterial = mat; }
            }
            inst.transform.SetParent(_model, true);
            SetLayerRecursive(inst, 0);
        }

        static void SetLayerRecursive(GameObject go, int layer) { foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer; }

        // ------------------------------------------------------------------------------------------------ per frame

        void Update()
        {
            if (root == null) return;
            if (root.carInput != null && root.carInput.BallPressed) root.RequestBall();
            if (puppet || !Exists) return;
            // Tile colliders only exist around the cars (and the ball): if it is far from every car or fell out of the world, it is lost.
            float best = float.MaxValue;
            foreach (var c in RocketCar.All) if (c != null && c.simulated) best = Mathf.Min(best, (_rb.position - c.transform.position).sqrMagnitude);
            if (best == float.MaxValue) return;
            if (best > LostDistance * LostDistance || _rb.position.y < -600f + root.carCtl.transform.position.y) { Despawn(); }
        }

        /// <summary>Called by RocketCar every frame after its own visual: interpolated pose between the last two physics ticks.</summary>
        public void ApplyVisual(float alpha)
        {
            if (puppet || !Exists || _model == null) return;
            _model.SetPositionAndRotation(Vector3.LerpUnclamped(_prevPos, _currPos, alpha), Quaternion.Slerp(_prevRot, _currRot, alpha));
        }

        // ------------------------------------------------------------------------------------------------ physics ticks (called by CarSimLoop)

        /// <summary>Once per tick, before the cars: bookkeeping.</summary>
        public void BeginTick()
        {
            if (!Exists || puppet) return;
            _tick++;
            _hitCooldown = Mathf.Max(0f, _hitCooldown - Tick);
            _velBefore = _rb.linearVelocity;
            _hitFlag = false;
        }

        /// <summary>Before Physics.Simulate, once per car: RocketSim's extra hit impulse, if that car will touch the ball during this tick.</summary>
        public void CarPreTick(RocketCar car)
        {
            if (!Exists || puppet) return;
            RegisterCar(car);
            if (_tick <= car.ballExtraTick + RLBall.ExtraImpulseMinTickGap - 1) return;       // RocketSim: skip if last + 1 >= tick

            var hb = car.HitCollider;
            var ht = hb.transform;
            var center = ht.TransformPoint(hb.center);
            var half = hb.size * 0.5f;
            var inv = Quaternion.Inverse(ht.rotation);
            var p = _rb.position;
            var local = inv * (p - center);
            var cl = new Vector3(Mathf.Clamp(local.x, -half.x, half.x), Mathf.Clamp(local.y, -half.y, half.y), Mathf.Clamp(local.z, -half.z, half.z));
            var closest = center + ht.rotation * cl;
            var away = p - closest;
            float dist = away.magnitude - ColliderRadius;                                   // gap between the sphere and the hitbox
            var n = away.sqrMagnitude > 1e-8f ? away.normalized : (p - center).normalized;
            var relV = _rb.linearVelocity - car.Body.linearVelocity;
            float closing = Mathf.Max(0f, -Vector3.Dot(relV, n)) * Tick;                    // how far they approach during this tick
            if (dist - closing > 0.04f) return;                                             // not touching in this tick

            var carRot = car.Body.rotation;
            var relPos = (PhysicsSetup.ToSim(p) - PhysicsSetup.ToSim(car.Body.position)) * RL.BtToUu;
            var relVel = PhysicsSetup.ToSim(relV) * RL.BtToUu;
            var fwd = PhysicsSetup.ToSim(carRot * Vector3.forward);
            var dv = RLBall.ExtraHitVelocity(relPos, relVel, fwd);
            if (dv.IsZero()) return;
            _rb.linearVelocity += PhysicsSetup.ToUnity(dv * RL.UuToBt);
            car.ballExtraTick = _tick;
            _hitFlag = true; _hitCar = car;
            _predictedContact = closest;
        }

        /// <summary>After Physics.Simulate: remember the pose for interpolation and fire the sparks on a real touch.</summary>
        public void PostTick()
        {
            if (!Exists || puppet) return;
            _prevPos = _currPos; _prevRot = _currRot;
            _currPos = _rb.position; _currRot = _rb.rotation;

            if ((_touchedCar || _hitFlag) && _hitCooldown <= 0f)
            {
                float dBall = (_rb.linearVelocity - _velBefore).magnitude * RL.BtToUu;      // uu/s change of the ball's velocity in this tick
                if (dBall > 260f)
                {
                    float intensity = Mathf.Min(1f, 0.12f + dBall / 3200f);                 // soft touch ~ 0.2, hard shot ~ 1
                    var at = _touchedCar ? _contactPoint : _predictedContact;
                    if (_sparks != null) _sparks.Spawn(at, intensity);
                    _hitCooldown = 0.3f;
                    var who = _touchedCar ? _touchCar : _hitCar;
                    if (who != null) BallHit?.Invoke(who);
                }
            }
            _touchedCar = false; _touchCar = null; _hitCar = null;
        }

        /// <summary>Cesium moved the origin: the car already re-synced; do the same for the ball (dq = the frame change).</summary>
        public void OnOriginShift(Quaternion dq)
        {
            if (!Exists) return;
            Physics.SyncTransforms();
            _rb.position = _go.transform.position; _rb.rotation = _go.transform.rotation;
            _rb.linearVelocity = dq * _rb.linearVelocity; _rb.angularVelocity = dq * _rb.angularVelocity;
            _prevPos = _currPos = _rb.position; _prevRot = _currRot = _rb.rotation;
        }

        public void ReportCarContact(RocketCar car, Vector3 point) { _touchedCar = true; _touchCar = car; _contactPoint = point; }

        // ------------------------------------------------------------------------------------------------ contact modification

        /// <summary>RocketSim: car-ball contacts use friction 2.0 and restitution 0 (the bounce comes from the extra impulse instead).</summary>
        void OnContactModify(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs)
        {
            for (int i = 0; i < pairs.Length; i++)
            {
                var pair = pairs[i];
                int a = pair.colliderInstanceID, b = pair.otherColliderInstanceID;
                if (!((a == _ballColId && _carCols.Contains(b)) || (b == _ballColId && _carCols.Contains(a)))) continue;
                for (int c = 0; c < pair.contactCount; c++)
                {
                    pair.SetBounciness(c, RLBall.CarBallRestitution);
                    pair.SetStaticFriction(c, RLBall.CarBallFriction);
                    pair.SetDynamicFriction(c, RLBall.CarBallFriction);
                }
            }
        }
    }

    /// <summary>Reports contacts between the ball and the car (for the touch sparks).</summary>
    public sealed class BallContactSensor : MonoBehaviour
    {
        public RocketBall ball;
        void OnCollisionEnter(Collision c) { Report(c); }
        void OnCollisionStay(Collision c) { Report(c); }
        void Report(Collision c)
        {
            if (ball == null || c.contactCount == 0 || c.rigidbody == null) return;
            var car = c.rigidbody.GetComponent<RocketCar>();
            if (car != null) ball.ReportCarContact(car, c.GetContact(0).point);
        }
    }
}
