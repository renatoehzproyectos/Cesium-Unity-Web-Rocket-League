using CesiumForUnity;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Opening sequence. The game starts with the car parked inside a C-130 style cargo plane, 1 km above the start point.
    /// You can drive around the hold but cannot leave it; when the terrain below has loaded the rear ramp swings open
    /// (animated), you drive off the end and fall to the ground. The plane then flies away and is removed.
    ///
    /// The model (Resources/Models/YC130.glb, CC BY 4.0, see THIRD_PARTY.md) was pre-processed: the closed ramp that was welded to
    /// the hull is its own node "RearRamp" so it can be animated. The hold is made of invisible box colliders (floor, walls, ceiling,
    /// bulkhead) plus a collider that follows the ramp. All numbers below are in the model's own space (metres, nose towards -Z).
    /// If the model cannot be loaded the game simply starts on the ground as before.
    /// </summary>
    public class PlaneIntro : MonoBehaviour
    {
        public enum Stage { Idle, InPlane, Opening, Open, Falling, Done }

        public GameRoot root;
        public Stage CurrentStage { get; private set; } = Stage.Idle;
        /// <summary>True while the player must not use "rescue" (it would raycast onto the plane or into thin air).</summary>
        public bool BlocksRescue => CurrentStage != Stage.Idle && CurrentStage != Stage.Done;

        public const float Altitude = 1000f;          // m above the start point (Unity y = 0 is ground level at the origin)
        const float MinWait = 6f;                     // never open earlier than this, so the player gets a moment inside
        const float Timeout = 45f;                    // open anyway if the terrain never finishes loading
        const float OpenSeconds = 6f;
        const float SlopeDeg = 23f;                   // closed ramp rises towards the tail at this angle
        const float DropDeg = 8f;                     // open ramp points slightly downwards
        const float PlaneSpeed = 75f;                 // fly-away speed

        // hold geometry (model space)
        const float FloorY = 0.956f, CeilY = 3.6f, HalfW = 1.5f;
        const float FrontZ = -5.9f, HingeZ = 5.874f, RampLen = 7.8f, RampW = 3.1f;
        static readonly Vector3 CarSpawn = new Vector3(0f, FloorY + 0.5f, -2.6f);

        RocketCar _car;
        CesiumGlobeAnchor _carAnchor;
        GameObject _plane;
        Transform _bay, _hinge;
        bool _used;
        float _t0, _openT0, _loadedSince = -1f, _fallT0, _planeSpeed;
        double _startHeight;

        public bool Begin(RocketCar car)
        {
            if (_used) return false;
            _used = true;
            if (root == null || root.settings.bench || root.settings.noIntro || root.georeference == null) return false;
            var prefab = Resources.Load<GameObject>("Models/YC130");
            if (prefab == null) { Debug.LogWarning("[RocketWorld] Cargo plane model not found - starting on the ground."); return false; }

            _car = car;
            _carAnchor = car.GetComponent<CesiumGlobeAnchor>();

            _plane = new GameObject("CargoPlane");
            _plane.transform.SetParent(root.georeference.transform, false);
            var model = Instantiate(prefab, _plane.transform);
            model.name = "Model";
            model.transform.localPosition = Vector3.zero; model.transform.localRotation = Quaternion.identity; model.transform.localScale = Vector3.one;
            SetLayer(model, PhysicsSetup.PlaneLayer);
            FixMaterials(model);

            // Which way does the imported model face? The cockpit ("Interior") must sit at -Z of the model.
            bool flipped = false;
            var interior = FindDeep(model.transform, "Interior");
            var ir = interior != null ? interior.GetComponentInChildren<Renderer>() : null;
            var all = model.GetComponentsInChildren<Renderer>();
            if (ir != null && all.Length > 0)
            {
                var b = all[0].bounds;
                foreach (var r in all) b.Encapsulate(r.bounds);
                flipped = _plane.transform.InverseTransformPoint(ir.bounds.center).z > _plane.transform.InverseTransformPoint(b.center).z;
            }

            _bay = new GameObject("Bay").transform;
            _bay.SetParent(_plane.transform, false);
            _bay.localRotation = flipped ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;

            BuildHold();

            // hinge for the rear ramp
            _hinge = new GameObject("RampHinge").transform;
            _hinge.SetParent(_bay, false);
            _hinge.localPosition = new Vector3(0f, FloorY, HingeZ);
            var ramp = FindDeep(model.transform, "RearRamp");
            if (ramp != null) ramp.SetParent(_hinge, true);          // keeps its pose; now it rotates around the hinge
            var rc = new GameObject("RampCollider");
            rc.layer = PhysicsSetup.PlaneLayer;
            rc.transform.SetParent(_hinge, false);
            rc.transform.localRotation = Quaternion.Euler(-SlopeDeg, 0f, 0f);
            var bc = rc.AddComponent<BoxCollider>();
            bc.size = new Vector3(RampW, 0.4f, RampLen); bc.center = new Vector3(0f, -0.2f, RampLen * 0.5f);
            bc.sharedMaterial = PhysicsSetup.WorldMaterial;

            // place the plane so that the car (which faces the tail) looks along the configured heading
            float heading = root.settings.heading;
            _plane.transform.rotation = Quaternion.Euler(0f, heading, 0f) * Quaternion.Inverse(_bay.localRotation);
            _plane.transform.position = new Vector3(0f, Altitude, 0f);
            _plane.AddComponent<CesiumGlobeAnchor>();                // stays put when Cesium re-centres the origin
            Physics.SyncTransforms();

            PlaceCarInBay();
            _startHeight = _carAnchor != null ? _carAnchor.longitudeLatitudeHeight.z : 0.0;
            _t0 = Time.unscaledTime;
            CurrentStage = Stage.InPlane;
            return true;
        }

        void BuildHold()
        {
            float zEnd = 13.2f;                                       // end of the roof / side walls
            float len = zEnd - FrontZ, zc = (zEnd + FrontZ) * 0.5f;
            float h = CeilY - FloorY;
            Box("Floor", new Vector3(0f, FloorY - 0.25f, (HingeZ + FrontZ) * 0.5f), new Vector3(HalfW * 2f + 0.8f, 0.5f, HingeZ - FrontZ));
            Box("WallL", new Vector3(-HalfW - 0.25f, FloorY + h * 0.5f, zc), new Vector3(0.5f, h + 1f, len));
            Box("WallR", new Vector3(HalfW + 0.25f, FloorY + h * 0.5f, zc), new Vector3(0.5f, h + 1f, len));
            Box("Ceiling", new Vector3(0f, CeilY + 0.25f, zc), new Vector3(HalfW * 2f + 1f, 0.5f, len));
            Box("Bulkhead", new Vector3(0f, FloorY + h * 0.5f, FrontZ - 0.25f), new Vector3(HalfW * 2f + 1f, h + 1f, 0.5f));
        }

        void Box(string name, Vector3 center, Vector3 size)
        {
            var go = new GameObject(name) { layer = PhysicsSetup.PlaneLayer };
            go.transform.SetParent(_bay, false);
            go.transform.localPosition = center;
            var bc = go.AddComponent<BoxCollider>();
            bc.size = size; bc.sharedMaterial = PhysicsSetup.WorldMaterial;
        }

        void PlaceCarInBay()
        {
            var pos = _bay.TransformPoint(CarSpawn);
            var rot = Quaternion.LookRotation(_bay.forward, Vector3.up);
            _car.PlaceDirect(pos, rot);
        }

        /// <summary>R while in the plane puts the car back in the hold; while falling it is ignored. Returns true if handled.</summary>
        public bool TryResetInBay()
        {
            if (!BlocksRescue) return false;
            if (CurrentStage == Stage.InPlane || CurrentStage == Stage.Opening || CurrentStage == Stage.Open)
            {
                var local = _bay.InverseTransformPoint(_car.transform.position);
                if (local.z < 14f) PlaceCarInBay();
            }
            return true;
        }

        void Update()
        {
            if (_plane == null) { if (CurrentStage != Stage.Done) Finish(); return; }
            float now = Time.unscaledTime, dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);

            // someone respawned the car on the ground (new city, new credentials): drop the intro
            if (_car.CurrentPhase == RocketCar.Phase.WaitingForGround && CurrentStage != Stage.Idle && CurrentStage != Stage.Done) { Abort(); return; }

            switch (CurrentStage)
            {
                case Stage.InPlane:
                {
                    float t = now - _t0;
                    float progress = _car.tileset != null ? _car.tileset.ComputeLoadProgress() : 100f;
                    if (progress >= 99.9f) { if (_loadedSince < 0f) _loadedSince = now; } else _loadedSince = -1f;
                    bool ready = (t >= MinWait && _loadedSince > 0f && now - _loadedSince > 0.8f) || t >= Timeout;
                    if (!ready) { _car.SetStatus(t < MinWait && progress >= 99.9f ? "Approaching the drop zone…" : $"Approaching the drop zone… {progress:0}%"); break; }
                    _openT0 = now; CurrentStage = Stage.Opening;
                    _car.SetStatus("Rear ramp opening…");
                    if (_car.colliderBudget != null) _car.colliderBudget.burst = true;
                    break;
                }
                case Stage.Opening:
                {
                    float e = Mathf.Clamp01((now - _openT0) / OpenSeconds);
                    _hinge.localRotation = Quaternion.Euler((SlopeDeg + DropDeg) * Mathf.SmoothStep(0f, 1f, e), 0f, 0f);
                    if (e >= 1f) { CurrentStage = Stage.Open; _car.SetStatus("Drop zone - go!"); _openT0 = now; }
                    break;
                }
                case Stage.Open:
                {
                    if (now - _openT0 > 3f) _car.SetStatus("");
                    var local = _bay.InverseTransformPoint(_car.transform.position);
                    if (local.z > 15f || local.y < FloorY - 6f) { StartFall(); }
                    break;
                }
                case Stage.Falling:
                {
                    // the plane leaves
                    _planeSpeed = Mathf.MoveTowards(_planeSpeed, PlaneSpeed, 14f * dt);
                    _plane.transform.position += -_bay.forward * (_planeSpeed * dt);
                    if (now - _fallT0 > 60f) { Destroy(_plane); _plane = null; }

                    // landed?
                    if (now - _fallT0 > 3f && _car.State.isOnGround) Finish();
                    // fell through the world (colliders never appeared): fall back to the normal ground spawn
                    else if (_carAnchor != null && _carAnchor.longitudeLatitudeHeight.z < _startHeight - (Altitude + 450.0)) { Finish(); _car.Respawn(false); }
                    break;
                }
            }
        }

        void StartFall()
        {
            CurrentStage = Stage.Falling; _fallT0 = Time.unscaledTime; _planeSpeed = 0f;
            _car.SetStatus("");
            foreach (var c in _plane.GetComponentsInChildren<Collider>()) c.enabled = false;
            if (_car.colliderBudget != null) _car.colliderBudget.burst = true;       // build terrain colliders as fast as possible on the way down
        }

        void Finish()
        {
            CurrentStage = Stage.Done;
            if (_car != null)
            {
                _car.SetStatus("");
                if (_car.colliderBudget != null) _car.colliderBudget.burst = false;
            }
        }

        void Abort()
        {
            if (_plane != null) Destroy(_plane);
            _plane = null;
            Finish();
        }

        /// <summary>
        /// glTFast's shader-graph materials are stripped from WebGL builds (the meshes then render as nothing - same problem the car had).
        /// Swap them for URP Lit, which is always in the build, keeping the base texture. Double sided, opaque (even the glass).
        /// </summary>
        static void FixMaterials(GameObject model)
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

        static void SetLayer(GameObject go, int layer)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = FindDeep(t.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }
    }
}
