using System;

namespace RocketWorld.Sim
{
    /// <summary>
    /// What the demolition bot sees this frame. Every vector is expressed in the BOT's local frame (Unity axes):
    /// x = right, y = up, z = forward, in metres and m/s. The runtime adapter (DemoBotManager) fills it in; keeping the
    /// brain free of engine types lets it be unit-tested without Unity.
    /// </summary>
    public struct BotSense
    {
        public float tx, ty, tz;            // target position relative to the bot
        public float tvx, tvy, tvz;         // target WORLD velocity, in the bot's frame
        public float vx, vy, vz;            // the bot's own world velocity, in its frame
        public float ux, uy, uz;            // world "up" in the bot's frame (uy = 1 when level)
        public bool onGround, hasJumped, hasDoubleJumped, supersonic;
        public float clearL, clearC, clearR;    // free distance along three horizontal rays (left 30 deg, centre, right 30 deg)
        public float dt;
    }

    /// <summary>
    /// Demolition bot: the only goal is to run into the player, supersonic. No ball, no boost pads, no goals.
    /// Ground: lead the target, steer, power-slide through tight turns, hold boost when pointed at it, front-flip for speed
    /// on the straights, go around obstacles using three clearance rays, back out of dead ends.
    /// Air: jump + aim the nose at a target above it (aerial hit); otherwise land on the wheels, nose along the travel direction.
    /// Conventions match CarInput: steer &gt; 0 = right, yaw &gt; 0 = nose right, pitch &gt; 0 = nose up (pitch &lt; 0 = nose down,
    /// and a dodge with pitch &lt; 0 is a front flip), roll &gt; 0 = roll right.
    /// </summary>
    public sealed class DemoBotBrain
    {
        // ---- tuning -----------------------------------------------------------------------------------------
        public const float SteerGain = 2.4f;
        public const float AirKp = 1.6f, AirKd = 0.65f;
        public const float DodgeCooldown = 1.9f;
        public const float DodgeMinDist = 16f, DodgeMinSpeed = 6f, DodgeMaxSpeed = 40f;     // m/s; supersonic starts at 44
        public const float AerialMaxDist = 42f, AerialMinHeight = 2.2f;
        public const float ClearRange = 40f;                                                 // value the adapter reports for "nothing hit"

        // ---- state ------------------------------------------------------------------------------------------
        float _t;
        int _dodge;                               // 0 none, 1 first jump, 2 release, 3 second jump
        float _dodgeT0, _nextDodge, _lastDodgeStart = -10f;
        float _dPitch, _dRoll, _dYaw;
        float _stuckT, _recoverUntil, _recoverSide = 1f, _airStuckT;
        int _recoverCount;
        float _movingT;
        float _jumpHoldUntil;
        float _pPitch = float.NaN, _pYaw = float.NaN, _pRoll = float.NaN;

        /// <summary>The bot has been trapped repeatedly: the manager should respawn it near the player.</summary>
        public bool WantsRespawn { get; private set; }
        /// <summary>Distance to the target at the last Think (m), for the manager.</summary>
        public float Distance { get; private set; }
        /// <summary>True while the bot is doing a recovery manoeuvre (for debug display).</summary>
        public bool Recovering => _t < _recoverUntil;

        public void Reset()
        {
            _t = 0f; _dodge = 0; _dodgeT0 = 0f; _nextDodge = 0f; _lastDodgeStart = -10f; _stuckT = 0f; _recoverUntil = 0f;
            _recoverCount = 0; _airStuckT = 0f; _movingT = 0f; _jumpHoldUntil = 0f;
            _pPitch = _pYaw = _pRoll = float.NaN;
            WantsRespawn = false;
        }

        static float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
        static float Clamp1(float v) { return v < -1f ? -1f : (v > 1f ? 1f : v); }
        static float Len(float x, float y, float z) { return (float)Math.Sqrt(x * x + y * y + z * z); }

        /// <summary>Angle error PD on one axis. The derivative is taken on the error itself, so it needs no knowledge of rotation signs.</summary>
        static float Pd(float err, ref float prev, float dt)
        {
            float d = 0f;
            if (!float.IsNaN(prev) && Math.Abs(err - prev) < 1.5f) d = (err - prev) / dt;
            prev = err;
            return Clamp1(AirKp * err + AirKd * d);
        }

        public CarControls Think(BotSense s)
        {
            float dt = Clamp(s.dt, 1f / 240f, 0.1f);
            _t += dt;
            var c = new CarControls();

            float dist = Len(s.tx, s.ty, s.tz);
            float dist2 = (float)Math.Sqrt(s.tx * s.tx + s.tz * s.tz);
            Distance = dist;
            float fwd = s.vz;
            float speed = (float)Math.Sqrt(s.vx * s.vx + s.vz * s.vz);

            // Lead the target: aim where it will be when we get there (capped, so a fast player is not chased into the distance).
            float tLead = Clamp(dist / (Math.Max(fwd, 8f) + 12f), 0f, 1.1f);
            float lx = s.tx + s.tvx * tLead, ly = s.ty + s.tvy * tLead, lz = s.tz + s.tvz * tLead;
            float ang = (float)Math.Atan2(lx, lz);                  // > 0: target is to the right

            // ---- dodge sequence in progress (jump, release, jump with pitch = front flip) --------------------
            if (_dodge != 0)
            {
                float e = _t - _dodgeT0;
                if (_dodge == 1) { c.jump = true; c.boost = true; if (e >= 0.10f) _dodge = 2; }
                else if (_dodge == 2) { c.boost = true; if (e >= 0.16f) _dodge = 3; }
                if (_dodge == 3)
                {
                    c.jump = true; c.boost = true; c.pitch = _dPitch; c.roll = _dRoll; c.yaw = _dYaw;
                    if (e >= 0.28f) { _dodge = 0; _nextDodge = _t + DodgeCooldown; }
                }
                c.throttle = 1f;
                return c;
            }

            // ---- airborne ------------------------------------------------------------------------------------
            if (!s.onGround)
            {
                _stuckT = 0f;
                // Flipped and lying still (on its roof against a wall): a sideways dodge turns it back over.
                if (speed < 1.5f && Math.Abs(s.vy) < 1.5f) _airStuckT += dt; else _airStuckT = 0f;
                if (_airStuckT > 1.6f && !s.hasDoubleJumped)
                {
                    _airStuckT = 0f; StartDodge(0f, _recoverSide, 0f);
                    _recoverSide = -_recoverSide;
                    return c;
                }

                if (_t < _jumpHoldUntil && !s.hasDoubleJumped) c.jump = true;        // finish the held jump of an aerial

                bool justDodged = _t - _lastDodgeStart < 0.8f;
                bool aerial = ly > AerialMinHeight * 0.5f && dist < AerialMaxDist + 8f && (s.hasJumped || ly > AerialMinHeight);
                float fx, fy, fz;
                if (aerial) { fx = lx; fy = ly; fz = lz; }
                else
                {
                    // Landing: nose along the horizontal travel direction (or toward the target), wheels toward the ground.
                    float dv = s.vx * s.ux + s.vy * s.uy + s.vz * s.uz;
                    fx = s.vx - s.ux * dv; fy = s.vy - s.uy * dv; fz = s.vz - s.uz * dv;
                    if (Len(fx, fy, fz) < 3f)
                    {
                        float dl = lx * s.ux + ly * s.uy + lz * s.uz;
                        fx = lx - s.ux * dl; fy = ly - s.uy * dl; fz = lz - s.uz * dl;
                    }
                }
                float fl = Len(fx, fy, fz);
                if (fl > 1e-3f && !justDodged)
                {
                    fx /= fl; fy /= fl; fz /= fl;
                    c.yaw = Pd((float)Math.Atan2(fx, fz), ref _pYaw, dt);
                    c.pitch = Pd((float)Math.Atan2(fy, fz), ref _pPitch, dt);
                    // Wheels toward the ground (world up in the bot's frame must point out of its roof: ux -> 0, uy -> +1).
                    c.roll = Pd((float)Math.Atan2(s.ux, s.uy), ref _pRoll, dt);
                }
                if (aerial)
                {
                    float cosErr = fl > 1e-3f ? fz : 0f;                       // alignment of the nose with the aim line
                    c.boost = cosErr > 0.8f;
                    c.throttle = 1f;
                }
                else c.boost = false;
                return c;
            }

            _pPitch = _pYaw = _pRoll = float.NaN;
            _airStuckT = 0f;
            WantsRespawn = WantsRespawn && _recoverCount >= 5;

            // ---- aerial attack: the target is above us and close: jump, then the air branch aims at it ---------
            if (ly > AerialMinHeight && dist2 < AerialMaxDist && Math.Abs(ang) < 0.5f && fwd > 4f && _t >= _nextDodge)
            {
                _jumpHoldUntil = _t + 0.22f;
                _nextDodge = _t + 1.5f;
                c.jump = true; c.throttle = 1f; c.boost = true;
                return c;
            }

            // ---- recovery: back out of a dead end ------------------------------------------------------------
            if (_t < _recoverUntil)
            {
                c.throttle = -1f; c.steer = -_recoverSide;           // reversing with the wheels turned the other way swings the nose toward _recoverSide
                if (_t > _recoverUntil - 0.25f) { c.throttle = 1f; c.steer = _recoverSide; }
                return c;
            }

            // ---- steering --------------------------------------------------------------------------------------
            float steer = Clamp1(ang * SteerGain);
            float throttle = 1f;
            bool handbrake = false;
            float absAng = Math.Abs(ang);

            if (absAng > 0.85f && fwd > 12f) handbrake = true;               // power-slide through a tight turn
            if (absAng > 1.3f && fwd > 25f) throttle = 0f;                    // let the speed come off so it can actually turn
            if (absAng > 2.4f && dist2 < 14f && fwd < 6f) { throttle = -1f; steer = -steer; }   // it is right behind us: reverse around

            // Obstacles: three rays. When the way ahead is closing, steer to the freer side, harder the closer the wall.
            float look = Clamp(fwd * 0.9f + 8f, 8f, ClearRange);
            if (s.clearC < look)
            {
                float danger = 1f - s.clearC / look;
                float side = s.clearR >= s.clearL ? 1f : -1f;
                // when the target is clearly on one side and that side is open, the target steer is already right
                bool targetSideOpen = (steer > 0.2f && s.clearR > look * 0.8f) || (steer < -0.2f && s.clearL > look * 0.8f);
                if (!targetSideOpen)
                {
                    float avoid = side * Clamp1(danger * 1.8f);
                    float k = Clamp(danger * 1.6f, 0f, 1f);
                    steer = Clamp1(steer + (avoid - steer) * k);
                    if (s.clearC < 5f && fwd > 10f) throttle = 0f;
                }
            }
            if (s.clearL < 2.2f && steer < 0.5f) steer += 0.4f;
            if (s.clearR < 2.2f && steer > -0.5f) steer -= 0.4f;
            steer = Clamp1(steer);

            // ---- boost: pointed at it ---------------------------------------------------------------------------
            bool boost = absAng < 0.45f && s.clearC > 4f;

            // ---- stuck detection ---------------------------------------------------------------------------------
            if (dist2 > 7f && speed < 2f) _stuckT += dt; else _stuckT = Math.Max(0f, _stuckT - dt * 2f);
            if (speed > 6f) { _movingT += dt; if (_movingT > 1.2f) { _recoverCount = 0; WantsRespawn = false; } } else _movingT = 0f;
            if (_stuckT > 0.9f)
            {
                _stuckT = 0f; _recoverCount++;
                _recoverSide = s.clearR >= s.clearL ? 1f : -1f;
                if (Math.Abs(s.clearR - s.clearL) < 1f) _recoverSide = (_recoverCount & 1) == 0 ? 1f : -1f;
                _recoverUntil = _t + 0.9f + 0.2f * Math.Min(_recoverCount, 4);
                if (_recoverCount >= 3 && !s.hasJumped) { _jumpHoldUntil = _t + 0.2f; c.jump = true; }   // hop over a kerb / lip
                if (_recoverCount >= 5) WantsRespawn = true;
                c.throttle = -1f; c.steer = -_recoverSide;
                return c;
            }

            // ---- front flip for speed on a clear straight --------------------------------------------------------
            if (_t >= _nextDodge && absAng < 0.10f && dist2 > DodgeMinDist && fwd > DodgeMinSpeed && fwd < DodgeMaxSpeed
                && s.clearC > 25f && s.uy > 0.85f && Math.Abs(ly) < 2.5f && !s.supersonic)
            {
                StartDodge(-1f, 0f, 0f);
                c.jump = true; c.boost = true; c.throttle = 1f;
                return c;
            }

            c.throttle = throttle; c.steer = steer; c.handbrake = handbrake; c.boost = boost;
            return c;
        }

        void StartDodge(float pitch, float roll, float yaw)
        {
            _dodge = 1; _dodgeT0 = _t; _lastDodgeStart = _t;
            _dPitch = pitch; _dRoll = roll; _dYaw = yaw;
        }
    }
}
