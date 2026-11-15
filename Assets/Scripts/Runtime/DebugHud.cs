using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using RocketWorld.Sim.Fpv;
using RocketWorld.Sim;
using UnityEngine;
using UnityEngine.Networking;

namespace RocketWorld
{
    /// <summary>
    /// IMGUI HUD + settings panel (works identically in WebGL, WebGPU and the Editor).
    /// Layout rules (see <see cref="UiLayout"/>): the minimap sits top-left, the toolbar is a right-aligned row that WRAPS instead of overlapping,
    /// everything centred at the top goes below both, panels hang below the toolbar (wide screens) or fill the screen (narrow ones).
    /// </summary>
    public class DebugHud : MonoBehaviour
    {
        /// <summary>A toolbar button added by another layer (multiplayer's "Online"). Drawn in the same wrapping row.</summary>
        public struct ToolItem { public string label; public System.Action onClick; public System.Func<bool> visible; }
        public static readonly List<ToolItem> Extra = new List<ToolItem>();

        public GameRoot root;
        bool _open;
        float _toast;
        string _toastText = "";
        Vector2 _scroll;
        bool _vehOpen;
        Rect _panel, _report, _vehRect;
        bool _reportOpen;

        // ---- controls (key / gamepad rebinding) panel ----
        Rect _ctlPanel;
        bool _ctlOpen;
        Vector2 _ctlScroll;
        int _listenAction = -1, _listenSlot;          // slot: 0/1 = keyboard, 2 = gamepad
        float[] _listenBase; float _listenUntil;
        int _selRow, _selCol = InputBindings.SlotPad;
        Edge _nUp, _nDown, _nLeft, _nRight, _nOk, _nClear, _nReset, _nBack;

        string _searchQuery = "";
        string _searchStatus = "";
        bool _searching;

        // ---- toolbar flow (right to left, wraps) ----
        readonly List<Rect> _hit = new List<Rect>(16);         // every toolbar rect this frame, so the camera ignores drags that start on them
        readonly Dictionary<string, float> _textW = new Dictionary<string, float>();
        float _fx, _fy, _mmRight, _mmBottom, _barBottom;
        const float RowH = 42f, Gap = 6f;
        GUIStyle _vehStyle, _countStyle, _mapN, _attr;
        float _runStart = -1f, _shownSpeed;

        public void ShowToast(string t) { Toast(t); }

        void Update()
        {
            if (root == null) return;
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
                _vehOpen = false;
                if (_ctlOpen) SetControlsOpen(false);
                if (root.camMenu != null) root.camMenu.Close();
            }
            _open = open;
        }

        /// <summary>True when a screen position (pixels, origin bottom-left) is over HUD, so the camera ignores that drag.</summary>
        public bool Over(Vector2 screenPos)
        {
            if (GameRoot.MenuBlocksInput) return true;
            var p = UiLayout.ToGui(screenPos);
            if (root.carInput != null && root.carInput.TouchControlsActive && CarInput.OverTouchZone(screenPos)) return true;
            if (root.camMenu != null && root.camMenu.Over(screenPos)) return true;
            for (int i = 0; i < _hit.Count; i++) if (_hit[i].Contains(p)) return true;
            if (root.minimap != null && root.minimap.Rect.Contains(p)) return true;
            return (_vehOpen && _vehRect.Contains(p)) || (_ctlOpen && _ctlPanel.Contains(p)) || (_open && _panel.Contains(p)) || (_reportOpen && _report.Contains(p));
        }

        static void Shadowed(Rect r, string text, GUIStyle st)
        {
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f * old.a);
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, st);
            GUI.color = old;
            GUI.Label(r, text, st);
        }

        // ------------------------------------------------------------------------------------------------ toolbar

        float TextW(string s, float min, float pad)
        {
            if (!_textW.TryGetValue(s, out float w)) { w = UiTheme.Chip.CalcSize(new GUIContent(s)).x; _textW[s] = w; }
            return Mathf.Max(min, w + pad);
        }

        void BeginToolbar()
        {
            _hit.Clear();
            var mm = root.minimap != null ? root.minimap.Rect : new Rect(UiLayout.Left, UiLayout.Top, 0, 0);
            _mmRight = mm.xMax + 10f; _mmBottom = mm.yMax + 4f;
            _fx = UiLayout.W - UiLayout.Right; _fy = UiLayout.Top; _barBottom = _fy;
        }

        /// <summary>Next toolbar slot of width <paramref name="w"/>: right to left, wrapping to a new row before it would touch the minimap / left edge.</summary>
        Rect Slot(float w)
        {
            float rowStart = UiLayout.W - UiLayout.Right;
            float minX = _fy < _mmBottom ? _mmRight : UiLayout.Left;
            if (_fx - w < minX && _fx < rowStart - 0.5f)
            {
                _fx = rowStart; _fy += RowH + Gap;
                minX = _fy < _mmBottom ? _mmRight : UiLayout.Left;
            }
            var r = new Rect(_fx - w, _fy, w, RowH);
            _fx -= w + Gap; _hit.Add(r); _barBottom = Mathf.Max(_barBottom, r.yMax);
            return r;
        }

        void DrawToolbar()
        {
            BeginToolbar();
            bool inFpv = root.carCtl != null && root.carCtl.InFpv;
            string curVeh = inFpv ? "FPV Drone" : root.carCtl != null ? Vehicles.NameOf(root.carCtl.CurrentVehicle) : "Car";
            Rect vehRect = default;

            if (UiKit.IconButton(Slot(RowH + 4f), UiTheme.Gear, 26f)) SetOpen(!_open);
            if (GUI.Button(Slot(TextW("Camera", 64f, 22f)), "Camera", UiTheme.Chip) && root.camMenu != null)
            {
                if (_open) SetOpen(false);
                if (_ctlOpen) SetControlsOpen(false);
                _vehOpen = false;
                root.camMenu.Toggle();
            }
            _vehStyle ??= new GUIStyle(UiTheme.Chip) { padding = new RectOffset(12, 30, 0, 0) };
            vehRect = Slot(TextW(curVeh, 70f, 12f + 30f));
            if (GUI.Button(vehRect, curVeh, _vehStyle)) _vehOpen = !_vehOpen;
            if (Event.current.type == EventType.Repaint)
            {
                var old = GUI.color; GUI.color = UiTheme.TextDim;
                GUI.DrawTexture(new Rect(vehRect.xMax - 26f, vehRect.center.y - 8f, 16f, 16f), UiTheme.Chevron);
                GUI.color = old;
            }
            if (GUI.Button(Slot(TextW("Ball", 56f, 22f)), "Ball", UiTheme.Chip)) root.RequestBall();
            if (GUI.Button(Slot(TextW("Controls", 76f, 22f)), "Controls", UiTheme.Chip)) { _vehOpen = false; SetControlsOpen(!_ctlOpen); }
            DrawBotControls();
            for (int i = 0; i < Extra.Count; i++)
            {
                var it = Extra[i];
                if (it.visible != null && !it.visible()) continue;
                if (GUI.Button(Slot(TextW(it.label, 60f, 22f)), it.label, UiTheme.Chip)) it.onClick?.Invoke();
            }

            UiLayout.ToolbarBottom = _barBottom;
            UiLayout.TopBlockBottom = Mathf.Max(_barBottom, _mmBottom) + 8f;
            if (_vehOpen) DrawVehicleMenu(vehRect, inFpv);
        }

        /// <summary>Demolition bots: a Bot button (spawn one / remove all) and -, count, + to add or remove them one at a time.</summary>
        void DrawBotControls()
        {
            int n = root.BotCount; bool can = root.CanUseBots;
            float wBot = TextW("Bot", 56f, 22f);
            var g = Slot(wBot + 4f + 36f + 4f + 52f + 4f + 36f);
            var prev = GUI.enabled;
            var rb = new Rect(g.x, g.y, wBot, g.height);
            GUI.enabled = prev && (can || n > 0);
            if (GUI.Button(rb, "Bot", n > 0 ? UiTheme.ChipOn : UiTheme.Chip)) root.ToggleBot();
            GUI.enabled = prev && n > 0;
            if (GUI.Button(new Rect(rb.xMax + 4f, g.y, 36f, g.height), "-", UiTheme.Chip)) root.RemoveBot();
            GUI.enabled = prev;
            var rc = new Rect(rb.xMax + 44f, g.y, 52f, g.height);
            _countStyle ??= new GUIStyle(GUI.skin.box) { padding = new RectOffset(2, 2, 0, 0), fontSize = 15, wordWrap = false, alignment = TextAnchor.MiddleCenter };
            if (Event.current.type == EventType.Repaint) GUI.Box(rc, n + "/" + root.BotMax, _countStyle);
            GUI.enabled = prev && can && n < root.BotMax;
            if (GUI.Button(new Rect(rc.xMax + 4f, g.y, 36f, g.height), "+", UiTheme.Chip)) root.AddBot();
            GUI.enabled = prev;
        }

        void DrawVehicleMenu(Rect anchor, bool inFpv)
        {
            int n = Vehicles.Order.Length + 1; const float itemH = 40f, itemGap = 2f;
            float w = Mathf.Max(anchor.width, 190f);
            float x = Mathf.Clamp(anchor.xMax - w, UiLayout.Left, UiLayout.W - UiLayout.Right - w), y = anchor.yMax + 10f;
            _vehRect = new Rect(x - 8f, y - 8f, w + 16f, n * (itemH + itemGap) + 14f);
            GUI.Box(_vehRect, GUIContent.none);
            for (int i = 0; i < Vehicles.Order.Length; i++)
            {
                var k = Vehicles.Order[i]; bool cur = root.carCtl != null && !inFpv && root.carCtl.CurrentVehicle == k;
                if (GUI.Button(new Rect(x, y + i * (itemH + itemGap), w, itemH), Vehicles.NameOf(k), cur ? UiTheme.RowOn : UiTheme.Row)) { root.RequestVehicle(k); _vehOpen = false; }
            }
            if (GUI.Button(new Rect(x, y + Vehicles.Order.Length * (itemH + itemGap), w, itemH), "FPV Drone", inFpv ? UiTheme.RowOn : UiTheme.Row)) { root.ToggleFpv(); _vehOpen = false; }
        }

        // ------------------------------------------------------------------------------------------------ in-game readouts

        /// <summary>Plane mode: airspeed (kt), altitude (ft), vertical speed, throttle lever, stall warning.</summary>
        void DrawFlightInstruments(RocketCar cc, float W, float B)
        {
            var p = cc.plane; if (p == null) return;
            Shadowed(new Rect(W * 0.5f - 160, B - 176, 320, 22), $"PLANE   IAS {p.AirspeedKt:0} kt   ALT {p.AltitudeFt:0} ft   VS {p.VerticalSpeedFpm:+0;-0;0} fpm", UiTheme.Unit);
            LeverBar(W, B, p.ThrottleLever);
            if (p.Stalled) Shadowed(new Rect(W * 0.5f - 120, UiLayout.TopBlockBottom + 60f, 240, 30), "STALL", UiTheme.Unit);
        }

        void LeverBar(float W, float B, float lever)
        {
            if (Event.current.type != EventType.Repaint) return;
            var oc = GUI.color; float x = W * 0.5f - 160, y = B - 152;
            GUI.color = new Color(0, 0, 0, 0.45f); GUI.DrawTexture(new Rect(x, y, 320, 6), Texture2D.whiteTexture);
            GUI.color = lever > 0.85f ? UiTheme.Hot : UiTheme.Good; GUI.DrawTexture(new Rect(x, y, 320 * lever, 6), Texture2D.whiteTexture);
            GUI.color = oc;
        }

        /// <summary>Touch widgets of the current vehicle: buttons (lit while held / latched), the throttle lever, the spring sliders and the rudder.</summary>
        void DrawTouchWidgets(float W, float H)
        {
            var ws = CarInput.Widgets; var cc = root.carCtl; var oc = GUI.color;
            float k = UiLayout.K;
            for (int i = 0; i < ws.Length; i++)
            {
                var w = ws[i];
                var r = new Rect(w.zone.x * Screen.width / k, (1f - w.zone.y - w.zone.height) * Screen.height / k, w.zone.width * Screen.width / k, w.zone.height * Screen.height / k);
                bool on = root.carInput.WidgetOn(i);
                string label = w.label;
                if (cc != null && cc.InFpv && cc.fpv != null)       // drone buttons show the live state
                {
                    if (w.id == "arm") { on = cc.fpv.Armed; label = on ? "DISARM" : cc.fpv.Crashed ? "CRASHED" : "ARM"; }
                    else if (w.id == "mode") label = "MODE\n" + FpvModes.Name(cc.fpv.Mode);
                }
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
                            GUI.color = new Color(UiTheme.Accent.r, UiTheme.Accent.g, UiTheme.Accent.b, touched ? 0.60f : 0.38f);
                            if (w.kind == WidgetKind.SliderSticky) { float hh = r.height * Mathf.Clamp01(v); GUI.DrawTexture(new Rect(r.x + 4, r.yMax - hh - 3, r.width - 8, Mathf.Max(hh, 3)), Texture2D.whiteTexture); }
                            else if (w.kind == WidgetKind.SliderSpring) { float cy = r.center.y, hy = -v * r.height * 0.5f; GUI.DrawTexture(new Rect(r.x + 4, Mathf.Min(cy, cy + hy), r.width - 8, Mathf.Max(Mathf.Abs(hy), 3)), Texture2D.whiteTexture); GUI.color = new Color(1, 1, 1, 0.5f); GUI.DrawTexture(new Rect(r.x, cy - 1, r.width, 2), Texture2D.whiteTexture); }
                            else { float cx = r.center.x, hx = v * r.width * 0.5f; GUI.DrawTexture(new Rect(Mathf.Min(cx, cx + hx), r.y + 4, Mathf.Max(Mathf.Abs(hx), 3), r.height - 8), Texture2D.whiteTexture); GUI.color = new Color(1, 1, 1, 0.5f); GUI.DrawTexture(new Rect(cx - 1, r.y, 2, r.height), Texture2D.whiteTexture); }
                            GUI.color = oc;
                        }
                        GUI.Label(new Rect(r.x - 12f, r.y + (w.kind == WidgetKind.SliderH ? 2 : -20), r.width + 24f, 18), w.label, UiTheme.Unit);
                        break;
                    }
                    default:
                        if (on) GUI.color = UiTheme.Accent;
                        GUI.Box(r, label, UiTheme.Touch);
                        GUI.color = oc;
                        break;
                }
            }
        }

        /// <summary>Vehicle mode (F-35B, F-15E, helicopter, DeLorean): speed, altitude, vertical speed, status line, throttle lever.</summary>
        void DrawVehicleInstruments(RocketCar cc, float W, float B)
        {
            var d = cc.vehicle; if (d == null || !d.Active) return;
            Shadowed(new Rect(W * 0.5f - 200, B - 176, 400, 22), $"{d.Spec.name.ToUpperInvariant()}   ALT {d.AltM:0} m   VS {d.VsMs:+0.0;-0.0;0.0} m/s", UiTheme.Unit);
            if (!string.IsNullOrEmpty(d.Status)) Shadowed(new Rect(W * 0.5f - 200, B - 198, 400, 22), d.Status, UiTheme.Unit);
            LeverBar(W, B, d.Lever);
            if (d.Stalled) Shadowed(new Rect(W * 0.5f - 120, UiLayout.TopBlockBottom + 60f, 240, 30), "STALL", UiTheme.Unit);
        }

        /// <summary>Baseline for the centre-bottom readouts. On a narrow touch screen the sticks and buttons fill the bottom, so lift them above.</summary>
        float ReadoutBase(float H, bool touch)
        {
            float lift = 0f;
            if (touch && UiLayout.Compact)
            {
                float k = UiLayout.K, lo = (UiLayout.W * 0.5f - 130f) * k, hi = (UiLayout.W * 0.5f + 130f) * k;
                var sr = CarInput.StickRect;
                if (sr.xMax > lo && sr.xMin < hi) lift = Mathf.Max(lift, sr.yMax / k);
                var ws = CarInput.Widgets;
                for (int i = 0; i < ws.Length; i++)
                {
                    var z = ws[i].zone;
                    if (z.xMax * Screen.width > lo && z.xMin * Screen.width < hi) lift = Mathf.Max(lift, z.yMax * Screen.height / k);
                }
                lift += 10f;
            }
            return H - Mathf.Max(lift, UiLayout.Bottom - 12f);
        }

        void DrawSpeed(float W, float B)
        {
            var cc = root.carCtl;
            _shownSpeed = Mathf.Lerp(_shownSpeed, cc.SpeedMS, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 12f));
            float frac = Mathf.Clamp01(_shownSpeed / 46f);
            bool boosting = cc.IsBoosting;
            if (cc.InBoat) Shadowed(new Rect(W * 0.5f - 120, B - 150, 240, 20), "BOAT", UiTheme.Unit);
            if (cc.InPlane) DrawFlightInstruments(cc, W, B);
            else if (cc.InVehicle) DrawVehicleInstruments(cc, W, B);
            if (cc.HyperT > 0.05f) Shadowed(new Rect(W * 0.5f - 160, B - 222, 320, 24), $"HYPER BOOST  x{cc.HyperMul:0.#}", UiTheme.Unit);
            Shadowed(new Rect(W * 0.5f - 120, B - 128, 240, 66), Mathf.RoundToInt(_shownSpeed * 3.6f).ToString(), UiTheme.Big);
            Shadowed(new Rect(W * 0.5f - 120, B - 62, 240, 20), "KM/H", UiTheme.Unit);
            if (Event.current.type != EventType.Repaint) return;
            var track = new Rect(W * 0.5f - 110, B - 38, 220, 6);
            GUI.DrawTexture(track, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, new Color(1, 1, 1, 0.16f), 0f, 3f);
            if (frac > 0.01f)
                GUI.DrawTexture(new Rect(track.x, track.y, Mathf.Max(6f, track.width * frac), track.height), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                    boosting ? UiTheme.Hot : Color.Lerp(UiTheme.Accent, Color.white, frac * 0.35f), 0f, 3f);
        }

        // ------------------------------------------------------------------------------------------------ OnGUI

        void OnGUI()
        {
            if (root == null) return;
            GUI.skin = UiTheme.Skin;
            UiLayout.Begin();
            float W = UiLayout.W, H = UiLayout.H;
            _mapN ??= new GUIStyle(GUI.skin.label) { font = UiTheme.Bold, fontSize = 13, alignment = TextAnchor.MiddleCenter, wordWrap = false, normal = { textColor = UiTheme.Accent } };
            _attr ??= new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.LowerCenter, wordWrap = false };
            var s = root.settings;
            _panel = UiLayout.Panel(380f);
            _ctlPanel = UiLayout.Panel(520f);

            bool running = root.carCtl.CurrentPhase == RocketCar.Phase.Running;
            if (running && _runStart < 0f) _runStart = Time.unscaledTime;
            bool touch = root.carInput != null && root.carInput.TouchControlsActive;
            bool camOpen = root.camMenu != null && root.camMenu.Open;
            bool sheet = UiLayout.Compact && (_open || _ctlOpen || camOpen);     // full-screen panels hide everything under them

            if (Event.current.type == EventType.Repaint)
                GUI.DrawTexture(new Rect(0, 0, W, H), UiTheme.Vignette, ScaleMode.StretchToFill, true);

            if (!sheet)
            {
                if (root.minimap != null) root.minimap.Draw(_mapN);
                if (root.waterMap != null)       // OpenStreetMap's licence asks for attribution wherever its data is used
                {
                    var oc = GUI.color; GUI.color = new Color(1f, 1f, 1f, 0.50f);
                    GUI.Label(new Rect(0, H - 20, W, 18), "Water data \u00a9 OpenStreetMap contributors \u00b7 OpenFreeMap", _attr);
                    GUI.color = oc;
                }

                float B = ReadoutBase(H, touch);
                if (running && !root.carCtl.InFpv) DrawSpeed(W, B);       // the drone has its own telemetry line, and its buttons use that spot
                if (touch) DrawSticks(H);

                DrawToolbar();

                // centred notices, stacked under the toolbar / minimap (and under the multiplayer banner, if any)
                float ny = UiLayout.TopBlockBottom + UiLayout.ExtraTop, nw = UiLayout.BannerWidth(420f);
                if (!s.HasCredentials) { GUI.Box(new Rect((W - nw) * 0.5f, ny, nw, 34), "Test ground: world streaming is off in this build", UiTheme.Pill); ny += 40f; }
                if (!touch && running && W > 1100f)
                {
                    // controls cheat-sheet: shown for the first seconds of play, then fades away
                    float t = Time.unscaledTime - _runStart, a = t < 12f ? 1f : Mathf.Clamp01(1f - (t - 12f) / 2f);
                    if (a > 0.01f)
                    {
                        var oc = GUI.color; GUI.color = new Color(1, 1, 1, a);
                        GUI.Box(new Rect(W * 0.5f - 350, ny, 700, 36), "WASD drive  \u00b7  Space jump  \u00b7  Shift boost  \u00b7  Ctrl drift  \u00b7  C camera  \u00b7  R rescue  \u00b7  B ball", UiTheme.Pill);
                        GUI.color = oc; ny += 42f;
                    }
                }
                if (Time.unscaledTime < _toast) { float tw = UiLayout.BannerWidth(420f); GUI.Box(new Rect((W - tw) * 0.5f, ny, tw, 44), _toastText, UiTheme.Toast); }
                if (!string.IsNullOrEmpty(root.carCtl.Status))
                {
                    float sw = UiLayout.BannerWidth(400f);
                    GUI.Box(new Rect((W - sw) * 0.5f, H * 0.40f, sw, 44), root.carCtl.Status, UiTheme.Toast);
                }
                if (_reportOpen && root.benchmark != null) DrawReport(W, H);
            }

            if (_ctlOpen) DrawControls();
            if (_open) DrawSettings();

            // closes the vehicle menu on any tap that nothing else wanted (drawn last = lowest priority, and it draws nothing)
            if (_vehOpen && !sheet && GUI.Button(new Rect(0, 0, W, H), GUIContent.none, GUIStyle.none)) _vehOpen = false;
            if (sheet) { _vehOpen = false; _hit.Clear(); }
        }

        /// <summary>Analog stick(s) of the touch layout.</summary>
        void DrawSticks(float H)
        {
            float k = UiLayout.K;
            var sr = CarInput.StickRect;
            var box = new Rect(sr.x / k, (Screen.height - sr.yMax) / k, sr.width / k, sr.height / k);
            DrawTouchWidgets(UiLayout.W, H);
            GUI.Box(box, "", UiTheme.Touch);
            var v = root.carInput.StickValue;
            float ks = box.width * 0.30f;
            var kc = box.center + new Vector2(v.x * box.width * 0.5f, -v.y * box.height * 0.5f);
            var oc = GUI.color; GUI.color = v.sqrMagnitude > 0.01f ? UiTheme.Accent : new Color(1, 1, 1, 0.9f);
            GUI.Box(new Rect(kc.x - ks * 0.5f, kc.y - ks * 0.5f, ks, ks), "", UiTheme.Touch);
            GUI.color = oc;
            if (CarInput.Profile != TouchProfile.Drone) return;

            // right stick (pitch / roll) and the throttle bar beside the left stick (the throttle stays where the thumb left it)
            var rr = CarInput.RightStickRect;
            var rbox = new Rect(rr.x / k, (Screen.height - rr.yMax) / k, rr.width / k, rr.height / k);
            GUI.Box(rbox, "", UiTheme.Touch);
            var rv = root.carInput.RightStickValue;
            var rkc = rbox.center + new Vector2(rv.x * rbox.width * 0.5f, -rv.y * rbox.height * 0.5f);
            GUI.Box(new Rect(rkc.x - ks * 0.5f, rkc.y - ks * 0.5f, ks, ks), "", UiTheme.Touch);
            GUI.Label(new Rect(box.x, box.y - 18, box.width, 18), "THROTTLE / YAW", UiTheme.Unit);
            GUI.Label(new Rect(rbox.x, rbox.y - 18, rbox.width, 18), "PITCH / ROLL", UiTheme.Unit);
            if (Event.current.type == EventType.Repaint && root.carCtl.fpv != null)
            {
                var oc2 = GUI.color; float t = root.carCtl.fpv.Throttle01;
                var bar = new Rect(box.xMax + 6, box.y, 8, box.height);
                GUI.color = new Color(1, 1, 1, 0.16f); GUI.DrawTexture(bar, Texture2D.whiteTexture);
                GUI.color = UiTheme.Accent; GUI.DrawTexture(new Rect(bar.x, bar.yMax - bar.height * t, bar.width, Mathf.Max(2f, bar.height * t)), Texture2D.whiteTexture);
                GUI.color = oc2;
            }
        }

        // ------------------------------------------------------------------------------------------------ settings panel

        void DrawSettings()
        {
            var s = root.settings;
            bool changed = false;
            GUILayout.BeginArea(_panel, GUI.skin.box);
            if (UiKit.Header("Settings")) { GUILayout.EndArea(); SetOpen(false); return; }
            _scroll = GUILayout.BeginScrollView(_scroll);

            UiKit.Section("Location");
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("rw_loc_search");
            _searchQuery = GUILayout.TextField(_searchQuery ?? "", GUILayout.MinWidth(120), GUILayout.Height(40));
            bool go = GUILayout.Button(_searching ? "..." : "Go", UiTheme.Primary, GUILayout.Width(64), GUILayout.Height(40));
            GUILayout.EndHorizontal();
            if (go && !_searching && !string.IsNullOrWhiteSpace(_searchQuery)) StartCoroutine(SearchAndTeleport(_searchQuery.Trim()));
            // Enter key while the search field is focused
            if (!_searching && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return
                && GUI.GetNameOfFocusedControl() == "rw_loc_search" && !string.IsNullOrWhiteSpace(_searchQuery))
            {
                StartCoroutine(SearchAndTeleport(_searchQuery.Trim()));
                Event.current.Use();
            }
            GUILayout.Label(string.IsNullOrEmpty(_searchStatus) ? "Type a city, address or landmark." : _searchStatus, UiTheme.Muted);

            UiKit.Section("Priorities");
            {
                var pr = s.priorities; var np = pr;
                np = PrioToggle(np, Priority.FastLoad, "Fast tile loading", "Coarse tiles first, more downloads at once, a tighter full-detail ring.");
                np = PrioToggle(np, Priority.TileQuality, "Tile quality", "Sharper tiles, near and far.");
                np = PrioToggle(np, Priority.Fps, "Frame rate", "Lower resolution, view distance and effects.");
                np = PrioToggle(np, Priority.Realism, "Realism", "Best of everything the device allows, effects at full strength.");
                if (np != pr) { s.priorities = np; changed = true; }
                GUILayout.Label(np == Priority.None ? "Off: only your sliders below apply." : "Applied on top of your sliders. Opposites (quality vs frame rate) partly cancel. Now: SSE " + root.UserSse.ToString("0") + ", scale " + root.UserScale.ToString("0.00") + "x, view " + (PriorityLogic.Far(s.viewDistance, root.Mods) / 1000f).ToString("0.#") + " km.", UiTheme.Muted);
            }

            UiKit.Section("Graphics");
            s.sse = UiKit.Slider("Tile detail", "SSE " + s.sse.ToString("0"), s.sse, 4f, 64f);
            GUILayout.Label("Lower is sharper but needs more data.", UiTheme.Muted);
            UiKit.Gap(4);
            s.viewDistance = UiKit.Slider("View distance", (s.viewDistance / 1000f).ToString("0.#") + " km", s.viewDistance, 400f, 20000f);
            UiKit.Gap(4);
            float maxScale = DeviceProfile.MaxRenderScale;
            s.renderScale = UiKit.Slider("Render scale", s.renderScale.ToString("0.00") + "x", Mathf.Min(s.renderScale, maxScale), 0.3f, maxScale);
            UiKit.Gap(6);
            bool ac = UiKit.Toggle(s.antiCrash, "Anti-crash mode", "Adapts quality to hold 30+ FPS; your sliders are the maximum. iPhone / iPad use the low-memory build (reload after changing).");
            if (ac != s.antiCrash) { s.antiCrash = ac; WebBridge.SetAntiCrash(ac); changed = true; }
            if (WebBridge.IsLiteBuild()) GUILayout.Label("Low-memory build active", UiTheme.Muted);

            UiKit.Section("Camera and effects");
            if (UiKit.Button("Open camera menu")) { SetOpen(false); root.camMenu.OpenMenu(); }
            UiKit.Gap(6);
            s.fxDof = UiKit.Slider("Depth of field", (s.fxDof * 100f).ToString("0") + "%", s.fxDof, 0f, 2f);
            UiKit.Gap(4);
            s.fxMotion = UiKit.Slider("Motion blur", (s.fxMotion * 100f).ToString("0") + "%", s.fxMotion, 0f, 2f);
            GUILayout.Label("0% = off, 100% = the designed look.", UiTheme.Muted);
            if (root.cinematic != null && !string.IsNullOrEmpty(root.cinematic.Status)) GUILayout.Label(root.cinematic.Status, UiTheme.Muted);

            UiKit.Section("Car colour");
            if (DrawSwatches(s)) changed = true;

            UiKit.Section("About");
            GUILayout.Label("Settings are saved in this browser.", UiTheme.Muted);
            GUILayout.Label("Aircraft model: \"YC-130\" by KOG_THORNS, CC BY 4.0.", UiTheme.Muted);
            GUILayout.Label(DeviceProfile.Describe() + (root.memGuard != null && root.memGuard.HeapMB > 0 ? " · heap " + root.memGuard.HeapMB + " MB" + (root.memLevel > 0 ? " (reducing detail, level " + root.memLevel + ")" : "") : ""), UiTheme.Muted);
            if (root.roads != null && root.roads.Enabled)
            {
                UiKit.Section("Saved zones");
                float zr = SavedZones.DefaultRadius;
                int tiles = root.roads.EstimateZoneTiles(zr);
                zr = UiKit.Slider("Zone radius", (zr / 1000f).ToString("0.0#") + " km" + (tiles > 0 ? " · " + tiles + " tiles" : ""), zr, SavedZones.MinRadius, SavedZones.MaxRadius);
                SavedZones.DefaultRadius = zr;
                GUILayout.Label("Roads inside the zone are downloaded and kept for offline use.", UiTheme.Muted);
                UiKit.Gap(4);
                GUI.enabled = root.roads.CanSaveZone && SavedZones.All.Count < SavedZones.MaxZones;
                if (UiKit.PrimaryButton("Save zone here")) root.roads.SaveZoneHere(null);
                GUI.enabled = true;
                if (SavedZones.All.Count > 0) GUILayout.Label(SavedZones.All.Count + " saved - " + root.roads.ZoneProgress(), UiTheme.Muted);
                GUILayout.Label(root.roads.Info + "   (keyboard: F9)", UiTheme.Muted);
            }
            GUILayout.Space(8);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            UiKit.TouchScroll(_panel, ref _scroll);

            if (GUI.changed || changed) root.ApplyLive();
        }

        static Priority PrioToggle(Priority cur, Priority flag, string label, string hint)
        {
            bool on = UiKit.Toggle((cur & flag) != 0, label, hint);
            return on ? cur | flag : cur & ~flag;
        }

        /// <summary>Round colour chips; the current one gets an accent ring. True when the choice changed.</summary>
        bool DrawSwatches(GameSettings s)
        {
            bool changed = false;
            GUILayout.BeginHorizontal();
            for (int i = 0; i < GameSettings.CarColors.Length; i++)
            {
                var r = GUILayoutUtility.GetRect(40, 40, GUILayout.Width(40), GUILayout.Height(40));
                if (Event.current.type == EventType.Repaint)
                {
                    var c = r; c.x += 3; c.y += 3; c.width -= 6; c.height -= 6;
                    GUI.DrawTexture(c, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, GameSettings.CarColors[i].color, 0f, c.width * 0.5f);
                    if (s.carColor == i) GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, UiTheme.Accent, 3f, r.width * 0.5f);
                }
                if (GUI.Button(r, GUIContent.none, GUIStyle.none) && s.carColor != i) { s.carColor = i; changed = true; }
                if (i < GameSettings.CarColors.Length - 1) GUILayout.Space(4);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(GameSettings.CarColors[Mathf.Clamp(s.carColor, 0, GameSettings.CarColors.Length - 1)].name, UiTheme.Muted);
            return changed;
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
            // column widths adapt to the panel, so the table fits a phone as well as a desktop window
            float aw = _ctlPanel.width - 56f;
            float lw = Mathf.Clamp(aw * 0.28f, 84f, 150f), kw = Mathf.Max(52f, (aw - lw - 12f) / 3.3f), pw = kw * 1.3f;

            GUILayout.BeginArea(_ctlPanel, GUI.skin.box);
            if (UiKit.Header("Controls")) { GUILayout.EndArea(); SetControlsOpen(false); return; }
            string pad = InputDevices.PadName();
            GUILayout.Label(pad != null ? "Gamepad: " + pad : "No gamepad detected. Connect one and press any button.", UiTheme.Muted);
            if (pad != null) GUILayout.Label("Pressing now: " + (InputDevices.ActivePadInputs() is var act && act.Length > 0 ? act : "none"), UiTheme.Muted);
            GUILayout.Label("Tap a slot, then press the key or button you want. Esc cancels, Backspace clears. Right stick orbits the camera; left stick up/down pitches in the air.", UiTheme.Muted);
            _ctlScroll = GUILayout.BeginScrollView(_ctlScroll);
            GUILayout.BeginHorizontal();
            GUILayout.Label("", GUILayout.Width(lw));
            GUILayout.Label("Key", UiTheme.Hint, GUILayout.Width(kw)); GUILayout.Label("Alt key", UiTheme.Hint, GUILayout.Width(kw)); GUILayout.Label("Gamepad", UiTheme.Hint, GUILayout.Width(pw));
            GUILayout.EndHorizontal();
            for (int i = 0; i < InputBindings.Actions.Length; i++)
            {
                var a = InputBindings.Actions[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label(InputBindings.Label(a), UiTheme.Body, GUILayout.Width(lw));
                for (int slot = 0; slot < InputBindings.SlotCount; slot++)
                {
                    bool locked = slot < InputBindings.KeySlots && InputBindings.KeyboardLocked(a);
                    bool listening = _listenAction == i && _listenSlot == slot;
                    string text = listening ? "press..." : locked ? (slot == 0 ? "Esc" : "Tab")
                        : slot == InputBindings.SlotPad ? InputBindings.PadLabel(ib.GetPad(a)) : InputBindings.KeyLabel(ib.GetKey(a, slot));
                    var slotStyle = listening ? UiTheme.BtnListen : (i == _selRow && slot == _selCol && InputDevices.HasGamepad) ? UiTheme.BtnSelect : GUI.skin.button;
                    GUI.enabled = !locked;
                    if (GUILayout.Button(text, slotStyle, GUILayout.Width(slot == InputBindings.SlotPad ? pw : kw), GUILayout.Height(34))) StartListening(i, slot);
                    GUI.enabled = true;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (UiKit.Button("Reset to defaults")) { ib.ResetAll(); ib.Save(); _listenAction = -1; Toast("Controls reset to defaults"); }
            if (UiKit.PrimaryButton("Done")) SetControlsOpen(false);
            GUILayout.EndHorizontal();
            if (InputDevices.HasGamepad)
                GUILayout.Label("Gamepad: D-pad / left stick move · A bind · X clear · Y reset all · B / Menu close", UiTheme.Muted);
            GUILayout.EndArea();
            UiKit.TouchScroll(_ctlPanel, ref _ctlScroll);
        }

        // ------------------------------------------------------------------------------------------------ search

        IEnumerator SearchAndTeleport(string query)
        {
            _searching = true;
            _searchStatus = "Searching...";
            // Nominatim (OpenStreetMap): free, no API key. Identify the app per their usage policy.
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
                // Root array: wrap for JsonUtility
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
                _searchStatus = "Looking up elevation...";
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
                if (name.Length > 60) name = name.Substring(0, 57) + "...";
                _searchStatus = "Going to " + name;
                root.TeleportTo(lat, lon, gh);
                Toast("Teleported");
            }
            _searching = false;
        }

        [System.Serializable] class ElevationResponse { public float[] elevation; }
        [System.Serializable] class NominatimList { public NominatimItem[] items; }
        [System.Serializable] class NominatimItem { public string lat; public string lon; public string display_name; }

        void DrawReport(float W, float H)
        {
            float w = Mathf.Min(640f, W - UiLayout.Left - UiLayout.Right);
            _report = new Rect(UiLayout.Left, H - UiLayout.Bottom - 230f, w, 215f);
            GUILayout.BeginArea(_report, GUI.skin.box);
            GUILayout.Label("Benchmark report", UiTheme.Title);
            GUILayout.Label(root.benchmark.Report, UiTheme.Muted);
            GUILayout.FlexibleSpace();
            if (UiKit.Button("Close", 36f)) _reportOpen = false;
            GUILayout.EndArea();
        }
    }
}
