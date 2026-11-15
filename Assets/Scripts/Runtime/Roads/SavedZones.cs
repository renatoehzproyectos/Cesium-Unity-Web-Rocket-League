using System;
using System.Collections.Generic;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Saved zones: named circles (lat/lon/radius) the player keeps. For roads they are pinned: their tiles are downloaded ahead (offline use) and are
    /// never evicted from the persistent cache. Stored in PlayerPrefs (JSON), shared by the road system and any UI.
    /// </summary>
    public static class SavedZones
    {
        [Serializable] public class Zone { public string name; public double lat, lon; public float radiusM = 1500f; }
        [Serializable] class Store { public List<Zone> zones = new List<Zone>(); }
        const string Key = "rw2.zones";
        public const int MaxZones = 24;
        static Store _s;
        public static event Action Changed;
        public static IReadOnlyList<Zone> All { get { Load(); return _s.zones; } }

        static void Load()
        {
            if (_s != null) return;
            try { _s = JsonUtility.FromJson<Store>(PlayerPrefs.GetString(Key, "")); } catch (Exception) { }
            if (_s == null || _s.zones == null) _s = new Store();
        }

        public static Zone Add(string name, double lat, double lon, float radiusM = 1500f)
        {
            Load(); if (_s.zones.Count >= MaxZones) _s.zones.RemoveAt(0);
            var z = new Zone { name = string.IsNullOrWhiteSpace(name) ? $"Zone {_s.zones.Count + 1}" : name.Trim(), lat = lat, lon = lon, radiusM = Mathf.Clamp(radiusM, 300f, 4000f) };
            _s.zones.Add(z); Save(); return z;
        }
        public static void Remove(Zone z) { Load(); if (_s.zones.Remove(z)) Save(); }
        static void Save() { PlayerPrefs.SetString(Key, JsonUtility.ToJson(_s)); PlayerPrefs.Save(); Changed?.Invoke(); }
    }
}
