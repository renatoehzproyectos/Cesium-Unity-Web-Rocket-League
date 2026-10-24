using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

namespace RocketWorld.Net
{
    public enum NetState { Offline, Connecting, Host, Client }

    /// <summary>Starts the multiplayer layer without touching the offline game's boot code.</summary>
    static class NetBoot
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Pre() { GameRoot.RequireMenu = true; }          // GameRoot then shows the start menu and holds the car

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Post()
        {
            if (UnityEngine.Object.FindFirstObjectByType<NetSession>() != null) return;
            var go = new GameObject("RocketWorldNet");
            go.AddComponent<NetSession>();
            go.AddComponent<NetUi>();
        }
    }

    /// <summary>
    /// Netcode for GameObjects over a WebRTC transport (PeerJS, browser to browser): the host's browser is the server.
    /// Host: pick a 4-digit code and register it as the PeerJS id. Join: type the code. Max 4 players = 1 host + 3 visitors.
    /// </summary>
    public class NetSession : MonoBehaviour, INetRole
    {
        public const int MaxPlayers = 4;
        public static NetSession Instance;

        public GameRoot Root;
        public NetState State { get; private set; } = NetState.Offline;
        public string Code { get; private set; } = "";
        public string Error = "", Info = "";
        /// <summary>Connection state (diagnostics: shown in the code banner).</summary>
        public string NetInfo = "";
        public bool ReturnToMenu;                       // set when a session ended: the UI shows the menu again

        public bool IsClientOnly => State == NetState.Client;
        public int PlayerCount => PlayerNet.All.Count;

        NetworkManager _nm;
        PeerTransport _pt;
        GameObject _prefab;
        bool _clientPrepared;

        void Awake() { Instance = this; }

        void Start()
        {
            Root = FindFirstObjectByType<GameRoot>();
            if (Root == null) { enabled = false; return; }
            Root.net = this;
            PlayerNet.Root = Root;
            RocketBall.BallHit += OnBallHit;
        }

        void Update()
        {
            // diagnostics shown in the code banner
            NetInfo = _nm == null ? "" : _nm.IsServer ? (_nm.IsListening ? "anfitrión activo" : "anfitrión parado")
                : _nm.IsConnectedClient ? "conectado" : "sin conexión";
        }

        void OnDestroy()
        {
            RocketBall.BallHit -= OnBallHit;
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------------------------------------ public API

        /// <summary>"Jugar solo": the original offline game.</summary>
        public void PlaySolo() { if (Root != null) Root.BeginPlay(); }

        public void HostGame() { _ = HostCore(); }
        public void JoinByCode(string code) { _ = JoinCore((code ?? "").Trim()); }

        public void RequestBall()
        {
            foreach (var p in PlayerNet.All) if (p.IsOwner) { p.RequestBall(); return; }
        }

        public void Leave()
        {
            if (State != NetState.Host && State != NetState.Client) return;
            EndLocal(null);
            StopNet();
        }

        void StopNet()
        {
            try { if (_nm != null && _nm.IsListening) _nm.Shutdown(); } catch (Exception) { }
            try { if (_pt != null) _pt.Shutdown(); } catch (Exception) { }
        }

        // ------------------------------------------------------------------------------------------------ connect

        const string IdPrefix = "rocketworld-";
        string _discReason = "";

        static string RandomCode()
        {
            return UnityEngine.Random.Range(0, 10000).ToString("D4");          // 0000-9999: keeps leading zeros
        }

        async Task HostCore()
        {
            if (State != NetState.Offline) return;
            Error = ""; Info = "Creando partida…"; State = NetState.Connecting;
            try
            {
                EnsureNetworkManager();
                if (!await WaitFor(() => !_nm.ShutdownInProgress, 5f)) throw new Exception("La red anterior aún se está cerrando. Reintenta.");
                string code = "";
                for (int attempt = 0; attempt < 8 && code == ""; attempt++)
                {
                    string c = RandomCode();
                    _pt.OpenHost(IdPrefix + c);
                    if (!await WaitFor(() => _pt.Status != 1, 15f)) throw new Exception("El servidor de señalización no responde. Revisa tu conexión.");
                    if (_pt.Status == 2) code = c;
                    else if (_pt.Status != -2) throw new Exception(_pt.LastError);
                }
                if (code == "") throw new Exception("No se pudo reservar un código libre. Reintenta.");
                Code = code;
                if (!_nm.StartHost()) throw new Exception("No se pudo iniciar el anfitrión.");
                if (!await WaitFor(() => _nm.IsListening && _nm.IsServer, 10f)) throw new Exception("No se pudo iniciar el anfitrión.");
                Debug.Log("[RocketWorld] hosting, code " + Code);
                Begin(NetState.Host);
            }
            catch (Exception e) { Fail(e); }
        }

        async Task JoinCore(string code)
        {
            if (State != NetState.Offline) return;
            Error = ""; Info = "Uniéndose a la partida…"; State = NetState.Connecting;
            try
            {
                if (code.Length != 4) throw new Exception("El código son 4 números.");
                EnsureNetworkManager();
                if (!await WaitFor(() => !_nm.ShutdownInProgress, 5f)) throw new Exception("La red anterior aún se está cerrando. Reintenta.");
                Code = code; _discReason = "";
                _pt.HostId = IdPrefix + code;
                if (!_nm.StartClient()) throw new Exception("No se pudo iniciar el cliente.");
                float t0 = Time.realtimeSinceStartup;
                while (!_nm.IsConnectedClient)
                {
                    float el = Time.realtimeSinceStartup - t0;
                    Info = $"Conectando con el anfitrión… {el:0}s\n<size=11>{NetDetail()}</size>";
                    if (_pt.Status < 0 || !string.IsNullOrEmpty(_discReason))
                        throw new Exception(!string.IsNullOrEmpty(_pt.LastError) ? _pt.LastError : "Se cerró la conexión con el anfitrión. [" + NetDetail() + "]");
                    if (el > 40f) throw new Exception("No se pudo conectar con el anfitrión. [" + NetDetail() + "]");
                    await Task.Yield();
                }
                Begin(NetState.Client);
            }
            catch (Exception e) { Fail(e); }
        }

        string NetDetail()
        {
            if (_nm == null || _pt == null) return "sin red";
            string reason = !string.IsNullOrEmpty(_discReason) ? _discReason : _nm.DisconnectReason;
            return $"webrtc={_pt.Status} cliente={_nm.IsClient} conectado={_nm.IsConnectedClient} motivo='{reason}' err='{_pt.LastError}'";
        }

        void Begin(NetState role)
        {
            State = role; Info = "";
            Application.runInBackground = true;               // a hosted game must keep running when the tab loses focus
            if (role == NetState.Client) PrepareClient();
            Root.BeginPlay();
        }

        /// <summary>Joined player: the local car stops simulating and follows the host (idempotent).</summary>
        public void PrepareClient()
        {
            if (_clientPrepared || Root == null) return;
            _clientPrepared = true;
            if (Root.carCtl.intro != null) Root.carCtl.intro.Abort();
            Root.carCtl.SetSimulated(false);
            if (Root.ball != null) { Root.ball.Despawn(); Root.ball.SetPuppet(true); }
        }

        void Fail(Exception e)
        {
            Error = Pretty(e); Info = "";
            StopNet();
            State = NetState.Offline; Code = "";
        }

        /// <summary>Everything local that must be undone when a session ends (idempotent).</summary>
        void EndLocal(string reason)
        {
            if (State != NetState.Host && State != NetState.Client) return;
            bool wasClient = State == NetState.Client;
            State = NetState.Offline; Code = ""; _clientPrepared = false;
            if (!string.IsNullOrEmpty(reason)) Error = reason;
            Application.runInBackground = false;
            if (Root != null)
            {
                foreach (var c in RocketCar.All.ToArray()) if (c != null && c != Root.carCtl) Root.DestroyRemoteCar(c);
                Root.extraFoci.Clear();
                if (wasClient)
                {
                    if (Root.ball != null) Root.ball.SetPuppet(false);
                    Root.carCtl.SetSimulated(true);
                    Root.carCtl.Respawn(true);                       // keep playing solo where the puppet was
                }
            }
            ReturnToMenu = true;
        }

        // ------------------------------------------------------------------------------------------------ services / netcode setup

        void EnsureNetworkManager()
        {
            if (_nm != null) return;
            _prefab = Resources.Load<GameObject>("Net/PlayerNet");
            if (_prefab == null) throw new Exception("Falta Resources/Net/PlayerNet.prefab (menú RocketWorld > Ensure project setup).");

            var go = new GameObject("NetworkManager");
            go.SetActive(false);                                   // configure before NGO initialises
            _pt = go.AddComponent<PeerTransport>();
            _nm = go.AddComponent<NetworkManager>();
            _nm.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = _pt,
                EnableSceneManagement = false,                     // the world is built from code, nothing to synchronise
                ConnectionApproval = false,
                TickRate = 30,
            };
            _nm.NetworkConfig.Prefabs.Add(new NetworkPrefab { Prefab = _prefab });
            DontDestroyOnLoad(go);
            go.SetActive(true);
            _nm.OnClientConnectedCallback += OnClientConnected;
            _nm.OnClientDisconnectCallback += id => { if (_nm != null && !_nm.IsServer) _discReason = "desconectado (" + _nm.DisconnectReason + ")"; };
            _nm.OnClientStopped += _ => EndLocal("Conexión cerrada.");
            _nm.OnServerStopped += _ => EndLocal("Partida cerrada.");
        }

        void OnClientConnected(ulong clientId)
        {
            if (_nm == null || !_nm.IsServer) return;
            var go = Instantiate(_prefab);
            var pn = go.GetComponent<PlayerNet>();
            bool isHost = clientId == NetworkManager.ServerClientId;
            pn.initSlot = (byte)FreeSlot();
            pn.initColor = (byte)(isHost ? Mathf.Clamp(Root.settings.carColor, 0, GameSettings.CarColors.Length - 1) : FreeColor());
            pn.initName = isHost ? NetUi.LocalName : "Jugador " + (pn.initSlot + 1);
            go.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientId, true);
        }

        static int FreeSlot()
        {
            for (int i = 0; i < MaxPlayers; i++)
            {
                bool used = false;
                foreach (var p in PlayerNet.All) if (p.IsSpawned && p.Slot.Value == i) used = true;
                if (!used) return i;
            }
            return MaxPlayers - 1;
        }

        static int FreeColor()
        {
            for (int i = 0; i < GameSettings.CarColors.Length; i++)
            {
                bool used = false;
                foreach (var p in PlayerNet.All) if (p.IsSpawned && p.ColorIdx.Value == i) used = true;
                if (!used) return i;
            }
            return 0;
        }

        void OnBallHit(RocketCar car)
        {
            if (_nm == null || !_nm.IsServer) return;
            foreach (var p in PlayerNet.All) if (p.Car == car && p.IsSpawned) { p.Score.Value += 1; return; }
        }

        static async Task<bool> WaitFor(Func<bool> cond, float timeout)
        {
            float t0 = Time.realtimeSinceStartup;
            while (!cond())
            {
                if (Time.realtimeSinceStartup - t0 > timeout) return false;
                await Task.Yield();
            }
            return true;
        }

        static string Pretty(Exception e)
        {
            Debug.LogWarning("[RocketWorld] multiplayer error: " + e.GetType().Name + ": " + e.Message);
            string m = e.Message ?? "Error";
            return m.Length > 320 ? m.Substring(0, 320) + "…" : m;
        }
    }
}
