using UnityEngine;

namespace RocketWorld.Net
{
    /// <summary>
    /// Start menu (Crear partida / Unirse a partida / Jugar solo), the join code in the ceiling of the screen, scoreboard and name tags.
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

            if (!_registered) { _registered = true; DebugHud.Extra.Add(new DebugHud.ToolItem { label = "Online", onClick = () => { _menu = true; _scr = Scr.Main; }, visible = () => !_menu && _ns != null && _ns.State == NetState.Offline }); }
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

        // ------------------------------------------------------------------------------------------------ menu

        void DrawMenu(float W, float H)
        {
            if (Event.current.type == EventType.Repaint) GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, new Color(0f, 0.02f, 0.05f, 0.5f), 0f, 0f);
            float mw = UiLayout.BannerWidth(460f), mh = Mathf.Min(500f, H - 24f);
            var r = new Rect((W - mw) * 0.5f, Mathf.Max(12f, (H - mh) * 0.5f), mw, mh);
            GUILayout.BeginArea(r, GUI.skin.box);
            GUILayout.Label("Rocket World", UiTheme.Title);

            if (_ns.State == NetState.Connecting)
            {
                GUILayout.Space(30);
                GUILayout.Label(_ns.Info, UiTheme.Hint);
            }
            else
            {
                if (!string.IsNullOrEmpty(_ns.Error)) GUILayout.Label("<color=#ff7b7b>" + _ns.Error + "</color>", _err);
                switch (_scr) { case Scr.Main: MainScreen(); break; case Scr.Create: CreateScreen(); break; case Scr.Join: JoinScreen(); break; }
            }
            GUILayout.EndArea();
            GUI.Button(new Rect(0, 0, W, H), GUIContent.none, GUIStyle.none);       // modal: swallow clicks that miss the panel
        }

        void SaveName()
        {
            LocalName = string.IsNullOrWhiteSpace(LocalName) ? "Jugador" : LocalName.Trim();
            PlayerPrefs.SetString("rw.name", LocalName);
        }

        void MainScreen()
        {
            GUILayout.Label("Multijugador · hasta 4 jugadores (1 anfitrión + 3 visitantes)", UiTheme.Hint);
            GUILayout.Space(8);
            GUILayout.Label("Tu nombre");
            LocalName = GUILayout.TextField(LocalName ?? "", 16);
            GUILayout.Space(10);
            if (GUILayout.Button("Crear partida", GUILayout.Height(44))) { SaveName(); _ns.Error = ""; _scr = Scr.Create; }
            if (GUILayout.Button("Unirse a partida", GUILayout.Height(44))) { SaveName(); _ns.Error = ""; _scr = Scr.Join; }
            if (GUILayout.Button(_ns.Root.carCtl != null && !_ns.Root.carCtl.Held ? "Cerrar" : "Jugar solo", GUILayout.Height(44)))
            {
                SaveName(); _ns.Error = ""; _menu = false; _ns.PlaySolo();
            }
        }

        void CreateScreen()
        {
            GUILayout.Label("Crear partida", UiTheme.Hint);
            GUILayout.Space(6);
            GUILayout.Label("Se genera un código de 4 números para invitar a tus amigos. Máximo 4 jugadores. Tu navegador hace de servidor: mantén esta pestaña abierta.", UiTheme.Hint);
            GUILayout.Space(10);
            if (GUILayout.Button("Crear y entrar", GUILayout.Height(44))) _ns.HostGame();
            if (GUILayout.Button("Atrás", GUILayout.Height(36))) { _ns.Error = ""; _scr = Scr.Main; }
        }

        static string DigitsOnly(string s)
        {
            var sb = new System.Text.StringBuilder(4);
            foreach (char c in s ?? "") if (c >= '0' && c <= '9' && sb.Length < 4) sb.Append(c);
            return sb.ToString();
        }

        void JoinScreen()
        {
            GUILayout.Label("Unirse a partida", UiTheme.Hint);
            GUILayout.Space(4);
            GUILayout.Label("Código de la partida");
            _code = DigitsOnly(GUILayout.TextField(_code ?? "", 4));
            GUILayout.Space(6);
            GUI.enabled = _code.Length == 4;
            if (GUILayout.Button("Unirse", GUILayout.Height(44))) _ns.JoinByCode(_code);
            GUI.enabled = true;
            if (GUILayout.Button("Atrás", GUILayout.Height(36))) { _ns.Error = ""; _scr = Scr.Main; }
        }
    }
}
