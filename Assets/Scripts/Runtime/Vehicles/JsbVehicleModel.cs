using RocketWorld.Sim;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>Loads the unmodified JSBSim files in Resources/Flight/jsb (aircraft, engine, system, thruster) into a <see cref="JsbFdm"/>.</summary>
    public static class JsbRes
    {
        public static string Text(string name)
        {
            var t = Resources.Load<TextAsset>("Flight/jsb/" + name);
            if (t == null) throw new System.IO.FileNotFoundException("Resources/Flight/jsb/" + name + ".xml");
            return t.text;
        }
        public static JsbFdm Load(string aircraft) => JsbFdm.Load(Text(aircraft), Text, Text, Text);
    }

    /// <summary>Per-vehicle JSBSim wiring: which file, how the throttle lever maps onto the JSBSim throttle, which controls exist.</summary>
    public sealed class JsbVehicleCfg
    {
        public string file; public bool augmentedThrottle, stovl, speedbrake, flapDetents, reverser, autoTrim, heli, hoverCar; public float[] flapSteps;
        public static JsbVehicleCfg For(VehicleKind k)
        {
            switch (k)
            {
                case VehicleKind.F15E: return new JsbVehicleCfg { file = "f15e", augmentedThrottle = true, speedbrake = true, autoTrim = true };
                case VehicleKind.F35B: return new JsbVehicleCfg { file = "f35b", stovl = true };
                case VehicleKind.Helicopter: return new JsbVehicleCfg { file = "heli", heli = true };
                case VehicleKind.DeLorean: return new JsbVehicleCfg { file = "delorean", hoverCar = true };
                default: return null;
            }
        }

        /// <summary>
        /// Puts the centre of mass where JSBSim has it relative to the landing gear: the model's wheel positions stay the truth for contact and looks,
        /// the CG sits at the same fraction of the wheelbase and the same height above the wheel bottoms as in the JSBSim file.
        /// </summary>
        public static VehicleSpec Align(VehicleSpec sp)
        {
            var cfg = For(sp.kind); if (cfg == null) return sp;
            var f = JsbRes.Load(cfg.file);
            if (cfg.heli) return sp;                                                                   // spec.com / hub already follow the JSBSim file
            if (cfg.hoverCar)
            {   // centroid of the contact points: model wheels (bottoms) vs JSBSim contacts
                double jx = 0, jz = 0; foreach (var g in f.gears) { var b = f.StructuralToBody(g.locIn); jx += b.x; jz += b.z; } jx /= f.gears.Count; jz /= f.gears.Count;
                float mx = 0, my = 0, mz = 0; foreach (var w in sp.wheels) { mx += w.pos.x; my += w.pos.y - w.radius; mz += w.pos.z; } int nw = sp.wheels.Length;
                sp.com = new Vector3(0f, my / nw + (float)(jz * PlaneModel.FtToM), mz / nw + (float)(jx * PlaneModel.FtToM));
                return sp;
            }
            double nose = 0, main = 0, h = 0; int nm = 0, nn = 0; double zn = 0, zm = 0;
            foreach (var g in f.gears) { var b = f.StructuralToBody(g.locIn); h += b.z; if (System.Math.Abs(g.locIn.y) < 1.0) { zn += b.x; nn++; } else { zm += b.x; nm++; } }
            h /= f.gears.Count; if (nn == 0 || nm == 0) return sp;
            zn /= nn; zm /= nm; double frac = (0 - zm) / (zn - zm);
            float mainZ = 0, noseZ = 0, bottom = 0; int cm = 0, cn = 0;
            foreach (var w in sp.wheels) { if (w.steer) { noseZ += w.pos.z; cn++; } else { mainZ += w.pos.z; cm++; } bottom += w.pos.y - w.radius; }
            mainZ /= cm; noseZ /= cn; bottom /= sp.wheels.Length;
            sp.com = new Vector3(0f, bottom + (float)(h * PlaneModel.FtToM), mainZ + (float)frac * (noseZ - mainZ));
            return sp;
        }
    }

    /// <summary>
    /// F-35B, F-15E, helicopter and DeLorean on unmodified-format JSBSim aircraft files (see <see cref="JsbFdm"/>): the interpreter returns body forces and moments each 120 Hz tick
    /// (aerodynamics, turbine engines with vectoring, lift fan, roll posts), this class applies them to the Rigidbody, adds gravity and the gear strut / tyre model.
    /// </summary>
    internal sealed class JsbJetModel : IVehicleModel
    {
        readonly VehicleSpec s; readonly JsbVehicleCfg cfg; JsbFdm f;
        float _lever, _el, _ail, _rud, _trim, _drive; int _flaps; bool _gearDown = true, _stovl, _sb, _reverse, _onGround, _hover; float _alpha; D3 _uvwPrev; bool _hasPrev;
        readonly bool[] _wow = new bool[8]; int _enginesStarted;

        public JsbJetModel(VehicleSpec spec) { s = spec; cfg = JsbVehicleCfg.For(spec.kind); }
        public float Lever => _lever;
        public bool OnGround => _onGround;
        public float AlphaDeg => _alpha;
        public bool Stalled => !cfg.heli && !cfg.hoverCar && !_onGround && _alpha > 28f;
        public string Status
        {
            get
            {
                if (cfg.heli) return "ROTOR " + (f != null ? (f["rotor/omega-rad_sec"] / 33.5 * 100).ToString("0") : "100") + "%  COLL " + (_lever * 100f).ToString("0") + "%  JSBSim";
                if (cfg.hoverCar) return (_hover ? "HOVER" : "WHEELS") + "  JSBSim";
                string t = _gearDown ? "GEAR DN" : "GEAR UP";
                if (cfg.flapDetents) t += "  FLAPS " + _flaps;
                if (cfg.stovl) t += _stovl ? "  VTOL" : "  CRUISE";
                if (cfg.speedbrake) t += _sb ? "  BRAKE" : "";
                if (_reverse) t += "  REV";
                return t + "  JSBSim";
            }
        }

        public void Enter(VehicleDriver d, Rigidbody rb, bool airborne)
        {
            f = JsbRes.Load(cfg.file);
            rb.mass = (float)(f.massSlug * PlaneModel.SlugToKg);
            rb.inertiaTensor = new Vector3((float)(f.iyy * 1.35582), (float)(f.izz * 1.35582), (float)(f.ixx * 1.35582));   // Unity local x pitch, y yaw, z roll
            rb.inertiaTensorRotation = Quaternion.identity;
            _gearDown = !airborne; _flaps = 0; _sb = false; _stovl = false; _reverse = false; _trim = 0; _el = _ail = _rud = 0; _hasPrev = false;
            f["gear/gear-cmd-norm"] = _gearDown ? 1 : 0; f["gear/gear-pos-norm"] = _gearDown ? 1 : 0;
            _lever = airborne ? 0.7f : 0f;
            if (cfg.heli) { _lever = airborne ? 0.52f : 0.25f; _gearDown = true; }
            if (cfg.hoverCar) { _hover = airborne; _gearDown = !airborne; f["fcs/hover-cmd-norm"] = airborne ? 1 : 0; f["fcs/hover-pos-norm"] = airborne ? 1 : 0; f["gear/gear-pos-norm"] = airborne ? 0 : 1; _lever = 0f; }
            f.StartEngines(airborne ? ThrottleOf(_lever) : 0.0);
            _enginesStarted = 1;
        }

        public void Toggle(VehicleDriver d, VehicleToggle what)
        {
            if (cfg.hoverCar) { if (what == VehicleToggle.Mode) { _hover = !_hover; f["fcs/hover-cmd-norm"] = _hover ? 1 : 0; } return; }
            if (cfg.heli) return;
            if (what == VehicleToggle.Gear) { _gearDown = !_gearDown; f["gear/gear-cmd-norm"] = _gearDown ? 1 : 0; }
            else if (what == VehicleToggle.Flaps && cfg.flapDetents) _flaps = (_flaps + 1) % cfg.flapSteps.Length;
            else if (what == VehicleToggle.Mode) { if (cfg.stovl) _stovl = !_stovl; else if (cfg.speedbrake) _sb = !_sb; }
        }

        double ThrottleOf(float lever) => cfg.augmentedThrottle ? (lever <= 0.75f ? lever / 0.75 : 1.0 + (lever - 0.75) / 0.25) : lever;

        public void Step(VehicleDriver d, Rigidbody rb, in CarInput.Controls c, float dt)
        {
            Quaternion q = rb.rotation, inv = Quaternion.Inverse(q); Vector3 vW = rb.linearVelocity, wW = rb.angularVelocity;
            // ---- pilot inputs (stick smoothing like the Cessna: JSBSim convention, stick forward = elevator cmd +, rudder cmd + = nose left)
            if (cfg.hoverCar) _lever = Mathf.Abs(c.throttle);
            else if (c.leverSet) _lever = Mathf.MoveTowards(_lever, Mathf.Clamp01(c.lever), 2f * dt);
            else _lever = Mathf.Clamp01(_lever + ((c.jump ? 1f : 0f) - (c.handbrake && !_reverse ? 1f : 0f)) * (cfg.heli ? 0.3f : 0.45f) * dt);
            float tgtEl = Mathf.Clamp(c.throttle, -1f, 1f), tgtAil = Mathf.Clamp(c.steer, -1f, 1f), tgtRud = Mathf.Clamp(-(c.roll + (_onGround ? c.steer * 0.6f : c.steer * 0.8f)), -1f, 1f);
            float rate = 3.2f * dt; _el = Mathf.MoveTowards(_el, tgtEl, rate); _ail = Mathf.MoveTowards(_ail, tgtAil, rate); _rud = Mathf.MoveTowards(_rud, tgtRud, rate);
            // reverse thrust: hold the lever brake at idle on the ground
            if (cfg.reverser) _reverse = _onGround && _lever < 0.02f && c.handbrake && vW.magnitude > 3f;

            D3 uvw = PlaneModel.VelocityToBody(inv * vW), pqr = PlaneModel.RatesToBody(inv * wW);
            if (cfg.heli)
            {   // JSBSim helicopter: stick = velocity command (forward stick = elevator cmd +), pedals = yaw rate (rudder cmd + = nose left), lever = collective
                f["fcs/elevator-cmd-norm"] = _el; f["fcs/aileron-cmd-norm"] = _ail; f["fcs/rudder-cmd-norm"] = Mathf.Clamp(-c.roll, -1f, 1f);
                f["fcs/collective-cmd-norm"] = _lever; f["fcs/vert-cmd-norm"] = Mathf.Clamp(c.vert, -1f, 1f);
            }
            else if (cfg.hoverCar)
            {
                _drive = Mathf.MoveTowards(_drive, Mathf.Clamp(c.throttle, -1f, 1f), 4f * dt);
                f["fcs/throttle-cmd-norm"] = _drive; f["fcs/steer-cmd-norm"] = Mathf.Clamp(c.steer, -1f, 1f); f["fcs/elevator-cmd-norm"] = 0f;
                f["fcs/vert-cmd-norm"] = Mathf.Clamp(c.vert + (c.jump ? 1f : 0f), -1f, 1f);
            }
            else if (cfg.autoTrim && !_onGround)
            {   // pitch hold assist: while the stick is centred the trim nulls the pitch rate (what a trimmed airliner / fighter FBW does)
                if (Mathf.Abs(_el) < 0.05f) _trim = Mathf.Clamp(_trim + (float)pqr.y * 0.6f * dt, -0.6f, 0.6f);
            }
            if (!cfg.heli && !cfg.hoverCar)
            {
                f["fcs/elevator-cmd-norm"] = _el; f["fcs/aileron-cmd-norm"] = _ail; f["fcs/rudder-cmd-norm"] = _rud; f["fcs/pitch-trim-cmd-norm"] = _trim;
                f["fcs/speedbrake-cmd-norm"] = _sb ? 1 : 0; f["fcs/flap-cmd-norm"] = cfg.flapDetents ? cfg.flapSteps[_flaps] : 0f;
            }
            if (cfg.heli || cfg.hoverCar) { }
            else if (cfg.stovl)
            {
                f["fcs/stovl-cmd-norm"] = _stovl ? 1 : 0; f["fcs/lever-norm"] = _lever; f["fcs/vert-cmd-norm"] = Mathf.Clamp(c.vert, -1f, 1f);
                f["fcs/flap-cmd-norm"] = _stovl || (!_onGround && uvw.x < 150 * 1.68781) ? 1 : 0;
            }
            else
            {
                double thr = ThrottleOf(_lever); if (_reverse) thr = 0.7;
                for (int i = 0; i < f.engines.Count; i++) { f["fcs/throttle-cmd-norm[" + i + "]"] = thr; f["propulsion/engine[" + i + "]/reverser-angle-rad"] = _reverse ? System.Math.PI : 0.0; }
            }

            // ---- JSBSim frame
            float altM = d.HeightMeters(rb);
            float agl = 3000f; if (Physics.Raycast(rb.position, Vector3.down, out var gh, 3000f, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore)) agl = gh.distance;
            Vector3 fwd = q * Vector3.forward, rgt = q * Vector3.right, up = q * Vector3.up;
            // body axes in local NED (north = Unity +Z, east = +X, down = -Y)
            double xbN = fwd.z, xbE = fwd.x, xbD = -fwd.y, ybD = -rgt.y, zbD = up.y;       // NED components of the body axes (body z = down = -up)
            double theta = -System.Math.Asin(System.Math.Max(-1.0, System.Math.Min(1.0, xbD))), phi = System.Math.Atan2(ybD, zbD), psi = System.Math.Atan2(xbE, xbN);
            D3 uvwDot = _hasPrev ? (uvw - _uvwPrev) * (1.0 / dt) : new D3(); _uvwPrev = uvw; _hasPrev = true;
            var st = new JsbFdm.State { uvw = uvw, pqr = pqr, uvwDot = uvwDot, altSlFt = altM / PlaneModel.FtToM, aglFt = agl / PlaneModel.FtToM, phi = phi, theta = theta, psi = psi };
            var o = f.Step(st, dt);

            float spd = vW.magnitude, vRef = s.vMax, qs = spd > vRef * 1.25f ? Mathf.Pow(vRef * 1.25f / spd, 2f) : 1f;   // Hyper Boost caps the aerodynamic load
            rb.mass = (float)(f.massSlug * PlaneModel.SlugToKg);
            rb.AddForce(q * PlaneModel.ForceToUnity(o.force) * qs, ForceMode.Force);
            rb.AddTorque(q * PlaneModel.MomentToUnity(o.moment) * qs, ForceMode.Force);
            rb.AddForce(Vector3.down * (Vehicles.G * rb.mass), ForceMode.Force);
            float brakeIn = cfg.hoverCar ? (c.handbrake || c.wheelBrake ? 1f : 0f) : cfg.heli ? (c.wheelBrake ? 1f : 0f) : (c.wheelBrake || c.boost ? 1f : 0f) + (c.handbrake && !cfg.reverser && _lever < 0.02f ? 1f : 0f);
            ApplyGear(rb, q, vW, wW, brakeIn, cfg.hoverCar ? Mathf.Clamp(c.steer, -1f, 1f) : tgtAil, tgtRud);
            if (d.car.HyperMul > 1.001f || spd > vRef * 1.1f) d.ApplyHyperDrive(rb, fwd, vRef, dt, true);

            _alpha = (float)(o.alpha * 57.29578);
            if (cfg.heli) { d.Viz = new VehicleViz { gear = 1f, rotorRpm = Mathf.Clamp01((float)(f["rotor/omega-rad_sec"] / 33.5)), wheelSpeed = spd }; return; }
            if (cfg.hoverCar) { d.Viz = new VehicleViz { gear = (float)f["gear/gear-pos-norm"], fold = (float)f["fcs/hover-pos-norm"], wheelSpeed = (float)uvw.x * PlaneModel.FtToM }; return; }
            d.Viz = new VehicleViz
            {
                gear = (float)f["gear/gear-pos-norm"], fold = 0f, rotorRpm = 0f, wheelSpeed = spd,
                burner = f.engines.Count > 0 && f.engines[0].Afterburner ? 1f : (c.hyper ? 1f : 0f),
                nozzle = cfg.stovl ? Mathf.Clamp01((float)f["fcs/nozzle-pos-deg"] / 90f) : 0f
            };
        }

        /// <summary>FGLGear on the model's wheel positions: strut spring / damper along the ground normal, rolling + brake friction, tyre side force.</summary>
        void ApplyGear(Rigidbody rb, Quaternion q, Vector3 vW, Vector3 wW, float brake, float steer, float rudder)
        {
            bool any = false; int n = 0;
            for (int i = 0; i < f.gears.Count && i < s.wheels.Length; i++)
            {
                var g = f.gears[i];
                // JSBSim contact i <-> model wheel: nose = steer wheel, mains by side (JSBSim y > 0 = right = model +x)
                WheelDef w = MatchWheel(g);
                Vector3 local = new Vector3(w.pos.x, w.pos.y - w.radius, w.pos.z) - s.com, arm = q * local, wp = rb.position + arm;
                bool extended = g.Extended; if (!extended) { _wow[i] = false; continue; }
                const float lift = 0.5f, reach = 0.5f + 0.9f;
                bool hitOk = Physics.Raycast(wp + Vector3.up * lift, Vector3.down, out var hit, reach, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore);
                float height = hitOk ? hit.distance - lift : 1f; _wow[i] = hitOk && height < 0f; any |= _wow[i];
                if (!_wow[i]) continue;
                Vector3 nrm = hit.normal, vp = vW + Vector3.Cross(wW, arm);
                float compress = Mathf.Min(-height, 0.9f) / PlaneModel.FtToM, compressSpeed = -Vector3.Dot(vp, nrm) / PlaneModel.FtToM;
                float N = (float)-g.StrutForce(compress, compressSpeed) * PlaneModel.LbfToN;
                rb.AddForceAtPosition(nrm * N, hit.point, ForceMode.Force);
                float speed = vp.magnitude, steerFactor = 1f / (1f + (speed / 9f) * (speed / 9f));
                Vector3 fw = Vector3.ProjectOnPlane(q * Vector3.forward, nrm).normalized;
                if (g.maxSteerDeg > 0.01) fw = Quaternion.AngleAxis((Mathf.Abs(steer) > 0.01f ? steer : -rudder * 0.5f) * (float)g.maxSteerDeg * steerFactor, nrm) * fw;
                Vector3 rt = Vector3.Cross(nrm, fw).normalized;
                float vx = Vector3.Dot(vp, fw), vy = Vector3.Dot(vp, rt);
                float slip = -Mathf.Atan2(vy, Mathf.Abs(vx)) * Mathf.Rad2Deg;
                float side = Mathf.Sqrt(vx * vx + vy * vy) > 1f ? (float)g.SideForceCoeff(slip) : Mathf.Clamp(-vy, -1f, 1f) * (float)g.staticF;
                float b = g.brakeGroup == "NONE" ? 0f : Mathf.Clamp01(brake);
                float roll = -Mathf.Clamp(vx / 0.8f, -1f, 1f) * (float)g.BrakeForceCoeff(b);
                rb.AddForceAtPosition((rt * side + fw * roll) * N, hit.point, ForceMode.Force);
                n++;
            }
            _onGround = any;
        }

        /// <summary>The model wheel nearest to a JSBSim contact (both relative to the CG, metres, x forward / y right).</summary>
        WheelDef MatchWheel(JsbGear g)
        {
            D3 b = f.StructuralToBody(g.locIn); float fx = (float)b.x * PlaneModel.FtToM, ry = (float)b.y * PlaneModel.FtToM;
            WheelDef best = s.wheels[0]; float bd = float.MaxValue;
            foreach (var w in s.wheels)
            {
                float dz = (w.pos.z - s.com.z) - fx, dx = (w.pos.x - s.com.x) - ry, d2 = dz * dz + dx * dx;
                if (d2 < bd) { bd = d2; best = w; }
            }
            return best;
        }
    }
}
