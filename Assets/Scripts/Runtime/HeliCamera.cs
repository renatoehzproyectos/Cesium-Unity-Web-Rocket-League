using RocketWorld.Sim;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace RocketWorld
{
    /// <summary>
    /// Helicopter chase camera: hovers high above and a little behind the car and looks steeply down along its heading (street canyons stay
    /// centred, buildings lean in from both sides). The heading is followed slowly, so it feels like a helicopter tracking the car. Height and
    /// distance grow with speed (<see cref="HeliCamPolicy"/>); mouse wheel, pinch or +/- change the zoom. If a building would be in the way it climbs
    /// instead of pulling in, and it never dips below the roofs / terrain under it.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class HeliCamera : MonoBehaviour
    {
        public RocketCar car;
        public System.Func<Vector2, bool> pointerBlocked;   // true when the pointer is over HUD (so a click there does not zoom)
        public float zoom = 1f;                              // 0.5 .. 2.5, scales height and distance
        const float GroundClearance = 4f;                    // m kept between the camera and the roofs / terrain directly under it

        Vector3 _heading = Vector3.forward, _lookOffset;
        bool _ready;
        float _fov = 70f, _pinchPrev;
        Camera _cam;

        void Awake() { _cam = GetComponent<Camera>(); }
        void OnEnable() { _ready = false; }

        void LateUpdate()
        {
            if (car == null) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            ReadZoom(dt);

            var carPos = car.visual != null ? car.visual.position : car.transform.position;
            var carFwd = car.visual != null ? car.visual.forward : car.transform.forward;
            bool grounded = car.CurrentPhase == RocketCar.Phase.Running && (car.State.isOnGround || car.InBoat);
            bool plane = car.InPlane || car.InVehicle;
            float speed = car.SpeedMS;

            var vel = car.UnityVelocity; var hv = new Vector3(vel.x, 0, vel.z);
            var dir = Vector3.ProjectOnPlane(carFwd, Vector3.up);
            if (!grounded && hv.magnitude > 6f) dir = hv;                       // airborne: follow where it travels, not where it points
            if (dir.sqrMagnitude < 1e-4f) dir = _heading;
            dir.Normalize();
            if (!_ready) _heading = dir;
            _heading = Vector3.Lerp(_heading, dir, 1f - Mathf.Exp(-dt * (grounded ? 2.2f : 1.4f))).normalized;   // slower than the chase camera

            float h = HeliCamPolicy.Height(speed, plane, zoom), back = HeliCamPolicy.Back(speed, plane, zoom);
            var want = carPos - _heading * back + Vector3.up * h;

            // A building between the car and the camera: climb (up to +100 %) until the line is clear instead of dragging the camera in.
            Vector3 from = carPos + Vector3.up * 1.5f;
            for (int k = 0; k < 4; k++)
            {
                if (!Physics.Linecast(from, want, PhysicsSetup.WorldMask, QueryTriggerInteraction.Ignore)) break;
                want.y += h * 0.33f;
            }

            var tgt = carPos + _heading * HeliCamPolicy.LookAhead(speed, plane);
            var pos = transform.position;                                        // the camera has a CesiumGlobeAnchor: valid across origin shifts
            if (!_ready) { pos = want; _lookOffset = tgt - carPos; _ready = true; }
            pos = Vector3.Lerp(pos, want, 1f - Mathf.Exp(-dt * 4f));
            _lookOffset = Vector3.Lerp(_lookOffset, tgt - carPos, 1f - Mathf.Exp(-dt * 8f));

            // Stay above the roofs / ground right under the camera.
            if (Physics.Raycast(pos + Vector3.up * 40f, Vector3.down, out var gh, 160f, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore)
                && pos.y < gh.point.y + GroundClearance)
                pos.y = gh.point.y + GroundClearance;

            var look = (carPos + _lookOffset) - pos;
            if (look.sqrMagnitude < 1e-4f) look = Vector3.down + _heading * 0.1f;
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(look, Vector3.up));
            _fov += (HeliCamPolicy.Fov(speed) - _fov) * Mathf.Min(1f, dt * 2.5f);
            if (_cam != null) _cam.fieldOfView = _fov;
        }

        bool Blocked(Vector2 screenPos) => pointerBlocked != null && pointerBlocked(screenPos);

        /// <summary>Mouse wheel, two-finger pinch or +/- keys.</summary>
        void ReadZoom(float dt)
        {
            float z = 0f;
#if ENABLE_INPUT_SYSTEM
            var ts = Touchscreen.current;
            int n = 0; Vector2 p0 = default, p1 = default;
            if (ts != null)
                foreach (var t in ts.touches)
                {
                    if (!t.press.isPressed) continue;
                    var p = t.position.ReadValue();
                    if (n == 0) p0 = p; else if (n == 1) p1 = p;
                    n++;
                }
            if (n >= 2) { float d = Vector2.Distance(p0, p1); if (_pinchPrev > 0f) z += (d - _pinchPrev) * 0.01f; _pinchPrev = d; }
            else _pinchPrev = 0f;
            var m = Mouse.current;
            if (m != null && !Blocked(m.position.ReadValue())) { float s = m.scroll.ReadValue().y; if (Mathf.Abs(s) > 0.01f) z += s * 0.0012f; }
            var k = Keyboard.current;
            if (k != null) z += ((k.equalsKey.isPressed || k.numpadPlusKey.isPressed ? 1f : 0f) - (k.minusKey.isPressed || k.numpadMinusKey.isPressed ? 1f : 0f)) * 1.2f * dt;
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.touchCount >= 2) { float d = Vector2.Distance(Input.GetTouch(0).position, Input.GetTouch(1).position); if (_pinchPrev > 0f) z += (d - _pinchPrev) * 0.01f; _pinchPrev = d; }
            else _pinchPrev = 0f;
            if (!Blocked(Input.mousePosition) && Mathf.Abs(Input.mouseScrollDelta.y) > 0.01f) z += Input.mouseScrollDelta.y * 0.1f;
            z += ((Input.GetKey(KeyCode.Equals) || Input.GetKey(KeyCode.KeypadPlus) ? 1f : 0f) - (Input.GetKey(KeyCode.Minus) || Input.GetKey(KeyCode.KeypadMinus) ? 1f : 0f)) * 1.2f * dt;
#endif
            if (z != 0f) zoom = HeliCamPolicy.ClampZoom(zoom * Mathf.Exp(-z));   // wheel up / pinch out = closer, like the orbit camera
        }
    }
}
