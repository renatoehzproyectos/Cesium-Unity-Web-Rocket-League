using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Keeps ~60 FPS by trading, in this order: render scale (down to 0.7), view distance (down to 50 %, hidden by the haze), and only as a last
    /// resort tile detail (SSE up to +16): changing the SSE makes Cesium re-request almost every tile, which shows as waves of blur.
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
            // levels 1-3: render scale 0.9/0.8/0.7 ; 4-8: view distance 90..50 % ; 9-12: SSE +4..+16 (last resort)
            root.autoScaleMul = Level >= 3 ? 0.7f / 1f : 1f - 0.1f * Level;
            root.autoFarMul = Level <= 3 ? 1f : 1f - 0.1f * (Level - 3);
            root.autoSseAdd = Level <= 8 ? 0f : (Level - 8) * 4f;
            root.ApplyLive();
        }

        public void ResetToUser() { logic.Reset(); Set(0); }
    }
}
