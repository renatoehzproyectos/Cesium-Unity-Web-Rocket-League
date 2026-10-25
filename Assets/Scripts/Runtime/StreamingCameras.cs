using System;
using System.Collections;
using System.Reflection;
using RocketWorld.Sim;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace RocketWorld
{
    /// <summary>
    /// Makes Cesium load every tile within a radius around the car at full detail, whether or not the player is looking at it.
    /// Four invisible cameras (N/E/S/W, 90 degrees each, far plane = radius) sit on the car and are registered as additional
    /// Cesium tile-selection cameras. They render nothing (culling mask 0, tiny cost) - Cesium only reads their frustum.
    /// The radius is read every frame, so the settings slider takes effect immediately.
    ///
    /// Beauty pass: a fifth "look-ahead" camera points where the car is travelling (or where the main camera looks when stopped) and reaches
    /// 350-900 m depending on speed, with a narrower field of view, so Cesium asks for finer tiles there than the main camera would. The
    /// detail is already loaded when the player gets to it, and it exists even while the main camera is swung sideways.
    /// </summary>
    public class StreamingCameras : MonoBehaviour
    {
        public GameRoot root;
        public Transform follow;

        const float AheadFov = 50f;     // narrower than the main camera (55) at a taller virtual viewport => finer detail than the main view asks for
        Camera[] _cams;
        Camera _ahead;
        Vector3 _lastPos, _vel, _dir = Vector3.forward;
        bool _haveLast;
        RenderTexture _rt;
        IList _list;
        float _appliedRadius = -1f, _appliedMult = 1f, _rtChanged;

        void Start()
        {
            // Large virtual viewport => same pixels-per-degree as the main camera, so SSE picks the same (sharp) detail level.
            _cams = new Camera[5];
            _rt = MakeRt(root.settings.distantQuality);
            for (int i = 0; i < 4; i++)
            {
                var c = NewCam("StreamCam" + i, 90f);
                c.transform.rotation = Quaternion.Euler(0f, 90f * i, 0f);
                _cams[i] = c;
            }
            _ahead = NewCam("StreamAhead", AheadFov);
            _ahead.farClipPlane = TileCachePolicy.AheadRange(0f);
            _cams[4] = _ahead;
            if (!Register()) { Debug.LogWarning("[RocketWorld] CesiumCameraManager not found: radius loading disabled"); enabled = false; foreach (var c in _cams) c.enabled = false; }
        }

        Camera NewCam(string name, float fov)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var c = go.AddComponent<Camera>();
            c.fieldOfView = fov;
            c.nearClipPlane = 1f;
            c.cullingMask = 0;
            c.clearFlags = CameraClearFlags.Nothing;
            c.depth = -100;
            c.allowHDR = false; c.allowMSAA = false;
            c.targetTexture = _rt; c.aspect = 1f;   // frustum stays square; only the virtual viewport HEIGHT (= sharpness) changes
            c.useOcclusionCulling = false;
            var d = c.GetUniversalAdditionalCameraData();
            if (d != null) { d.renderShadows = false; d.renderPostProcessing = false; d.antialiasing = AntialiasingMode.None; d.requiresColorOption = CameraOverrideOption.Off; d.requiresDepthOption = CameraOverrideOption.Off; }
            return c;
        }

        /// <summary>
        /// "Distant tile quality": Cesium's screen-space error scales with the camera's viewport height, so a taller virtual viewport
        /// makes these cameras ask for finer tiles at the same distance. The RT is narrow (cheap) and only its height grows.
        /// </summary>
        RenderTexture MakeRt(float mult)
        {
            int baseH = DeviceProfile.IsMobile ? 1024 : 1536, maxH = DeviceProfile.IsMobile ? 4096 : 8192;
            int h = Mathf.Clamp(Mathf.RoundToInt(baseH * Mathf.Max(0.25f, mult)), 256, maxH);
            _appliedMult = mult; _rtChanged = Time.unscaledTime;
            return new RenderTexture(128, h, 0, RenderTextureFormat.ARGB32) { name = "RW_StreamRT", antiAliasing = 1 };
        }

        bool Register()
        {
            try
            {
                Type t = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { t = a.GetType("CesiumForUnity.CesiumCameraManager"); if (t != null) break; }
                if (t == null) return false;
                var get = t.GetMethod("GetDefaultCameraManager", BindingFlags.Public | BindingFlags.Static);
                var mgr = get?.Invoke(null, new object[] { gameObject });
                if (mgr == null) return false;
                object list = t.GetField("additionalCameras")?.GetValue(mgr) ?? t.GetProperty("additionalCameras")?.GetValue(mgr);
                _list = list as IList;
                if (_list == null) return false;
                foreach (var c in _cams) _list.Add(c);
                return true;
            }
            catch (Exception e) { Debug.LogWarning("[RocketWorld] streaming cameras: " + e.Message); return false; }
        }

        void LateUpdate()
        {
            if (_cams == null || follow == null || root == null) return;
            transform.position = follow.position + Vector3.up * 2f;
            float dq = root.settings.distantQuality;
            if (Mathf.Abs(dq - _appliedMult) > 0.04f && Time.unscaledTime - _rtChanged > 0.6f)   // live, but not on every slider tick
            {
                var old = _rt; _rt = MakeRt(dq);
                foreach (var c in _cams) { c.targetTexture = _rt; c.aspect = 1f; }
                if (old != null) old.Release();
            }
            float r = Mathf.Max(50f, root.EffectiveRadius);
            if (!Mathf.Approximately(r, _appliedRadius))
            {
                _appliedRadius = r;
                for (int i = 0; i < 4; i++) _cams[i].farClipPlane = r;      // the four ring cameras; the look-ahead camera has its own range
            }
            UpdateAhead();
        }

        /// <summary>Aim the look-ahead camera along the (smoothed) direction of travel and size its reach by speed.</summary>
        void UpdateAhead()
        {
            if (_ahead == null) return;
            float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            Vector3 p = follow.position;
            if (_haveLast)
            {
                Vector3 v = (p - _lastPos) / dt;
                if (v.magnitude > 400f) v = Vector3.zero;                    // teleport / origin shift, not real movement
                _vel = Vector3.Lerp(_vel, v, 1f - Mathf.Exp(-dt * 4f));
            }
            _lastPos = p; _haveLast = true;
            float speed = _vel.magnitude;
            Vector3 want = speed > 3f ? _vel / speed : (root.cam != null ? root.cam.transform.forward : follow.forward);   // stopped: follow the view
            if (want.sqrMagnitude < 1e-4f) want = follow.forward;
            _dir = Vector3.Slerp(_dir, want.normalized, 1f - Mathf.Exp(-dt * 3f));
            if (_dir.sqrMagnitude < 1e-4f) _dir = want.normalized;
            Vector3 up = Mathf.Abs(_dir.y) > 0.98f ? Vector3.forward : Vector3.up;
            _ahead.transform.rotation = Quaternion.LookRotation(_dir, up);
            _ahead.farClipPlane = TileCachePolicy.AheadRange(speed);
        }

        void OnDestroy()
        {
            if (_list != null && _cams != null) foreach (var c in _cams) if (c != null) _list.Remove(c);
            if (_rt != null) _rt.Release();
        }
    }
}
