using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Keeps ~60 FPS by trading, in this order: render scale (down to 0.7), tile detail (SSE up to +16), view distance (down to 60 %).
    /// The user's settings stay the ceiling: this only adds offsets that GameRoot applies on top of them.
    /// </summary>
    public class AutoQuality : MonoBehaviour
    {
        public GameRoot root;
        public bool enabledByUser = true;
        public readonly AutoQualityLogic logic = new AutoQualityLogic();
        public int Level { get; private set; }          // 0 = user's settings, higher = more degraded
        const int MaxLevel = 12;

        void Update()
        {
            if (root == null || !enabledByUser || root.benchmark != null && root.benchmark.Running) return;
            if (root.tileset != null && root.tileset.ComputeLoadProgress() < 99f) { logic.Feed(16f); return; }   // initial load hitches don't count
            var a = logic.Feed(Time.unscaledDeltaTime * 1000f);
            if (a == AutoQualityLogic.Action.Degrade && Level < MaxLevel) Set(Level + 1);
            else if (a == AutoQualityLogic.Action.Improve && Level > 0) Set(Level - 1);
        }

        public void Set(int level)
        {
            Level = Mathf.Clamp(level, 0, MaxLevel);
            // levels 1-3: render scale 0.9/0.8/0.7 ; 4-8: SSE +4..+16 ; 9-12: view distance 90..60 %
            root.autoScaleMul = Level >= 3 ? 0.7f / 1f : 1f - 0.1f * Level;
            root.autoSseAdd = Level <= 3 ? 0f : Mathf.Min(16f, (Level - 3) * 4f);
            root.autoFarMul = Level <= 8 ? 1f : 1f - 0.1f * (Level - 8);
            root.ApplyLive();
        }

        public void ResetToUser() { logic.Reset(); Set(0); }
    }
}
