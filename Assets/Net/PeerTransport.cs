using System;
using System.Runtime.InteropServices;
using Unity.Netcode;
using UnityEngine;

namespace RocketWorld.Net
{
    /// <summary>
    /// Netcode transport over WebRTC data channels (PeerJS, see RocketWorldPeer.jslib). Browser to browser, so a WebGL build can host.
    /// Host = client id 0 in transport terms; visitors get 1, 2, 3. Reliable traffic uses the ordered channel, unreliable the unordered one.
    /// </summary>
    public class PeerTransport : NetworkTransport
    {
        public override ulong ServerClientId => 0;

        /// <summary>PeerJS id of the host to join (client side).</summary>
        public string HostId = "";

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void RW_PeerHost(string id);
        [DllImport("__Internal")] static extern void RW_PeerClient(string hostId);
        [DllImport("__Internal")] static extern int RW_PeerStatus();
        [DllImport("__Internal")] static extern string RW_PeerError();
        [DllImport("__Internal")] static extern int RW_PeerCount();
        [DllImport("__Internal")] static extern int RW_PeerSend(int id, byte[] data, int len, int reliable);
        [DllImport("__Internal")] static extern int RW_PeerPoll(int[] info, byte[] data, int cap);
        [DllImport("__Internal")] static extern void RW_PeerClose(int id);
        [DllImport("__Internal")] static extern void RW_PeerShutdown();
#else
        static void RW_PeerHost(string id) { }
        static void RW_PeerClient(string hostId) { }
        static int RW_PeerStatus() => -1;
        static string RW_PeerError() => "El multijugador solo funciona en el navegador (WebGL).";
        static int RW_PeerCount() => 0;
        static int RW_PeerSend(int id, byte[] data, int len, int reliable) => 0;
        static int RW_PeerPoll(int[] info, byte[] data, int cap) => 0;
        static void RW_PeerClose(int id) { }
        static void RW_PeerShutdown() { }
#endif

        readonly byte[] _rx = new byte[256 * 1024];
        readonly byte[] _tx = new byte[256 * 1024];
        readonly int[] _info = new int[2];

        /// <summary>0 idle, 1 working, 2 open, -1 error, -2 host id already taken.</summary>
        public int Status => RW_PeerStatus();
        public string LastError => RW_PeerError();
        public int ConnectedPeers => RW_PeerCount();

        /// <summary>Registers this browser as host with the signalling server (async: poll Status).</summary>
        public void OpenHost(string peerId) => RW_PeerHost(peerId);

        public override void Initialize(NetworkManager networkManager = null) { }

        public override bool StartServer() => RW_PeerStatus() == 2;

        public override bool StartClient() { RW_PeerClient(HostId); return true; }

        public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery delivery)
        {
            if (payload.Count > _tx.Length) return;
            Buffer.BlockCopy(payload.Array, payload.Offset, _tx, 0, payload.Count);
            bool reliable = delivery != NetworkDelivery.Unreliable && delivery != NetworkDelivery.UnreliableSequenced;
            RW_PeerSend((int)clientId, _tx, payload.Count, reliable ? 1 : 0);
        }

        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
        {
            clientId = 0; payload = default; receiveTime = Time.realtimeSinceStartup;
            int t = RW_PeerPoll(_info, _rx, _rx.Length);
            if (t == 0) return NetworkEvent.Nothing;
            clientId = (ulong)_info[0];
            switch (t)
            {
                case 1: return NetworkEvent.Connect;
                case 2: payload = new ArraySegment<byte>(_rx, 0, _info[1]); return NetworkEvent.Data;
                default: return NetworkEvent.Disconnect;
            }
        }

        public override void DisconnectRemoteClient(ulong clientId) => RW_PeerClose((int)clientId);
        public override void DisconnectLocalClient() => RW_PeerShutdown();
        public override ulong GetCurrentRtt(ulong clientId) => 0;
        public override void Shutdown() => RW_PeerShutdown();
    }
}
