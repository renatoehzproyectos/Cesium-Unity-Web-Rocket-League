using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// The Fennec as a static showcase model. glTF space after glTFast import: +X forward, +Y up; the root faces +Z (north at heading 0)
    /// and its wheels' bottom sits on y = 0 (the ground height set in settings).
    /// </summary>
    public static class CarVisual
    {
        // RocketWorld scale (1 uu = 2 cm): the Octane hitbox is 120.5 x 84.2 x 36.2 uu.
        public const float LengthM = 120.5f * 0.02f;
        public const float HeightM = 36.2f * 0.02f;

        public static Transform Build(Transform carRoot, Color body, float bottomY = 0f)
        {
            var root = new GameObject("Visual").transform;
            root.SetParent(carRoot, false);
            var prefab = Resources.Load<GameObject>("Cars/fennec");
            if (prefab == null)
            {
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(box.GetComponent<Collider>());
                box.transform.SetParent(root, false);
                box.transform.localScale = new Vector3(84.2f * 0.02f, HeightM, LengthM);
                box.transform.localPosition = new Vector3(0, HeightM * 0.5f + bottomY, 0);
                SetBodyColor(root, body);
                root.gameObject.AddComponent<BoostFx>();
                return root;
            }
            var model = Object.Instantiate(prefab);
            model.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);   // measure at identity
            var b = Measure(model);
            // Prefer length along the model's longest horizontal axis (glTFast often uses +X forward).
            float modelLen = Mathf.Max(b.size.x, b.size.z, 1e-4f);
            float s = (LengthM * 0.98f) / modelLen;
            // Work out which way the model's nose points from its headlights (falls back to +X forward).
            float noseSign = 1f; Vector3 hl = Vector3.zero; int hn = 0;
            foreach (var r in model.GetComponentsInChildren<Renderer>())
                foreach (var m in r.sharedMaterials)
                    if (m != null && Has(m.name, "Headlight")) { hl += r.bounds.center; hn++; break; }
            if (hn > 0) { float dx = hl.x / hn - b.center.x, dz = hl.z / hn - b.center.z; noseSign = (Mathf.Abs(dx) >= Mathf.Abs(dz) ? dx : dz) < 0f ? -1f : 1f; if (Mathf.Abs(dz) > Mathf.Abs(dx)) noseSign = 0f; }
            Quaternion yaw;
            if (hn > 0 && noseSign == 0f) { float dz = hl.z / hn - b.center.z; yaw = Quaternion.Euler(0f, dz > 0f ? 0f : 180f, 0f); }   // nose along Z
            else yaw = Quaternion.Euler(0f, -90f * (noseSign < 0f ? -1f : 1f), 0f);        // nose along +X -> -90, along -X -> +90
            model.transform.SetParent(root, false);
            model.transform.localRotation = yaw;
            model.transform.localScale = Vector3.one * s;
            var centre = yaw * new Vector3(b.center.x * s, 0f, b.center.z * s);
            model.transform.localPosition = new Vector3(-centre.x, -b.min.y * s + bottomY, -centre.z);   // wheels bottom at bottomY, centred on the root
            foreach (var col in model.GetComponentsInChildren<Collider>()) Object.Destroy(col);
            // Replace glTFast shader-graph materials with URP Lit so the body survives WebGL shader stripping.
            // (Otherwise you can see the boost flame but an invisible car.)
            foreach (var r in model.GetComponentsInChildren<Renderer>())
            {
                r.enabled = true;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                var oldMats = r.sharedMaterials;
                var newMats = new Material[oldMats.Length];
                for (int i = 0; i < oldMats.Length; i++)
                    newMats[i] = MakeUrpLit(oldMats[i], body);
                r.sharedMaterials = newMats;
            }
            // If the GLB produced no visible mesh, fall back to a solid body cube so the car is never invisible.
            if (model.GetComponentsInChildren<Renderer>().Length == 0)
            {
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(box.GetComponent<Collider>());
                box.transform.SetParent(root, false);
                box.transform.localScale = new Vector3(84.2f * 0.02f, HeightM, LengthM);
                box.transform.localPosition = new Vector3(0, HeightM * 0.5f + bottomY, 0);
                var br = box.GetComponent<Renderer>();
                br.sharedMaterial = MakeUrpLit(null, body);
                br.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            root.gameObject.AddComponent<BoostFx>();
            return root;
        }

        /// <summary>URP Lit material that is always included in WebGL builds (unlike glTFast shader graphs).</summary>
        static Material MakeUrpLit(Material source, Color bodyFallback)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            var m = new Material(sh != null ? sh : Shader.Find("Hidden/InternalErrorShader"));
            string n = source != null ? source.name : "Body";
            Color c = bodyFallback;
            float metal = 0.25f, rough = 0.35f;
            Color emissive = Color.black;
            if (Has(n, "Window")) { c = new Color(0.05f, 0.05f, 0.06f); metal = 0f; rough = 0.1f; }
            else if (Has(n, "Headlight")) { c = new Color(0.96f, 0.98f, 1f); metal = 0f; rough = 0.2f; emissive = new Color(0.62f, 0.94f, 1f) * 1.4f; }
            else if (Has(n, "Alpha") || Has(n, "Dieci") || Has(n, "Wheel") || Has(n, "Tire")) { c = new Color(0.08f, 0.08f, 0.09f); metal = 0.1f; rough = 0.7f; }
            else if (IsBody(n)) { c = bodyFallback; metal = 0.25f; rough = 0.3f; }
            else if (source != null)
            {
                // Try to keep a plausible colour from the imported material.
                if (source.HasProperty("baseColorFactor")) c = source.GetColor("baseColorFactor");
                else if (source.HasProperty("_BaseColor")) c = source.GetColor("_BaseColor");
                else if (source.HasProperty("_Color")) c = source.GetColor("_Color");
            }
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            else if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 1f - rough);
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emissive);
            }
            m.name = "RW_" + n;
            return m;
        }

        public static void SetBodyColor(Transform visual, Color c)
        {
            if (visual == null) return;
            foreach (var r in visual.GetComponentsInChildren<Renderer>())
            {
                if (r == null || r.name == "Flame") continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    // Our runtime materials are named RW_<original>; treat body-ish slots as recolourable.
                    bool bodyish = IsBody(m.name) || (Has(m.name, "RW_")
                        && !Has(m.name, "Window") && !Has(m.name, "Headlight")
                        && !Has(m.name, "Wheel") && !Has(m.name, "Tire")
                        && !Has(m.name, "Alpha") && !Has(m.name, "Dieci"));
                    if (bodyish) Prop(m, c, "baseColorFactor", "_BaseColor", "_Color");
                }
            }
        }

        static bool IsBody(string n) => Has(n, "Body");

        static Bounds Measure(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return new Bounds(Vector3.zero, Vector3.one);
            var b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b;
        }

        static void Paint(Material m, Color body)
        {
            string n = m.name;
            if (Has(n, "Window")) Set(m, Color.black, 0f, 0.1f, Color.black);
            else if (Has(n, "Headlight")) Set(m, new Color(0.96f, 0.98f, 1f), 0f, 0.2f, new Color(0.62f, 0.94f, 1f) * 1.4f);
            else if (Has(n, "Alpha")) Set(m, new Color(0.04f, 0.04f, 0.05f), 0.5f, 0.4f, Color.black);
            else if (Has(n, "Dieci")) Set(m, new Color(0.08f, 0.08f, 0.09f), 0f, 0.95f, Color.black);
            else if (IsBody(n)) Set(m, body, 0.25f, 0.3f, Color.black);
            else Set(m, new Color(0.1f, 0.1f, 0.11f), 0.3f, 0.4f, Color.black);
        }

        static bool Has(string s, string part) => s.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0;

        // glTFast's shader graph exposes baseColorFactor/metallicFactor/roughnessFactor/emissiveFactor; URP Lit uses _BaseColor/_Metallic/_Smoothness/_EmissionColor.
        static void Set(Material m, Color c, float metal, float rough, Color emissive)
        {
            Prop(m, c, "baseColorFactor", "_BaseColor", "_Color");
            Prop(m, emissive, "emissiveFactor", "_EmissionColor");
            if (m.HasProperty("metallicFactor")) m.SetFloat("metallicFactor", metal); else if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
            if (m.HasProperty("roughnessFactor")) m.SetFloat("roughnessFactor", rough); else if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 1f - rough);
            if (m.HasProperty("baseColorTexture")) m.SetTexture("baseColorTexture", null);
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", null);
        }
        static void Prop(Material m, Color c, params string[] names) { foreach (var n in names) if (m.HasProperty(n)) { m.SetColor(n, c); return; } }
    }
}
