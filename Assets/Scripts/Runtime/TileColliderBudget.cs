using System.Collections.Generic;
using CesiumForUnity;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Cesium's createPhysicsMeshes bakes a MeshCollider for EVERY loaded tile, and on Web that baking runs on the main thread.
    /// This turns that off and adds MeshColliders only to tiles near the car (a couple per cycle, nearest first), removing them when the
    /// car moves away. Colliders get the RocketSim car-world material (friction 0.3, restitution 0.3).
    /// </summary>
    public class TileColliderBudget : MonoBehaviour
    {
        public Cesium3DTileset tileset;
        public Transform focus;
        public List<Transform> extraFoci;                     // multiplayer host: remote players' cars
        public Transform focus2;                              // optional second centre (the ball): colliders are also kept around it
        public float addRadius = 260f, removeRadius = 420f;   // m (hysteresis)
        public int maxAddsPerCycle = 2, burstAddsWhileWaiting = 24;
        public float cycleSeconds = 0.15f;
        public bool burst;                                    // RocketCar sets this while waiting for the first ground
        public int Registered => _tiles.Count;
        public int WithCollider { get; private set; }

        sealed class Entry { public GameObject go; public Renderer[] renderers; public MeshFilter[] filters; public MeshCollider[] colliders; }
        readonly List<Entry> _tiles = new List<Entry>(2048);
        readonly List<(float d, Entry e)> _candidates = new List<(float, Entry)>(256);
        float _next;
        readonly List<Vector3> _pts = new List<Vector3>(8);
        float D2(Bounds b) { float m = float.MaxValue; for (int i = 0; i < _pts.Count; i++) m = Mathf.Min(m, b.SqrDistance(_pts[i])); return m; }

        void OnEnable() { if (tileset != null) tileset.OnTileGameObjectCreated += OnTile; }
        void OnDisable() { if (tileset != null) tileset.OnTileGameObjectCreated -= OnTile; }

        void OnTile(GameObject go)
        {
            if (go == null) return;
            var filters = go.GetComponentsInChildren<MeshFilter>(true);
            if (filters.Length == 0) return;
            var rs = new Renderer[filters.Length];
            for (int i = 0; i < filters.Length; i++) rs[i] = filters[i].GetComponent<Renderer>();
            _tiles.Add(new Entry { go = go, filters = filters, renderers = rs, colliders = new MeshCollider[filters.Length] });
        }

        void Update()
        {
            if (focus == null || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + (burst ? 0.02f : cycleSeconds);
            _pts.Clear(); _pts.Add(focus.position);
            if (focus2 != null && focus2.gameObject.activeInHierarchy) _pts.Add(focus2.position);
            if (extraFoci != null) foreach (var x in extraFoci) if (x != null) _pts.Add(x.position);
            float on2 = addRadius * addRadius, off2 = removeRadius * removeRadius;
            _candidates.Clear(); int with = 0;
            for (int t = _tiles.Count - 1; t >= 0; t--)
            {
                var e = _tiles[t];
                if (e.go == null) { _tiles.RemoveAt(t); continue; }
                if (!e.go.activeInHierarchy) { with += Count(e); continue; }      // hidden LOD: its colliders are inactive anyway
                for (int i = 0; i < e.filters.Length; i++)
                {
                    var f = e.filters[i]; var r = e.renderers[i];
                    if (f == null || r == null) continue;
                    float d2 = D2(r.bounds);
                    if (e.colliders[i] != null) { if (d2 > off2) { Destroy(e.colliders[i]); e.colliders[i] = null; } else with++; }
                    else if (d2 < on2 && f.sharedMesh != null) _candidates.Add((d2, e));
                }
            }
            WithCollider = with;
            if (_candidates.Count == 0) return;
            _candidates.Sort((a, b) => a.d.CompareTo(b.d));
            int budget = burst ? burstAddsWhileWaiting : maxAddsPerCycle, added = 0;
            for (int c = 0; c < _candidates.Count && added < budget; c++)
            {
                var e = _candidates[c].e;
                for (int i = 0; i < e.filters.Length && added < budget; i++)
                {
                    if (e.colliders[i] != null || e.filters[i] == null || e.filters[i].sharedMesh == null) continue;
                    if (e.renderers[i] == null || D2(e.renderers[i].bounds) >= on2) continue;
                    var mc = e.filters[i].gameObject.AddComponent<MeshCollider>();       // shares the MeshFilter's mesh
                    mc.sharedMesh = e.filters[i].sharedMesh;
                    mc.sharedMaterial = PhysicsSetup.WorldMaterial;
                    e.colliders[i] = mc; added++;
                }
            }
            if (added > 0) Physics.SyncTransforms();
        }

        static int Count(Entry e) { int n = 0; foreach (var c in e.colliders) if (c != null) n++; return n; }
    }
}
