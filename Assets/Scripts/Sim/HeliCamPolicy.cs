using System;

namespace RocketWorld.Sim
{
    /// <summary>
    /// Numbers behind the helicopter chase camera (engine-free, unit-tested): how high and how far behind the car it hovers, how far ahead it looks
    /// and its field of view. Looking at the car from ~38 m up and ~14 m behind gives the steep (~70 degree) top-down view along the street;
    /// it climbs and pulls back with speed so the road ahead stays on screen. <c>zoom</c> (0.5 .. 2.5) scales height and distance together.
    /// </summary>
    public static class HeliCamPolicy
    {
        public const float MinZoom = 0.5f, MaxZoom = 2.5f;

        static float Sp(float speedMs) => Math.Max(0f, Math.Min(1f, speedMs / 46f));   // 46 m/s = the car's top speed; planes just saturate it
        public static float ClampZoom(float zoom) => Math.Max(MinZoom, Math.Min(MaxZoom, zoom));

        /// <summary>Metres above the car.</summary>
        public static float Height(float speedMs, bool plane, float zoom) => ((plane ? 50f : 38f) + (plane ? 15f : 22f) * Sp(speedMs)) * ClampZoom(zoom);
        /// <summary>Metres behind the car (along its heading), measured on the ground plane.</summary>
        public static float Back(float speedMs, bool plane, float zoom) => ((plane ? 42f : 14f) + (plane ? 18f : 12f) * Sp(speedMs)) * ClampZoom(zoom);
        /// <summary>Metres ahead of the car the camera aims at (0 = exactly at the car; more = the car sits lower on screen, more road ahead).</summary>
        public static float LookAhead(float speedMs, bool plane) => (plane ? 20f : 3f) + (plane ? 10f : 8f) * Sp(speedMs);
        /// <summary>Vertical field of view in degrees.</summary>
        public static float Fov(float speedMs) => 70f + 10f * Sp(speedMs);
        /// <summary>Downward tilt of the line from the camera to the car, in degrees.</summary>
        public static float PitchDeg(float height, float back) => (float)(Math.Atan2(height, Math.Max(0.01f, back)) * 180.0 / Math.PI);
    }
}
