using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace RocketWorld
{
    /// <summary>
    /// Makes Cesium load every tile within a radius around the car at full detail, whether or not the player is looking at it.
    /// Four invisible cameras (N/E/S/W, 90 degrees each, far plane = radius) sit on the car and are registered as additional
    /// Cesium tile-selection cameras. They render nothing (culling mask 0, tiny cost) - Cesium only reads their frustum.
    /// The radius is read every frame, so the settings slider takes effect immediately.
    /// </summary>
    public class StreamingCameras : MonoBehaviour
    {
        public GameRoot root;
        public Transform follow;

        Camera[] _cams;
        RenderTexture _rt;
        IList _list;
        float _appliedRadius = -1f;

        void Start()
        {
            // Large virtual viewport => same pixels-per-degree as the main camera, so SSE picks the same (sharp) detail level.
            int px = DeviceProfile.IsMobile ? 1024 : 1536;
            _rt = new RenderTexture(px, px, 0, RenderTextureFormat.ARGB32) { name = "RW_StreamRT", antiAliasing = 1 };
            _cams = new Camera[4];
            for (int i = 0; i < 4; i++)
            {
                var go = new GameObject("StreamCam" + i);
                go.transform.SetParent(transform, false);
                go.transform.rotation = Quaternion.Euler(0f, 90f * i, 0f);
                var c = go.AddComponent<Camera>();
                c.fieldOfView = 90f; c.aspect = 1f;
                c.nearClipPlane = 1f;
                c.cullingMask = 0;
                c.clearFlags = CameraClearFlags.Nothing;
                c.depth = -100;
                c.allowHDR = false; c.allowMSAA = false;
                c.targetTexture = _rt;
                c.useOcclusionCulling = false;
                var d = c.GetUniversalAdditionalCameraData();
                if (d != null) { d.renderShadows = false; d.renderPostProcessing = false; d.antialiasing = AntialiasingMode.None; d.requiresColorOption = CameraOverrideOption.Off; d.requiresDepthOption = CameraOverrideOption.Off; }
                _cams[i] = c;
            }
            if (!Register()) { Debug.LogWarning("[RocketWorld] CesiumCameraManager not found: radius loading disabled"); enabled = false; foreach (var c in _cams) c.enabled = false; }
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
            float r = Mathf.Max(50f, root.EffectiveRadius);
            if (!Mathf.Approximately(r, _appliedRadius))
            {
                _appliedRadius = r;
                foreach (var c in _cams) c.farClipPlane = r;
            }
        }

        void OnDestroy()
        {
            if (_list != null && _cams != null) foreach (var c in _cams) if (c != null) _list.Remove(c);
            if (_rt != null) _rt.Release();
        }
    }
}
