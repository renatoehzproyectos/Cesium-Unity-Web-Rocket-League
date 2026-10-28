using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace RocketWorld
{
    /// <summary>
    /// Keyboard / gamepad / on-screen touch controls to RocketSim controls (steer &gt; 0 = right, yaw &gt; 0 = nose right, pitch &lt; 0 = nose down,
    /// roll &gt; 0 = roll right). Touch zones are in normalized screen coordinates (origin bottom-left) and drawn by DebugHud.
    /// </summary>
    public class CarInput : MonoBehaviour
    {
        public struct Controls
        {
            public float throttle, steer, pitch, yaw, roll;
            public bool jump, boost, handbrake;
            /// <summary>Absolute throttle lever from the touch slider (only valid when <see cref="leverSet"/>: a finger is on it).</summary>
            public bool leverSet; public float lever;
            /// <summary>Vertical throttle -1..1: lift buttons / collective slider (F-35B VTOL, helicopter, DeLorean hover).</summary>
            public float vert;
            /// <summary>Hyper Boost: +500 % speed (key held, or the touch button latched on).</summary>
            public bool hyper;
            /// <summary>Wheel brake button (touch).</summary>
            public bool wheelBrake;
        }
        public Controls controls;
        public bool CameraTogglePressed { get; private set; }
        public bool ResetPressed { get; private set; }
        public bool BallPressed { get; private set; }
        public bool PlanePressed { get; private set; }
        public bool VehiclePressed { get; private set; }
        public bool ModePressed { get; private set; }
        public bool GearPressed { get; private set; }
        public bool FlapsPressed { get; private set; }
        public bool SettingsPressed { get; private set; }
        /// <summary>True when the settings/menu press came from the gamepad (then the HUD opens the gamepad-navigable controls screen).</summary>
        public bool MenuFromPad { get; private set; }
        public bool TouchControlsActive;

        /// <summary>Which widget set is on screen; set by the game each frame from the current vehicle.</summary>
        public static TouchProfile Profile = TouchProfile.Car;
        public static TouchWidget[] Widgets => TouchLayout.For(Profile);

        // Per-widget state for the HUD (indices match Widgets).
        readonly bool[] _held = new bool[16], _prevHeld = new bool[16], _latched = new bool[16], _touched = new bool[16];
        readonly float[] _slider = new float[16];
        TouchProfile _lastProfile = (TouchProfile)(-1);
        public bool WidgetOn(int i) => i >= 0 && i < 16 && (_held[i] || _latched[i]);
        public bool WidgetTouched(int i) => i >= 0 && i < 16 && _touched[i];
        public float WidgetValue(int i) => i >= 0 && i < 16 ? _slider[i] : 0f;

        /// <summary>Analog stick area in pixels (origin bottom-left): a square at the lower left.</summary>
        public static Rect StickRect { get { float sz = Screen.height * 0.46f, m = Screen.height * 0.04f; return new Rect(m, m, sz, sz); } }
        /// <summary>Current stick deflection (x: right+, y: up+), -1..1, for drawing the knob.</summary>
        public Vector2 StickValue { get; private set; }
        readonly List<Vector2> _touches = new List<Vector2>(10);

        /// <summary>Rebindable keyboard / gamepad map (edited by the controls panel in <see cref="DebugHud"/>).</summary>
        public InputBindings bindings;
        /// <summary>Set by the HUD while a menu that uses the keyboard / gamepad is open: the car gets no input and no hot-keys fire.</summary>
        public bool Blocked;
        Edge _eCam, _eReset, _eMenu, _eBall, _ePlane, _eVehicle, _eMode, _eGear, _eFlaps;

        float Axis(CarAction a)
        {
            var b = bindings.Get(a);
            return Mathf.Max(InputDevices.KeyValue(b.key0), InputDevices.KeyValue(b.key1), InputDevices.PadValue(b.pad));
        }

        void Awake() { bindings = InputBindings.Load(); }

        void Update()
        {
            CameraTogglePressed = ResetPressed = SettingsPressed = BallPressed = PlanePressed = VehiclePressed = ModePressed = GearPressed = FlapsPressed = false;
            // Edges are tracked even while blocked so a held button does not "fire" the moment a menu closes.
            bool cam = _eCam.Update(Axis(CarAction.CameraToggle) > 0.5f);
            bool reset = _eReset.Update(Axis(CarAction.Reset) > 0.5f);
            bool ball = _eBall.Update(Axis(CarAction.Ball) > 0.5f);
            bool plane = _ePlane.Update(Axis(CarAction.Plane) > 0.5f);
            bool vehicle = _eVehicle.Update(Axis(CarAction.Vehicle) > 0.5f);
            bool mode = _eMode.Update(Axis(CarAction.VehicleMode) > 0.5f);
            bool gear = _eGear.Update(Axis(CarAction.Gear) > 0.5f);
            bool flaps = _eFlaps.Update(Axis(CarAction.Flaps) > 0.5f);
            bool menuKey = InputDevices.KeyValue("Escape") > 0f || InputDevices.KeyValue("Tab") > 0f;
            bool menuPad = InputDevices.PadValue(bindings.GetPad(CarAction.Menu)) > 0.5f;
            bool menu = _eMenu.Update(menuKey || menuPad);
            MenuFromPad = false;
            _touches.Clear();
#if ENABLE_INPUT_SYSTEM
            var ts = Touchscreen.current;
            if (ts != null) foreach (var t in ts.touches) if (t.press.isPressed) _touches.Add(t.position.ReadValue());
#elif ENABLE_LEGACY_INPUT_MANAGER
            for (int i = 0; i < Input.touchCount; i++) _touches.Add(Input.GetTouch(i).position);
#endif
            var ws = Widgets;
            if (Profile != _lastProfile)                                   // new vehicle: clear latched buttons and slider memory
            {
                _lastProfile = Profile;
                for (int i = 0; i < _latched.Length; i++) { _latched[i] = false; _held[i] = false; _prevHeld[i] = false; _slider[i] = 0f; _touched[i] = false; }
            }
            if (Blocked)
            {
                StickValue = Vector2.zero;
                controls = default;
                for (int i = 0; i < _held.Length; i++) { _held[i] = false; _touched[i] = false; }
                return;
            }
            BallPressed = ball; PlanePressed = plane; VehiclePressed = vehicle; ModePressed = mode; GearPressed = gear; FlapsPressed = flaps;
            CameraTogglePressed = cam; ResetPressed = reset; SettingsPressed = menu; MenuFromPad = menu && menuPad && !menuKey;

            float th = Axis(CarAction.Accelerate) - Axis(CarAction.Brake);
            float st = Axis(CarAction.SteerRight) - Axis(CarAction.SteerLeft);
            float roll = Axis(CarAction.RollRight) - Axis(CarAction.RollLeft);
            bool jump = Axis(CarAction.Jump) > 0.5f, boost = Axis(CarAction.Boost) > 0.5f, hb = Axis(CarAction.Handbrake) > 0.5f;
            bool hyper = Axis(CarAction.HyperBoost) > 0.5f, wheelBrake = false, leverSet = false;
            float lever = 0f, vert = 0f;
            // Left stick up/down also pitches the car in the air (up = nose down, like Rocket League); it does nothing on the ground.
            float stickPitch = InputDevices.LeftStick().y;
            StickValue = Vector2.zero;
            if (TouchControlsActive)
            {
                // One analog stick: up = throttle / nose down, down = brake / nose up, left/right = steer (ground) and yaw / roll (air).
                var sr = StickRect; Vector2 best = Vector2.zero;
                foreach (var p in _touches)
                {
                    if (!sr.Contains(p)) continue;
                    var d = new Vector2((p.x - sr.center.x) / (sr.width * 0.5f), (p.y - sr.center.y) / (sr.height * 0.5f));
                    if (d.sqrMagnitude > best.sqrMagnitude) best = d;
                }
                if (best.sqrMagnitude > 1f) best.Normalize();
                const float dead = 0.12f;
                float sx = Mathf.Abs(best.x) < dead ? 0f : Mathf.Sign(best.x) * (Mathf.Abs(best.x) - dead) / (1f - dead);
                float sy = Mathf.Abs(best.y) < dead ? 0f : Mathf.Sign(best.y) * (Mathf.Abs(best.y) - dead) / (1f - dead);
                StickValue = best;
                st += sx; th += sy;

                for (int i = 0; i < ws.Length && i < 16; i++)
                {
                    var w = ws[i]; bool held = false; Vector2 pos = default;
                    foreach (var p in _touches) { var n = new Vector2(p.x / Screen.width, p.y / Screen.height); if (w.zone.Contains(n)) { held = true; pos = n; } }
                    _touched[i] = held;
                    bool edge = held && !_prevHeld[i]; _prevHeld[i] = held;
                    switch (w.kind)
                    {
                        case WidgetKind.Hold: _held[i] = held; break;
                        case WidgetKind.Tap: _held[i] = held; if (edge) FireTap(w.id); break;
                        case WidgetKind.Latch: if (edge) _latched[i] = !_latched[i]; _held[i] = false; break;
                        case WidgetKind.SliderSticky: if (held) _slider[i] = Mathf.Clamp01((pos.y - w.zone.yMin) / w.zone.height); break;
                        case WidgetKind.SliderSpring: _slider[i] = held ? Mathf.Clamp((pos.y - w.zone.center.y) / (w.zone.height * 0.5f), -1f, 1f) : 0f; break;
                        case WidgetKind.SliderH: _slider[i] = held ? Mathf.Clamp((pos.x - w.zone.center.x) / (w.zone.width * 0.5f), -1f, 1f) : 0f; break;
                    }
                    bool on = _held[i] || _latched[i];
                    switch (w.id)
                    {
                        case "jump": jump |= on; break;
                        case "boost": boost |= on; break;
                        case "brake": wheelBrake |= on; break;
                        case "rollL": roll -= on ? 1f : 0f; break;
                        case "rollR": roll += on ? 1f : 0f; break;
                        case "hyper": hyper |= on; break;
                        case "up": vert += on ? 1f : 0f; break;
                        case "down": vert -= on ? 1f : 0f; break;
                        case "throttle": if (held) { leverSet = true; lever = _slider[i]; } break;
                        case "vert": vert += _slider[i]; break;
                        case "rudder": roll += _slider[i]; break;
                    }
                }
            }
            else for (int i = 0; i < _held.Length; i++) { _held[i] = false; _touched[i] = false; }
            th = Mathf.Clamp(th, -1, 1); st = Mathf.Clamp(st, -1, 1);
            controls = new Controls
            {
                throttle = th, steer = st, yaw = st, pitch = -Mathf.Clamp(th + stickPitch, -1, 1), roll = Mathf.Clamp(roll, -1, 1), jump = jump, boost = boost, handbrake = hb,
                leverSet = leverSet, lever = lever, vert = Mathf.Clamp(vert, -1, 1), hyper = hyper, wheelBrake = wheelBrake,
            };
        }

        void FireTap(string id)
        {
            switch (id) { case "gear": GearPressed = true; break; case "mode": ModePressed = true; break; case "flaps": FlapsPressed = true; break; }
        }

        public static bool OverTouchZone(Vector2 screenPos)
        {
            var n = new Vector2(screenPos.x / Screen.width, screenPos.y / Screen.height);
            foreach (var w in Widgets) if (w.zone.Contains(n)) return true;
            return StickRect.Contains(screenPos);
        }
    }
}
