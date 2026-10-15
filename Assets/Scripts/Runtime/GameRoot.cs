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
        public RocketCar carCtl;
        public CarInput carInput;
        public TileColliderBudget budget;
        public ChaseCamera chase;
        public int cameraMode;                                // 0 chase, 1 orbit
        public Camera cam;
        public OrbitCamera orbit;
        public DebugHud hud;
        public AutoQuality autoQuality;
        public BenchmarkRunner benchmark;
        // offsets applied on top of the user's settings by AutoQuality
        public float autoSseAdd, autoScaleMul = 1f, autoFarMul = 1f;
        public float EffectiveSse => Mathf.Max(settings.sse + autoSseAdd, _turbo ? _turboCur : 0f);

        // "Turbo load": until the first view is fully loaded (or 20 s), ask for coarser tiles so the whole picture arrives fast,
        // then drop to the user's SSE and let Cesium refine in place (ancestors are already cached, so refining is cheap).
        const float TurboSse = 48f;
        bool _turbo; float _turboStart, _turboStep, _turboCur = TurboSse;
        public float EffectiveScale => Mathf.Clamp(settings.renderScale * autoScaleMul, 0.3f, 2f);
        public float EffectiveFar => settings.viewDistance * autoFarMul;
        bool _benchStarted;

        Light _sun;
        Transform _ground;
        double _lon, _lat; float _gh = float.NaN;
        int _color = -1;
        Material _skyMat;
        // last values we actually pushed to Cesium / camera — avoid thrashing tiles every slider frame
        float _appliedSse = float.NaN, _appliedFar = float.NaN, _appliedScale = float.NaN;
        float _originDebounceUntil;          // delay origin shifts while the user is dragging the ground-height slider
        double _pendingLon, _pendingLat; float _pendingGh;
        bool _originPending;
        /// <summary>True while the settings panel is open. Expensive Cesium writes (SSE) are deferred until close.</summary>
        public bool settingsOpen;

        static readonly Color DayFog = new Color(0.812f, 0.902f, 0.949f);   // 0xcfe6f2, RocketWorld's sky/fog
        static readonly Color DuskFog = new Color(0.95f, 0.60f, 0.42f);
        static readonly Color NightFog = new Color(0.03f, 0.04f, 0.09f);

        void Awake()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            WebGLInput.captureAllKeyboardInput = true;
#endif
            PhysicsSetup.Apply();
            Application.runInBackground = false;                      // hidden tab: stop streaming/rendering (battery, bandwidth)
            settings = GameSettings.Load();
            if (DeviceProfile.IsMobile) Application.targetFrameRate = 60;

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
            autoQuality = gameObject.AddComponent<AutoQuality>();
            autoQuality.root = this; autoQuality.enabledByUser = settings.autoQuality;
            benchmark = gameObject.AddComponent<BenchmarkRunner>();
            benchmark.root = this;
            orbit.pointerBlocked = hud.Over;
        }

        void Start() { WebBridge.HideLoading(); }

        void Update()
        {
            if (_turbo && tileset != null)
            {
                // Step the detail down gradually (48 -> target) instead of one cliff: a single big drop makes Cesium
                // re-request nearly every tile at once (progress falls back to 0 %). Each step waits until the view is ~loaded.
                float now = Time.unscaledTime;
                if (now - _turboStart > 2f && now - _turboStep >= 1.5f && (tileset.ComputeLoadProgress() >= 90f || now - _turboStep >= 4f))
                {
                    _turboStep = now;
                    _turboCur -= 16f;
                    if (_turboCur <= settings.sse + autoSseAdd) _turbo = false;
                    CommitSse(EffectiveSse, protectCar: false);
                }
            }
            if (carInput != null && carInput.CameraTogglePressed) SetCameraMode(1 - cameraMode);
            // ?bench=1: start once the first tiles are in (or after 25 s on a slow link / no key)
            if (settings.bench && !_benchStarted && (tileset == null || tileset.ComputeLoadProgress() >= 99f || Time.realtimeSinceStartup > 25f))
            { _benchStarted = true; benchmark.Begin(); }

            // Debounced origin apply: while the user drags ground-height / picks a preset we wait a short
            // moment so Cesium is not destroyed/reloaded every frame (which drops all tile colliders).
            if (_originPending && Time.unscaledTime >= _originDebounceUntil)
            {
                _originPending = false;
                CommitOrigin(_pendingLon, _pendingLat, _pendingGh, forceRespawn: true);
            }
        }

        // ---- world -----------------------------------------------------------------------------------------

        /// <summary>(Re)creates the tileset (or the flat fallback ground) from the current credentials.</summary>
        public void CreateWorldContent()
        {
            if (tileset != null) Destroy(tileset.gameObject);
            tileset = null; budget = null;
            _appliedSse = float.NaN;   // force SSE to be written onto the new tileset in the next ApplyLive
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
                tileset.createPhysicsMeshes = false;                   // TileColliderBudget adds colliders near the car only (baking is main-thread on Web)
                tileset.generateSmoothNormals = false;
                tileset.preloadAncestors = true;
                tileset.forbidHoles = false;
                tileset.enableFrustumCulling = true;
                tileset.enforceCulledScreenSpaceError = true;
                tileset.culledScreenSpaceError = 256f;      // tiles outside the view only get the coarsest level: bandwidth goes to what you look at
                tileset.maximumSimultaneousTileLoads = DeviceProfile.SimultaneousLoads;
                tileset.loadingDescendantLimit = 60;       // let many visible tiles refine in parallel
                // Optional tuning: set by reflection so a property that differs in this Cesium version cannot break the build.
                TrySetTileset("preloadSiblings", false);    // siblings are mostly outside the view
                TrySetTileset("enableFoveatedRendering", true);
                TrySetTileset("foveatedConeSize", 0.25f);
                TrySetTileset("foveatedTimeDelay", 0f);
                TrySetTileset("foveatedMinimumScreenSpaceErrorRelaxation", 0f);
                _turbo = true; _turboStart = _turboStep = Time.unscaledTime; _turboCur = TurboSse;
                tileset.maximumCachedBytes = DeviceProfile.TileCacheBytes;   // WebGL heap is limited: sized from the device
                tileset.maximumScreenSpaceError = EffectiveSse;
                _appliedSse = EffectiveSse;
                budget = go.AddComponent<TileColliderBudget>();        // subscribes in OnEnable, before the tileset starts loading
                budget.tileset = tileset;
                budget.focus = car;
                go.SetActive(true);
            }
            if (_ground != null) { Destroy(_ground.gameObject); _ground = null; }
            if (!online)
            {
                var gm = Resources.Load<Material>("Materials/Ground");
                var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
                plane.GetComponent<Collider>().sharedMaterial = PhysicsSetup.WorldMaterial;   // test ground the car can drive on
                plane.name = "FallbackGround";
                plane.transform.SetParent(georeference.transform, false);
                plane.transform.localScale = new Vector3(400f, 1f, 400f);   // 4 km square
                if (gm != null) plane.GetComponent<Renderer>().sharedMaterial = gm;
                _ground = plane.transform;
            }
            if (carCtl != null) { carCtl.tileset = tileset; carCtl.colliderBudget = budget; carCtl.Respawn(false); }
        }

        void TrySetTileset(string name, object value)
        {
            try
            {
                var p = typeof(Cesium3DTileset).GetProperty(name);
                if (p != null && p.CanWrite) { p.SetValue(tileset, System.Convert.ChangeType(value, p.PropertyType)); return; }
                var f = typeof(Cesium3DTileset).GetField(name);
                if (f != null) f.SetValue(tileset, System.Convert.ChangeType(value, f.FieldType));
            }
            catch (System.Exception) { }
        }

        void UpdateOrigin(bool force)
        {
            if (!force && _lon == settings.lon && _lat == settings.lat && Mathf.Approximately(_gh, settings.groundHeight)) return;

            // On first boot (force) apply immediately. While the settings panel is open, origin
            // changes are debounced so dragging the ground-height slider does not unload tiles
            // and drop the car through the map every frame.
            if (force)
            {
                CommitOrigin(settings.lon, settings.lat, settings.groundHeight, forceRespawn: false);
                return;
            }

            _pendingLon = settings.lon;
            _pendingLat = settings.lat;
            _pendingGh = settings.groundHeight;
            _originPending = true;
            _originDebounceUntil = Time.unscaledTime + 0.35f;   // wait until the user pauses dragging
        }

        void CommitOrigin(double lon, double lat, float gh, bool forceRespawn)
        {
            bool sameLatLon = _lon == lon && _lat == lat;
            bool sameAll = sameLatLon && Mathf.Approximately(_gh, gh);
            if (sameAll && !float.IsNaN(_gh)) return;

            _lon = lon; _lat = lat; _gh = gh;
            georeference.SetOriginLongitudeLatitudeHeight(_lon, _lat, _gh);   // Unity y = 0 is the (approximate) ground height

            if (carCtl == null) return;
            // Same place, only height tweak: keep XZ and re-raycast (rescue path). New city: full respawn at origin.
            carCtl.Respawn(sameLatLon);
        }

        /// <summary>
        /// Applies live-changeable settings. Safe to call every frame from the settings panel:
        /// expensive Cesium properties (SSE, origin) are only written when they actually change
        /// (and origin is debounced) so tiles/colliders are not torn down while the user drags a slider.
        /// </summary>
        public void ApplyLive()
        {
            var s = settings;
            UpdateOrigin(false);
            if (carCtl != null) carCtl.spawnHeadingDeg = s.heading;
            if (_color != s.carColor) { _color = s.carColor; CarVisual.SetBodyColor(carVisual, GameSettings.CarColors[s.carColor].color); }

            // SSE must NOT be written while the settings panel is open: dragging the slider would
            // unload/reload Cesium tiles every frame and the car falls through missing colliders.
            // AutoQuality (panel closed) and FlushSse (on panel close) still push SSE.
            if (!settingsOpen)
                CommitSse(EffectiveSse, protectCar: false);

            if (orbit != null) orbit.autoRotate = s.autoRotate;

            float far = EffectiveFar;
            if (cam != null && !Mathf.Approximately(_appliedFar, far))
            {
                _appliedFar = far;
                cam.farClipPlane = far;
            }

            if (autoQuality != null) autoQuality.enabledByUser = s.autoQuality;

            float scale = EffectiveScale;
            if (UniversalRenderPipeline.asset != null && !Mathf.Approximately(_appliedScale, scale))
            {
                _appliedScale = scale;
                UniversalRenderPipeline.asset.renderScale = scale;
            }

            ApplyTimeOfDay();
        }

        /// <summary>
        /// Push maximumScreenSpaceError once. When <paramref name="protectCar"/> is true and SSE
        /// actually changes, the car is soft-held (kinematic, same pose) until nearby tile colliders
        /// exist again — no respawn, no fall through the map.
        /// </summary>
        public void CommitSse(float sse, bool protectCar)
        {
            if (tileset == null) return;
            if (!float.IsNaN(_appliedSse) && Mathf.Abs(_appliedSse - sse) < 0.5f) return;
            _appliedSse = sse;
            tileset.maximumScreenSpaceError = sse;
            if (budget != null) budget.burst = true;
            if (protectCar && carCtl != null)
                carCtl.SoftHoldForTerrain(1.5f);
        }

        /// <summary>Force an immediate origin commit (e.g. after "Reload tiles" or a city preset that already called ApplyLive).</summary>
        public void FlushOrigin()
        {
            if (!_originPending) return;
            _originPending = false;
            CommitOrigin(_pendingLon, _pendingLat, _pendingGh, forceRespawn: true);
        }

        /// <summary>Apply the user's SSE (and auto offsets) once — call when the settings panel closes.</summary>
        public void FlushSse()
        {
            CommitSse(EffectiveSse, protectCar: true);
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
            RenderSettings.fogStartDistance = EffectiveFar * 0.10f;
            RenderSettings.fogEndDistance = EffectiveFar * 0.443f;   // RocketWorld: fog 140..620 m with far plane 1400 m
            bool proc = s.proceduralSky && _skyMat != null;
            RenderSettings.skybox = proc ? _skyMat : null;
            if (cam != null) { cam.clearFlags = proc ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor; cam.backgroundColor = fog; }
        }

        // ---- objects ---------------------------------------------------------------------------------------

        void BuildCar()
        {
            var go = new GameObject("Car");
            go.transform.SetParent(georeference.transform, false);
            car = go.transform;
            go.AddComponent<CesiumGlobeAnchor>();
            var shift = go.AddComponent<CesiumOriginShift>();
            shift.distance = 1500.0;                                     // re-centre Unity's origin on the car every 1.5 km
            carInput = go.AddComponent<CarInput>();
            carInput.TouchControlsActive = DeviceProfile.IsMobile;
            carCtl = go.AddComponent<RocketCar>();                       // builds Rigidbody + hitbox collider + vehicle model in Awake
            carCtl.georeference = georeference;
            carCtl.input = carInput;
            carCtl.spawnHeadingDeg = settings.heading;
            carVisual = CarVisual.Build(car, GameSettings.CarColors[settings.carColor].color, -0.3406f);   // Octane rest height: CoM 17 uu above the ground
            carCtl.visual = carVisual;
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
            go.AddComponent<CesiumGlobeAnchor>();                        // keeps the camera valid across origin shifts
            chase = go.AddComponent<ChaseCamera>();
            chase.car = carCtl;
            orbit = go.AddComponent<OrbitCamera>();
            orbit.target = car;
            orbit.yaw = settings.camYaw; orbit.pitch = settings.camPitch; orbit.distance = settings.camDist;
            orbit.autoRotate = settings.autoRotate;
            SetCameraMode(0);
        }

        public void SetCameraMode(int mode)
        {
            cameraMode = mode;
            if (chase != null) chase.enabled = mode == 0;
            if (orbit != null) orbit.enabled = mode == 1;
        }
    }
}
