using System;

namespace RocketWorld.Sim
{
    public enum FlightStyle { FixedWing, Rotor, Hover }

    /// <summary>Per-vehicle numbers the flight brain needs (m/s, m). Filled by the runtime adapter from the VehicleSpec.</summary>
    public struct FlightProfile
    {
        public FlightStyle style;
        public float minSpeed, cruiseSpeed, maxSpeed;
        public float reach;          // demolition hit radius of this vehicle (m)
        public float cruiseAgl;      // preferred height above the ground when far from the target (m)
    }

    /// <summary>
    /// What a flying bot sees. Vectors are in the bot's body frame (x right, y up, z forward), metres and m/s.
    /// Body rates: p = roll right +, q = pitch up +, r = yaw right + (JSBSim convention, PlaneModel.RatesToBody).
    /// </summary>
    public struct FlightSense
    {
        public float tx, ty, tz;            // target position
        public float tvx, tvy, tvz;         // target WORLD velocity
        public float vx, vy, vz;            // own WORLD velocity
        public float ux, uy, uz;            // world up
        public float p, q, r;               // body rates, rad/s
        public float agl;                   // height above the ground below (m)
        public float clearF, clearL, clearR, clearU, clearD;   // ray distances: F along the velocity, L/R 28 deg left/right, U/D 22 deg up/down
        public float clearUp;               // straight up
        public float maxRay;                // value reported for "nothing hit" on the forward rays
        public float sx, sy, sz;            // separation from other bots (points away from them, length <= 1)
        public bool onGround;
        public float dt;
    }

    /// <summary>
    /// Brain output, vehicle independent. pitch &gt; 0 nose up, roll &gt; 0 roll right, yaw &gt; 0 nose right.
    /// Fixed wing: lever = throttle 0..1. Rotor / hover: fwd = forward speed command -1..1, vert = climb command -1..1,
    /// yaw = pedals / yaw rate, roll = lateral velocity (helicopter).
    /// </summary>
    public struct FlightControls
    {
        public float pitch, roll, yaw, lever, fwd, vert;
        public bool brake;
    }

    /// <summary>
    /// Flying demolition bot. Fixed wing: bank-to-turn pursuit with lead, speed control, stall protection, a terrain floor and
    /// look-ahead obstacle avoidance (over the obstacle when the way up is open, around it otherwise). Rotor / hover: heading + climb
    /// control with the same obstacle logic. Both steer away from other bots (separation vector).
    /// </summary>
    public sealed class FlightBotBrain
    {
        public FlightProfile Profile;
        public bool WantsRespawn { get; private set; }
        public float Distance { get; private set; }
        public bool Avoiding { get; private set; }

        float _t, _crashT;

        public FlightBotBrain(FlightProfile p) { Profile = p; }

        public void Reset() { _t = 0f; _crashT = 0f; WantsRespawn = false; Avoiding = false; }

        static float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
        static float C01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }
        static float C1(float v) { return v < -1f ? -1f : (v > 1f ? 1f : v); }
        static float Len(float x, float y, float z) { return (float)Math.Sqrt(x * x + y * y + z * z); }
        static float Lerp(float a, float b, float k) { return a + (b - a) * k; }

        public FlightControls Think(FlightSense s)
        {
            float dt = Clamp(s.dt, 1f / 240f, 0.1f);
            _t += dt;
            float speed = Len(s.vx, s.vy, s.vz);
            float dist = Len(s.tx, s.ty, s.tz);
            Distance = dist;
            float tHeight = s.tx * s.ux + s.ty * s.uy + s.tz * s.uz;
            float dist2 = (float)Math.Sqrt(Math.Max(0f, dist * dist - tHeight * tHeight));

            // Crashed / landed and not moving: ask for a respawn.
            if (s.onGround && speed < 3f) _crashT += dt; else _crashT = Math.Max(0f, _crashT - dt);
            WantsRespawn = _crashT > 3f;

            if (!(s.maxRay > 1f)) s.maxRay = 200f;
            return Profile.style == FlightStyle.FixedWing ? Wing(s, speed, dist, tHeight) : Rotor(s, speed, dist2, tHeight);
        }

        // ---------------------------------------------------------------------------------------- fixed wing
        FlightControls Wing(FlightSense s, float speed, float dist, float tHeight)
        {
            var p = Profile; var c = new FlightControls();

            // Lead the target.
            float tLead = Clamp(dist / Math.Max(speed, 40f), 0f, 2.2f);
            float lx = s.tx + s.tvx * tLead, ly = s.ty + s.tvy * tLead, lz = s.tz + s.tvz * tLead;
            float ll = Len(lx, ly, lz);
            if (ll < 1e-3f) { lx = 0f; ly = 0f; lz = 1f; ll = 1f; }
            float gx = lx / ll, gy = ly / ll, gz = lz / ll;

            float vs = s.vx * s.ux + s.vy * s.uy + s.vz * s.uz;                     // vertical speed
            bool commit = dist < p.reach * 10f + speed * 0.5f && tHeight < 40f;     // last stretch: dive on the target

            // Terrain floor + time-to-ground.
            float floorAgl = commit ? 4f : Clamp(speed * 0.8f, 30f, 110f);
            float climb = 0f;
            if (!commit)
            {
                if (s.agl < floorAgl) climb = (1f - s.agl / floorAgl) * 2.2f;
                if (vs < -1f) { float ttg = s.agl / -vs; if (ttg < 3f) climb = Math.Max(climb, (1f - ttg / 3f) * 3f); }
            }
            gx += s.ux * climb; gy += s.uy * climb; gz += s.uz * climb;
            { float gl = Len(gx, gy, gz); if (gl > 1e-3f) { gx /= gl; gy /= gl; gz /= gl; } }

            // Obstacle ahead: go over it when the way up is open, otherwise around it.
            float look = Clamp(speed * 2.6f, 60f, s.maxRay);
            float blockDist = commit ? Math.Min(look, dist * 0.7f) : look;
            Avoiding = false;
            bool upOpen = s.clearUp > 40f && (s.clearU > s.clearF * 1.3f || s.clearU >= s.maxRay * 0.95f);
            float side = s.clearR >= s.clearL ? 1f : -1f;
            if (s.clearF < blockDist)
            {
                Avoiding = true;
                float danger = 1f - s.clearF / blockDist;
                float ex, ey, ez;
                if (upOpen) { ex = s.ux; ey = s.uy; ez = s.uz + 0.35f; }
                else { ex = side + s.ux * 0.3f; ey = s.uy * 0.3f; ez = 0.4f + s.uz * 0.3f; }
                float el = Len(ex, ey, ez); if (el > 1e-3f) { ex /= el; ey /= el; ez /= el; }
                float k = C01(danger * 2f);
                gx = Lerp(gx, ex, k); gy = Lerp(gy, ey, k); gz = Lerp(gz, ez, k);
            }
            // Side walls: drift away.
            if (s.clearL < look * 0.35f) gx += 0.5f * (1f - s.clearL / (look * 0.35f));
            if (s.clearR < look * 0.35f) gx -= 0.5f * (1f - s.clearR / (look * 0.35f));
            // Other bots.
            gx += s.sx * 1.2f; gy += s.sy * 1.2f; gz += s.sz * 0.6f;
            { float gl = Len(gx, gy, gz); if (gl < 1e-3f) { gx = 0f; gy = 0f; gz = 1f; } else { gx /= gl; gy /= gl; gz /= gl; } }

            // Bank to turn: roll until the lift vector points at the aim point, then pull.
            float off = (float)Math.Acos(Clamp(gz, -1f, 1f));
            float rollErr = (float)Math.Atan2(gx, gy);
            bool push = Math.Abs(rollErr) > 1.8f && s.agl > 120f && climb < 0.2f && !Avoiding;     // aim point below: push over instead of rolling inverted
            float rErr = push ? (float)Math.Atan2(-gx, -gy) : rollErr;
            float pullSign = push ? -1f : 1f;
            float rollFactor = C01(1.25f - Math.Abs(rErr));
            float pullLimit = Clamp(speed / (p.minSpeed * 2f), 0.35f, 1f);
            float pull = C01(off * 2.4f) * rollFactor * pullSign * pullLimit;
            float bankRoll = C1(rErr * 1.6f - s.p * 0.35f);
            float levelRoll = C1((float)Math.Atan2(s.ux, s.uy) * 1.4f - s.p * 0.35f);

            float fine = C01(1f - off / 0.18f);                        // well aimed: track with pitch + rudder, wings level
            c.pitch = Lerp(pull, C1(gy * 5f), fine);
            c.roll = Lerp(bankRoll, levelRoll, fine);
            c.yaw = off < 0.25f ? C1(gx * 2f) : 0f;
            c.pitch = C1(c.pitch - 0.15f * s.q);

            // Imminent impact: full pull-up (wings level), or a hard bank if there is no room above.
            float emergencyDist = Math.Max(speed * 0.8f, 25f);
            if (s.clearF < emergencyDist && (!commit || s.clearF < dist * 0.5f))
            {
                c.pitch = 1f;
                c.roll = upOpen ? levelRoll : side * 0.9f;
                c.yaw = 0f;
            }
            else if (!commit && s.agl < 15f && vs < -5f) { c.pitch = 1f; c.roll = levelRoll; }

            // Speed: fast run-in, slower when close so it can turn onto the target.
            float want = dist > 500f ? p.maxSpeed * 0.9f : p.cruiseSpeed;
            if (commit) want = Lerp(Math.Max(p.minSpeed * 1.4f, 28f), p.cruiseSpeed, C01(dist / 250f));
            else if (off > 1.0f && dist < 300f) want = Math.Min(want, p.cruiseSpeed * 0.8f);
            c.lever = C01(0.55f + (want - speed) * 1.8f / Math.Max(want, 30f));

            // Stall protection.
            if (speed < p.minSpeed * 1.15f)
            {
                c.lever = 1f;
                c.pitch = Math.Min(c.pitch, 0.25f);
                if (s.agl > 80f) c.pitch = Math.Min(c.pitch, -0.2f);
                c.roll = levelRoll;
            }
            return c;
        }

        // ---------------------------------------------------------------------------------------- helicopter / hover car
        FlightControls Rotor(FlightSense s, float speed, float dist2, float tHeight)
        {
            var p = Profile; var c = new FlightControls();

            float tLead = Clamp(dist2 / Math.Max(speed, 12f), 0f, 2f);
            float lx = s.tx + s.tvx * tLead, ly = s.ty + s.tvy * tLead, lz = s.tz + s.tvz * tLead;
            float dot = lx * s.ux + ly * s.uy + lz * s.uz;
            float hx = lx - s.ux * dot, hz = lz - s.uz * dot;
            float ang = (float)Math.Atan2(hx, hz);
            float absAng = Math.Abs(ang);

            c.yaw = C1(ang * 1.8f);
            float fwd = C01(1.15f - absAng / 0.9f);

            // Height: hold the cruise height far away, come down to the target's height when close.
            float cruise = p.cruiseAgl;
            float vert;
            if (dist2 > 45f)
            {
                vert = C1((cruise - s.agl) / 6f);
                if (tHeight > 10f) vert = Math.Max(vert, C1((tHeight - 4f) / 8f));
            }
            else vert = C1((tHeight + 0.5f) / 6f);

            // Obstacles ahead: climb over, slow down, turn to the freer side when the roof is low.
            float look = Clamp(speed * 2.2f + 12f, 14f, s.maxRay);
            Avoiding = false;
            if (s.clearF < look)
            {
                Avoiding = true;
                float danger = 1f - s.clearF / look;
                if (s.clearUp > 25f) vert = Math.Max(vert, C1(danger * 1.5f));
                fwd *= 1f - 0.8f * danger;
                if (s.clearU < s.clearF || s.clearUp <= 25f)
                {
                    float side = s.clearR >= s.clearL ? 1f : -1f, k = C01(danger * 1.6f);
                    c.yaw = C1(c.yaw * (1f - k) + side * k);
                }
            }
            if (s.clearL < 8f) c.roll += 0.5f;
            if (s.clearR < 8f) c.roll -= 0.5f;
            c.roll = C1(c.roll);

            // Other bots.
            c.yaw = C1(c.yaw + s.sx);
            vert = C1(vert + s.sy * 0.8f);
            if (s.sz < -0.5f) fwd *= 0.5f;

            if (s.agl < 3f && vert < 0f) vert = 0f;
            c.fwd = C01(fwd);
            c.vert = vert;
            return c;
        }
    }
}
