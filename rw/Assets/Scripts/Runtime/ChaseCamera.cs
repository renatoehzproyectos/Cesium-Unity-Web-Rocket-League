using UnityEngine;

namespace RocketWorld
{
    /// <summary>Smoothed chase camera (port of RocketWorld's camera.js) with real collision against the tile colliders.</summary>
    [DefaultExecutionOrder(100)]
    public class ChaseCamera : MonoBehaviour
    {
        public RocketCar car;
        Vector3 _heading = Vector3.forward, _lookOffset;
        bool _ready;
        float _fov = 80f;
        Camera _cam;

        void Awake() { _cam = GetComponent<Camera>(); }
        void OnEnable() { _ready = false; }

        void LateUpdate()
        {
            if (car == null) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            var carPos = car.visual != null ? car.visual.position : car.transform.position;
            var carFwd = car.visual != null ? car.visual.forward : car.transform.forward;
            bool grounded = car.CurrentPhase == RocketCar.Phase.Running && car.State.isOnGround;

            var vel = car.UnityVelocity; var hv = new Vector3(vel.x, 0, vel.z);
            var dir = Vector3.ProjectOnPlane(carFwd, Vector3.up);
            if (!grounded && hv.magnitude > 6f) dir = hv;
            if (dir.sqrMagnitude < 1e-4f) dir = _heading;
            dir.Normalize();
            if (!_ready) _heading = dir;
            _heading = Vector3.Lerp(_heading, dir, 1f - Mathf.Exp(-dt * (grounded ? 5f : 2.5f))).normalized;

            var want = carPos - _heading * 7.5f; want.y = carPos.y + 2.8f;
            if (Physics.Linecast(carPos + Vector3.up * 0.8f, want, out var hit, PhysicsSetup.WorldMask, QueryTriggerInteraction.Ignore))
                want = hit.point + hit.normal * 0.4f;
            var tgt = carPos + _heading * 5f + Vector3.up;
            var pos = transform.position;                                  // camera has a CesiumGlobeAnchor: valid across origin shifts
            if (!_ready) { pos = want; _lookOffset = tgt - carPos; _ready = true; }
            pos = Vector3.Lerp(pos, want, 1f - Mathf.Exp(-dt * 8f));
            _lookOffset = Vector3.Lerp(_lookOffset, tgt - carPos, 1f - Mathf.Exp(-dt * 12f));
            if (Physics.Raycast(pos + Vector3.up * 50f, Vector3.down, out var gh, 60f, PhysicsSetup.WorldMask, QueryTriggerInteraction.Ignore) && pos.y < gh.point.y + 0.8f)
                pos.y = gh.point.y + 0.8f;

            transform.SetPositionAndRotation(pos, Quaternion.LookRotation((carPos + _lookOffset) - pos, Vector3.up));
            float f = 78f + Mathf.Min(car.SpeedMS / 46f, 1f) * 18f;
            _fov += (f - _fov) * Mathf.Min(1f, dt * 4f);
            if (_cam != null) _cam.fieldOfView = _fov;
        }
    }
}
