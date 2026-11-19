using UnityEngine;

namespace RocketWorld.Net
{
    /// <summary>
    /// Start menu in the style of Rocket League's main menu (Jugar solo / Crear partida / Unirse a partida / Ver tutorial), the join code in the ceiling of the screen, scoreboard and name tags.
    /// IMGUI like the rest of the HUD, drawn above it (GUI.depth).
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class NetUi : MonoBehaviour
    {
        public static string LocalName = "Jugador";

        enum Scr { Main, Create, Join }
        NetSession _ns;
        bool _menu = true;
        Scr _scr = Scr.Main;
        string _code = "";
        float _toast; string _toastText = "";
        GUIStyle _tag, _err;
        bool _registered;
        GameRoot Root() { return _ns.Root; }

        void Awake()
        {
            LocalName = PlayerPrefs.GetString("rw.name", "Jugador" + Random.Range(100, 999));
        }

        void Update()
        {
            if (_ns == null) _ns = GetComponent<NetSession>();
            if (_ns == null) return;
            if (_ns.ReturnToMenu) { _ns.ReturnToMenu = false; _menu = true; _scr = Scr.Main; }
            if (_ns.State == NetState.Host || _ns.State == NetState.Client) _menu = false;
            GameRoot.MenuBlocksInput = _menu || (_ns.State == NetState.Connecting);
        }

        static string Hex(int colorIdx) => ColorUtility.ToHtmlStringRGB(GameSettings.CarColors[Mathf.Clamp(colorIdx, 0, GameSettings.CarColors.Length - 1)].color);

        void OnGUI()
        {
            if (_ns == null || _ns.Root == null) return;
            GUI.depth = -1000;
            GUI.skin = UiTheme.Skin;
            UiLayout.Begin();
            float k = UiLayout.K, W = UiLayout.W, H = UiLayout.H;
            _tag ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 15, wordWrap = false };
            _err ??= new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };

            if (!_registered) { _registered = true; DebugHud.Extra.Add(new DebugHud.ToolItem { label = "Multijugador", onClick = () => { _menu = true; _scr = Scr.Main; }, visible = () => !_menu && _ns != null && _ns.State == NetState.Offline }); }
            bool inGame = _ns.State == NetState.Host || _ns.State == NetState.Client;
            bool sheet = UiLayout.Compact && Root().settingsOpen;
            float y = UiLayout.TopBlockBottom;
            UiLayout.ExtraTop = 0f;
            if (inGame) { DrawTags(k); if (!sheet) { DrawBanner(W, y); UiLayout.ExtraTop += 56f; DrawScores(); } }
            if (!_menu && !sheet && _ns.State != NetState.Connecting) { DrawTeleport(W, y + UiLayout.ExtraTop); UiLayout.ExtraTop += 42f; }
            if (Time.unscaledTime < _toast) { float tw = UiLayout.BannerWidth(280f); GUI.Box(new Rect((W - tw) * 0.5f, y + UiLayout.ExtraTop, tw, 38), _toastText, UiTheme.Toast); UiLayout.ExtraTop += 44f; }

            if (_menu || _ns.State == NetState.Connecting) DrawMenu(W, H);
        }

        // ------------------------------------------------------------------------------------------------ in game

        void DrawBanner(float W, float y)
        {
            string code = string.IsNullOrEmpty(_ns.Code) ? "—" : _ns.Code;
            var me = LocalPlayer();
            string lag = _ns.State == NetState.Client && me != null ? $"   ·   {me.InputLagMs:0} ms" : "";
            string role = _ns.State == NetState.Host ? "ANFITRIÓN" : "JUGADOR";
            float bw = UiLayout.BannerWidth(460f), x = (W - bw) * 0.5f;
            GUI.Box(new Rect(x + 78f, y, bw - 78f, 50), $"{role}   CÓDIGO  <b>{code}</b>   ·   {_ns.PlayerCount}/{NetSession.MaxPlayers}{lag}\n<size=11>Red: {(string.IsNullOrEmpty(_ns.NetInfo) ? "?" : _ns.NetInfo)}</size>", UiTheme.Pill);
            if (GUI.Button(new Rect(x, y, 72, 50), "Salir", UiTheme.DangerBtn)) _ns.Leave();
        }

        int _tpIdx = -1;

        void DrawTeleport(float W, float y)
        {
            var car = _ns.Root.carCtl;
            if (car == null) return;
            bool net = _ns.State == NetState.Host || _ns.State == NetState.Client;
            var me = net ? LocalPlayer() : null;
            GUI.enabled = car.CurrentPhase == RocketCar.Phase.Running;
            float x = net ? W * 0.5f - 126f : W * 0.5f - 60f;
            if (GUI.Button(new Rect(x, y, 120, 36), "Subir 100 m"))
            {
                if (me != null) me.TeleportAbove(); else car.TeleportAbove(PlayerNet.TeleportMeters);
            }
            if (net)
            {
                var others = new System.Collections.Generic.List<PlayerNet>();
                foreach (var p in PlayerNet.All) if (!p.IsOwner) others.Add(p);
                others.Sort((a, b) => a.Slot.Value.CompareTo(b.Slot.Value));
                GUI.enabled = GUI.enabled && others.Count > 0 && me != null;
                if (GUI.Button(new Rect(W * 0.5f + 6, y, 120, 36), "Ir a jugador"))
                {
                    _tpIdx = (_tpIdx + 1) % others.Count;
                    var t = others[_tpIdx];
                    me.TeleportTo(t);
                    _toast = Time.unscaledTime + 1.6f; _toastText = "Ir a " + t.DisplayName;
                }
            }
            GUI.enabled = true;
        }

        PlayerNet LocalPlayer() { foreach (var p in PlayerNet.All) if (p.IsOwner) return p; return null; }

        void DrawScores()
        {
            int n = PlayerNet.All.Count;
            if (n == 0) return;
            float y = Mathf.Max(UiLayout.Top, 0f) + Minimap.Size + 14f;
            GUI.Box(new Rect(UiLayout.Left, y, 230, 38 + n * 24), "");
            GUI.Label(new Rect(UiLayout.Left + 10, y + 6, 210, 24), "Jugadores", UiTheme.Hint);
            var list = new System.Collections.Generic.List<PlayerNet>(PlayerNet.All);
            list.Sort((a, b) => a.Slot.Value.CompareTo(b.Slot.Value));
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                string me = p.IsOwner ? " (tú)" : "";
                string state = p.Alive.Value ? "<color=#4DDE85>live</color>" : "<color=#9fb3cc>out</color>";
                GUI.Label(new Rect(UiLayout.Left + 10, y + 34 + i * 24, 214, 24), $"<color=#{Hex(p.ColorIdx.Value)}><b>|</b></color> {p.DisplayName}{me}   <b>{p.Score.Value}</b> {state}");
            }
        }

        void DrawTags(float k)
        {
            var cam = _ns.Root.cam;
            if (cam == null) return;
            foreach (var p in PlayerNet.All)
            {
                if (p.IsOwner || p.Car == null || p.Car.visual == null || !p.Car.CanDrawTag()) continue;
                var wp = p.Car.visual.position + Vector3.up * 1.7f;
                var sp = cam.WorldToScreenPoint(wp);
                if (sp.z <= 0.5f || sp.z > 400f) continue;
                var r = new Rect(sp.x / k - 90, (Screen.height - sp.y) / k - 12, 180, 24);
                var old = GUI.color; GUI.color = new Color(0, 0, 0, 0.6f);
                GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), p.DisplayName, _tag);
                GUI.color = old;
                GUI.Label(r, $"<color=#{Hex(p.ColorIdx.Value)}>{p.DisplayName}</color>", _tag);
            }
        }

        // ------------------------------------------------------------------------------------------------ menu (Rocket League style)
        // Left column of big dark rows (the hovered / selected one turns white with an amber marker), logo on top, player card at the bottom-left,
        // tips panel on the right. The 3D world stays visible behind it. Mouse, touch and keyboard (up / down / Enter / Esc) all work.

        static Texture2D _grad;
        static Texture2D Grad()
        {
            if (_grad != null) return _grad;
            _grad = new Texture2D(64, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (int i = 0; i < 64; i++) _grad.SetPixel(i, 0, new Color(0.01f, 0.03f, 0.07f, Mathf.Pow(1f - i / 63f, 1.5f) * 0.92f));
            _grad.Apply();
            return _grad;
        }

        GUIStyle _mRow, _mHead, _mSec, _mSub, _mCap, _mName, _mCode, _mTip, _mAv, _mAttr, _mHdr, _mKey;
        readonly float[] _hl = new float[8];
        int _sel, _rows;
        bool _enter, _mouseMoved;
        float _menuT0 = -10f, _lastDraw = -10f;
        Vector2 _lastMouse;
        Scr _lastScr = (Scr)(-1);
        bool _lastConn;

        void EnsureMenuStyles()
        {
            if (_mRow != null) return;
            var lab = GUI.skin.label;
            _mRow = new GUIStyle(lab) { font = UiTheme.Bold, fontSize = 21, alignment = TextAnchor.MiddleLeft, wordWrap = false, richText = false, padding = new RectOffset(20, 0, 0, 0) };
            _mHead = new GUIStyle(lab) { font = UiTheme.Bold, fontSize = 46, alignment = TextAnchor.MiddleLeft, wordWrap = false, richText = true, padding = new RectOffset(0, 0, 0, 0) };
            _mHead.normal.textColor = UiTheme.Text;
            _mSec = new GUIStyle(_mHead) { fontSize = 24 };
            _mSec.normal.textColor = UiTheme.Accent;
            _mSub = new GUIStyle(lab) { font = UiTheme.Semi, fontSize = 16, alignment = TextAnchor.UpperLeft, wordWrap = true, richText = true, padding = new RectOffset(0, 0, 0, 0) };
            _mSub.normal.textColor = UiTheme.TextDim;
            _mCap = new GUIStyle(lab) { font = UiTheme.Bold, fontSize = 12, alignment = TextAnchor.MiddleLeft, wordWrap = false, padding = new RectOffset(0, 0, 0, 0) };
            _mCap.normal.textColor = UiTheme.TextDim;
            _mName = new GUIStyle(GUI.skin.textField) { font = UiTheme.Bold, fontSize = 19, alignment = TextAnchor.MiddleLeft };
            _mCode = new GUIStyle(GUI.skin.textField) { font = UiTheme.Bold, fontSize = 36, alignment = TextAnchor.MiddleCenter };
            _mTip = new GUIStyle(lab) { font = UiTheme.Semi, fontSize = 14, alignment = TextAnchor.UpperLeft, wordWrap = true, padding = new RectOffset(0, 0, 0, 0) };
            _mTip.normal.textColor = UiTheme.Text;
            _mAv = new GUIStyle(lab) { font = UiTheme.Bold, fontSize = 28, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(0, 0, 0, 0) };
            _mAttr = new GUIStyle(lab) { fontSize = 11, alignment = TextAnchor.LowerCenter, wordWrap = false };
            _mHdr = new GUIStyle(_mCap) { fontSize = 13, padding = new RectOffset(14, 0, 0, 0) };
            _mHdr.normal.textColor = Color.white;
            _mKey = new GUIStyle(_mCap) { alignment = TextAnchor.MiddleCenter };
            _mKey.normal.textColor = UiTheme.Ink;
        }

        static void Fill(Rect r, Color c, float radius = 0f)
        {
            if (Event.current.type == EventType.Repaint) GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c, 0f, radius);
        }

        static float Ease(float t) { t = Mathf.Clamp01(t); return 1f - (1f - t) * (1f - t) * (1f - t); }

        /// <summary>One big menu row. True when it was clicked / tapped (or Enter pressed while it is the selected row).</summary>
        bool MenuRow(int idx, Rect r, string text, bool enabled = true)
        {
            var ev = Event.current;
            float e = Ease((Time.unscaledTime - _menuT0 - idx * 0.06f) / 0.32f);
            if (ev.type == EventType.Repaint)
            {
                if (enabled && _mouseMoved && r.Contains(ev.mousePosition)) _sel = idx;
                _hl[idx] = Mathf.MoveTowards(_hl[idx], (enabled && _sel == idx) ? 1f : 0f, Time.unscaledDeltaTime * 9f);
                float h = _hl[idx];
                var rr = new Rect(r.x - (1f - e) * 80f + h * 10f, r.y, r.width, r.height);
                var bg = Color.Lerp(new Color(0.04f, 0.07f, 0.13f, 0.84f), new Color(0.97f, 0.98f, 1f, 0.98f), h);
                bg.a *= e * (enabled ? 1f : 0.5f);
                Fill(rr, bg, 3f);
                if (h > 0.01f) Fill(new Rect(rr.x, rr.y, 6f, rr.height), new Color(UiTheme.Hot.r, UiTheme.Hot.g, UiTheme.Hot.b, h * e));
                var old = GUI.color; GUI.color = new Color(1f, 1f, 1f, e);
                _mRow.normal.textColor = enabled ? Color.Lerp(UiTheme.Text, UiTheme.Ink, h) : UiTheme.TextDim;
                GUI.Label(rr, text, _mRow);
                if (h > 0.05f)
                {
                    var m = GUI.matrix; var c = new Vector2(rr.xMax - 30f, rr.center.y);
                    GUIUtility.RotateAroundPivot(45f, c);
                    GUI.color = new Color(UiTheme.Hot.r, UiTheme.Hot.g, UiTheme.Hot.b, h * e);
                    GUI.DrawTexture(new Rect(c.x - 6f, c.y - 6f, 12f, 12f), Texture2D.whiteTexture);
                    GUI.matrix = m;
                }
                GUI.color = old;
            }
            bool click = enabled && GUI.Button(r, GUIContent.none, GUIStyle.none);
            if (_enter && enabled && _sel == idx) { click = true; _enter = false; }
            if (click) _sel = idx;
            return click;
        }

        void DrawMenu(float W, float H)
        {
            EnsureMenuStyles();
            var ev = Event.current;
            bool conn = _ns.State == NetState.Connecting;
            if (Time.unscaledTime - _lastDraw > 0.4f || _lastScr != _scr || _lastConn != conn)
            {   // menu just opened, or the screen changed: replay the slide-in
                _menuT0 = Time.unscaledTime; _sel = 0;
                for (int i = 0; i < _hl.Length; i++) _hl[i] = 0f;
            }
            if (ev.type == EventType.Repaint) _lastDraw = Time.unscaledTime;
            _lastScr = _scr; _lastConn = conn;
            _mouseMoved = ev.type == EventType.Repaint && (ev.mousePosition - _lastMouse).sqrMagnitude > 0.25f;

            // keyboard: up / down choose, Enter accepts, Esc goes back
            _enter = false;
            if (ev.type == EventType.KeyDown && !conn)
            {
                string focus = GUI.GetNameOfFocusedControl();
                if (ev.keyCode == KeyCode.DownArrow) { _sel = (_sel + 1) % Mathf.Max(1, _rows); ev.Use(); }
                else if (ev.keyCode == KeyCode.UpArrow) { _sel = (_sel + Mathf.Max(1, _rows) - 1) % Mathf.Max(1, _rows); ev.Use(); }
                else if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter)
                {
                    if (focus == "rw_name") GUI.FocusControl("");
                    else _enter = true;
                    ev.Use();
                }
                else if (ev.keyCode == KeyCode.Escape && _scr != Scr.Main) { _ns.Error = ""; _scr = Scr.Main; ev.Use(); }
            }

            float fade = Mathf.Clamp01((Time.unscaledTime - _menuT0) / 0.4f);
            bool narrow = W < 760f, shortScr = H < 470f;
            if (ev.type == EventType.Repaint)
            {
                var old = GUI.color; GUI.color = new Color(1f, 1f, 1f, fade);
                GUI.DrawTexture(new Rect(0, 0, W * (narrow ? 1f : 0.62f), H), Grad(), ScaleMode.StretchToFill, true);
                GUI.color = old;
                Fill(new Rect(0, 0, W, H), new Color(0f, 0.02f, 0.05f, 0.2f * fade));
            }

            float pad = narrow ? 16f : 44f;
            float colW = Mathf.Min(narrow ? W - pad * 2f : 340f, W - pad * 2f);
            float x = pad, y = 16f + (shortScr ? 0f : 34f);
            float rowH = shortScr ? 38f : 48f, gap = 5f;
            _mHead.fontSize = shortScr ? 30 : narrow ? 36 : 46;

            // logo
            float eh = Ease((Time.unscaledTime - _menuT0) / 0.4f);
            var oc = GUI.color; GUI.color = new Color(1f, 1f, 1f, eh);
            GUI.Label(new Rect(x - (1f - eh) * 40f, y, colW + 200f, shortScr ? 38f : 58f), "ROCKET <color=#3DD6F5>WORLD</color>", _mHead);
            GUI.color = oc;
            float cy = y + (shortScr ? 40f : 60f);

            if (conn)
            {
                GUI.Label(new Rect(x, cy, colW, 34f), "CONECTANDO", _mSec); cy += 42f;
                GUI.Label(new Rect(x, cy, colW + 60f, 140f), _ns.Info + new string('.', (int)(Time.unscaledTime * 2.5f) % 4), _mSub);
                _rows = 0;
            }
            else
            {
                if (_scr == Scr.Main) { GUI.Label(new Rect(x, cy - 4f, colW, 20f), "CONDUCE POR CUALQUIER LUGAR DEL MUNDO", _mCap); cy += shortScr ? 14f : 26f; }
                else { GUI.Label(new Rect(x, cy, colW, 34f), _scr == Scr.Create ? "CREAR PARTIDA" : "UNIRSE A PARTIDA", _mSec); cy += 40f; }
                if (!string.IsNullOrEmpty(_ns.Error))
                {
                    var c = new GUIContent("<color=#ff7b7b>" + _ns.Error + "</color>");
                    float eh2 = _mSub.CalcHeight(c, colW + 40f);
                    GUI.Label(new Rect(x, cy, colW + 40f, eh2), c, _mSub); cy += eh2 + 10f;
                }
                switch (_scr)
                {
                    case Scr.Main: MainScreen(x, cy, colW, rowH, gap, W, H, narrow, shortScr); break;
                    case Scr.Create: CreateScreen(x, cy, colW, rowH, gap); break;
                    case Scr.Join: JoinScreen(x, cy, colW, rowH, gap); break;
                }
            }

            if (_ns.Root.waterMap != null)       // OpenStreetMap's licence asks for attribution wherever its data is used
            {
                var oc2 = GUI.color; GUI.color = new Color(1f, 1f, 1f, 0.5f);
                GUI.Label(new Rect(0, H - 20, W, 18), "Datos de agua \u00a9 colaboradores de OpenStreetMap \u00b7 OpenFreeMap", _mAttr);
                GUI.color = oc2;
            }
            if (ev.type == EventType.Repaint) _lastMouse = ev.mousePosition;
            GUI.Button(new Rect(0, 0, W, H), GUIContent.none, GUIStyle.none);       // modal: swallow clicks that miss the controls
        }

        void SaveName()
        {
            LocalName = string.IsNullOrWhiteSpace(LocalName) ? "Jugador" : LocalName.Trim();
            PlayerPrefs.SetString("rw.name", LocalName);
        }

        void MainScreen(float x, float cy, float colW, float rowH, float gap, float W, float H, bool narrow, bool shortScr)
        {
            _rows = 4;
            bool running = _ns.Root.carCtl != null && !_ns.Root.carCtl.Held;
            if (MenuRow(0, new Rect(x, cy, colW, rowH), running ? "SEGUIR JUGANDO" : "JUGAR SOLO")) { SaveName(); _ns.Error = ""; _menu = false; _ns.PlaySolo(); }
            if (MenuRow(1, new Rect(x, cy + (rowH + gap), colW, rowH), "CREAR PARTIDA")) { SaveName(); _ns.Error = ""; _scr = Scr.Create; }
            if (MenuRow(2, new Rect(x, cy + (rowH + gap) * 2f, colW, rowH), "UNIRSE A PARTIDA")) { SaveName(); _ns.Error = ""; _scr = Scr.Join; }
            if (MenuRow(3, new Rect(x, cy + (rowH + gap) * 3f, colW, rowH), "VER TUTORIAL")) { WebBridge.ShowTutorial(); }
            float rowsBottom = cy + (rowH + gap) * 4f;
            if (!shortScr) GUI.Label(new Rect(x, rowsBottom + 6f, colW, 40f), "Multijugador de hasta 4 jugadores: 1 anfitri\u00f3n y 3 visitantes.", _mSub);

            // player card (bottom-left, like the in-game profile bar)
            float cw = Mathf.Min(colW, 340f), ch = 64f;
            var card = new Rect(x, H - 26f - ch - 14f, cw, ch);
            if (card.y < rowsBottom + (shortScr ? 4f : 50f)) card.x = Mathf.Max(x, W - 16f - cw);        // not enough height: park it on the right
            float ec = Ease((Time.unscaledTime - _menuT0 - 0.25f) / 0.4f);
            var oc = GUI.color; GUI.color = new Color(1f, 1f, 1f, ec);
            card.y += (1f - ec) * 30f;
            Fill(card, new Color(0.04f, 0.07f, 0.13f, 0.9f * ec), 6f);
            var cc = GameSettings.CarColors[Mathf.Clamp(_ns.Root.settings.carColor, 0, GameSettings.CarColors.Length - 1)].color;
            var av = new Rect(card.x + 6f, card.y + 6f, ch - 12f, ch - 12f);
            Fill(av, new Color(cc.r, cc.g, cc.b, ec), 4f);
            string nm = string.IsNullOrWhiteSpace(LocalName) ? "?" : LocalName.Trim().Substring(0, 1).ToUpperInvariant();
            _mAv.normal.textColor = (cc.r * 0.3f + cc.g * 0.59f + cc.b * 0.11f) > 0.55f ? UiTheme.Ink : Color.white;
            GUI.Label(av, nm, _mAv);
            GUI.Label(new Rect(card.x + ch + 4f, card.y + 6f, cw - ch - 10f, 16f), "JUGADOR \u00b7 toca para cambiar el nombre", _mCap);
            GUI.SetNextControlName("rw_name");
            LocalName = GUI.TextField(new Rect(card.x + ch + 4f, card.y + 24f, cw - ch - 14f, 32f), LocalName ?? "", 16, _mName);
            GUI.color = oc;

            // tips panel (right), only when there is room
            if (!narrow && W >= 900f)
            {
                float pw = 310f, e2 = Ease((Time.unscaledTime - _menuT0 - 0.3f) / 0.45f);
                var panel = new Rect(W - 44f - pw + (1f - e2) * 120f, Mathf.Max(110f, H * 0.2f), pw, 262f);
                var o2 = GUI.color; GUI.color = new Color(1f, 1f, 1f, e2);
                Fill(panel, new Color(0.05f, 0.08f, 0.15f, 0.9f * e2), 6f);
                Fill(new Rect(panel.x, panel.y, pw, 34f), new Color(0.10f, 0.36f, 0.56f, 0.95f * e2), 6f);
                GUI.Label(new Rect(panel.x, panel.y, pw, 34f), "CONSEJOS PARA EMPEZAR", _mHdr);
                string[] keys = { "SHIFT", "ESPACIO \u00d7 2", "B  \u00b7  R" };
                string[] tips = { "Turbo infinito: mantenlo pulsado para acelerar sin l\u00edmite.", "Salta y vuelve a pulsar en el aire para dar una voltereta.", "B invoca una pelota gigante. R te rescata si te atascas." };
                for (int i = 0; i < 3; i++)
                {
                    float ty = panel.y + 46f + i * 68f;
                    var kr = new Rect(panel.x + 14f, ty, 14f + keys[i].Length * 8.5f, 22f);
                    Fill(kr, new Color(UiTheme.Accent.r, UiTheme.Accent.g, UiTheme.Accent.b, e2), 4f);
                    GUI.Label(kr, keys[i], _mKey);
                    GUI.Label(new Rect(panel.x + 14f, ty + 28f, pw - 28f, 36f), tips[i], _mTip);
                }
                GUI.color = o2;
            }
        }

        void CreateScreen(float x, float cy, float colW, float rowH, float gap)
        {
            _rows = 2;
            string d = "Se genera un c\u00f3digo de 4 n\u00fameros para invitar a tus amigos. M\u00e1ximo 4 jugadores. Tu navegador hace de servidor: mant\u00e9n esta pesta\u00f1a abierta.";
            float dh = _mSub.CalcHeight(new GUIContent(d), colW + 40f);
            GUI.Label(new Rect(x, cy, colW + 40f, dh), d, _mSub); cy += dh + 14f;
            if (MenuRow(0, new Rect(x, cy, colW, rowH), "CREAR Y ENTRAR")) _ns.HostGame();
            if (MenuRow(1, new Rect(x, cy + rowH + gap, colW, rowH), "ATR\u00c1S")) { _ns.Error = ""; _scr = Scr.Main; }
        }

        static string DigitsOnly(string s)
        {
            var sb = new System.Text.StringBuilder(4);
            foreach (char c in s ?? "") if (c >= '0' && c <= '9' && sb.Length < 4) sb.Append(c);
            return sb.ToString();
        }

        void JoinScreen(float x, float cy, float colW, float rowH, float gap)
        {
            _rows = 2;
            GUI.Label(new Rect(x, cy, colW, 18f), "C\u00d3DIGO DE LA PARTIDA", _mCap); cy += 22f;
            var rc = new Rect(x, cy, colW, 58f);
            GUI.SetNextControlName("rw_code");
            _code = DigitsOnly(GUI.TextField(rc, _code ?? "", 4, _mCode));
            if (string.IsNullOrEmpty(_code) && GUI.GetNameOfFocusedControl() != "rw_code")
            {
                var o = GUI.color; GUI.color = new Color(1f, 1f, 1f, 0.22f); GUI.Label(rc, "0000", _mCode); GUI.color = o;
            }
            cy += 70f;
            if (MenuRow(0, new Rect(x, cy, colW, rowH), "UNIRSE", _code.Length == 4)) _ns.JoinByCode(_code);
            if (MenuRow(1, new Rect(x, cy + rowH + gap, colW, rowH), "ATR\u00c1S")) { _ns.Error = ""; _scr = Scr.Main; }
        }
    }
}
