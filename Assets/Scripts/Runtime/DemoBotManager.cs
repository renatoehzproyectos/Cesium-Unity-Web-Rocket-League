using System.Collections.Generic;
using RocketWorld.Sim;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Manages up to <see cref="maxBots"/> AI demolition bots. Each bot drives one of the game's vehicles (car, Cessna, F-35B, F-15E,
    /// helicopter, DeLorean; they cycle through them) and tries to ram the player. Cars use <see cref="DemoBotBrain"/>, everything that
    /// flies uses <see cref="FlightBotBrain"/>. Bots avoid terrain / buildings with look-ahead rays and avoid each other.
    ///
    /// Tick order: DemoBotManager.Update fills <see cref="RocketCar.netControls"/> of each bot, then <see cref="CarSimLoop"/> consumes
    /// them (a bot car has <c>useNetControls = true</c>, like a remote multiplayer car).
    /// </summary>
    public class DemoBotManager : MonoBehaviour
    {
        [Tooltip("Hit radius for a car (metres); other vehicles use their own, see HitRadiusOf.")]
        public float hitRadius = 2.6f;
        [Tooltip("Minimum speed for a car demolition hit (m/s). RocketSim supersonic is about 44 m/s.")]
        public float hitMinSpeed = 30f;
        [Tooltip("After a hit, seconds before the bot respawns and hunts again.")]
        public float respawnDelay = 3f;
        public string botName = "DEMO-BOT";
        public int botColorIndex = 4;
        [Tooltip("Upper limit of simultaneous bots (each one is a full simulated car).")]
        public int maxBots = 50;
        [Tooltip("Bots cycle through every vehicle in the game. Off = cars only.")]
        public bool allVehicles = true;
        [Tooltip("Only the first N bots stream terrain tiles around themselves; the rest use the tiles around the player.")]
        public int streamingBots = 12;
        [Tooltip("A bot farther than this from the player is respawned near it (metres).")]
        public float lostDistance = 2500f;

        sealed class Bot
        {
            public RocketCar car;
            public VehicleKind kind;
            public DemoBotBrain brain = new DemoBotBrain();
            public FlightBotBrain flight;
            public float respawnAt = -1f;
            public bool active, entered;
            public int number;
            // sensing cache (rays are refreshed every few frames)
            public float cL, cC, cR, cLL, cRR, fF, fL, fR, fU, fD, fUp, agl = 400f;
            public Vector3 sep;
            public bool Flies => kind != VehicleKind.Car;
        }

        GameRoot _root;
        RocketCar _player;
        readonly List<Bot> _bots = new List<Bot>();
        DemoHitEffect _hitFx;
        float _spawnYawDeg;
        bool _yawInit;
        int _nextNumber = 1;

        const float RayLen = DemoBotBrain.ClearRange;
        const float SpawnHeight = 12f;
        const float NearGroundMax = 60f;
        const float SpawnHeightStep = 6f;
        const float RayY = 0.35f;
        static readonly Vector3[] RayDirs = {
            Quaternion.Euler(0f, -30f, 0f) * Vector3.forward,
            Vector3.forward,
            Quaternion.Euler(0f,  30f, 0f) * Vector3.forward,
            Quaternion.Euler(0f, -60f, 0f) * Vector3.forward,
            Quaternion.Euler(0f,  60f, 0f) * Vector3.forward,
        };

        public int Count => _bots.Count;
        public bool HasBot => _bots.Count > 0;
        public bool CanAdd => _bots.Count < Mathf.Max(1, maxBots);
        public int MaxBots => Mathf.Max(1, maxBots);
        public bool IsActive { get { foreach (var b in _bots) if (b.active) return true; return false; } }

        public void Spawn(GameRoot root, RocketCar player) { RemoveAll(); AddBot(root, player); }

        public bool AddBot(GameRoot root, RocketCar player)
        {
            if (root == null || player == null || !CanAdd) return false;
            _root = root; _player = player;
            if (!_yawInit)
            {
                _spawnYawDeg = (player.Body != null ? player.Body.rotation : player.transform.rotation).eulerAngles.y + 160f;
                _yawInit = true;
            }

            int n = _nextNumber++;
            int color = (botColorIndex + (n - 1)) % GameSettings.CarColors.Length;
            var car = root.BuildRemoteCar(color, botName + " " + n, true);
            if (n <= Mathf.Max(0, streamingBots)) root.TrackRemote(car.transform);
            if (_hitFx == null) _hitFx = gameObject.AddComponent<DemoHitEffect>();

            var bot = new Bot { car = car, number = n };
            bot.kind = allVehicles ? Vehicles.Order[(n - 1) % Vehicles.Order.Length] : VehicleKind.Car;
            if (bot.Flies) bot.flight = new FlightBotBrain(ProfileOf(bot.kind));
            _bots.Add(bot);
            PlaceNearPlayer(bot);
            return true;
        }

        public bool AddBot() => AddBot(_root, _player);

        public void RemoveBot()
        {
            if (_bots.Count == 0) return;
            var b = _bots[_bots.Count - 1];
            _bots.RemoveAt(_bots.Count - 1);
            Kill(b);
            if (_bots.Count == 0) ResetCounters();
        }

        public void Remove() => RemoveAll();

        public void RemoveAll()
        {
            foreach (var b in _bots) Kill(b);
            _bots.Clear();
            ResetCounters();
        }

        void Kill(Bot b)
        {
            if (b != null && b.car != null && _root != null) { _root.UntrackRemote(b.car.transform); _root.DestroyRemoteCar(b.car); }
            if (b != null) b.car = null;
        }

        void ResetCounters() { _nextNumber = 1; _yawInit = false; }

        // ---- vehicle data --------------------------------------------------------------------------------

        static FlightProfile ProfileOf(VehicleKind k)
        {
            switch (k)
            {
                case VehicleKind.Cessna:
                    return new FlightProfile { style = FlightStyle.FixedWing, minSpeed = 28f, cruiseSpeed = 55f, maxSpeed = 75f, reach = 6f, cruiseAgl = 60f };
                case VehicleKind.F35B:
                case VehicleKind.F15E:
                {
                    float v = Vehicles.Get(k).vMax;
                    return new FlightProfile { style = FlightStyle.FixedWing, minSpeed = v * 0.22f, cruiseSpeed = v * 0.42f, maxSpeed = v * 0.75f, reach = 7f, cruiseAgl = 120f };
                }
                case VehicleKind.Helicopter:
                    return new FlightProfile { style = FlightStyle.Rotor, minSpeed = 0f, cruiseSpeed = 30f, maxSpeed = 35f, reach = 5.5f, cruiseAgl = 25f };
                default: // DeLorean hovering
                    return new FlightProfile { style = FlightStyle.Hover, minSpeed = 0f, cruiseSpeed = 40f, maxSpeed = 55f, reach = 3.2f, cruiseAgl = 8f };
            }
        }

        float HitRadiusOf(VehicleKind k)
        {
            switch (k)
            {
                case VehicleKind.Car: return hitRadius;
                case VehicleKind.DeLorean: return hitRadius + 0.8f;
                case VehicleKind.Helicopter: return 5.5f;
                case VehicleKind.Cessna: return 6f;
                default: return 7f;
            }
        }

        float HitMinSpeedOf(VehicleKind k)
        {
            switch (k)
            {
                case VehicleKind.Car: return hitMinSpeed;
                case VehicleKind.DeLorean: return 25f;
                case VehicleKind.Helicopter: return 14f;
                case VehicleKind.Cessna: return 22f;
                default: return 40f;
            }
        }

        float AirSpawnAgl(VehicleKind k)
        {
            switch (k)
            {
                case VehicleKind.Cessna: return 70f;
                case VehicleKind.F35B: case VehicleKind.F15E: return 130f;
                case VehicleKind.Helicopter: return 45f;
                default: return 20f;
            }
        }

        /// <summary>Switch the bot's car into its vehicle (once, right after it was placed).</summary>
        void EnterVehicle(Bot b)
        {
            if (b.entered || b.car == null) return;
            b.entered = true;
            if (b.kind == VehicleKind.Car || b.car.vehicle == null) return;
            b.car.infiniteBoost = true;
            b.car.vehicle.Select(b.kind);
        }

        // ---- Unity loop ----------------------------------------------------------------------------------

        void Update()
        {
            if (_bots.Count == 0 || _root == null || _player == null) return;
            bool playerRunning = _player.CurrentPhase == RocketCar.Phase.Running;

            for (int i = _bots.Count - 1; i >= 0; i--)
                if (_bots[i].car == null) _bots.RemoveAt(i);
            if (_bots.Count == 0) { ResetCounters(); return; }

            ComputeSeparation();
            for (int i = _bots.Count - 1; i >= 0; i--) Tick(_bots[i], playerRunning);
        }

        static Vector3 PosOf(RocketCar c) => c.Body != null ? c.Body.position : c.transform.position;

        /// <summary>Per bot: a world vector pointing away from the neighbours that are too close (length &lt;= 1).</summary>
        void ComputeSeparation()
        {
            int n = _bots.Count;
            for (int i = 0; i < n; i++)
            {
                var a = _bots[i]; a.sep = Vector3.zero;
                if (a.car == null || !a.active) continue;
                Vector3 pa = PosOf(a.car);
                float speed = a.car.Body != null ? a.car.Body.linearVelocity.magnitude : 0f;
                float R = a.Flies && a.kind != VehicleKind.DeLorean ? 25f + speed * 0.5f : 7f + speed * 0.12f;
                Vector3 acc = Vector3.zero;
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    var o = _bots[j]; if (o.car == null || !o.active) continue;
                    Vector3 d = pa - PosOf(o.car);
                    float m = d.magnitude;
                    if (m < R && m > 0.01f) acc += d / m * (1f - m / R);
                }
                a.sep = Vector3.ClampMagnitude(acc, 1f);
            }
        }

        void Tick(Bot b, bool playerRunning)
        {
            if (b.car == null || b.car.SoftHeld) return;

            if (!b.active)
            {
                if (b.car.CurrentPhase == RocketCar.Phase.Running)
                {
                    b.active = true;
                    if (!b.entered && b.Flies)
                    {
                        // Waited for the ground (no terrain under the player yet): lift off into the air first.
                        Vector3 p = PosOf(b.car);
                        b.car.PlaceDirect(p + Vector3.up * AirSpawnAgl(b.kind), Quaternion.LookRotation(FlatToPlayer(p), Vector3.up));
                    }
                    EnterVehicle(b);
                }
                return;
            }

            if (b.respawnAt >= 0f)
            {
                if (Time.time < b.respawnAt) return;
                b.respawnAt = -1f;
                b.brain.Reset(); if (b.flight != null) b.flight.Reset();
                PlaceNearPlayer(b);
                return;
            }

            bool wants = b.flight != null ? b.flight.WantsRespawn : b.brain.WantsRespawn;
            if (!wants && Vector3.Distance(PosOf(b.car), PosOf(_player)) > lostDistance) wants = true;
            if (wants) { ScheduleRespawn(b); return; }

            if (!playerRunning) return;

            if (b.flight != null) TickFlight(b); else TickGround(b);
        }

        // ---- ground / car --------------------------------------------------------------------------------

        void TickGround(Bot b)
        {
            var s = BuildSense(b);

            if (s.onGround || !s.hasJumped)
            {
                float dist2D = Mathf.Sqrt(s.tx * s.tx + s.tz * s.tz);
                float botSpeed = b.car.Body != null ? b.car.Body.linearVelocity.magnitude : b.car.SpeedMS;
                if (dist2D < HitRadiusOf(b.kind) && botSpeed >= HitMinSpeedOf(b.kind)) { OnHit(b, botSpeed); return; }
            }

            var c = b.brain.Think(s);
            b.car.netControls = new CarInput.Controls
            {
                throttle = c.throttle, steer = c.steer, pitch = c.pitch, yaw = c.yaw, roll = c.roll,
                jump = c.jump, boost = c.boost, handbrake = c.handbrake,
            };
        }

        bool RaysDue(Bot b, int every) => every <= 1 || (Time.frameCount + b.number) % every == 0;

        BotSense BuildSense(Bot b)
        {
            var bot = b.car;
            Transform bt = bot.transform, pt = _player.transform;
            Quaternion bRot = bot.Body != null ? bot.Body.rotation : bt.rotation;
            Vector3 bRight = bRot * Vector3.right, bUp = bRot * Vector3.up, bForward = bRot * Vector3.forward;
            Vector3 bPos = bot.Body != null ? bot.Body.position : bt.position;
            Vector3 pPos = _player.Body != null ? _player.Body.position : pt.position;

            Vector3 rel = pPos - bPos;
            Vector3 bVel = bot.Body != null ? bot.Body.linearVelocity : bot.UnityVelocity;
            Vector3 pVel = _player.Body != null ? _player.Body.linearVelocity : _player.UnityVelocity;
            Vector3 wUp = Vector3.up;

            int every = _bots.Count > 16 ? 3 : (_bots.Count > 6 ? 2 : 1);
            if (RaysDue(b, every))
            {
                Vector3 o = bPos + Vector3.up * RayY;
                int mask = PhysicsSetup.TerrainMask & ~(1 << PhysicsSetup.CarLayer);
                b.cL = Clear(o, bRot, RayDirs[0], bForward, mask, RayLen);
                b.cC = Clear(o, bRot, RayDirs[1], bForward, mask, RayLen);
                b.cR = Clear(o, bRot, RayDirs[2], bForward, mask, RayLen);
                b.cLL = Clear(o, bRot, RayDirs[3], bForward, mask, RayLen);
                b.cRR = Clear(o, bRot, RayDirs[4], bForward, mask, RayLen);
            }

            var st = bot.State;
            return new BotSense
            {
                tx = Vector3.Dot(rel, bRight), ty = Vector3.Dot(rel, bUp), tz = Vector3.Dot(rel, bForward),
                tvx = Vector3.Dot(pVel, bRight), tvy = Vector3.Dot(pVel, bUp), tvz = Vector3.Dot(pVel, bForward),
                vx = Vector3.Dot(bVel, bRight), vy = Vector3.Dot(bVel, bUp), vz = Vector3.Dot(bVel, bForward),
                ux = Vector3.Dot(wUp, bRight), uy = Vector3.Dot(wUp, bUp), uz = Vector3.Dot(wUp, bForward),
                onGround = st.isOnGround, hasJumped = st.hasJumped, hasDoubleJumped = st.hasDoubleJumped, supersonic = st.isSupersonic,
                clearL = b.cL, clearC = b.cC, clearR = b.cR,
                hasWide = true, clearLL = b.cLL, clearRR = b.cRR,
                sx = Vector3.Dot(b.sep, bRight), sy = Vector3.Dot(b.sep, bUp), sz = Vector3.Dot(b.sep, bForward),
                dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f),
            };
        }

        static float Clear(Vector3 origin, Quaternion rot, Vector3 localDir, Vector3 fallbackForward, int mask, float len)
        {
            Vector3 dir = rot * localDir;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = fallbackForward;
            dir.Normalize();
            return Physics.Raycast(origin, dir, out var hit, len, mask, QueryTriggerInteraction.Ignore) ? hit.distance : len;
        }

        // ---- aircraft / helicopter / hover car -----------------------------------------------------------

        void TickFlight(Bot b)
        {
            var car = b.car;
            if (!car.InVehicle && !car.InPlane)
            {
                // Not in its vehicle (it was reset): enter again.
                b.entered = false; EnterVehicle(b);
                return;
            }
            var rb = car.Body; if (rb == null) return;

            Quaternion rot = rb.rotation;
            Vector3 right = rot * Vector3.right, up = rot * Vector3.up, fwd = rot * Vector3.forward;
            Vector3 pos = rb.position, vel = rb.linearVelocity;
            Vector3 pPos = PosOf(_player);
            Vector3 pVel = _player.Body != null ? _player.Body.linearVelocity : _player.UnityVelocity;
            Vector3 rel = pPos - pos;
            float speed = vel.magnitude;

            // Demolition hit: 3-D distance, radius grows with the speed so a fast flyer cannot skip over the target between two frames.
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (rel.magnitude < HitRadiusOf(b.kind) + speed * dt * 0.6f && speed >= HitMinSpeedOf(b.kind)) { OnHit(b, speed); return; }

            var prof = b.flight.Profile;
            float maxRay = Mathf.Clamp(speed * 3f, 80f, 600f);
            int every = _bots.Count > 16 ? 3 : (_bots.Count > 6 ? 2 : 1);
            if (RaysDue(b, every))
            {
                int mask = PhysicsSetup.TerrainMask;
                Vector3 dirV = speed > 12f ? vel / speed : fwd;
                Vector3 flatRight = Vector3.Cross(Vector3.up, dirV);
                if (flatRight.sqrMagnitude < 1e-4f) flatRight = right;
                flatRight.Normalize();
                float rf = prof.style == FlightStyle.FixedWing ? maxRay : Mathf.Clamp(speed * 3f + 20f, 25f, 90f);
                b.fF = Ray(pos, dirV, rf, mask);
                b.fL = Ray(pos, Quaternion.AngleAxis(-28f, Vector3.up) * dirV, rf, mask);
                b.fR = Ray(pos, Quaternion.AngleAxis(28f, Vector3.up) * dirV, rf, mask);
                b.fU = Ray(pos, Quaternion.AngleAxis(-22f, flatRight) * dirV, rf, mask);
                b.fD = Ray(pos, Quaternion.AngleAxis(22f, flatRight) * dirV, rf, mask);
                b.fUp = Ray(pos, Vector3.up, 120f, mask);
                b.agl = Ray(pos, Vector3.down, 400f, mask);
                maxRay = rf;
            }
            else maxRay = prof.style == FlightStyle.FixedWing ? maxRay : Mathf.Clamp(speed * 3f + 20f, 25f, 90f);

            // Body rates in JSBSim convention (p roll right, q pitch up, r yaw right) = PlaneModel.RatesToBody.
            Vector3 w = Quaternion.Inverse(rot) * rb.angularVelocity;

            var s = new FlightSense
            {
                tx = Vector3.Dot(rel, right), ty = Vector3.Dot(rel, up), tz = Vector3.Dot(rel, fwd),
                tvx = Vector3.Dot(pVel, right), tvy = Vector3.Dot(pVel, up), tvz = Vector3.Dot(pVel, fwd),
                vx = Vector3.Dot(vel, right), vy = Vector3.Dot(vel, up), vz = Vector3.Dot(vel, fwd),
                ux = right.y, uy = up.y, uz = fwd.y,
                p = -w.z, q = -w.x, r = w.y,
                agl = b.agl,
                clearF = b.fF, clearL = b.fL, clearR = b.fR, clearU = b.fU, clearD = b.fD, clearUp = b.fUp,
                maxRay = maxRay,
                sx = Vector3.Dot(b.sep, right), sy = Vector3.Dot(b.sep, up), sz = Vector3.Dot(b.sep, fwd),
                onGround = car.vehicle != null && car.vehicle.Active ? car.vehicle.OnGround : (car.plane != null && car.plane.OnGround),
                dt = dt,
            };
            var o = b.flight.Think(s);

            var ctl = new CarInput.Controls();
            switch (prof.style)
            {
                case FlightStyle.FixedWing:
                    // JSBSim: elevator command + = stick forward = nose down; aileron + = roll right; rudder = -(roll + 0.8 steer): roll + = nose right.
                    ctl.throttle = -o.pitch; ctl.steer = o.roll; ctl.roll = o.yaw;
                    ctl.leverSet = true; ctl.lever = o.lever;
                    break;
                case FlightStyle.Rotor:
                    // Helicopter: stick = velocity command (forward +), pedals = yaw rate (roll + = nose right), vert = climb rate.
                    ctl.throttle = o.fwd; ctl.steer = o.roll; ctl.roll = o.yaw; ctl.vert = o.vert;
                    break;
                default:
                    // Hover car: throttle = forward thrust, steer = yaw rate, vert = climb rate.
                    ctl.throttle = o.fwd; ctl.steer = o.yaw; ctl.vert = o.vert;
                    break;
            }
            car.netControls = ctl;
        }

        static float Ray(Vector3 o, Vector3 d, float len, int mask)
            => Physics.Raycast(o, d, out var hit, len, mask, QueryTriggerInteraction.Ignore) ? hit.distance : len;

        static Vector3 FlatToPlayerDir(Vector3 d) { d.y = 0f; return d.sqrMagnitude < 1e-3f ? Vector3.forward : d.normalized; }
        Vector3 FlatToPlayer(Vector3 from) => FlatToPlayerDir(PosOf(_player) - from);

        // ---- placement -----------------------------------------------------------------------------------

        void PlaceNearPlayer(Bot b)
        {
            if (b.car == null || _player == null) return;

            Vector3 playerPos = PosOf(_player);
            b.active = false; b.entered = false; b.sep = Vector3.zero;
            float golden = b.number * 137.5f + Time.frameCount * 0.37f;

            bool haveGround = Physics.Raycast(playerPos + Vector3.up, Vector3.down, out var gh, 5000f,
                PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore);
            if (!haveGround)
            {
                _spawnYawDeg += 67f;
                b.car.SetSpawnColumn(playerPos, true, _spawnYawDeg);
                return;
            }

            Vector3 pos; Quaternion rot;
            if (b.Flies)
            {
                // Out in the air on a ring around the player, facing it, clear of any building.
                float radius = b.kind == VehicleKind.Helicopter || b.kind == VehicleKind.DeLorean ? 40f + 6f * (b.number % 6) : 160f + 35f * (b.number % 6);
                Quaternion ring = Quaternion.Euler(0f, golden, 0f);
                Vector3 flat = ring * Vector3.forward * radius;
                float ground = gh.point.y;
                pos = new Vector3(playerPos.x + flat.x, ground + AirSpawnAgl(b.kind), playerPos.z + flat.z);
                for (int i = 0; i < 4 && Physics.CheckSphere(pos, 10f, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore); i++) pos += Vector3.up * 40f;
                rot = Quaternion.LookRotation(FlatToPlayer(pos), Vector3.up);
            }
            else
            {
                rot = Quaternion.Euler(0f, golden, 0f);
                if (playerPos.y - gh.point.y > NearGroundMax) pos = gh.point + Vector3.up * 0.4f;
                else
                {
                    // Directly above the player, spread on a growing ring so 50 bots do not land on top of each other.
                    float r = b.number <= 1 ? 0f : 5f + 4f * Mathf.Sqrt(b.number);
                    Vector3 flat = Quaternion.Euler(0f, golden, 0f) * Vector3.forward * r;
                    pos = playerPos + flat + Vector3.up * (SpawnHeight + (b.number % 5) * SpawnHeightStep);
                    if (Physics.CheckSphere(pos, 2.5f, PhysicsSetup.TerrainMask, QueryTriggerInteraction.Ignore))
                        pos = playerPos + Vector3.up * (SpawnHeight + (b.number % 4) * SpawnHeightStep);
                }
            }

            b.car.PlaceDirect(pos, rot);
            b.car.infiniteBoost = true;
            EnterVehicle(b);
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
            Debug.Log($"[DemoBot] #{b.number} ({Vehicles.NameOf(b.kind)}) DEMOLITION hit at {speed * 3.6f:0} km/h!");
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
