using RocketWorld.Sim;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// The camera menu's values, shared by every camera mode. They are the working values (edited live by the menu);
    /// <see cref="Save"/> stores them in this browser, <see cref="LoadSaved"/> brings the stored ones back, <see cref="ResetDefaults"/> zeroes them.
    /// </summary>
    public static class CamTuning
    {
        public static float hTurn, vTurn;                       // degrees, + = right / up
        public static float up, side, front;                    // metres in camera space, + = up / right / towards the vehicle
        public static float fov;                                // degrees added to the mode's own field of view
        public static float delay = CamTunePolicy.DefaultDelay; // follow-delay multiplier (1 = original, 0 = rigid)
        public static bool fullAxis;                            // false: follow yaw + pitch only (original); true: follow yaw, pitch and roll of the vehicle

        const string P = "rw2.camtune.";

        public static void ResetDefaults()
        {
            hTurn = vTurn = up = side = front = fov = 0f;
            delay = CamTunePolicy.DefaultDelay; fullAxis = false;
        }

        public static bool HasSaved => PlayerPrefs.HasKey(P + "saved");

        public static void Save()
        {
            PlayerPrefs.SetFloat(P + "h", hTurn); PlayerPrefs.SetFloat(P + "v", vTurn);
            PlayerPrefs.SetFloat(P + "up", up); PlayerPrefs.SetFloat(P + "side", side); PlayerPrefs.SetFloat(P + "front", front);
            PlayerPrefs.SetFloat(P + "fov", fov); PlayerPrefs.SetFloat(P + "delay", delay); PlayerPrefs.SetInt(P + "full", fullAxis ? 1 : 0);
            PlayerPrefs.SetInt(P + "saved", 1);
            PlayerPrefs.Save();
        }

        /// <summary>Loads the last saved values (defaults when nothing was saved yet).</summary>
        public static void LoadSaved()
        {
            ResetDefaults();
            if (!HasSaved) return;
            hTurn = CamTunePolicy.Clamp(PlayerPrefs.GetFloat(P + "h", 0f), CamTunePolicy.MaxTurnH);
            vTurn = CamTunePolicy.Clamp(PlayerPrefs.GetFloat(P + "v", 0f), CamTunePolicy.MaxTurnV);
            up = CamTunePolicy.Clamp(PlayerPrefs.GetFloat(P + "up", 0f), CamTunePolicy.MaxMove);
            side = CamTunePolicy.Clamp(PlayerPrefs.GetFloat(P + "side", 0f), CamTunePolicy.MaxMove);
            front = CamTunePolicy.Clamp(PlayerPrefs.GetFloat(P + "front", 0f), CamTunePolicy.MaxMove);
            fov = CamTunePolicy.Clamp(PlayerPrefs.GetFloat(P + "fov", 0f), CamTunePolicy.MaxFov);
            delay = CamTunePolicy.ClampDelay(PlayerPrefs.GetFloat(P + "delay", CamTunePolicy.DefaultDelay));
            fullAxis = PlayerPrefs.GetInt(P + "full", 0) == 1;
        }

        /// <summary>Rate (1/s) of a camera smoothing term after the follow delay.</summary>
        public static float Rate(float baseRate) => CamTunePolicy.Rate(baseRate, delay);
        public static float Blend(float baseRate, float dt) => CamTunePolicy.Blend(Rate(baseRate), dt);
    }

    /// <summary>
    /// Applies the camera menu's offsets on top of whichever camera mode is active. The chase / helicopter cameras use the camera's own
    /// position as their smoothing state, so the offset is undone first thing next frame (<see cref="CamRigBegin"/>) and re-applied last (<see cref="CamRigEnd"/>);
    /// the offset therefore never feeds back into their smoothing.
    /// </summary>
    [DefaultExecutionOrder(40)]
    public class CamRigBegin : MonoBehaviour
    {
        public CamRigEnd end;
        void LateUpdate() { if (end != null) end.Undo(); }
    }

    [DefaultExecutionOrder(200)]
    public class CamRigEnd : MonoBehaviour
    {
        Camera _cam; Vector3 _posDelta; Quaternion _rotDelta = Quaternion.identity; bool _applied;
        void Awake() { _cam = GetComponent<Camera>(); }
        void OnDisable() { Undo(); }

        public void Undo()
        {
            if (!_applied) return;
            _applied = false;
            transform.position -= _posDelta;                                   // world delta: still valid after a Cesium origin shift (it only translates)
            transform.rotation = transform.rotation * Quaternion.Inverse(_rotDelta);
        }

        void LateUpdate()
        {
            if (_cam != null) _cam.fieldOfView = CamTunePolicy.Fov(_cam.fieldOfView, CamTuning.fov);   // every mode assigns its own fov each frame: no accumulation
            var rot = transform.rotation;
            _rotDelta = Quaternion.Euler(-CamTuning.vTurn, CamTuning.hTurn, 0f);
            _posDelta = rot * new Vector3(CamTuning.side, 0f, CamTuning.front) + Vector3.up * CamTuning.up;
            if (_posDelta == Vector3.zero && CamTuning.hTurn == 0f && CamTuning.vTurn == 0f) return;
            transform.position += _posDelta;
            transform.rotation = rot * _rotDelta;
            _applied = true;
        }
    }
}
