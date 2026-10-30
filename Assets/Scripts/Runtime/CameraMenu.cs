using RocketWorld.Sim;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// The Camera menu: camera mode, the six offsets (horizontal / vertical turn, up / down, side, field of view, front / back), the
    /// follow-roll toggle and the follow-delay slider, with OK / Save / Last Save / Reset Defaults. Everything applies live so the view can be
    /// judged while editing (the panel is translucent and does not pause the game). Values live in <see cref="CamTuning"/>.
    /// </summary>
    public class CameraMenu : MonoBehaviour
    {
        public GameRoot root;
        public bool Open { get; private set; }

        const float PW = 600f, PH = 470f;                       // panel size in design units
        const float HoldAfter = 0.35f;

        struct Row { public string label, unit; public float tap, perSec, limit; }
        static readonly Row[] Rows =
        {
            new Row { label = "Horizontal Turn", unit = "°", tap = 2f,   perSec = 30f, limit = CamTunePolicy.MaxTurnH },
            new Row { label = "Vertical Turn",   unit = "°", tap = 2f,   perSec = 30f, limit = CamTunePolicy.MaxTurnV },
            new Row { label = "Up/Down",         unit = " m", tap = 0.5f, perSec = 8f,  limit = CamTunePolicy.MaxMove },
            new Row { label = "Side position",   unit = " m", tap = 0.5f, perSec = 8f,  limit = CamTunePolicy.MaxMove },
            new Row { label = "Field of View",   unit = "°", tap = 1f,   perSec = 20f, limit = CamTunePolicy.MaxFov },
            new Row { label = "Front/Back",      unit = " m", tap = 0.5f, perSec = 8f,  limit = CamTunePolicy.MaxMove },
        };

        float Get(int i)
        {
            switch (i) { case 0: return CamTuning.hTurn; case 1: return CamTuning.vTurn; case 2: return CamTuning.up; case 3: return CamTuning.side; case 4: return CamTuning.fov; default: return CamTuning.front; }
        }
        void Set(int i, float v)
        {
            switch (i) { case 0: CamTuning.hTurn = v; break; case 1: CamTuning.vTurn = v; break; case 2: CamTuning.up = v; break; case 3: CamTuning.side = v; break; case 4: CamTuning.fov = v; break; default: CamTuning.front = v; break; }
        }

        int _holdRow = -1, _holdDir; float _holdStart;
        string _msg = ""; float _msgUntil;
        float _scale = 1f; Vector2 _origin;
        Texture2D _circle;
        GUIStyle _label, _value, _plus, _btn, _title, _toggle;

        public void Toggle() { if (Open) Close(); else OpenMenu(); }
        public void OpenMenu() { Open = true; _holdRow = -1; }
        public void Close() { Open = false; _holdRow = -1; if (root != null) root.SaveSettings(); }

        void OnApplicationFocus(bool focus) { if (!focus) _holdRow = -1; }

        void ComputeLayout()
        {
            float fit = Mathf.Min((Screen.height - 16f) / PH, (Screen.width - 16f) / PW);
            _scale = Mathf.Max(0.4f, Mathf.Min(Mathf.Clamp(Screen.height / 720f, 1f, 2f), fit));
            _origin = new Vector2((Screen.width - PW * _scale) * 0.5f, (Screen.height - PH * _scale) * 0.5f);
        }

        /// <summary>True when a screen position (pixels, origin bottom-left) is over the open panel, so the camera ignores that drag.</summary>
        public bool Over(Vector2 screenPos)
        {
            if (!Open) return false;
            ComputeLayout();
            var p = new Vector2((screenPos.x - _origin.x) / _scale, ((Screen.height - screenPos.y) - _origin.y) / _scale);
            return new Rect(0, 0, PW, PH).Contains(p);
        }

        void Update()
        {
            if (!Open) { _holdRow = -1; return; }
            if (_holdRow >= 0)
            {
                if (GUIUtility.hotControl == 0) { _holdRow = -1; return; }     // the release happened somewhere we did not see it
                var r = Rows[_holdRow];
                float heldFor = Time.unscaledTime - _holdStart;
                Set(_holdRow, CamTunePolicy.HoldStep(Get(_holdRow), _holdDir, heldFor, Mathf.Min(Time.unscaledDeltaTime, 0.1f), r.perSec, HoldAfter, r.limit));
            }
        }

        // ------------------------------------------------------------------------------------------------- drawing
        void EnsureStyles()
        {
            if (_circle == null) _circle = MakeCircle(128);
            var font = UiTheme.Bold;
            _label ??= new GUIStyle(GUI.skin.label) { font = font, fontSize = 19, alignment = TextAnchor.MiddleCenter, wordWrap = false, normal = { textColor = Color.white } };
            _value ??= new GUIStyle(_label) { fontSize = 15, normal = { textColor = new Color(0.75f, 0.88f, 1f, 1f) } };
            _title ??= new GUIStyle(_label) { fontSize = 24, alignment = TextAnchor.MiddleLeft };
            _plus ??= new GUIStyle(GUI.skin.label) { font = font, fontSize = 38, alignment = TextAnchor.MiddleCenter, normal = { background = null, textColor = Color.white } };
            _btn ??= new GUIStyle(GUI.skin.label) { font = font, fontSize = 20, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.15f, 0.15f, 0.18f) } };
            _toggle ??= new GUIStyle(GUI.skin.toggle) { font = font, fontSize = 17, wordWrap = true, normal = { textColor = Color.white }, onNormal = { textColor = Color.white }, hover = { textColor = Color.white }, onHover = { textColor = Color.white } };
        }

        void OnGUI()
        {
            if (!Open || root == null) return;
            GUI.depth = -100;                                                   // in front of the HUD, first to get the clicks
            GUI.skin = UiTheme.Skin;
            EnsureStyles();
            ComputeLayout();
            var oldM = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(new Vector3(_origin.x, _origin.y, 0f), Quaternion.identity, new Vector3(_scale, _scale, 1f));
            var e = Event.current;
            var panel = new Rect(0, 0, PW, PH);

            if (e.type == EventType.Repaint)
            {
                GUI.DrawTexture(new Rect(-6, -6, PW + 12, PH + 12), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, new Color(0f, 0f, 0f, 0.55f), 0f, 10f);
                GUI.DrawTexture(panel, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, new Color(0.04f, 0.06f, 0.62f, 0.80f), 0f, 6f);
            }

            GUI.Label(new Rect(24, 8, 300, 34), "Camera", _title);
            if (RoundBtn(new Rect(PW - 52, 8, 38, 34), "X", new Color(0.93f, 0.22f, 0.22f))) Close();

            // camera mode
            string[] names = { "Chase", "Helicopter", "Orbit" }; int[] modes = { 0, 2, 1 };
            for (int i = 0; i < 3; i++)
            {
                bool cur = root.cameraMode == modes[i];
                if (RoundBtn(new Rect(30 + i * 185, 46, 170, 34), names[i], cur ? UiTheme.Accent : Color.white)) root.ChooseCamera(modes[i]);
            }

            // six offsets: 3 columns x 2 rows, like the reference layout
            for (int i = 0; i < Rows.Length; i++)
            {
                int col = i % 3, rw = i / 3;
                float cx = 100f + col * 200f, y = 92f + rw * 114f;
                GUI.Label(new Rect(cx - 95, y, 190, 26), Rows[i].label, _label);
                float v = Get(i);
                GUI.Label(new Rect(cx - 95, y + 22, 190, 20), v.ToString("+0.#;-0.#;0") + Rows[i].unit, _value);
                Circle(new Rect(cx - 70, y + 46, 56, 56), "+", i, +1);
                Circle(new Rect(cx + 14, y + 46, 56, 56), "-", i, -1);
            }

            // follow mode + delay
            CamTuning.fullAxis = GUI.Toggle(new Rect(30, 322, 540, 26), CamTuning.fullAxis, "Follow roll too (yaw + pitch + roll). Off = yaw and pitch only", _toggle);
            GUI.Label(new Rect(30, 352, 540, 24), CamTuning.delay <= 0.02f ? "Follow delay: none (rigid)" : $"Follow delay: {CamTuning.delay:0.00}x  (1.00 = default)", _label);
            CamTuning.delay = CamTunePolicy.ClampDelay(GUI.HorizontalSlider(new Rect(40, 382, 520, 20), CamTuning.delay, 0f, CamTunePolicy.MaxDelay));

            // bottom buttons
            var white = Color.white;
            if (RoundBtn(new Rect(24, 416, 124, 40), "OK", white)) Close();
            if (RoundBtn(new Rect(160, 416, 124, 40), "Save", white)) { CamTuning.Save(); Msg("Saved"); }
            if (RoundBtn(new Rect(296, 416, 140, 40), "Last Save", white)) { CamTuning.LoadSaved(); Msg(CamTuning.HasSaved ? "Last save loaded" : "Nothing saved yet - defaults"); }
            if (RoundBtn(new Rect(448, 416, 130, 40), "Reset Defaults", white)) { CamTuning.ResetDefaults(); Msg("Defaults"); }
            if (Time.unscaledTime < _msgUntil) GUI.Label(new Rect(24, 398, PW - 48, 18), _msg, _value);

            // swallow clicks on the empty panel so they never reach the HUD / game below
            if (e.type == EventType.MouseDown && panel.Contains(e.mousePosition)) e.Use();
            GUI.matrix = oldM;
        }

        void Msg(string t) { _msg = t; _msgUntil = Time.unscaledTime + 2f; }

        /// <summary>Round +/- button: a tap steps once, holding repeats (see Update).</summary>
        bool Circle(Rect r, string text, int row, int dir)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive, r);
            var e = Event.current; bool pressed = false;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (r.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = id; e.Use(); pressed = true;
                        var rw = Rows[row];
                        Set(row, CamTunePolicy.TapStep(Get(row), dir, rw.tap, rw.limit));
                        _holdRow = row; _holdDir = dir; _holdStart = Time.unscaledTime;
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; _holdRow = -1; e.Use(); }
                    break;
                case EventType.Repaint:
                    bool down = GUIUtility.hotControl == id;
                    var oc = GUI.color; GUI.color = down ? new Color(0.75f, 0.85f, 1f, 1f) : Color.white;
                    GUI.DrawTexture(r, _circle);
                    GUI.color = oc;
                    GUI.Label(r, text, _plus);
                    break;
            }
            return pressed;
        }

        /// <summary>White rounded button (tinted by <paramref name="tint"/>); true on click.</summary>
        bool RoundBtn(Rect r, string text, Color tint)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive, r);
            var e = Event.current; bool clicked = false;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (r.Contains(e.mousePosition)) { GUIUtility.hotControl = id; e.Use(); }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; clicked = r.Contains(e.mousePosition); e.Use(); }
                    break;
                case EventType.Repaint:
                    bool down = GUIUtility.hotControl == id;
                    var col = down ? tint * 0.82f : tint; col.a = 1f;
                    GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, col, 0f, 8f);
                    GUI.Label(r, text, _btn);
                    break;
            }
            return clicked;
        }

        // ------------------------------------------------------------------------------------------------- textures
        /// <summary>Dark disc with a white ring and a thin dark outline (the +/- buttons).</summary>
        static Texture2D MakeCircle(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[n * n]; float c = (n - 1) * 0.5f, R = n * 0.5f - 1f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / R;      // 0 centre .. 1 edge
                    float a = Mathf.Clamp01((1f - d) * R);                                  // 1 px anti-aliased edge
                    Color col = d < 0.80f ? new Color(0.10f, 0.10f, 0.11f) : d < 0.92f ? Color.white : new Color(0.12f, 0.12f, 0.13f);
                    col.a = a; px[y * n + x] = col;
                }
            t.SetPixels32(px); t.Apply(false, true); return t;
        }
    }
}
