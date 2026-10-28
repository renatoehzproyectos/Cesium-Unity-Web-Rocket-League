using CesiumForUnity;
using RocketWorld.Sim;
using Unity.Mathematics;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Plane mode. The car's Rigidbody becomes a small box-built airplane whose flight model is JSBSim's (see Sim/Flight/JsbModel.cs, a C# port
    /// verified frame by frame against native JSBSim): standard atmosphere, stall-curve lift, drag, stability derivatives, control surfaces, an
    /// electric motor driving the c172x fixed-pitch propeller with P-factor and gyroscopic moments. PhysX integrates the body and handles
    /// collisions with the world (fuselage, wings and tail are box colliders); this class feeds it JSBSim's forces and moments at the game's
    /// 120 Hz tick, in place of the RocketSim wheel model. The landing gear uses JSBSim's strut spring/damper on ray casts with a tire model.
    ///
    /// Controls: W/S elevator (S = nose up), A/D aileron (+ nose wheel / rudder on the ground), Q/E rudder, Space / Ctrl throttle lever up / down,
    /// Shift wheel brakes. Switch mode with P (or the Plane button).
    /// </summary>
    [DefaultExecutionOrder(-39)]
    public class PlaneDriver : MonoBehaviour
    {
        public RocketCar car;
        /// <summary>Normal top speed (m/s) that Hyper Boost multiplies.</summary>
        public const float HyperRefSpeed = 70f;
        public bool Active { get; private set; }
        public float ThrottleLever { get; private set; }
        public float AirspeedKt { get; private set; }
        public float AltitudeFt { get; private set; }
        public float VerticalSpeedFpm { get; private set; }
        public float AlphaDeg { get; private set; }
        public bool Stalled => Active && AlphaDeg > 16f;
        public bool OnGround { get; private set; }
        public string Info => Active ? $"plane  {AirspeedKt:0} kt  alt {AltitudeFt:0} ft  vs {VerticalSpeedFpm:0}  a {AlphaDeg:0.0}  thr {ThrottleLever * 100:0}%  gear {(OnGround ? "ground" : "air")}" : "plane off";

        JsbAircraft _ac;
        GameObject _cols, _vis; Transform _prop;
        float _el, _ail, _rud, _spin, _lastAlt;
        bool _hasAlt;
        // saved car body state
        float _carMass; Vector3 _carInertia; Quaternion _carInertiaRot; bool _carGravity;
        Renderer[] _hidden;
        readonly bool[] _wow = new bool[8];

        void Awake()
        {
            if (car == null) car = GetComponent<RocketCar>();
        }

        JsbAircraft Model => _ac ?? (_ac = PlaneModel.Load());

        // ------------------------------------------------------------------------------------------ mode switch
        public void Toggle() { if (Active) Leave(); else Enter(); }

        void BuildOnce()
        {
            if (_cols != null) return;
            var a = Model;
            _cols = new GameObject("PlaneColliders"); _cols.transform.SetParent(transform, false); _cols.layer = PhysicsSetup.CarLayer;
            // fuselage, wing, tail: compound box colliders in the body frame (centre / size from the same JSBSim structural coordinates as the visual)
            AddBox(a, 100, 0, 36, 190, 40, 40); AddBox(a, 205, 0, 44, 90, 14, 20); AddBox(a, -12, 0, 30, 36, 38, 36);
            AddBox(a, 43, 0, 62, 59, 432, 5); AddBox(a, 235, 0, 50, 33, 96, 4); AddBox(a, 232, 0, 74, 40, 6, 52);
            _cols.SetActive(false);
            _vis = PlaneVisual.Build(car.visual, a, out _prop).gameObject; _vis.SetActive(false);
        }

        void AddBox(JsbAircraft a, float sx, float sy, float sz, float lx, float ly, float lz)
        {
            var b = _cols.AddComponent<BoxCollider>();
            b.center = PlaneModel.Local(a, sx, sy, sz); b.size = new Vector3(ly, lz, lx) * 0.0254f;
            b.sharedMaterial = PhysicsSetup.CarMaterial;
        }

        void Enter()
        {
            if (!car.CanTeleport || Active) return;
            if (car.water != null) car.water.ForceExit();
            if (car.vehicle != null) car.vehicle.ForceExit();
            BuildOnce();
            var rb = car.Body; var a = Model;
            _carMass = rb.mass; _carInertia = rb.inertiaTensor; _carInertiaRot = rb.inertiaTensorRotation; _carGravity = rb.useGravity;
            // ground or air start
            bool ground = Physics.Raycast(rb.position + Vector3.up * 2f, Vector3.down, out var hit, 6f, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore);
            float yaw = rb.rotation.eulerAngles.y;
            var rot = Quaternion.Euler(0, yaw, 0);
            if (ground)
            {
                float lowest = 0f; foreach (var g in a.gears) lowest = Mathf.Max(lowest, -PlaneModel.Local(a, g.locIn.x, g.locIn.y, g.locIn.z).y);
                rb.position = hit.point + Vector3.up * (lowest + 0.15f); rb.linearVelocity = Vector3.zero; ThrottleLever = 0f;
            }
            else { rb.linearVelocity = rot * Vector3.forward * 40f; ThrottleLever = 0.55f; a.propRpm = 2200; }
            rb.rotation = rot; rb.angularVelocity = Vector3.zero;
            rb.mass = (float)(a.massSlug * PlaneModel.SlugToKg);
            rb.inertiaTensor = new Vector3((float)(a.iyy * 1.35582), (float)(a.izz * 1.35582), (float)(a.ixx * 1.35582));   // Unity local x pitch, y yaw, z roll (slug ft2 -> kg m2)
            rb.inertiaTensorRotation = Quaternion.identity;
            rb.useGravity = false;
            car.HitCollider.enabled = false; _cols.SetActive(true);
            _hidden = car.visual.GetComponentsInChildren<Renderer>(true);
            foreach (var r in _hidden) if (r != null && !r.transform.IsChildOf(_vis.transform)) r.enabled = false;
            _vis.SetActive(true); foreach (var r in _vis.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
            _el = _ail = _rud = 0f; _hasAlt = false; Active = true;
            Debug.Log("[RocketWorld] plane mode ON (" + (ground ? "on the ground" : "airborne") + ")");
        }

        void Leave()
        {
            if (!Active) return;
            Active = false; var rb = car.Body;
            _cols.SetActive(false); car.HitCollider.enabled = true; _vis.SetActive(false);
            if (_hidden != null) foreach (var r in _hidden) if (r != null) r.enabled = true;
            foreach (var r in _vis.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            rb.mass = _carMass; rb.inertiaTensor = _carInertia; rb.inertiaTensorRotation = _carInertiaRot; rb.useGravity = _carGravity;
            // back to a car: upright, same heading, a little above whatever is below
            float yaw = rb.rotation.eulerAngles.y;
            Vector3 up = Vector3.up * 1.2f;
            if (Physics.Raycast(rb.position + Vector3.up * 3f, Vector3.down, out var hit, 200f, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore)) up = Vector3.up * 0.6f;
            rb.position += up; rb.rotation = Quaternion.Euler(0, yaw, 0); rb.angularVelocity = Vector3.zero;
            var v = rb.linearVelocity; v = Vector3.ClampMagnitude(new Vector3(v.x, Mathf.Min(v.y, 0f), v.z), RL.CarMaxSpeed * RL.UuToBt); rb.linearVelocity = v;
            car.OnBoatExit();                                     // clean wheel-model state
            Debug.Log("[RocketWorld] plane mode OFF");
        }

        /// <summary>Respawn / rescue / network hand-over: drop plane mode without touching the car state (the caller resets it).</summary>
        public void ForceExit()
        {
            if (!Active) return;
            Active = false;
            if (_cols != null) _cols.SetActive(false);
            if (car != null && car.HitCollider != null) car.HitCollider.enabled = true;
            if (_vis != null) _vis.SetActive(false);
            if (_hidden != null) foreach (var r in _hidden) if (r != null) r.enabled = true;
            var rb = car.Body; if (rb != null) { rb.mass = _carMass; rb.inertiaTensor = _carInertia; rb.inertiaTensorRotation = _carInertiaRot; rb.useGravity = _carGravity; }
        }

        // ------------------------------------------------------------------------------------------ per frame
        void Update()
        {
            if (!Active) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (_prop != null) { _spin += (float)_ac.propRpm * 6f * dt; _prop.localRotation = Quaternion.Euler(0, 0, _spin); }
        }

        float HeightMeters(Rigidbody rb)
        {
            if (car.georeference == null) return rb.position.y;
            double3 e = car.georeference.TransformUnityPositionToEarthCenteredEarthFixed(new double3(rb.position.x, rb.position.y, rb.position.z));
            Geo.EcefToLonLatHeight(e.x, e.y, e.z, out _, out _, out double h);
            return (float)h;
        }

        // ------------------------------------------------------------------------------------------ 120 Hz tick
        /// <summary>Replaces RocketVehicle.PreTick while in plane mode.</summary>
        public void PreStep(CarInput.Controls c, float dt)
        {
            var rb = car.Body; var a = Model;
            Quaternion q = rb.rotation, inv = Quaternion.Inverse(q);
            Vector3 vWorld = rb.linearVelocity, wWorld = rb.angularVelocity;

            // controls: stick-like smoothing, throttle lever
            if (c.leverSet) ThrottleLever = Mathf.MoveTowards(ThrottleLever, Mathf.Clamp01(c.lever), 2f * dt);          // touch throttle slider
            else ThrottleLever = Mathf.Clamp01(ThrottleLever + ((c.jump ? 1f : 0f) - (c.handbrake ? 1f : 0f)) * 0.45f * dt);
            float tgtEl = Mathf.Clamp(c.throttle, -1f, 1f), tgtAil = Mathf.Clamp(c.steer, -1f, 1f), tgtRud = Mathf.Clamp(-(c.roll + (OnGround ? c.steer * 0.6f : c.steer * 0.8f)), -1f, 1f);   // JSBSim: positive rudder = nose LEFT (Cn_dr < 0), so negate; 0.8 in the air cancels the aileron adverse yaw (Cn_da)
            float rate = 3.2f * dt;
            _el = Mathf.MoveTowards(_el, tgtEl, rate); _ail = Mathf.MoveTowards(_ail, tgtAil, rate); _rud = Mathf.MoveTowards(_rud, tgtRud, rate);
            a.SetControls(_el, _ail, _rud, ThrottleLever);

            float altM = HeightMeters(rb);
            D3 uvw = PlaneModel.VelocityToBody(inv * vWorld), pqr = PlaneModel.RatesToBody(inv * wWorld);
            var o = a.Step(uvw, pqr, altM / PlaneModel.FtToM, dt);
            // Hyper Boost: aerodynamic forces / moments are capped at 1.25x the normal top speed's dynamic pressure; the extra speed comes from the hyper drive
            float spd = vWorld.magnitude, qs = spd > HyperRefSpeed * 1.25f ? Mathf.Pow(HyperRefSpeed * 1.25f / spd, 2f) : 1f;
            rb.AddForce(q * PlaneModel.ForceToUnity(o.force) * qs, ForceMode.Force);
            rb.AddTorque(q * PlaneModel.MomentToUnity(o.moment) * qs, ForceMode.Force);
            rb.AddForce(Vector3.down * (9.80665f * rb.mass), ForceMode.Force);

            ApplyGear(rb, q, vWorld, wWorld, c.boost || c.wheelBrake ? 1f : 0f, tgtAil);
            if (car.HyperMul > 1.001f || spd > HyperRefSpeed * 1.1f) VehicleDriver.HyperDrive(car, rb, q * Vector3.forward, HyperRefSpeed, dt, true);

            AirspeedKt = (float)(uvw.Length / 1.68781); AlphaDeg = (float)(o.alpha * 57.2958);
            float altFt = altM / PlaneModel.FtToM;
            if (_hasAlt) VerticalSpeedFpm = Mathf.Lerp(VerticalSpeedFpm, (altFt - _lastAlt) / dt * 60f, 0.02f);
            _lastAlt = altFt; _hasAlt = true; AltitudeFt = altFt;
        }

        /// <summary>JSBSim bogey contacts: strut spring / damper (same formula as FGLGear) along the ground normal, plus rolling / brake and side friction.</summary>
        void ApplyGear(Rigidbody rb, Quaternion q, Vector3 vWorld, Vector3 wWorld, float brake, float steer)
        {
            var a = Model; bool any = false; int i = 0;
            foreach (var g in a.gears)
            {
                Vector3 local = PlaneModel.Local(a, g.locIn.x, g.locIn.y, g.locIn.z), arm = q * local, wp = rb.position + arm;
                const float lift = 0.5f, reach = 0.5f + 0.9f;
                bool hitOk = Physics.Raycast(wp + Vector3.up * lift, Vector3.down, out var hit, reach, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore);
                float height = hitOk ? hit.distance - lift : 1f;                  // m, negative = wheel pushed into the ground
                _wow[i] = hitOk && height < 0f; any |= _wow[i];
                if (_wow[i])
                {
                    Vector3 n = hit.normal, vp = vWorld + Vector3.Cross(wWorld, arm);
                    float compress = Mathf.Min(-height, 0.9f) / PlaneModel.FtToM;                               // ft
                    float compressSpeed = -Vector3.Dot(vp, n) / PlaneModel.FtToM;                                // ft/s, > 0 while compressing
                    double strut = -g.StrutForce(compress, compressSpeed);                                       // lbf, up
                    float N = (float)strut * PlaneModel.LbfToN;
                    rb.AddForceAtPosition(n * N, hit.point, ForceMode.Force);
                    // wheel frame: heading projected on the ground, nose wheel steered
                    Vector3 fwd = Vector3.ProjectOnPlane(q * Vector3.forward, n).normalized;
                    if (g.maxSteerDeg > 0.01) fwd = Quaternion.AngleAxis(steer * (float)g.maxSteerDeg, n) * fwd;
                    Vector3 right = Vector3.Cross(n, fwd).normalized;
                    float vx = Vector3.Dot(vp, fwd), vy = Vector3.Dot(vp, right);
                    float slip = -Mathf.Atan2(vy, Mathf.Abs(vx)) * Mathf.Rad2Deg;
                    float side = Mathf.Sqrt(vx * vx + vy * vy) > 1f ? (float)g.SideForceCoeff(slip) : Mathf.Clamp(-vy / 1f, -1f, 1f) * (float)g.staticF;
                    float roll = -Mathf.Clamp(vx / 0.8f, -1f, 1f) * (float)g.BrakeForceCoeff(g.brakeGroup == "NONE" ? 0.0 : brake);
                    rb.AddForceAtPosition((right * side + fwd * roll) * N, hit.point, ForceMode.Force);
                }
                if (++i >= _wow.Length) break;
            }
            OnGround = any;
        }

        /// <summary>Replaces PostTick / FinishTick: nothing to clamp (JSBSim has no speed limit), just keep the car's interpolation state.</summary>
        public void PostStep() { }
    }
}
