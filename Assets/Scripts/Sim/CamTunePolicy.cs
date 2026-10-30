using System;

namespace RocketWorld.Sim
{
    /// <summary>
    /// Numbers behind the camera menu (engine-free, unit-tested): limits of the six offsets, the follow-delay scaling and the +/- button steps.
    /// Follow delay is a multiplier of the built-in camera lag: 1 = the original feel, 0 = rigidly attached, 3 = three times lazier.
    /// </summary>
    public static class CamTunePolicy
    {
        public const float MaxTurnH = 90f, MaxTurnV = 60f, MaxMove = 40f, MaxFov = 40f;   // deg, deg, m, deg
        public const float MaxDelay = 3f, DefaultDelay = 1f;
        public const float MinFov = 20f, MaxFovAbs = 120f;

        public static float Clamp(float v, float limit) => Math.Max(-limit, Math.Min(limit, v));
        public static float ClampDelay(float d) => Math.Max(0f, Math.Min(MaxDelay, d));

        /// <summary>A smoothing rate (1/s) stretched by the follow delay. Delay 0 is effectively instant.</summary>
        public static float Rate(float baseRate, float delay) => delay <= 0.02f ? 1000f : baseRate / delay;

        /// <summary>Frame-rate independent blend factor for an exponential follow with the given rate.</summary>
        public static float Blend(float rate, float dt) => 1f - (float)Math.Exp(-rate * dt);

        /// <summary>Field of view with the user's offset added, kept inside a sane range.</summary>
        public static float Fov(float baseFov, float offset) => Math.Max(MinFov, Math.Min(MaxFovAbs, baseFov + offset));

        /// <summary>A +/- button held down: after <paramref name="holdAfter"/> s it moves <paramref name="perSec"/> per second (the first tap is <see cref="TapStep"/>).</summary>
        public static float HoldStep(float value, int dir, float heldFor, float dt, float perSec, float holdAfter, float limit)
        {
            float d = heldFor > holdAfter ? perSec * dt : 0f;
            return Clamp(value + dir * d, limit);
        }
        public static float TapStep(float value, int dir, float tap, float limit) => Clamp(value + dir * tap, limit);
    }
}
