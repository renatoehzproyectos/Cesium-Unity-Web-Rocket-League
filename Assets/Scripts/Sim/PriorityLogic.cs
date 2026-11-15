using System;

namespace RocketWorld.Sim
{
    /// <summary>What the player wants the game to favour. Several can be on at once; opposing ones (quality vs fps) partly cancel.</summary>
    [Flags]
    public enum Priority
    {
        None = 0,
        FastLoad = 1,      // tiles arrive as fast as possible
        TileQuality = 2,   // tiles look as good as possible
        Fps = 4,           // as many frames per second as possible
        Realism = 8,       // best quality of everything (tiles, resolution, effects, view distance)
        All = FastLoad | TileQuality | Fps | Realism,
    }

    /// <summary>Multipliers applied on top of the user's own sliders (so the sliders stay the base values). Engine-free, unit-tested.</summary>
    public struct PriorityMods
    {
        public float sse, scale, far, radius, distantQ, fx, culledSse, loads;
        public float fxFloor;     // minimum effect amount (only used when no anti-crash override is active)
        public float turboSse;    // first SSE of the "coarse first" load
        public static PriorityMods Identity => new PriorityMods { sse = 1, scale = 1, far = 1, radius = 1, distantQ = 1, fx = 1, culledSse = 1, loads = 1, fxFloor = 0f, turboSse = 48f };
    }

    public static class PriorityLogic
    {
        public const float BaseCulledSse = 128f;

        public static PriorityMods Compute(Priority p, bool mobile)
        {
            var m = PriorityMods.Identity;
            if ((p & Priority.FastLoad) != 0)
            {   // coarse first, more requests in flight, a smaller full-detail ring, coarser tiles off-screen
                m.turboSse = 64f; m.loads *= 1.5f; m.radius *= 0.85f; m.distantQ *= 0.9f; m.culledSse *= 1.25f;
            }
            if ((p & Priority.TileQuality) != 0)
            {   // sharper tiles everywhere
                m.sse *= 0.7f; m.culledSse *= 0.75f; m.distantQ *= 1.3f; m.radius *= 1.15f;
            }
            if ((p & Priority.Fps) != 0)
            {   // lighter rendering
                m.sse *= 1.4f; m.scale *= 0.75f; m.far *= 0.7f; m.radius *= 0.75f; m.distantQ *= 0.8f; m.fx *= 0.4f; m.culledSse *= 1.2f;
            }
            if ((p & Priority.Realism) != 0)
            {   // everything as good as the device allows
                m.sse *= 0.8f; m.scale *= 1.15f; m.far *= 1.15f; m.radius *= 1.1f; m.distantQ *= 1.2f; m.fxFloor = 1f; m.culledSse *= 0.9f;
            }
            if (mobile) m.radius = Math.Min(m.radius, 1f);      // phones: never more full-detail tiles than the device default
            return m;
        }

        public static float Sse(float v, PriorityMods m, bool mobile) => Clamp(v * m.sse, mobile ? 12f : 6f, 64f);
        public static float Far(float v, PriorityMods m) => Clamp(v * m.far, 200f, 20000f);
        public static float Radius(float v, PriorityMods m) => Clamp(v * m.radius, 100f, 20000f);
        public static float DistantQ(float v, PriorityMods m) => Clamp(v * m.distantQ, 0.5f, 8f);
        public static float CulledSse(PriorityMods m) => Clamp(BaseCulledSse * m.culledSse, 64f, 256f);
        public static float Scale(float v, PriorityMods m) => v * m.scale;     // caller clamps to the device maximum
        public static float Fx(float v, PriorityMods m, bool antiCrashActive) => antiCrashActive ? v * m.fx : Math.Max(v * m.fx, m.fxFloor);

        public static Priority Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return Priority.None;
            if (int.TryParse(s, out int n)) return (Priority)(n & (int)Priority.All);
            var p = Priority.None;
            foreach (var t in s.ToLowerInvariant().Split(',', '+', '|'))
            {
                string k = t.Trim();
                if (k.StartsWith("fast") || k.StartsWith("load")) p |= Priority.FastLoad;
                else if (k.StartsWith("qual") || k.StartsWith("tile")) p |= Priority.TileQuality;
                else if (k.StartsWith("fps")) p |= Priority.Fps;
                else if (k.StartsWith("real")) p |= Priority.Realism;
            }
            return p;
        }

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
