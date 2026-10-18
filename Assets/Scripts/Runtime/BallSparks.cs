using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Sparkles when the car touches the ball: one additive spark sprite (Resources/Ball/spark) that starts at 0.55x and grows to 1.75x
    /// over 0.42 s, holds, then fades, with a random spin, plus a short light flash. Size and colour follow the strength of the touch.
    /// Same numbers as the original web game's spawnSpark().
    /// </summary>
    public sealed class BallSparks : MonoBehaviour
    {
        const float Life = 0.42f;
        ParticleSystem _ps;
        Light _light;
        float _lightLife, _lightPeak;

        void Awake()
        {
            var baseMat = Resources.Load<Material>("Materials/ParticleAdd");
            var tex = Resources.Load<Texture2D>("Ball/spark");
            if (baseMat == null) { Debug.LogWarning("[RocketWorld] ParticleAdd material missing (run RocketWorld/Setup) - no ball sparks."); }

            var go = new GameObject("Sparks");
            go.transform.SetParent(transform, false);
            _ps = go.AddComponent<ParticleSystem>();
            _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = _ps.main;
            main.loop = true; main.playOnAwake = false; main.duration = 1f;
            main.startLifetime = Life; main.startSpeed = 0f; main.startSize = 1f; main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.World; main.maxParticles = 16;
            var em = _ps.emission; em.enabled = false;                       // particles are emitted by hand
            var sh = _ps.shape; sh.enabled = false;

            // 0.55x -> 1.75x
            var sol = _ps.sizeOverLifetime; sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.55f, 1f, 1.75f));

            // hold, then fade: alpha = 1 until t = 0.18, then (1 - (t - 0.18) / 0.82) ^ 1.4
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.18f), new GradientAlphaKey(0.80f, 0.30f), new GradientAlphaKey(0.57f, 0.45f),
                    new GradientAlphaKey(0.37f, 0.60f), new GradientAlphaKey(0.19f, 0.75f), new GradientAlphaKey(0.05f, 0.90f), new GradientAlphaKey(0f, 1f),
                });
            var col = _ps.colorOverLifetime; col.enabled = true; col.color = g;

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            r.sortMode = ParticleSystemSortMode.None;
            if (baseMat != null)
            {
                var m = new Material(baseMat) { name = "RW_Spark" };
                if (tex != null && m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
                r.sharedMaterial = m;
            }
            else r.enabled = false;
            _ps.Play();

            var lg = new GameObject("SparkLight");
            lg.transform.SetParent(transform, false);
            _light = lg.AddComponent<Light>();
            _light.type = LightType.Point; _light.range = 14f; _light.color = new Color(1f, 0.93f, 0.73f); _light.shadows = LightShadows.None; _light.intensity = 0f; _light.enabled = false;
        }

        /// <summary>intensity 0..1: 0.12 for a tap, ~1 for a hard shot.</summary>
        public void Spawn(Vector3 pos, float intensity)
        {
            intensity = Mathf.Clamp01(intensity);
            var ep = new ParticleSystem.EmitParams
            {
                position = pos, velocity = Vector3.zero,
                startLifetime = Life,
                startSize = 0.9f + intensity * 1.5f,                         // metres (sprite width)
                startColor = intensity > 0.7f ? Color.white : new Color(1f, 0.94f, 0.75f),
                rotation = Random.Range(0f, 360f),
                angularVelocity = Random.Range(-344f, 344f),                 // +-6 rad/s
            };
            _ps.Emit(ep, 1);
            _light.transform.position = pos;
            _lightPeak = (1.2f + intensity * 3f) * 1.4f; _lightLife = 0.32f;
            _light.enabled = true; _light.intensity = _lightPeak;
        }

        void Update()
        {
            if (_lightLife <= 0f) return;
            _lightLife -= Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(_lightLife / 0.32f);
            _light.intensity = _lightPeak * k * k;
            if (_lightLife <= 0f) _light.enabled = false;
        }
    }
}
