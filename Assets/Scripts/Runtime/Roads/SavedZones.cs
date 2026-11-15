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
        public const float MinRadius = 300f, MaxRadius = 4000f;
        const string RadiusKey = "rw2.zoneRadius";
        static float _defR = -1f;
        /// <summary>Radius used by "save zone here" (F9 or the touch button); the settings slider edits it. Persisted.</summary>
        public static float DefaultRadius
        {
            get { if (_defR < 0f) _defR = Mathf.Clamp(PlayerPrefs.GetFloat(RadiusKey, 1500f), MinRadius, MaxRadius); return _defR; }
            set { value = Mathf.Clamp(Mathf.Round(value / 50f) * 50f, MinRadius, MaxRadius); if (Mathf.Approximately(value, _defR)) return; _defR = value; PlayerPrefs.SetFloat(RadiusKey, value); }
        }
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
            var z = new Zone { name = string.IsNullOrWhiteSpace(name) ? $"Zone {_s.zones.Count + 1}" : name.Trim(), lat = lat, lon = lon, radiusM = Mathf.Clamp(radiusM, MinRadius, MaxRadius) };
            _s.zones.Add(z); Save(); return z;
        }
        public static void Remove(Zone z) { Load(); if (_s.zones.Remove(z)) Save(); }
        static void Save() { PlayerPrefs.SetString(Key, JsonUtility.ToJson(_s)); PlayerPrefs.Save(); Changed?.Invoke(); }
    }
}
