namespace RocketWorld
{
    /// <summary>
    /// Pure frame-time controller (no Unity types, unit-tested). Feed it frame times; it says when to cut quality and when to
    /// cautiously give some back. Under vsync the frame time sits at ~16.7 ms even with GPU headroom, so recovery is
    /// time-based (long stable stretch), and every re-degrade shortly after a recovery doubles the wait (anti-oscillation).
    /// </summary>
    public class AutoQualityLogic
    {
        public enum Action { None, Degrade, Improve }

        public float targetMs = 1000f / 60f;
        public float degradeFactor = 1.12f;        // window average above target*1.12 is "slow"
        public float windowSeconds = 1f;
        public int slowWindowsToDegrade = 2;
        public float cooldownSeconds = 3f;
        public float improveAfterSeconds = 20f;
        public float maxImproveWait = 160f;
        public float ignoreFrameMs = 250f;         // tab switches / one-off hitches do not count

        float _sum, _time; int _n;
        int _slow;
        float _stable, _cooldown;
        float _wait;
        float _sinceImprove = 1e9f;

        public AutoQualityLogic() { _wait = improveAfterSeconds; }

        public void Reset() { _sum = _time = _stable = _cooldown = 0; _n = _slow = 0; _wait = improveAfterSeconds; _sinceImprove = 1e9f; }

        public Action Feed(float frameMs)
        {
            float dt = frameMs / 1000f;
            _sinceImprove += dt;
            if (_cooldown > 0) _cooldown -= dt;
            if (frameMs > ignoreFrameMs) return Action.None;
            _sum += frameMs; _n++; _time += dt;
            if (_time < windowSeconds) return Action.None;

            float avg = _sum / _n; _sum = 0; _n = 0; _time = 0;
            bool slow = avg > targetMs * degradeFactor;
            if (slow)
            {
                _slow++; _stable = 0;
                if (_slow >= slowWindowsToDegrade && _cooldown <= 0)
                {
                    _slow = 0; _cooldown = cooldownSeconds;
                    if (_sinceImprove < 30f) _wait = System.Math.Min(_wait * 2f, maxImproveWait);   // we improved and it hurt: wait longer next time
                    return Action.Degrade;
                }
                return Action.None;
            }
            _slow = 0;
            _stable += windowSeconds;
            if (_stable >= _wait && _cooldown <= 0)
            {
                _stable = 0; _sinceImprove = 0;
                return Action.Improve;
            }
            return Action.None;
        }
    }
}
