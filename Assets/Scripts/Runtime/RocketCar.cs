using CesiumForUnity;
using RocketWorld.Sim;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// The drivable car: a PhysX Rigidbody (exact collision against Cesium tile colliders) driven by the RocketSim vehicle model
    /// (RocketWorld.Sim.RocketVehicle) at a fixed 120 Hz. Tick order is RocketSim's Arena::Step:
    /// PreTick (vehicle forces) -> Physics.Simulate -> PostTick -> velocity limits. Rendering is interpolated between ticks.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class RocketCar : MonoBehaviour
    {
        public enum Phase { WaitingForGround, Running }

        public CesiumGeoreference georeference;
        public Cesium3DTileset tileset;
        public TileColliderBudget colliderBudget;
        public CarInput input;
        public Transform visual;
        public bool infiniteBoost = true;
        public float spawnHeadingDeg;

        public Phase CurrentPhase { get; private set; }
        public string Status { get; private set; } = "";
        public RocketVehicle Vehicle { get; private set; }
        public CarState State => Vehicle.state;
        public bool GyroscopicCompensation => _body.cancelGyroscopic;
        public Vector3 UnityVelocity => _rb.linearVelocity;
        public float SpeedMS => _rb.linearVelocity.magnitude;
        public Rigidbody Body => _rb;

        const float Tick = 1f / 120f;
        Rigidbody _rb;
        PhysxBody _body;
        CarConfig _cfg;
        Vector3 _prevPos, _currPos;
        Quaternion _prevRot = Quaternion.identity, _currRot = Quaternion.identity;
        float _acc;
        bool _resync, _rescue;
        float _waitStart, _stableSince = -1f;
        Transform _flame;

        void Awake()
        {
            PhysicsSetup.Apply();
            _cfg = CarConfig.Octane();
            gameObject.layer = PhysicsSetup.CarLayer;

            var inertia = _cfg.LocalInertia(RL.CarMassBt);
            var eff = _cfg.EffectiveHitboxSize();
            _rb = gameObject.AddComponent<Rigidbody>();
            _rb.mass = RL.CarMassBt;
            _rb.automaticCenterOfMass = false; _rb.centerOfMass = Vector3.zero;              // CoM == body origin, hitbox is offset from it
            _rb.automaticInertiaTensor = false;
            _rb.inertiaTensor = new Vector3(inertia.y, inertia.z, inertia.x);                 // sim (X,Y,Z) -> Unity local (Z,X,Y)
            _rb.inertiaTensorRotation = Quaternion.identity;
            _rb.linearDamping = 0f; _rb.angularDamping = 0f;
            _rb.maxLinearVelocity = 1000f; _rb.maxAngularVelocity = 1000f;                   // RocketSim clamps itself (FinishTick)
            _rb.maxDepenetrationVelocity = 5f;
            _rb.interpolation = RigidbodyInterpolation.None;                                 // we interpolate the visual ourselves
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _rb.sleepThreshold = 0f;
            _rb.solverIterations = 10; _rb.solverVelocityIterations = 1;
            _rb.useGravity = true;

            var box = gameObject.AddComponent<BoxCollider>();
            box.size = new Vector3(eff.y, eff.z, eff.x);
            box.center = PhysicsSetup.ToUnity(_cfg.hitboxPosOffset * RL.UuToBt);
            box.sharedMaterial = PhysicsSetup.CarMaterial;
            gameObject.AddComponent<CarContactSensor>().car = this;

            _body = new PhysxBody(_rb, RL.CarMassBt, new Vec3(1f / inertia.x, 1f / inertia.y, 1f / inertia.z));
            _body.cancelGyroscopic = PhysxCalibration.Run(inertia, out float delta);
            Debug.Log($"[RocketWorld] PhysX gyroscopic torque detected: {_body.cancelGyroscopic} (dw {delta:0.000} rad/s over 0.25 s) -> compensation {(_body.cancelGyroscopic ? "ON" : "off")}");
            Vehicle = new RocketVehicle(_body, new PhysxRayCaster(), _cfg);
        }

        void Start()
        {
            if (georeference != null) georeference.changed += OnGeoChanged;
            if (visual != null) _flame = visual.Find("Flame");
            BeginWaiting();
        }

        void OnDestroy() { if (georeference != null) georeference.changed -= OnGeoChanged; }
        void OnGeoChanged() { _resync = true; }

        public void ReportWorldContact(Vector3 unityNormal) { Vehicle.NotifyWorldContact(PhysicsSetup.ToSim(unityNormal)); }

        /// <summary>Back to "waiting for ground": atCurrentPosition keeps XZ and heading (rescue), otherwise spawns at the georeference origin.</summary>
        public void Respawn(bool atCurrentPosition)
        {
            _rescue = atCurrentPosition;
            if (!atCurrentPosition) transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            BeginWaiting();
        }

        void BeginWaiting()
        {
            if (colliderBudget != null) colliderBudget.burst = true;
            CurrentPhase = Phase.WaitingForGround; _waitStart = Time.realtimeSinceStartup; _stableSince = -1f;
            if (_rb != null) { _rb.linearVelocity = Vector3.zero; _rb.angularVelocity = Vector3.zero; _rb.isKinematic = true; }
        }

        void Update()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (input != null && input.ResetPressed && CurrentPhase == Phase.Running) { _rescue = true; BeginWaiting(); }
            if (CurrentPhase == Phase.WaitingForGround) { TrySpawn(); return; }
            if (_resync) ApplyOriginShift();

            _acc += dt;
            int ticks = Mathf.Min((int)(_acc / Tick), 8);
            _acc -= ticks * Tick;
            if (_acc > Tick) _acc = 0f;                       // spiral-of-death guard
            for (int i = 0; i < ticks; i++) RunTick();
            ApplyVisual(_acc / Tick);
        }

        void RunTick()
        {
            var c = input != null ? input.controls : default;
            Vehicle.controls = new RocketWorld.Sim.CarControls
            {
                throttle = c.throttle, steer = c.steer, pitch = c.pitch, yaw = c.yaw, roll = c.roll,
                jump = c.jump, boost = c.boost, handbrake = c.handbrake,
            };
            if (infiniteBoost) Vehicle.state.boost = RL.BoostMax;

            _body.Pull();
            Vehicle.PreTick(Tick);                            // btVehicleRL + Car::_PreTickUpdate
            _body.Push();
            Physics.Simulate(Tick);                           // gravity, forces, exact contact against tile colliders (OnCollision* -> ReportWorldContact)
            _body.Pull();
            Vehicle.PostTick(Tick);
            Vehicle.FinishTick();                             // speed / angular speed limits
            _body.PushVelocity();

            _prevPos = _currPos; _prevRot = _currRot;
            _currPos = _rb.position; _currRot = _rb.rotation;
        }

        void ApplyVisual(float alpha)
        {
            if (visual == null) return;
            visual.SetPositionAndRotation(Vector3.LerpUnclamped(_prevPos, _currPos, alpha), Quaternion.Slerp(_prevRot, _currRot, alpha));
            if (_flame != null)
            {
                bool on = Vehicle.state.isBoosting;
                if (_flame.gameObject.activeSelf != on) _flame.gameObject.SetActive(on);
                if (on) { float k = 1f + 0.25f * Mathf.Sin(Time.time * 60f); _flame.localScale = new Vector3(0.45f * k, 0.45f * k, 1.6f * (1f + 0.2f * Mathf.Sin(Time.time * 47f))); }
            }
        }

        // CesiumOriginShift moved the georeference origin: anchors re-expressed our transform in the new frame; the Rigidbody follows,
        // with velocities rotated by the frame change (tile colliders were re-placed by Cesium the same way).
        void ApplyOriginShift()
        {
            _resync = false;
            Physics.SyncTransforms();
            Quaternion dq = transform.rotation * Quaternion.Inverse(_currRot);
            _rb.position = transform.position; _rb.rotation = transform.rotation;
            _rb.linearVelocity = dq * _rb.linearVelocity; _rb.angularVelocity = dq * _rb.angularVelocity;
            _prevPos = _currPos = _rb.position; _prevRot = _currRot = _rb.rotation;
        }

        void TrySpawn()
        {
            float waited = Time.realtimeSinceStartup - _waitStart;
            float progress = tileset != null ? tileset.ComputeLoadProgress() : 100f;
            var origin = _rescue ? transform.position + Vector3.up * 60f : new Vector3(0f, 2500f, 0f);
            bool ground = Physics.Raycast(origin, Vector3.down, out var hit, _rescue ? 400f : 5000f, PhysicsSetup.WorldMask, QueryTriggerInteraction.Ignore) && hit.normal.y > 0.5f;
            Status = ground ? $"Loading city… {progress:0}%" : $"Waiting for terrain… {progress:0}%";
            if (!ground) { _stableSince = -1f; return; }
            if (progress >= 99.9f) { if (_stableSince < 0) _stableSince = Time.realtimeSinceStartup; } else _stableSince = -1f;
            bool settled = _stableSince > 0 && Time.realtimeSinceStartup - _stableSince > 0.6f;
            if (settled || waited > 30f || _rescue) PlaceAt(hit.point);
        }

        void PlaceAt(Vector3 groundPoint)
        {
            var heading = _rescue ? Vector3.ProjectOnPlane(transform.forward, Vector3.up) : Quaternion.Euler(0f, spawnHeadingDeg, 0f) * Vector3.forward;
            if (heading.sqrMagnitude < 1e-3f) heading = Vector3.forward;
            var rot = Quaternion.LookRotation(heading.normalized, Vector3.up);
            var pos = groundPoint + Vector3.up * (0.34f + (_rescue ? 0.5f : 0.12f));      // rest height of the Octane CoM is 17 uu = 0.34 m
            _rb.isKinematic = false;
            transform.SetPositionAndRotation(pos, rot);
            _rb.position = pos; _rb.rotation = rot;
            _rb.linearVelocity = Vector3.zero; _rb.angularVelocity = Vector3.zero;
            Physics.SyncTransforms();
            _body.Pull();
            Vehicle.OnStateSet(true);
            _prevPos = _currPos = pos; _prevRot = _currRot = rot;
            _rescue = false; _acc = 0f; _resync = false; Status = "";
            if (colliderBudget != null) colliderBudget.burst = false;
            CurrentPhase = Phase.Running;
            ApplyVisual(0f);
        }
    }
}
