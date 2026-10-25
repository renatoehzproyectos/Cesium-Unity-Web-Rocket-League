using System;

namespace RocketWorld.Sim
{
    /// <summary>
    /// Numbers behind view-driven tile streaming (engine-free, unit-tested): how big the tile cache may grow and how far the always-loaded
    /// physics ring reaches. The cache is what makes a tile that was seen once (and finished loading) come back instantly when looked at again.
    /// </summary>
    public static class TileCachePolicy
    {
        /// <summary>Largest cache to try on this device: the normal budget times 3 on desktop (2 on mobile), never above a fraction of the heap.</summary>
        public static long MaxBudget(long baseBytes, bool mobile, long heapLimitBytes)
        {
            long want = baseBytes * (mobile ? 2 : 3);
            return Math.Min(want, (long)(heapLimitBytes * 0.5));
        }

        /// <summary>One adaptation step (call every couple of seconds): shrink fast under memory pressure, grow slowly when there is room.</summary>
        public static long Next(long budget, long allocated, long heapLimit, long minBudget, long maxBudget)
        {
            if (allocated > heapLimit * 0.88) budget = (long)(budget * 0.85);
            else if (allocated < heapLimit * 0.62) budget = (long)(budget * 1.08);
            return Math.Max(minBudget, Math.Min(maxBudget, budget));
        }

        /// <summary>How far (m) the look-ahead streaming camera reaches in the direction of travel: the faster you go, the earlier the detail
        /// must already be there. Always loaded at a finer level than the main camera asks for.</summary>
        public static float AheadRange(float speedMs, float minRange = 350f, float maxRange = 900f)
            => Math.Max(minRange, Math.Min(maxRange, minRange + 12f * Math.Max(0f, speedMs)));

        /// <summary>Radius (m) around a car that is loaded at full detail whether or not it is on screen: just enough for the physics colliders,
        /// a bit more at speed so the ground ahead exists before the car gets there.</summary>
        public static float PhysicsRing(float speedMs, float baseRing = 150f, float maxRing = 330f)
            => Math.Max(baseRing, Math.Min(maxRing, baseRing + 2.5f * Math.Max(0f, speedMs)));
    }
}
