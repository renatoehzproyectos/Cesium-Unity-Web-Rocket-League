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
                AddFlame(root);
                return root;
            }
            var model = Object.Instantiate(prefab);
            model.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);   // measure at identity
            var b = Measure(model);
            float s = (LengthM * 0.98f) / Mathf.Max(b.size.x, 1e-4f);
            var yaw = Quaternion.Euler(0f, -90f, 0f);                                      // model +X -> car +Z
            model.transform.SetParent(root, false);
            model.transform.localRotation = yaw;
            model.transform.localScale = Vector3.one * s;
            var centre = yaw * new Vector3(b.center.x * s, 0f, b.center.z * s);
            model.transform.localPosition = new Vector3(-centre.x, -b.min.y * s + bottomY, -centre.z);   // wheels bottom at bottomY, centred on the root
            foreach (var col in model.GetComponentsInChildren<Collider>()) Object.Destroy(col);
            foreach (var r in model.GetComponentsInChildren<Renderer>())
            {
                var mats = r.materials;                                                     // instances, safe to edit
                for (int i = 0; i < mats.Length; i++) Paint(mats[i], body);
                r.materials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            AddFlame(root);
            return root;
        }

        // Boost flame: stretched sphere + point light behind the car. The material is a Resources asset so its shader survives stripping.
        static void AddFlame(Transform root)
        {
            var mat = Resources.Load<Material>("Materials/Flame");
            var f = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            f.name = "Flame";
            Object.Destroy(f.GetComponent<Collider>());
            f.transform.SetParent(root, false);
            f.transform.localPosition = new Vector3(0f, 0.05f - 0.34f + 0.34f, -LengthM * 0.5f - 0.7f);
            f.transform.localScale = new Vector3(0.45f, 0.45f, 1.6f);
            var r = f.GetComponent<Renderer>();
            if (mat != null) r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var l = new GameObject("FlameLight").AddComponent<Light>();
            l.transform.SetParent(f.transform, false);
            l.type = LightType.Point; l.color = new Color(1f, 0.69f, 0.13f); l.range = 14f; l.intensity = 2.2f; l.shadows = LightShadows.None;
            f.SetActive(false);
        }

        public static void SetBodyColor(Transform visual, Color c)
        {
            if (visual == null) return;
            foreach (var r in visual.GetComponentsInChildren<Renderer>())
                foreach (var m in r.sharedMaterials)
                    if (m != null && IsBody(m.name)) Prop(m, c, "baseColorFactor", "_BaseColor", "_Color");
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
