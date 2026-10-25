using System;

namespace RocketWorld.Sim
{
    /// <summary>
    /// Engine-free numbers for the cinematic post-processing (bloom, depth of field, motion blur), so the look can be tuned and unit-tested
    /// without Unity. <c>CinematicFx</c> feeds these into the URP Volume every frame. Each effect has an <c>amount</c> (the settings slider):
    /// 0 = off, 1 = the designed look, up to 2 = twice as strong.
    /// </summary>
    public static class CinematicPolicy
    {
        public const float MaxAmount = 2f;
        /// <summary>Below this a slider counts as "off" and the effect is not even rendered.</summary>
        public const float OffBelow = 0.01f;

        static float Clamp01(float v) => Math.Max(0f, Math.Min(1f, v));
        static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float ClampAmount(float amount) => Math.Max(0f, Math.Min(MaxAmount, amount));
        public static bool IsOn(float amount) => amount > OffBelow;

        /// <summary>0 = night .. 1 = day, from the hour (same sun model as GameRoot.ApplyTimeOfDay).</summary>
        public static float Daylight(float timeOfDay)
        {
            double elevDeg = Math.Sin((timeOfDay - 6.0) / 12.0 * Math.PI) * 68.0;
            float h = (float)Math.Sin(elevDeg * Math.PI / 180.0);
            return Clamp01((h + 0.1f) / 0.4f);
        }

        public static float BloomThreshold(bool hdr) => hdr ? 1.0f : 0.85f;
        /// <summary>Stronger glow at night (lights) and while boosting, scaled by the slider.</summary>
        public static float BloomIntensity(float daylight, bool boosting, float amount = 1f)
            => (Lerp(0.55f, 0.30f, daylight) + (boosting ? 0.20f : 0f)) * ClampAmount(amount);

        /// <summary>Gaussian depth of field keeps everything near the car sharp and softens the far distance. Distances in metres from the camera.</summary>
        public static float DofStart(float cameraToCar) => Math.Max(120f, cameraToCar * 3.5f);
        public static float DofEnd(float start) => start + 700f;
        /// <summary>Blur strength of the far distance (URP's Max Radius, 0.5 .. 1.5): 0.9 at the default look.</summary>
        public static float DofMaxRadius(float amount) => Math.Max(0.5f, Math.Min(1.5f, 0.5f + 0.4f * ClampAmount(amount)));

        /// <summary>Camera motion blur: none when slow, up to ~0.65 at top speed, a little more while boosting; scaled by the slider, never above 1.</summary>
        public static float MotionBlurIntensity(float speedMs, bool boosting, float amount = 1f)
        {
            float t = Clamp01((speedMs - 12f) / (46f - 12f));
            float v = Math.Min(0.8f, 0.65f * t + (boosting ? 0.10f * t : 0f));
            return Math.Min(1f, v * ClampAmount(amount));
        }
    }
}
