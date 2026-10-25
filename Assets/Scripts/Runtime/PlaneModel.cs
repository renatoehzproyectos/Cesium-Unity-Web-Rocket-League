using RocketWorld.Sim;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>The BoxPlane's JSBSim files (Resources/Flight) and the JSBSim body frame (x forward, y right, z down, feet) to Unity local frame (x right, y up, z forward, metres).</summary>
    public static class PlaneModel
    {
        public const float FtToM = 0.3048f, LbfToN = 4.44822f, LbfFtToNm = 1.35582f, SlugToKg = 14.5939f;
        static string _ac, _eng, _prop;

        public static JsbAircraft Load()
        {
            if (_ac == null)
            {
                _ac = Resources.Load<TextAsset>("Flight/boxplane").text;
                _eng = Resources.Load<TextAsset>("Flight/eng_electric160").text;
                _prop = Resources.Load<TextAsset>("Flight/prop_75in2f").text;
            }
            return JsbAircraft.Load(_ac, _eng, _prop);
        }

        /// <summary>Position relative to the CG, structural inches (x aft, y right, z up) to Unity local metres.</summary>
        public static Vector3 Local(JsbAircraft a, double sx, double sy, double sz) => Local(a.StructuralToBody(new D3(sx, sy, sz)));
        public static Vector3 Local(D3 b) => new Vector3((float)b.y, (float)-b.z, (float)b.x) * FtToM;      // polar vector: body (fwd,right,down) -> Unity (right,up,fwd)
        public static Vector3 ForceToUnity(D3 f) => new Vector3((float)f.y, (float)-f.z, (float)f.x) * LbfToN;
        /// <summary>Axial vector (moments, rates): the body->Unity map is a reflection, so axial vectors flip sign.</summary>
        public static Vector3 MomentToUnity(D3 m) => new Vector3((float)-m.y, (float)m.z, (float)-m.x) * LbfFtToNm;
        public static D3 VelocityToBody(Vector3 local) => new D3(local.z, local.x, -local.y) * (1.0 / FtToM);
        public static D3 RatesToBody(Vector3 local) => new D3(-local.z, -local.x, local.y);
    }

    /// <summary>The airplane built from simple boxes, placed from the JSBSim structural coordinates so it matches the model's CG, gear and propeller.</summary>
    public static class PlaneVisual
    {
        public static Transform Build(Transform parent, JsbAircraft a, out Transform prop)
        {
            var root = new GameObject("PlaneVisual").transform; root.SetParent(parent, false);
            Color body = new Color(0.92f, 0.92f, 0.94f), accent = new Color(0.85f, 0.15f, 0.12f), glass = new Color(0.25f, 0.45f, 0.65f), dark = new Color(0.12f, 0.12f, 0.13f);
            // structural inches: x aft from the nose datum, y right, z up. Box(centre, size) with sizes in inches along (x aft, y right, z up).
            Box(root, a, "Fuselage", 100, 0, 36, 190, 40, 40, body);
            Box(root, a, "Tail boom", 205, 0, 44, 90, 14, 20, body);
            Box(root, a, "Cowling", -12, 0, 30, 36, 38, 36, accent);
            Box(root, a, "Cabin glass", 52, 0, 56, 56, 38, 14, glass);
            Box(root, a, "Wing", 43, 0, 62, 59, 432, 4, body);
            Box(root, a, "Wing stripe", 43, 0, 62.3f, 59, 432, 0.6f, accent);
            Box(root, a, "Wing strut L", 50, -55, 40, 4, 3, 45, dark, 0, 0, 30); Box(root, a, "Wing strut R", 50, 55, 40, 4, 3, 45, dark, 0, 0, -30);
            Box(root, a, "Htail", 235, 0, 50, 33, 96, 3, body);
            Box(root, a, "Vtail", 232, 0, 74, 40, 3, 52, accent);
            Box(root, a, "Rudder", 252, 0, 74, 10, 3, 50, body);
            // propeller: a spinner box and two blades on one transform that PlaneDriver spins about the forward axis
            var pl = PlaneModel.Local(a, -37.7, 0, 26.6);
            var p = new GameObject("Prop").transform; p.SetParent(root, false); p.localPosition = pl; prop = p;
            Cube(p, "Spinner", Vector3.zero, new Vector3(0.14f, 0.14f, 0.12f), dark);
            Cube(p, "Blade A", Vector3.zero, new Vector3(0.09f, 1.9f, 0.025f), dark);
            Cube(p, "Blade B", Vector3.zero, new Vector3(1.9f, 0.09f, 0.025f), dark);
            // fixed gear: wheel under each contact point, strut up to the fuselage
            foreach (var g in a.gears)
            {
                var c = PlaneModel.Local(a, g.locIn.x, g.locIn.y, g.locIn.z);
                float r = 0.2f;
                Cube(root, "Wheel", c + new Vector3(0, r, 0), new Vector3(0.16f, r * 2, r * 2), dark);
                Cube(root, "Strut", c + new Vector3(0, r + 0.35f, 0), new Vector3(0.05f, 0.7f, 0.05f), dark);
            }
            return root;
        }

        static void Box(Transform parent, JsbAircraft a, string name, float sx, float sy, float sz, float lx, float ly, float lz, Color c, float rx = 0, float ry = 0, float rz = 0)
        {
            // inches -> metres; (x aft, y right, z up) -> Unity (right, up, forward) with forward = -x_structural
            const float k = 0.0254f;
            var cube = Cube(parent, name, PlaneModel.Local(a, sx, sy, sz), new Vector3(ly * k, lz * k, lx * k), c);
            if (rx != 0 || ry != 0 || rz != 0) cube.localRotation = Quaternion.Euler(0, 0, rz);
        }

        static Transform Cube(Transform parent, string name, Vector3 pos, Vector3 size, Color c)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name;
            Object.Destroy(go.GetComponent<Collider>());                  // visual only: the physics colliders are made by PlaneDriver
            go.transform.SetParent(parent, false); go.transform.localPosition = pos; go.transform.localScale = size;
            var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Universal Render Pipeline/Simple Lit") ?? Shader.Find("Sprites/Default");
            var m = new Material(sh) { name = "RW_Plane_" + name };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c); if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            go.GetComponent<Renderer>().sharedMaterial = m;
            return go.transform;
        }
    }
}
