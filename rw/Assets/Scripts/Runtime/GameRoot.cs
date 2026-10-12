using CesiumForUnity;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace RocketWorld
{
    /// <summary>
    /// Builds the whole viewer from code at startup (no scene authoring needed):
    /// CesiumGeoreference -> Cesium3DTileset (Google Photorealistic 3D Tiles) + showcase car + orbit camera + sky + HUD.
    /// </summary>
    public class GameRoot : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (FindFirstObjectByType<GameRoot>() != null) return;
            new GameObject("RocketWorld").AddComponent<GameRoot>();
        }

        public GameSettings settings;
        public CesiumGeoreference georeference;
        public Cesium3DTileset tileset;
        public Transform car, carVisual;
        public Camera cam;
        public OrbitCamera orbit;
        public DebugHud hud;

        Light _sun;
        Transform _ground;
        double _lon, _lat; float _gh = float.NaN;
        int _color = -1;
        Material _skyMat;

        static readonly Color DayFog = new Color(0.812f, 0.902f, 0.949f);   // 0xcfe6f2, RocketWorld's sky/fog
        static readonly Color DuskFog = new Color(0.95f, 0.60f, 0.42f);
        static readonly Color NightFog = new Color(0.03f, 0.04f, 0.09f);

        void Awake()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            WebGLInput.captureAllKeyboardInput = true;
#endif
            Application.runInBackground = true;
            settings = GameSettings.Load();

            var g = new GameObject("CesiumGeoreference");
            georeference = g.AddComponent<CesiumGeoreference>();
            UpdateOrigin(true);
            CesiumCreditSystem.GetDefaultCreditSystem();

            BuildSun();
            BuildCar();
            BuildCamera();
            CreateWorldContent();
            ApplyLive();
            hud = gameObject.AddComponent<DebugHud>();
            hud.root = this;
            orbit.pointerBlocked = hud.Over;
        }

        void Start() { WebBridge.HideLoading(); }

        // ---- world -----------------------------------------------------------------------------------------

        /// <summary>(Re)creates the tileset (or the flat fallback ground) from the current credentials.</summary>
        public void CreateWorldContent()
        {
            if (tileset != null) Destroy(tileset.gameObject);
            tileset = null;
            bool online = settings.HasCredentials;
            if (online)
            {
                var go = new GameObject(settings.provider == TileProvider.GoogleDirect ? "Google Photorealistic 3D Tiles" : "Cesium ion Tileset");
                go.transform.SetParent(georeference.transform, false);
                go.SetActive(false);                                   // configure before OnEnable creates the native tileset
                tileset = go.AddComponent<Cesium3DTileset>();
                if (settings.provider == TileProvider.GoogleDirect)
                {
                    tileset.tilesetSource = CesiumDataSource.FromUrl;
                    tileset.url = "https://tile.googleapis.com/v1/3dtiles/root.json?key=" + UnityEngine.Networking.UnityWebRequest.EscapeURL(settings.googleKey.Trim());
                }
                else
                {
                    tileset.tilesetSource = CesiumDataSource.FromCesiumIon;
                    tileset.ionAssetID = settings.ionAssetId;
                    tileset.ionAccessToken = settings.ionToken.Trim();
                }
                tileset.showCreditsOnScreen = true;                    // Google/Cesium attribution is mandatory
                tileset.createPhysicsMeshes = false;                   // render-only viewer: no colliders are ever built
                tileset.generateSmoothNormals = false;
                tileset.preloadAncestors = true;
                tileset.forbidHoles = false;
                tileset.enableFrustumCulling = true;
                tileset.enforceCulledScreenSpaceError = true;
                tileset.culledScreenSpaceError = 64f;
                tileset.maximumSimultaneousTileLoads = 20;
                tileset.loadingDescendantLimit = 20;
                tileset.maximumCachedBytes = 384L * 1024 * 1024;       // WebGL heap is limited: keep the tile cache modest
                tileset.maximumScreenSpaceError = settings.sse;
                go.SetActive(true);
            }
            if (_ground != null) { Destroy(_ground.gameObject); _ground = null; }
            if (!online)
            {
                var gm = Resources.Load<Material>("Materials/Ground");
                var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
                Destroy(plane.GetComponent<Collider>());
                plane.name = "FallbackGround";
                plane.transform.SetParent(georeference.transform, false);
                plane.transform.localScale = new Vector3(400f, 1f, 400f);   // 4 km square
                if (gm != null) plane.GetComponent<Renderer>().sharedMaterial = gm;
                _ground = plane.transform;
            }
        }

        void UpdateOrigin(bool force)
        {
            if (!force && _lon == settings.lon && _lat == settings.lat && Mathf.Approximately(_gh, settings.groundHeight)) return;
            _lon = settings.lon; _lat = settings.lat; _gh = settings.groundHeight;
            georeference.SetOriginLongitudeLatitudeHeight(_lon, _lat, _gh);   // Unity y = 0 is the ground the car stands on
        }

        /// <summary>Applies every live-changeable setting (cheap; called whenever a setting moves).</summary>
        public void ApplyLive()
        {
            var s = settings;
            UpdateOrigin(false);
            car.localRotation = Quaternion.Euler(0f, s.heading, 0f);
            if (_color != s.carColor) { _color = s.carColor; CarVisual.SetBodyColor(carVisual, GameSettings.CarColors[s.carColor].color); }
            if (tileset != null) tileset.maximumScreenSpaceError = s.sse;
            if (orbit != null) orbit.autoRotate = s.autoRotate;
            if (cam != null) cam.farClipPlane = s.viewDistance;
            if (UniversalRenderPipeline.asset != null) UniversalRenderPipeline.asset.renderScale = Mathf.Clamp(s.renderScale, 0.3f, 2f);
            ApplyTimeOfDay();
        }

        public void SaveSettings() { settings.Save(); }

        // ---- look ------------------------------------------------------------------------------------------

        void BuildSun()
        {
            var l = new GameObject("Sun");
            l.transform.SetParent(georeference.transform, false);
            _sun = l.AddComponent<Light>();
            _sun.type = LightType.Directional;
            _sun.shadows = LightShadows.None;                          // photorealistic tiles carry baked shadows
            RenderSettings.sun = _sun;
            _skyMat = Resources.Load<Material>("Materials/Sky");
        }

        void ApplyTimeOfDay()
        {
            var s = settings;
            float elev = Mathf.Sin((s.timeOfDay - 6f) / 12f * Mathf.PI) * 68f;       // sunrise 06:00, noon 68 deg, sunset 18:00
            _sun.transform.rotation = Quaternion.Euler(elev, 150f, 0f);
            float h = Mathf.Sin(elev * Mathf.Deg2Rad);
            float toDusk = Mathf.InverseLerp(-0.15f, 0f, h), toDay = Mathf.InverseLerp(0f, 0.35f, h);
            var fog = Color.Lerp(Color.Lerp(NightFog, DuskFog, toDusk), DayFog, toDay);
            float daylight = Mathf.Clamp01(Mathf.InverseLerp(-0.1f, 0.3f, h));
            _sun.color = Color.Lerp(new Color(1f, 0.62f, 0.4f), new Color(1f, 0.945f, 0.839f), toDay);

            if (s.bakedLighting)
            {   // Google's tiles already contain their lighting: flat ambient ~ unlit, like RocketWorld's Basic materials.
                float a = Mathf.Lerp(0.10f, 0.96f, daylight);
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(a, a, a);
                _sun.intensity = 0.12f * daylight;
            }
            else
            {
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = new Color(0.75f, 0.85f, 0.95f) * Mathf.Lerp(0.08f, 1f, daylight);
                RenderSettings.ambientEquatorColor = new Color(0.55f, 0.55f, 0.55f) * Mathf.Lerp(0.06f, 1f, daylight);
                RenderSettings.ambientGroundColor = new Color(0.40f, 0.34f, 0.28f) * Mathf.Lerp(0.05f, 1f, daylight);
                _sun.intensity = 1.1f * daylight;
            }

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = fog;
            RenderSettings.fogStartDistance = s.viewDistance * 0.10f;
            RenderSettings.fogEndDistance = s.viewDistance * 0.443f;   // RocketWorld: fog 140..620 m with far plane 1400 m
            bool proc = s.proceduralSky && _skyMat != null;
            RenderSettings.skybox = proc ? _skyMat : null;
            if (cam != null) { cam.clearFlags = proc ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor; cam.backgroundColor = fog; }
        }

        // ---- objects ---------------------------------------------------------------------------------------

        void BuildCar()
        {
            var go = new GameObject("Car");
            go.transform.SetParent(georeference.transform, false);       // at the origin: y = 0 is the ground
            car = go.transform;
            carVisual = CarVisual.Build(car, GameSettings.CarColors[settings.carColor].color);
        }

        void BuildCamera()
        {
            var go = new GameObject("Main Camera") { tag = "MainCamera" };
            go.transform.SetParent(georeference.transform, false);
            cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.nearClipPlane = 0.3f;
            cam.fieldOfView = 55f;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            var data = cam.GetUniversalAdditionalCameraData();
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            data.antialiasingQuality = AntialiasingQuality.Medium;
            data.renderShadows = false;
            orbit = go.AddComponent<OrbitCamera>();
            orbit.target = car;
            orbit.yaw = settings.camYaw; orbit.pitch = settings.camPitch; orbit.distance = settings.camDist;
            orbit.autoRotate = settings.autoRotate;
        }
    }
}
