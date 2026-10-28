using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace RocketWorld.Net
{
    /// <summary>
    /// One per player (the NGO player object, prefab Resources/Net/PlayerNet, built by Editor/NetPrefabSetup).
    /// Host authoritative:
    ///   client -> host   InputRpc    controls + sequence number, unreliable, every 20 ms
    ///   host   -> clients SnapshotRpc car state, unreliable, 30 Hz (clients interpolate; the own car is extrapolated)
    ///                     BallRpc     ball state (sent by the host's own PlayerNet)
    ///   NetworkVariables  name, score, alive, colour, slot
    ///   reliable RPCs     SetNameRpc, RescueRpc, BallRequestRpc
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

        // set by NetSession on the host before Spawn (written into the NetworkVariables in OnNetworkSpawn)
        [System.NonSerialized] public byte initSlot, initColor;
        [System.NonSerialized] public string initName = "";

        public RocketCar Car { get; private set; }
        public string DisplayName => PlayerName.Value.ToString();

        const float SnapHz = 30f;               // host -> clients
        const float InputInterval = 0.02f;      // client -> host (20 ms)
        const float InterpDelay = 0.10f;        // remote objects are shown ~3 snapshots in the past
        const float OwnLead = 0.04f;            // own car: extrapolate a little ahead of the newest snapshot

        // host
        ushort _lastSeq; bool _gotInput; float _lastInputT, _nextSnap; ushort _snapSeq, _ballSeq;
        // client (owner)
        ushort _seq, _lastAck; float _nextInput; bool _jumpAcc, _boostAcc, _centered, _hasDisp; PoseSnap _disp;
        // client (everyone)
        readonly PoseBuffer _buf = new PoseBuffer();
        readonly PoseBuffer _ballBuf = new PoseBuffer();
        readonly PoseBuffer _planeBuf = new PoseBuffer();
        PlaneIntro _visPlane; float _planeRecvT; ushort _planeSeq; bool _planeSent;

        /// <summary>Rough input -> host -> snapshot delay in ms (sequence numbers still in flight x 20 ms).</summary>
        public float InputLagMs => Mathf.Min(2000f, (ushort)(_seq - _lastAck) * InputInterval * 1000f);

        public static FixedString32Bytes Fs(string s) { var f = new FixedString32Bytes(); f.CopyFromTruncated(s ?? ""); return f; }

        // ------------------------------------------------------------------------------------------------ lifecycle

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            var root = Root;
            if (root == null) return;
            PlayerName.OnValueChanged += (a, b) => { if (Car != null) Car.playerName = b.ToString(); };

            if (IsServer)
            {
                Slot.Value = initSlot; ColorIdx.Value = initColor; PlayerName.Value = Fs(initName);
                if (IsOwner) Car = root.carCtl;                                   // the host's own car (local input)
                else
                {
                    Car = root.BuildRemoteCar(initColor, initName, true);         // physics for this player runs on the host
                    PlaceRemoteCar();
                    root.TrackRemote(Car.transform);
                }
                Car.slot = initSlot; Car.playerName = initName;
            }
            else
            {
                if (IsOwner)
                {
                    NetSession.Instance?.PrepareClient();
                    Car = root.carCtl;                                            // puppet of the host's simulation
                    SetNameRpc(Fs(NetUi.LocalName));
                    int ci = Mathf.Clamp(ColorIdx.Value, 0, GameSettings.CarColors.Length - 1);   // host-assigned colour (not saved)
                    root.settings.carColor = ci;
                    CarVisual.SetBodyColor(root.carVisual, GameSettings.CarColors[ci].color);
                }
                else Car = root.BuildRemoteCar(ColorIdx.Value, DisplayName, false);
            }
        }

        public override void OnNetworkDespawn()
        {
            All.Remove(this);
            if (_visPlane != null) { _visPlane.Abort(); Destroy(_visPlane); _visPlane = null; }
            if (Car != null && Car.intro != null && Car != Root.carCtl) { Car.intro.Abort(); Destroy(Car.intro); }
            if (Root != null && Car != null && Car != Root.carCtl) Root.DestroyRemoteCar(Car);
            Car = null;
        }

        public override void OnDestroy() { All.Remove(this); base.OnDestroy(); }

        void PlaceRemoteCar() { StartCoroutine(PlaceRemoteLater()); }

        /// <summary>Host: a remote player starts inside its own cargo plane, in formation with the host's (falls back to the ground if the plane cannot be built).</summary>
        System.Collections.IEnumerator PlaceRemoteLater()
        {
            yield return null; yield return null;                    // let the new car run its own Start first
            if (Car == null || Root == null) yield break;
            var intro = Root.gameObject.AddComponent<PlaneIntro>();
            intro.root = Root;
            Car.intro = intro;
            if (intro.BeginRemote(Car, Root.carCtl, initSlot)) yield break;
            Car.intro = null; Destroy(intro);
            PlaceRemoteCarOnGround();
        }

        void PlaceRemoteCarOnGround()
        {
            var host = Root.carCtl;
            bool rel = host != null && host.CurrentPhase == RocketCar.Phase.Running && !(host.intro != null && host.intro.BlocksRescue);
            var right = rel ? host.transform.right : Vector3.right; right.y = 0f;
            right = right.sqrMagnitude > 1e-4f ? right.normalized : Vector3.right;
            var basePos = rel ? host.transform.position : Vector3.zero;
            Car.SetSpawnColumn(basePos + right * (initSlot * 6f), rel, Root.settings.heading);
        }

        // ------------------------------------------------------------------------------------------------ per frame

        void Update()
        {
            if (!IsSpawned || Car == null || Root == null) return;
            if (IsServer) ServerTick();
            else if (IsOwner) ClientInput();
        }

        void LateUpdate()
        {
            if (!IsSpawned || Root == null || IsServer) return;
            ApplyPlane();
            if (Car != null) ApplyPuppet();
            if (OwnerClientId == NetworkManager.ServerClientId) ApplyBall();     // the host's PlayerNet carries the ball
        }

        // ------------------------------------------------------------------------------------------------ host

        void ServerTick()
        {
            float now = Time.unscaledTime;
            if (!IsOwner && now - _lastInputT > 0.5f) Car.netControls = default;           // packets stopped: do not keep a key stuck
            bool running = Car.CurrentPhase == RocketCar.Phase.Running;
            if (Alive.Value != running) Alive.Value = running;
            if (now < _nextSnap) return;
            _nextSnap = now + 1f / SnapHz;
            var rb = Car.Body;
            var s = Pack(rb.position, rb.rotation, rb.isKinematic ? Vector3.zero : rb.linearVelocity, ++_snapSeq,
                (Car.State.isOnGround ? 1 : 0) | (Car.IsBoosting ? 2 : 0) | (running ? 4 : 0) | (Car.InBoat ? 8 : 0) | ((Car.InPlane || Car.InVehicle) ? 16 : 0) | (Car.InVehicle ? (int)Car.vehicle.Kind << 5 : 0));   // bits 5-7: which vehicle (0 = Cessna / none)
            s.ack = _lastSeq;
            SnapshotRpc(s);
            SendPlane();
            if (IsOwner) SendBall();
        }

        void SendPlane()
        {
            var intro = Car.intro;
            if (intro != null && intro.TryGetPose(out var pos, out var rot, out float ramp))
            {
                var s = Pack(pos, rot, Vector3.zero, ++_planeSeq, 4);
                s.aux = ramp;
                PlaneRpc(s); _planeSent = true;
            }
            else if (_planeSent) { _planeSent = false; var e = default(PoseSnap); e.seq = ++_planeSeq; PlaneRpc(e); }
        }

        void SendBall()
        {
            var b = Root.ball;
            PoseSnap s;
            if (b != null && b.Exists) s = Pack(b.Position, b.Rotation, b.Velocity, ++_ballSeq, 4);
            else { s = default; s.seq = ++_ballSeq; }
            BallRpc(s);
        }

        PoseSnap Pack(Vector3 pos, Quaternion rot, Vector3 vel, ushort seq, int flags)
        {
            GeoSync.ToEcef(Root.georeference, pos, rot, vel, out double x, out double y, out double z, out Vector3 f, out Vector3 u, out Vector3 v);
            return new PoseSnap { seq = seq, x = x, y = y, z = z, f = f, u = u, v = v, flags = (byte)flags };
        }

        [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable)]
        void InputRpc(ulong packed, RpcParams rpcParams = default)
        {
            if (rpcParams.Receive.SenderClientId != OwnerClientId || Car == null) return;
            ushort seq = (ushort)(packed & 0xFFFF);
            if (_gotInput && (short)(seq - _lastSeq) <= 0) return;                         // stale / reordered input
            _gotInput = true; _lastSeq = seq; _lastInputT = Time.unscaledTime;
            Car.netControls = new CarInput.Controls
            {
                throttle = Sb(packed >> 16), steer = Sb(packed >> 24), pitch = Sb(packed >> 32), yaw = Sb(packed >> 40), roll = Sb(packed >> 48),
                jump = (packed & (1UL << 56)) != 0, boost = (packed & (1UL << 57)) != 0, handbrake = (packed & (1UL << 58)) != 0,
            };
        }

        [Rpc(SendTo.Server)]
        void RescueRpc(RpcParams rpcParams = default)
        {
            if (rpcParams.Receive.SenderClientId != OwnerClientId || Car == null) return;
            if (Car.intro != null && Car.intro.TryResetInBay()) return;         // inside the plane: back to the hold
            Car.Rescue();
        }

        [Rpc(SendTo.Server)]
        void TeleportAboveRpc(RpcParams rpcParams = default)
        {
            if (rpcParams.Receive.SenderClientId == OwnerClientId && Car != null) Car.TeleportAbove(TeleportMeters);
        }

        [Rpc(SendTo.Server)]
        void TeleportToPlayerRpc(ulong targetClientId, RpcParams rpcParams = default)
        {
            if (rpcParams.Receive.SenderClientId != OwnerClientId || Car == null) return;
            foreach (var p in All) if (p.OwnerClientId == targetClientId && p.Car != null) { Car.TeleportBeside(p.Car); return; }
        }

        public const float TeleportMeters = 100f;

        /// <summary>This player's car goes 100 m straight up (client: asks the host).</summary>
        public void TeleportAbove()
        {
            if (!IsSpawned || !IsOwner) return;
            if (IsServer) { if (Car != null) Car.TeleportAbove(TeleportMeters); } else TeleportAboveRpc();
        }

        /// <summary>This player's car goes next to another player (client: asks the host).</summary>
        public void TeleportTo(PlayerNet target)
        {
            if (!IsSpawned || !IsOwner || target == null || target == this) return;
            if (IsServer) { if (Car != null) Car.TeleportBeside(target.Car); } else TeleportToPlayerRpc(target.OwnerClientId);
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

        /// <summary>Client: ask the host to put the ball in front of my car.</summary>
        public void RequestBall() { if (IsSpawned && !IsServer && IsOwner) BallRequestRpc(); }

        static float Sb(ulong b) => (sbyte)(byte)(b & 0xFF) / 127f;
        static ulong Qb(float v) => (byte)(sbyte)Mathf.RoundToInt(Mathf.Clamp(v, -1f, 1f) * 127f);

        // ------------------------------------------------------------------------------------------------ joined client

        void ClientInput()
        {
            var ci = Root.carInput;
            if (ci == null) return;
            var c = ci.controls;
            _jumpAcc |= c.jump; _boostAcc |= c.boost;                      // a short tap between two packets must not be lost
            if (ci.ResetPressed) RescueRpc();
            float now = Time.unscaledTime;
            if (now < _nextInput) return;
            _nextInput = Mathf.Max(_nextInput + InputInterval, now - InputInterval);
            ulong p = (ulong)(++_seq)
                | (Qb(c.throttle) << 16) | (Qb(c.steer) << 24) | (Qb(c.pitch) << 32) | (Qb(c.yaw) << 40) | (Qb(c.roll) << 48)
                | (_jumpAcc ? 1UL << 56 : 0UL) | (_boostAcc ? 1UL << 57 : 0UL) | (c.handbrake ? 1UL << 58 : 0UL);
            _jumpAcc = false; _boostAcc = false;
            InputRpc(p);
        }

        [Rpc(SendTo.NotServer, Delivery = RpcDelivery.Unreliable)]
        void SnapshotRpc(PoseSnap s)
        {
            _buf.Add(Time.unscaledTime, s);
            if (IsOwner) _lastAck = s.ack;
        }

        [Rpc(SendTo.NotServer, Delivery = RpcDelivery.Unreliable)]
        void PlaneRpc(PoseSnap s) { _planeBuf.Add(Time.unscaledTime, s); _planeRecvT = Time.unscaledTime; }

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

        [Rpc(SendTo.NotServer, Delivery = RpcDelivery.Unreliable)]
        void BallRpc(PoseSnap s) { _ballBuf.Add(Time.unscaledTime, s); }

        void ApplyPuppet()
        {
            float now = Time.unscaledTime, dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            bool own = IsOwner;
            if (!_buf.Sample(own ? now + OwnLead : now - InterpDelay, own ? 0.25f : 0.2f, out var s)) return;
            if (own)
            {
                // blend toward the extrapolated host state: hides the small correction every time a snapshot arrives
                if (!_hasDisp || _disp.DistanceTo(s) > 25.0) { _disp = s; _hasDisp = true; }
                else _disp = PoseSnap.Lerp(_disp, s, 1f - Mathf.Exp(-dt * 30f));
                _disp.flags = s.flags;
                s = _disp;
                if (!_centered) { GeoSync.Recenter(Root.georeference, s.x, s.y, s.z); _centered = true; }
            }
            GeoSync.FromEcef(Root.georeference, s.x, s.y, s.z, s.f, s.u, s.v, out var pos, out var rot, out var vel);
            Car.ApplyPuppetPose(pos, rot, vel, s.OnGround, s.Boosting, s.Running, s.Boat, s.Plane, s.VehicleKind);
        }

        void ApplyBall()
        {
            var b = Root.ball;
            if (b == null || !b.puppet || !_ballBuf.Sample(Time.unscaledTime - InterpDelay, 0.15f, out var s)) return;
            if (!s.Running) { b.NetSet(false, Vector3.zero, Quaternion.identity); return; }
            GeoSync.FromEcef(Root.georeference, s.x, s.y, s.z, s.f, s.u, s.v, out var pos, out var rot, out _);
            b.NetSet(true, pos, rot);
        }
    }
}
