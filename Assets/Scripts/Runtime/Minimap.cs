using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace RocketWorld
{
    /// <summary>
    /// Circular minimap, top-left. No map service and no API key: a small orthographic camera looks straight down at the car and
    /// renders the tiles that are already loaded (the streaming cameras keep a full radius around the car loaded) into a tiny
    /// RenderTexture, refreshed ~15x per second. Fog is switched off and ambient light lifted only while that camera renders,
    /// so the map stays readable at night. Click / tap it to switch between "rotates with the car" and "north is up".
    /// </summary>
    [DefaultExecutionOrder(110)]
    public class Minimap : MonoBehaviour
    {
        public GameRoot root;
        public const float Size = 176f;                 // GUI units (the HUD is laid out on a 720 px high canvas)
        public const float Margin = 16f;
        public Rect Rect => new Rect(Margin, Margin, Size, Size);

        const string RotKey = "rw_minimap_rotate";
        const float Height = 600f;                      // camera height above the car; far plane covers terrain below it
        Camera _cam;
        RenderTexture _rt;
        float _next, _range = 120f;
        bool _rotate = true, _fog, _hasHeading;
        float _heading;                                 // car heading, degrees clockwise from north
        AmbientMode _amb; Color _ambCol;

        void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += OnBegin;
            RenderPipelineManager.endCameraRendering += OnEnd;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBegin;
            RenderPipelineManager.endCameraRendering -= OnEnd;
        }

        void OnDestroy() { if (_rt != null) _rt.Release(); }

        void Create()
        {
            _rotate = PlayerPrefs.GetInt(RotKey, 1) == 1;
            int res = DeviceProfile.IsMobile ? 256 : 384;
            _rt = new RenderTexture(res, res, 16, RenderTextureFormat.ARGB32) { name = "RW_MinimapRT", antiAliasing = 1, filterMode = FilterMode.Bilinear, useMipMap = false };
            var go = new GameObject("MinimapCamera");
            go.transform.SetParent(transform, false);
            _cam = go.AddComponent<Camera>();
            _cam.orthographic = true;
            _cam.orthographicSize = _range;
            _cam.nearClipPlane = 1f;
            _cam.farClipPlane = Height + 2200f;                // also reaches the ground from the cargo plane (1 km up)
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.06f, 0.09f, 0.13f, 1f);
            _cam.cullingMask = ~((1 << PhysicsSetup.CarLayer) | (1 << PhysicsSetup.PlaneLayer));   // never show the intro plane
            _cam.allowHDR = false; _cam.allowMSAA = false;
            _cam.useOcclusionCulling = false;
            _cam.depth = -50;                            // rendered before the main camera; target is a texture, so nothing hits the screen
            _cam.targetTexture = _rt;
            _cam.enabled = false;
            var d = _cam.GetUniversalAdditionalCameraData();
            if (d != null) { d.renderShadows = false; d.renderPostProcessing = false; d.antialiasing = AntialiasingMode.None; d.requiresColorOption = CameraOverrideOption.Off; d.requiresDepthOption = CameraOverrideOption.Off; }
        }

        void LateUpdate()
        {
            if (root == null || root.car == null || root.carCtl == null) return;
            if (_cam == null) Create();

            var carPos = root.carVisual != null ? root.carVisual.position : root.car.position;
            var fwd = root.carVisual != null ? root.carVisual.forward : root.car.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 1e-4f) { fwd.Normalize(); _heading = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg; _hasHeading = true; }
            else fwd = Vector3.forward;

            // zoom out a little at speed so you can see what is coming
            float speed01 = Mathf.Clamp01(root.carCtl.SpeedMS / 46f);
            _range = Mathf.Lerp(_range, Mathf.Lerp(105f, 190f, speed01), 1f - Mathf.Exp(-Time.unscaledDeltaTime * 2f));
            _cam.orthographicSize = _range;

            var up = _rotate ? fwd : Vector3.forward;
            _cam.transform.SetPositionAndRotation(carPos + Vector3.up * Height, Quaternion.LookRotation(Vector3.down, up));

            // throttle: render only on due frames (the texture keeps its last image otherwise)
            float interval = root.autoQuality != null && root.autoQuality.Level >= 2 ? 0.20f : 0.066f;
            bool due = Time.unscaledTime >= _next;
            if (due) _next = Time.unscaledTime + interval;
            _cam.enabled = due;
        }

        void OnBegin(ScriptableRenderContext ctx, Camera c)
        {
            if (c != _cam) return;
            _fog = RenderSettings.fog; _amb = RenderSettings.ambientMode; _ambCol = RenderSettings.ambientLight;
            RenderSettings.fog = false;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.92f, 0.92f, 0.92f);
        }

        void OnEnd(ScriptableRenderContext ctx, Camera c)
        {
            if (c != _cam) return;
            RenderSettings.fog = _fog; RenderSettings.ambientMode = _amb; RenderSettings.ambientLight = _ambCol;
        }

        // ------------------------------------------------------------------------------------------------ IMGUI

        /// <summary>Called from DebugHud.OnGUI (GUI.matrix already scaled to the 720 px canvas).</summary>
        public void Draw(GUIStyle letter)
        {
            if (_rt == null || !_hasHeading) return;
            var r = Rect;
            float rad = Size * 0.5f;
            var c = r.center;

            if (Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition))
            {
                _rotate = !_rotate; PlayerPrefs.SetInt(RotKey, _rotate ? 1 : 0); Event.current.Use();
            }
            if (Event.current.type != EventType.Repaint) return;

            // drop shadow, map, rim
            GUI.DrawTexture(new Rect(r.x - 5, r.y - 3, Size + 10, Size + 10), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, new Color(0, 0, 0, 0.35f), 0f, rad + 5f);
            GUI.DrawTexture(r, _rt, ScaleMode.StretchToFill, false, 0f, Color.white, 0f, rad);
            GUI.DrawTexture(new Rect(r.x - 2, r.y - 2, Size + 4, Size + 4), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, new Color(0.93f, 0.96f, 1f, 0.85f), 3f, rad + 2f);

            // car arrow (always the centre; points up when the map rotates, otherwise follows the heading)
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(_rotate ? 0f : _heading, c);
            var old = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.55f);
            GUI.DrawTexture(new Rect(c.x - 15, c.y - 14, 30, 30), UiTheme.Arrow);
            GUI.color = UiTheme.Accent;
            GUI.DrawTexture(new Rect(c.x - 12, c.y - 12, 24, 24), UiTheme.Arrow);
            GUI.color = old;
            GUI.matrix = m;

            // north marker on the rim
            float a = (_rotate ? -_heading : 0f) * Mathf.Deg2Rad;
            var n = new Vector2(c.x + Mathf.Sin(a) * (rad - 11f), c.y - Mathf.Cos(a) * (rad - 11f));
            GUI.DrawTexture(new Rect(n.x - 10, n.y - 10, 20, 20), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, new Color(0.04f, 0.07f, 0.11f, 0.92f), 0f, 10f);
            GUI.Label(new Rect(n.x - 10, n.y - 11, 20, 20), "N", letter);
        }
    }
}
