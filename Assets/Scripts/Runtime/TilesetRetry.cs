using System;
using System.Threading;
using CesiumForUnity;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Network watchdog for the tileset. When the very first requests ("Failed to fetch" on the Cesium ion endpoint or Google's root.json: the phone
    /// lost its connection, switched network, or a request was dropped) fail, Cesium for Unity does not try again by itself and the map stays empty.
    /// This waits for the browser to report a connection, then recreates the native tileset with a growing delay (2 s, 4 s ... 30 s) until tiles arrive.
    /// Sits on the tileset's GameObject, so it disappears with it.
    /// </summary>
    public class TilesetRetry : MonoBehaviour
    {
        public GameRoot root;
        Cesium3DTileset _ts;
        float _next, _retryAt = -1f;
        int _tries, _rootFails;
        bool _listening, _toasted;

        void OnEnable()
        {
            _ts = GetComponent<Cesium3DTileset>();
            _next = Time.unscaledTime + 4f;
            if (!_listening) { Application.logMessageReceivedThreaded += OnLog; _listening = true; }
        }

        void OnDisable() { if (_listening) { Application.logMessageReceivedThreaded -= OnLog; _listening = false; } }

        // Cesium's native logger runs on worker threads: counters only, no Unity API.
        void OnLog(string msg, string stack, LogType type)
        {
            if (type == LogType.Log || string.IsNullOrEmpty(msg)) return;
            if (msg.IndexOf("Failed to fetch", StringComparison.OrdinalIgnoreCase) < 0) return;
            bool isRoot = msg.IndexOf("/endpoint", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("root.json", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("tileset.json", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isRoot) Interlocked.Increment(ref _rootFails);
        }

        void Update()
        {
            float now = Time.unscaledTime;
            if (_ts == null || now < _next) return;
            _next = now + 1f;
            if (transform.childCount > 0) { _tries = 0; _retryAt = -1f; _toasted = false; Interlocked.Exchange(ref _rootFails, 0); return; }   // tiles exist: all fine
            if (_retryAt < 0f)
            {
                if (Volatile.Read(ref _rootFails) == 0) return;                               // nothing failed: it is just still loading
                _retryAt = now + Mathf.Min(30f, 2f * (1 << Mathf.Min(_tries, 4)));
                if (!_toasted && root != null && root.hud != null) { _toasted = true; root.hud.ShowToast("Sin conexión con el mapa. Reintentando…"); }
                return;
            }
            if (now < _retryAt) return;
            if (Application.internetReachability == NetworkReachability.NotReachable) { _retryAt = now + 3f; return; }   // wait for the browser to say it is online
            _retryAt = -1f; _tries++;
            Interlocked.Exchange(ref _rootFails, 0);
            Debug.Log("[RocketWorld] tileset retry " + _tries + ": recreating the map after a network failure");
            try
            {
                var m = typeof(Cesium3DTileset).GetMethod("RecreateTileset", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (m != null) m.Invoke(_ts, null); else { var go = _ts.gameObject; go.SetActive(false); go.SetActive(true); }
            }
            catch (Exception e) { Debug.LogWarning("[RocketWorld] tileset retry failed: " + e.Message); }
        }
    }
}
