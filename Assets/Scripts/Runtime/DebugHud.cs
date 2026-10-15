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
        Rect _gear, _link, _panel, _report;
        // IMGUI allocates every frame it builds strings, so the stats text is rebuilt 4x per second and cached.
        readonly float[] _ring = new float[120]; int _ringN, _ringI;
        readonly float[] _sorted = new float[120];
        string _stats = "", _hint = "<color=#ffffffaa>WASD drive · Space jump (twice = flip) · Shift boost · Ctrl/X drift · Q/E air-roll · C camera · R rescue · Esc settings · 🔗 copy link</color>";
        float _nextStats;
        bool _reportOpen;

        void Update()
        {
            if (root == null) return;
            float dtMs = Time.unscaledDeltaTime * 1000f;
            _fps = Mathf.Lerp(_fps, 1000f / Mathf.Max(dtMs, 0.1f), 0.05f);
            _ring[_ringI] = dtMs; _ringI = (_ringI + 1) % _ring.Length; if (_ringN < _ring.Length) _ringN++;
            if (Time.unscaledTime >= _nextStats && root.settings.debugHud) { _nextStats = Time.unscaledTime + 0.25f; RebuildStats(); }
            if (root.benchmark != null && !_reportOpen && !string.IsNullOrEmpty(root.benchmark.Report) && !root.benchmark.Running) _reportOpen = true;
            if (OrbitCamera.SettingsKeyPressed()) SetOpen(!_open);
        }

        void RebuildStats()
        {
            var s = root.settings;
            System.Array.Copy(_ring, _sorted, _ringN); System.Array.Sort(_sorted, 0, _ringN);
            float p95 = _ringN > 0 ? _sorted[Mathf.Min(_ringN - 1, (int)(_ringN * 0.95f))] : 0f;
            float worst = _ringN > 0 ? _sorted[_ringN - 1] : 0f;
            string tiles = root.tileset != null ? $"{root.tileset.ComputeLoadProgress():0}%" : "-";
            long heap = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);
            string bench = root.benchmark != null && root.benchmark.Running ? $"   <color=#ffd54f>BENCH {root.benchmark.Progress * 100f:0}%</color>" : "";
            _stats = $"<color=#ffffffcc>{_fps:0} fps  p95 {p95:0.0} ms  worst {worst:0} ms   tiles {tiles}   heap {heap} MB{bench}\n" +
                     $"{s.lat:0.00000}, {s.lon:0.00000}   ground {s.groundHeight:0} m   cam yaw {root.orbit.yaw:0} pitch {root.orbit.pitch:0} dist {root.orbit.distance:0.0} m\n" +
                     $"car {root.carCtl.SpeedMS * 3.6f:0} km/h  wheels {(root.carCtl.CurrentPhase == RocketCar.Phase.Running ? WheelCount() : 0)}/4  {(root.carCtl.State.isOnGround ? "ground" : "air")}  gyro-comp {(root.carCtl.GyroscopicCompensation ? "on" : "off")}  colliders {(root.budget != null ? root.budget.WithCollider + "/" + root.budget.Registered : "-")}\n" +
                     $"quality: sse {root.EffectiveSse:0}  scale {root.EffectiveScale:0.00}  far {root.EffectiveFar:0} m  auto-level {(root.autoQuality != null ? root.autoQuality.Level : 0)}</color>";
        }

        int WheelCount() { int n = 0; for (int i = 0; i < 4; i++) if (root.carCtl.Vehicle.WheelInContact(i)) n++; return n; }

        void SetOpen(bool open)
        {
            if (_open && !open)
            {
                root.settingsOpen = false;
                root.FlushOrigin();          // apply any pending location/height change now
                root.FlushSse();             // apply tile-detail once (not while the slider was dragged)
                root.SaveSettings();
            }
            if (!_open && open)
                root.settingsOpen = true;
            _open = open;
        }

        /// <summary>True when a screen position (pixels, origin bottom-left) is over HUD, so the camera ignores that drag.</summary>
        public bool Over(Vector2 screenPos)
        {
            var p = new Vector2(screenPos.x / _k, (Screen.height - screenPos.y) / _k);
            if (root.carInput != null && root.carInput.TouchControlsActive && CarInput.OverTouchZone(screenPos)) return true;
            return _gear.Contains(p) || _link.Contains(p) || (_open && _panel.Contains(p)) || (_reportOpen && _report.Contains(p));
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

            if (!string.IsNullOrEmpty(root.carCtl.Status))
                GUI.Label(new Rect(0, H * 0.42f, W, 40), "<size=22>" + root.carCtl.Status + "</size>", _center);
            if (root.carCtl.CurrentPhase == RocketCar.Phase.Running)
                GUI.Label(new Rect(0, H - 76, W, 56), "<size=34><b>" + Mathf.RoundToInt(root.carCtl.SpeedMS * 3.6f) + "</b></size> <size=14>km/h</size>", _center);
            if (root.carInput != null && root.carInput.TouchControlsActive)
                foreach (var z in CarInput.TouchZones)
                    GUI.Box(new Rect(z.zone.x * W, (1f - z.zone.y - z.zone.height) * H, z.zone.width * W, z.zone.height * H), z.label);
            if (root.carInput != null && root.carInput.TouchControlsActive)
            {
                var sr = CarInput.StickRect;
                var box = new Rect(sr.x / _k, (Screen.height - sr.yMax) / _k, sr.width / _k, sr.height / _k);
                GUI.Box(box, "");
                var v = root.carInput.StickValue;
                float ks = box.width * 0.30f;
                var kc = box.center + new Vector2(v.x * box.width * 0.5f, -v.y * box.height * 0.5f);
                GUI.Box(new Rect(kc.x - ks * 0.5f, kc.y - ks * 0.5f, ks, ks), "");
            }
            if (root.tileLog != null && !string.IsNullOrEmpty(root.tileLog.Hud))
                GUI.Label(new Rect(0, 30, W, 24), "<size=15><b>" + root.tileLog.Hud + "</b></size>", _center);
            if (!s.HasCredentials)
                GUI.Label(new Rect(0, H * 0.14f, W, 70), "<size=16>No tile key set — showing a flat test ground.\nOpen ⚙ (or press Esc) and add a Google Maps Platform key (Map Tiles API) or a Cesium ion token.</size>", _center);

            if (s.debugHud)
            {
                GUI.Label(new Rect(10, 6, 640, 60), _stats, _small);
            }
            GUI.Label(new Rect(10, H - 24, 700, 20), _hint, _small);

            if (GUI.Button(_gear, "⚙")) SetOpen(!_open);
            if (GUI.Button(_link, "🔗"))
            {
                GUIUtility.systemCopyBuffer = WebBridge.BaseUrl() + s.ShareQuery(root.orbit.yaw, root.orbit.pitch, root.orbit.distance);
                _toast = Time.unscaledTime + 2.5f; _toastText = "Link copied";
            }
            if (Time.unscaledTime < _toast) GUI.Label(new Rect(0, 56, W, 24), "<size=14>" + _toastText + "</size>", _center);
            if (_reportOpen && root.benchmark != null) DrawReport(H);
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
            if (GUILayout.Button("Reload tiles")) { root.FlushOrigin(); root.SaveSettings(); root.CreateWorldContent(); }

            GUILayout.Space(6);
            GUILayout.Label("Location");
            int col = 0;
            GUILayout.BeginHorizontal();
            foreach (var p in GameSettings.Presets)
            {
                if (GUILayout.Button(p.name))
                {
                    s.lat = p.lat; s.lon = p.lon; s.groundHeight = p.h;
                    changed = true;
                    // City presets should apply immediately (not wait for the ground-height debounce).
                    root.ApplyLive();
                    root.FlushOrigin();
                }
                if (++col % 3 == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label($"lat {s.lat:0.0000}  lon {s.lon:0.0000}");
            GUILayout.Label($"Ground height {s.groundHeight:0} m (ellipsoid) — adjust until the wheels touch the street");
            s.groundHeight = GUILayout.HorizontalSlider(s.groundHeight, -100f, 500f);
            GUILayout.Label($"Spawn heading {s.heading:0}°");
            s.heading = GUILayout.HorizontalSlider(s.heading, 0f, 360f);

            GUILayout.Space(6);
            GUILayout.Label($"Time of day {Mathf.FloorToInt(s.timeOfDay):00}:{Mathf.FloorToInt((s.timeOfDay % 1f) * 60f):00}");
            s.timeOfDay = GUILayout.HorizontalSlider(s.timeOfDay, 0f, 24f);
            GUILayout.Label($"Tile detail (max SSE) {s.sse:0} — lower = sharper");
            s.sse = GUILayout.HorizontalSlider(s.sse, 4f, 64f);
            GUILayout.Label($"View distance {s.viewDistance:0} m");
            s.viewDistance = GUILayout.HorizontalSlider(s.viewDistance, 400f, 20000f);
            GUILayout.Label($"Load radius around car {s.loadRadius:0} m (all tiles, full detail, live)");
            s.loadRadius = GUILayout.HorizontalSlider(s.loadRadius, 100f, 20000f);
            GUILayout.Label($"Distant tile quality ×{s.distantQuality:0.0} (higher = sharper far tiles, more tiles to load)");
            s.distantQuality = GUILayout.HorizontalSlider(s.distantQuality, 0.5f, 6f);
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
            bool aq = GUILayout.Toggle(s.autoQuality, "Auto quality (keeps ~60 fps by lowering scale / detail / distance)");
            if (aq != s.autoQuality) { s.autoQuality = aq; if (!aq && root.autoQuality != null) root.autoQuality.ResetToUser(); changed = true; }
            GUILayout.Space(6);
            if (root.tileLog != null)
            {
                var tl = root.tileLog;
                GUILayout.Label($"<b>Tile log</b> — {tl.Lines} frames, {tl.Chars / 1024} KB kept (one line per frame)", _small);
                tl.fullTileList = GUILayout.Toggle(tl.fullTileList, "Include every tile on every line (bigger log)");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Copy tile log")) { GUIUtility.systemCopyBuffer = tl.Text(); _toast = Time.unscaledTime + 2.5f; _toastText = "Tile log copied (" + tl.StoredLines + " lines)"; }
                if (GUILayout.Button("Clear / restart timer")) tl.Clear();
                GUILayout.EndHorizontal();
            }
            GUILayout.Label("<color=#ffffffcc>Device: " + DeviceProfile.Describe() + "</color>", _small);
            if (root.benchmark != null && GUILayout.Button(root.benchmark.Running ? "Benchmark running…" : "Run 36 s benchmark (camera flight)") && !root.benchmark.Running) { _reportOpen = false; SetOpen(false); root.benchmark.Begin(); }
            GUILayout.Label("<color=#ffffff88>Settings are stored in this browser only. Share links never contain your API key.</color>", _small);
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            if (GUI.changed || changed) root.ApplyLive();
        }

        void DrawReport(float H)
        {
            _report = new Rect(10, H - 250, 640, 215);
            GUI.Box(_report, GUIContent.none);
            GUILayout.BeginArea(new Rect(_report.x + 8, _report.y + 6, _report.width - 16, _report.height - 12));
            GUILayout.Label("<b>Benchmark report</b>", _small);
            GUILayout.Label(root.benchmark.Report, _small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Copy")) { GUIUtility.systemCopyBuffer = root.benchmark.Report; _toast = Time.unscaledTime + 2f; _toastText = "Report copied"; }
            if (GUILayout.Button("Close")) _reportOpen = false;
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
