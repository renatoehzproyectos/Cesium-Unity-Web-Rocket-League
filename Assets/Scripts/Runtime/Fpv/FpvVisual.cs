using UnityEngine;

namespace RocketWorld
{
    /// <summary>A small procedural quad (X frame, four motors with spinning props, a camera pod) built from primitives; no assets needed. Nose = +z.</summary>
    public sealed class FpvVisual : MonoBehaviour
    {
        Transform[] _props = new Transform[4];
        Renderer[] _all;
        readonly float[] _phase = new float[4];

        public static FpvVisual Build(Transform parent, float wheelbaseM, float propDiameterM)
        {
            var go = new GameObject("FpvDrone"); go.transform.SetParent(parent, false);
            var v = go.AddComponent<FpvVisual>();
            float arm = wheelbaseM / 2f / Mathf.Sqrt(2f); float pr = Mathf.Max(0.02f, propDiameterM * 0.5f);
            var carbon = Mat(new Color(0.08f, 0.08f, 0.09f)); var accent = Mat(new Color(1f, 0.45f, 0.1f)); var prop = Mat(new Color(0.9f, 0.92f, 1f, 1f));
            Prim(go.transform, PrimitiveType.Cube, new Vector3(arm * 0.9f, arm * 0.35f, arm * 1.3f), Vector3.zero, carbon);          // body stack
            Prim(go.transform, PrimitiveType.Cube, new Vector3(arm * 0.55f, arm * 0.4f, arm * 0.5f), new Vector3(0, arm * 0.1f, arm * 0.75f), accent);   // camera pod (front)
            // sim motor order M1 RR, M2 FR, M3 RL, M4 FL; model +z is the nose, so front = +z
            Vector2[] m = { new Vector2(arm, -arm), new Vector2(arm, arm), new Vector2(-arm, -arm), new Vector2(-arm, arm) };
            for (int i = 0; i < 4; i++)
            {
                var a = Prim(go.transform, PrimitiveType.Cube, new Vector3(0.012f + arm * 0.05f, 0.012f, arm * 1.45f), new Vector3(m[i].x * 0.5f, 0, m[i].y * 0.5f), carbon);
                a.localRotation = Quaternion.Euler(0, Mathf.Atan2(m[i].x, m[i].y) * Mathf.Rad2Deg, 0);
                Prim(go.transform, PrimitiveType.Cylinder, new Vector3(arm * 0.28f, arm * 0.12f, arm * 0.28f), new Vector3(m[i].x, arm * 0.1f, m[i].y), accent);
                var p = Prim(go.transform, PrimitiveType.Cylinder, new Vector3(pr * 2f, 0.002f, pr * 2f), new Vector3(m[i].x, arm * 0.3f, m[i].y), prop);
                v._props[i] = p;
            }
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) Destroy(c);             // the model collides through FpvWorld, never through these
            v._all = go.GetComponentsInChildren<Renderer>(true);
            return v;
        }

        static Material Mat(Color c)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Universal Render Pipeline/Simple Lit") ?? Shader.Find("Standard");
            var m = new Material(sh) { color = c };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            return m;
        }

        static Transform Prim(Transform parent, PrimitiveType t, Vector3 scale, Vector3 pos, Material m)
        {
            var g = GameObject.CreatePrimitive(t); g.transform.SetParent(parent, false);
            g.transform.localScale = scale; g.transform.localPosition = pos; g.GetComponent<Renderer>().sharedMaterial = m;
            g.layer = PhysicsSetup.CarLayer;
            return g.transform;
        }

        public void SetVisible(bool on) { if (_all != null) foreach (var r in _all) if (r != null) r.enabled = on; }

        /// <summary>Props turn with the motor outputs (0..1); the pairs spin opposite ways like the real props (M1 CW, M2 CCW, M3 CCW, M4 CW).</summary>
        public void Spin(double[] motors, float dt)
        {
            float[] dir = { 1, -1, -1, 1 };
            for (int i = 0; i < 4; i++)
            {
                _phase[i] += dir[i] * (float)motors[i] * 4000f * dt;
                if (_props[i] != null) _props[i].localRotation = Quaternion.Euler(0, _phase[i], 0);
            }
        }
    }
}
