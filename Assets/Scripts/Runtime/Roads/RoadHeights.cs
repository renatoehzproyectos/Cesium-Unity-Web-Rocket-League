using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Ground heights for the road solver from Cesium's own <c>SampleHeightMostDetailed</c> (ellipsoidal metres, taken from the tileset geometry that is actually drawn),
    /// instead of ray casts against the tile colliders. Requests are batched and answered asynchronously: until a point has been answered <see cref="Get"/> returns NaN, the
    /// chunk stays "pending" and is rebuilt later. So far terrain that is not loaded does not matter: when the car gets close the tiles load, the heights are sampled and the road appears.
    /// If sampling is not available (API missing in this build, repeated failures) <see cref="Broken"/> turns on and the caller falls back to ray casts.
    /// </summary>
    public sealed class RoadHeights
    {
        const double Grid = 2e5;                    // cells per degree: about 0.55 m north-south
        const int BatchSize = 768, MaxCells = 600000;
        struct Ask { public long key; public double lon, lat; }

        readonly Dictionary<long, float> _h = new Dictionary<long, float>();
        readonly Dictionary<long, float> _retryAt = new Dictionary<long, float>();      // asked, no surface yet: ask again later
        readonly HashSet<long> _asked = new HashSet<long>();
        readonly List<Ask> _queue = new List<Ask>();
        Task<CesiumSampleHeightResult> _task; Ask[] _taskAsks; float _taskStart; int _failures;

        public bool Broken { get; private set; }
        public int Cells => _h.Count; public int Queued => _queue.Count; public bool Busy => _task != null; public int Sampled { get; private set; }

        static long Key(double lon, double lat) => ((long)Math.Round(lon * Grid) << 32) ^ (uint)(int)Math.Round(lat * Grid);

        /// <summary>Ellipsoidal height at lon/lat, or NaN while it is not known. With <paramref name="request"/> the point is queued for sampling.</summary>
        public float Get(double lon, double lat, bool request)
        {
            long k = Key(lon, lat);
            if (_h.TryGetValue(k, out float h)) return h;
            if (request && (!_asked.Contains(k) || (_retryAt.TryGetValue(k, out float t) && Time.unscaledTime >= t)))
            {
                _asked.Add(k); _retryAt.Remove(k);
                _queue.Add(new Ask { key = k, lon = Math.Round(lon * Grid) / Grid, lat = Math.Round(lat * Grid) / Grid });
            }
            return float.NaN;
        }

        public void Clear() { _h.Clear(); _asked.Clear(); _retryAt.Clear(); _queue.Clear(); _task = null; }

        /// <summary>Call every frame: collects the finished batch and starts the next one.</summary>
        public void Update(Cesium3DTileset ts)
        {
            if (Broken || ts == null) return;
            if (_task != null)
            {
                if (_task.IsCompleted)
                {
                    try
                    {
                        if (_task.IsFaulted || _task.IsCanceled) throw new InvalidOperationException("sample task failed");
                        var r = _task.Result; int n = Math.Min(_taskAsks.Length, r.longitudeLatitudeHeightPositions.Length);
                        for (int i = 0; i < n; i++)
                        {
                            bool ok = r.sampleSuccess == null || (i < r.sampleSuccess.Length && r.sampleSuccess[i]);
                            double hh = r.longitudeLatitudeHeightPositions[i].z;
                            if (ok && !double.IsNaN(hh)) { _h[_taskAsks[i].key] = (float)hh; Sampled++; }
                            else _retryAt[_taskAsks[i].key] = Time.unscaledTime + 8f;            // nothing under that point yet (tile not loaded): ask again later
                        }
                        _failures = 0;
                    }
                    catch (Exception e) { if (++_failures >= 3) { Broken = true; Debug.LogWarning("[RocketWorld] roads: height sampling unavailable, using ray casts (" + e.Message + ")"); } RequeueFailed(); }
                    _task = null;
                }
                else if (Time.unscaledTime - _taskStart > 30f)            // a batch that never answers (tiles failing to load): give up on it and try again
                {
                    if (++_failures >= 3) { Broken = true; Debug.LogWarning("[RocketWorld] roads: height sampling does not answer, using ray casts"); }
                    RequeueFailed(); _task = null;
                }
                return;
            }
            if (_queue.Count == 0) return;
            if (_h.Count > MaxCells) { _h.Clear(); _asked.Clear(); _retryAt.Clear(); }
            int take = Math.Min(BatchSize, _queue.Count); int from = _queue.Count - take;         // newest requests first: they belong to where the car is now
            _taskAsks = _queue.GetRange(from, take).ToArray(); _queue.RemoveRange(from, take);
            var pos = new double3[take]; for (int i = 0; i < take; i++) pos[i] = new double3(_taskAsks[i].lon, _taskAsks[i].lat, 0.0);
            try { _task = ts.SampleHeightMostDetailed(pos); _taskStart = Time.unscaledTime; }
            catch (Exception e) { Broken = true; _task = null; Debug.LogWarning("[RocketWorld] roads: height sampling unavailable, using ray casts (" + e.Message + ")"); }
        }

        void RequeueFailed() { if (_taskAsks != null) foreach (var a in _taskAsks) { _asked.Remove(a.key); } }
    }
}
