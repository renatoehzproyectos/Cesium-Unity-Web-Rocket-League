using System.Collections.Generic;
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
        public Minimap minimap;
        public RocketBall ball;
        /// <summary>OpenStreetMap water polygons (free, no key). Null when ?water=0.</summary>
        public WaterMap waterMap;
        public AutoQuality autoQuality;
        public BenchmarkRunner benchmark;
        public TileLog tileLog;

        // ---- multiplayer hooks (the networking layer lives in Assets/Net) ----
        /// <summary>Set before boot by the multiplayer layer: show the start menu and hold the car until a mode is chosen.</summary>
        public static bool RequireMenu;
        /// <summary>True while a menu (start / online) is open: the car gets no input and the camera ignores drags.</summary>
        public static bool MenuBlocksInput;
        public INetRole net;
        /// <summary>Remote players' cars (host): tile colliders are kept around them too.</summary>
        public readonly List<Transform> extraFoci = new List<Transform>();
        readonly Dictionary<Transform, GameObject> _streams = new Dictionary<Transform, GameObject>();
        public bool TurboActive => _turbo;
        // offsets applied on top of the user's settings by AutoQuality
        public float autoSseAdd, autoScaleMul = 1f, autoFarMul = 1f;
        public float EffectiveSse => Mathf.Max(settings.sse + autoSseAdd, _turbo ? _turboCur : 0f);

        // "Turbo load": until the first view is fully loaded (or 20 s), ask for coarser tiles so the whole picture arrives fast,
        // then drop to the user's SSE and let Cesium refine in place (ancestors are already cached, so refining is cheap).
        const float TurboSse = 48f;
        /// <summary>SSE for tiles outside the view (coarse but present, so there are no holes). Lower = sharper behind/beside you, more bandwidth.</summary>
        public const float CulledSse = 128f;
        bool _turbo; float _turboStart, _turboStep, _turboCur = TurboSse;
        public float EffectiveScale => Mathf.Clamp(settings.renderScale * autoScaleMul, 0.3f, 2f);
        /// <summary>Radius loaded at full detail round a car. With view-driven streaming only the physics ring: the rest follows the camera.</summary>
        public float EffectiveRadius => settings.viewCull && viewPolicy != null ? Mathf.Min(settings.loadRadius, viewPolicy.Ring) : settings.loadRadius * autoFarMul * RadiusMul;
        public TileViewPolicy viewPolicy;
        // Adaptive radius: if the demanded tiles do not fit the cache, loading stalls (tiles evicted as fast as they arrive).
        // The slider is the MAXIMUM; the radius shrinks while progress stagnates and grows back once everything is loaded.
        public float RadiusMul { get; private set; } = 1f;
        float _radChkT, _radChkProg;
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
            // Sharp ground at grazing angles (driving): without anisotropic filtering the road/terrain smears into blur a few metres ahead.
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            Texture.SetGlobalAnisotropicFilteringLimits(DeviceProfile.IsMobile ? 8 : 16, DeviceProfile.IsMobile ? 8 : 16);
            Application.runInBackground = false;                      // hidden tab: stop streaming/rendering (battery, bandwidth)
            settings = GameSettings.Load();
            if (DeviceProfile.IsMobile) Application.targetFrameRate = 60;

            var g = new GameObject("CesiumGeoreference");
            georeference = g.AddComponent<CesiumGeoreference>();
            UpdateOrigin(true);
            CesiumCreditSystem.GetDefaultCreditSystem();

            BuildSun();
            BuildCar();
            BuildWater();
            AttachPlane(carCtl);
            BuildCamera();
            CreateWorldContent();
            ApplyLive();
            gameObject.AddComponent<CarSimLoop>().ball = ball;
            if (RequireMenu) { carCtl.HoldStart(); SetCameraMode(1); }   // start menu: show the city (orbit camera) until a mode is chosen
            hud = gameObject.AddComponent<DebugHud>();
            hud.root = this;
            minimap = gameObject.AddComponent<Minimap>();
            minimap.root = this;
            autoQuality = gameObject.AddComponent<AutoQuality>();
            autoQuality.root = this; autoQuality.enabledByUser = settings.autoQuality;
            benchmark = gameObject.AddComponent<BenchmarkRunner>();
            benchmark.root = this;
            tileLog = gameObject.AddComponent<TileLog>();
            tileLog.root = this;
            if (tileset != null) tileLog.Begin(tileset);   // the first tileset was created before the log existed
            orbit.pointerBlocked = hud.Over;
            orbit.padBlocked = () => carInput != null && carInput.Blocked;
        }

        void Start() { WebBridge.HideLoading(); }

        void Update()
        {
            if (carInput != null && carInput.PlanePressed) RequestPlane();
            if (tileset != null && Time.unscaledTime - _radChkT >= 6f)
            {
                float p = tileset.ComputeLoadProgress();
                if (_radChkT > 0f)
                {
                    if (p < 99f && p <= _radChkProg + 1.5f) RadiusMul = Mathf.Max(0.2f, RadiusMul * 0.75f);
                    else if (p >= 99.9f) RadiusMul = Mathf.Min(1f, RadiusMul * 1.15f);
                }
                _radChkT = Time.unscaledTime; _radChkProg = p;
            }
            if (_turbo && tileset != null)
            {
                // Step the detail down gradually (48 -> target) instead of one cliff: a single big drop makes Cesium
                // re-request nearly every tile at once (progress falls back to 0 %). Each step waits until the view is ~loaded.
                float now = Time.unscaledTime;
                if (now - _turboStart > 2f && now - _turboStep >= 1.5f && (tileset.ComputeLoadProgress() >= 85f || now - _turboStep >= 3f))
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
                // Beauty pass: no holes. Tiles outside every camera are still loaded, but only down to a coarse level (culledScreenSpaceError),
                // so turning the camera never shows empty ground and the finer levels only stream in where you look / are heading.
                tileset.enforceCulledScreenSpaceError = true;
                tileset.culledScreenSpaceError = CulledSse;
                tileset.maximumSimultaneousTileLoads = DeviceProfile.SimultaneousLoads;
                tileset.loadingDescendantLimit = 120;       // let many visible tiles refine in parallel
                // Optional tuning: set by reflection so a property that differs in this Cesium version cannot break the build.
                TrySetTileset("preloadSiblings", false);    // siblings are mostly outside the view
                TrySetTileset("enableFoveatedRendering", false);   // foveation blurs everything off-centre: terrain should be sharp edge to edge
                _turbo = true; _turboStart = _turboStep = Time.unscaledTime; _turboCur = TurboSse;
                tileset.maximumCachedBytes = DeviceProfile.TileCacheBytes;   // WebGL heap is limited: sized from the device
                tileset.maximumScreenSpaceError = EffectiveSse;
                _appliedSse = EffectiveSse;
                if (tileLog != null) tileLog.Begin(tileset);
                if (settings.viewCull) { viewPolicy = go.AddComponent<TileViewPolicy>(); viewPolicy.root = this; viewPolicy.tileset = tileset; }
                budget = go.AddComponent<TileColliderBudget>();        // subscribes in OnEnable, before the tileset starts loading
                budget.tileset = tileset;
                budget.focus = car;
                budget.extraFoci = extraFoci;
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
            // new city: arrive by plane again (no-op before the first start and when the intro is disabled)
            if (!sameLatLon && carCtl.intro != null) carCtl.intro.Restart(false);
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
            {
                // all tiles reload: cover it with the plane drop (falls back to the short freeze when the intro is off)
                if (carCtl.intro == null || !carCtl.intro.Restart(true)) carCtl.SoftHoldForTerrain(1.5f);
            }
        }

        /// <summary>Force an immediate origin commit (e.g. after "Reload tiles" or a city preset that already called ApplyLive).</summary>
        public void FlushOrigin()
        {
            if (!_originPending) return;
            _originPending = false;
            CommitOrigin(_pendingLon, _pendingLat, _pendingGh, forceRespawn: true);
        }

        /// <summary>Teleport the georeference + car to a new lat/lon (and optional ground height). Applies immediately.</summary>
        public void TeleportTo(double lat, double lon, float groundHeight)
        {
            settings.lat = lat;
            settings.lon = lon;
            settings.groundHeight = groundHeight;
            _originPending = false;
            CommitOrigin(lon, lat, groundHeight, forceRespawn: true);
            ApplyLive();
            SaveSettings();
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
            // Atmospheric haze: starts early (~500 m at far 20 km) so far-away LOD changes dissolve into the sky colour and the world gains depth.
            RenderSettings.fogStartDistance = EffectiveFar * 0.025f;
            RenderSettings.fogEndDistance = EffectiveFar * 0.35f;
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
            var intro = gameObject.AddComponent<PlaneIntro>();            // opening: start inside a cargo plane 1 km up
            intro.root = this;
            carCtl.intro = intro;
            ball = gameObject.AddComponent<RocketBall>();                 // appears when the player presses the Ball button
            ball.root = this;
            carCtl.ball = ball;
        }

        /// <summary>Water system: one shared map (OpenFreeMap vector tiles) and one driver per simulated car.</summary>
        void BuildWater()
        {
            if (!settings.water) return;
            waterMap = gameObject.AddComponent<WaterMap>();
            waterMap.georeference = georeference;
            AttachWater(carCtl);
        }

        void AttachPlane(RocketCar c)
        {
            if (c == null || c.plane != null) return;
            var d = c.gameObject.AddComponent<PlaneDriver>();
            d.car = c; c.plane = d;
        }

        void AttachWater(RocketCar c)
        {
            if (waterMap == null || c == null || c.water != null) return;
            var d = c.gameObject.AddComponent<WaterDriver>();
            d.car = c; d.map = waterMap;
            c.water = d;
        }

        void BuildCamera()
        {
            var go = new GameObject("Main Camera") { tag = "MainCamera" };
            go.transform.SetParent(georeference.transform, false);
            cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.nearClipPlane = 0.5f;                                    // far plane is up to 20 km: a larger near plane keeps depth precision (no z-fighting between tile LODs)
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
            var streamGo = new GameObject("StreamingCameras");
            var stream = streamGo.AddComponent<StreamingCameras>(); stream.root = this; stream.follow = car;
            orbit = go.AddComponent<OrbitCamera>();
            orbit.target = car;
            orbit.yaw = settings.camYaw; orbit.pitch = settings.camPitch; orbit.distance = settings.camDist;
            orbit.autoRotate = settings.autoRotate;
            SetCameraMode(0);
        }

        // ---- multiplayer helpers ----------------------------------------------------------------------------

        /// <summary>Start driving (solo, host or client). Releases the car held by the start menu and switches to the chase camera.</summary>
        public void BeginPlay()
        {
            if (carCtl != null) carCtl.ReleaseStart();
            SetCameraMode(0);
        }

        /// <summary>Put the ball in front of the local car (solo / host) or ask the host to do it (client).</summary>
        /// <summary>Plane mode on / off for the local car (solo and host; a client-only player has no local physics to switch).</summary>
        public void RequestPlane()
        {
            if (carCtl != null && carCtl.plane != null && carCtl.simulated) carCtl.plane.Toggle();
        }

        public void RequestBall()
        {
            if (net != null && net.IsClientOnly) net.RequestBall();
            else if (ball != null && carCtl != null) ball.Summon(carCtl);
        }

        /// <summary>Another player's car. simulated = host (physics runs here), otherwise a puppet fed by snapshots.</summary>
        public RocketCar BuildRemoteCar(int colorIdx, string playerName, bool simulated)
        {
            var go = new GameObject("Car_" + playerName);
            go.transform.SetParent(georeference.transform, false);
            go.AddComponent<CesiumGlobeAnchor>();
            var rc = go.AddComponent<RocketCar>();                      // builds Rigidbody + collider + vehicle model in Awake
            rc.georeference = georeference; rc.tileset = tileset; rc.colliderBudget = budget;
            rc.input = null; rc.useNetControls = true; rc.spawnHeadingDeg = settings.heading;
            rc.playerName = playerName;
            colorIdx = Mathf.Clamp(colorIdx, 0, GameSettings.CarColors.Length - 1);
            rc.visual = CarVisual.Build(go.transform, GameSettings.CarColors[colorIdx].color, -0.3406f);
            AttachPlane(rc);
            AttachWater(rc);                                            // the host simulates remote cars: they float too (puppets just follow the snapshot)
            if (!simulated) rc.SetSimulated(false);
            return rc;
        }

        /// <summary>Host: keep terrain tiles streaming + colliders around a remote car.</summary>
        public void TrackRemote(Transform t)
        {
            if (t == null || _streams.ContainsKey(t)) return;
            extraFoci.Add(t);
            var sg = new GameObject("StreamingCameras_Remote");
            var sc = sg.AddComponent<StreamingCameras>(); sc.root = this; sc.follow = t;
            _streams[t] = sg;
        }

        public void UntrackRemote(Transform t)
        {
            if (t == null) return;
            extraFoci.Remove(t);
            if (_streams.TryGetValue(t, out var sg)) { if (sg != null) Destroy(sg); _streams.Remove(t); }
        }

        public void DestroyRemoteCar(RocketCar c)
        {
            if (c == null || c == carCtl) return;
            UntrackRemote(c.transform);
            Destroy(c.gameObject);
        }

        public void SetCameraMode(int mode)
        {
            cameraMode = mode;
            if (chase != null) chase.enabled = mode == 0;
            if (orbit != null) orbit.enabled = mode == 1;
        }
    }
}
