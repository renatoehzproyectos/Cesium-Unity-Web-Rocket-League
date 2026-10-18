using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Boost flame, ported from the original game's "Alpha Gold Rush"-style boost: a soft glowing sprite trail, not a stretched cone.
    /// Two emitters (left / right of the exhaust), 260 particles/s in total, 0.5 s life, world-space so the trail hangs in the air when the car
    /// turns or flips. Size grows 0.44 m -> 2.1 m (ease-out), colour goes yellow -> orange -> deep orange, opacity rises quickly then fades,
    /// particles are thrown backwards at 2-5 m/s, slowed by drag, and spin ~1.5 rad over their life. A warm light follows while boosting.
    /// </summary>
    public sealed class BoostFx : MonoBehaviour
    {
        // exhaust position in the car's body frame (the Octane's hitbox is 120.5 x 86.7 x 38.7 uu): rear of the hitbox, slightly below centre
        static readonly Vector3 Exhaust = new Vector3(0f, -0.077f, -1.205f);
        const float Side = 0.277f;                      // each emitter sits 0.16 * hitbox width to the side

        ParticleSystem[] _ps;
        Light _light;
        float _level;
        bool _on;
        static Texture2D _tex;

        void Awake()
        {
            var baseMat = Resources.Load<Material>("Materials/ParticleAlpha");
            if (baseMat == null) { Debug.LogWarning("[RocketWorld] ParticleAlpha material missing (run RocketWorld/Setup) - no boost trail."); return; }
            var mat = new Material(baseMat) { name = "RW_BoostTrail" };
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", SoftSprite());
            _ps = new[] { Make("BoostL", new Vector3(-Side, Exhaust.y, Exhaust.z), mat), Make("BoostR", new Vector3(Side, Exhaust.y, Exhaust.z), mat) };

            var lg = new GameObject("BoostLight");
            lg.transform.SetParent(transform, false);
            lg.transform.localPosition = new Vector3(0f, Exhaust.y, Exhaust.z - 0.6f);
            _light = lg.AddComponent<Light>();
            _light.type = LightType.Point; _light.range = 14f; _light.color = new Color(1f, 0.69f, 0.13f); _light.shadows = LightShadows.None;
            _light.intensity = 0f; _light.enabled = false;
        }

        ParticleSystem Make(string name, Vector3 localPos, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);            // the cone points along local +Z: make that "backwards"
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = true; main.playOnAwake = false; main.duration = 1f;
            main.startLifetime = 0.5f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(2f, 5f);
            main.startSize = 0.44f;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = Color.white;
            main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 200;

            var em = ps.emission; em.enabled = false; em.rateOverTime = 130f;      // 2 x 130 = 260 / s while boosting

            var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 14f; sh.radius = 0.04f; sh.radiusThickness = 1f;

            // size: 0.44 m -> 2.1 m, ease-out (t^0.6)
            var sol = ps.sizeOverLifetime; sol.enabled = true;
            var curve = new AnimationCurve();
            foreach (float t in new[] { 0f, 0.05f, 0.1f, 0.2f, 0.4f, 0.7f, 1f }) curve.AddKey(t, 1f + (2.1f / 0.44f - 1f) * Mathf.Pow(t, 0.6f));
            sol.size = new ParticleSystem.MinMaxCurve(1f, curve);

            // colour: 0xffe27a -> 0xffae00 (at 40 %) -> 0xff5a00; opacity 0.5 * min(12 t, 1) * (1 - t)^0.9
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.886f, 0.478f), 0f), new GradientColorKey(new Color(1f, 0.682f, 0f), 0.4f), new GradientColorKey(new Color(1f, 0.353f, 0f), 1f) },
                new[]
                {
                    new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.46f, 0.083f), new GradientAlphaKey(0.36f, 0.3f), new GradientAlphaKey(0.22f, 0.6f),
                    new GradientAlphaKey(0.09f, 0.85f), new GradientAlphaKey(0f, 1f),
                });
            var col = ps.colorOverLifetime; col.enabled = true; col.color = g;

            var rot = ps.rotationOverLifetime; rot.enabled = true; rot.z = new ParticleSystem.MinMaxCurve(3f);   // 3 rad/s ~ 1.5 rad over the life

            var lim = ps.limitVelocityOverLifetime; lim.enabled = true; lim.limit = 100f; lim.drag = 3f;           // v *= 1 - 3 dt

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            r.sortMode = ParticleSystemSortMode.None;
            r.sharedMaterial = mat;
            ps.Play();
            return ps;
        }

        public void SetBoosting(bool on)
        {
            if (_ps == null || on == _on) return;
            _on = on;
            foreach (var p in _ps) { var e = p.emission; e.enabled = on; }
        }

        void Update()
        {
            if (_light == null) return;
            _level += ((_on ? 1f : 0f) - _level) * Mathf.Min(1f, Time.unscaledDeltaTime * 14f);
            _light.enabled = _level > 0.01f;
            _light.intensity = _level * 2.5f;
        }

        /// <summary>Soft round glow: alpha 1 at the centre, 0.9 at 30 %, 0.28 at 65 %, 0 at the edge.</summary>
        static Texture2D SoftSprite()
        {
            if (_tex != null) return _tex;
            const int n = 64;
            _tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt(Mathf.Pow((x + 0.5f) / n * 2f - 1f, 2f) + Mathf.Pow((y + 0.5f) / n * 2f - 1f, 2f));
                    float a = d < 0.3f ? Mathf.Lerp(1f, 0.9f, d / 0.3f) : d < 0.65f ? Mathf.Lerp(0.9f, 0.28f, (d - 0.3f) / 0.35f) : Mathf.Lerp(0.28f, 0f, Mathf.Clamp01((d - 0.65f) / 0.35f));
                    px[y * n + x] = new Color(1f, 1f, 1f, a);
                }
            _tex.SetPixels(px); _tex.Apply(false, true);
            return _tex;
        }
    }
}
