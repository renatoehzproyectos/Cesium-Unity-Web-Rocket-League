using System.Collections.Generic;
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
        public PlaneIntro intro;
        public RocketBall ball;
        public bool infiniteBoost = true;
        public float spawnHeadingDeg;

        /// <summary>Every car in the scene (local, remote, puppets).</summary>
        public static readonly List<RocketCar> All = new List<RocketCar>();

        // ---- multiplayer ----
        /// <summary>False = puppet (multiplayer client): never stepped, its pose comes from host snapshots.</summary>
        public bool simulated = true;
        /// <summary>Remote player's controls on the host (the local car reads <see cref="input"/>).</summary>
        public bool useNetControls;
        public CarInput.Controls netControls;
        public Vector3 netVelocity;                 // puppets: velocity from snapshots (HUD / camera)
        public int ballExtraTick = -10;             // RocketSim keeps the last extra-hit tick per car
        public int slot;
        public string playerName = "";

        public Phase CurrentPhase { get; private set; }
        public string Status { get; private set; } = "";
        public RocketVehicle Vehicle { get; private set; }
        public CarState State => Vehicle.state;
        public bool GyroscopicCompensation => _body.cancelGyroscopic;
        public Vector3 UnityVelocity => simulated ? _rb.linearVelocity : netVelocity;
        public float SpeedMS => simulated ? _rb.linearVelocity.magnitude : netVelocity.magnitude;
        public Rigidbody Body => _rb;
        public BoxCollider HitCollider { get; private set; }

        /// <summary>Shown by the HUD in the middle of the screen (empty = nothing).</summary>
        public void SetStatus(string s) { if (CurrentPhase == Phase.Running) Status = s; }

        const float Tick = 1f / 120f;
        Rigidbody _rb;
        PhysxBody _body;
        CarConfig _cfg;
        Vector3 _prevPos, _currPos;
        Quaternion _prevRot = Quaternion.identity, _currRot = Quaternion.identity;
        bool _resync, _rescue;
        float _waitStart, _stableSince = -1f;
        BoostFx _boostFx;
        // Soft hold: freeze the car in place while Cesium LOD/colliders settle (SSE change). No respawn, no fall.
        bool _softHold;
        float _softHoldUntil;
        bool _held;                                  // menu is open: do not start yet
        bool _spawnCol, _spawnRel; Vector3 _spawnPos; float _spawnHeading;
        static bool _gyroKnown, _gyro;               // PhysX calibration steps the world: do it once, not for every car

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
            HitCollider = box;
            gameObject.AddComponent<CarContactSensor>().car = this;

            _body = new PhysxBody(_rb, RL.CarMassBt, new Vec3(1f / inertia.x, 1f / inertia.y, 1f / inertia.z));
            if (!_gyroKnown)
            {
                _gyro = PhysxCalibration.Run(inertia, out float delta); _gyroKnown = true;
                Debug.Log($"[RocketWorld] PhysX gyroscopic torque detected: {_gyro} (dw {delta:0.000} rad/s over 0.25 s) -> compensation {(_gyro ? "ON" : "off")}");
            }
            _body.cancelGyroscopic = _gyro;
            All.Add(this);
            Vehicle = new RocketVehicle(_body, new PhysxRayCaster(), _cfg);
        }

        void Start()
        {
            if (georeference != null) georeference.changed += OnGeoChanged;
            if (visual != null) _boostFx = visual.GetComponent<BoostFx>();
            if (_held) return;
            BeginInitial();
        }

        void BeginInitial()
        {
            if (!simulated) return;
            if (intro != null && intro.Begin(this)) return;          // opening sequence: start inside the cargo plane
            BeginWaiting();
        }

        /// <summary>Called by GameRoot before Start when a start menu is shown: the car waits until <see cref="ReleaseStart"/>.</summary>
        public void HoldStart() { _held = true; if (_rb != null) _rb.isKinematic = true; }
        public void ReleaseStart() { if (!_held) return; _held = false; BeginInitial(); }
        public bool Held => _held;

        void OnDestroy() { All.Remove(this); if (georeference != null) georeference.changed -= OnGeoChanged; }

        /// <summary>Name tags are drawn for cars that are actually placed in the world.</summary>
        public bool CanDrawTag() { return CurrentPhase == Phase.Running; }

        public bool CanStep => simulated && !_held && CurrentPhase == Phase.Running && !_softHold;

        /// <summary>Switch between a car this machine simulates and a puppet that follows host snapshots.</summary>
        public void SetSimulated(bool sim)
        {
            if (simulated == sim) return;
            simulated = sim; _softHold = false;
            if (!sim)
            {
                if (_rb != null) { _rb.isKinematic = true; }
                CurrentPhase = Phase.WaitingForGround; Status = "";
            }
        }

        /// <summary>Host: a remote player's car appears on the ground at a column (relative = just below the host car, else the start column).</summary>
        public void SetSpawnColumn(Vector3 groundHint, bool relative, float headingDeg)
        {
            _spawnCol = true; _spawnRel = relative; _spawnPos = groundHint; _spawnHeading = headingDeg;
            transform.position = groundHint;            // tile colliders are kept around waiting cars too (extra foci)
            BeginWaiting();
        }

        /// <summary>True when this car may be teleported (simulated here, driving, not inside the cargo plane).</summary>
        public bool CanTeleport => simulated && !_held && CurrentPhase == Phase.Running && !(intro != null && intro.BlocksRescue);

        static Quaternion Level(Quaternion r)
        {
            var f = r * Vector3.forward; f.y = 0f;
            return f.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(f.normalized, Vector3.up) : Quaternion.identity;
        }

        /// <summary>Straight up, same heading, level, stopped. The car then falls.</summary>
        public bool TeleportAbove(float meters)
        {
            if (!CanTeleport) return false;
            PlaceDirect(_rb.position + Vector3.up * meters, Level(_rb.rotation));
            return true;
        }

        /// <summary>Right next to another car (same heading, a little above so it does not clip the ground).</summary>
        public bool TeleportBeside(RocketCar other)
        {
            if (!CanTeleport || other == null || other == this) return false;
            var orot = other.Body != null ? other.Body.rotation : other.transform.rotation;
            var opos = other.Body != null ? other.Body.position : other.transform.position;
            var lvl = Level(orot);
            PlaceDirect(opos + lvl * Vector3.right * 4f + Vector3.up * 0.6f, lvl);
            return true;
        }

        /// <summary>Rescue (R): back to the ground below the car, same heading.</summary>
        public void Rescue() { if (CurrentPhase == Phase.Running && simulated) { _rescue = true; BeginWaiting(); } }

        /// <summary>Puppet pose from a snapshot (multiplayer clients).</summary>
        public void ApplyPuppetPose(Vector3 pos, Quaternion rot, Vector3 vel, bool onGround, bool boosting, bool running)
        {
            transform.SetPositionAndRotation(pos, rot);
            if (visual != null) visual.SetPositionAndRotation(pos, rot);
            netVelocity = vel;
            Vehicle.state.isOnGround = onGround; Vehicle.state.isBoosting = boosting;
            CurrentPhase = running ? Phase.Running : Phase.WaitingForGround;
            Status = running ? "" : "Waiting for the host…";
            if (_boostFx != null) _boostFx.SetBoosting(boosting && running);
        }
        void OnGeoChanged() { _resync = true; }

        public void ReportWorldContact(Vector3 unityNormal) { Vehicle.NotifyWorldContact(PhysicsSetup.ToSim(unityNormal)); }

        /// <summary>Back to "waiting for ground": atCurrentPosition keeps XZ and heading (rescue), otherwise spawns at the georeference origin.</summary>
        public void Respawn(bool atCurrentPosition)
        {
            _softHold = false;
            _rescue = atCurrentPosition;
            if (!atCurrentPosition) transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            BeginWaiting();
        }

        /// <summary>
        /// Freeze the car at its current pose while tile colliders are rebuilt (e.g. after an SSE change).
        /// Does not teleport, does not enter WaitingForGround — the car simply stays put until nearby
        /// colliders exist again (or a short timeout), then continues driving.
        /// </summary>
        public void SoftHoldForTerrain(float seconds = 1.25f)
        {
            if (CurrentPhase != Phase.Running || _rb == null) return;
            if (colliderBudget != null) colliderBudget.burst = true;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.isKinematic = true;
            _softHold = true;
            _softHoldUntil = Time.realtimeSinceStartup + Mathf.Max(0.3f, seconds);
            Status = "";
        }

        void BeginWaiting()
        {
            if (colliderBudget != null) colliderBudget.burst = true;
            CurrentPhase = Phase.WaitingForGround; _waitStart = Time.realtimeSinceStartup; _stableSince = -1f;
            if (_rb != null) { _rb.linearVelocity = Vector3.zero; _rb.angularVelocity = Vector3.zero; _rb.isKinematic = true; }
        }

        void Update()
        {
            if (!simulated || _held) return;                  // puppet: PlayerNet moves it; held: waiting for the start menu
            if (input != null && input.ResetPressed && CurrentPhase == Phase.Running && !(intro != null && intro.TryResetInBay())) { _rescue = true; BeginWaiting(); }
            if (CurrentPhase == Phase.WaitingForGround) { TrySpawn(); return; }
            if (_resync) ApplyOriginShift();

            if (_softHold)
            {
                // Stay frozen until nearby colliders are back or the timeout elapses.
                bool collidersReady = colliderBudget != null && colliderBudget.WithCollider > 0;
                bool timedOut = Time.realtimeSinceStartup >= _softHoldUntil;
                if (collidersReady || timedOut)
                {
                    // Optional micro-snap to ground if we are slightly floating / sinking after LOD swap.
                    if (Physics.Raycast(transform.position + Vector3.up * 3f, Vector3.down, out var hit, 8f,
                            PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore) && hit.normal.y > 0.5f)
                    {
                        var pos = hit.point + Vector3.up * 0.34f;
                        transform.position = pos;
                        _rb.position = pos;
                        _prevPos = _currPos = pos;
                    }
                    _rb.isKinematic = false;
                    _rb.linearVelocity = Vector3.zero;
                    _rb.angularVelocity = Vector3.zero;
                    if (colliderBudget != null) colliderBudget.burst = false;
                    _softHold = false;
                    Physics.SyncTransforms();
                }
                else
                {
                    ApplyVisual(0f);
                    return;
                }
            }

            // The fixed-tick loop (PreStep / PostStep, one shared Physics.Simulate) lives in CarSimLoop.
        }

        /// <summary>First half of a tick (RocketSim PreTick), called by <see cref="CarSimLoop"/> for every steppable car.</summary>
        public void PreStep()
        {
            var c = useNetControls ? netControls : (input != null ? input.controls : default);
            Vehicle.controls = new RocketWorld.Sim.CarControls
            {
                throttle = c.throttle, steer = c.steer, pitch = c.pitch, yaw = c.yaw, roll = c.roll,
                jump = c.jump, boost = c.boost, handbrake = c.handbrake,
            };
            if (infiniteBoost) Vehicle.state.boost = RL.BoostMax;

            _body.Pull();
            Vehicle.PreTick(Tick);                            // btVehicleRL + Car::_PreTickUpdate
            _body.Push();
        }

        /// <summary>Second half of a tick, after the shared Physics.Simulate.</summary>
        public void PostStep()
        {
            _body.Pull();
            Vehicle.PostTick(Tick);
            Vehicle.FinishTick();                             // speed / angular speed limits
            _body.PushVelocity();

            _prevPos = _currPos; _prevRot = _currRot;
            _currPos = _rb.position; _currRot = _rb.rotation;
        }

        public void ApplyVisual(float alpha)
        {
            if (visual == null) return;
            visual.SetPositionAndRotation(Vector3.LerpUnclamped(_prevPos, _currPos, alpha), Quaternion.Slerp(_prevRot, _currRot, alpha));
            if (_boostFx != null) _boostFx.SetBoosting(Vehicle.state.isBoosting);
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
            if (ball != null) ball.OnOriginShift(dq);
        }

        void TrySpawn()
        {
            float waited = Time.realtimeSinceStartup - _waitStart;
            float progress = tileset != null ? tileset.ComputeLoadProgress() : 100f;
            Vector3 origin; float range;
            if (_spawnCol) { origin = _spawnPos + Vector3.up * (_spawnRel ? 60f : 2500f); range = _spawnRel ? 400f : 5000f; }
            else if (_rescue) { origin = transform.position + Vector3.up * 60f; range = 400f; }
            else { origin = new Vector3(0f, 2500f, 0f); range = 5000f; }
            bool ground = Physics.Raycast(origin, Vector3.down, out var hit, range, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore) && hit.normal.y > 0.5f;
            Status = ground ? $"Loading city… {progress:0}%" : $"Waiting for terrain… {progress:0}%";
            if (!ground) { _stableSince = -1f; return; }
            if (progress >= 99.9f) { if (_stableSince < 0) _stableSince = Time.realtimeSinceStartup; } else _stableSince = -1f;
            bool settled = _stableSince > 0 && Time.realtimeSinceStartup - _stableSince > 0.6f;
            if (settled || waited > 30f || _rescue || (_spawnCol && _spawnRel)) PlaceAt(hit.point);
        }

        void PlaceAt(Vector3 groundPoint)
        {
            var heading = _rescue ? Vector3.ProjectOnPlane(transform.forward, Vector3.up) : Quaternion.Euler(0f, _spawnCol ? _spawnHeading : spawnHeadingDeg, 0f) * Vector3.forward;
            if (heading.sqrMagnitude < 1e-3f) heading = Vector3.forward;
            var rot = Quaternion.LookRotation(heading.normalized, Vector3.up);
            var pos = groundPoint + Vector3.up * (0.34f + (_rescue ? 0.5f : 0.12f));      // rest height of the Octane CoM is 17 uu = 0.34 m
            if (colliderBudget != null) colliderBudget.burst = false;
            PlaceDirect(pos, rot);
        }

        /// <summary>Puts the car at an exact pose and starts driving immediately (no ground search). Used by the plane intro.</summary>
        public void PlaceDirect(Vector3 pos, Quaternion rot)
        {
            _rb.isKinematic = false;
            transform.SetPositionAndRotation(pos, rot);
            _rb.position = pos; _rb.rotation = rot;
            _rb.linearVelocity = Vector3.zero; _rb.angularVelocity = Vector3.zero;
            Physics.SyncTransforms();
            _body.Pull();
            Vehicle.OnStateSet(true);
            _prevPos = _currPos = pos; _prevRot = _currRot = rot;
            _rescue = false; _spawnCol = false; _resync = false; Status = "";
            CurrentPhase = Phase.Running;
            ApplyVisual(0f);
        }
    }
}
