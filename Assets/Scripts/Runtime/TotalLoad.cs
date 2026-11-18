using System;
using System.Collections.Generic;
using System.Reflection;
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
        };

        Cesium3DTileset _ts;
        readonly Dictionary<string, object> _orig = new Dictionary<string, object>();
        float _t0, _doneSince = -1f;
        bool _finished;
        string _doneMsg = "";

        public void Begin()
        {
            if (Active || root == null || root.tileset == null) return;
            Active = true;
            Take(root.tileset);
            Toast("Tile cache cleared. Loading everything around you.");
        }

        /// <summary>Back to the normal view-driven streaming. Tiles already loaded stay in the cache until the normal budget evicts them.</summary>
        public void End()
        {
            if (!Active) return;
            Active = false; Status = "";
            if (_ts != null)
                foreach (var kv in _orig)
                    if (kv.Key != "maximumCachedBytes" || root.viewPolicy == null) Set(_ts, kv.Key, kv.Value);   // the view policy owns the cache budget when it exists
        }

        void Take(Cesium3DTileset ts)
        {
            _ts = ts; _orig.Clear(); _finished = false; _doneSince = -1f; _doneMsg = ""; _t0 = Time.unscaledTime;
            foreach (var k in Keys) { var v = Get(ts, k); if (v != null) _orig[k] = v; }

            Set(ts, "enableFrustumCulling", false);
            Set(ts, "enforceCulledScreenSpaceError", false);
            Set(ts, "enableFogCulling", false);
            Set(ts, "enableOcclusionCulling", false);
            Set(ts, "preloadSiblings", true);
            Set(ts, "maximumSimultaneousTileLoads", Mathf.Clamp(Int(_orig, "maximumSimultaneousTileLoads", 12) * 3, 24, 48));
            Set(ts, "loadingDescendantLimit", Mathf.Clamp(Int(_orig, "loadingDescendantLimit", 20) * 2, 40, 100));
            if (root.viewPolicy != null) root.viewPolicy.Boost();                  // it still shrinks the budget if the heap gets tight
            else Set(ts, "maximumCachedBytes", TileViewPolicy.MaxBudgetForDevice());

            Recreate(ts);                                                          // empties the tile cache: new native tileset, nothing kept
            root.CancelTurbo();                                                    // no coarse-first SSE ramp: detail stays what the user chose
        }

        void Update()
        {
            if (!Active) return;
            var ts = root != null ? root.tileset : null;
            if (ts == null) { Status = "Waiting for the tileset..."; return; }
            if (ts != _ts) { Take(ts); return; }                                   // new place / new tileset: apply the mode again and save again once it is loaded
            root.CancelTurbo();
            if (_finished) { Status = _doneMsg; return; }

            float p = ts.ComputeLoadProgress();
            bool settled = p >= 99.9f && Time.unscaledTime - _t0 > 6f;
            if (settled) { if (_doneSince < 0f) _doneSince = Time.unscaledTime; } else _doneSince = -1f;
            Status = "Loading all around you: " + Mathf.Min(p, 99f).ToString("0") + " %";
            if (_doneSince >= 0f && Time.unscaledTime - _doneSince >= 4f) Finish();
        }

        void Finish()
        {
            _finished = true;
            var roads = root.roads;
            if (roads == null || !roads.CanSaveZone) { _doneMsg = "All loaded. Roads are off, so no zone was saved."; Toast(_doneMsg); return; }
            if (SavedZones.All.Count >= SavedZones.MaxZones) { _doneMsg = "All loaded. Zone list is full: delete one to auto-save."; Toast(_doneMsg); return; }
            var z = roads.SaveZoneHere("Auto " + DateTime.Now.ToString("MM-dd HH:mm"));
            _doneMsg = z != null ? "All loaded. Zone saved: " + z.name : "All loaded. No position yet to save a zone.";
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
