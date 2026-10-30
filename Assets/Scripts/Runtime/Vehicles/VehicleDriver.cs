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

        sealed class Built { public GameObject cols; public VehicleVisual vis; public IVehicleModel model; }
        readonly Built[] _built = new Built[8];
        Transform _visRoot;
        IVehicleModel _model; Built _cur;
        float _lastAlt; bool _hasAlt;
        float _carMass; Vector3 _carInertia; Quaternion _carInertiaRot; bool _carGravity; float _carMaxLin;
        Renderer[] _hidden;

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

        public void Toggle(VehicleToggle what) { if (Active && _model != null) _model.Toggle(this, what); }

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
            if (_visRoot == null) { _visRoot = new GameObject("Vehicles").transform; _visRoot.SetParent(car.visual, false); }
            b.vis = VehicleVisual.Build(_visRoot, sp); b.vis.gameObject.SetActive(false);
            b.model = VehiclePhysicsFactory.Create(sp);
            _built[i] = b;
        }

        void Enter(VehicleKind k)
        {
            if (!car.CanTeleport || Active) return;
            if (car.water != null) car.water.ForceExit();
            Build(k); var sp = Vehicles.Get(k); _cur = _built[(int)k]; _model = _cur.model; Spec = sp;
            var rb = car.Body;
            _carMass = rb.mass; _carInertia = rb.inertiaTensor; _carInertiaRot = rb.inertiaTensorRotation; _carGravity = rb.useGravity; _carMaxLin = rb.maxLinearVelocity;

            float bottom = 0f;                                                    // lowest contact point below the body origin
            foreach (var w in sp.wheels) bottom = Mathf.Max(bottom, -(w.pos.y - w.radius - sp.com.y));
            bool ground = Physics.Raycast(rb.position + Vector3.up * 2f, Vector3.down, out var hit, 6f, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore);
            float yaw = rb.rotation.eulerAngles.y; var rot = Quaternion.Euler(0, yaw, 0);
            if (ground) { rb.position = hit.point + Vector3.up * (bottom + 0.15f); rb.linearVelocity = Vector3.zero; }
            else rb.linearVelocity = rot * Vector3.forward * (sp.aero != AeroClass.None ? Mathf.Min(150f, sp.vMax * 0.45f) : 0f);
            rb.rotation = rot; rb.angularVelocity = Vector3.zero;
            rb.mass = sp.mass; rb.inertiaTensor = sp.inertia; rb.inertiaTensorRotation = Quaternion.identity;
            rb.useGravity = false; rb.maxLinearVelocity = 6000f;                 // Hyper Boost goes well past the car's 1000 m/s clamp
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            car.HitCollider.enabled = false; _cur.cols.SetActive(true);

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
            if (_cur != null) { _cur.cols.SetActive(false); _cur.vis.gameObject.SetActive(false); foreach (var r in _cur.vis.GetComponentsInChildren<Renderer>(true)) r.enabled = false; }
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
            _cur.vis.Tick(Viz, Mathf.Min(Time.unscaledDeltaTime, 0.1f));
        }

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
