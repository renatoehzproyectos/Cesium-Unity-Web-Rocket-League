using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Fixed camera flight (orbit + zoom + pitch sweep) that records frame times, so "60 FPS at max settings" is measured instead of
    /// assumed. Start from the settings panel or with ?bench=1. AutoQuality is paused while it runs.
    /// </summary>
    public class BenchmarkRunner : MonoBehaviour
    {
        public GameRoot root;
        public float warmupSeconds = 6f, runSeconds = 30f;
        public bool Running { get; private set; }
        public string Report { get; private set; } = "";
        public float Progress => Running ? Mathf.Clamp01(_t / (warmupSeconds + runSeconds)) : 0f;

        readonly List<float> _ms = new List<float>(4096);
        float _t;
        float _y0, _p0, _d0;

        public void Begin()
        {
            if (Running || root == null || root.orbit == null) return;
            Running = true; _t = 0; _ms.Clear(); Report = "";
            var o = root.orbit; _y0 = o.yaw; _p0 = o.pitch; _d0 = o.distance;
            o.externalControl = true;
            root.SetCameraMode(1);
            if (root.autoQuality != null) root.autoQuality.ResetToUser();
        }

        void Update()
        {
            if (!Running) return;
            float dt = Time.unscaledDeltaTime;
            _t += dt;
            if (_t > warmupSeconds) _ms.Add(dt * 1000f);
            float k = _t - warmupSeconds;
            var o = root.orbit;
            o.yaw = _y0 + _t * 24f;                                                      // one lap per 15 s
            o.pitch = 14f + 22f * Mathf.Sin(_t * 0.5f);                                  // low street view <-> high aerial
            o.distance = Mathf.Lerp(6f, 45f, 0.5f + 0.5f * Mathf.Sin(_t * 0.35f));
            if (_t >= warmupSeconds + runSeconds) Finish();
        }

        void Finish()
        {
            Running = false;
            var o = root.orbit; o.externalControl = false; o.yaw = _y0; o.pitch = _p0; o.distance = _d0;
            Report = Summarize();
            Debug.Log("[RocketWorld bench]\n" + Report);
        }

        string Summarize()
        {
            if (_ms.Count == 0) return "no frames";
            var s = new List<float>(_ms); s.Sort();
            float sum = 0; foreach (var v in s) sum += v;
            float avg = sum / s.Count;
            float Pct(float p) => s[Mathf.Min(s.Count - 1, (int)(p * s.Count))];
            int over = 0, over33 = 0; foreach (var v in s) { if (v > 16.7f * 1.15f) over++; if (v > 33.4f) over33++; }
            var sb = new StringBuilder();
            sb.AppendLine($"frames {s.Count} over {runSeconds:0}s");
            sb.AppendLine($"avg {avg:0.0} ms ({1000f / avg:0} fps)   p50 {Pct(0.5f):0.0}   p95 {Pct(0.95f):0.0}   p99 {Pct(0.99f):0.0}   worst {s[s.Count - 1]:0.0} ms");
            sb.AppendLine($"frames >19 ms: {100f * over / s.Count:0.0}%   >33 ms: {100f * over33 / s.Count:0.0}%");
            sb.AppendLine($"tiles loaded {(root.tileset != null ? root.tileset.ComputeLoadProgress() : 0):0}%   heap {UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024)} MB (reserved {UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / (1024 * 1024)} MB)");
            sb.AppendLine($"settings: sse {root.EffectiveSse:0} scale {root.EffectiveScale:0.00} far {root.EffectiveFar:0} m, baked {root.settings.bakedLighting}, sky {(root.settings.proceduralSky ? "procedural" : "flat")}");
            sb.AppendLine("device: " + DeviceProfile.Describe());
            sb.AppendLine("verdict: " + (Pct(0.95f) <= 19f && over33 == 0 ? "PASS (stable 60 fps)" : Pct(0.95f) <= 25f ? "BORDERLINE" : "FAIL (below 60 fps)"));
            return sb.ToString();
        }
    }
}
