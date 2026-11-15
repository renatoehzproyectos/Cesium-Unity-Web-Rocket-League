using System.Collections;
using System.Globalization;
using UnityEngine;
using UnityEngine.Networking;

namespace RocketWorld
{
    /// <summary>Ground height above the ellipsoid for a lat/lon (Open-Meteo, no key). Shared by the "Go to location" search and the zone tool.</summary>
    public static class ElevationLookup
    {
        [System.Serializable] class Resp { public float[] elevation; }

        /// <summary>Yields until done; <paramref name="onDone"/> gets the height, or <paramref name="fallback"/> when the lookup fails.</summary>
        public static IEnumerator Get(double lat, double lon, float fallback, System.Action<float> onDone)
        {
            string url = string.Format(CultureInfo.InvariantCulture, "https://api.open-meteo.com/v1/elevation?latitude={0:0.#####}&longitude={1:0.#####}", lat, lon);
            float h = fallback;
            using (var req = UnityWebRequest.Get(url))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    var r = JsonUtility.FromJson<Resp>(req.downloadHandler.text);
                    if (r != null && r.elevation != null && r.elevation.Length > 0) h = r.elevation[0];
                }
            }
            onDone(h);
        }
    }
}
