using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// The FPV camera of ShramkoGSFPV: bolted to the airframe at the body centre, tilted UP by the drone's camera uptilt, with a wide
    /// HORIZONTAL field of view (115 degrees by default; Unity wants the vertical one, converted from the aspect each frame). No smoothing:
    /// what the camera does is what the drone does, which is what makes the view feel like goggles.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class FpvCamera : MonoBehaviour
    {
        public RocketCar car;
        public FpvDriver drone;
        Camera _cam; float _near; Behaviour _rig;

        void Awake() { _cam = GetComponent<Camera>(); }

        void OnEnable()
        {
            if (_cam == null) _cam = GetComponent<Camera>();
            _near = _cam.nearClipPlane; _cam.nearClipPlane = 0.1f;            // the world camera's 0.5 m near plane would slice the ground at drone height
            _rig = GetComponent<CamRigEnd>(); if (_rig != null) _rig.enabled = false;   // the Camera menu's offsets are for the chase view
        }

        void OnDisable()
        {
            if (_cam != null) _cam.nearClipPlane = _near;
            if (_rig != null) _rig.enabled = true;
        }

        void LateUpdate()
        {
            if (car == null || car.visual == null || drone == null || !drone.Active) return;
            var rot = car.visual.rotation * Quaternion.Euler(-drone.UptiltDeg, 0f, 0f);
            transform.SetPositionAndRotation(car.visual.position, rot);
            float h = drone.FovDeg * Mathf.Deg2Rad;
            float v = 2f * Mathf.Atan(Mathf.Tan(h * 0.5f) / Mathf.Max(0.3f, _cam.aspect));
            _cam.fieldOfView = Mathf.Clamp(v * Mathf.Rad2Deg, 20f, 150f);
        }
    }
}
