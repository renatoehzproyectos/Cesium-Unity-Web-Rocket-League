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
        public bool SettingsPressed { get; private set; }
        public bool TouchControlsActive;

        public static readonly (string label, Rect zone)[] TouchZones =
        {
            ("◀", new Rect(0.02f, 0.05f, 0.11f, 0.22f)), ("▶", new Rect(0.14f, 0.05f, 0.11f, 0.22f)),
            ("GAS", new Rect(0.82f, 0.24f, 0.15f, 0.20f)), ("BRK", new Rect(0.82f, 0.05f, 0.15f, 0.17f)),
            ("JUMP", new Rect(0.64f, 0.05f, 0.16f, 0.20f)), ("BOOST", new Rect(0.64f, 0.27f, 0.16f, 0.17f)),
        };
        readonly List<Vector2> _touches = new List<Vector2>(10);

        void Update()
        {
            CameraTogglePressed = ResetPressed = SettingsPressed = false;
            float th = 0, st = 0, roll = 0; bool jump = false, boost = false, hb = false;
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k != null)
            {
                th += (k.wKey.isPressed || k.upArrowKey.isPressed ? 1 : 0) - (k.sKey.isPressed || k.downArrowKey.isPressed ? 1 : 0);
                st += (k.dKey.isPressed || k.rightArrowKey.isPressed ? 1 : 0) - (k.aKey.isPressed || k.leftArrowKey.isPressed ? 1 : 0);
                roll += (k.eKey.isPressed ? 1 : 0) - (k.qKey.isPressed ? 1 : 0);
                jump |= k.spaceKey.isPressed;
                boost |= k.leftShiftKey.isPressed || k.rightShiftKey.isPressed;
                hb |= k.leftCtrlKey.isPressed || k.rightCtrlKey.isPressed || k.xKey.isPressed;
                CameraTogglePressed |= k.cKey.wasPressedThisFrame;
                ResetPressed |= k.rKey.wasPressedThisFrame;
                SettingsPressed |= k.escapeKey.wasPressedThisFrame || k.tabKey.wasPressedThisFrame;
            }
            var g = Gamepad.current;
            if (g != null)
            {
                th += g.rightTrigger.ReadValue() - g.leftTrigger.ReadValue();
                st += g.leftStick.x.ReadValue();
                roll += (g.rightShoulder.isPressed ? 1 : 0) - (g.leftShoulder.isPressed ? 1 : 0);
                jump |= g.buttonSouth.isPressed; boost |= g.buttonEast.isPressed; hb |= g.buttonWest.isPressed;
                CameraTogglePressed |= g.buttonNorth.wasPressedThisFrame;
                ResetPressed |= g.selectButton.wasPressedThisFrame;
            }
            _touches.Clear();
            var ts = Touchscreen.current;
            if (ts != null) foreach (var t in ts.touches) if (t.press.isPressed) _touches.Add(t.position.ReadValue());
#elif ENABLE_LEGACY_INPUT_MANAGER
            th = (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1 : 0);
            st = (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1 : 0);
            roll = (Input.GetKey(KeyCode.E) ? 1 : 0) - (Input.GetKey(KeyCode.Q) ? 1 : 0);
            jump = Input.GetKey(KeyCode.Space);
            boost = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            hb = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.X);
            CameraTogglePressed = Input.GetKeyDown(KeyCode.C);
            ResetPressed = Input.GetKeyDown(KeyCode.R);
            SettingsPressed = Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Tab);
            _touches.Clear();
            for (int i = 0; i < Input.touchCount; i++) _touches.Add(Input.GetTouch(i).position);
#endif
            if (TouchControlsActive && _touches.Count > 0)
            {
                bool Held(int zone)
                {
                    var z = TouchZones[zone].zone;
                    foreach (var p in _touches) { float x = p.x / Screen.width, y = p.y / Screen.height; if (z.Contains(new Vector2(x, y))) return true; }
                    return false;
                }
                st += (Held(1) ? 1 : 0) - (Held(0) ? 1 : 0);
                th += (Held(2) ? 1 : 0) - (Held(3) ? 1 : 0);
                jump |= Held(4); boost |= Held(5);
            }
            th = Mathf.Clamp(th, -1, 1); st = Mathf.Clamp(st, -1, 1);
            controls = new Controls { throttle = th, steer = st, yaw = st, pitch = -th, roll = Mathf.Clamp(roll, -1, 1), jump = jump, boost = boost, handbrake = hb };
        }

        public static bool OverTouchZone(Vector2 screenPos)
        {
            var n = new Vector2(screenPos.x / Screen.width, screenPos.y / Screen.height);
            foreach (var z in TouchZones) if (z.zone.Contains(n)) return true;
            return false;
        }
    }
}
