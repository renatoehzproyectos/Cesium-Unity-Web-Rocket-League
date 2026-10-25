namespace RocketWorld
{
    /// <summary>
    /// Pure controller (no Unity types, unit-tested) for the "Anti crash" toggle. It owns a quality level (0 = the minimum of
    /// render scale / depth of field / motion blur, MaxLevel = the player's own sliders) that starts at 0 on every page load
    /// and moves with the measured frame rate: a slow stretch (below ~33 FPS) drops it at once, a stable fast stretch raises it
    /// one step at a time. A drop soon after a rise caps the level just below the one that failed, and the cap relaxes slowly.
    /// </summary>
    public class AntiCrashLogic
    {
        public const int MaxLevel = 20;
        // stages: levels 0-10 raise the render scale, 10-15 depth of field, 15-20 motion blur
        public const int ScaleLevels = 10, DofStart = 10, DofLevels = 5, MotionStart = 15, MotionLevels = 5;
        public const float MinScale = 0.3f;

        public float slowMs = 1000f / 33f;          // window average above this = below ~33 FPS: too slow
        public float fastMs = 1000f / 48f;          // window average below this = headroom to try more
        public float windowSeconds = 1f;
        public int fastWindowsToImprove = 2;
        public float improveCooldown = 2f;
        public float degradeCooldown = 1f;
        public int degradeSteps = 2;
        public float capRelaxSeconds = 30f;
        public float ignoreFrameMs = 250f;          // tab switches / one-off hitches do not count

        public int Level { get; private set; }
        public int Cap { get; private set; } = MaxLevel;

        float _sum, _time; int _n, _fast;
        float _cooldown, _sinceImprove = 1e9f, _sinceCap;

        public void Reset() { Level = 0; Cap = MaxLevel; _sum = _time = _cooldown = _sinceCap = 0; _n = _fast = 0; _sinceImprove = 1e9f; }

        /// <summary>Feed one frame time; returns true when <see cref="Level"/> changed.</summary>
        public bool Feed(float frameMs)
        {
            float dt = frameMs / 1000f;
            _sinceImprove += dt; _sinceCap += dt;
            if (_cooldown > 0) _cooldown -= dt;
            if (Cap < MaxLevel && _sinceCap >= capRelaxSeconds) { Cap++; _sinceCap = 0; }
            if (frameMs > ignoreFrameMs) return false;
            _sum += frameMs; _n++; _time += dt;
            if (_time < windowSeconds) return false;

            float avg = _sum / _n; _sum = 0; _n = 0; _time = 0;
            if (avg > slowMs)
            {
                _fast = 0;
                if (_cooldown > 0 || Level == 0) return false;
                if (_sinceImprove < 10f) { Cap = System.Math.Max(0, Level - 1); _sinceCap = 0; }   // the level we just reached did not hold
                int before = Level;
                Level = System.Math.Max(0, Level - degradeSteps);
                _cooldown = degradeCooldown;
                return Level != before;
            }
            if (avg < fastMs)
            {
                _fast++;
                if (_fast >= fastWindowsToImprove && _cooldown <= 0 && Level < System.Math.Min(Cap, MaxLevel))
                {
                    _fast = 0; _sinceImprove = 0; _cooldown = improveCooldown; Level++;
                    return true;
                }
            }
            else _fast = 0;
            return false;
        }

        static float Frac(int level, int start, int span) => level <= start ? 0f : level >= start + span ? 1f : (level - start) / (float)span;

        /// <summary>Values for a level, never above the player's own settings (their sliders are the ceiling).</summary>
        public static void Resolve(int level, float userScale, float userDof, float userMotion, out float scale, out float dof, out float motion)
        {
            float lo = System.Math.Min(MinScale, userScale);
            scale = lo + (userScale - lo) * Frac(level, 0, ScaleLevels);
            dof = userDof * Frac(level, DofStart, DofLevels);
            motion = userMotion * Frac(level, MotionStart, MotionLevels);
        }
    }
}
