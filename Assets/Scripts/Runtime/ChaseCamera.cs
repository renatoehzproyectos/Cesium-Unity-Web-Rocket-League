using UnityEngine;

namespace RocketWorld
{
    /// <summary>Smoothed chase camera (port of RocketWorld's camera.js) with real collision against the tile colliders.</summary>
    [DefaultExecutionOrder(100)]
    public class ChaseCamera : MonoBehaviour
    {
        public RocketCar car;
        Vector3 _heading = Vector3.forward, _lookOffset;
        Quaternion _rot = Quaternion.identity;                  // full-axis mode: smoothed vehicle orientation (yaw + pitch + roll)
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
            bool grounded = car.CurrentPhase == RocketCar.Phase.Running && (car.State.isOnGround || car.InBoat);   // a boat keeps the heading-follow camera

            var vel = car.UnityVelocity; var hv = new Vector3(vel.x, 0, vel.z);
            var dir = Vector3.ProjectOnPlane(carFwd, Vector3.up);
            if (!grounded && hv.magnitude > 6f) dir = hv;
            if (dir.sqrMagnitude < 1e-4f) dir = _heading;
            dir.Normalize();
            if (!_ready) _heading = dir;
            _heading = Vector3.Lerp(_heading, dir, CamTuning.Blend(grounded ? 3.6f : 2f, dt)).normalized;   // Camera menu: follow delay stretches every smoothing term

            float sp = Mathf.Clamp01(car.SpeedMS / 46f);   // (plane speeds exceed this: the clamp keeps the car-tuned FOV / distance bounded)
            bool plane = car.InPlane || car.InVehicle;                                      // the airplane is bigger and faster: sit further back and higher
            bool veh = car.InVehicle; var vs = veh ? car.vehicle.Spec : null;
            float back = veh ? vs.camBack : plane ? 17f : 7.6f + sp * 2.2f, upD = veh ? vs.camUp : plane ? 4.8f : 2.9f + sp * 0.5f, ahead = veh ? vs.camLook : plane ? 12f : 5f;
            bool full = CamTuning.fullAxis;                                  // Camera menu: follow the vehicle's roll as well (default: yaw + pitch only)
            var camUp = Vector3.up;
            Vector3 want;
            if (full)
            {
                var vr = car.visual != null ? car.visual.rotation : car.transform.rotation;
                if (!_ready) _rot = vr;
                _rot = Quaternion.Slerp(_rot, vr, CamTuning.Blend(5f, dt));
                camUp = _rot * Vector3.up;
                want = carPos + _rot * new Vector3(0f, upD, -back);
            }
            else { want = carPos - _heading * back; want.y = carPos.y + upD; }   // pulls back a little with speed
            if (Physics.Linecast(carPos + Vector3.up * 0.8f, want, out var hit, PhysicsSetup.WorldMask, QueryTriggerInteraction.Ignore))
                want = hit.point + hit.normal * 0.4f;
            var tgt = full ? carPos + _rot * new Vector3(0f, 1f, ahead) : carPos + _heading * ahead + Vector3.up;
            var pos = transform.position;                                  // camera has a CesiumGlobeAnchor: valid across origin shifts
            if (!_ready) { pos = want; _lookOffset = tgt - carPos; _ready = true; }
            pos = Vector3.Lerp(pos, want, CamTuning.Blend(6.5f, dt));
            _lookOffset = Vector3.Lerp(_lookOffset, tgt - carPos, CamTuning.Blend(12f, dt));
            if (Physics.Raycast(pos + Vector3.up * 50f, Vector3.down, out var gh, 60f, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore) && pos.y < gh.point.y + 0.8f)
                pos.y = gh.point.y + 0.8f;

            transform.SetPositionAndRotation(pos, Quaternion.LookRotation((carPos + _lookOffset) - pos, camUp));
            float f = 72f + Mathf.SmoothStep(0f, 1f, Mathf.Min(car.SpeedMS / 46f, 1f)) * 16f + car.HyperT * 18f;   // Hyper Boost widens the view
            _fov += (f - _fov) * Mathf.Min(1f, dt * 2.5f);
            if (_cam != null) _cam.fieldOfView = _fov;
        }
    }
}
