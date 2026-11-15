using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace RocketWorld
{
    [Serializable] public class GeoZone
    {
        public string id, name, state, detail;
        public double lat, lon, radius, groundHeight, bytes;
        public long createdAt, syncedAt;
        public int expected, stored, stale;
        public bool liveOnly;
    }

    /// <summary>
    /// Saved geographic zones and their tile store. The store itself is a service worker + IndexedDB in the page (TemplateData/rw-geocache.js):
    /// tiles requested by Cesium (and the water vector tiles) are answered from it when stored, and stored on the way back when they belong to a
    /// zone. This class only talks to it (request / poll, no callbacks) and keeps the list the zone panel shows.
    /// Outside a WebGL player there is no store: <see cref="Available"/> is false and the panel says so.
    /// </summary>
    public class GeoCache : MonoBehaviour
    {
        [Serializable] class Envelope { public int id; public bool ok; public string v, error; public string ev, phase; public int done, total; public double bytes; public bool truncated; }
        [Serializable] class ZoneList { public GeoZone[] items; }
        [Serializable] class StatsDto { public int tiles, zones; public double bytes, quota, usage, maxTotalBytes; public bool allowRestricted; public Counters counters; }
        [Serializable] public class Counters { public int hits, misses, stored, deduped, skipped, errors; public double bytesCache, bytesNet; }
        [Serializable] class InitDto { public bool available, allowRestricted; public string reason; }

        public struct Progress { public string phase; public int done, total; public double bytes; public bool truncated; public string error; }

        public bool Available { get; private set; }
        public string Reason { get; private set; } = "";
        public bool AllowRestricted { get; private set; }
        public readonly List<GeoZone> Zones = new List<GeoZone>();
        public readonly Dictionary<string, Progress> Active = new Dictionary<string, Progress>();
        public double StoredBytes, QuotaBytes; public int StoredTiles; public Counters Stats = new Counters();
        public string LastError = "";
        public event Action ZonesChanged;

        int _seq; float _next;
        readonly Dictionary<int, Action<Envelope>> _waiting = new Dictionary<int, Action<Envelope>>();

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int RW_Geo_Available();
        [DllImport("__Internal")] static extern string RW_Geo_Reason();
        [DllImport("__Internal")] static extern void RW_Geo_Request(string json);
        [DllImport("__Internal")] static extern int RW_Geo_Pending();
        [DllImport("__Internal")] static extern string RW_Geo_Poll();
        static bool JsAvailable() => RW_Geo_Available() == 1;
        static string JsReason() => RW_Geo_Reason();
        static void JsRequest(string j) => RW_Geo_Request(j);
        static int JsPending() => RW_Geo_Pending();
        static string JsPoll() => RW_Geo_Poll();
#else
        static bool JsAvailable() => false;
        static string JsReason() => "Saved zones need the WebGL player (the tile store is a browser service worker).";
        static void JsRequest(string j) { }
        static int JsPending() => 0;
        static string JsPoll() => "";
#endif

        void Start() { Probe(); }

        void Probe()
        {
            Available = JsAvailable(); Reason = Available ? "" : JsReason();
            if (!Available) return;
            Call("init", "{}", e => { if (!e.ok) return; var d = JsonUtility.FromJson<InitDto>(e.v); AllowRestricted = d.allowRestricted; });
            Refresh();
        }

        void Update()
        {
            if (!Available) { if (Time.unscaledTime > _next) { _next = Time.unscaledTime + 3f; Probe(); } return; }   // the page may become controlled a moment after start
            int guard = 16;
            while (guard-- > 0 && JsPending() > 0) Handle(JsPoll());
        }

        void Handle(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            Envelope e; try { e = JsonUtility.FromJson<Envelope>(json); } catch (Exception) { return; }
            if (e == null) return;
            if (e.ev == "progress")
            {
                var raw = JsonUtility.FromJson<ProgressRaw>(json);
                if (raw.phase == "done" || raw.phase == "error" || raw.phase == "cancelled") Active.Remove(raw.zone);
                else Active[raw.zone] = new Progress { phase = raw.phase, done = raw.done, total = raw.total, bytes = raw.bytes, truncated = raw.truncated, error = raw.error };
                if (!string.IsNullOrEmpty(raw.error)) LastError = raw.error;
                return;
            }
            if (e.ev == "zones") { Refresh(); return; }
            if (_waiting.TryGetValue(e.id, out var cb)) { _waiting.Remove(e.id); if (!e.ok) LastError = e.error; cb?.Invoke(e); }
        }
        [Serializable] class ProgressRaw { public string zone, phase, error; public int done, total; public double bytes; public bool truncated; }

        void Call(string op, string argsJson, Action<Envelope> cb)
        {
            if (!Available) return;
            int id = ++_seq; if (cb != null) _waiting[id] = cb;
            JsRequest("{\"id\":" + id + ",\"op\":\"" + op + "\",\"args\":" + argsJson + "}");
        }

        static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");
        static string N(double d) => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Reload the zone list (and storage totals) from the store.</summary>
        public void Refresh()
        {
            Call("list", "{}", e =>
            {
                if (!e.ok) return;
                var l = JsonUtility.FromJson<ZoneList>("{\"items\":" + e.v + "}");
                Zones.Clear(); if (l != null && l.items != null) Zones.AddRange(l.items);
                ZonesChanged?.Invoke();
            });
            Call("stats", "{}", e =>
            {
                if (!e.ok) return; var s = JsonUtility.FromJson<StatsDto>(e.v);
                StoredBytes = s.bytes; StoredTiles = s.tiles; QuotaBytes = s.quota; AllowRestricted = s.allowRestricted; if (s.counters != null) Stats = s.counters;
            });
        }

        /// <summary>Save a zone and start storing its tiles (only the ones not stored yet are downloaded).</summary>
        public void Create(string name, double lat, double lon, double radius, double groundHeight, string detail, Action<bool> done = null)
        {
            string z = "{\"name\":\"" + Esc(name) + "\",\"lat\":" + N(lat) + ",\"lon\":" + N(lon) + ",\"radius\":" + N(radius) + ",\"groundHeight\":" + N(groundHeight) + ",\"detail\":\"" + Esc(detail) + "\"}";
            Call("create", "{\"zone\":" + z + "}", e => { Refresh(); done?.Invoke(e.ok); });
        }
        public void SetRadius(string id, double radius) => Call("radius", "{\"id\":\"" + id + "\",\"radius\":" + N(radius) + "}", e => Refresh());
        public void Rename(string id, string name) => Call("rename", "{\"id\":\"" + id + "\",\"name\":\"" + Esc(name) + "\"}", e => Refresh());
        /// <summary>Download what is missing (force = refresh everything the zone references).</summary>
        public void Sync(string id, bool force) => Call("sync", "{\"id\":\"" + id + "\",\"force\":" + (force ? "true" : "false") + "}", e => Refresh());
        public void Cancel(string id) => Call("cancel", "{\"id\":\"" + id + "\"}", null);
        /// <summary>Delete a zone. Tiles another zone also references stay in the store.</summary>
        public void Remove(string id) => Call("remove", "{\"id\":\"" + id + "\"}", e => Refresh());
        public void SetAllowRestricted(bool on) => Call("config", "{\"allowRestricted\":" + (on ? "true" : "false") + "}", e => { AllowRestricted = on; Refresh(); });
        public void ClearAll() => Call("clear", "{}", e => Refresh());
        public GeoZone Find(string id) { foreach (var z in Zones) if (z.id == id) return z; return null; }
    }
}
