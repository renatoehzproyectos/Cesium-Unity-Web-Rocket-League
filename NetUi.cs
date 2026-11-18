using UnityEngine;

namespace RocketWorld.Net
{
    /// <summary>
    /// Menú de inicio inspirado en Rocket League (panel vertical izquierdo) + código de partida, marcador y etiquetas de nombre.
    /// IMGUI como el resto del HUD, dibujado por encima (GUI.depth).
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class NetUi : MonoBehaviour
    {
        public static string LocalName = "Jugador";

        enum Scr { Main, Play, Create, Join }
        NetSession _ns;
        bool _menu = true;
        Scr _scr = Scr.Main;
        string _code = "";
        float _toast; string _toastText = "";
        GUIStyle _tag, _err, _menuTitle, _menuItem, _menuItemSel, _menuSub, _bigBtn, _smallBtn;
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

        void EnsureStyles()
        {
            if (_menuTitle != null) return;
            var font = UiTheme.Bold != null ? UiTheme.Bold : GUI.skin.label.font;
            _menuTitle = new GUIStyle(GUI.skin.label)
            {
                font = font,
                fontSize = 28,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white }
            };
            _menuItem = new GUIStyle(GUI.skin.label)
            {
                font = font,
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(18, 12, 0, 0),
                normal = { textColor = new Color(0.92f, 0.95f, 1f, 0.92f) }
            };
            _menuItemSel = new GUIStyle(_menuItem)
            {
                normal = { textColor = new Color(0.15f, 0.85f, 1f, 1f) }
            };
            _menuSub = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                wordWrap = true,
                normal = { textColor = new Color(0.65f, 0.72f, 0.82f, 0.95f) }
            };
            _bigBtn = new GUIStyle(GUI.skin.button)
            {
                font = font,
                fontSize = 17,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                fixedHeight = 48
            };
            _smallBtn = new GUIStyle(GUI.skin.button)
            {
                fontSize = 15,
                alignment = TextAnchor.MiddleCenter,
                fixedHeight = 40
            };
            _tag ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 15, wordWrap = false };
            _err ??= new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
        }

        void OnGUI()
        {
            if (_ns == null || _ns.Root == null) return;
            GUI.depth = -1000;
            GUI.skin = UiTheme.Skin;
            UiLayout.Begin();
            float k = UiLayout.K, W = UiLayout.W, H = UiLayout.H;
            EnsureStyles();

            if (!_registered)
            {
                _registered = true;
                DebugHud.Extra.Add(new DebugHud.ToolItem
                {
                    label = "Online",
                    onClick = () => { _menu = true; _scr = Scr.Main; },
                    visible = () => !_menu && _ns != null && _ns.State == NetState.Offline
                });
            }

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
                    if (me != null) me.TeleportTo(t);
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
                string state = p.Alive.Value ? "<color=#4DDE85>vivo</color>" : "<color=#9fb3cc>fuera</color>";
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

        // ------------------------------------------------------------------------------------------------ menú estilo Rocket League

        void DrawMenu(float W, float H)
        {
            // Fondo semitransparente oscuro (deja ver el coche / mundo detrás, como en RL)
            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                    new Color(0.02f, 0.04f, 0.08f, 0.55f), 0f, 0f);
            }

            // Panel izquierdo estilo Rocket League
            float panelW = Mathf.Clamp(W * 0.32f, 280f, 380f);
            float panelH = H;
            var panel = new Rect(0, 0, panelW, panelH);

            if (Event.current.type == EventType.Repaint)
            {
                // Gradiente lateral oscuro
                GUI.DrawTexture(panel, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                    new Color(0.04f, 0.07f, 0.12f, 0.92f), 0f, 0f);
                // Línea de acento a la derecha del panel
                GUI.DrawTexture(new Rect(panelW - 2, 0, 2, H), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                    new Color(0.24f, 0.85f, 1f, 0.35f), 0f, 0f);
            }

            GUILayout.BeginArea(new Rect(18, 28, panelW - 36, H - 56));

            // Título
            GUILayout.Label("ROCKET", _menuTitle);
            var old = GUI.color;
            GUI.color = new Color(0.24f, 0.85f, 1f);
            GUILayout.Label("WORLD", _menuTitle);
            GUI.color = old;

            GUILayout.Space(6);
            GUILayout.Label("Conduce en cualquier lugar de la Tierra", _menuSub);
            GUILayout.Space(28);

            if (_ns.State == NetState.Connecting)
            {
                GUILayout.Space(20);
                GUILayout.Label(_ns.Info, UiTheme.Hint);
            }
            else
            {
                if (!string.IsNullOrEmpty(_ns.Error))
                    GUILayout.Label("<color=#ff7b7b>" + _ns.Error + "</color>", _err);

                switch (_scr)
                {
                    case Scr.Main: MainMenuItems(panelW); break;
                    case Scr.Play: PlayScreen(); break;
                    case Scr.Create: CreateScreen(); break;
                    case Scr.Join: JoinScreen(); break;
                }
            }

            GUILayout.FlexibleSpace();

            // Pie del panel (nombre del jugador)
            GUILayout.Label("Jugador", UiTheme.Hint);
            LocalName = GUILayout.TextField(LocalName ?? "", 16, GUILayout.Height(36));
            GUILayout.Space(8);
            GUILayout.Label("Los ajustes se guardan en este navegador", _menuSub);

            GUILayout.EndArea();

            // Bloquear clics fuera del panel
            if (Event.current.type == EventType.MouseDown)
            {
                var mouse = Event.current.mousePosition;
                if (mouse.x > panelW) Event.current.Use();
            }
        }

        bool MenuButton(string label, bool selected = false)
        {
            var style = selected ? _menuItemSel : _menuItem;
            var r = GUILayoutUtility.GetRect(0, 48, GUILayout.ExpandWidth(true));
            bool hover = r.Contains(Event.current.mousePosition);
            if (Event.current.type == EventType.Repaint)
            {
                if (selected || hover)
                {
                    var col = selected
                        ? new Color(0.12f, 0.55f, 0.75f, 0.35f)
                        : new Color(1f, 1f, 1f, 0.07f);
                    GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, col, 0f, 6f);
                    if (selected)
                    {
                        GUI.DrawTexture(new Rect(r.x, r.y + 8, 4, r.height - 16), Texture2D.whiteTexture,
                            ScaleMode.StretchToFill, true, 0f, new Color(0.24f, 0.85f, 1f, 1f), 0f, 0f);
                    }
                }
            }
            return GUI.Button(r, label, style);
        }

        void MainMenuItems(float panelW)
        {
            if (MenuButton("JUGAR", _scr == Scr.Play || _scr == Scr.Create || _scr == Scr.Join))
            {
                SaveName();
                _ns.Error = "";
                _scr = Scr.Play;
            }

            if (MenuButton("CONTROLES"))
            {
                SaveName();
                _menu = false;
                if (Root().hud != null) Root().hud.SetControlsOpen(true);
            }

            if (MenuButton("CÁMARA"))
            {
                SaveName();
                _menu = false;
                if (Root().camMenu != null) Root().camMenu.OpenMenu();
            }

            if (MenuButton("AJUSTES"))
            {
                SaveName();
                _menu = false;
                if (Root().hud != null) Root().hud.SetOpen(true);
            }

            GUILayout.Space(16);

            // Botón grande de jugar solo (acción principal)
            if (GUILayout.Button("JUGAR SOLO", _bigBtn, GUILayout.Height(52)))
            {
                SaveName();
                _ns.Error = "";
                _menu = false;
                _ns.PlaySolo();
            }

            GUILayout.Space(10);
            GUILayout.Label("O elige JUGAR para crear o unirte a una partida multijugador (hasta 4 jugadores).", _menuSub);
        }

        void PlayScreen()
        {
            GUILayout.Label("MULTIJUGADOR", _menuItemSel);
            GUILayout.Space(4);
            GUILayout.Label("Hasta 4 jugadores (1 anfitrión + 3 visitantes). Funciona de navegador a navegador.", _menuSub);
            GUILayout.Space(18);

            if (GUILayout.Button("Crear partida", _bigBtn))
            {
                SaveName();
                _ns.Error = "";
                _scr = Scr.Create;
            }
            GUILayout.Space(8);
            if (GUILayout.Button("Unirse a partida", _bigBtn))
            {
                SaveName();
                _ns.Error = "";
                _scr = Scr.Join;
            }
            GUILayout.Space(8);
            if (GUILayout.Button("Jugar solo", _bigBtn))
            {
                SaveName();
                _ns.Error = "";
                _menu = false;
                _ns.PlaySolo();
            }
            GUILayout.Space(16);
            if (GUILayout.Button("← Volver al menú", _smallBtn))
            {
                _ns.Error = "";
                _scr = Scr.Main;
            }
        }

        void SaveName()
        {
            LocalName = string.IsNullOrWhiteSpace(LocalName) ? "Jugador" : LocalName.Trim();
            PlayerPrefs.SetString("rw.name", LocalName);
        }

        void CreateScreen()
        {
            GUILayout.Label("CREAR PARTIDA", _menuItemSel);
            GUILayout.Space(6);
            GUILayout.Label("Se genera un código de 4 números para invitar a tus amigos. Máximo 4 jugadores. Tu navegador hace de servidor: mantén esta pestaña abierta.", _menuSub);
            GUILayout.Space(16);
            if (GUILayout.Button("Crear y entrar", _bigBtn)) _ns.HostGame();
            GUILayout.Space(10);
            if (GUILayout.Button("← Atrás", _smallBtn)) { _ns.Error = ""; _scr = Scr.Play; }
        }

        static string DigitsOnly(string s)
        {
            var sb = new System.Text.StringBuilder(4);
            foreach (char c in s ?? "") if (c >= '0' && c <= '9' && sb.Length < 4) sb.Append(c);
            return sb.ToString();
        }

        void JoinScreen()
        {
            GUILayout.Label("UNIRSE A PARTIDA", _menuItemSel);
            GUILayout.Space(6);
            GUILayout.Label("Introduce el código de 4 números que te ha pasado el anfitrión.", _menuSub);
            GUILayout.Space(10);
            GUILayout.Label("Código de la partida", UiTheme.Hint);
            _code = DigitsOnly(GUILayout.TextField(_code ?? "", 4, GUILayout.Height(40)));
            GUILayout.Space(12);
            GUI.enabled = _code.Length == 4;
            if (GUILayout.Button("Unirse", _bigBtn)) _ns.JoinByCode(_code);
            GUI.enabled = true;
            GUILayout.Space(10);
            if (GUILayout.Button("← Atrás", _smallBtn)) { _ns.Error = ""; _scr = Scr.Play; }
        }
    }
}
