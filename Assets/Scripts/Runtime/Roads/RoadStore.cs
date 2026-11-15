using System;
using System.Collections.Generic;
using System.IO;
using RocketWorld.Sim.Roads;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Persistent road cache under persistentDataPath/roads (IndexedDB on WebGL, flushed with RW_FlushFS). Three kinds of file:
    /// raw (downloaded tile, per release), proc (fitted height profiles of a chunk, per release + processing version) and nodes (shared junction heights).
    /// Size is bounded by an LRU; saved-zone tiles are pinned. Every read validates its content, so a half-written file is simply a cache miss.
    /// </summary>
    public sealed class RoadStore
    {
        readonly string _dir; readonly ByteLru _lru = new ByteLru(); readonly HashSet<string> _pinned = new HashSet<string>();
        float _flushAt; bool _dirty;
        public long Bytes => _lru.Total; public int Files => _lru.Count; public bool Ok { get; private set; }

        public RoadStore()
        {
            try
            {
                _dir = Path.Combine(Application.persistentDataPath, "roads"); Directory.CreateDirectory(_dir);
                foreach (var f in new DirectoryInfo(_dir).GetFiles("*.bin")) _lru.Touch(f.Name, f.Length);
                Ok = true;
            }
            catch (Exception e) { Debug.LogWarning("[RocketWorld] road cache unavailable, running memory-only: " + e.Message); }
        }

        public static string Name(string kind, string release, int z, int x, int y) => RoadCachePolicy.FileName(kind, release, z, x, y);

        public byte[] Read(string name)
        {
            if (!Ok) return null;
            try { string p = Path.Combine(_dir, name); if (!File.Exists(p)) return null; var b = File.ReadAllBytes(p); _lru.Touch(name, b.Length, _pinned.Contains(name)); return b; }
            catch (Exception) { return null; }
        }

        public void Write(string name, byte[] data)
        {
            if (!Ok || data == null) return;
            try
            {
                string p = Path.Combine(_dir, name), tmp = p + ".tmp"; File.WriteAllBytes(tmp, data); if (File.Exists(p)) File.Delete(p); File.Move(tmp, p);
                _lru.Touch(name, data.Length, _pinned.Contains(name)); _dirty = true; _flushAt = Time.unscaledTime + 2f;
            }
            catch (Exception e) { Debug.LogWarning("[RocketWorld] road cache write failed: " + e.Message); }
        }

        /// <summary>Most recent older-release copy of a tile (used offline / when the download fails). Null if none.</summary>
        public string FindStale(string kind, int z, int x, int y, string notRelease)
        {
            if (!Ok) return null; string suffix = $"_{z}_{x}_{y}.bin", best = null; DateTime bt = DateTime.MinValue;
            try { foreach (var f in new DirectoryInfo(_dir).GetFiles(kind + "_*" + suffix)) if (!f.Name.Contains("_" + notRelease + "_") && f.LastWriteTimeUtc >= bt) { bt = f.LastWriteTimeUtc; best = f.Name; } } catch (Exception) { }
            return best;
        }

        public void Pin(string name) { _pinned.Add(name); _lru.Pin(name, true); }
        public void ClearPins() { _pinned.Clear(); }

        /// <summary>Delete oldest unpinned files over budget, and any file of a release other than <paramref name="current"/> that is not the only copy.</summary>
        public void Enforce(long maxBytes)
        {
            if (!Ok) return;
            foreach (var n in _lru.Evict(maxBytes)) { try { File.Delete(Path.Combine(_dir, n)); } catch (Exception) { } _dirty = true; }
            if (_dirty) _flushAt = Math.Min(_flushAt, Time.unscaledTime + 2f);
        }

        public void Tick() { if (_dirty && Time.unscaledTime >= _flushAt) { _dirty = false; WebBridge.FlushFS(); } }
    }
}
