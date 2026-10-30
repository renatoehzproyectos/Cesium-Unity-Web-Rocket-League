using UnityEngine;

namespace RocketWorld
{
    /// <summary>What the visual needs from the physics each frame (all 0..1 except wheelSpeed, m/s).</summary>
    public struct VehicleViz { public float gear, fold, rotorRpm, burner, wheelSpeed, nozzle; }

    public enum VehicleToggle { Mode, Gear, Flaps }

    /// <summary>
    /// The seam between <see cref="VehicleDriver"/> (mode switching, colliders, visual, HUD, Hyper Boost) and a vehicle's flight / driving physics.
    /// Implement this with the JSBSim-based models and return them from <see cref="VehiclePhysicsFactory.Create"/>.
    /// </summary>
    public interface IVehicleModel
    {
        /// <summary>Called when the vehicle is entered. <paramref name="airborne"/>: there was no ground within a few metres.</summary>
        void Enter(VehicleDriver d, Rigidbody rb, bool airborne);
        /// <summary>One 120 Hz physics tick (before Physics.Simulate): read <paramref name="c"/>, add forces / torques to <paramref name="rb"/>, write <see cref="VehicleDriver.Viz"/>.</summary>
        void Step(VehicleDriver d, Rigidbody rb, in CarInput.Controls c, float dt);
        /// <summary>Mode (VTOL / hover), landing gear and flaps buttons.</summary>
        void Toggle(VehicleDriver d, VehicleToggle what);
        /// <summary>Throttle lever 0..1 (collective for the helicopter) for the HUD and the touch slider.</summary>
        float Lever { get; }
        bool OnGround { get; }
        float AlphaDeg { get; }
        bool Stalled { get; }
        /// <summary>Short HUD status, e.g. "GEAR DN  FLAPS 2  VTOL".</summary>
        string Status { get; }
    }

    public static class VehiclePhysicsFactory
    {
        /// <summary>Physics for a vehicle kind: JSBSim-driven models for the jets and the airliner (and helicopter / DeLorean once their JSBSim models are wired).</summary>
        internal static IVehicleModel Create(VehicleSpec spec)
        {
            switch (spec.kind)
            {
                case VehicleKind.F35B: case VehicleKind.F15E: case VehicleKind.Airliner: case VehicleKind.Helicopter: case VehicleKind.DeLorean: return new JsbJetModel(spec);
                default: return new PlaceholderPhysics(spec);
            }
        }
    }

    /// <summary>
    /// PLACEHOLDER physics so every vehicle can be selected, shown, steered by every control (stick, throttle slider, rudder, lift buttons, gear,
    /// flaps, mode, Hyper Boost) and flown around while the real JSBSim models are written. It is not a flight model: gravity is cancelled in
    /// proportion to speed, the throttle lever pushes along the nose, the stick sets body rates directly, the vertical buttons change the vertical speed.
    /// A reference attempt at proper models (rate-command jets with VTOL, helicopter, hover car) is archived in docs/vehicle-physics-draft/.
    /// </summary>
    internal sealed class PlaceholderPhysics : IVehicleModel
    {
        readonly VehicleSpec s;
        float _lever, _gear = 1f, _mode; int _flaps; bool _gearDown = true, _modeOn;
        public float Lever => _lever;
        public bool OnGround { get; private set; }
        public float AlphaDeg => 0f;
        public bool Stalled => false;

        public string Status
        {
            get
            {
                string t = s.hasGear ? (_gearDown ? "GEAR DN" : "GEAR UP") : "";
                if (s.flaps) t += "  FLAPS " + _flaps;
                if (s.vtol) t += _modeOn ? "  VTOL" : "  CRUISE";
                if (s.hoverCar) t += _modeOn ? "  HOVER" : "  WHEELS";
                return (t + "  [placeholder physics]").Trim();
            }
        }

        public PlaceholderPhysics(VehicleSpec spec) { s = spec; }

        public void Enter(VehicleDriver d, Rigidbody rb, bool airborne)
        {
            _lever = airborne && s.aero != AeroClass.None ? 0.7f : 0f; _gearDown = !airborne; _gear = airborne ? 0f : 1f; _flaps = 0;
            _modeOn = airborne && (s.hoverCar || s.rotors);
        }

        public void Toggle(VehicleDriver d, VehicleToggle what)
        {
            if (what == VehicleToggle.Gear && s.hasGear) _gearDown = !_gearDown;
            else if (what == VehicleToggle.Flaps && s.flaps) _flaps = (_flaps + 1) % 4;
            else if (what == VehicleToggle.Mode && (s.vtol || s.hoverCar)) _modeOn = !_modeOn;
        }

        public void Step(VehicleDriver d, Rigidbody rb, in CarInput.Controls c, float dt)
        {
            Quaternion q = rb.rotation, inv = Quaternion.Inverse(q);
            Vector3 fwd = q * Vector3.forward, v = rb.linearVelocity;
            if (c.leverSet) _lever = Mathf.MoveTowards(_lever, Mathf.Clamp01(c.lever), 2f * dt);
            else _lever = Mathf.Clamp01(_lever + ((c.jump ? 1f : 0f) - (c.handbrake ? 1f : 0f)) * 0.5f * dt);
            _gear = Mathf.MoveTowards(_gear, _gearDown ? 1f : 0f, dt / 4f);
            _mode = Mathf.MoveTowards(_mode, _modeOn ? 1f : 0f, dt / 1.2f);

            bool hovers = s.rotors || (s.vtol && _modeOn) || (s.hoverCar && _modeOn);
            float g = Vehicles.G, m = rb.mass;
            float speed = v.magnitude;
            float liftFrac = hovers ? 1f : Mathf.Clamp01(speed / Mathf.Max(20f, s.vMax * 0.25f));
            Vector3 force = Vector3.down * (g * m) + Vector3.up * (g * m * liftFrac);
            if (hovers) force += Vector3.up * (m * Mathf.Clamp(Mathf.Clamp(c.vert + (c.jump ? 1f : 0f) - (c.handbrake ? 1f : 0f), -1f, 1f) * 8f - v.y, -6f, 6f) * 1.5f);
            float push = s.aero != AeroClass.None || s.vtol ? _lever * (c.boost ? 1.6f : 1f) * 12f : c.throttle * (c.boost ? 1.6f : 1f) * 12f;
            if (s.rotors || (s.hoverCar && _modeOn)) push = c.throttle * 10f;
            force += fwd * (m * push);
            force -= v * (m * (s.aero != AeroClass.None ? 0.02f : 0.35f));
            rb.AddForce(force, ForceMode.Force);

            // the stick sets body rates directly: pitch (stick forward = nose down), roll, yaw
            Vector3 w = inv * rb.angularVelocity;
            Vector3 want = new Vector3(c.throttle * (s.aero != AeroClass.None ? 0.8f : 0.4f) * (s.hoverCar || s.rotors ? 0f : 1f), (c.roll + (s.hoverCar || s.rotors ? c.steer : 0f)) * 1.0f, -c.steer * 1.4f * (s.hoverCar ? 0f : 1f));
            rb.AddRelativeTorque((want - w) * 3f * Mathf.Max(rb.inertiaTensor.x, 1f) * 0.2f, ForceMode.Force);

            d.ApplyHyperDrive(rb, fwd, s.vMax, dt, true);
            OnGround = Physics.Raycast(rb.position, Vector3.down, 1.2f, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore) && speed < 2f;
            d.Viz = new VehicleViz { gear = _gear, fold = _mode * (s.hoverCar ? 1f : 0f), rotorRpm = s.rotors ? 1f : 0f, burner = c.boost ? 1f : Mathf.Clamp01(_lever - 0.5f) * 0.4f, wheelSpeed = speed, nozzle = s.vtol ? _mode : 0f };
        }
    }
}
