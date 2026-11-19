using CesiumForUnity;
using RocketWorld.Sim;
using Unity.Mathematics;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Vehicle mode for the F-35B, F-15E, helicopter, and DeLorean (the Cessna stays with <see cref="PlaneDriver"/>, the car with RocketSim).
    /// Owns everything around the physics: mode switching (the car's Rigidbody is re-purposed exactly like the plane does), compound box colliders,
    /// the 3-D model and its animations, the HUD numbers, and Hyper Boost. The per-tick physics itself is an <see cref="IVehicleModel"/>
    /// (currently <see cref="PlaceholderPhysics"/>; see <see cref="VehiclePhysicsFactory"/>).
    /// </summary>
    [DefaultExecutionOrder(-38)]
    public class VehicleDriver : MonoBehaviour
    {
        public RocketCar car;
        public bool Active { get; private set; }
        public VehicleKind Kind { get; private set; } = VehicleKind.Car;
        public VehicleSpec Spec { get; private set; }
        public VehicleViz Viz;
        public Rigidbody Body => car != null ? car.Body : null;

        // HUD
        public float SpeedMs { get; private set; }
        public float AltM { get; private set; }
        public float VsMs { get; private set; }
        public float Lever => _model != null ? _model.Lever : 0f;
        public bool OnGround => _model != null && _model.OnGround;
        public bool Stalled => _model != null && _model.Stalled;
        public float AlphaDeg => _model != null ? _model.AlphaDeg : 0f;
        public string Status => _model != null ? _model.Status : "";
        public string Info => Active ? $"{Spec.name}  {SpeedMs * 3.6f:0} km/h  alt {AltM:0} m  vs {VsMs:0.0}  {Status}" : "vehicle off";

        sealed class Built { public GameObject cols, hold; public Transform hinge; public VehicleVisual vis; public IVehicleModel model; public BoxCollider[] hull; }
        readonly Built[] _built = new Built[16];
        Transform _visRoot;
        IVehicleModel _model; Built _cur;
        float _lastAlt; bool _hasAlt;
        float _carMass; Vector3 _carInertia; Quaternion _carInertiaRot; bool _carGravity; float _carMaxLin;
        Renderer[] _hidden;
        float _ramp, _rampTarget; bool _crashed;
        Vector3 _preV; readonly Collider[] _ovl = new Collider[4];
        /// <summary>Antonov rear ramp: 0 = closed .. 1 = open (replicated to the other players through the pose snapshot).</summary>
        public float RampOpen01 => _ramp;
        public bool HasRamp => Active && Spec != null && Spec.hasRamp;

        void Awake() { if (car == null) car = GetComponent<RocketCar>(); }

        // ------------------------------------------------------------------------------------------ mode switch
        /// <summary>Switch to a vehicle (Car = back to the car, Cessna = the JSBSim plane).</summary>
        public void Select(VehicleKind k)
        {
            if (car == null) return;
            if (k == Kind && (Active || k == VehicleKind.Car) && !(k == VehicleKind.Cessna && !car.InPlane)) return;
            if (Active) Leave();
            if (car.plane != null && car.plane.Active && k != VehicleKind.Cessna) car.plane.Toggle();
            if (k == VehicleKind.Car) { Kind = VehicleKind.Car; return; }
            if (k == VehicleKind.Cessna) { if (car.plane != null && !car.plane.Active) car.plane.Toggle(); Kind = car.plane != null && car.plane.Active ? VehicleKind.Cessna : VehicleKind.Car; return; }
            Enter(k);
        }

        /// <summary>The next vehicle in <see cref="Vehicles.Order"/>.</summary>
        public void Cycle()
        {
            int i = System.Array.IndexOf(Vehicles.Order, CurrentKind);
            Select(Vehicles.Order[(i + 1) % Vehicles.Order.Length]);
        }

        /// <summary>What the player is driving right now, across all drivers.</summary>
        public VehicleKind CurrentKind => Active ? Kind : (car != null && car.plane != null && car.plane.Active ? VehicleKind.Cessna : VehicleKind.Car);

        public void Toggle(VehicleToggle what)
        {
            if (!Active || _model == null) return;
            if (what == VehicleToggle.Door) { if (Spec.hasRamp) _rampTarget = _rampTarget > 0.5f ? 0f : 1f; return; }
            _model.Toggle(this, what);
        }

        /// <summary>Turns the ramp collider (and the model node, through <see cref="Viz"/>) to the current opening; the ramp visual follows in <see cref="Update"/>.</summary>
        void ApplyRamp()
        {
            if (_cur == null || _cur.hinge == null) return;
            _cur.hinge.localRotation = Quaternion.Euler(Spec.rampClosedDeg * (1f - _ramp), 0f, 0f);
            Physics.SyncTransforms();
        }

        /// <summary>Called by <see cref="VehicleCrashSensor"/> for every hull collision of the rocket.</summary>
        internal void OnHullCollision(Collision c)
        {
            if (!Active || Spec == null || !Spec.rocket || _crashed) return;
            int layer = c.collider.gameObject.layer;
            if (layer == PhysicsSetup.CarLayer || layer == PhysicsSetup.BallLayer) return;           // other cars / the ball do not destroy it
            if (c.relativeVelocity.magnitude < Spec.crashSpeed) return;                              // a gentle touch is survivable (the skids land it)
            Crash(c.contactCount > 0 ? c.GetContact(0).point : car.Body.position);
        }

        /// <summary>The rocket is destroyed: Tsar Bomba for the solo player / match creator, a small fireball for a joined client; the player is back in the car.</summary>
        void Crash(Vector3 point)
        {
            if (_crashed) return; _crashed = true;
            bool nuke = TsarBomba.CanDetonate();
            var rb = car.Body; Vector3 where = point;
            Select(VehicleKind.Car);
            TsarBomba.Spawn(where, nuke, rb);
            var root = Object.FindFirstObjectByType<GameRoot>();
            if (root != null && root.net != null && nuke) root.net.BroadcastExplosion(where);
            Debug.Log("[RocketWorld] rocket crash at " + where + (nuke ? " -> Tsar Bomba" : " -> small fireball"));
        }

        void Build(VehicleKind k)
        {
            int i = (int)k; if (_built[i] != null) return;
            var sp = Vehicles.Get(k); var b = new Built();
            b.cols = new GameObject("VehicleColliders_" + sp.name); b.cols.transform.SetParent(transform, false); b.cols.layer = PhysicsSetup.CarLayer;
            foreach (var bx in sp.boxes)
            {
                var c = b.cols.AddComponent<BoxCollider>(); c.center = bx.center - sp.com; c.size = bx.size; c.sharedMaterial = PhysicsSetup.CarMaterial;
            }
            b.cols.SetActive(false);
            b.hull = b.cols.GetComponents<BoxCollider>();
            if (sp.rocket) b.cols.AddComponent<VehicleCrashSensor>().driver = this;
            if (sp.holdBoxes != null)
            {   // drivable interior (Antonov): PlaneLayer, like the intro cargo plane, so the wheel rays of other cars see the deck
                b.hold = new GameObject("VehicleHold_" + sp.name); b.hold.transform.SetParent(transform, false); b.hold.layer = PhysicsSetup.PlaneLayer;
                foreach (var bx in sp.holdBoxes) { var c = b.hold.AddComponent<BoxCollider>(); c.center = bx.center - sp.com; c.size = bx.size; c.sharedMaterial = PhysicsSetup.WorldMaterial; }
                if (sp.hasRamp)
                {
                    b.hinge = new GameObject("RampHinge").transform; b.hinge.SetParent(b.hold.transform, false); b.hinge.localPosition = sp.rampHinge - sp.com; b.hinge.gameObject.layer = PhysicsSetup.PlaneLayer;
                    var rc = new GameObject("RampCollider") { layer = PhysicsSetup.PlaneLayer }; rc.transform.SetParent(b.hinge, false);
                    rc.transform.localRotation = Quaternion.Euler(-sp.rampOpenSlopeDeg, 0f, 0f);          // open pose: the ramp slopes down towards the tail
                    var bc = rc.AddComponent<BoxCollider>(); bc.size = new Vector3(sp.rampWidth, 0.3f, sp.rampLen); bc.center = new Vector3(0f, -0.15f, -sp.rampLen * 0.5f); bc.sharedMaterial = PhysicsSetup.WorldMaterial;
                }
                b.hold.SetActive(false);
            }
            if (_visRoot == null) { _visRoot = new GameObject("Vehicles").transform; _visRoot.SetParent(car.visual, false); }
            b.vis = VehicleVisual.Build(_visRoot, sp); b.vis.gameObject.SetActive(false);
            b.model = VehiclePhysicsFactory.Create(sp);
            _built[i] = b;
        }

        void Enter(VehicleKind k)
        {
            if (!car.CanTeleport || Active) return;
            if (car.water != null) car.water.ForceExit();
            if (car.fpv != null) car.fpv.ForceExit();
            Build(k); var sp = Vehicles.Get(k); _cur = _built[(int)k]; _model = _cur.model; Spec = sp;
            var rb = car.Body;
            _carMass = rb.mass; _carInertia = rb.inertiaTensor; _carInertiaRot = rb.inertiaTensorRotation; _carGravity = rb.useGravity; _carMaxLin = rb.maxLinearVelocity;

            float bottom = 0f;                                                    // lowest contact point below the body origin
            foreach (var w in sp.wheels) bottom = Mathf.Max(bottom, sp.rocket ? -(w.pos.z - sp.com.z) : -(w.pos.y - w.radius - sp.com.y));   // rocket: it stands on its tail, the skids are along -Z
            bool ground = Physics.Raycast(rb.position + Vector3.up * 2f, Vector3.down, out var hit, 6f, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore);
            float yaw = rb.rotation.eulerAngles.y; var rot = Quaternion.Euler(-sp.startPitchDeg, yaw, 0);   // startPitchDeg 90: nose up
            if (ground) { rb.position = hit.point + Vector3.up * (bottom + 0.15f); rb.linearVelocity = Vector3.zero; }
            else rb.linearVelocity = rot * Vector3.forward * (sp.aero != AeroClass.None ? Mathf.Min(150f, sp.vMax * 0.45f) : 0f);
            rb.rotation = rot; rb.angularVelocity = Vector3.zero;
            rb.mass = sp.mass; rb.inertiaTensor = sp.inertia; rb.inertiaTensorRotation = Quaternion.identity;
            rb.useGravity = false; rb.maxLinearVelocity = 6000f;                 // Hyper Boost goes well past the car's 1000 m/s clamp
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            car.HitCollider.enabled = false; _cur.cols.SetActive(true); if (_cur.hold != null) _cur.hold.SetActive(true);
            _crashed = false; _ramp = _rampTarget = sp.hasRamp && ground ? 1f : 0f; ApplyRamp();

            _hidden = car.visual.GetComponentsInChildren<Renderer>(true);
            foreach (var r in _hidden) if (r != null && !r.transform.IsChildOf(_visRoot)) r.enabled = false;
            _cur.vis.gameObject.SetActive(true); foreach (var r in _cur.vis.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
            Viz = default; _hasAlt = false; Kind = k; Active = true;
            _model.Enter(this, rb, !ground);
            Debug.Log("[RocketWorld] vehicle " + sp.name + " ON (" + (ground ? "on the ground" : "airborne") + ")");
        }

        void Leave()
        {
            if (!Active) return;
            var rb = car.Body; Restore(rb);
            float yaw = rb.rotation.eulerAngles.y; Vector3 up = Vector3.up * 1.2f;
            if (Physics.Raycast(rb.position + Vector3.up * 3f, Vector3.down, out _, 200f, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore)) up = Vector3.up * 0.6f;
            rb.position += up; rb.rotation = Quaternion.Euler(0, yaw, 0); rb.angularVelocity = Vector3.zero;
            var v = rb.linearVelocity; rb.linearVelocity = Vector3.ClampMagnitude(new Vector3(v.x, Mathf.Min(v.y, 0f), v.z), RL.CarMaxSpeed * RL.UuToBt);
            car.OnBoatExit();                                                    // clean wheel-model state
            Debug.Log("[RocketWorld] vehicle OFF");
        }

        void Restore(Rigidbody rb)
        {
            Active = false; Kind = VehicleKind.Car;
            if (_cur != null) { _cur.cols.SetActive(false); if (_cur.hold != null) _cur.hold.SetActive(false); _cur.vis.gameObject.SetActive(false); foreach (var r in _cur.vis.GetComponentsInChildren<Renderer>(true)) r.enabled = false; }
            if (car.HitCollider != null) car.HitCollider.enabled = true;
            if (_hidden != null) foreach (var r in _hidden) if (r != null && (_visRoot == null || !r.transform.IsChildOf(_visRoot))) r.enabled = true;
            if (rb != null) { rb.mass = _carMass; rb.inertiaTensor = _carInertia; rb.inertiaTensorRotation = _carInertiaRot; rb.useGravity = _carGravity; rb.maxLinearVelocity = _carMaxLin; }
        }

        /// <summary>Respawn / rescue / network hand-over: drop vehicle mode without touching the car state (the caller resets it).</summary>
        public void ForceExit() { if (Active) Restore(car != null ? car.Body : null); }

        // ------------------------------------------------------------------------------------------ per frame
        void Update()
        {
            if (!Active || _cur == null) return;
            if (!Mathf.Approximately(_ramp, _rampTarget)) { _ramp = Mathf.MoveTowards(_ramp, _rampTarget, Mathf.Min(Time.unscaledDeltaTime, 0.1f) / 9f); ApplyRamp(); }   // 9 s from shut to open
            Viz.ramp = _ramp;
            _cur.vis.Tick(Viz, Mathf.Min(Time.unscaledDeltaTime, 0.1f));
        }

        /// <summary>
        /// Rocket crash detection that does not rely on the PhysX <c>OnCollisionEnter</c> callback (which never reached the driver reliably for the
        /// rocket's hull): before each tick the hull boxes are swept along the velocity against the terrain; a closing speed above
        /// <see cref="VehicleSpec.crashSpeed"/> destroys the rocket. A hull that is already inside the world (tunnelled / depenetrating) is caught by <see cref="CrashOverlap"/>.
        /// </summary>
        bool CrashSweep(Rigidbody rb, float dt)
        {
            if (_crashed || Spec == null || !Spec.rocket || _cur == null || _cur.hull == null) return false;
            float speed = _preV.magnitude; if (speed < Spec.crashSpeed) return false;
            Vector3 dir = _preV / speed; float dist = speed * dt * 1.5f + 0.25f;
            foreach (var bc in _cur.hull)
            {
                if (bc == null) continue;
                var t = bc.transform; Vector3 ctr = t.TransformPoint(bc.center), half = Vector3.Scale(bc.size * 0.5f, Abs(t.lossyScale));
                if (Physics.BoxCast(ctr, half, dir, out var hit, t.rotation, dist, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore))
                {
                    if (hit.distance > speed * dt * 1.5f + 0.25f) continue;
                    if (-Vector3.Dot(_preV, hit.normal) < Spec.crashSpeed) continue;          // grazing / sliding along a surface is survivable
                    Crash(hit.point == Vector3.zero ? ctr + dir * hit.distance : hit.point); return true;
                }
            }
            return false;
        }

        /// <summary>Called after Physics.Simulate (see RocketCar.PostStep): the hull ended up inside the world while it was moving fast.</summary>
        public void PostStep()
        {
            if (!Active || _crashed || Spec == null || !Spec.rocket || _cur == null || _cur.hull == null) return;
            if (_preV.magnitude < Spec.crashSpeed) return;
            foreach (var bc in _cur.hull)
            {
                if (bc == null) continue;
                var t = bc.transform; Vector3 ctr = t.TransformPoint(bc.center), half = Vector3.Scale(bc.size * 0.5f, Abs(t.lossyScale)) + Vector3.one * 0.05f;
                if (Physics.OverlapBoxNonAlloc(ctr, half, _ovl, t.rotation, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore) > 0) { Crash(ctr); return; }
            }
        }

        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        public float HeightMeters(Rigidbody rb)
        {
            if (car.georeference == null) return rb.position.y;
            double3 e = car.georeference.TransformUnityPositionToEarthCenteredEarthFixed(new double3(rb.position.x, rb.position.y, rb.position.z));
            Geo.EcefToLonLatHeight(e.x, e.y, e.z, out _, out _, out double h);
            return (float)h;
        }

        // ------------------------------------------------------------------------------------------ 120 Hz tick
        public void PreStep(in CarInput.Controls c, float dt)
        {
            if (!Active || _model == null) return;
            var rb = car.Body;
            _preV = rb.linearVelocity;
            if (CrashSweep(rb, dt)) return;                                   // rocket: swept hull test against the world (does not depend on PhysX collision callbacks)
            _model.Step(this, rb, c, dt);
            SpeedMs = rb.linearVelocity.magnitude; AltM = HeightMeters(rb);
            if (_hasAlt) VsMs = Mathf.Lerp(VsMs, (AltM - _lastAlt) / dt, 0.1f);
            _lastAlt = AltM; _hasAlt = true;
        }

        /// <summary>
        /// Hyper Boost (+500 % speed): drives the speed along <paramref name="dir"/> toward <paramref name="vRef"/> x <see cref="RocketCar.HyperMul"/> with a
        /// velocity controller (up to ~12 g), turns the velocity toward the nose so the vehicle still steers, and bleeds the excess off when the boost ends.
        /// </summary>
        public void ApplyHyperDrive(Rigidbody rb, Vector3 dir, float vRef, float dt, bool steerVelocity) => HyperDrive(car, rb, dir, vRef, dt, steerVelocity);

        public static void HyperDrive(RocketCar car, Rigidbody rb, Vector3 dir, float vRef, float dt, bool steerVelocity)
        {
            float mul = car.HyperMul; Vector3 v = rb.linearVelocity; float speed = v.magnitude, target = vRef * mul;
            if (mul > 1.001f)
            {
                float need = target - Vector3.Dot(v, dir);
                if (need > 0f) rb.AddForce(dir * (Mathf.Min(need * 2.5f, 120f) * rb.mass), ForceMode.Force);
                if (steerVelocity && speed > 5f) rb.linearVelocity = Vector3.RotateTowards(v, dir, 1.3f * Mathf.Clamp01((mul - 1f) / 2f) * dt, 0f);
            }
            if (speed > target * 1.02f && speed > vRef * 1.1f)
            {
                float excess = speed - Mathf.Max(target, vRef * 1.1f);
                rb.AddForce(-v / speed * (Mathf.Min(excess * 2f, 150f) * rb.mass), ForceMode.Force);
            }
        }
    }
}

namespace RocketWorld
{
    /// <summary>Sits on the rocket's hull colliders and reports collisions to the <see cref="VehicleDriver"/>.</summary>
    public sealed class VehicleCrashSensor : UnityEngine.MonoBehaviour
    {
        public VehicleDriver driver;
        void OnCollisionEnter(UnityEngine.Collision c) { if (driver != null) driver.OnHullCollision(c); }
    }
}
