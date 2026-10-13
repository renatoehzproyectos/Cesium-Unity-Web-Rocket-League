using System.Globalization;
using UnityEngine;

namespace RocketWorld
{
    public enum TileProvider { GoogleDirect = 0, CesiumIon = 1 }

    /// <summary>User settings, persisted with PlayerPrefs (IndexedDB on WebGL). The query string wins (and is what the share link produces).</summary>
    public class GameSettings
    {
        public TileProvider provider = TileProvider.GoogleDirect;
        public string googleKey = "", ionToken = "";
        public long ionAssetId = 2275207;            // Google Photorealistic 3D Tiles on Cesium ion
        public double lat = 36.7213, lon = -4.4214;  // Malaga, same default as RocketWorld
        public float groundHeight = 61f;             // m above the WGS84 ellipsoid: Unity y=0 sits here, the car stands on it
        public float heading = 0f;                   // car heading, degrees clockwise from north
        public float timeOfDay = 14f;                // hours, 0..24
        public float sse = 16f;                      // maximumScreenSpaceError
        public float viewDistance = 1400f;           // camera far plane (m); RocketWorld used 1400
        public float renderScale = 1f;
        public bool bakedLighting = true;            // flat ambient => tiles look like RocketWorld's baked rendering
        public bool autoRotate = true;
        public bool proceduralSky = false;           // false = flat sky + fog like RocketWorld; true = Unity procedural skybox
        public bool debugHud = true;
        public bool autoQuality = true;              // adaptive 60 FPS controller (AutoQuality)
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
                provider = (TileProvider)PlayerPrefs.GetInt(P + "provider", 0),
                googleKey = PlayerPrefs.GetString(P + "gkey", ""),
                ionToken = PlayerPrefs.GetString(P + "iontoken", ""),
                ionAssetId = (long)D(PlayerPrefs.GetString(P + "ionasset", "2275207"), 2275207),
                lat = D(PlayerPrefs.GetString(P + "lat", "36.7213"), 36.7213),
                lon = D(PlayerPrefs.GetString(P + "lon", "-4.4214"), -4.4214),
                groundHeight = PlayerPrefs.GetFloat(P + "gh", 61f),
                heading = PlayerPrefs.GetFloat(P + "hdg", 0f),
                timeOfDay = PlayerPrefs.GetFloat(P + "tod", 14f),
                sse = PlayerPrefs.GetFloat(P + "sse", 16f),
                viewDistance = PlayerPrefs.GetFloat(P + "far", 1400f),
                renderScale = PlayerPrefs.GetFloat(P + "scale", 1f),
                bakedLighting = PlayerPrefs.GetInt(P + "baked", 1) == 1,
                autoRotate = PlayerPrefs.GetInt(P + "auto", 1) == 1,
                proceduralSky = PlayerPrefs.GetInt(P + "psky", 0) == 1,
                debugHud = PlayerPrefs.GetInt(P + "hud", 1) == 1,
                autoQuality = PlayerPrefs.GetInt(P + "autoq", 1) == 1,
                carColor = PlayerPrefs.GetInt(P + "car", 0),
            };
            DeviceProfile.Load();
            if (!PlayerPrefs.HasKey(P + "init")) DeviceProfile.ApplyFirstRunDefaults(s);
            string q;
            s.bench = WebBridge.Query("bench") == "1";
            if (!string.IsNullOrEmpty(q = WebBridge.Query("key"))) s.googleKey = q;
            if (!string.IsNullOrEmpty(q = WebBridge.Query("ion"))) { s.ionToken = q; s.provider = TileProvider.CesiumIon; }
            s.lat = D(WebBridge.Query("lat"), s.lat); s.lon = D(WebBridge.Query("lon"), s.lon);
            s.groundHeight = (float)D(WebBridge.Query("h"), s.groundHeight);
            s.heading = (float)D(WebBridge.Query("hdg"), s.heading);
            s.timeOfDay = Mathf.Repeat((float)D(WebBridge.Query("t"), s.timeOfDay), 24f);
            s.sse = (float)D(WebBridge.Query("sse"), s.sse);
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
            PlayerPrefs.SetFloat(P + "sse", sse); PlayerPrefs.SetFloat(P + "far", viewDistance); PlayerPrefs.SetFloat(P + "scale", renderScale);
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
