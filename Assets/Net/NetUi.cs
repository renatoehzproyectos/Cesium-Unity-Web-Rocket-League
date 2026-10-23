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
        bool _public = true;
        Vector2 _scroll;
        float _toast; string _toastText = "";
        GUIStyle _tag, _err;
        string _cloud;

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
            float k = Mathf.Clamp(Screen.height / 720f, 1f, 2.5f);
            GUIUtility.ScaleAroundPivot(new Vector2(k, k), Vector2.zero);
            float W = Screen.width / k, H = Screen.height / k;
            _tag ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 15, wordWrap = false };
            _err ??= new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };

            bool inGame = _ns.State == NetState.Host || _ns.State == NetState.Client;
            if (inGame) { DrawTags(k); DrawBanner(W); DrawScores(); }
            else if (!_menu && _ns.State == NetState.Offline)
            {
                if (GUI.Button(new Rect(W - 338, 14, 80, 46), "Online")) { _menu = true; _scr = Scr.Main; }
            }
            if (Time.unscaledTime < _toast) GUI.Box(new Rect(W * 0.5f - 130, 134, 260, 36), _toastText, UiTheme.Toast);

            if (_menu || _ns.State == NetState.Connecting) DrawMenu(W, H);
        }

        // ------------------------------------------------------------------------------------------------ in game

        void DrawBanner(float W)
        {
            string code = string.IsNullOrEmpty(_ns.Code) ? "—" : _ns.Code;
            var me = LocalPlayer();
            string lag = _ns.State == NetState.Client && me != null ? $"   ·   {me.InputLagMs:0} ms" : "";
            string role = _ns.State == NetState.Host ? "ANFITRIÓN" : "JUGADOR";
            GUI.Box(new Rect(W * 0.5f - 190, 8, 380, 38), $"{role}   CÓDIGO  <b>{code}</b>   ·   {_ns.PlayerCount}/{NetSession.MaxPlayers}{lag}", UiTheme.Pill);
            if (GUI.Button(new Rect(W * 0.5f + 196, 8, 76, 38), "Copiar") && !string.IsNullOrEmpty(_ns.Code))
            {
                GUIUtility.systemCopyBuffer = _ns.Code; _toast = Time.unscaledTime + 1.6f; _toastText = "Código copiado";
            }
            if (GUI.Button(new Rect(W * 0.5f - 272, 8, 76, 38), "Salir")) _ns.Leave();
        }

        PlayerNet LocalPlayer() { foreach (var p in PlayerNet.All) if (p.IsOwner) return p; return null; }

        void DrawScores()
        {
            int n = PlayerNet.All.Count;
            if (n == 0) return;
            float y = Minimap.Margin + Minimap.Size + 10f;
            GUI.Box(new Rect(16, y, 230, 34 + n * 24), "");
            GUI.Label(new Rect(26, y + 4, 210, 24), "Jugadores", UiTheme.Hint);
            var list = new System.Collections.Generic.List<PlayerNet>(PlayerNet.All);
            list.Sort((a, b) => a.Slot.Value.CompareTo(b.Slot.Value));
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                string me = p.IsOwner ? " (tú)" : "";
                string state = p.Alive.Value ? "<color=#7CFC9A>●</color>" : "<color=#9aa7b5>○</color>";
                GUI.Label(new Rect(26, y + 30 + i * 24, 214, 24), $"<color=#{Hex(p.ColorIdx.Value)}>■</color> {p.DisplayName}{me}   <b>{p.Score.Value}</b> {state}");
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
            var r = new Rect(W * 0.5f - 230, Mathf.Max(10f, H * 0.5f - 250), 460, Mathf.Min(500f, H - 20f));
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
                if (_ns.NeedsProjectId) CloudField();
                switch (_scr) { case Scr.Main: MainScreen(); break; case Scr.Create: CreateScreen(); break; case Scr.Join: JoinScreen(); break; }
            }
            GUILayout.EndArea();
            GUI.Button(new Rect(0, 0, W, H), GUIContent.none, GUIStyle.none);       // modal: swallow clicks that miss the panel
        }

        void CloudField()
        {
            _cloud ??= NetSession.SavedProjectId;
            GUILayout.Label("ID del proyecto de Unity Cloud (Project Settings > Services). Se guarda en este navegador.", UiTheme.Hint);
            _cloud = GUILayout.TextField(_cloud ?? "", 64);
            if (GUILayout.Button("Guardar y reintentar", GUILayout.Height(34))) { NetSession.SaveProjectId(_cloud); _ns.NeedsProjectId = false; _ns.Error = ""; _ns.RefreshList(); }
            GUILayout.Space(6);
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
            if (GUILayout.Button("Unirse a partida", GUILayout.Height(44))) { SaveName(); _ns.Error = ""; _scr = Scr.Join; _ns.RefreshList(); }
            if (GUILayout.Button(_ns.Root.carCtl != null && !_ns.Root.carCtl.Held ? "Cerrar" : "Jugar solo", GUILayout.Height(44)))
            {
                SaveName(); _ns.Error = ""; _menu = false; _ns.PlaySolo();
            }
        }

        void CreateScreen()
        {
            GUILayout.Label("Crear partida", UiTheme.Hint);
            GUILayout.Space(6);
            _public = GUILayout.Toggle(_public, "  Partida pública (aparece en la lista de partidas)");
            GUILayout.Label("Con código siempre puedes invitar a quien quieras. Máximo 4 jugadores.", UiTheme.Hint);
            GUILayout.Space(10);
            if (GUILayout.Button("Crear y entrar", GUILayout.Height(44))) _ns.HostGame(_public);
            if (GUILayout.Button("Atrás", GUILayout.Height(36))) { _ns.Error = ""; _scr = Scr.Main; }
        }

        void JoinScreen()
        {
            GUILayout.Label("Unirse a partida", UiTheme.Hint);
            GUILayout.Space(4);
            GUILayout.Label("Código de la partida");
            GUILayout.BeginHorizontal();
            _code = GUILayout.TextField(_code ?? "", 8).ToUpperInvariant();
            GUI.enabled = _code.Trim().Length >= 4;
            if (GUILayout.Button("Unirse", GUILayout.Width(110), GUILayout.Height(34))) _ns.JoinByCode(_code);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Partidas públicas");
            if (GUILayout.Button(_ns.ListBusy ? "…" : "Actualizar", GUILayout.Width(110))) _ns.RefreshList();
            GUILayout.EndHorizontal();
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(170));
            if (_ns.Games.Count == 0) GUILayout.Label(_ns.ListBusy ? "Buscando…" : "No hay partidas públicas ahora mismo.", UiTheme.Hint);
            foreach (var g in _ns.Games)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{g.name}  ·  {g.players}/{g.max}");
                if (GUILayout.Button("Unirse", GUILayout.Width(100))) _ns.JoinById(g.id);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("Atrás", GUILayout.Height(36))) { _ns.Error = ""; _scr = Scr.Main; }
        }
    }
}
