using UnityEngine;

namespace RocketWorld
{
    /// <summary>IMGUI HUD + settings panel (works identically in WebGL, WebGPU and the Editor).</summary>
    public class DebugHud : MonoBehaviour
    {
        public GameRoot root;
        bool _open;
        float _fps = 60f, _toast;
        string _toastText = "";
        Vector2 _scroll;
        GUIStyle _small, _center;
        float _k = 1f;
        Rect _gear, _link, _panel;

        void Update()
        {
            if (root == null) return;
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0.05f);
            if (OrbitCamera.SettingsKeyPressed()) SetOpen(!_open);
        }

        void SetOpen(bool open) { if (_open && !open) root.SaveSettings(); _open = open; }

        /// <summary>True when a screen position (pixels, origin bottom-left) is over HUD, so the camera ignores that drag.</summary>
        public bool Over(Vector2 screenPos)
        {
            var p = new Vector2(screenPos.x / _k, (Screen.height - screenPos.y) / _k);
            return _gear.Contains(p) || _link.Contains(p) || (_open && _panel.Contains(p));
        }

        void OnGUI()
        {
            if (root == null) return;
            _k = Mathf.Clamp(Screen.height / 720f, 1f, 2.5f);
            GUIUtility.ScaleAroundPivot(new Vector2(_k, _k), Vector2.zero);
            float W = Screen.width / _k, H = Screen.height / _k;
            _small ??= new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true };
            _center ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, richText = true };
            var s = root.settings;
            _gear = new Rect(W - 48, 8, 40, 40); _link = new Rect(W - 94, 8, 40, 40);
            _panel = new Rect(W - 380, 56, 370, H - 70);

            if (!s.HasCredentials)
                GUI.Label(new Rect(0, H * 0.14f, W, 70), "<size=16>No tile key set — showing a flat test ground.\nOpen ⚙ (or press Esc) and add a Google Maps Platform key (Map Tiles API) or a Cesium ion token.</size>", _center);

            if (s.debugHud)
            {
                string tiles = root.tileset != null ? $"{root.tileset.ComputeLoadProgress():0}%" : "-";
                GUI.Label(new Rect(10, 6, 460, 60), $"<color=#ffffffcc>{_fps:0} fps   tiles {tiles}\n{s.lat:0.00000}, {s.lon:0.00000}   ground {s.groundHeight:0} m\ncamera yaw {root.orbit.yaw:0}  pitch {root.orbit.pitch:0}  dist {root.orbit.distance:0.0} m</color>", _small);
            }
            GUI.Label(new Rect(10, H - 24, 700, 20), "<color=#ffffffaa>Drag: orbit · Wheel / pinch / +−: zoom · Esc: settings · 🔗: copy link to this view</color>", _small);

            if (GUI.Button(_gear, "⚙")) SetOpen(!_open);
            if (GUI.Button(_link, "🔗"))
            {
                GUIUtility.systemCopyBuffer = WebBridge.BaseUrl() + s.ShareQuery(root.orbit.yaw, root.orbit.pitch, root.orbit.distance);
                _toast = Time.unscaledTime + 2.5f; _toastText = "Link copied";
            }
            if (Time.unscaledTime < _toast) GUI.Label(new Rect(0, 56, W, 24), "<size=14>" + _toastText + "</size>", _center);
            if (!_open) return;

            bool changed = false;
            GUILayout.BeginArea(_panel, GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Label("<b>RocketWorld</b>", _small);
            s.provider = (TileProvider)GUILayout.Toolbar((int)s.provider, new[] { "Google 3D Tiles", "Cesium ion" });
            if (s.provider == TileProvider.GoogleDirect)
            {
                GUILayout.Label("Google Maps Platform key: " + (string.IsNullOrEmpty(s.googleKey) ? "(none)" : "••••" + s.googleKey.Substring(Mathf.Max(0, s.googleKey.Length - 4))));
                if (GUILayout.Button("Set key…")) s.googleKey = WebBridge.Prompt("Google Maps Platform API key (Map Tiles API enabled)", s.googleKey);
            }
            else
            {
                GUILayout.Label("Cesium ion token: " + (string.IsNullOrEmpty(s.ionToken) ? "(none)" : "set") + "   asset " + s.ionAssetId);
                if (GUILayout.Button("Set token…")) s.ionToken = WebBridge.Prompt("Cesium ion access token", s.ionToken);
                if (GUILayout.Button("Set asset id…") && long.TryParse(WebBridge.Prompt("Cesium ion asset id (2275207 = Google Photorealistic 3D Tiles)", s.ionAssetId.ToString()), out var id)) s.ionAssetId = id;
            }
            if (GUILayout.Button("Reload tiles")) { root.SaveSettings(); root.CreateWorldContent(); }

            GUILayout.Space(6);
            GUILayout.Label("Location");
            int col = 0;
            GUILayout.BeginHorizontal();
            foreach (var p in GameSettings.Presets)
            {
                if (GUILayout.Button(p.name)) { s.lat = p.lat; s.lon = p.lon; s.groundHeight = p.h; changed = true; }
                if (++col % 3 == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label($"lat {s.lat:0.0000}  lon {s.lon:0.0000}");
            GUILayout.Label($"Ground height {s.groundHeight:0} m (ellipsoid) — adjust until the wheels touch the street");
            s.groundHeight = GUILayout.HorizontalSlider(s.groundHeight, -100f, 500f);
            GUILayout.Label($"Car heading {s.heading:0}°");
            s.heading = GUILayout.HorizontalSlider(s.heading, 0f, 360f);

            GUILayout.Space(6);
            GUILayout.Label($"Time of day {Mathf.FloorToInt(s.timeOfDay):00}:{Mathf.FloorToInt((s.timeOfDay % 1f) * 60f):00}");
            s.timeOfDay = GUILayout.HorizontalSlider(s.timeOfDay, 0f, 24f);
            GUILayout.Label($"Tile detail (max SSE) {s.sse:0} — lower = sharper");
            s.sse = GUILayout.HorizontalSlider(s.sse, 4f, 64f);
            GUILayout.Label($"View distance {s.viewDistance:0} m");
            s.viewDistance = GUILayout.HorizontalSlider(s.viewDistance, 400f, 6000f);
            GUILayout.Label($"Render scale {s.renderScale:0.00}");
            s.renderScale = GUILayout.HorizontalSlider(s.renderScale, 0.3f, 2f);

            GUILayout.Space(6);
            GUILayout.Label("Car colour");
            GUILayout.BeginHorizontal();
            for (int i = 0; i < GameSettings.CarColors.Length; i++)
                if (GUILayout.Button(GameSettings.CarColors[i].name)) { s.carColor = i; changed = true; }
            GUILayout.EndHorizontal();
            s.bakedLighting = GUILayout.Toggle(s.bakedLighting, "Baked lighting (flat ambient, like RocketWorld)");
            s.proceduralSky = GUILayout.Toggle(s.proceduralSky, "Procedural skybox (off = flat sky like RocketWorld)");
            s.autoRotate = GUILayout.Toggle(s.autoRotate, "Auto-rotate when idle");
            s.debugHud = GUILayout.Toggle(s.debugHud, "Debug HUD");
            GUILayout.Label("<color=#ffffff88>Settings are stored in this browser only. Share links never contain your API key.</color>", _small);
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            if (GUI.changed || changed) root.ApplyLive();
        }
    }
}
