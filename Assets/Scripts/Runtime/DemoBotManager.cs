using System.Collections.Generic;
using RocketWorld.Sim;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Manages any number of AI-driven demolition bot cars. Each bot's only objective is to ram the player at
    /// supersonic speed — no ball, no goals. <see cref="AddBot"/> / <see cref="RemoveBot"/> add and remove one bot at a time,
    /// <see cref="RemoveAll"/> clears them, <see cref="Spawn"/> replaces everything with a single bot.
    ///
    /// Attach this to a persistent GameObject (GameRoot does it). Every bot has its own brain and respawn timer.
    ///
    /// Tick order: DemoBotManager.Update fills <see cref="RocketCar.netControls"/> of each bot from its brain, then
    /// <see cref="CarSimLoop"/> consumes them as usual (a bot car has <c>useNetControls = true</c>, exactly like a
    /// remote multiplayer car controlled by the host).
    /// </summary>
    public class DemoBotManager : MonoBehaviour
    {
        // ---- inspector knobs ---------------------------------------------------------------------------
        [Tooltip("How close a bot must be before it is considered a demolition hit (metres).")]
        public float hitRadius = 2.6f;
        [Tooltip("Minimum speed for a demolition hit (m/s). RocketSim supersonic ≈ 44 m/s.")]
        public float hitMinSpeed = 30f;
        [Tooltip("After a hit, seconds before the bot respawns and hunts again.")]
        public float respawnDelay = 3f;
        [Tooltip("Name shown over the bot cars (a number is appended).")]
        public string botName = "DEMO-BOT";
        [Tooltip("Color index of the first bot into GameSettings.CarColors; the following bots cycle through the other colors.")]
        public int botColorIndex = 4;
        [Tooltip("Upper limit of simultaneous bots (each one is a full simulated car + tile streaming).")]
        public int maxBots = 8;

        // ---- one bot -----------------------------------------------------------------------------------
        sealed class Bot
        {
            public RocketCar car;
            public readonly DemoBotBrain brain = new DemoBotBrain();
            public float respawnAt = -1f;
            public bool active;               // false while waiting to land after (re)placing
            public int number;
        }

        // ---- runtime refs, set by Init / Spawn ---------------------------------------------------------
        GameRoot _root;
        RocketCar _player;
        readonly List<Bot> _bots = new List<Bot>();
        DemoHitEffect _hitFx;
        float _spawnYawDeg;
        bool _yawInit;
        int _nextNumber = 1;

        // ---- clearance rays ----------------------------------------------------------------------------
        const float RayLen = DemoBotBrain.ClearRange;
        const float SpawnHeight     = 12f;   // metres above the player where a bot appears
        const float NearGroundMax   = 60f;   // player higher than this above the ground: bots spawn on the ground below instead
        const float SpawnHeightStep = 6f;    // extra height per bot slot so stacked bots do not collide
        const float RayY   = 0.35f;          // height above the car origin for horizontal obstacle rays
        static readonly Vector3[] RayDirs = {
            Quaternion.Euler(0f, -30f, 0f) * Vector3.forward,
            Vector3.forward,
            Quaternion.Euler(0f,  30f, 0f) * Vector3.forward,
        };

        // ---- public API --------------------------------------------------------------------------------

        public int Count => _bots.Count;
        public bool HasBot => _bots.Count > 0;
        public bool CanAdd => _bots.Count < Mathf.Max(1, maxBots);
        public int MaxBots => Mathf.Max(1, maxBots);

        /// <summary>True when at least one bot is on the ground and hunting.</summary>
        public bool IsActive { get { foreach (var b in _bots) if (b.active) return true; return false; } }

        /// <summary>Remove everything and start a single bot. Requires the game to be in the Running phase.</summary>
        public void Spawn(GameRoot root, RocketCar player)
        {
            RemoveAll();
            AddBot(root, player);
        }

        /// <summary>Add one more bot that hunts <paramref name="player"/>. Returns false at the bot limit.</summary>
        public bool AddBot(GameRoot root, RocketCar player)
        {
            if (root == null || player == null || !CanAdd) return false;
            _root = root;
            _player = player;
            if (!_yawInit)
            {
                _spawnYawDeg = (player.Body != null ? player.Body.rotation : player.transform.rotation).eulerAngles.y + 160f;
                _yawInit = true;
            }

            // Build through GameRoot so it gets a CesiumGlobeAnchor, tile streaming, water, plane…
            int n = _nextNumber++;
            int color = (botColorIndex + (n - 1)) % GameSettings.CarColors.Length;
            var car = root.BuildRemoteCar(color, botName + " " + n, true /* host-simulated */);
            root.TrackRemote(car.transform);

            if (_hitFx == null) _hitFx = gameObject.AddComponent<DemoHitEffect>();

            var bot = new Bot { car = car, number = n };
            _bots.Add(bot);
            PlaceNearPlayer(bot);
            return true;
        }

        /// <summary>Add one more bot using the player / root given earlier.</summary>
        public bool AddBot() => AddBot(_root, _player);

        /// <summary>Remove the most recently added bot.</summary>
        public void RemoveBot()
        {
            if (_bots.Count == 0) return;
            var b = _bots[_bots.Count - 1];
            _bots.RemoveAt(_bots.Count - 1);
            Kill(b);
            if (_bots.Count == 0) ResetCounters();
        }

        /// <summary>Destroy every bot and stop all activity.</summary>
        public void Remove() => RemoveAll();

        public void RemoveAll()
        {
            foreach (var b in _bots) Kill(b);
            _bots.Clear();
            ResetCounters();
        }

        void Kill(Bot b)
        {
            if (b != null && b.car != null && _root != null) _root.DestroyRemoteCar(b.car);
            if (b != null) b.car = null;
        }

        void ResetCounters() { _nextNumber = 1; _yawInit = false; }

        // ---- Unity loop ---------------------------------------------------------------------------------

        void Update()
        {
            if (_bots.Count == 0 || _root == null || _player == null) return;
            bool playerRunning = _player.CurrentPhase == RocketCar.Phase.Running;

            for (int i = _bots.Count - 1; i >= 0; i--)
            {
                var b = _bots[i];
                if (b.car == null) { _bots.RemoveAt(i); continue; }   // destroyed from elsewhere
                Tick(b, playerRunning);
            }
            if (_bots.Count == 0) ResetCounters();
        }

        void Tick(Bot b, bool playerRunning)
        {
            if (b.car.SoftHeld) return;     // frozen by the anti-fall guard until terrain is back under it

            // Wait for the bot to land on the ground.
            if (!b.active)
            {
                if (b.car.CurrentPhase == RocketCar.Phase.Running) b.active = true;
                return;
            }

            // Deferred respawn after a hit.
            if (b.respawnAt >= 0f)
            {
                if (Time.time < b.respawnAt) return;
                b.respawnAt = -1f;
                b.brain.Reset();
                PlaceNearPlayer(b);          // sets active = false: wait for ground again
                return;
            }

            // The brain can request a respawn after repeated stuck detection.
            if (b.brain.WantsRespawn) { ScheduleRespawn(b); return; }

            if (!playerRunning) return;

            // ---- Fill BotSense -----------------------------------------------------------------------
            var s = BuildSense(b);

            // ---- Demolition-hit check ----------------------------------------------------------------
            if (s.onGround || !s.hasJumped)
            {
                float dist2D = Mathf.Sqrt(s.tx * s.tx + s.tz * s.tz);
                float botSpeed = b.car.Body != null ? b.car.Body.linearVelocity.magnitude : b.car.SpeedMS;
                if (dist2D < hitRadius && botSpeed >= hitMinSpeed)
                {
                    OnHit(b, botSpeed);
                    return;
                }
            }

            // ---- Think -------------------------------------------------------------------------------
            var c = b.brain.Think(s);

            // Translate CarControls (Sim) -> CarInput.Controls (Runtime).
            b.car.netControls = new CarInput.Controls
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

        BotSense BuildSense(Bot b)
        {
            var bot = b.car;
            Transform bt = bot.transform, pt = _player.transform;

            // Bot's world rotation and its local-to-world matrix columns.
            Quaternion bRot = bot.Body != null ? bot.Body.rotation : bt.rotation;
            Vector3 bRight   = bRot * Vector3.right;
            Vector3 bUp      = bRot * Vector3.up;
            Vector3 bForward = bRot * Vector3.forward;
            Vector3 worldUp  = Vector3.up;

            // Bot and player world positions.
            Vector3 bPos = bot.Body != null ? bot.Body.position : bt.position;
            Vector3 pPos = _player.Body != null ? _player.Body.position : pt.position;

            // Target relative position in the bot's local frame.
            Vector3 rel = pPos - bPos;
            float tx = Vector3.Dot(rel, bRight);
            float ty = Vector3.Dot(rel, bUp);
            float tz = Vector3.Dot(rel, bForward);

            // Velocities.
            Vector3 bVel = bot.Body != null ? bot.Body.linearVelocity : bot.UnityVelocity;
            Vector3 pVel = _player.Body != null ? _player.Body.linearVelocity : _player.UnityVelocity;
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
            float c0, c1, c2;
            c0 = Clear(rayOrigin, bRot, RayDirs[0], bForward, mask);
            c1 = Clear(rayOrigin, bRot, RayDirs[1], bForward, mask);
            c2 = Clear(rayOrigin, bRot, RayDirs[2], bForward, mask);

            var bState = bot.State;
            return new BotSense
            {
                tx = tx, ty = ty, tz = tz,
                tvx = tvx, tvy = tvy, tvz = tvz,
                vx = vx, vy = vy, vz = vz,
                ux = ux, uy = uy, uz = uz,
                onGround        = bState.isOnGround,
                hasJumped       = bState.hasJumped,
                hasDoubleJumped = bState.hasDoubleJumped,
                supersonic      = bState.isSupersonic,
                clearL = c0,
                clearC = c1,
                clearR = c2,
                dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f),
            };
        }

        static float Clear(Vector3 origin, Quaternion rot, Vector3 localDir, Vector3 fallbackForward, int mask)
        {
            Vector3 dir = rot * localDir;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = fallbackForward;
            dir.Normalize();
            return Physics.Raycast(origin, dir, out var hit, RayLen, mask, QueryTriggerInteraction.Ignore) ? hit.distance : RayLen;
        }

        void PlaceNearPlayer(Bot b)
        {
            if (b.car == null || _player == null) return;

            Vector3 playerPos = _player.Body != null ? _player.Body.position : _player.transform.position;
            Quaternion rot = Quaternion.Euler(0f, _spawnYawDeg, 0f);
            _spawnYawDeg += 67f;
            b.active = false;               // wait for the ground

            // Is there terrain collider under the player? Without it a bot placed in the air would fall out of the map.
            bool haveGround = Physics.Raycast(playerPos + Vector3.up, Vector3.down, out var gh, 5000f,
                PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore);
            if (!haveGround)
            {
                // Built-in "appear on the ground below the host" logic: the bot waits (frozen, no falling) until terrain exists.
                b.car.SetSpawnColumn(playerPos, true, rot.eulerAngles.y);
                return;
            }

            Vector3 pPos;
            if (playerPos.y - gh.point.y > NearGroundMax)
                pPos = gh.point + Vector3.up * 0.4f;                          // player is flying/hovering: bot lands on the ground below
            else
                pPos = playerPos + Vector3.up * (SpawnHeight + (b.number % 4) * SpawnHeightStep);   // directly above the player

            b.car.PlaceDirect(pPos, rot);
            b.car.infiniteBoost = true;
        }

        void ScheduleRespawn(Bot b)
        {
            b.respawnAt = Time.time + respawnDelay;
            b.car.netControls = new CarInput.Controls();
        }

        void OnHit(Bot b, float speed)
        {
            if (_hitFx != null) _hitFx.Play(_player.transform.position, speed);
            ScheduleRespawn(b);
            Debug.Log($"[DemoBot] #{b.number} DEMOLITION hit at {speed * 3.6f:0} km/h!");
        }

        void OnDestroy() { RemoveAll(); }
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
            m.startColor  = new ParticleSystem.MinMaxGradient(new Color(1f, 0.8f, 0.4f, 0.9f),
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
