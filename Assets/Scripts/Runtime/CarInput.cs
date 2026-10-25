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
        public struct Controls { public float throttle, steer, pitch, yaw, roll; public bool jump, boost, handbrake; }
        public Controls controls;
        public bool CameraTogglePressed { get; private set; }
        public bool ResetPressed { get; private set; }
        public bool BallPressed { get; private set; }
        public bool PlanePressed { get; private set; }
        public bool SettingsPressed { get; private set; }
        /// <summary>True when the settings/menu press came from the gamepad (then the HUD opens the gamepad-navigable controls screen).</summary>
        public bool MenuFromPad { get; private set; }
        public bool TouchControlsActive;

        // Button zones (normalized, origin bottom-left). The analog stick is separate (StickRect, pixel-based so it stays square).
        public const int ZJump = 0, ZBoost = 1, ZRollL = 2, ZRollR = 3;
        public static readonly (string label, Rect zone)[] TouchZones =
        {
            ("JUMP", new Rect(0.80f, 0.05f, 0.17f, 0.20f)), ("BOOST", new Rect(0.80f, 0.27f, 0.17f, 0.20f)),
            ("ROLL ◀", new Rect(0.61f, 0.05f, 0.17f, 0.20f)), ("ROLL ▶", new Rect(0.61f, 0.27f, 0.17f, 0.20f)),
        };
        /// <summary>Analog stick area in pixels (origin bottom-left): a square at the lower left.</summary>
        public static Rect StickRect { get { float sz = Screen.height * 0.46f, m = Screen.height * 0.04f; return new Rect(m, m, sz, sz); } }
        /// <summary>Current stick deflection (x: right+, y: up+), -1..1, for drawing the knob.</summary>
        public Vector2 StickValue { get; private set; }
        readonly List<Vector2> _touches = new List<Vector2>(10);

        /// <summary>Rebindable keyboard / gamepad map (edited by the controls panel in <see cref="DebugHud"/>).</summary>
        public InputBindings bindings;
        /// <summary>Set by the HUD while a menu that uses the keyboard / gamepad is open: the car gets no input and no hot-keys fire.</summary>
        public bool Blocked;
        Edge _eCam, _eReset, _eMenu, _eBall, _ePlane;

        float Axis(CarAction a)
        {
            var b = bindings.Get(a);
            return Mathf.Max(InputDevices.KeyValue(b.key0), InputDevices.KeyValue(b.key1), InputDevices.PadValue(b.pad));
        }

        void Awake() { bindings = InputBindings.Load(); }

        void Update()
        {
            CameraTogglePressed = ResetPressed = SettingsPressed = BallPressed = PlanePressed = false;
            // Edges are tracked even while blocked so a held button does not "fire" the moment a menu closes.
            bool cam = _eCam.Update(Axis(CarAction.CameraToggle) > 0.5f);
            bool reset = _eReset.Update(Axis(CarAction.Reset) > 0.5f);
            bool ball = _eBall.Update(Axis(CarAction.Ball) > 0.5f);
            bool plane = _ePlane.Update(Axis(CarAction.Plane) > 0.5f);
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
            if (Blocked)
            {
                StickValue = Vector2.zero;
                controls = default;
                return;
            }
            BallPressed = ball; PlanePressed = plane;
            CameraTogglePressed = cam; ResetPressed = reset; SettingsPressed = menu; MenuFromPad = menu && menuPad && !menuKey;

            float th = Axis(CarAction.Accelerate) - Axis(CarAction.Brake);
            float st = Axis(CarAction.SteerRight) - Axis(CarAction.SteerLeft);
            float roll = Axis(CarAction.RollRight) - Axis(CarAction.RollLeft);
            bool jump = Axis(CarAction.Jump) > 0.5f, boost = Axis(CarAction.Boost) > 0.5f, hb = Axis(CarAction.Handbrake) > 0.5f;
            // Left stick up/down also pitches the car in the air (up = nose down, like Rocket League); it does nothing on the ground.
            float stickPitch = InputDevices.LeftStick().y;
            StickValue = Vector2.zero;
            if (TouchControlsActive && _touches.Count > 0)
            {
                bool Held(int zone)
                {
                    var z = TouchZones[zone].zone;
                    foreach (var p in _touches) { float x = p.x / Screen.width, y = p.y / Screen.height; if (z.Contains(new Vector2(x, y))) return true; }
                    return false;
                }
                // One analog stick: up = throttle / nose down, down = brake / nose up, left/right = steer (ground) and yaw (air).
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
                roll += (Held(ZRollR) ? 1 : 0) - (Held(ZRollL) ? 1 : 0);
                jump |= Held(ZJump); boost |= Held(ZBoost);
            }
            th = Mathf.Clamp(th, -1, 1); st = Mathf.Clamp(st, -1, 1);
            controls = new Controls { throttle = th, steer = st, yaw = st, pitch = -Mathf.Clamp(th + stickPitch, -1, 1), roll = Mathf.Clamp(roll, -1, 1), jump = jump, boost = boost, handbrake = hb };
        }

        public static bool OverTouchZone(Vector2 screenPos)
        {
            var n = new Vector2(screenPos.x / Screen.width, screenPos.y / Screen.height);
            foreach (var z in TouchZones) if (z.zone.Contains(n)) return true;
            return StickRect.Contains(screenPos);
        }
    }
}
