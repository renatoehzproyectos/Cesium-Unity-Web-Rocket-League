using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace RocketWorld.Net
{
    public enum NetState { Offline, Connecting, Host, Client }

    /// <summary>A public game in the lobby list.</summary>
    public struct ListedGame { public string id, name; public int players, max; }

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
    /// Unity Multiplayer Services (Authentication + Lobby + Relay, through the Sessions API) and Netcode for GameObjects.
    /// Host: create a session (join code from Lobby, traffic through Relay) and play as the host. Join: type the code or pick a public game.
    /// Max 4 players = 1 host + 3 visitors. WebGL uses secure WebSockets (the SDK default on the Web platform).
    /// </summary>
    public class NetSession : MonoBehaviour, INetRole
    {
        public const int MaxPlayers = 4;
        public static NetSession Instance;

        public GameRoot Root;
        public NetState State { get; private set; } = NetState.Offline;
        public string Code { get; private set; } = "";
        public string Error = "", Info = "";
        public bool ReturnToMenu;                       // set when a session ended: the UI shows the menu again
        public readonly List<ListedGame> Games = new List<ListedGame>();
        public bool ListBusy;

        public bool IsClientOnly => State == NetState.Client;
        public int PlayerCount => PlayerNet.All.Count;

        NetworkManager _nm;
        UnityTransport _ut;
        GameObject _prefab;
        ISession _session;
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

        void OnDestroy()
        {
            RocketBall.BallHit -= OnBallHit;
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------------------------------------ public API

        /// <summary>"Jugar solo": the original offline game.</summary>
        public void PlaySolo() { if (Root != null) Root.BeginPlay(); }

        public void HostGame(bool publicGame) { _ = HostCore(publicGame); }
        public void JoinByCode(string code) { _ = JoinCore(() => MultiplayerService.Instance.JoinSessionByCodeAsync(code.Trim().ToUpperInvariant()), code.Trim().ToUpperInvariant()); }
        public void JoinById(string id) { _ = JoinCore(() => MultiplayerService.Instance.JoinSessionByIdAsync(id), ""); }

        public async void RefreshList()
        {
            if (ListBusy || State != NetState.Offline) return;
            ListBusy = true; Error = "";
            try
            {
                await InitServices();
                var res = await MultiplayerService.Instance.QuerySessionsAsync(new QuerySessionsOptions());
                Games.Clear();
                foreach (var s in res.Sessions)
                    if (s.AvailableSlots > 0) Games.Add(new ListedGame { id = s.Id, name = s.Name, players = s.MaxPlayers - s.AvailableSlots, max = s.MaxPlayers });
            }
            catch (Exception e) { Error = Pretty(e); }
            ListBusy = false;
        }

        public void RequestBall()
        {
            foreach (var p in PlayerNet.All) if (p.IsOwner) { p.RequestBall(); return; }
        }

        public async void Leave()
        {
            if (State != NetState.Host && State != NetState.Client) return;
            var s = _session; bool host = State == NetState.Host; _session = null;
            EndLocal(null);
            try { if (s != null) { if (host) await s.AsHost().DeleteAsync(); else await s.LeaveAsync(); } } catch (Exception) { }
            try { if (_nm != null && _nm.IsListening) _nm.Shutdown(); } catch (Exception) { }
        }

        // ------------------------------------------------------------------------------------------------ connect

        async Task HostCore(bool publicGame)
        {
            if (State != NetState.Offline) return;
            Error = ""; Info = "Creando partida…"; State = NetState.Connecting;
            try
            {
                await InitServices();
                EnsureNetworkManager();
                var opts = new SessionOptions { Name = NetUi.LocalName + "'s game", MaxPlayers = MaxPlayers, IsPrivate = !publicGame }.WithRelayNetwork();
                _session = await MultiplayerService.Instance.CreateSessionAsync(opts);
                Hook(_session);
                Code = _session.Code ?? "";
                if (!await WaitFor(() => _nm.IsListening && _nm.IsServer, 15f)) throw new Exception("No se pudo iniciar el anfitrión.");
                Begin(NetState.Host);
            }
            catch (Exception e) { await Fail(e); }
        }

        async Task JoinCore(Func<Task<ISession>> join, string typedCode)
        {
            if (State != NetState.Offline) return;
            Error = ""; Info = "Uniéndose a la partida…"; State = NetState.Connecting;
            try
            {
                await InitServices();
                EnsureNetworkManager();
                _session = await join();
                Hook(_session);
                Code = !string.IsNullOrEmpty(_session.Code) ? _session.Code : typedCode;
                if (!await WaitFor(() => _nm.IsConnectedClient, 20f)) throw new Exception("No se pudo conectar con el anfitrión.");
                Begin(NetState.Client);
            }
            catch (Exception e) { await Fail(e); }
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

        async Task Fail(Exception e)
        {
            Error = Pretty(e); Info = "";
            var s = _session; _session = null;
            try { if (s != null) await s.LeaveAsync(); } catch (Exception) { }
            try { if (_nm != null && _nm.IsListening) _nm.Shutdown(); } catch (Exception) { }
            State = NetState.Offline; Code = "";
        }

        void Hook(ISession s)
        {
            s.Deleted += () => EndLocal("El anfitrión cerró la partida.");
            s.RemovedFromSession += () => EndLocal("Has salido de la partida.");
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

        static async Task InitServices()
        {
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                var o = new InitializationOptions();
                o.SetProfile("rw" + Guid.NewGuid().ToString("N").Substring(0, 12));      // one identity per page load (two tabs = two players)
                await UnityServices.InitializeAsync(o);
            }
            if (!AuthenticationService.Instance.IsSignedIn) await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }

        void EnsureNetworkManager()
        {
            if (_nm != null) return;
            _prefab = Resources.Load<GameObject>("Net/PlayerNet");
            if (_prefab == null) throw new Exception("Falta Resources/Net/PlayerNet.prefab (menú RocketWorld > Ensure project setup).");

            var go = new GameObject("NetworkManager");
            go.SetActive(false);                                   // configure before NGO initialises
            _ut = go.AddComponent<UnityTransport>();
            _nm = go.AddComponent<NetworkManager>();
            _nm.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = _ut,
                EnableSceneManagement = false,                     // the world is built from code, nothing to synchronise
                ConnectionApproval = false,
                TickRate = 30,
            };
            _nm.NetworkConfig.Prefabs.Add(new NetworkPrefab { Prefab = _prefab });
            DontDestroyOnLoad(go);
            go.SetActive(true);
#if UNITY_WEBGL && !UNITY_EDITOR
            _ut.UseWebSockets = true;                              // browsers: Relay over secure WebSockets
#endif
            _nm.OnClientConnectedCallback += OnClientConnected;
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
            string m = e.Message ?? "Error";
            if (m.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("NotFound", StringComparison.OrdinalIgnoreCase) >= 0) return "No existe una partida con ese código.";
            if (m.IndexOf("full", StringComparison.OrdinalIgnoreCase) >= 0) return "La partida está llena (máx. 4 jugadores).";
            return m.Length > 160 ? m.Substring(0, 160) + "…" : m;
        }
    }
}
