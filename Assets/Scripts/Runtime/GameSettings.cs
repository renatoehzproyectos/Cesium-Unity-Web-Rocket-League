using System.Globalization;
using UnityEngine;

namespace RocketWorld
{
    public enum TileProvider { GoogleDirect = 0, CesiumIon = 1 }

    /// <summary>User settings, persisted with PlayerPrefs (IndexedDB on WebGL). The query string wins (and is what the share link produces).</summary>
    public class GameSettings
    {
        public TileProvider provider = TileProvider.CesiumIon;
        public string googleKey = "", ionToken = "";
        public long ionAssetId = 2275207;            // Google Photorealistic 3D Tiles on Cesium ion
        public double lat = 36.7213, lon = -4.4214;  // Malaga, same default as RocketWorld
        public float groundHeight = 61f;             // m above the WGS84 ellipsoid: Unity y=0 sits here, the car stands on it
        public float heading = 0f;                   // car heading, degrees clockwise from north
        public float timeOfDay = 14f;                // hours, 0..24
        public float sse = 32f;                      // maximumScreenSpaceError
        public float viewDistance = 20000f;          // camera far plane (m)
        public float loadRadius = 600f;              // m around the car where ALL tiles are loaded at full detail (StreamingCameras)
        public float distantQuality = 1f;            // x: sharper tiles at distance (StreamingCameras virtual viewport height)
        public float renderScale = 2f;
        public bool bakedLighting = true;            // locked on (flat ambient => tiles look like RocketWorld's baked rendering)
        public bool autoRotate = true;
        public bool proceduralSky = true;            // locked on (Unity procedural skybox)
        public bool debugHud = false;                // locked off
        public bool autoQuality = false;             // locked off (no adaptive quality controller)
        public bool noIntro;                         // ?intro=0 skips the cargo-plane opening
        public bool viewCull = true;                 // ?viewcull=0: load a full radius round the car again (tiles never disappear)
        public bool water = true;                    // ?water=0 turns the water system off (no OpenStreetMap download, water stays solid)
        public bool bench;                           // ?bench=1 runs the benchmark right after the world is ready
        public int carColor = 0;
        public float camYaw = 200f, camPitch = 14f, camDist = 7.5f;

        // name, lat, lon, approximate ground height above the ellipsoid (geoid + terrain). Adjust with the slider if the car floats/sinks.
        public static readonly (string name, double lat, double lon, float h)[] Presets =
        {
            ("Malaga", 36.7213, -4.4214, 61f), ("Barcelona", 41.3874, 2.1686, 62f), ("Valencia", 39.4699, -0.3763, 65f), ("Monaco", 43.7384, 7.4246, 78f),
            ("Dubrovnik", 42.6507, 18.0944, 64f), ("Santorini", 36.4166, 25.4313, 133f), ("New York", 40.758, -73.9855, -23f), ("Dubai", 25.1972, 55.2744, -15f),
        };

        public static readonly (string name, Color color)[] CarColors =
        {
            ("Orange", new Color(1f, 0.353f, 0.063f)), ("Blue", new Color(0.1f, 0.38f, 1f)), ("Red", new Color(0.86f, 0.1f, 0.1f)),
            ("White", new Color(0.92f, 0.93f, 0.95f)), ("Black", new Color(0.06f, 0.06f, 0.07f)), ("Green", new Color(0.1f, 0.7f, 0.25f)),
        };

        const string P = "rw2.";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static double D(string s, double def) => double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : def;

        public static GameSettings Load()
        {
            var s = new GameSettings
            {
                provider = (TileProvider)PlayerPrefs.GetInt(P + "provider", (int)TileProvider.CesiumIon),
                googleKey = PlayerPrefs.GetString(P + "gkey", ""),
                ionToken = PlayerPrefs.GetString(P + "iontoken", ""),
                ionAssetId = (long)D(PlayerPrefs.GetString(P + "ionasset", "2275207"), 2275207),
                lat = D(PlayerPrefs.GetString(P + "lat", "36.7213"), 36.7213),
                lon = D(PlayerPrefs.GetString(P + "lon", "-4.4214"), -4.4214),
                groundHeight = PlayerPrefs.GetFloat(P + "gh", 61f),
                heading = PlayerPrefs.GetFloat(P + "hdg", 0f),
                timeOfDay = PlayerPrefs.GetFloat(P + "tod", 14f),
                sse = PlayerPrefs.GetFloat(P + "sse", 32f),
                viewDistance = PlayerPrefs.GetFloat(P + "far", 20000f),
                loadRadius = PlayerPrefs.GetFloat(P + "radius", 600f),
                distantQuality = PlayerPrefs.GetFloat(P + "dq", 1f),
                renderScale = PlayerPrefs.GetFloat(P + "scale", 2f),
                bakedLighting = true,                 // locked on
                autoRotate = PlayerPrefs.GetInt(P + "auto", 1) == 1,
                proceduralSky = true,                 // locked on
                debugHud = false,                     // locked off
                autoQuality = false,                  // locked off
                carColor = PlayerPrefs.GetInt(P + "car", 0),
            };
            DeviceProfile.Load();
            if (!PlayerPrefs.HasKey(P + "init")) DeviceProfile.ApplyFirstRunDefaults(s);
            // Re-assert locked flags (DeviceProfile / old PlayerPrefs must not re-enable them).
            s.bakedLighting = true;
            s.proceduralSky = true;
            s.debugHud = false;
            s.autoQuality = false;
            if (!PlayerPrefs.HasKey(P + "radius")) s.loadRadius = DeviceProfile.IsMobile ? 300f : 600f;
            string q;
            s.bench = WebBridge.Query("bench") == "1";
            s.noIntro = WebBridge.Query("intro") == "0";
            s.water = WebBridge.Query("water") != "0";
            s.viewCull = WebBridge.Query("viewcull") != "0";
            if (!string.IsNullOrEmpty(q = WebBridge.Query("key"))) { s.googleKey = q; s.provider = TileProvider.GoogleDirect; }
            if (!string.IsNullOrEmpty(q = WebBridge.Query("ion"))) { s.ionToken = q; s.provider = TileProvider.CesiumIon; }
            // Baked-in token from CI (ION_TOKEN secret → index.html). Query string still wins above.
            if (string.IsNullOrWhiteSpace(s.ionToken))
            {
                var baked = WebBridge.DefaultIonToken();
                if (!string.IsNullOrWhiteSpace(baked)) { s.ionToken = baked; s.provider = TileProvider.CesiumIon; }
            }
            s.lat = D(WebBridge.Query("lat"), s.lat); s.lon = D(WebBridge.Query("lon"), s.lon);
            s.groundHeight = (float)D(WebBridge.Query("h"), s.groundHeight);
            s.heading = (float)D(WebBridge.Query("hdg"), s.heading);
            s.timeOfDay = Mathf.Repeat((float)D(WebBridge.Query("t"), s.timeOfDay), 24f);
            s.sse = (float)D(WebBridge.Query("sse"), s.sse);
            s.loadRadius = Mathf.Clamp((float)D(WebBridge.Query("radius"), s.loadRadius), 100f, 20000f);
            s.distantQuality = Mathf.Clamp((float)D(WebBridge.Query("dq"), s.distantQuality), 0.5f, 8f);
            s.viewDistance = Mathf.Clamp((float)D(WebBridge.Query("far"), s.viewDistance), 200f, 20000f);
            s.renderScale = Mathf.Clamp((float)D(WebBridge.Query("scale"), s.renderScale), 0.3f, 2f);
            s.carColor = Mathf.Clamp((int)D(WebBridge.Query("car"), s.carColor), 0, CarColors.Length - 1);
            s.camYaw = (float)D(WebBridge.Query("yaw"), s.camYaw);
            s.camPitch = (float)D(WebBridge.Query("pitch"), s.camPitch);
            s.camDist = (float)D(WebBridge.Query("dist"), s.camDist);
            return s;
        }

        public void Save()
        {
            PlayerPrefs.SetInt(P + "provider", (int)provider); PlayerPrefs.SetString(P + "gkey", googleKey); PlayerPrefs.SetString(P + "iontoken", ionToken);
            PlayerPrefs.SetString(P + "ionasset", ionAssetId.ToString(Inv)); PlayerPrefs.SetString(P + "lat", lat.ToString(Inv)); PlayerPrefs.SetString(P + "lon", lon.ToString(Inv));
            PlayerPrefs.SetFloat(P + "gh", groundHeight); PlayerPrefs.SetFloat(P + "hdg", heading); PlayerPrefs.SetFloat(P + "tod", timeOfDay);
            PlayerPrefs.SetFloat(P + "sse", sse); PlayerPrefs.SetFloat(P + "far", viewDistance); PlayerPrefs.SetFloat(P + "scale", renderScale); PlayerPrefs.SetFloat(P + "radius", loadRadius); PlayerPrefs.SetFloat(P + "dq", distantQuality);
            PlayerPrefs.SetInt(P + "baked", bakedLighting ? 1 : 0); PlayerPrefs.SetInt(P + "auto", autoRotate ? 1 : 0); PlayerPrefs.SetInt(P + "psky", proceduralSky ? 1 : 0); PlayerPrefs.SetInt(P + "hud", debugHud ? 1 : 0);
            PlayerPrefs.SetInt(P + "car", carColor); PlayerPrefs.SetInt(P + "autoq", autoQuality ? 1 : 0); PlayerPrefs.SetInt(P + "init", 1);
            PlayerPrefs.Save();
        }

        public bool HasCredentials => provider == TileProvider.GoogleDirect ? !string.IsNullOrWhiteSpace(googleKey) : !string.IsNullOrWhiteSpace(ionToken);

        /// <summary>Link that reproduces this view. Never includes the API key.</summary>
        public string ShareQuery(float yaw, float pitch, float dist) =>
            string.Format(Inv, "?lat={0:0.00000}&lon={1:0.00000}&h={2:0.#}&hdg={3:0}&t={4:0.#}&car={5}&yaw={6:0}&pitch={7:0}&dist={8:0.#}",
                lat, lon, groundHeight, heading, timeOfDay, carColor, yaw, pitch, dist);
    }
}
