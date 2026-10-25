using RocketWorld.Sim;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Turns the car into a boat while it is over water, and back into a car when it reaches land.
    ///
    /// Where the water is comes from real OpenStreetMap polygons (<see cref="WaterMap"/>), not from the mesh. The tile mesh at a lake or sea
    /// is just the water SURFACE with a collider, which is why the car used to drive on it like concrete. In boat mode the car never rests on
    /// that collider: the hull is held at the surface by Archimedes-style spring/damper forces at six hull points, and the rigid body's hitbox
    /// floats about 0.35 m above the water, so it still hits piers, banks, bridge pillars and buildings through normal PhysX contacts but never
    /// the water mesh. The surface height is read from the mesh itself (first collider under the car), so it is right for the sea, a mountain
    /// lake or a river without any datum maths.
    ///
    /// Boat forces replace the RocketSim wheel model only while <see cref="Active"/>: propeller thrust (throttle / boost / reverse), rudder
    /// yaw torque, hull drag with a keel (low sideways drag, so it carves instead of skidding), inward bank in turns, a trim-by-stern bow-up
    /// from the thrust line, self-righting and a small swell. Units are metres / newtons in Unity space (same scale as the car: 180 kg).
    /// Everything runs inside the existing 120 Hz tick (RocketCar.PreStep / PostStep), so determinism and multiplayer host authority are unchanged.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public class WaterDriver : MonoBehaviour
    {
        public RocketCar car;
        public WaterMap map;

        [Header("Hull / buoyancy")]
        public float hullBottom = -0.60f;          // virtual hull bottom, m below the body origin (the hitbox bottom is 0.05 m ABOVE the origin)
        public float draft = 0.30f;                // immersion at rest, m
        public float hullHalfWidth = 0.55f, hullHalfLength = 0.95f;
        public float heaveDamping = 2400f, heaveDampingQuad = 120f;     // N s/m, N s^2/m^2 (whole hull, split over the points)
        [Header("Propulsion")]
        public float maxThrust = 1620f;            // N  = 9 m/s^2 on 180 kg
        public float boostThrustMul = 1.6f, reverseThrustMul = 0.25f;
        [Header("Water drag (body axes)")]
        public float dragForward = 1.53f, dragForwardLin = 40f;          // top speed solves thrust = dragForward v^2 + dragForwardLin v: 22 m/s, 30 m/s boosted
        public float dragLateral = 60f, dragLateralLin = 300f;           // keel: sideways motion is heavily damped
        public float brakeDrag = 500f;
        [Header("Handling")]
        public float yawTorque = 750f, yawDamping = 470f, rollDamping = 120f, pitchDamping = 250f, bankTorque = 600f, rightingTorque = 2500f;
        public float steerFalloffSpeed = 16f;      // m/s: the rudder bites less as speed grows (like RocketSim's steer-angle-from-speed curve)
        [Header("Look")]
        public float swellAmplitude = 0.05f, oceanSwellAmplitude = 0.11f;
        public float visualSink = 0.22f;           // the car model sits this much lower so it looks half-immersed
        [Header("Mode switching")]
        public float enterHeight = 1.0f;           // may become a boat when the body is less than this above the water surface
        public float minDwell = 0.4f;              // s, hysteresis between modes

        public const float DefaultVisualSink = 0.22f;
        public bool Active { get; private set; }
        public bool Boosting { get; private set; }
        public float VisualSink => _sink;
        public float Level => _level;
        public float Immersion => _imm;
        public WaterKind Kind => _kind;
        public string Info => $"water {(Active ? "BOAT" : "land")}  level {(_levelValid ? _level.ToString("0.00") : "-")}  imm {_imm:0.00}  probes w{_pw}/l{_pl}  tiles {(map != null ? map.Loaded : 0)}{(map != null && map.Loading > 0 ? "+" + map.Loading : "")}";

        static readonly Vector2[] Probes = { new Vector2(0f, 0f), new Vector2(0.5f, 0.9f), new Vector2(-0.5f, 0.9f), new Vector2(0.5f, -0.9f), new Vector2(-0.5f, -0.9f) };
        bool _levelValid, _rayValid, _rayFlat;
        float _level, _rawLevel, _imm, _stern, _sink, _since = -10f, _simT, _nextPrefetch;
        int _jump, _pw, _pl;
        WaterKind _kind;

        Vector3 HullPoint(int i)    // i: 0..5 = {left, right} x {stern, middle, bow}; local axes x right, y up, z forward
        {
            float x = (i & 1) == 0 ? -hullHalfWidth : hullHalfWidth, z = (i >> 1) == 0 ? -hullHalfLength : ((i >> 1) == 1 ? 0f : hullHalfLength);
            return new Vector3(x, hullBottom, z);
        }

        // ---------------------------------------------------------------------------------------------- per frame

        void Update()
        {
            if (map == null || car == null || car.Body == null) return;
            if (!car.simulated || car.InPlane) { if (Active) ForceExit(); return; }
            if (!car.CanStep) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            var rb = car.Body;
            if (Time.unscaledTime >= _nextPrefetch)
            {
                _nextPrefetch = Time.unscaledTime + 0.3f;
                map.Prefetch(rb.position);
                if (rb.linearVelocity.sqrMagnitude > 25f) map.Prefetch(rb.position + rb.linearVelocity * 3f);     // look ahead for fast boats
            }
            RefreshLevel(dt);
            Evaluate(rb);
            _sink = Mathf.Lerp(_sink, Active ? visualSink * Mathf.Clamp01(_imm * 2f) : 0f, 1f - Mathf.Exp(-dt * 4f));
        }

        /// <summary>The height of the first collider under the car = the water surface when over water. Smoothed; outliers (a pier, a boat) ignored.</summary>
        void RefreshLevel(float dt)
        {
            var rb = car.Body;
            _rayValid = false;
            if (!Physics.Raycast(rb.position + Vector3.up * 1.5f, Vector3.down, out var hit, 150f, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore)) return;
            _rayValid = true; _rawLevel = hit.point.y; _rayFlat = hit.normal.y > 0.8f;
            if (!_levelValid) { _level = _rawLevel; _levelValid = true; _jump = 0; }
            else if (Mathf.Abs(_rawLevel - _level) < 2f) { _level = Mathf.Lerp(_level, _rawLevel, 1f - Mathf.Exp(-dt * 2.5f)); _jump = 0; }
            else if (++_jump > 30) { _level = _rawLevel; _jump = 0; }
        }

        void Evaluate(Rigidbody rb)
        {
            var q = rb.rotation;
            int water = 0, land = 0, unknown = 0, cornersLand = 0; bool centerLand = false; var kind = WaterKind.Other;
            for (int i = 0; i < Probes.Length; i++)
            {
                var c = map.Query(rb.position + q * new Vector3(Probes[i].x, 0f, Probes[i].y), out var k);
                if (c == WaterMap.Cover.Water) { water++; if (i == 0) kind = k; }
                else if (c == WaterMap.Cover.Land) { land++; if (i == 0) centerLand = true; else cornersLand++; }
                else unknown++;
            }
            _pw = water; _pl = land;
            if (!Active)
            {
                // all probes in water (an unknown tile never enters), body near the surface, a flat surface below, and not on a bridge / pier
                if (water == Probes.Length && _rayValid && _rayFlat && rb.position.y - _rawLevel < enterHeight && Time.unscaledTime - _since > minDwell
                    && !map.NearSolid(rb.position, 7f)) Enter(kind);
            }
            else if (Time.unscaledTime - _since > minDwell && unknown < Probes.Length && (centerLand || cornersLand >= 3)) Exit();
        }

        void Enter(WaterKind kind)
        {
            Active = true; _kind = kind; _since = Time.unscaledTime;
            _level = _rawLevel; _levelValid = true; _jump = 0;
            Debug_Log("boat mode ON (" + kind + ")");
        }

        void Exit()
        {
            Active = false; Boosting = false; _since = Time.unscaledTime;
            car.OnBoatExit();
            Debug_Log("boat mode OFF");
        }

        /// <summary>Leave boat mode without waiting (respawn, rescue, teleport): the caller resets the vehicle state itself.</summary>
        public void ForceExit() { Active = false; Boosting = false; _sink = 0f; _since = Time.unscaledTime - 10f; _levelValid = false; }

        /// <summary>The Cesium origin moved: Unity heights changed with it (curvature), so the cached surface height is no longer valid.</summary>
        public void OnOriginShift() { _levelValid = false; }

        static void Debug_Log(string s) { UnityEngine.Debug.Log("[RocketWorld] water: " + s); }

        // ---------------------------------------------------------------------------------------------- 120 Hz tick

        /// <summary>Replaces RocketVehicle.PreTick while <see cref="Active"/>: all forces for this tick.</summary>
        public void PreStep(CarInput.Controls c, float dt)
        {
            var rb = car.Body;
            if (!_levelValid) RefreshLevel(0f);
            _simT += dt;
            Quaternion q = rb.rotation; Vector3 pos = rb.position, vel = rb.linearVelocity, w = rb.angularVelocity;
            Vector3 up = q * Vector3.up, fwd = q * Vector3.forward;
            float weight = rb.mass * Physics.gravity.magnitude, perPoint = weight / 6f;
            float swell = _kind == WaterKind.Ocean ? oceanSwellAmplitude : swellAmplitude;

            // --- buoyancy: spring (progressive, so a hard landing is caught) + damper at six hull points ---
            float imm = 0f, stern = 0f;
            for (int i = 0; i < 6; i++)
            {
                Vector3 rel = q * HullPoint(i), wp = pos + rel;
                float d = Mathf.Min(_level + Swell(wp.x, wp.z, swell) - wp.y, 1.2f);
                if (d <= 0f) continue;
                float ramp = Mathf.Clamp01(d / 0.15f), x = d / draft;
                imm += ramp; if (i < 2) stern += ramp;
                float vy = (vel + Vector3.Cross(w, rel)).y;
                float f = perPoint * 0.5f * x * (1f + x * x) - ramp * (heaveDamping * vy + heaveDampingQuad * vy * Mathf.Abs(vy)) / 6f;
                if (f > 0f) rb.AddForceAtPosition(Vector3.up * f, wp, ForceMode.Force);
            }
            _imm = imm / 6f; _stern = stern / 2f;
            float s = Mathf.Clamp01(_imm * 1.5f);                      // hydrodynamics scale with how much of the hull is in the water

            // --- propeller ---
            float thr = Mathf.Clamp(c.throttle, -1f, 1f);
            bool boost = c.boost && _stern > 0.2f;
            if (boost && !car.infiniteBoost)
            {
                var st = car.Vehicle.state;
                if (st.boost <= 0f) boost = false; else st.boost = Mathf.Max(0f, st.boost - RL.BoostUsedPerSecond * dt);
            }
            Boosting = boost;
            float cmd = boost ? 1f : thr;
            float thrust = (boost ? maxThrust * boostThrustMul : (cmd >= 0f ? cmd * maxThrust : cmd * maxThrust * reverseThrustMul)) * _stern;
            if (Mathf.Abs(thrust) > 0.01f) rb.AddForceAtPosition(fwd * thrust, pos + q * new Vector3(0f, hullBottom * 0.4f, -hullHalfLength), ForceMode.Force);

            // --- hull drag in body axes (keel: sideways drag >> forward drag) ---
            Quaternion inv = Quaternion.Inverse(q);
            Vector3 lv = inv * vel, lw = inv * w;
            float fx = -(dragLateral * Mathf.Abs(lv.x) + dragLateralLin) * lv.x;
            float fz = -(dragForward * Mathf.Abs(lv.z) + dragForwardLin) * lv.z;
            if (c.handbrake) { fz -= lv.z * brakeDrag; fx -= lv.x * brakeDrag * 0.5f; }
            rb.AddForce(q * new Vector3(fx, 0f, fz) * s, ForceMode.Force);

            // --- rudder, bank, damping (local torque: x pitch, y yaw, z roll; +y = turn right, +z = lean left) ---
            float vf = lv.z, steer = Mathf.Clamp(c.steer, -1f, 1f);
            float authority = Mathf.Max(Mathf.Clamp01(Mathf.Abs(vf) / 7f), Mathf.Abs(cmd) * 0.45f);   // rudder bites with speed, prop wash helps at standstill
            float dir = vf < -0.5f ? -1f : 1f;
            float speedScale = 1f / (1f + vf * vf / (steerFalloffSpeed * steerFalloffSpeed));
            float yaw = steer * yawTorque * authority * speedScale * dir * Mathf.Clamp01(_stern + 0.2f);
            float bank = -steer * bankTorque * Mathf.Clamp01(Mathf.Abs(vf) / 12f) * dir * s;
            Vector3 damp = new Vector3(-pitchDamping * lw.x, -yawDamping * lw.y, -rollDamping * lw.z) * (0.25f + 0.75f * s);
            rb.AddTorque(q * (new Vector3(0f, yaw, bank) + damp), ForceMode.Force);

            // --- self-righting (a boat that rolls over rights itself instead of lying on the water mesh) ---
            float tilt = 1f - up.y;
            if (tilt > 0.15f)
            {
                Vector3 axis = Vector3.Cross(up, Vector3.up);
                axis = axis.sqrMagnitude < 0.01f ? fwd : axis.normalized;
                rb.AddTorque(axis * (rightingTorque * (tilt - 0.15f) * (0.3f + 0.7f * s)), ForceMode.Force);
            }
        }

        /// <summary>Replaces PostTick / FinishTick while <see cref="Active"/>: RocketSim's speed limits.</summary>
        public void PostStep()
        {
            var rb = car.Body;
            float max = RL.CarMaxSpeed * RL.UuToBt;
            var v = rb.linearVelocity; if (v.sqrMagnitude > max * max) rb.linearVelocity = v.normalized * max;
            var w = rb.angularVelocity; if (w.sqrMagnitude > RL.CarMaxAngSpeed * RL.CarMaxAngSpeed) rb.angularVelocity = w.normalized * RL.CarMaxAngSpeed;
        }

        float Swell(float x, float z, float a)
            => a * (0.6f * Mathf.Sin(0.55f * x + 0.35f * z + _simT * 1.7f) + 0.4f * Mathf.Sin(-0.31f * x + 0.62f * z + _simT * 1.1f + 1.3f));
    }
}
