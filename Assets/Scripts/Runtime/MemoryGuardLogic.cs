namespace RocketWorld
{
    /// <summary>
    /// Pure controller (no Unity types, unit-tested) behind MemoryGuard. The wasm heap only ever grows, so "used" is a high-water mark:
    /// the level rises when the heap crosses 70 / 85 / 95 % of the soft limit and relaxes one step after <see cref="RelaxSeconds"/> without growth
    /// (the freed memory is reused inside the heap, so backing off is what keeps it from growing again).
    /// </summary>
    public class MemoryGuardLogic
    {
        public const int MaxLevel = 3;
        public static readonly float[] Thresholds = { 0.70f, 0.85f, 0.95f };
        public float RelaxSeconds = 45f;

        public int Level { get; private set; }
        int _lastMb; float _sinceGrowth;

        public static int LevelFor(int heapMb, int limitMb)
        {
            if (limitMb <= 0 || heapMb <= 0) return 0;
            float f = heapMb / (float)limitMb; int l = 0;
            for (int i = 0; i < Thresholds.Length; i++) if (f >= Thresholds[i]) l = i + 1;
            return l;
        }

        /// <summary>Feed the current heap size; returns true when <see cref="Level"/> changed.</summary>
        public bool Feed(int heapMb, int limitMb, float dt)
        {
            int before = Level;
            if (heapMb > _lastMb) { _sinceGrowth = 0f; _lastMb = heapMb; } else _sinceGrowth += dt;
            int want = LevelFor(heapMb, limitMb);
            if (want > Level) Level = want;
            else if (want < Level && _sinceGrowth >= RelaxSeconds) { Level--; _sinceGrowth = 0f; }
            return Level != before;
        }

        public void Reset() { Level = 0; _lastMb = 0; _sinceGrowth = 0f; }

        // what each level does (GameRoot applies them)
        public static float CacheMul(int level) => level == 0 ? 1f : level == 1 ? 0.65f : level == 2 ? 0.4f : 0.25f;
        public static float ColliderMul(int level) => level == 0 ? 1f : level == 1 ? 0.8f : level == 2 ? 0.55f : 0.4f;
        public static float ScaleMul(int level) => level <= 1 ? 1f : level == 2 ? 0.85f : 0.7f;
    }
}
