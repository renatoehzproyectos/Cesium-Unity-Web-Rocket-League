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

}
