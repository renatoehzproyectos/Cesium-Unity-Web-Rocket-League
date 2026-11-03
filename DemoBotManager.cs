using RocketWorld.Sim;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Manages one AI-driven demolition bot car. The only objective of the bot is to ram the player at supersonic
    /// speed — no ball, no goals. A second call to <see cref="Spawn"/> replaces a running bot in-place.
    ///
    /// Attach this to a persistent GameObject (GameRoot is fine). Call <see cref="Spawn"/> to start the bot and
    /// <see cref="Remove"/> to destroy it.
    ///
    /// Tick order: DemoBotManager.Update fills <see cref="RocketCar.netControls"/> each frame based on the bot brain,
    /// then <see cref="CarSimLoop"/> consumes them as usual (the bot car has <c>useNetControls = true</c>, exactly
    /// like a remote multiplayer car controlled by the host).
    /// </summary>
    public class DemoBotManager : MonoBehaviour
    {
        // ---- inspector knobs ---------------------------------------------------------------------------
        [Tooltip("How close the bot must be before it is considered a demolition hit (metres).")]
        public float hitRadius = 2.6f;
        [Tooltip("Minimum speed for a demolition hit (m/s). RocketSim supersonic ≈ 44 m/s.")]
        public float hitMinSpeed = 30f;
        [Tooltip("After a hit, seconds before the bot respawns and hunts again.")]
        public float respawnDelay = 3f;
        [Tooltip("Name shown over the bot car.")]
        public string botName = "DEMO-BOT";
        [Tooltip("Color index into GameSettings.CarColors (0=Orange … 5=Green). Black by default.")]
        public int botColorIndex = 4;

        // ---- runtime refs, set by Spawn ----------------------------------------------------------------
        GameRoot _root;
        RocketCar _bot;
        RocketCar _player;
        DemoBotBrain _brain = new DemoBotBrain();

        // ---- state -------------------------------------------------------------------------------------
        float _respawnAt = -1f;
        bool _spawning;
        bool _active;
        DemoHitEffect _hitFx;
        float _spawnYawDeg;

        // ---- clearance rays ----------------------------------------------------------------------------
        const float RayLen = DemoBotBrain.ClearRange;
        const float RayY   = 0.35f;          // height above the car origin for horizontal obstacle rays
        static readonly Vector3[] RayDirs = {
            Quaternion.Euler(0f, -30f, 0f) * Vector3.forward,
            Vector3.forward,
            Quaternion.Euler(0f,  30f, 0f) * Vector3.forward,
        };

        // ---- public API --------------------------------------------------------------------------------

        /// <summary>
        /// Start / replace the bot. Requires the game to be in the Running phase.
        /// <paramref name="root"/> is the GameRoot; <paramref name="player"/> is the car to hunt.
        /// </summary>
        public void Spawn(GameRoot root, RocketCar player)
        {
            _root = root;
            _player = player;
            _brain.Reset();
            _respawnAt = -1f;
            _active = false;
            _spawning = false;

            // Destroy any previous bot.
            if (_bot != null) { root.DestroyRemoteCar(_bot); _bot = null; }

            // Spawn offset from the player (behind + to the side, some metres away).
            _spawnYawDeg = player != null
                ? (player.Body?.rotation ?? player.transform.rotation).eulerAngles.y + 160f
                : 0f;

            // Build through GameRoot so it gets a CesiumGlobeAnchor, tile streaming, water, plane…
            _bot = root.BuildRemoteCar(botColorIndex, botName, true /* host-simulated */);
            root.TrackRemote(_bot.transform);

            if (_hitFx == null) _hitFx = gameObject.AddComponent<DemoHitEffect>();

            // Place it near the player.
            DoSpawnPlace();
        }

        /// <summary>Destroy the bot and stop all activity.</summary>
        public void Remove()
        {
            if (_bot != null && _root != null) { _root.DestroyRemoteCar(_bot); }
            _bot = null; _brain.Reset(); _active = false; _spawning = false; _respawnAt = -1f;
        }

        public bool IsActive => _active;
        public bool HasBot   => _bot != null;

        // ---- Unity loop ---------------------------------------------------------------------------------

        void Update()
        {
            if (_bot == null || _root == null || _player == null) return;

            // Wait for bot to land on the ground.
            if (!_active)
            {
                if (_bot.CurrentPhase == RocketCar.Phase.Running) _active = true;
                return;
            }

            // Deferred respawn after a hit.
            if (_respawnAt >= 0f)
            {
                if (Time.time < _respawnAt) return;
                _respawnAt = -1f;
                _brain.Reset();
                DoSpawnPlace();
                _active = false;       // wait for ground again
                return;
            }

            // Brain can request a respawn after repeated stuck detection.
            if (_brain.WantsRespawn && _respawnAt < 0f) { ScheduleRespawn(); return; }

            if (_player.CurrentPhase != RocketCar.Phase.Running) return;

            // ---- Fill BotSense -----------------------------------------------------------------------
            var s = BuildSense();

            // ---- Demolition-hit check ----------------------------------------------------------------
            if (s.onGround || !s.hasJumped)
            {
                float dist2D = Mathf.Sqrt(s.tx * s.tx + s.tz * s.tz);
                float botSpeed = _bot.Body?.velocity.magnitude ?? _bot.SpeedMS;
                if (dist2D < hitRadius && botSpeed >= hitMinSpeed)
                {
                    OnHit(botSpeed);
                    return;
                }
            }

            // ---- Think -------------------------------------------------------------------------------
            var c = _brain.Think(s);

            // Translate CarControls (Sim) -> CarInput.Controls (Runtime).
            _bot.netControls = new CarInput.Controls
            {
                throttle = c.throttle,
                steer    = c.steer,
                pitch    = c.pitch,
                yaw      = c.yaw,
                roll     = c.roll,
                jump     = c.jump,
                boost    = c.boost,
                handbrake= c.handbrake,
            };
        }

        // ---- helpers ------------------------------------------------------------------------------------

        BotSense BuildSense()
        {
            Transform bt = _bot.transform, pt = _player.transform;

            // Bot's world rotation and its local-to-world matrix columns.
            Quaternion bRot = _bot.Body?.rotation ?? bt.rotation;
            Vector3 bRight   = bRot * Vector3.right;
            Vector3 bUp      = bRot * Vector3.up;
            Vector3 bForward = bRot * Vector3.forward;
            Vector3 worldUp  = Vector3.up;

            // Bot and player world positions.
            Vector3 bPos = _bot.Body?.position ?? bt.position;
            Vector3 pPos = _player.Body?.position ?? pt.position;

            // Target relative position in the bot's local frame.
            Vector3 rel = pPos - bPos;
            float tx = Vector3.Dot(rel, bRight);
            float ty = Vector3.Dot(rel, bUp);
            float tz = Vector3.Dot(rel, bForward);

            // Velocities.
            Vector3 bVel = _bot.Body?.velocity  ?? _bot.UnityVelocity;
            Vector3 pVel = _player.Body?.velocity ?? _player.UnityVelocity;
            float vx = Vector3.Dot(bVel, bRight);
            float vy = Vector3.Dot(bVel, bUp);
            float vz = Vector3.Dot(bVel, bForward);
            float tvx = Vector3.Dot(pVel, bRight);
            float tvy = Vector3.Dot(pVel, bUp);
            float tvz = Vector3.Dot(pVel, bForward);

            // World up in the bot's frame (tells the brain whether it is upright).
            float ux = Vector3.Dot(worldUp, bRight);
            float uy = Vector3.Dot(worldUp, bUp);
            float uz = Vector3.Dot(worldUp, bForward);

            // Three clearance rays.
            Vector3 rayOrigin = bPos + Vector3.up * RayY;
            int mask = PhysicsSetup.TerrainMask & ~(1 << PhysicsSetup.CarLayer);
            float[] clears = new float[3];
            for (int i = 0; i < 3; i++)
            {
                Vector3 dir = bRot * RayDirs[i];
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.01f) dir = bForward;
                dir.Normalize();
                clears[i] = Physics.Raycast(rayOrigin, dir, out var hit, RayLen, mask, QueryTriggerInteraction.Ignore)
                    ? hit.distance : RayLen;
            }

            var bState = _bot.State;
            return new BotSense
            {
                tx = tx, ty = ty, tz = tz,
                tvx = tvx, tvy = tvy, tvz = tvz,
                vx = vx, vy = vy, vz = vz,
                ux = ux, uy = uy, uz = uz,
                onGround       = bState.isOnGround,
                hasJumped      = bState.hasJumped,
                hasDoubleJumped= bState.hasDoubleJumped,
                supersonic     = bState.isSupersonic,
                clearL = clears[0],
                clearC = clears[1],
                clearR = clears[2],
                dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f),
            };
        }

        void DoSpawnPlace()
        {
            if (_bot == null || _player == null) return;

            // Spawn 60–120 m behind and a little beside the player.
            float yaw    = _spawnYawDeg * Mathf.Deg2Rad;
            float radius = 70f;
            Vector3 offset = new Vector3(Mathf.Sin(yaw) * radius, 0f, Mathf.Cos(yaw) * radius);
            Vector3 pPos = (_player.Body?.position ?? _player.transform.position) + offset;

            // Find the ground below.
            Vector3 origin = pPos + Vector3.up * 2500f;
            if (Physics.Raycast(origin, Vector3.down, out var hit, 5000f, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore))
                pPos = hit.point + Vector3.up * 0.4f;

            // Face the player.
            Vector3 playerPos = _player.Body?.position ?? _player.transform.position;
            Vector3 toPlayer = playerPos - pPos;
            Quaternion rot = toPlayer.sqrMagnitude > 0.1f
                ? Quaternion.LookRotation(new Vector3(toPlayer.x, 0f, toPlayer.z).normalized, Vector3.up)
                : Quaternion.identity;

            _bot.PlaceDirect(pPos, rot);
            _bot.infiniteBoost = true;

            // Rotate next spawn so the bot does not always come from the same side.
            _spawnYawDeg += 67f;
        }

        void ScheduleRespawn()
        {
            _respawnAt = Time.time + respawnDelay;
            _bot.netControls = new CarInput.Controls();
        }

        void OnHit(float speed)
        {
            if (_hitFx != null) _hitFx.Play(_player.transform.position, speed);
            // Bump the player visually (the physics engine already resolved the collision).
            ScheduleRespawn();
            Debug.Log($"[DemoBot] DEMOLITION hit at {speed * 3.6f:0} km/h!");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Simple shockwave + flash effect played at the impact point.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One-shot shockwave ring + camera-flash on demolition impact.
    /// Entirely procedural: no assets required (a white additive quad is enough).
    /// </summary>
    public sealed class DemoHitEffect : MonoBehaviour
    {
        Light _flash;
        ParticleSystem _ring;
        bool _ready;

        void Awake() { Build(); }

        void Build()
        {
            // Flash light
            var lg  = new GameObject("DemoFlash");
            lg.transform.SetParent(transform, false);
            _flash  = lg.AddComponent<Light>();
            _flash.type = LightType.Point;
            _flash.range = 80f;
            _flash.color = new Color(1f, 0.85f, 0.55f);
            _flash.shadows = LightShadows.None;
            _flash.intensity = 0f;
            _flash.enabled = false;

            // Shockwave ring (very cheap: one particle in a quad-ring shape, scale over life)
            var rg  = new GameObject("DemoRing");
            rg.transform.SetParent(transform, false);
            _ring   = rg.AddComponent<ParticleSystem>();
            _ring.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var m = _ring.main;
            m.loop = false; m.playOnAwake = false; m.duration = 0.9f;
            m.startLifetime = 0.9f; m.startSpeed = 0f;
            m.startSize = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 1f, 0f, 30f), new Keyframe(1f, 30f, 0f, 0f)));
            m.startColor  = new ParticleSystem.MinMaxCurve(new Color(1f, 0.8f, 0.4f, 0.9f),
                                                             new Color(1f, 1f,   0.7f, 0.7f));
            m.gravityModifier = 0f;
            m.maxParticles    = 4;
            m.simulationSpace = ParticleSystemSimulationSpace.World;

            var em = _ring.emission; em.rateOverTime = 0f;

            var col = _ring.colorOverLifetime; col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.9f, 0.5f), 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0.85f, 0f), new GradientAlphaKey(0f, 1f) });
            col.color = g;

            var sol = _ring.sizeOverLifetime; sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.05f, 1f, 1f));

            var ren = rg.GetComponent<ParticleSystemRenderer>();
            ren.renderMode   = ParticleSystemRenderMode.Billboard;
            ren.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ren.receiveShadows    = false;

            // Try to grab the ball spark material (additive white) for the ring; graceful fallback to the default.
            var mat = Resources.Load<Material>("Materials/ParticleAdd");
            if (mat != null) ren.sharedMaterial = new Material(mat) { name = "DemoRingMat" };

            _ready = true;
        }

        float _flashT, _flashPeak;

        public void Play(Vector3 pos, float speed)
        {
            if (!_ready) return;
            float strength = Mathf.Clamp01((speed - 30f) / 30f);   // 0 at 30 m/s, 1 at 60 m/s

            // Flash
            _flash.transform.position = pos;
            _flashPeak = 2.5f + strength * 6f;
            _flashT = 0.35f;
            _flash.enabled = true;
            _flash.intensity = _flashPeak;

            // Ring burst: 2–4 particles depending on strength
            var ep = new ParticleSystem.EmitParams { position = pos, velocity = Vector3.zero,
                startLifetime = 0.9f, startSize = 0.8f + strength * 1.2f,
                startColor = Color.Lerp(new Color(1f, 0.7f, 0.3f), Color.white, strength) };
            _ring.transform.position = pos;
            _ring.Emit(ep, 2 + Mathf.RoundToInt(strength * 2f));
        }

        void Update()
        {
            if (_flashT <= 0f) return;
            _flashT -= Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(_flashT / 0.35f);
            _flash.intensity = _flashPeak * k * k;
            if (_flashT <= 0f) _flash.enabled = false;
        }
    }
}
