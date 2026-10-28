using System.Collections.Generic;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// The 3-D model of a <see cref="VehicleSpec"/> under the car's visual: loads the GLB (Resources/Models/Vehicles, made by tools/models/prep.mjs),
    /// fixes materials and orientation (from the Marker_* nodes baked into the model), and animates what the physics reports in a <see cref="VehicleViz"/>:
    /// landing gear, folding DeLorean wheels, procedural helicopter rotors, afterburner / hover glow.
    /// </summary>
    public sealed class VehicleVisual : MonoBehaviour
    {
        public Transform ModelRoot { get; private set; }
        VehicleSpec _s;
        Transform _gearDown, _gearUp, _gear, _mainRotor, _tailRotor;
        readonly List<Wheel> _wheels = new List<Wheel>();
        readonly List<Transform> _flames = new List<Transform>(), _glows = new List<Transform>(); readonly List<Vector3> _flameBase = new List<Vector3>();
        float _rotorAngle, _spin;

        sealed class Wheel { public Transform node, spin; public Quaternion baseInRoot; public Vector3 axleLocal; public float side, radius; }

        public static VehicleVisual Build(Transform parent, VehicleSpec s)
        {
            var go = new GameObject("Vehicle_" + s.name); go.transform.SetParent(parent, false);
            var v = go.AddComponent<VehicleVisual>(); v._s = s; v.Create();
            return v;
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++) { var r = FindDeep(t.GetChild(i), name); if (r != null) return r; }
            return null;
        }

        void Create()
        {
            var prefab = Resources.Load<GameObject>(_s.model);
            if (prefab != null)
            {
                var m = Instantiate(prefab, transform); m.name = "Model"; ModelRoot = m.transform;
                PlaneIntro.FixMaterials(m);
                foreach (var c in m.GetComponentsInChildren<Collider>(true)) Destroy(c);
                Orient(m.transform);
                _gearDown = Find("Gear_Down"); _gearUp = Find("Gear_Up"); _gear = Find("Gear");
                foreach (var n in new[] { "F0", "F1", "R0", "R1" })
                {
                    var w = Find("Wheel_" + n); if (w == null) continue;
                    var sp = w.childCount > 0 ? w.GetChild(0) : null;
                    var wh = new Wheel { node = w, spin = sp, baseInRoot = Quaternion.Inverse(transform.rotation) * w.rotation, side = transform.InverseTransformPoint(w.position).x };
                    wh.axleLocal = Quaternion.Inverse(w.rotation) * (transform.rotation * Vector3.right);
                    wh.radius = n[0] == 'F' ? 0.30f : 0.33f;
                    _wheels.Add(wh);
                }
            }
            else
            {
                Debug.LogWarning("[RocketWorld] model not found: " + _s.model + " (showing the collision boxes)");
                ModelRoot = new GameObject("Model").transform; ModelRoot.SetParent(transform, false);
                foreach (var b in _s.boxes)
                {
                    var c = GameObject.CreatePrimitive(PrimitiveType.Cube); Destroy(c.GetComponent<Collider>());
                    c.transform.SetParent(ModelRoot, false); c.transform.localPosition = b.center; c.transform.localScale = b.size;
                }
                ModelRoot.localPosition = -_s.com;
            }
            if (_s.rotors) BuildRotors();
            if (_s.nozzles != null) foreach (var n in _s.nozzles) { _flameBase.Add(n - _s.com); _flames.Add(Glow("Flame", n - _s.com, new Color(1f, 0.55f, 0.15f), new Vector3(0.7f, 0.7f, 2.6f))); }
            if (_s.hoverCar) foreach (var w in _wheels) _glows.Add(Glow("HoverGlow", transform.InverseTransformPoint(w.node.position) + new Vector3(0, -0.12f, 0), new Color(0.35f, 0.8f, 1f), new Vector3(0.55f, 0.06f, 0.55f)));
        }

        Transform Find(string n) => ModelRoot == null ? null : FindDeep(ModelRoot, n);

        /// <summary>Rotate the model so its Marker_Nose is +Z and its Marker_Up is +Y (the importer's axis convention is not relied on), and shift the CG onto the body origin.</summary>
        void Orient(Transform model)
        {
            var o = FindDeep(model, "Marker_Origin"); var n = FindDeep(model, "Marker_Nose"); var u = FindDeep(model, "Marker_Up");
            Quaternion fix = Quaternion.identity; Vector3 origin = Vector3.zero;
            if (o != null && n != null && u != null)
            {
                origin = model.InverseTransformPoint(o.position);
                Vector3 nose = model.InverseTransformPoint(n.position) - origin, up = model.InverseTransformPoint(u.position) - origin;
                if (nose.sqrMagnitude > 1e-6f && up.sqrMagnitude > 1e-6f) fix = Quaternion.Inverse(Quaternion.LookRotation(nose.normalized, up.normalized));
            }
            else Debug.LogWarning("[RocketWorld] vehicle model without orientation markers: " + _s.model);
            model.localRotation = fix;
            model.localPosition = -(fix * origin) - _s.com;
        }

        Transform Glow(string name, Vector3 pos, Color col, Vector3 scale)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Sphere); g.name = name; Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(transform, false); g.transform.localPosition = pos; g.transform.localScale = scale;
            var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var mat = new Material(sh); mat.color = col; if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", col);
            g.GetComponent<Renderer>().sharedMaterial = mat; g.SetActive(false);
            return g.transform;
        }

        void BuildRotors()
        {
            var dark = new Color(0.08f, 0.08f, 0.09f);
            _mainRotor = new GameObject("MainRotor").transform; _mainRotor.SetParent(transform, false); _mainRotor.localPosition = _s.hub - _s.com;
            for (int i = 0; i < 4; i++) Blade(_mainRotor, i * 90f, _s.rotorRadius, 0.34f, 0.03f, dark, true);
            _tailRotor = new GameObject("TailRotor").transform; _tailRotor.SetParent(transform, false); _tailRotor.localPosition = _s.tailRotor - _s.com;
            for (int i = 0; i < 2; i++) Blade(_tailRotor, i * 90f, _s.tailRadius, 0.14f, 0.02f, dark, false);
        }

        void Blade(Transform parent, float angle, float length, float chord, float thick, Color col, bool horizontal)
        {
            var pivot = new GameObject("Blade").transform; pivot.SetParent(parent, false);
            pivot.localRotation = horizontal ? Quaternion.Euler(0, angle, 0) : Quaternion.Euler(angle, 0, 0);
            var c = GameObject.CreatePrimitive(PrimitiveType.Cube); Destroy(c.GetComponent<Collider>()); c.transform.SetParent(pivot, false);
            c.transform.localScale = new Vector3(length, thick, chord);                          // horizontal rotor: blade along local X
            c.transform.localPosition = new Vector3(length * 0.5f, 0, 0);
            var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(sh); mat.color = col; if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", col);
            c.GetComponent<Renderer>().sharedMaterial = mat;
            if (!horizontal) { c.transform.localScale = new Vector3(thick, length, chord); c.transform.localPosition = new Vector3(0, length * 0.5f, 0); }
        }

        /// <summary>Apply the physics' visual state; called every frame by the driver.</summary>
        public void Tick(in VehicleViz v, float dt)
        {
            if (_gearDown != null) _gearDown.gameObject.SetActive(v.gear > 0.5f);
            if (_gearUp != null) _gearUp.gameObject.SetActive(v.gear <= 0.5f);
            if (_gear != null) _gear.gameObject.SetActive(v.gear > 0.5f);
            Quaternion rootRot = transform.rotation;
            foreach (var w in _wheels)
            {
                float ang = (w.side >= 0f ? -90f : 90f) * v.fold;                                  // outer face turns to point down
                w.node.rotation = rootRot * Quaternion.AngleAxis(ang, Vector3.forward) * w.baseInRoot;
                if (w.spin != null) { _spin += v.wheelSpeed / Mathf.Max(0.1f, w.radius) * Mathf.Rad2Deg * dt / _wheels.Count; w.spin.localRotation = Quaternion.AngleAxis(_spin, w.axleLocal); }
            }
            foreach (var g in _glows) { g.gameObject.SetActive(v.fold > 0.6f); }
            if (_mainRotor != null)
            {
                _rotorAngle += v.rotorRpm * 1100f * dt;
                _mainRotor.localRotation = Quaternion.Euler(0, _rotorAngle, 0);
                _tailRotor.localRotation = Quaternion.Euler(_rotorAngle * 2.4f, 0, 0);
            }
            for (int i = 0; i < _flames.Count; i++)
            {
                var f = _flames[i]; bool on = v.burner > 0.05f; if (f.gameObject.activeSelf != on) f.gameObject.SetActive(on);
                if (!on) continue;
                float len = 1f + 3.2f * v.burner; f.localScale = new Vector3(0.6f, 0.6f, len); f.localPosition = _flameBase[i] + Vector3.back * (0.5f * len);
            }
        }
    }
}
