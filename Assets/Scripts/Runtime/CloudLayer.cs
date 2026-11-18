using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace RocketWorld
{
    /// <summary>
    /// Cheap cloud deck: ONE flat disc (a 24x24 grid, ~1.2k triangles, one draw call) at a fixed height above the camera, with a soft procedural cloud
    /// texture generated over a few frames at start. It slowly turns round the camera so the sky is not static, takes its colour from the fog
    /// (warm at dusk, dark at night) and fades out when the camera climbs near it. No volumetrics, no extra render pass.
    /// The RGB of the texture already looks like a cloudy sky (blue to white), so if a build ever ignores the alpha it still looks like sky, never a white ceiling.
    /// Off with <c>?clouds=0</c>, and never created on "safe" (low-end) devices.
    /// </summary>
    public class CloudLayer : MonoBehaviour
    {
        const float Alt = 1800f;
        const int Grid = 24;
        Material _mat; MeshRenderer _mr; Texture2D _tex; bool _ready; float _radius = -1f, _nextTint;
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        void Start()
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) { enabled = false; return; }
            _mat = new Material(sh) { name = "RW_Clouds" };
            _mat.SetFloat("_Surface", 1f); _mat.SetFloat("_Blend", 0f);
            _mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); _mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (_mat.HasProperty("_SrcBlendAlpha")) _mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            if (_mat.HasProperty("_DstBlendAlpha")) _mat.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            _mat.SetFloat("_ZWrite", 0f); _mat.SetFloat("_Cull", 0f);
            _mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            _mat.SetOverrideTag("RenderType", "Transparent");
            _mat.renderQueue = (int)RenderQueue.Transparent - 50;                 // before the other transparent effects

            var go = new GameObject("CloudLayer");
            go.layer = PhysicsSetup.CarLayer;                                      // the minimap camera skips this layer: no clouds over the map
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = BuildDisc();
            _mr = go.AddComponent<MeshRenderer>();
            _mr.sharedMaterial = _mat; _mr.shadowCastingMode = ShadowCastingMode.Off; _mr.receiveShadows = false; _mr.enabled = false;
            _mr.lightProbeUsage = LightProbeUsage.Off; _mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _mrT = go.transform;
            StartCoroutine(Generate());
        }

        Transform _mrT;

        void OnDestroy() { if (_tex != null) Destroy(_tex); if (_mat != null) Destroy(_mat); }

        static Mesh BuildDisc()
        {
            int n = Grid + 1; var v = new Vector3[n * n]; var uv = new Vector2[n * n]; var tri = new int[Grid * Grid * 6];
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    float fx = x / (float)Grid * 2f - 1f, fz = z / (float)Grid * 2f - 1f;
                    v[z * n + x] = new Vector3(fx, 0f, fz); uv[z * n + x] = new Vector2((fx + 1f) * 0.5f, (fz + 1f) * 0.5f);
                }
            int t = 0;
            for (int z = 0; z < Grid; z++)
                for (int x = 0; x < Grid; x++)
                {
                    int a = z * n + x, b = a + 1, c = a + n, d = c + 1;
                    tri[t++] = a; tri[t++] = b; tri[t++] = c; tri[t++] = b; tri[t++] = d; tri[t++] = c;
                }
            var m = new Mesh { name = "CloudDisc", vertices = v, uv = uv, triangles = tri };
            m.bounds = new Bounds(Vector3.zero, new Vector3(2f, 0.2f, 2f));
            return m;
        }

        static float Fbm(float x, float y)
        {
            float a = 0.5f, s = 0f;
            for (int i = 0; i < 4; i++) { s += a * Mathf.PerlinNoise(x, y); x *= 2.03f; y *= 2.03f; a *= 0.5f; }
            return s / 0.9375f;
        }

        IEnumerator Generate()
        {
            int N = DeviceProfile.IsMobile ? 256 : 512;
            _tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "RW_CloudTex" };
            var px = new Color32[N * N];
            float ox = Random.value * 100f, oy = Random.value * 100f;
            var sky = new Color(0.55f, 0.70f, 0.93f);
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    float u = x / (float)N, w = y / (float)N;
                    float d = Mathf.SmoothStep(0.46f, 0.80f, Fbm(u * 6f + ox, w * 6f + oy));
                    float dx = u - 0.5f, dy = w - 0.5f, r = Mathf.Sqrt(dx * dx + dy * dy) * 2f;
                    float fall = 1f - Mathf.SmoothStep(0.50f, 0.98f, r);          // fades to nothing at the rim, so the disc edge never shows
                    float tone = 1f - 0.20f * Mathf.SmoothStep(0.55f, 1f, d);      // denser = slightly greyer underside
                    var c = Color.Lerp(sky, new Color(tone, tone, tone), Mathf.Clamp01(d * 1.5f));
                    px[y * N + x] = new Color32((byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), (byte)(Mathf.Clamp01(d * 0.92f * fall) * 255f));
                }
                if ((y & 15) == 15) yield return null;                            // spread the work over many frames: no start-up hitch
            }
            _tex.SetPixels32(px); _tex.Apply(false, true);
            _mat.SetTexture("_BaseMap", _tex);
            _ready = true;
        }

        void LateUpdate()
        {
            if (!_ready || _mr == null) return;
            var c = Camera.main; if (c == null) return;
            var p = c.transform.position;
            float fade = Mathf.InverseLerp(Alt - 100f, Alt - 700f, p.y);              // fully visible below 1.1 km, gone at 1.7 km
            if (fade < 0.02f) { _mr.enabled = false; return; }
            _mr.enabled = true;
            float rad = Mathf.Clamp(c.farClipPlane * 0.9f, 3000f, 16000f);
            if (!Mathf.Approximately(rad, _radius)) { _radius = rad; _mrT.localScale = new Vector3(rad, 1f, rad); }
            _mrT.position = new Vector3(p.x, Alt, p.z);
            _mrT.rotation = Quaternion.Euler(0f, Time.time * 0.12f, 0f);              // slow drift
            if (Time.unscaledTime >= _nextTint)
            {
                _nextTint = Time.unscaledTime + 0.5f;
                var f = RenderSettings.fogColor; float m = Mathf.Max(0.01f, Mathf.Max(f.r, Mathf.Max(f.g, f.b)));
                var hue = new Color(f.r / m, f.g / m, f.b / m);
                var col = Color.Lerp(Color.white, hue, 0.6f) * Mathf.Clamp01(m * 1.25f + 0.1f); col.a = 1f;
                _tint = col;
            }
            var t = _tint; t.a = fade; _mat.SetColor(BaseColor, t);
        }

        Color _tint = Color.white;
    }
}
