using CesiumForUnity;
using System.Runtime.InteropServices;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// The Starship hits the terrain: a 50 Mt "Tsar Bomba" goes off at the impact point (fireball ~4.6 km, shock ring, 64 km mushroom cloud, blast wave that
    /// throws every other rigidbody within 15 km: cars, ball). Only the solo player or the match creator (host) can set it off; on a joined client
    /// <see cref="CanDetonate"/> is false and the rocket just ends in a small fireball.
    /// Everything is procedural: primitives with URP Unlit / Lit opaque materials (no transparency, no particle shaders: both can be stripped from a WebGL build),
    /// scaled down if the camera far plane cannot show a 64 km cloud.
    /// </summary>
    public static class TsarBomba
    {
        /// <summary>Solo / match creator: true. A joined client: false.</summary>
        public static bool CanDetonate()
        {
            var root = Object.FindFirstObjectByType<GameRoot>();
            return root == null || root.net == null || !root.net.IsClientOnly;
        }

        /// <summary>Starts the effect at a Unity world position. <paramref name="full"/> = the real thing; otherwise a small crash fireball (1 / 100 scale).</summary>
        public static TsarBombaFx Spawn(Vector3 position, bool full, Rigidbody exclude = null)
        {
            var go = new GameObject(full ? "TsarBomba" : "RocketCrash");
            var geo = Object.FindFirstObjectByType<CesiumGeoreference>();
            if (geo != null) go.transform.SetParent(geo.transform, false);
            go.transform.position = position; go.transform.rotation = Quaternion.identity;
            if (geo != null) go.AddComponent<CesiumGlobeAnchor>();           // stays put when Cesium re-centres the origin
            var fx = go.AddComponent<TsarBombaFx>(); fx.Begin(full ? 1f : 0.01f, exclude);
            return fx;
        }
    }

    [DefaultExecutionOrder(2000)]
    public sealed class TsarBombaFx : MonoBehaviour
    {
        // real-world numbers (metres, seconds); the game compresses time so the whole thing is over in about a minute
        const float FireballR = 4600f, StemR = 2600f, CapR = 30000f, CloudTop = 64000f, ShockSpeed = 2500f, ShockMax = 40000f, ForceRadius = 15000f, Life = 75f;

        float _k = 1f, _t, _farK = 1f; bool _full;
        Rigidbody _exclude; Rigidbody[] _bodies; bool[] _hit; Camera _cam;
        Transform _fire, _stem, _cap, _ring, _glow; Material _fireMat, _cloudMat, _ringMat;
        AudioSource _audio; bool _boomPlayed; float _boomAt;
        static Texture2D _white;

        public void Begin(float scale, Rigidbody exclude)
        {
            _k = scale; _full = scale > 0.5f; _exclude = exclude; _t = 0f;
            _fire = Prim(PrimitiveType.Sphere, "Fireball", out _fireMat, new Color(1f, 0.95f, 0.8f), false);
            _stem = Prim(PrimitiveType.Cylinder, "Stem", out _cloudMat, new Color(0.95f, 0.5f, 0.15f), true);
            _cap = Prim(PrimitiveType.Sphere, "Cap", out _, new Color(0.9f, 0.45f, 0.12f), true);
            _cap.GetComponent<Renderer>().sharedMaterial = _cloudMat;
            _ring = MakeRing(out _ringMat);
            _glow = null;
            var all = FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);
            var list = new System.Collections.Generic.List<Rigidbody>();
            foreach (var rb in all) if (rb != null && !rb.isKinematic && rb != _exclude && (rb.position - transform.position).sqrMagnitude < ForceRadius * ForceRadius * _k * _k) list.Add(rb);
            _bodies = list.ToArray(); _hit = new bool[_bodies.Length];
            _cam = Camera.main;
            _boomAt = _cam != null ? Mathf.Min(4f, (_cam.transform.position - transform.position).magnitude / 343f) : 1f;
            Update();
        }

        Transform Prim(PrimitiveType type, string name, out Material mat, Color col, bool lit)
        {
            var g = GameObject.CreatePrimitive(type); g.name = name; Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(transform, false);
            var sh = Shader.Find(lit ? "Universal Render Pipeline/Lit" : "Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            mat = new Material(sh); SetCol(mat, col);
            var r = g.GetComponent<Renderer>(); r.sharedMaterial = mat; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            return g.transform;
        }

        static void SetCol(Material m, Color c) { m.color = c; if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c); if (m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", c * 0.6f); } }

        /// <summary>Flat annulus (double sided, 1 unit outer radius, inner radius 0.93) lying on the ground: the shock ring.</summary>
        Transform MakeRing(out Material mat)
        {
            const int N = 96; var v = new Vector3[N * 2]; var tri = new int[N * 12];
            for (int i = 0; i < N; i++) { float a = i * Mathf.PI * 2f / N, c = Mathf.Cos(a), s = Mathf.Sin(a); v[i] = new Vector3(c, 0, s); v[N + i] = new Vector3(c * 0.93f, 0, s * 0.93f); }
            for (int i = 0; i < N; i++)
            {
                int j = (i + 1) % N, o = i * 12;
                tri[o] = i; tri[o + 1] = j; tri[o + 2] = N + i; tri[o + 3] = j; tri[o + 4] = N + j; tri[o + 5] = N + i;
                tri[o + 6] = i; tri[o + 7] = N + i; tri[o + 8] = j; tri[o + 9] = j; tri[o + 10] = N + i; tri[o + 11] = N + j;
            }
            var m = new Mesh { name = "ShockRing", vertices = v, triangles = tri }; m.RecalculateNormals(); m.RecalculateBounds();
            var g = new GameObject("ShockRing"); g.transform.SetParent(transform, false);
            g.AddComponent<MeshFilter>().sharedMesh = m;
            var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            mat = new Material(sh); SetCol(mat, new Color(1f, 0.97f, 0.9f));
            var r = g.AddComponent<MeshRenderer>(); r.sharedMaterial = mat; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return g.transform;
        }

        void Update()
        {
            _t += Mathf.Min(Time.unscaledDeltaTime, 0.1f); if (_t > Life * Mathf.Max(0.25f, _k)) { Destroy(gameObject); return; }
            if (_cam == null) _cam = Camera.main;
            // the camera far plane limits what can be shown: shrink the whole thing if needed (keeps the proportions)
            float far = _cam != null ? _cam.farClipPlane : 20000f; _farK = Mathf.Clamp01(far * 0.8f / (CloudTop * 1.3f)); float S = _k * Mathf.Max(_farK, _full ? 0.12f : 1f);
            float t = _t / Mathf.Max(0.05f, Mathf.Sqrt(_k));                                  // small blast: faster

            // fireball: expands, white -> yellow -> orange -> dull red, then collapses into the cloud
            float rf = FireballR * S * (1f - Mathf.Exp(-t / 2.6f)); float shrink = Mathf.SmoothStep(1f, 0.05f, Mathf.InverseLerp(11f, 22f, t));
            _fire.localPosition = new Vector3(0, rf * 0.55f * shrink, 0); _fire.localScale = Vector3.one * (rf * 2f * shrink);
            SetCol(_fireMat, Color.Lerp(Color.Lerp(new Color(1f, 0.97f, 0.85f), new Color(1f, 0.55f, 0.1f), Mathf.InverseLerp(0.3f, 5f, t)), new Color(0.45f, 0.08f, 0.02f), Mathf.InverseLerp(5f, 20f, t)));
            _fire.gameObject.SetActive(rf > 0.5f && shrink > 0.06f);

            // shock ring on the ground
            float rr = Mathf.Min(ShockSpeed * S * t, ShockMax * S); _ring.localPosition = new Vector3(0, 2f, 0); _ring.localScale = new Vector3(rr, 1f, rr);
            SetCol(_ringMat, Color.Lerp(new Color(1f, 0.97f, 0.9f), new Color(0.55f, 0.5f, 0.45f), Mathf.InverseLerp(0f, 14f, t)));
            _ring.gameObject.SetActive(t > 0.02f && rr < ShockMax * S * 0.995f);

            // mushroom: stem rises, the cap follows and spreads; colour goes from fire orange to ash grey
            float rise = 1f - Mathf.Exp(-Mathf.Max(0f, t - 1.5f) / 16f), h = CloudTop * S * rise;
            float sr = StemR * S * Mathf.Min(1f, 0.15f + rise * 1.2f);
            _stem.gameObject.SetActive(h > 20f); _cap.gameObject.SetActive(h > 20f);
            _stem.localPosition = new Vector3(0, h * 0.5f, 0); _stem.localScale = new Vector3(sr * 2f, h * 0.5f, sr * 2f);       // Unity cylinder: height 2, radius 0.5
            float cr = CapR * S * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(2f, 40f, t));
            _cap.localPosition = new Vector3(0, h, 0); _cap.localScale = new Vector3(cr * 2f, Mathf.Max(1f, cr * 0.7f), cr * 2f);
            SetCol(_cloudMat, Color.Lerp(new Color(0.95f, 0.5f, 0.15f), new Color(0.32f, 0.28f, 0.26f), Mathf.InverseLerp(4f, 40f, t)));

            // blast wave: velocity kick on bodies when the ring reaches them
            if (_bodies != null)
                for (int i = 0; i < _bodies.Length; i++)
                {
                    var rb = _bodies[i]; if (_hit[i] || rb == null) continue;
                    Vector3 d = rb.position - transform.position; float dist = d.magnitude;
                    if (dist > rr) continue;
                    _hit[i] = true; Vector3 dir = new Vector3(d.x, Mathf.Max(0.4f * dist, 40f * _k), d.z).normalized;
                    float v = 260f * _k * Mathf.Clamp01(1f - dist / (ForceRadius * _k)) + 5f;                 // m/s kick, falls off linearly to the edge of the blast radius
                    rb.AddForce(dir * Mathf.Max(v, 5f), ForceMode.VelocityChange);
                }

            // boom (procedural low rumble), delayed by the sound travel time
            if (!_boomPlayed && _t >= _boomAt) { _boomPlayed = true; PlayBoom(); }
        }

        void LateUpdate()
        {
            if (_cam == null) return;
            float dist = (_cam.transform.position - transform.position).magnitude;
            float amp = (_full ? 1f : 0.05f) * Mathf.Clamp01(1f - dist / 30000f) * Mathf.Clamp01(1f - (_t - _boomAt) / 14f) * (_t >= _boomAt ? 1f : 0f);
            if (amp > 0.001f) _cam.transform.position += Random.insideUnitSphere * (1.6f * amp);      // camera shake after the sound arrives
        }

        void OnGUI()
        {
            if (!_full || _t > 6f) return;                                                    // the flash (white-out), then it fades over ~5 s
            if (_white == null) { _white = new Texture2D(1, 1); _white.SetPixel(0, 0, Color.white); _white.Apply(); }
            float a = _t < 0.35f ? 1f : Mathf.Pow(1f - Mathf.InverseLerp(0.35f, 5.5f, _t), 2f);
            var old = GUI.color; GUI.color = new Color(1f, 0.98f, 0.92f, a); GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _white); GUI.color = old;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void RW_PlayBoom(int full, float volume);
#endif

        void PlayBoom()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            RW_PlayBoom(_full ? 1 : 0, _full ? 1f : 0.5f);      // WebAudio in JS: AudioClip.SetData crashes the threaded WebGL player
            return;
#endif
            const int rate = 22050; float len = _full ? 7f : 1.5f; int n = (int)(rate * len); var d = new float[n];
            uint seed = 12345u; float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate, env = Mathf.Exp(-t / (_full ? 2.2f : 0.35f)) * Mathf.Clamp01(t * 30f);
                seed = seed * 1664525u + 1013904223u; float noise = ((seed >> 9) / 4194304f) - 1f; lp += (noise - lp) * 0.02f;        // low-passed noise = rumble
                d[i] = (lp * 4f + 0.7f * Mathf.Sin(2f * Mathf.PI * 32f * t) + 0.4f * Mathf.Sin(2f * Mathf.PI * 21f * t)) * env * 0.9f;
            }
            var clip = AudioClip.Create("boom", n, 1, rate, false); clip.SetData(d, 0);
            _audio = gameObject.AddComponent<AudioSource>(); _audio.spatialBlend = 0f; _audio.volume = _full ? 1f : 0.5f; _audio.clip = clip; _audio.Play();
        }
    }
}
