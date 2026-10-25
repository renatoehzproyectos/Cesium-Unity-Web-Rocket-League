using System;

namespace RocketWorld.Sim
{
    /// <summary>
    /// Engine-free numbers for the cinematic post-processing (bloom, colour grade, depth of field, motion blur, edge blur), so the look can be
    /// tuned and unit-tested without Unity. <c>CinematicFx</c> feeds these into the URP Volume every frame.
    /// </summary>
    public static class CinematicPolicy
    {
        // Each effect is its own bit so a misbehaving one can be switched off alone (settings panel, ?fx=<mask>).
        public const int Bloom = 1, Grade = 2, Edge = 4, Dof = 8, MotionBlur = 16;
        public const int Light = Bloom | Grade;                       // standard URP effects only
        public const int Full = Light | Dof | MotionBlur;
        /// <summary>Preset 0 = off, 1 = light, 2 = full. The custom edge blur is a separate toggle until it has been verified on devices.</summary>
        public static int PresetMask(int level) => level <= 0 ? 0 : level == 1 ? Light : Full;

        static float Clamp01(float v) => Math.Max(0f, Math.Min(1f, v));
        static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        static float Sp(float speedMs) => Clamp01(speedMs / 46f);        // 46 m/s = the car's top speed

        /// <summary>0 = night .. 1 = day, from the hour (same sun model as GameRoot.ApplyTimeOfDay).</summary>
        public static float Daylight(float timeOfDay)
        {
            double elevDeg = Math.Sin((timeOfDay - 6.0) / 12.0 * Math.PI) * 68.0;
            float h = (float)Math.Sin(elevDeg * Math.PI / 180.0);
            return Clamp01((h + 0.1f) / 0.4f);
        }

        public static float BloomThreshold(bool hdr) => hdr ? 1.0f : 0.85f;
        /// <summary>Stronger glow at night (lights) and while boosting.</summary>
        public static float BloomIntensity(float daylight, bool boosting) => Lerp(0.55f, 0.30f, daylight) + (boosting ? 0.20f : 0f);

        /// <summary>Colour grade: a little punch and saturation by day (photogrammetry looks flat), a moodier grade at night.</summary>
        public static float Saturation(float daylight) => Lerp(2f, 14f, daylight);
        public static float Contrast(float daylight) => Lerp(14f, 10f, daylight);

        /// <summary>Gaussian depth of field keeps everything near the car sharp and softens the far distance. Distances in metres from the camera.</summary>
        public static float DofStart(float cameraToCar) => Math.Max(120f, cameraToCar * 3.5f);
        public static float DofEnd(float start) => start + 700f;

        /// <summary>Camera motion blur: none when slow, up to ~0.65 at top speed, a little more while boosting.</summary>
        public static float MotionBlurIntensity(float speedMs, bool boosting)
        {
            float t = Clamp01((speedMs - 12f) / (46f - 12f));
            return Math.Min(0.8f, 0.65f * t + (boosting ? 0.10f * t : 0f));
        }

        /// <summary>Blur of the viewport borders: always present, tighter (stronger) the faster you go.</summary>
        public static float EdgeBlurStrength(float speedMs) => 0.6f + 0.4f * Sp(speedMs);
    }
}
