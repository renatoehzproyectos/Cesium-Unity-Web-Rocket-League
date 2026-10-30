using System.Collections;
using System.Globalization;
using UnityEngine;
using UnityEngine.Networking;

namespace RocketWorld
{
    /// <summary>IMGUI HUD + settings panel (works identically in WebGL, WebGPU and the Editor).</summary>
    public class DebugHud : MonoBehaviour
    {
        public GameRoot root;
        bool _open;
        float _toast;
        string _toastText = "";
        Vector2 _scroll;
        GUIStyle _small, _center, _attr;
        float _k = 1f;
        bool _vehOpen;
        Rect _gear, _panel, _report, _camBtn;
        // IMGUI allocates every frame it builds strings, so the stats text is rebuilt 4x per second and cached.
        readonly float[] _ring = new float[120]; int _ringN, _ringI;
        readonly float[] _sorted = new float[120];
        string _stats = "";
        float _nextStats;
        bool _reportOpen;

        // ---- controls (key / gamepad rebinding) panel ----
        Rect _ctl, _ctlPanel;
        bool _ctlOpen;
        GUIStyle _btn;
        Vector2 _ctlScroll;
        int _listenAction = -1, _listenSlot;          // slot: 0/1 = keyboard, 2 = gamepad
        float[] _listenBase; float _listenUntil;
        int _selRow, _selCol = InputBindings.SlotPad;
        Edge _nUp, _nDown, _nLeft, _nRight, _nOk, _nClear, _nReset, _nBack;

        string _searchQuery = "";
        string _searchStatus = "";
        bool _searching;

        void Update()
        {
            if (root == null) return;
            float dtMs = Time.unscaledDeltaTime * 1000f;
            _ring[_ringI] = dtMs; _ringI = (_ringI + 1) % _ring.Length; if (_ringN < _ring.Length) _ringN++;
            if (Time.unscaledTime >= _nextStats && root.settings.debugHud) { _nextStats = Time.unscaledTime + 0.25f; RebuildStats(); }
            if (root.benchmark != null && !_reportOpen && !string.IsNullOrEmpty(root.benchmark.Report) && !root.benchmark.Running) _reportOpen = true;
            var ci = root.carInput;
            if (_ctlOpen) UpdateControlsPanel();
            else if (ci != null ? ci.SettingsPressed : OrbitCamera.SettingsKeyPressed())
            {
                if (root.camMenu != null && root.camMenu.Open) root.camMenu.Close();
                else if (ci != null && ci.MenuFromPad && !_open) SetControlsOpen(true);   // gamepad menu button: the pad-navigable screen
                else SetOpen(!_open);
            }
            if (ci != null) ci.Blocked = _ctlOpen || GameRoot.MenuBlocksInput;
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
            float fps = 1000f / Mathf.Max(p95 > 0 ? p95 : 16f, 0.1f);
            _stats = $"<color=#ffffffcc>{fps:0} fps  p95 {p95:0.0} ms  worst {worst:0} ms   tiles {tiles}   heap {heap} MB{bench}\n" +
                     $"{s.lat:0.00000}, {s.lon:0.00000}   ground {s.groundHeight:0} m   cam yaw {root.orbit.yaw:0} pitch {root.orbit.pitch:0} dist {root.orbit.distance:0.0} m\n" +
                     $"car {root.carCtl.SpeedMS * 3.6f:0} km/h  wheels {(root.carCtl.CurrentPhase == RocketCar.Phase.Running ? WheelCount() : 0)}/4  {(root.carCtl.InBoat ? "boat" : root.carCtl.State.isOnGround ? "ground" : "air")}  gyro-comp {(root.carCtl.GyroscopicCompensation ? "on" : "off")}  colliders {(root.budget != null ? root.budget.WithCollider + "/" + root.budget.Registered : "-")}\n" +
                     $"{(root.viewPolicy != null ? root.viewPolicy.Info : "view-driven tiles off")}\n{(root.carCtl.water != null ? root.carCtl.water.Info : "water off")}   {(root.waterMap != null && !string.IsNullOrEmpty(root.waterMap.LastError) ? "<color=#ff8a80>" + root.waterMap.LastError + "</color>" : "")}\n" +
                     $"quality: sse {root.EffectiveSse:0}  scale {root.EffectiveScale:0.00}  far {root.EffectiveFar:0} m  auto-level {(root.autoQuality != null ? root.autoQuality.Level : 0)}</color>";
        }

        int WheelCount() { int n = 0; for (int i = 0; i < 4; i++) if (root.carCtl.Vehicle.WheelInContact(i)) n++; return n; }

        void SetOpen(bool open)
        {
            if (_open && !open)
            {
                root.settingsOpen = false;
                root.FlushOrigin();
                root.FlushSse();
                root.SaveSettings();
            }
            if (!_open && open)
            {
                root.settingsOpen = true;
                if (_ctlOpen) SetControlsOpen(false);
                if (root.camMenu != null) root.camMenu.Close();
            }
            _open = open;
        }

        /// <summary>True when a screen position (pixels, origin bottom-left) is over HUD, so the camera ignores that drag.</summary>
        public bool Over(Vector2 screenPos)
        {
            if (GameRoot.MenuBlocksInput) return true;
            var p = new Vector2(screenPos.x / _k, (Screen.height - screenPos.y) / _k);
            if (root.carInput != null && root.carInput.TouchControlsActive && CarInput.OverTouchZone(screenPos)) return true;
            if (root.camMenu != null && root.camMenu.Over(screenPos)) return true;
            return _camBtn.Contains(p) || (root.minimap != null && root.minimap.Rect.Contains(p)) || new Rect(_ctl.x - 86, _ctl.y, 80, _ctl.height).Contains(p) || _gear.Contains(p) || _ctl.Contains(p) || (_ctlOpen && _ctlPanel.Contains(p)) || (_open && _panel.Contains(p)) || (_reportOpen && _report.Contains(p));
        }

        float _runStart = -1f, _shownSpeed;
        GUIStyle _mapN;

        static void Shadowed(Rect r, string text, GUIStyle st)
        {
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f * old.a);
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, st);
            GUI.color = old;
            GUI.Label(r, text, st);
        }

        /// <summary>Plane mode: airspeed (kt), altitude (ft), vertical speed, throttle lever, stall warning.</summary>
        void DrawFlightInstruments(RocketCar cc, float W, float H)
        {
            var p = cc.plane; if (p == null) return;
            Shadowed(new Rect(W * 0.5f - 160, H - 176, 320, 22), $"PLANE   IAS {p.AirspeedKt:0} kt   ALT {p.AltitudeFt:0} ft   VS {p.VerticalSpeedFpm:+0;-0;0} fpm", UiTheme.Unit);
            if (Event.current.type == EventType.Repaint)
            {
                var oc = GUI.color; float x = W * 0.5f - 160, y = H - 152;
                GUI.color = new Color(0, 0, 0, 0.45f); GUI.DrawTexture(new Rect(x, y, 320, 6), Texture2D.whiteTexture);
                GUI.color = p.ThrottleLever > 0.85f ? new Color(1f, 0.6f, 0.2f) : new Color(0.4f, 0.9f, 0.5f); GUI.DrawTexture(new Rect(x, y, 320 * p.ThrottleLever, 6), Texture2D.whiteTexture);
                GUI.color = oc;
            }
            if (p.Stalled) Shadowed(new Rect(W * 0.5f - 120, 150, 240, 30), "STALL", UiTheme.Unit);
        }

        /// <summary>Touch widgets of the current vehicle: buttons (lit while held / latched), the throttle lever, the spring sliders and the rudder.</summary>
        void DrawTouchWidgets(float W, float H)
        {
            var ws = CarInput.Widgets; var cc = root.carCtl; var oc = GUI.color;
            for (int i = 0; i < ws.Length; i++)
            {
                var w = ws[i];
                var r = new Rect(w.zone.x * W, (1f - w.zone.y - w.zone.height) * H, w.zone.width * W, w.zone.height * H);
                bool on = root.carInput.WidgetOn(i);
                switch (w.kind)
                {
                    case WidgetKind.SliderSticky:
                    case WidgetKind.SliderSpring:
                    case WidgetKind.SliderH:
                    {
                        GUI.Box(r, "", UiTheme.Touch);
                        float v = root.carInput.WidgetValue(i); bool touched = root.carInput.WidgetTouched(i);
                        if (w.kind == WidgetKind.SliderSticky && !touched) v = cc != null ? cc.LeverDisplay : 0f;      // follow the real lever (keys / gamepad move it too)
                        if (Event.current.type == EventType.Repaint)
                        {
                            GUI.color = new Color(0.4f, 0.85f, 1f, touched ? 0.55f : 0.35f);
                            if (w.kind == WidgetKind.SliderSticky) { float hh = r.height * Mathf.Clamp01(v); GUI.DrawTexture(new Rect(r.x + 4, r.yMax - hh - 3, r.width - 8, Mathf.Max(hh, 3)), Texture2D.whiteTexture); }
                            else if (w.kind == WidgetKind.SliderSpring) { float cy = r.center.y, hy = -v * r.height * 0.5f; GUI.DrawTexture(new Rect(r.x + 4, Mathf.Min(cy, cy + hy), r.width - 8, Mathf.Max(Mathf.Abs(hy), 3)), Texture2D.whiteTexture); GUI.color = new Color(1, 1, 1, 0.5f); GUI.DrawTexture(new Rect(r.x, cy - 1, r.width, 2), Texture2D.whiteTexture); }
                            else { float cx = r.center.x, hx = v * r.width * 0.5f; GUI.DrawTexture(new Rect(Mathf.Min(cx, cx + hx), r.y + 4, Mathf.Max(Mathf.Abs(hx), 3), r.height - 8), Texture2D.whiteTexture); GUI.color = new Color(1, 1, 1, 0.5f); GUI.DrawTexture(new Rect(cx - 1, r.y, 2, r.height), Texture2D.whiteTexture); }
                            GUI.color = oc;
                        }
                        GUI.Label(new Rect(r.x, r.y + (w.kind == WidgetKind.SliderH ? 2 : -18), r.width, 18), w.label, UiTheme.Unit);
                        break;
                    }
                    default:
                        if (on) GUI.color = new Color(0.45f, 0.9f, 1f, 1f);
                        GUI.Box(r, w.label, UiTheme.Touch);
                        GUI.color = oc;
                        break;
                }
            }
        }

        /// <summary>Vehicle mode (F-35B, F-15E, helicopter, DeLorean): speed, altitude, vertical speed, status line, throttle lever.</summary>
        void DrawVehicleInstruments(RocketCar cc, float W, float H)
        {
            var d = cc.vehicle; if (d == null || !d.Active) return;
            Shadowed(new Rect(W * 0.5f - 200, H - 176, 400, 22), $"{d.Spec.name.ToUpperInvariant()}   ALT {d.AltM:0} m   VS {d.VsMs:+0.0;-0.0;0.0} m/s", UiTheme.Unit);
            if (!string.IsNullOrEmpty(d.Status)) Shadowed(new Rect(W * 0.5f - 200, H - 198, 400, 22), d.Status, UiTheme.Unit);
            if (Event.current.type == EventType.Repaint)
            {
                var oc = GUI.color; float x = W * 0.5f - 160, y = H - 152;
                GUI.color = new Color(0, 0, 0, 0.45f); GUI.DrawTexture(new Rect(x, y, 320, 6), Texture2D.whiteTexture);
                GUI.color = d.Lever > 0.85f ? new Color(1f, 0.6f, 0.2f) : new Color(0.4f, 0.9f, 0.5f); GUI.DrawTexture(new Rect(x, y, 320 * d.Lever, 6), Texture2D.whiteTexture);
                GUI.color = oc;
            }
            if (d.Stalled) Shadowed(new Rect(W * 0.5f - 120, 150, 240, 30), "STALL", UiTheme.Unit);
        }

        void DrawSpeed(float W, float H)
        {
            var cc = root.carCtl;
            _shownSpeed = Mathf.Lerp(_shownSpeed, cc.SpeedMS, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 12f));
            float frac = Mathf.Clamp01(_shownSpeed / 46f);
            bool boosting = cc.IsBoosting;
            if (cc.InBoat) Shadowed(new Rect(W * 0.5f - 120, H - 150, 240, 20), "BOAT", UiTheme.Unit);
            if (cc.InPlane) DrawFlightInstruments(cc, W, H);
            else if (cc.InVehicle) DrawVehicleInstruments(cc, W, H);
            if (cc.HyperT > 0.05f) Shadowed(new Rect(W * 0.5f - 160, H - 222, 320, 24), $"HYPER BOOST  ×{cc.HyperMul:0.#}", UiTheme.Unit);
            Shadowed(new Rect(W * 0.5f - 120, H - 128, 240, 66), Mathf.RoundToInt(_shownSpeed * 3.6f).ToString(), UiTheme.Big);
            Shadowed(new Rect(W * 0.5f - 120, H - 62, 240, 20), "KM/H", UiTheme.Unit);
            if (Event.current.type != EventType.Repaint) return;
            var track = new Rect(W * 0.5f - 110, H - 38, 220, 6);
            GUI.DrawTexture(track, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, new Color(1, 1, 1, 0.16f), 0f, 3f);
            if (frac > 0.01f)
                GUI.DrawTexture(new Rect(track.x, track.y, Mathf.Max(6f, track.width * frac), track.height), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                    boosting ? UiTheme.Hot : Color.Lerp(UiTheme.Accent, Color.white, frac * 0.35f), 0f, 3f);
        }

        void OnGUI()
        {
            if (root == null) return;
            GUI.skin = UiTheme.Skin;
            _k = Mathf.Clamp(Screen.height / 720f, 1f, 2.5f);
            GUIUtility.ScaleAroundPivot(new Vector2(_k, _k), Vector2.zero);
            float W = Screen.width / _k, H = Screen.height / _k;
            _small ??= new GUIStyle(GUI.skin.label) { fontSize = 14 };
            _center = UiTheme.Center;
            _btn ??= new GUIStyle(GUI.skin.button) { fontSize = 14, wordWrap = true };
            _mapN ??= new GUIStyle(GUI.skin.label) { font = UiTheme.Bold, fontSize = 13, alignment = TextAnchor.MiddleCenter, wordWrap = false, normal = { textColor = UiTheme.Accent } };
            var s = root.settings;
            _gear = new Rect(W - 60, 14, 46, 46);
            _ctl = new Rect(W - 166, 14, 100, 46);
            _camBtn = new Rect(W - 96, 66, 82, 34);            // Camera menu button (right of the vehicle button)
            _panel = new Rect(W - 380, 70, 370, H - 84);
            _ctlPanel = new Rect(Mathf.Max(8, W - 520), 70, Mathf.Min(510, W - 16), H - 84);

            bool running = root.carCtl.CurrentPhase == RocketCar.Phase.Running;
            if (running && _runStart < 0f) _runStart = Time.unscaledTime;
            bool touch = root.carInput != null && root.carInput.TouchControlsActive;
            if (root.waterMap != null)       // OpenStreetMap's licence asks for attribution wherever its data is used
            {
                _attr ??= new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.LowerLeft };
                var oc = GUI.color; GUI.color = new Color(1f, 1f, 1f, 0.55f);
                GUI.Label(new Rect(10, H - 22, 460, 18), "Water data \u00a9 OpenStreetMap contributors \u00b7 OpenFreeMap", _attr);
                GUI.color = oc;
            }

            if (Event.current.type == EventType.Repaint)
                GUI.DrawTexture(new Rect(0, 0, W, H), UiTheme.Vignette, ScaleMode.StretchToFill, true);

            if (root.minimap != null) root.minimap.Draw(_mapN);

            if (!string.IsNullOrEmpty(root.carCtl.Status))
                GUI.Box(new Rect(W * 0.5f - 200, H * 0.40f, 400, 44), root.carCtl.Status, UiTheme.Toast);
            if (running) DrawSpeed(W, H);

            if (touch)
            {
                DrawTouchWidgets(W, H);
                var sr = CarInput.StickRect;
                var box = new Rect(sr.x / _k, (Screen.height - sr.yMax) / _k, sr.width / _k, sr.height / _k);
                GUI.Box(box, "", UiTheme.Touch);
                var v = root.carInput.StickValue;
                float ks = box.width * 0.30f;
                var kc = box.center + new Vector2(v.x * box.width * 0.5f, -v.y * box.height * 0.5f);
                GUI.Box(new Rect(kc.x - ks * 0.5f, kc.y - ks * 0.5f, ks, ks), "", UiTheme.Touch);
            }
            else if (running && W > 1100f)
            {
                // controls cheat-sheet: shown for the first seconds of play, then fades away
                float t = Time.unscaledTime - _runStart, a = t < 12f ? 1f : Mathf.Clamp01(1f - (t - 12f) / 2f);
                if (a > 0.01f && !_open && !_ctlOpen)
                {
                    var oc = GUI.color; GUI.color = new Color(1, 1, 1, a);
                    GUI.Box(new Rect(W * 0.5f - 350, 52, 700, 36), "WASD drive  ·  Space jump  ·  Shift boost  ·  Ctrl drift  ·  C camera  ·  R rescue  ·  B ball", UiTheme.Pill);
                    GUI.color = oc;
                }
            }
            if (!s.HasCredentials)
                GUI.Box(new Rect(W * 0.5f - 190, 94, 380, 34), "Test ground — world streaming is off in this build", UiTheme.Pill);

            if (GUI.Button(_gear, GUIContent.none)) SetOpen(!_open);
            if (Event.current.type == EventType.Repaint)
            {
                var oc = GUI.color; GUI.color = UiTheme.Text;
                GUI.DrawTexture(new Rect(_gear.center.x - 14, _gear.center.y - 14, 28, 28), UiTheme.Gear);
                GUI.color = oc;
            }
            if (GUI.Button(_ctl, "Controls", _btn)) SetControlsOpen(!_ctlOpen);
            if (GUI.Button(new Rect(W - 252, 14, 80, 46), "Ball", _btn)) root.RequestBall();
            if (GUI.Button(_camBtn, "Camera", _btn) && root.camMenu != null)
            {
                if (_open) SetOpen(false);
                if (_ctlOpen) SetControlsOpen(false);
                root.camMenu.Toggle();
            }
            string curVeh = root.carCtl != null ? Vehicles.NameOf(root.carCtl.CurrentVehicle) : "Car";
            if (GUI.Button(new Rect(W - 252, 66, 154, 34), curVeh + " ▾", _btn)) _vehOpen = !_vehOpen;
            if (_vehOpen)
            {
                for (int i = 0; i < Vehicles.Order.Length; i++)
                {
                    var k = Vehicles.Order[i]; bool cur = root.carCtl != null && root.carCtl.CurrentVehicle == k;
                    if (GUI.Button(new Rect(W - 332, 104 + i * 36, 160, 32), (cur ? "● " : "   ") + Vehicles.NameOf(k), _btn)) { root.RequestVehicle(k); _vehOpen = false; }
                }
            }
            if (Time.unscaledTime < _toast) GUI.Box(new Rect(W * 0.5f - 160, 134, 320, 38), _toastText, UiTheme.Toast);
            if (_reportOpen && root.benchmark != null) DrawReport(H);
            if (_ctlOpen) DrawControls();
            if (!_open) return;

            bool changed = false;
            GUILayout.BeginArea(_panel, GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Label("Rocket World", UiTheme.Title);

            GUILayout.Label("Go to location");
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("rw_loc_search");
            _searchQuery = GUILayout.TextField(_searchQuery ?? "", GUILayout.MinWidth(180));
            bool go = GUILayout.Button(_searching ? "…" : "Go", GUILayout.Width(44));
            GUILayout.EndHorizontal();
            if (go && !_searching && !string.IsNullOrWhiteSpace(_searchQuery))
                StartCoroutine(SearchAndTeleport(_searchQuery.Trim()));
            // Enter key while the search field is focused
            if (!_searching && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return
                && GUI.GetNameOfFocusedControl() == "rw_loc_search" && !string.IsNullOrWhiteSpace(_searchQuery))
            {
                StartCoroutine(SearchAndTeleport(_searchQuery.Trim()));
                Event.current.Use();
            }
            if (!string.IsNullOrEmpty(_searchStatus))
                GUILayout.Label(_searchStatus, _small);

            GUILayout.Space(6);
            GUILayout.Label($"Tile detail (max SSE) {s.sse:0} — lower = sharper");
            s.sse = GUILayout.HorizontalSlider(s.sse, 4f, 64f);
            GUILayout.Label($"View distance {s.viewDistance:0} m");
            s.viewDistance = GUILayout.HorizontalSlider(s.viewDistance, 400f, 20000f);
            GUILayout.Label($"Render scale {s.renderScale:0.00}");
            s.renderScale = GUILayout.HorizontalSlider(s.renderScale, 0.3f, 2f);
            bool ac = GUILayout.Toggle(s.antiCrash, "Anti crash (keeps 30+ FPS: render scale, depth of field and motion blur start at minimum and adapt; the sliders are the maximum. On iPhone/iPad it also loads the low-memory build: reload the page after changing it)");
            if (ac != s.antiCrash) { s.antiCrash = ac; WebBridge.SetAntiCrash(ac); }
            if (WebBridge.IsLiteBuild()) GUILayout.Label("Low-memory build active");

            GUILayout.Space(6);
            if (GUILayout.Button("Camera menu (mode, offsets, follow, delay)")) { SetOpen(false); root.camMenu.OpenMenu(); }

            GUILayout.Space(6);
            GUILayout.Label("Cinematic effects (0 % = off, 100 % = default look)");
            GUILayout.Label($"Depth of field {s.fxDof * 100f:0} %");
            s.fxDof = GUILayout.HorizontalSlider(s.fxDof, 0f, 2f);
            GUILayout.Label($"Motion blur {s.fxMotion * 100f:0} %");
            s.fxMotion = GUILayout.HorizontalSlider(s.fxMotion, 0f, 2f);
            if (root.cinematic != null) GUILayout.Label("<color=#ffffff88>" + root.cinematic.Status + "</color>", _small);

            GUILayout.Space(6);
            GUILayout.Label("Car colour");
            GUILayout.BeginHorizontal();
            for (int i = 0; i < GameSettings.CarColors.Length; i++)
                if (GUILayout.Button(GameSettings.CarColors[i].name)) { s.carColor = i; changed = true; }
            GUILayout.EndHorizontal();
            GUILayout.Label("<color=#ffffff88>Settings are saved in this browser.\nAircraft model: \"YC-130\" by KOG_THORNS, CC BY 4.0.</color>", _small);
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            if (GUI.changed || changed) root.ApplyLive();
        }

        // =================================================== controls panel ===================================================
        void SetControlsOpen(bool open)
        {
            if (open == _ctlOpen) return;
            if (open && _open) SetOpen(false);
            if (open && root.camMenu != null) root.camMenu.Close();
            _ctlOpen = open;
            _listenAction = -1;
            if (!open && root.carInput != null) root.carInput.bindings.Save();
            if (root.carInput != null) root.carInput.Blocked = open;
        }

        void StartListening(int action, int slot)
        {
            if (_listenAction == action && _listenSlot == slot) { _listenAction = -1; return; }   // click again = cancel
            _listenAction = action; _listenSlot = slot; _listenBase = InputDevices.PadBaseline(); _listenUntil = Time.unscaledTime + 8f;
            _selRow = action; _selCol = slot;
        }

        void Toast(string t) { _toast = Time.unscaledTime + 2.5f; _toastText = t; }

        void Bound(CarAction a, CarAction? stolen)
        {
            root.carInput.bindings.Save();
            Toast($"{InputBindings.Label(a)} bound");
        }

        void UpdateControlsPanel()
        {
            var ib = root.carInput != null ? root.carInput.bindings : null;
            if (ib == null) return;
            // Edges are sampled every frame (even while listening) so a held button never counts as a fresh press later.
            var ls = InputDevices.LeftStick();
            bool up = _nUp.Update(InputDevices.PadValue(PadInput.DpadUp) > 0.5f || ls.y > 0.6f);
            bool down = _nDown.Update(InputDevices.PadValue(PadInput.DpadDown) > 0.5f || ls.y < -0.6f);
            bool left = _nLeft.Update(InputDevices.PadValue(PadInput.DpadLeft) > 0.5f || ls.x < -0.6f);
            bool right = _nRight.Update(InputDevices.PadValue(PadInput.DpadRight) > 0.5f || ls.x > 0.6f);
            bool ok = _nOk.Update(InputDevices.PadValue(PadInput.ButtonSouth) > 0.5f);
            bool clear = _nClear.Update(InputDevices.PadValue(PadInput.ButtonWest) > 0.5f);
            bool reset = _nReset.Update(InputDevices.PadValue(PadInput.ButtonNorth) > 0.5f);
            bool back = _nBack.Update(InputDevices.PadValue(PadInput.ButtonEast) > 0.5f || InputDevices.PadValue(ib.GetPad(CarAction.Menu)) > 0.5f);

            if (_listenAction >= 0)
            {
                var act = InputBindings.Actions[_listenAction];
                if (Time.unscaledTime > _listenUntil) { _listenAction = -1; return; }
                if (_listenSlot < InputBindings.KeySlots)
                {
                    if (InputDevices.AnyKeyboardKeyPressed(out var key))
                    {
                        if (key == "Escape") { _listenAction = -1; return; }
                        if (key == "Backspace" || key == "Delete") { ib.Clear(act, _listenSlot); Bound(act, null); _listenAction = -1; return; }
                        var stolen = ib.SetKey(act, _listenSlot, key);
                        _listenAction = -1; Bound(act, stolen);
                    }
                }
                else
                {
                    // Compare against the values when listening began: the confirm press already held, or an axis that rests above zero, must not bind.
                    if (InputDevices.AnyPadInputRaised(_listenBase, out var p))
                    {
                        var stolen = ib.SetPad(act, p);
                        _listenAction = -1; Bound(act, stolen);
                    }
                    if (OrbitCamera.SettingsKeyPressed()) _listenAction = -1;   // Esc cancels
                }
                return;
            }

            if (OrbitCamera.SettingsKeyPressed() || back) { SetControlsOpen(false); return; }
            int n = InputBindings.Actions.Length;
            if (up) _selRow = (_selRow + n - 1) % n;
            if (down) _selRow = (_selRow + 1) % n;
            if (left) _selCol = Mathf.Max(0, _selCol - 1);
            if (right) _selCol = Mathf.Min(InputBindings.SlotPad, _selCol + 1);
            var sel = InputBindings.Actions[_selRow];
            if (InputBindings.KeyboardLocked(sel)) _selCol = InputBindings.SlotPad;
            if (ok && !(InputBindings.KeyboardLocked(sel) && _selCol < InputBindings.KeySlots)) StartListening(_selRow, _selCol);
            if (clear) { ib.Clear(sel, _selCol); ib.Save(); }
            if (reset) { ib.ResetAll(); ib.Save(); Toast("Controls reset to defaults"); }
        }

        void DrawControls()
        {
            var ib = root.carInput != null ? root.carInput.bindings : null;
            if (ib == null) return;
            GUILayout.BeginArea(_ctlPanel, GUI.skin.box);
            GUILayout.Label("Controls", UiTheme.Title);
            string pad = InputDevices.PadName();
            GUILayout.Label(pad != null ? "Gamepad: " + pad : "No gamepad detected — connect one and press any button.", _small);
            if (pad != null) GUILayout.Label("Pressing now: " + (InputDevices.ActivePadInputs() is var act && act.Length > 0 ? act : "—"), _small);
            GUILayout.Label("<color=#ffffff99>Click a slot, then press the key / button you want. Esc cancels, Backspace clears. Right stick orbits the camera; left stick up/down pitches in the air.</color>", _small);
            _ctlScroll = GUILayout.BeginScrollView(_ctlScroll);
            GUILayout.BeginHorizontal();
            GUILayout.Label("", GUILayout.Width(150));
            GUILayout.Label("Key", _center, GUILayout.Width(88)); GUILayout.Label("Alt key", _center, GUILayout.Width(88)); GUILayout.Label("Gamepad", _center, GUILayout.Width(110));
            GUILayout.EndHorizontal();
            var old = GUI.backgroundColor;
            for (int i = 0; i < InputBindings.Actions.Length; i++)
            {
                var a = InputBindings.Actions[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label(InputBindings.Label(a), GUILayout.Width(150));
                for (int slot = 0; slot < InputBindings.SlotCount; slot++)
                {
                    bool locked = slot < InputBindings.KeySlots && InputBindings.KeyboardLocked(a);
                    bool listening = _listenAction == i && _listenSlot == slot;
                    string text = listening ? "press…" : locked ? (slot == 0 ? "Esc" : "Tab")
                        : slot == InputBindings.SlotPad ? InputBindings.PadLabel(ib.GetPad(a)) : InputBindings.KeyLabel(ib.GetKey(a, slot));
                    var slotStyle = listening ? UiTheme.BtnListen : (i == _selRow && slot == _selCol && InputDevices.HasGamepad) ? UiTheme.BtnSelect : _btn;
                    GUI.enabled = !locked;
                    if (GUILayout.Button(text, slotStyle, GUILayout.Width(slot == InputBindings.SlotPad ? 110 : 88), GUILayout.Height(28))) StartListening(i, slot);
                    GUI.enabled = true;
                }
                GUILayout.EndHorizontal();
            }
            GUI.backgroundColor = old;
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset to defaults")) { ib.ResetAll(); ib.Save(); _listenAction = -1; Toast("Controls reset to defaults"); }
            if (GUILayout.Button("Done")) SetControlsOpen(false);
            GUILayout.EndHorizontal();
            if (InputDevices.HasGamepad)
                GUILayout.Label("<color=#ffffff88>Gamepad: D-pad / left stick move · A bind · X clear · Y reset all · B / Menu close</color>", _small);
            GUILayout.EndArea();
        }

        IEnumerator SearchAndTeleport(string query)
        {
            _searching = true;
            _searchStatus = "Searching…";
            // Nominatim (OpenStreetMap) — free, no API key. Identify the app per their usage policy.
            string url = "https://nominatim.openstreetmap.org/search?format=json&limit=1&q=" + UnityWebRequest.EscapeURL(query);
            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("Accept", "application/json");
                req.SetRequestHeader("User-Agent", "RocketWorld/1.0 (Unity WebGL; location search)");
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    _searchStatus = "Search failed: " + req.error;
                    _searching = false;
                    yield break;
                }
                var json = req.downloadHandler.text;
                if (string.IsNullOrEmpty(json) || json == "[]")
                {
                    _searchStatus = "No results for \"" + query + "\"";
                    _searching = false;
                    yield break;
                }
                // Root array → wrap for JsonUtility
                var list = JsonUtility.FromJson<NominatimList>("{\"items\":" + json + "}");
                if (list == null || list.items == null || list.items.Length == 0)
                {
                    _searchStatus = "No results for \"" + query + "\"";
                    _searching = false;
                    yield break;
                }
                var hit = list.items[0];
                if (!double.TryParse(hit.lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                    || !double.TryParse(hit.lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                {
                    _searchStatus = "Bad coordinates in result";
                    _searching = false;
                    yield break;
                }

                float gh = root.settings.groundHeight;
                _searchStatus = "Looking up elevation…";
                string elevUrl = string.Format(CultureInfo.InvariantCulture,
                    "https://api.open-meteo.com/v1/elevation?latitude={0:0.#####}&longitude={1:0.#####}", lat, lon);
                using (var elevReq = UnityWebRequest.Get(elevUrl))
                {
                    yield return elevReq.SendWebRequest();
                    if (elevReq.result == UnityWebRequest.Result.Success)
                    {
                        var elev = JsonUtility.FromJson<ElevationResponse>(elevReq.downloadHandler.text);
                        if (elev != null && elev.elevation != null && elev.elevation.Length > 0)
                            gh = elev.elevation[0];
                    }
                }

                string name = string.IsNullOrEmpty(hit.display_name) ? query : hit.display_name;
                if (name.Length > 60) name = name.Substring(0, 57) + "…";
                _searchStatus = "Going to " + name;
                root.TeleportTo(lat, lon, gh);
                _toast = Time.unscaledTime + 2.5f;
                _toastText = "Teleported";
            }
            _searching = false;
        }

        [System.Serializable] class NominatimList { public NominatimItem[] items; }
        [System.Serializable] class NominatimItem { public string lat; public string lon; public string display_name; }
        [System.Serializable] class ElevationResponse { public float[] elevation; }

        void DrawReport(float H)
        {
            _report = new Rect(10, H - 250, 640, 215);
            GUI.Box(_report, GUIContent.none);
            GUILayout.BeginArea(new Rect(_report.x + 8, _report.y + 6, _report.width - 16, _report.height - 12));
            GUILayout.Label("<b>Benchmark report</b>", _small);
            GUILayout.Label(root.benchmark.Report, _small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Close")) _reportOpen = false;
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
