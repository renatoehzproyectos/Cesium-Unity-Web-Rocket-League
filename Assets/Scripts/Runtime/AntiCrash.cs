using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// "Anti crash" toggle (settings, on by default). Starts every page load with render scale, depth of field and motion blur at their
    /// minimum and raises/lowers them from the measured frame rate so the game keeps at least ~30 FPS. Only those three values are touched,
    /// and the player's own sliders stay the ceiling. GameRoot reads the results through Effective* (anti* overrides, -1 = not active).
    /// </summary>
    public class AntiCrash : MonoBehaviour
    {
        public GameRoot root;
        public readonly AntiCrashLogic logic = new AntiCrashLogic();
        public int Level => logic.Level;
        bool _wasActive;

        void Update()
        {
            if (root == null) return;
            var s = root.settings;
            bool bench = root.benchmark != null && root.benchmark.Running;   // the benchmark measures the player's own settings
            bool active = s.antiCrash && !bench;
            if (!active)
            {
                if (_wasActive || root.antiScale >= 0f) { root.antiScale = root.antiDof = root.antiMotion = -1f; root.ApplyLive(); }
                _wasActive = false; logic.Reset();
                return;
            }
            if (!_wasActive) { _wasActive = true; logic.Reset(); }

            if (root.tileset != null && root.tileset.ComputeLoadProgress() < 99f) logic.Feed(16f);   // initial load hitches don't count
            else logic.Feed(Time.unscaledDeltaTime * 1000f);

            AntiCrashLogic.Resolve(logic.Level, s.renderScale, s.fxDof, s.fxMotion, out float scale, out float dof, out float motion);
            bool scaleChanged = !Mathf.Approximately(root.antiScale, scale);
            root.antiScale = scale; root.antiDof = dof; root.antiMotion = motion;
            if (scaleChanged) root.ApplyLive();
        }
    }
}
