using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>Implemented by the multiplayer layer (Assets/Net). Null in the offline game.</summary>
    public interface INetRole
    {
        /// <summary>True on a joined (non-host) player: the car is a puppet driven by host snapshots.</summary>
        bool IsClientOnly { get; }
        /// <summary>Ask the host to put the ball in front of this player's car.</summary>
        void RequestBall();
    }

    /// <summary>
    /// Frame-independent pose transport. Every machine has its own Unity origin (Cesium shifts it as the car drives), so poses travel as
    /// Earth-Centered Earth-Fixed doubles + ECEF direction vectors (forward / up / velocity) and are converted with the local georeference.
    /// </summary>
    public static class GeoSync
    {
        static Vector3 V(double3 d) => new Vector3((float)d.x, (float)d.y, (float)d.z);
        static double3 D(Vector3 v) => new double3(v.x, v.y, v.z);

        public static void ToEcef(CesiumGeoreference g, Vector3 pos, Quaternion rot, Vector3 vel,
            out double x, out double y, out double z, out Vector3 f, out Vector3 u, out Vector3 v)
        {
            var p = g.TransformUnityPositionToEarthCenteredEarthFixed(new double3(pos.x, pos.y, pos.z));
            x = p.x; y = p.y; z = p.z;
            f = V(g.TransformUnityDirectionToEarthCenteredEarthFixed(D(rot * Vector3.forward)));
            u = V(g.TransformUnityDirectionToEarthCenteredEarthFixed(D(rot * Vector3.up)));
            v = V(g.TransformUnityDirectionToEarthCenteredEarthFixed(D(vel)));
        }

        public static void FromEcef(CesiumGeoreference g, double x, double y, double z, Vector3 f, Vector3 u, Vector3 v,
            out Vector3 pos, out Quaternion rot, out Vector3 vel)
        {
            var p = g.TransformEarthCenteredEarthFixedPositionToUnity(new double3(x, y, z));
            pos = new Vector3((float)p.x, (float)p.y, (float)p.z);
            var fw = V(g.TransformEarthCenteredEarthFixedDirectionToUnity(D(f)));
            var up = V(g.TransformEarthCenteredEarthFixedDirectionToUnity(D(u)));
            vel = V(g.TransformEarthCenteredEarthFixedDirectionToUnity(D(v)));
            rot = fw.sqrMagnitude > 1e-6f && up.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(fw, up) : Quaternion.identity;
        }

        /// <summary>Move the Unity origin to an ECEF point (first snapshot of a joining player, so floats stay precise).</summary>
        public static void Recenter(CesiumGeoreference g, double x, double y, double z) { g.SetOriginEarthCenteredEarthFixed(x, y, z); }
    }
}
