using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using CesiumForUnity;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// "Total load" switch. While it is on:
    ///  - Cesium stops culling tiles by view (frustum / fog / occlusion culling off, culled-SSE cap off), so the sides and the back load at the
    ///    same detail as the front. Unity still only DRAWS what the camera sees. (The old "Keep tiles loaded" relied on extra cameras that Cesium
    ///    seems to ignore; this does not depend on them.)
    ///  - Loading is pushed as hard as the device allows (more parallel downloads, deeper refinement, biggest safe cache) WITHOUT touching the
    ///    screen-space error: the tile detail stays exactly what the user chose.
    ///  - The number of parallel downloads is ADAPTIVE (like TCP congestion control): it starts moderate, climbs while the server answers well and
    ///    is halved the moment a rate-limit / server error shows up in the log (HTTP 429, 502, 503, 504, "too many requests", repeated request failures).
    ///    It then remembers a ceiling just below the rate that failed, waits a growing cool-down, and probes upwards again slowly. Cesium retries the
    ///    tiles that failed, so a throttled burst costs seconds, not holes. Fastest rate the server tolerates, without the errors.
    ///  - Turning it on first throws away the whole in-game tile cache (irreversible). Saved zones and their road data are NOT touched.
    ///  - When every selected tile is loaded (nothing left to load for a few seconds) it presses "Save zone here" once, automatically.
    /// Everything is set by reflection so a Cesium for Unity version without one of these properties cannot break the build.
    /// </summary>
    public class TotalLoad : MonoBehaviour
    {
        public GameRoot root;
        public bool Active { get; private set; }
        public string Status { get; private set; } = "";

        static readonly string[] Keys =
        {
            "enableFrustumCulling", "enforceCulledScreenSpaceError", "enableFogCulling", "enableOcclusionCulling",
            "maximumSimultaneousTileLoads", "loadingDescendantLimit", "preloadSiblings", "maximumCachedBytes",
            "culledScreenSpaceError", "enableFoveatedRendering",
        };

        Cesium3DTileset _ts;
        readonly Dictionary<string, object> _orig = new Dictionary<string, object>();
        float _t0, _doneSince = -1f, _nextEnsure;
        int _desc; string _diag = "";
        bool _finished;
        string _doneMsg = "";

        // ---- adaptive download rate ----
        float _cur, _ceiling, _floor, _devMax, _nextTick, _noGrowUntil, _lastHit = -100f, _cleanSince;
        int _backoffs, _totalThrottles, _totalSoft;
        int _hardHits, _softHits;                       // written from any thread by the log callback, read/reset on the main thread
        bool _listening;

        public void Begin()
        {
            if (Active || root == null || root.tileset == null) return;
            Active = true;
            Listen(true);
            Take(root.tileset);
            Toast("Caché del mapa borrada. Cargando todo a tu alrededor.");
        }

        /// <summary>Back to the normal view-driven streaming. Tiles already loaded stay in the cache until the normal budget evicts them.</summary>
        public void End()
        {
            if (!Active) return;
            Active = false; Status = "";
            Listen(false);
            if (_ts != null)
                foreach (var kv in _orig)
                    if (kv.Key != "maximumCachedBytes" || root.viewPolicy == null) Set(_ts, kv.Key, kv.Value);   // the view policy owns the cache budget when it exists
        }

        void Take(Cesium3DTileset ts)
        {
            _ts = ts; _orig.Clear(); _finished = false; _doneSince = -1f; _doneMsg = ""; _t0 = Time.unscaledTime;
            foreach (var k in Keys) { var v = Get(ts, k); if (v != null) _orig[k] = v; }

            // Adaptive download rate: start moderate (the burst right after the cache is wiped is what trips rate limits), climb while healthy.
            _devMax = DeviceProfile.Safe ? 6f : DeviceProfile.IsMobile ? 20f : 48f;
            _floor = DeviceProfile.Safe ? 3f : 4f;
            _cur = Mathf.Min(_devMax, DeviceProfile.Safe ? 4f : DeviceProfile.IsMobile ? 8f : 16f);
            _ceiling = _devMax; _backoffs = 0; _totalThrottles = 0; _totalSoft = 0; _lastHit = -100f; _noGrowUntil = Time.unscaledTime + 1.5f; _nextTick = 0f; _cleanSince = Time.unscaledTime;
            Interlocked.Exchange(ref _hardHits, 0); Interlocked.Exchange(ref _softHits, 0);
            int origDesc = Int(_orig, "loadingDescendantLimit", 20);
            _desc = Mathf.Clamp(origDesc * 2, 40, DeviceProfile.IsMobile ? 64 : 100);      // phones hold every in-flight tile's decoded images: smaller
            Ensure(ts);
            if (root.viewPolicy != null) root.viewPolicy.Boost();                  // it still shrinks the budget if the heap gets tight
            else Set(ts, "maximumCachedBytes", TileViewPolicy.MaxBudgetForDevice());

            Recreate(ts);                                                          // empties the tile cache: new native tileset, nothing kept
            Ensure(ts);                                                            // a new native tileset may have re-read the fields: assert again
            Debug.Log("[RocketWorld] total load: " + Diag(ts));
            root.CancelTurbo();                                                    // no coarse-first SSE ramp: detail stays what the user chose
        }

        /// <summary>
        /// Every detail the user chose must hold in ALL directions. Belt and braces: (1) no frustum / fog / occlusion culling, (2) no foveation
        /// (it relaxes the detail away from the view centre), (3) if Cesium still treats a tile as culled, it must refine it to the SAME screen-space
        /// error as the visible ones (culledScreenSpaceError = the real SSE). Only writes when a value differs, so it never re-triggers anything.
        /// </summary>
        void Ensure(Cesium3DTileset ts)
        {
            Want(ts, "enableFrustumCulling", false);
            Want(ts, "enableFogCulling", false);
            Want(ts, "enableOcclusionCulling", false);
            Want(ts, "enableFoveatedRendering", false);
            Want(ts, "enforceCulledScreenSpaceError", true);
            Want(ts, "culledScreenSpaceError", root.EffectiveSse);
            Want(ts, "preloadSiblings", true);
            Want(ts, "maximumSimultaneousTileLoads", Mathf.RoundToInt(_cur));
            Want(ts, "loadingDescendantLimit", _desc);
        }

        static void Want(object o, string name, object value)
        {
            var cur = Get(o, name); if (cur == null) return;                        // property does not exist in this Cesium version
            try { if (cur.Equals(Convert.ChangeType(value, cur.GetType()))) return; } catch (Exception) { }
            Set(o, name, value);
        }

        string Diag(Cesium3DTileset ts)
        {
            return "adaptiveLoads=" + Mathf.RoundToInt(_cur) + "/" + Mathf.RoundToInt(_ceiling) + " throttles=" + _totalThrottles + " frustumCulling=" + Get(ts, "enableFrustumCulling") + " foveated=" + Get(ts, "enableFoveatedRendering") + " enforceCulled=" + Get(ts, "enforceCulledScreenSpaceError")
                 + " culledSSE=" + Get(ts, "culledScreenSpaceError") + " SSE=" + Get(ts, "maximumScreenSpaceError") + " loads=" + Get(ts, "maximumSimultaneousTileLoads");
        }

        void Update()
        {
            if (!Active) return;
            var ts = root != null ? root.tileset : null;
            if (ts == null) { Status = "Esperando el mapa..."; return; }
            if (ts != _ts) { Take(ts); return; }                                   // new place / new tileset: apply the mode again and save again once it is loaded
            root.CancelTurbo();
            if (Time.unscaledTime >= _nextTick) { _nextTick = Time.unscaledTime + 0.5f; Pace(ts); }
            if (Time.unscaledTime >= _nextEnsure) { _nextEnsure = Time.unscaledTime + 1f; Ensure(ts); _diag = Diag(ts); }
            if (_finished) { Status = _doneMsg; return; }

            float p = ts.ComputeLoadProgress();
            bool calm = Time.unscaledTime - _lastHit > 8f;                          // no rate-limit errors lately: only then is "100 %" trustworthy
            bool settled = p >= 99.9f && Time.unscaledTime - _t0 > 6f && calm;
            if (settled) { if (_doneSince < 0f) _doneSince = Time.unscaledTime; } else _doneSince = -1f;
            Status = "Cargando todo a tu alrededor: " + Mathf.Min(p, 99f).ToString("0") + " %  ·  descargas en paralelo: " + Mathf.RoundToInt(_cur)
                   + (Time.unscaledTime < _noGrowUntil && _lastHit > 0f && Time.unscaledTime - _lastHit < 30f ? "  ·  el servidor pide calma, ajustando el ritmo" : "");
            if (_doneSince >= 0f && Time.unscaledTime - _doneSince >= 4f) Finish();
        }


        // ---- adaptive pacing ----

        /// <summary>Called twice a second. Multiplicative decrease on a rate-limit signal, slow additive/multiplicative increase while it is quiet.</summary>
        void Pace(Cesium3DTileset ts)
        {
            float now = Time.unscaledTime;
            int hard = Interlocked.Exchange(ref _hardHits, 0), soft = Interlocked.Exchange(ref _softHits, 0);
            _totalThrottles += hard; _totalSoft += soft;
            bool hit = hard > 0 || soft >= 3;                                        // a lone failed tile is noise; a handful in half a second is overload
            if (hit)
            {
                _lastHit = now; _cleanSince = now;
                float failedAt = _cur;
                _ceiling = Mathf.Max(_floor, Mathf.Floor(failedAt * 0.75f));         // settle just under the rate that failed
                _cur = Mathf.Max(_floor, Mathf.Floor(failedAt * 0.5f));
                _noGrowUntil = now + Mathf.Min(30f, 3f * Mathf.Pow(2f, Mathf.Min(_backoffs, 4)));   // 3 s, 6 s, 12 s, 24 s, 30 s
                _backoffs++;
                Want(ts, "maximumSimultaneousTileLoads", Mathf.RoundToInt(_cur));
                Debug.Log("[RocketWorld] total load: server pushed back (hard " + hard + ", soft " + soft + "): loads " + Mathf.RoundToInt(failedAt) + " -> " + Mathf.RoundToInt(_cur) + ", ceiling " + Mathf.RoundToInt(_ceiling) + ", pause " + (_noGrowUntil - now).ToString("0") + " s");
                return;
            }
            if (now < _noGrowUntil) return;
            if (now - _cleanSince > 12f) { _backoffs = Mathf.Max(0, _backoffs - 1); _cleanSince = now; _ceiling = Mathf.Min(_devMax, _ceiling + 2f); }   // quiet for a while: probe a higher ceiling again
            if (_cur < _ceiling)
            {
                _cur = Mathf.Min(_ceiling, _cur + Mathf.Max(2f, Mathf.Floor(_cur * 0.25f)));
                _noGrowUntil = now + 1.5f;                                           // one step per 1.5 s: gives errors time to show up before the next step
                Want(ts, "maximumSimultaneousTileLoads", Mathf.RoundToInt(_cur));
            }
        }

        void Listen(bool on)
        {
            if (on == _listening) return;
            _listening = on;
            if (on) Application.logMessageReceivedThreaded += OnLog; else Application.logMessageReceivedThreaded -= OnLog;
        }

        void OnDestroy() { Listen(false); }

        /// <summary>Runs on whatever thread logged (Cesium's native code logs from worker threads): no Unity API here, only counters.</summary>
        void OnLog(string msg, string stack, LogType type)
        {
            if (type == LogType.Log || string.IsNullOrEmpty(msg)) return;
            if (IsHardThrottle(msg)) Interlocked.Increment(ref _hardHits);
            else if (IsSoftFailure(msg)) Interlocked.Increment(ref _softHits);
        }

        static bool Has(string m, string needle) { return m.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0; }

        /// <summary>Explicit "slow down" answers from the server or its front end.</summary>
        static bool IsHardThrottle(string m)
        {
            if (Has(m, "too many requests") || Has(m, "rate limit") || Has(m, "RESOURCE_EXHAUSTED") || Has(m, "slow down")) return true;
            bool http = Has(m, "status") || Has(m, "http") || Has(m, "response") || Has(m, "request");
            return http && (m.Contains("429") || m.Contains("503") || m.Contains("502") || m.Contains("504"));
        }

        /// <summary>
        /// A browser cannot read a 429 that carries no CORS headers: it only sees a failed request (status 0). So repeated tile/request failures
        /// count too, but only when several arrive together (see Pace).
        /// </summary>
        static bool IsSoftFailure(string m)
        {
            bool tileOrRequest = Has(m, "tile") || Has(m, "request") || Has(m, "download");
            return tileOrRequest && (Has(m, "failed") || Has(m, "timed out") || Has(m, "timeout") || Has(m, "network error") || Has(m, "status code 0"));
        }

        void Finish()
        {
            _finished = true;
            Debug.Log("[RocketWorld] total load finished in " + (Time.unscaledTime - _t0).ToString("0") + " s; rate-limit signals " + _totalThrottles + ", other request failures " + _totalSoft + ", final parallel loads " + Mathf.RoundToInt(_cur));
            var roads = root.roads;
            if (roads == null || !roads.CanSaveZone) { _doneMsg = "Todo cargado. Las carreteras están desactivadas, así que no se guardó ninguna zona."; Toast(_doneMsg); return; }
            if (SavedZones.All.Count >= SavedZones.MaxZones) { _doneMsg = "Todo cargado. La lista de zonas está llena: borra una para guardar automáticamente."; Toast(_doneMsg); return; }
            var z = roads.SaveZoneHere("Auto " + DateTime.Now.ToString("MM-dd HH:mm"));
            _doneMsg = z != null ? "Todo cargado. Zona guardada: " + z.name : "Todo cargado. Aún no hay una posición para guardar la zona.";
        }

        void Toast(string t) { if (root != null && root.hud != null) root.hud.ShowToast(t); }

        // ---- reflection helpers ----
        static object Get(object o, string name)
        {
            try
            {
                var t = o.GetType();
                var p = t.GetProperty(name); if (p != null && p.CanRead) return p.GetValue(o);
                var f = t.GetField(name); if (f != null) return f.GetValue(o);
            }
            catch (Exception) { }
            return null;
        }

        static void Set(object o, string name, object value)
        {
            try
            {
                var t = o.GetType();
                var p = t.GetProperty(name);
                if (p != null && p.CanWrite) { p.SetValue(o, Convert.ChangeType(value, p.PropertyType)); return; }
                var f = t.GetField(name);
                if (f != null) f.SetValue(o, Convert.ChangeType(value, f.FieldType));
            }
            catch (Exception) { }
        }

        static int Int(Dictionary<string, object> d, string k, int fallback)
        {
            try { if (d.TryGetValue(k, out var v) && v != null) return Convert.ToInt32(v); } catch (Exception) { }
            return fallback;
        }

        static void Recreate(Cesium3DTileset ts)
        {
            try
            {
                var m = typeof(Cesium3DTileset).GetMethod("RecreateTileset", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (m != null) m.Invoke(ts, null);
                else { var go = ts.gameObject; go.SetActive(false); go.SetActive(true); }   // OnDisable/OnEnable rebuild the native tileset: same effect
            }
            catch (Exception e) { Debug.LogWarning("[RocketWorld] total load: could not clear the tile cache: " + e.Message); }
            GC.Collect();
            Resources.UnloadUnusedAssets();
        }
    }
}
