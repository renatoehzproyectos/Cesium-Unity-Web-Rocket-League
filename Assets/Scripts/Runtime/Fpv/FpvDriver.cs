using RocketWorld.Sim;
using RocketWorld.Sim.Fpv;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// FPV drone mode (K). The flight model is ShramkoGSFPV's (RocketWorld.Sim.Fpv, a C# port checked against the original): motors with lag and
    /// battery sag, Betaflight-style rate PID, acro / horizon / angle, rotor drag, prop wash, crash detection. It runs at its native 1 kHz inside
    /// RocketWorld's 120 Hz tick (8 or 9 model steps per tick) and collides with the Cesium tile colliders through <see cref="FpvWorld"/>.
    /// The car's Rigidbody becomes a pose carrier (no gravity, hit collider off): the model writes its pose into it after every tick, so the
    /// existing interpolation, minimap, streaming and chase camera follow the drone like any other vehicle. Like the Cessna, this mode is local
    /// only: other players still see the car model at the drone's pose.
    /// </summary>
    [DefaultExecutionOrder(-37)]
    public class FpvDriver : MonoBehaviour
    {
        public RocketCar car;
        public FpvCamera fpvCam;
        public bool Active { get; private set; }

        public FpvSim Model => _sim;
        public bool Armed => _sim != null && _sim.Armed;
        public bool Crashed => _sim != null && _sim.Crashed;
        public float Throttle01 => (float)_input.Throttle01;
        public FpvParams Params => _prm;
        public FpvPreset Preset => FpvPresets.All[_presetIdx];
        public FlightMode Mode => _input.mode;
        public string Status { get; private set; } = "";
        public float UptiltDeg => (float)(_prm != null ? _prm.cameraUptiltDeg + _uptiltAdd : 20f);
        public float FovDeg => (float)(_prm != null ? _prm.cameraFovDeg : 115f);
        public float HeightAgl { get; private set; }
        public float SpeedMs => _sim != null ? (float)System.Math.Sqrt(_sim.s[FpvS.vx] * _sim.s[FpvS.vx] + _sim.s[FpvS.vy] * _sim.s[FpvS.vy] + _sim.s[FpvS.vz] * _sim.s[FpvS.vz]) : 0f;
        public string Info => Active ? $"FPV {Preset.name}  {SpeedMs * 3.6f:0} km/h  {FpvModes.Name(Mode)}  {(_sim.Armed ? "armed" : "disarmed")}" : "fpv off";

        FpvSim _sim; FpvParams _prm; int _presetIdx;
        readonly FpvInput _input = new FpvInput();
        readonly FpvWorld _world = new FpvWorld();
        FpvVisual _vis; Renderer[] _hidden; readonly double[] _mot = new double[4];
        double _acc; Vector3 _lastPos;
        float _uptiltAdd, _statusUntil, _aglT, _voidT;
        bool _carGravity; float _carMaxLin; Vector3 _home; float _homeYaw;
        bool _prevEq, _prevMinus;
        Vector3 _safePos; bool _hasSafe;

        void Awake() { if (car == null) car = GetComponent<RocketCar>(); _presetIdx = FpvPresets.IndexOf(PlayerPrefs.GetString("rw2.fpv.preset", FpvPresets.DefaultId)); }

        // ------------------------------------------------------------------------------------------------ mode switch
        public void Toggle() { if (Active) Leave(); else Enter(); }

        void Build()
        {
            var pre = Preset;
            _prm = FpvCompile.Compile(pre);
            _world.mask = PhysicsSetup.WorldMask;
            _sim = new FpvSim(_prm, _world);
            if (_vis != null) Destroy(_vis.gameObject);
            _vis = FpvVisual.Build(car.visual, (float)(pre.wheelbaseMm / 1000.0), (float)(pre.propDiameterMm > 0 ? pre.propDiameterMm / 1000.0 : pre.wheelbaseMm / 2000.0));
            if (_hidden != null) HideCarVisual();
        }

        void HideCarVisual()
        {
            _hidden = car.visual.GetComponentsInChildren<Renderer>(true);
            foreach (var r in _hidden) if (r != null && !r.transform.IsChildOf(_vis.transform)) r.enabled = false;
        }

        void Enter()
        {
            if (!car.CanTeleport || Active) return;
            if (car.water != null) car.water.ForceExit();
            if (car.plane != null) car.plane.ForceExit();
            if (car.vehicle != null) car.vehicle.ForceExit();
            var rb = car.Body;
            _carGravity = rb.useGravity; _carMaxLin = rb.maxLinearVelocity;
            Build();
            float yaw = rb.rotation.eulerAngles.y;
            Vector3 pos = rb.position;
            if (Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out var hit, 8f, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore))
                pos = hit.point + Vector3.up * ((float)_prm.bodyBottom + 0.02f);
            _home = pos; _homeYaw = yaw;
            rb.useGravity = false; rb.maxLinearVelocity = 6000f;
            rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
            car.HitCollider.enabled = false;
            HideCarVisual();
            Active = true;
            PlaceAt(pos, yaw, true);
            _input.Reset(); _uptiltAdd = 0f; _hasSafe = false;
            SetStatus(car.input != null && car.input.TouchControlsActive ? "FPV  —  throttle down, then tap ARM" : "FPV  —  lower the throttle, then arm (Space / A)", 8f);
            Debug.Log("[RocketWorld] FPV drone ON (" + Preset.name + ")");
        }

        void Leave()
        {
            if (!Active) return;
            var rb = car.Body; Restore(rb);
            float yaw = rb.rotation.eulerAngles.y;
            Vector3 up = Physics.Raycast(rb.position + Vector3.up * 3f, Vector3.down, out _, 200f, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore) ? Vector3.up * 0.6f : Vector3.up * 1.2f;
            rb.position += up; rb.rotation = Quaternion.Euler(0, yaw, 0); rb.angularVelocity = Vector3.zero;
            var v = rb.linearVelocity; rb.linearVelocity = Vector3.ClampMagnitude(new Vector3(v.x, Mathf.Min(v.y, 0f), v.z), RL.CarMaxSpeed * RL.UuToBt);
            car.OnBoatExit();
            Debug.Log("[RocketWorld] FPV drone OFF");
        }

        void Restore(Rigidbody rb)
        {
            Active = false;
            if (_vis != null) _vis.SetVisible(false);
            if (car.HitCollider != null) car.HitCollider.enabled = true;
            if (_hidden != null) foreach (var r in _hidden) if (r != null && (_vis == null || !r.transform.IsChildOf(_vis.transform))) r.enabled = true;
            if (rb != null) { rb.useGravity = _carGravity; rb.maxLinearVelocity = _carMaxLin; }
            if (fpvCam != null) fpvCam.enabled = false;
        }

        /// <summary>Respawn / rescue / network hand-over: drop the mode without touching the car state (the caller resets it).</summary>
        public void ForceExit() { if (Active) Restore(car != null ? car.Body : null); }

        void PlaceAt(Vector3 posUnity, float yawDeg, bool fresh)
        {
            FpvFrame.ToSim(posUnity, out double x, out double y, out double z);
            _sim.Respawn(x, y, z, yawDeg, 1.0);                  // a respawn is a fresh pack
            _input.armSwitch = false; _input.ch[4] = -1; _acc = 0;
            WritePose(car.Body);
            car.SnapVisual();
        }

        /// <summary>R: after a crash, start again where it happened (level, still); while flying, back to the launch point.</summary>
        public void OnReset()
        {
            if (!Active) return;
            var rb = car.Body;
            if (_sim.Crashed)
            {
                Vector3 p = rb.position + Vector3.up * 0.3f;
                float yaw; { FpvSim.Attitude(_sim.s, out _, out _, out double yw); yaw = (float)yw; }
                PlaceAt(p, yaw, false); SetStatus("respawned here", 2f);
            }
            else { PlaceAt(_home, _homeYaw, false); SetStatus("back at the launch point", 2f); }
        }

        public void NextPreset()
        {
            _presetIdx = (_presetIdx + 1) % FpvPresets.All.Length;
            PlayerPrefs.SetString("rw2.fpv.preset", Preset.id);
            var rb = car.Body; Vector3 p = rb.position;
            Build();
            PlaceAt(p + Vector3.up * 0.1f, rb.rotation.eulerAngles.y, false);
            SetStatus(Preset.name, 3f);
        }

        void SetStatus(string s, float sec) { Status = s; _statusUntil = Time.unscaledTime + sec; }

        // ------------------------------------------------------------------------------------------------ per frame
        void Update()
        {
            if (!Active || _sim == null) return;
            var ci = car.input;
            bool blocked = ci != null && ci.Blocked;
            _input.Update(Time.unscaledDeltaTime, blocked, ci);
            if (ci != null && !blocked)
            {
                if (ci.ModePressed) { _input.CycleMode(); SetStatus(FpvModes.Name(_input.mode), 1.5f); }
                if (ci.GearPressed) NextPreset();
            }
            bool eq = InputDevices.KeyValue("Equals") > 0, mn = InputDevices.KeyValue("Minus") > 0;
            bool tUp = ci != null && !blocked && ci.TiltUpPressed, tDn = ci != null && !blocked && ci.TiltDownPressed;
            if ((eq && !_prevEq) || tUp) { _uptiltAdd += 5f; SetStatus($"camera tilt {UptiltDeg:0}°", 1.5f); }
            if ((mn && !_prevMinus) || tDn) { _uptiltAdd -= 5f; SetStatus($"camera tilt {UptiltDeg:0}°", 1.5f); }
            _prevEq = eq; _prevMinus = mn;

            if (_vis != null)
            {
                _vis.SetVisible(fpvCam == null || !fpvCam.enabled);
                for (int i = 0; i < 4; i++) _mot[i] = _sim.s[FpvS.m0 + i];
                _vis.Spin(_mot, Time.unscaledDeltaTime);
            }
            if (Time.unscaledTime >= _statusUntil) Status = "";

            _aglT -= Time.unscaledDeltaTime;
            if (_aglT <= 0f)
            {
                _aglT = 0.2f;
                Vector3 p = car.Body.position;
                if (Physics.Raycast(p + Vector3.up * 0.2f, Vector3.down, out var h, 400f, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore))
                {
                    HeightAgl = h.distance - 0.2f; _safePos = p; _hasSafe = true; _voidT = 0f;
                }
                else
                {
                    HeightAgl = -1f;
                    // nothing solid below for a long way and well under the last safe spot: the tiles under us are gone, go back to it
                    if (_hasSafe && p.y < _safePos.y - 30f) { _voidT += 0.2f; if (_voidT > 0.6f) { PlaceAt(_safePos + Vector3.up * 1f, car.Body.rotation.eulerAngles.y, false); SetStatus("terrain was missing — put back", 3f); _voidT = 0f; } }
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ 120 Hz tick
        public void PreStep(in CarInput.Controls c, float dt)
        {
            if (!Active || _sim == null) return;
            var rb = car.Body;
            if ((rb.position - _lastPos).sqrMagnitude > 1e-6f) Resync(rb);   // Cesium origin shift or a teleport: take the pose back from the body
            float speed = SpeedMs;
            _world.Prepare(rb.position, (float)_prm.boundRadius + speed * dt * 1.5f + 0.6f);
            _sim.SetChannels(_input.ch);
            _acc += dt;
            int n = (int)(_acc / FpvSim.DT + 1e-9); _acc -= n * FpvSim.DT; if (n > 25) n = 25;
            for (int i = 0; i < n; i++) _sim.Step();
            Drain();
            // the model refuses to arm with the throttle up: let the switch go, so the next press is a fresh edge
            if (_input.armSwitch && !_sim.Armed && !_sim.Crashed && _input.ch[2] > -0.9)
            { _input.armSwitch = false; SetStatus("lower the throttle to arm", 2f); }
        }

        public void PostStep() { if (Active && _sim != null) WritePose(car.Body); }

        void Drain()
        {
            var ev = _sim.events;
            for (int i = 0; i < ev.Count; i++)
            {
                var e = ev[i];
                if (e.type == FpvEventType.Crash) { _input.armSwitch = false; SetStatus($"CRASH {e.speed:0.0} m/s — press R", 6f); }
                else if (e.type == FpvEventType.Arm) SetStatus("ARMED", 1.2f);
                else if (e.type == FpvEventType.Disarm && e.reason == "switch") SetStatus("DISARMED", 1.2f);
            }
            ev.Clear();
        }

        void Resync(Rigidbody rb)
        {
            FpvFrame.ToSim(rb.position, out double x, out double y, out double z);
            FpvFrame.ToSim(rb.rotation, out double qw, out double qx, out double qy, out double qz);
            FpvFrame.ToSim(rb.linearVelocity, out double vx, out double vy, out double vz);
            _sim.SetPose(x, y, z, qw, qx, qy, qz, vx, vy, vz);
        }

        void WritePose(Rigidbody rb)
        {
            var s = _sim.s;
            rb.position = FpvFrame.ToUnity(s[FpvS.px], s[FpvS.py], s[FpvS.pz]);
            rb.rotation = FpvFrame.ToUnity(s[FpvS.qw], s[FpvS.qx], s[FpvS.qy], s[FpvS.qz]);
            rb.linearVelocity = FpvFrame.ToUnity(s[FpvS.vx], s[FpvS.vy], s[FpvS.vz]);
            rb.angularVelocity = Vector3.zero;
            _lastPos = rb.position;
        }

        // ------------------------------------------------------------------------------------------------ OSD
        GUIStyle _osd, _osdS, _big;
        void OnGUI()
        {
            if (!Active || _sim == null) return;
            if (_osd == null)
            {
                _osd = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
                _big = new GUIStyle(_osd) { fontSize = 26 };
            }
            var oldMatrix = GUI.matrix;
            UiLayout.Begin();
            float W = UiLayout.W, H = UiLayout.H;
            var s = _sim.s;
            FpvSim.Attitude(s, out double roll, out double pitch, out double yaw);
            double volts = s[FpvS.volt], cell = volts / _prm.cells;
            string arm = _sim.Armed ? "ARMED" : _sim.Crashed ? "CRASHED" : "DISARMED";
            Color cc = cell < 3.3 ? Color.red : cell < 3.5 ? new Color(1f, 0.7f, 0.1f) : Color.white;
            string line = $"{FpvModes.Name(Mode)}   {arm}   {SpeedMs * 3.6f:0} km/h   AGL {(HeightAgl >= 0 ? HeightAgl.ToString("0.0") + " m" : "--")}   {volts:0.0} V ({cell:0.00}/cell)   BAT {s[FpvS.soc] * 100:0}%";
            bool touch = car.input != null && car.input.TouchControlsActive;
            if (touch)      // phones: the bottom is taken by the sticks and buttons, so a short telemetry line goes under the top bar
            {
                _osdS ??= new GUIStyle(_osd) { fontSize = 14 };
                string shortLine = $"{FpvModes.Name(Mode)} · {arm} · {SpeedMs * 3.6f:0} km/h · AGL {(HeightAgl >= 0 ? HeightAgl.ToString("0") + " m" : "--")} · BAT {s[FpvS.soc] * 100:0}%";
                { float lw = UiLayout.BannerWidth(420f); Outline(new Rect((W - lw) * 0.5f, UiLayout.TopBlockBottom + UiLayout.ExtraTop, lw, 24), shortLine, _osdS, cc); }
            }
            else Outline(new Rect(W * 0.5f - 420, H - 54, 840, 28), line, _osd, cc);
            if (_sim.washNow > 0.25) Outline(new Rect(W * 0.5f - 200, H * 0.30f, 400, 32), "PROP WASH", _big, new Color(1f, 0.6f, 0.15f));
            if (!string.IsNullOrEmpty(Status)) { float sw = UiLayout.BannerWidth(800f); Outline(new Rect((W - sw) * 0.5f, H * 0.40f, sw, 40), Status, _big, Color.white); }
            if (!_sim.Armed && !_sim.Crashed && touch)
                { float hw = UiLayout.BannerWidth(600f); Outline(new Rect((W - hw) * 0.5f, H * 0.28f, hw, 24), UiLayout.Compact ? "Left: throttle / yaw   Right: pitch / roll   Tap ARM" : "Left stick: up/down throttle (stays), left/right yaw   |   Right stick: pitch / roll   |   tap ARM", _osdS, new Color(1, 1, 1, 0.8f)); }
            else if (!_sim.Armed && !_sim.Crashed)
                Outline(new Rect(W * 0.5f - 460, H - 90, 920, 24), "W/S throttle · A/D yaw · arrows pitch/roll · Space arm · M mode · G drone · -/= cam tilt · C camera · R reset · K exit   |   pad: sticks (Mode 2), A arm", _osd, new Color(1, 1, 1, 0.75f));
            GUI.matrix = oldMatrix;
        }

        static void Outline(Rect r, string t, GUIStyle st, Color c)
        {
            var old = st.normal.textColor; st.normal.textColor = new Color(0, 0, 0, 0.85f);
            foreach (var o in new[] { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(-1, -1) }) GUI.Label(new Rect(r.x + o.x, r.y + o.y, r.width, r.height), t, st);
            st.normal.textColor = c; GUI.Label(r, t, st); st.normal.textColor = old;
        }
    }
}
