using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace RocketWorld
{
    /// <summary>Orbit/zoom camera around the car: mouse drag + wheel, one-finger drag + pinch on touch, arrow keys, +/-.</summary>
    public class OrbitCamera : MonoBehaviour
    {
        public Transform target;
        public float yaw = 200f, pitch = 14f, distance = 7.5f;
        public float minDistance = 2.5f, maxDistance = 80f;
        public bool autoRotate = true;
        public float autoRotateSpeed = 6f;       // deg/s once idle
        public float idleBeforeAutoRotate = 3f;
        public Vector3 lookOffset = new Vector3(0f, 0.8f, 0f);
        public bool externalControl;             // benchmark drives yaw/pitch/distance itself
        public System.Func<Vector2, bool> pointerBlocked;   // true when the pointer is over HUD

        float _idle;
        float _pinchPrev;
        Camera _cam;

        void Awake() { _cam = GetComponent<Camera>(); }

        void LateUpdate()
        {
            if (target == null) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            Vector2 drag = Vector2.zero; float zoom = 0f; bool active = false;
            if (!externalControl) ReadInput(dt, out drag, out zoom, out active);

            if (active) _idle = 0f; else _idle += dt;
            yaw += drag.x * 0.25f;
            pitch = Mathf.Clamp(pitch - drag.y * 0.25f, -4f, 85f);
            distance = Mathf.Clamp(externalControl ? distance : distance * Mathf.Exp(-zoom), minDistance, maxDistance);
            if (!externalControl && autoRotate && _idle > idleBeforeAutoRotate) yaw += autoRotateSpeed * dt;
            yaw = Mathf.Repeat(yaw, 360f);

            var focus = target.position + lookOffset;
            var rot = Quaternion.Euler(pitch, yaw, 0f);
            var pos = focus - rot * Vector3.forward * distance;
            if (pos.y < target.position.y + 0.25f) pos.y = target.position.y + 0.25f;   // never dip below the car's wheels
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(focus - pos, Vector3.up));
            if (_cam != null) _cam.fieldOfView = 55f;
        }

        void ReadInput(float dt, out Vector2 drag, out float zoom, out bool active)
        {
            drag = Vector2.zero; zoom = 0f; active = false;
#if ENABLE_INPUT_SYSTEM
            var ts = Touchscreen.current;
            int n = 0; Vector2 p0 = default, p1 = default, d0 = default;
            if (ts != null)
                foreach (var t in ts.touches)
                {
                    if (!t.press.isPressed) continue;
                    var p = t.position.ReadValue();
                    if (n == 0) { p0 = p; d0 = t.delta.ReadValue(); } else if (n == 1) p1 = p;
                    n++;
                }
            if (n >= 2)
            {
                float d = Vector2.Distance(p0, p1);
                if (_pinchPrev > 0f) zoom = (d - _pinchPrev) * 0.01f;
                _pinchPrev = d; active = true; return;
            }
            _pinchPrev = 0f;
            if (n == 1 && !Blocked(p0)) { drag = d0; active = true; }
            var m = Mouse.current;
            if (m != null)
            {
                var mp = m.position.ReadValue();
                if (m.leftButton.isPressed && !Blocked(mp)) { drag = m.delta.ReadValue(); active = true; }
                if (!Blocked(mp)) { float s = m.scroll.ReadValue().y; if (Mathf.Abs(s) > 0.01f) { zoom += s * 0.0012f; active = true; } }
            }
            var k = Keyboard.current;
            if (k != null)
            {
                float kx = (k.rightArrowKey.isPressed ? 1 : 0) - (k.leftArrowKey.isPressed ? 1 : 0);
                float ky = (k.upArrowKey.isPressed ? 1 : 0) - (k.downArrowKey.isPressed ? 1 : 0);
                float kz = (k.equalsKey.isPressed || k.numpadPlusKey.isPressed ? 1 : 0) - (k.minusKey.isPressed || k.numpadMinusKey.isPressed ? 1 : 0);
                if (kx != 0 || ky != 0 || kz != 0) { drag += new Vector2(kx, ky) * 240f * dt; zoom += kz * 1.2f * dt; active = true; }
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.touchCount >= 2)
            {
                float d = Vector2.Distance(Input.GetTouch(0).position, Input.GetTouch(1).position);
                if (_pinchPrev > 0f) zoom = (d - _pinchPrev) * 0.01f;
                _pinchPrev = d; active = true; return;
            }
            _pinchPrev = 0f;
            if (Input.touchCount == 1 && !Blocked(Input.GetTouch(0).position)) { drag = Input.GetTouch(0).deltaPosition; active = true; }
            if (Input.GetMouseButton(0) && !Blocked(Input.mousePosition)) { drag = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 8f; active = true; }
            if (!Blocked(Input.mousePosition) && Mathf.Abs(Input.mouseScrollDelta.y) > 0.01f) { zoom += Input.mouseScrollDelta.y * 0.1f; active = true; }
            float kx = (Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKey(KeyCode.LeftArrow) ? 1 : 0);
            float ky = (Input.GetKey(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKey(KeyCode.DownArrow) ? 1 : 0);
            float kz = (Input.GetKey(KeyCode.Equals) || Input.GetKey(KeyCode.KeypadPlus) ? 1 : 0) - (Input.GetKey(KeyCode.Minus) || Input.GetKey(KeyCode.KeypadMinus) ? 1 : 0);
            if (kx != 0 || ky != 0 || kz != 0) { drag += new Vector2(kx, ky) * 240f * dt; zoom += kz * 1.2f * dt; active = true; }
#endif
        }

        bool Blocked(Vector2 screenPos) => pointerBlocked != null && pointerBlocked(screenPos);

        public static bool SettingsKeyPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            return k != null && (k.escapeKey.wasPressedThisFrame || k.tabKey.wasPressedThisFrame);
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Tab);
#else
            return false;
#endif
        }
    }
}
