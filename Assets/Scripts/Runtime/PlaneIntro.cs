using CesiumForUnity;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Opening sequence. The game starts with the player aboard a flying "battle bus" (blue bus under a balloon) 1 km above the start
    /// point, seen from a third-person camera. The car is parked inside the bus (not simulated, hidden). While the map loads the
    /// page's loading video loops over the game and the jump is locked; when the terrain has loaded the video fades out, the bus
    /// starts to cruise and a prompt asks for the Jump button. Pressing it drops the car out of the bus and it falls to the ground.
    /// The bus then flies away and is removed.
    ///
    /// The bus is built from primitives (no model asset). Axes of the bus: nose = +Z, up = +Y, origin in the middle of the body.
    /// If the intro is disabled (?intro=0, benchmark) or cannot be built, the game starts on the ground as before.
    /// </summary>
    [DefaultExecutionOrder(150)]
    public class PlaneIntro : MonoBehaviour
    {
        public enum Stage { Idle, InPlane, Ready, Falling, Done }

        public GameRoot root;
        public Stage CurrentStage { get; private set; } = Stage.Idle;
        /// <summary>True while the player must not use "rescue" (it would raycast onto the bus or into thin air).</summary>
        public bool BlocksRescue => CurrentStage != Stage.Idle && CurrentStage != Stage.Done;
        /// <summary>True while the jump is locked because the map is still loading.</summary>
        public bool JumpLocked => CurrentStage == Stage.InPlane;

        public const float Altitude = 1000f;          // m above the start point (Unity y = 0 is ground level at the origin)
        const float MinWait = 4f;                     // never unlock earlier than this, so the loading video is not just a flash
        const float Timeout = 45f;                    // unlock anyway if the terrain never finishes loading
        const float ReadyGrace = 0.4f;                // a jump pressed in the very moment the video fades out is ignored
        const float CruiseSpeed = 22f;                // the bus cruises forward once the jump is unlocked
        const float AwaySpeed = 60f;                  // fly-away speed after the player jumped
        const float DropBelow = 3.4f;                 // the car appears this far below the bus centre (under the belly)

        // third-person camera (bus space: +Z nose)
        const float CamBack = 23f, CamUp = 8f, CamSide = 4.5f, CamFov = 62f;

        RocketCar _car;
        CesiumGlobeAnchor _carAnchor;
        GameObject _plane;
        Transform _model;
        float _t0, _readyT0, _loadedSince = -1f, _fallT0, _planeSpeed;
        double _startHeight;
        bool _prevJump, _camInit;
        int _lastPct = -1;

        bool _began, _visualOnly;
        public Transform PlaneTransform => _plane != null && !_visualOnly ? _plane.transform : null;

        /// <summary>First start of the game: bus above the start point.</summary>
        public bool Begin(RocketCar car)
        {
            if (_began) return false;
            _began = true;
            _car = car;
            _carAnchor = car.GetComponent<CesiumGlobeAnchor>();
            float hd = root != null ? root.settings.heading : 0f;
            return Spawn(SlotOffset(hd), hd);
        }

        /// <summary>Multiplayer: every player flies in their own bus, side by side (the player's slot picks the lane). 0 offline.</summary>
        public static int NetSlot;
        static Vector3 SlotOffset(float headingDeg) => NetSlot > 0 ? Quaternion.Euler(0f, headingDeg, 0f) * Vector3.right * (PlaneSpacing * NetSlot) : Vector3.zero;

        /// <summary>
        /// Run the sequence again: after teleporting to another city (atCar = false: above the new origin) or after a detail (SSE)
        /// change (atCar = true: above where the car is, same heading). Returns false if the intro is unavailable / disabled,
        /// so the caller can fall back to the old behaviour.
        /// </summary>
        public bool Restart(bool atCar)
        {
            if (!_began || _car == null || root == null || root.settings.bench || root.settings.noIntro) return false;
            Vector3 xz = Vector3.zero;
            float heading = root.settings.heading;
            if (atCar)
            {
                var p = _car.transform.position; xz = new Vector3(p.x, 0f, p.z);
                var f = _car.transform.forward; f.y = 0f;
                if (f.sqrMagnitude > 1e-3f) heading = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            }
            if (!atCar) xz += SlotOffset(heading);
            ClearPlane();
            return Spawn(xz, heading);
        }

        /// <summary>Host: another player's car starts in its own bus, flying in formation next to the reference bus (or above the reference car).</summary>
        public bool BeginRemote(RocketCar car, RocketCar reference, int slot)
        {
            if (_began || car == null || root == null) return false;
            Vector3 basePos; float heading;
            var rt = reference != null && reference.intro != null ? reference.intro.PlaneTransform : null;
            var st = reference != null && reference.intro != null ? reference.intro.CurrentStage : Stage.Idle;
            if (rt != null && (st == Stage.InPlane || st == Stage.Ready))
            {
                basePos = rt.position; heading = rt.eulerAngles.y;
            }
            else
            {
                var rp = reference != null ? reference.transform : null;
                basePos = rp != null ? rp.position : Vector3.zero;
                var f = rp != null ? rp.forward : Vector3.forward; f.y = 0f;
                heading = f.sqrMagnitude > 1e-3f ? Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg : root.settings.heading;
            }
            var right = Quaternion.Euler(0f, heading, 0f) * Vector3.right;
            var xz = new Vector3(basePos.x, 0f, basePos.z) + right * (PlaneSpacing * slot);
            _began = true; _car = car; _carAnchor = car.GetComponent<CesiumGlobeAnchor>();
            return Spawn(xz, heading);
        }

        const float PlaneSpacing = 14f;               // formation: buses side by side (the balloon is ~8 m wide)

        // ---- multiplayer replication (host sends the pose, clients show a model without colliders)

        /// <summary>Host: pose of this bus (the "ramp" value is unused now and always 0), false when there is no bus.</summary>
        public bool TryGetPose(out Vector3 pos, out Quaternion rot, out float ramp)
        {
            pos = default; rot = Quaternion.identity; ramp = 0f;
            if (_plane == null || _visualOnly) return false;
            pos = _plane.transform.position; rot = _plane.transform.rotation;
            return true;
        }

        /// <summary>Client: build the bus model only (no physics, no car).</summary>
        public bool StartVisual() { if (_plane != null) return true; return Spawn(Vector3.zero, 0f, true); }

        public void SetVisualPose(Vector3 pos, Quaternion rot, float unused)
        {
            if (_plane == null) return;
            _plane.transform.SetPositionAndRotation(pos, rot);
        }

        void ClearPlane()
        {
            if (_plane != null) { _plane.SetActive(false); Destroy(_plane); _plane = null; }
            if (_car != null) _car.Unpark();
            CurrentStage = Stage.Idle; _loadedSince = -1f; _planeSpeed = 0f; _camInit = false; _lastPct = -1;
            if (_car != null && _car.colliderBudget != null) _car.colliderBudget.burst = false;
        }

        bool Spawn(Vector3 xz, float heading, bool visual = false)
        {
            if (root == null || root.georeference == null) return false;
            if (!visual && (root.settings.bench || root.settings.noIntro)) return false;

            _plane = new GameObject("BattleBus");
            _plane.transform.SetParent(root.georeference.transform, false);
            _model = BuildBus(_plane.transform);
            SetLayer(_plane, PhysicsSetup.PlaneLayer);

            _plane.transform.rotation = Quaternion.Euler(0f, heading, 0f);
            _plane.transform.position = new Vector3(xz.x, Altitude, xz.z);
            if (visual) { _visualOnly = true; return true; }         // joined client: just the model, the pose comes from the host
            _plane.AddComponent<CesiumGlobeAnchor>();                // stays put when Cesium re-centres the origin

            ParkCar();
            _startHeight = _carAnchor != null ? _carAnchor.longitudeLatitudeHeight.z : 0.0;
            _t0 = Time.unscaledTime;
            _planeSpeed = 0f; _loadedSince = -1f; _camInit = false; _lastPct = -1;
            _prevJump = true;                                        // a jump button already held at this moment is not a press
            CurrentStage = Stage.InPlane;
            WebBridge.ShowLoading();                                 // map loading: the loading video loops over the game until it is ready
            return true;
        }

        void ParkCar()
        {
            if (_car != null && _plane != null) _car.ParkAt(_plane.transform.position, _plane.transform.rotation);
        }

        // ---- the bus -------------------------------------------------------------------------------------

        static Transform BuildBus(Transform parent)
        {
            var root = new GameObject("Model").transform;
            root.SetParent(parent, false);
            var blue = Mat(new Color(0.10f, 0.28f, 0.85f), 0.45f);
            var navy = Mat(new Color(0.04f, 0.09f, 0.32f), 0.4f);
            var glass = Mat(new Color(0.03f, 0.05f, 0.07f), 0.85f);
            var metal = Mat(new Color(0.24f, 0.24f, 0.27f), 0.3f);
            var rope = Mat(new Color(0.45f, 0.38f, 0.20f), 0.1f);
            var teal = Mat(new Color(0.12f, 0.72f, 0.84f), 0.4f);
            var white = Mat(new Color(0.90f, 0.93f, 0.96f), 0.4f);
            var yellow = Mat(new Color(0.95f, 0.78f, 0.12f), 0.4f);

            // body (hollow in spirit: the car is parked inside it, so it stays hidden)
            Part(root, PrimitiveType.Cube, "Body", new Vector3(0f, 0f, 0f), new Vector3(2.6f, 2.4f, 9f), blue);
            Part(root, PrimitiveType.Cube, "Hood", new Vector3(0f, -0.35f, 5.1f), new Vector3(2.4f, 1.5f, 1.8f), blue);
            Part(root, PrimitiveType.Cube, "Bumper", new Vector3(0f, -0.7f, 6.05f), new Vector3(2.6f, 0.5f, 0.35f), metal);
            Part(root, PrimitiveType.Cube, "Underside", new Vector3(0f, -1.3f, 0.4f), new Vector3(2.5f, 0.3f, 10.4f), navy);
            Part(root, PrimitiveType.Cube, "RoofStripe", new Vector3(0f, 1.22f, 0f), new Vector3(1.6f, 0.08f, 8.4f), white);
            Part(root, PrimitiveType.Cube, "Stripe", new Vector3(0f, -0.35f, 0f), new Vector3(2.64f, 0.12f, 9.02f), navy);
            Part(root, PrimitiveType.Cube, "Windshield", new Vector3(0f, 0.4f, 4.51f), new Vector3(2.2f, 0.85f, 0.06f), glass);
            Part(root, PrimitiveType.Cube, "Rear", new Vector3(0f, 0.2f, -4.52f), new Vector3(2.2f, 1.0f, 0.06f), glass);
            for (int side = -1; side <= 1; side += 2)
            {
                float x = side * 1.31f;
                Part(root, PrimitiveType.Cube, "Windows", new Vector3(x, 0.4f, 0f), new Vector3(0.06f, 0.8f, 7.6f), glass);
                Part(root, PrimitiveType.Cube, "Pane", new Vector3(x, 0.4f, 0f), new Vector3(0.08f, 0.9f, 0.12f), navy);
                for (int k = -3; k <= 3; k++)
                    Part(root, PrimitiveType.Cube, "Pillar", new Vector3(x, 0.4f, k * 1.05f), new Vector3(0.09f, 0.9f, 0.1f), navy);
                Part(root, PrimitiveType.Cube, "Light", new Vector3(side * 0.9f, -0.35f, 6.0f), new Vector3(0.4f, 0.28f, 0.1f), yellow);
            }

            // engine block + frame under the balloon
            Part(root, PrimitiveType.Cube, "Engine", new Vector3(0f, 1.9f, 0f), new Vector3(1.8f, 0.9f, 2.2f), metal);
            Part(root, PrimitiveType.Cylinder, "Ring", new Vector3(0f, 8.2f, 0f), new Vector3(3.2f, 0.18f, 3.2f), metal);
            Vector3[] foot = { new Vector3(-1f, 1.2f, -3f), new Vector3(1f, 1.2f, -3f), new Vector3(-1f, 1.2f, 3f), new Vector3(1f, 1.2f, 3f) };
            Vector3[] top = { new Vector3(-1.4f, 8.1f, -1.4f), new Vector3(1.4f, 8.1f, -1.4f), new Vector3(-1.4f, 8.1f, 1.4f), new Vector3(1.4f, 8.1f, 1.4f) };
            for (int i = 0; i < 4; i++) Strut(root, foot[i], top[i], 0.07f, rope);

            // balloon: teal envelope with a white belt
            Part(root, PrimitiveType.Sphere, "Balloon", new Vector3(0f, 12.4f, 0f), new Vector3(7.6f, 8.6f, 7.6f), teal);
            Part(root, PrimitiveType.Cylinder, "Belt", new Vector3(0f, 11.3f, 0f), new Vector3(7.55f, 0.9f, 7.55f), white);
            Part(root, PrimitiveType.Sphere, "Knot", new Vector3(0f, 8.3f, 0f), new Vector3(1.1f, 0.6f, 1.1f), yellow);
            return root;
        }

        static GameObject Part(Transform parent, PrimitiveType type, string name, Vector3 pos, Vector3 scale, Material m)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var col = go.GetComponent<Collider>(); if (col != null) Destroy(col);        // pure visuals
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos; go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            return go;
        }

        /// <summary>A thin cylinder between two points (local space).</summary>
        static void Strut(Transform parent, Vector3 a, Vector3 b, float radius, Material m)
        {
            var d = b - a;
            var go = Part(parent, PrimitiveType.Cylinder, "Strut", (a + b) * 0.5f, new Vector3(radius * 2f, d.magnitude * 0.5f, radius * 2f), m);
            go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, d.normalized);
        }

        static Material Mat(Color c, float smooth)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Simple Lit");
            var m = new Material(sh != null ? sh : Shader.Find("Hidden/InternalErrorShader")) { name = "RW_Bus" };
            c.a = 1f;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.05f);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
            return m;
        }

        // ---- sequence ------------------------------------------------------------------------------------

        /// <summary>R while aboard is ignored (there is nothing to rescue). Returns true if handled.</summary>
        public bool TryResetInBay() => BlocksRescue;

        bool _loaderLifted;

        string JumpKeyName()
        {
            if (DeviceProfile.IsMobile) return null;
            string k = _car != null && _car.input != null && _car.input.bindings != null ? _car.input.bindings.Get(CarAction.Jump).key0 : "Space";
            return string.IsNullOrEmpty(k) || k == "Space" ? "ESPACIO" : k.ToUpperInvariant();
        }

        void Update()
        {
            if (_visualOnly) { if (!_loaderLifted) { _loaderLifted = true; WebBridge.HideLoading(); } return; }
            if (_plane == null) { if (CurrentStage != Stage.Done) Finish(); return; }
            float now = Time.unscaledTime, dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);

            // someone respawned the car on the ground (new city, new credentials): drop the intro
            if (_car.CurrentPhase == RocketCar.Phase.WaitingForGround && CurrentStage != Stage.Idle && CurrentStage != Stage.Done) { Abort(); return; }

            bool jump = _car.ActiveControls.jump;
            bool jumpPressed = jump && !_prevJump;
            _prevJump = jump;

            if (_model != null)                                      // the bus floats: gentle bob and sway
            {
                _model.localPosition = new Vector3(0f, Mathf.Sin(now * 1.4f) * 0.22f, 0f);
                _model.localRotation = Quaternion.Euler(Mathf.Sin(now * 0.8f) * 0.8f, 0f, Mathf.Sin(now * 1.1f) * 1.6f);
            }

            switch (CurrentStage)
            {
                case Stage.InPlane:
                {
                    ParkCar();
                    float t = now - _t0;
                    float progress = _car.tileset != null ? _car.tileset.ComputeLoadProgress() : 100f;
                    if (progress >= 99.9f) { if (_loadedSince < 0f) _loadedSince = now; } else _loadedSince = -1f;
                    // Total load wipes the cache and loads every direction: Cesium reports ~100 % of what it has selected so far (coarse tiles, or none
                    // yet right after the wipe), so the jump must wait for Total load's own "everything loaded and calm" signal, with a longer timeout.
                    var tl = root != null ? root.totalLoad : null;
                    bool total = tl != null && tl.Active;
                    bool loaded = t >= MinWait && _loadedSince > 0f && now - _loadedSince > 0.8f && (!total || tl.Finished);
                    bool ready = loaded || t >= (total ? Timeout * 4f : Timeout);
                    if (total && !ready) progress = Mathf.Min(progress, 99f);
                    if (!ready)
                    {
                        int pct = Mathf.Clamp((int)progress, 0, 99);
                        if (pct != _lastPct) { _lastPct = pct; _car.SetStatus(total && !string.IsNullOrEmpty(tl.Status) ? tl.Status : $"Cargando el mapa… {pct}%"); }    // jump stays locked
                        break;
                    }
                    _readyT0 = now; CurrentStage = Stage.Ready;
                    WebBridge.HideLoading();                         // map loaded: the looping loading video ends, the bus appears
                    if (_car.colliderBudget != null) _car.colliderBudget.burst = true;   // build terrain colliders ahead of the fall
                    string key = JumpKeyName();
                    _car.SetStatus(key != null ? $"Pulsa {key} para saltar del autobús" : "Toca SALTAR para lanzarte del autobús");
                    break;
                }
                case Stage.Ready:
                {
                    ParkCar();
                    _planeSpeed = Mathf.MoveTowards(_planeSpeed, CruiseSpeed, 6f * dt);
                    _plane.transform.position += _plane.transform.forward * (_planeSpeed * dt);
                    if (jumpPressed && now - _readyT0 > ReadyGrace) Jump(now);
                    break;
                }
                case Stage.Falling:
                {
                    // the bus leaves
                    _planeSpeed = Mathf.MoveTowards(_planeSpeed, AwaySpeed, 14f * dt);
                    _plane.transform.position += _plane.transform.forward * (_planeSpeed * dt);
                    if (now - _fallT0 > 60f) { Destroy(_plane); _plane = null; }

                    // landed?
                    if (now - _fallT0 > 3f && _car.State.isOnGround) Finish();
                    // fell through the world (colliders never appeared): fall back to the normal ground spawn
                    else if (_carAnchor != null && _carAnchor.longitudeLatitudeHeight.z < _startHeight - (Altitude + 450.0)) { Finish(); _car.Respawn(false); }
                    break;
                }
            }
        }

        /// <summary>
        /// Third-person view of the bus while aboard: behind, above and a little to the side, with the balloon in frame. Runs after the
        /// normal cameras (ChaseCamera 100) and before the Camera-menu offset (CamRigEnd 200); afterwards the chase camera simply
        /// glides from this position to the falling car.
        /// </summary>
        void LateUpdate()
        {
            if (_visualOnly || _plane == null || root == null || root.cam == null || _car == null || _car != root.carCtl) return;
            if (CurrentStage != Stage.InPlane && CurrentStage != Stage.Ready) { _camInit = false; return; }
            var cam = root.cam;
            var ct = cam.transform;
            var bt = _plane.transform;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);

            var fwd = Vector3.ProjectOnPlane(bt.forward, Vector3.up).normalized;
            var right = Vector3.Cross(Vector3.up, fwd);
            var want = bt.position - fwd * CamBack + Vector3.up * CamUp + right * CamSide;
            var look = bt.position + Vector3.up * 5.5f + fwd * 1.5f;
            var pos = _camInit ? Vector3.Lerp(ct.position, want, 1f - Mathf.Exp(-6f * dt)) : want;
            _camInit = true;
            ct.SetPositionAndRotation(pos, Quaternion.LookRotation(look - pos, Vector3.up));
            cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, CamFov, 1f - Mathf.Exp(-4f * dt));
        }

        void Jump(float now)
        {
            var t = _plane.transform;
            var pos = t.position - Vector3.up * DropBelow;
            var rot = Quaternion.LookRotation(Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized, Vector3.up);
            _car.LaunchFrom(pos, rot, t.forward * _planeSpeed);
            CurrentStage = Stage.Falling; _fallT0 = now;
            _car.SetStatus("");
            if (_car.colliderBudget != null) _car.colliderBudget.burst = true;       // build terrain colliders as fast as possible on the way down
        }

        void Finish()
        {
            CurrentStage = Stage.Done;
            WebBridge.HideLoading();   // safety net: no bus / aborted intro still lifts the loading video
            if (_car != null)
            {
                _car.Unpark();
                _car.SetStatus("");
                if (_car.colliderBudget != null) _car.colliderBudget.burst = false;
            }
        }

        /// <summary>Drop the bus and the intro (joining a multiplayer game as a client).</summary>
        public void Abort()
        {
            if (_plane != null) Destroy(_plane);
            _plane = null;
            Finish();
        }

        /// <summary>
        /// glTFast's shader-graph materials are stripped from WebGL builds (the meshes then render as nothing - same problem the car had).
        /// Swap them for URP Lit, which is always in the build, keeping the base texture. Double sided, opaque (even the glass).
        /// </summary>
        internal static void FixMaterials(GameObject model)
        {
            var cache = new System.Collections.Generic.Dictionary<Material, Material>();
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                r.enabled = true;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                var old = r.sharedMaterials;
                var nm = new Material[old.Length];
                for (int i = 0; i < old.Length; i++)
                {
                    var src = old[i];
                    if (src == null) { nm[i] = null; continue; }
                    if (!cache.TryGetValue(src, out var m)) cache[src] = m = ToUrpLit(src);
                    nm[i] = m;
                }
                r.sharedMaterials = nm;
            }
        }

        static Material ToUrpLit(Material src)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Simple Lit");
            var m = new Material(sh != null ? sh : Shader.Find("Hidden/InternalErrorShader")) { name = "RW_" + src.name };
            bool glass = src.name.IndexOf("Glass", System.StringComparison.OrdinalIgnoreCase) >= 0;
            Texture tex = null; Color c = Color.white;
            foreach (var n in new[] { "baseColorTexture", "_BaseMap", "_MainTex" }) if (src.HasProperty(n)) { tex = src.GetTexture(n); if (tex != null) break; }
            foreach (var n in new[] { "baseColorFactor", "_BaseColor", "_Color" }) if (src.HasProperty(n)) { c = src.GetColor(n); break; }
            if (glass) { c = new Color(0.03f, 0.05f, 0.06f, 1f); tex = null; }
            c.a = 1f;
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", glass ? 0f : 0.1f);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", glass ? 0.85f : 0.35f);
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);          // double sided: the hold is seen from inside
            return m;
        }

        internal static void SetLayer(GameObject go, int layer)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }
    }
}
