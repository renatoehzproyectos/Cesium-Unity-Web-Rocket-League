using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace RocketWorld.Net
{
    /// <summary>
    /// One per player (the NGO player object, prefab Resources/Net/PlayerNet, built by Editor/NetPrefabSetup).
    ///
    /// Network model (built for up to 15 players, minimum latency):
    ///   * Every player simulates ONLY their own car locally (zero input lag, own car never rubber-bands). The host does not simulate or
    ///     stream terrain for the others, so its cost does not grow with the player count.
    ///   * owner -> host   OwnerStateRpc  own pose (31 B, + the intro bus while it exists), unreliable, 30 Hz (20 Hz above 8 players)
    ///   * host  -> player WorldRpc       ONE batched datagram per player per tick: every other car / bus + the ball, compact codec
    ///                                    (<see cref="NetCodec"/>), only entries that changed, distance based update rate (far cars 10 / 3 Hz)
    ///   * other cars are interpolated with an adaptive delay (follows the measured jitter); on every machine they are kinematic
    ///     bodies moved each physics tick, so the own car and the ball collide with them
    ///   * the ball is the only host-authoritative simulation (hits by remote players are applied when the host sees their car touch it)
    ///   * NetworkVariables: name, score, alive, colour, slot, max players; reliable RPCs: name, ball request; ping/pong for the RTT readout
    /// </summary>
    public class PlayerNet : NetworkBehaviour
    {
        public static readonly List<PlayerNet> All = new List<PlayerNet>();
        public static GameRoot Root;

        public NetworkVariable<FixedString32Bytes> PlayerName = new NetworkVariable<FixedString32Bytes>();
        public NetworkVariable<int> Score = new NetworkVariable<int>(0);
        public NetworkVariable<bool> Alive = new NetworkVariable<bool>(false);
        public NetworkVariable<byte> ColorIdx = new NetworkVariable<byte>(0);
        public NetworkVariable<byte> Slot = new NetworkVariable<byte>(0);
        public NetworkVariable<byte> MaxPlayersNv = new NetworkVariable<byte>(4);

        // set by NetSession on the host before Spawn (written into the NetworkVariables in OnNetworkSpawn)
        [System.NonSerialized] public byte initSlot, initColor, initMax = 4;
        [System.NonSerialized] public string initName = "";

        public RocketCar Car { get; private set; }
        public string DisplayName => PlayerName.Value.ToString();

        const int Sz = NetCodec.EntrySize;
        const int MaxBatch = 1000;                  // bytes: one unreliable datagram (the transport sends <= 1100 B on the unordered channel)
        const float InterpFloor = 0.05f, InterpCeil = 0.30f;

        /// <summary>Send rate of own pose (owner -> host) and of the host's batches. Fewer packets per second when the room is full.</summary>
        static float SendHz => All.Count > 8 ? 20f : 30f;

        // ---------------- receive side clock (shared by every remote object: one batch carries all of them)
        static float _batchT = -1f, _batchInterval = 1f / 30f, _jitter;
        /// <summary>Remote objects are drawn this many seconds in the past: 1.5 batch intervals + 3 x measured jitter.</summary>
        public static float InterpDelay => Mathf.Clamp(_batchInterval * 1.5f + _jitter * 3f, InterpFloor, InterpCeil);
        public static readonly PoseBuffer BallBuf = new PoseBuffer();
        /// <summary>Round trip time to the host in ms (joined players), measured by ping / pong.</summary>
        public static float RttMs { get; private set; }
        /// <summary>First pose of the host's car received by a joining player (the world is recentred there before the player starts).</summary>
        public static bool HostPoseKnown; public static PoseSnap HostPose;
        static int _lodTick;

        public static void ResetStatics()
        {
            _batchT = -1f; _batchInterval = 1f / 30f; _jitter = 0f; RttMs = 0f; HostPoseKnown = false; _lodTick = 0;
            BallBuf.Clear();
        }

        // ---------------- owner
        float _nextSend, _nextPing; ushort _seq, _busSeq, _ballSeq; bool _planeSent;
        // ---------------- host: latest raw entries of this player (relayed untouched to the others)
        readonly byte[] _raw = new byte[Sz], _rawBus = new byte[Sz];
        bool _hasRaw, _hasBus, _lastRunning; ushort _rawSeq, _rawBusSeq;
        readonly ushort[] _sentSeq = new ushort[256]; readonly bool[] _sentHas = new bool[256];     // what this receiver was already sent
        readonly byte[] _batch = new byte[1 + (MaxBatch / Sz + 1) * Sz];
        // ---------------- everyone: buffers of this player's car / bus
        readonly PoseBuffer _buf = new PoseBuffer();
        readonly PoseBuffer _planeBuf = new PoseBuffer();
        PlaneIntro _visPlane; float _planeRecvT;

        public static FixedString32Bytes Fs(string s) { var f = new FixedString32Bytes(); f.CopyFromTruncated(s ?? ""); return f; }

        // ------------------------------------------------------------------------------------------------ lifecycle

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            var root = Root;
            if (root == null) return;
            PlayerName.OnValueChanged += (a, b) => { if (Car != null) Car.playerName = b.ToString(); };

            if (IsServer) { Slot.Value = initSlot; ColorIdx.Value = initColor; PlayerName.Value = Fs(initName); MaxPlayersNv.Value = initMax; }

            if (IsOwner)
            {
                Car = root.carCtl;                                                // my own car: simulated right here
                if (!IsServer)
                {
                    SetNameRpc(Fs(NetUi.LocalName));
                    int ci = Mathf.Clamp(ColorIdx.Value, 0, GameSettings.CarColors.Length - 1);   // host-assigned colour (not saved)
                    root.settings.carColor = ci;
                    CarVisual.SetBodyColor(root.carVisual, GameSettings.CarColors[ci].color);
                }
                else Car.slot = initSlot;
                Car.playerName = IsServer ? initName : NetUi.LocalName;
                NetSession.Instance?.OnLocalPlayerSpawned(this);
            }
            else
            {
                string nm = IsServer ? initName : DisplayName;
                int col = IsServer ? initColor : ColorIdx.Value;
                Car = root.BuildRemoteCar(col, nm, false);                        // a puppet: pose from the network, kinematic body
                Car.slot = IsServer ? initSlot : Slot.Value; Car.playerName = nm;
            }
        }

        public override void OnNetworkDespawn()
        {
            All.Remove(this);
            if (_visPlane != null) { _visPlane.Abort(); Destroy(_visPlane); _visPlane = null; }
            if (Root != null && Car != null && Car != Root.carCtl) Root.DestroyRemoteCar(Car);
            Car = null;
        }

        public override void OnDestroy() { All.Remove(this); base.OnDestroy(); }

        // ------------------------------------------------------------------------------------------------ per frame

        void Update()
        {
            if (!IsSpawned || Root == null) return;
            if (IsServer)
            {
                bool running = IsOwner ? (Car != null && Car.CurrentPhase == RocketCar.Phase.Running) : _lastRunning;
                if (Alive.Value != running) Alive.Value = running;
            }
            if (!IsOwner || Car == null) return;
            OwnerTick();
            if (!IsServer && Time.unscaledTime >= _nextPing) { _nextPing = Time.unscaledTime + 1f; PingRpc(Time.unscaledTime); }
        }

        void LateUpdate()
        {
            if (!IsSpawned || Root == null) return;
            if (!IsOwner)
            {
                ApplyPlane();
                if (Car != null) ApplyPuppet();
            }
            else if (!IsServer) ApplyBall();                                      // joined players show the host's ball
        }

        // ------------------------------------------------------------------------------------------------ owner: send my state

        PoseSnap Pack(Vector3 pos, Quaternion rot, Vector3 vel, ushort seq, int flags)
        {
            GeoSync.ToEcef(Root.georeference, pos, rot, vel, out double x, out double y, out double z, out Vector3 f, out Vector3 u, out Vector3 v);
            return new PoseSnap { seq = seq, x = x, y = y, z = z, f = f, u = u, v = v, flags = (byte)flags };
        }

        PoseSnap PackCar()
        {
            var rb = Car.Body; bool running = Car.CurrentPhase == RocketCar.Phase.Running;
            var s = Pack(rb.position, rb.rotation, rb.isKinematic ? Vector3.zero : rb.linearVelocity, ++_seq,
                (Car.State.isOnGround ? 1 : 0) | (Car.IsBoosting ? 2 : 0) | (running ? 4 : 0) | (Car.InBoat ? 8 : 0) | ((Car.InPlane || Car.InVehicle) ? 16 : 0));
            if (Car.InVehicle) { s.veh = (byte)Car.vehicle.Kind; s.aux = Car.vehicle.RampOpen01; }
            return s;
        }

        void OwnerTick()
        {
            float now = Time.unscaledTime, iv = 1f / SendHz;
            if (now < _nextSend) return;
            _nextSend = Mathf.Max(_nextSend + iv, now - iv);

            var car = PackCar();
            bool sendBus = false; PoseSnap bus = default;
            var intro = Car.intro;
            if (intro != null && intro.TryGetPose(out var bp, out var br, out float ramp))
            {
                bus = Pack(bp, br, Vector3.zero, ++_busSeq, 4); bus.aux = ramp; sendBus = true; _planeSent = true;
            }
            else if (_planeSent) { _planeSent = false; bus.seq = ++_busSeq; sendBus = true; }     // "the bus is gone" marker
            byte slot = Slot.Value;

            if (IsServer)
            {
                NetCodec.Write(_raw, 0, slot, car); _hasRaw = true; _rawSeq = car.seq;
                if (sendBus) { NetCodec.Write(_rawBus, 0, (byte)(NetCodec.BusBase + slot), bus); _hasBus = true; _rawBusSeq = bus.seq; }
                ServerBroadcast();
            }
            else
            {
                int n = sendBus ? 2 : 1;
                var pkt = new byte[1 + n * Sz]; pkt[0] = (byte)n;
                NetCodec.Write(pkt, 1, slot, car);
                if (sendBus) NetCodec.Write(pkt, 1 + Sz, (byte)(NetCodec.BusBase + slot), bus);
                OwnerStateRpc(pkt);
            }
        }

        // ------------------------------------------------------------------------------------------------ host: receive + relay

        [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable)]
        void OwnerStateRpc(byte[] data, RpcParams rpcParams = default)
        {
            if (rpcParams.Receive.SenderClientId != OwnerClientId || data == null || data.Length < 1 + Sz) return;
            float now = Time.unscaledTime; int n = Mathf.Min(data[0], 2); byte slot = Slot.Value;
            for (int i = 0; i < n; i++)
            {
                int off = 1 + i * Sz; if (off + Sz > data.Length) break;
                NetCodec.Read(data, off, out byte id, out PoseSnap s);
                if (id < NetCodec.BusBase)
                {
                    if (_hasRaw && (short)(s.seq - _rawSeq) <= 0) continue;                  // stale / reordered
                    Buffer.BlockCopy(data, off, _raw, 0, Sz); _raw[0] = slot;                  // the slot is the server's, never the sender's
                    _hasRaw = true; _rawSeq = s.seq; _lastRunning = s.Running;
                    _buf.Add(now, s);
                }
                else
                {
                    if (_hasBus && (short)(s.seq - _rawBusSeq) <= 0) continue;
                    Buffer.BlockCopy(data, off, _rawBus, 0, Sz); _rawBus[0] = (byte)(NetCodec.BusBase + slot);
                    _hasBus = true; _rawBusSeq = s.seq;
                    _planeBuf.Add(now, s); _planeRecvT = now;
                }
            }
        }

        bool ShouldSend(byte id, ushort seq, float d2)
        {
            if (!_sentHas[id]) return true;
            if (_sentSeq[id] == seq) return false;                                    // nothing new
            int every = d2 < 600f * 600f ? 1 : d2 < 4000f * 4000f ? 3 : 10;           // far cars need fewer updates: 30 / 10 / 3 Hz
            return every == 1 || (_lodTick + Slot.Value + id) % every == 0;
        }

        void MarkSent(byte id, ushort seq) { _sentHas[id] = true; _sentSeq[id] = seq; }

        /// <summary>Host: ONE datagram per player with everything it needs (instead of N x (N-1) separate messages).</summary>
        void ServerBroadcast()
        {
            _lodTick++;
            var b = Root.ball; bool ballLive = b != null && b.Exists;
            PoseSnap ball;
            if (ballLive) ball = Pack(b.Position, b.Rotation, b.Velocity, ++_ballSeq, 4); else { ball = default; ball.seq = ++_ballSeq; }

            foreach (var r in All)
            {
                if (r == this || !r.IsSpawned || r.Car == null) continue;
                Vector3 rp = r.Car.transform.position; int off = 1, n = 0;
                foreach (var p in All)
                {
                    if (p == r || !p.IsSpawned || p.Car == null) continue;
                    float d2 = (p.Car.transform.position - rp).sqrMagnitude;
                    if (p._hasRaw && off + Sz <= MaxBatch && r.ShouldSend(p._raw[0], p._rawSeq, d2))
                    {
                        Buffer.BlockCopy(p._raw, 0, _batch, off, Sz); off += Sz; n++; r.MarkSent(p._raw[0], p._rawSeq);
                    }
                    if (p._hasBus && off + Sz <= MaxBatch && r.ShouldSend(p._rawBus[0], p._rawBusSeq, d2))
                    {
                        Buffer.BlockCopy(p._rawBus, 0, _batch, off, Sz); off += Sz; n++; r.MarkSent(p._rawBus[0], p._rawBusSeq);
                    }
                }
                bool sendBall;
                if (ballLive) sendBall = (b.Position - rp).sqrMagnitude < 3000f * 3000f || _lodTick % 5 == 0;
                else sendBall = _lodTick % 10 == 0;                                    // "no ball" keep-alive
                if (sendBall && off + Sz <= MaxBatch) { NetCodec.Write(_batch, off, NetCodec.BallId, ball); off += Sz; n++; }
                if (n == 0) continue;
                _batch[0] = (byte)n;
                var arr = new byte[off]; Buffer.BlockCopy(_batch, 0, arr, 0, off);
                r.WorldRpc(arr);
            }
        }

        // ------------------------------------------------------------------------------------------------ player: receive the world

        [Rpc(SendTo.Owner, Delivery = RpcDelivery.Unreliable)]
        void WorldRpc(byte[] data)
        {
            if (data == null || data.Length < 1 + Sz) return;
            float now = Time.unscaledTime;
            if (_batchT > 0f)
            {
                float dt = Mathf.Clamp(now - _batchT, 0.005f, 0.25f);
                _jitter = Mathf.Lerp(_jitter, Mathf.Abs(dt - _batchInterval), 0.05f);
                _batchInterval = Mathf.Lerp(_batchInterval, dt, 0.05f);
            }
            _batchT = now;
            int n = data[0];
            for (int i = 0; i < n; i++)
            {
                int off = 1 + i * Sz; if (off + Sz > data.Length) break;
                NetCodec.Read(data, off, out byte id, out PoseSnap s);
                if (id == NetCodec.BallId) { BallBuf.Add(now, s); continue; }
                bool bus = id >= NetCodec.BusBase;
                var p = BySlot(bus ? id - NetCodec.BusBase : id);
                if (p == null || p == this) continue;
                if (bus) { p._planeBuf.Add(now, s); p._planeRecvT = now; }
                else
                {
                    p._buf.Add(now, s);
                    if (!HostPoseKnown && p.OwnerClientId == NetworkManager.ServerClientId && s.Running) { HostPose = s; HostPoseKnown = true; }
                    p._lastRunning = s.Running;
                }
            }
        }

        static PlayerNet BySlot(int slot)
        {
            foreach (var p in All) if (p.IsSpawned && p.Slot.Value == slot) return p;
            return null;
        }

        void ApplyPlane()
        {
            float now = Time.unscaledTime;
            bool show = _planeBuf.Sample(now - InterpDelay, 0.2f, out var s) && s.Running && now - _planeRecvT < 1.5f;
            if (!show) { if (_visPlane != null) { _visPlane.Abort(); Destroy(_visPlane); _visPlane = null; } return; }
            if (_visPlane == null)
            {
                _visPlane = Root.gameObject.AddComponent<PlaneIntro>();
                _visPlane.root = Root;
                if (!_visPlane.StartVisual()) { Destroy(_visPlane); _visPlane = null; _planeRecvT = -100f; return; }
            }
            GeoSync.FromEcef(Root.georeference, s.x, s.y, s.z, s.f, s.u, s.v, out var pos, out var rot, out _);
            _visPlane.SetVisualPose(pos, rot, s.aux);
        }

        void ApplyPuppet()
        {
            if (!_buf.Sample(Time.unscaledTime - InterpDelay, 0.25f, out var s)) return;
            GeoSync.FromEcef(Root.georeference, s.x, s.y, s.z, s.f, s.u, s.v, out var pos, out var rot, out var vel);
            Car.ApplyPuppetPose(pos, rot, vel, s.OnGround, s.Boosting, s.Running, s.Boat, s.Plane, s.VehicleKind, s.aux);
            Car.SetProxyTarget(pos, rot, vel);                                    // the kinematic body that others collide with
        }

        void ApplyBall()
        {
            var b = Root.ball;
            if (b == null || !b.puppet || !BallBuf.Sample(Time.unscaledTime - InterpDelay, 0.15f, out var s)) return;
            if (!s.Running) { b.NetSet(false, Vector3.zero, Quaternion.identity); return; }
            GeoSync.FromEcef(Root.georeference, s.x, s.y, s.z, s.f, s.u, s.v, out var pos, out var rot, out _);
            b.NetSet(true, pos, rot);
        }

        // ------------------------------------------------------------------------------------------------ ping

        [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable)]
        void PingRpc(float t, RpcParams rpcParams = default)
        {
            if (rpcParams.Receive.SenderClientId == OwnerClientId) PongRpc(t);
        }

        [Rpc(SendTo.Owner, Delivery = RpcDelivery.Unreliable)]
        void PongRpc(float t)
        {
            float rtt = Mathf.Clamp((Time.unscaledTime - t) * 1000f, 0f, 5000f);
            RttMs = RttMs <= 0f ? rtt : Mathf.Lerp(RttMs, rtt, 0.25f);
        }

        // ------------------------------------------------------------------------------------------------ misc

        /// <summary>Host: the Starship set off the Tsar Bomba here; the joined players only get the visual (position travels as ECEF, every machine has its own Unity origin).</summary>
        public void SendExplosion(Vector3 worldPos)
        {
            if (!IsServer || !IsSpawned || Root == null || Root.georeference == null) return;
            GeoSync.ToEcef(Root.georeference, worldPos, Quaternion.identity, Vector3.zero, out double x, out double y, out double z, out _, out _, out _);
            ExplosionRpc(x, y, z);
        }

        [Rpc(SendTo.NotServer)]
        void ExplosionRpc(double x, double y, double z)
        {
            if (Root == null || Root.georeference == null) return;
            GeoSync.FromEcef(Root.georeference, x, y, z, Vector3.forward, Vector3.up, Vector3.zero, out var pos, out _, out _);
            TsarBomba.Spawn(pos, true, null);
        }

        public const float TeleportMeters = 100f;

        /// <summary>My car goes 100 m straight up (my car is simulated here, so no round trip).</summary>
        public void TeleportAbove() { if (IsSpawned && IsOwner && Car != null) Car.TeleportAbove(TeleportMeters); }

        /// <summary>My car goes next to another player's (its pose is the interpolated one from the network).</summary>
        public void TeleportTo(PlayerNet target)
        {
            if (!IsSpawned || !IsOwner || target == null || target == this || target.Car == null || Car == null) return;
            Car.TeleportBeside(target.Car);
        }

        [Rpc(SendTo.Server)]
        void BallRequestRpc(RpcParams rpcParams = default)
        {
            if (rpcParams.Receive.SenderClientId == OwnerClientId && Car != null && Root.ball != null) Root.ball.Summon(Car);
        }

        [Rpc(SendTo.Server)]
        void SetNameRpc(FixedString32Bytes name, RpcParams rpcParams = default)
        {
            if (rpcParams.Receive.SenderClientId == OwnerClientId && name.Length > 0) PlayerName.Value = name;
        }

        /// <summary>Joined player: ask the host to put the ball in front of my car.</summary>
        public void RequestBall() { if (IsSpawned && !IsServer && IsOwner) BallRequestRpc(); }
    }
}
