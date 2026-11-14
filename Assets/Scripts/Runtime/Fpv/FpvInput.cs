using RocketWorld.Sim.Fpv;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Turns the keyboard and a gamepad into the drone's channels, the way ShramkoGSFPV does it (Mode 2 sticks).
    /// Keyboard ("pilot" layout, the one the simulator calls Mode 2): W/S throttle that STAYS where you leave it (held W: 0 -> 100 % in 0.5 s,
    /// S: 100 -> 0 in 0.33 s), A/D yaw, arrow keys pitch / roll (bang-bang: tilt 60 %, yaw 50 % of full stick), Space arms / disarms.
    /// Gamepad (Mode 2): left stick up/down throttle (it springs to the centre: the centre is half power), left stick left/right yaw,
    /// right stick pitch / roll. Lowering the throttle stick to the bottom starts the motors from acro; A arms / disarms.
    /// The arm rule is the simulator's: the switch must go off -> on with the throttle at the bottom (the model refuses otherwise).
    /// Channels: 0 roll, 1 pitch, 2 throttle, 3 yaw, 4 arm, 5 mode (see <see cref="FpvSim"/>).
    /// </summary>
    public sealed class FpvInput
    {
        public const double TiltStick = 0.6, YawStick = 0.5, ThrUpSec = 0.5, ThrDownSec = 1.0 / 3.0;
        public const double TouchThrUpSec = 0.8, TouchThrDownSec = 0.6;     // full left-stick deflection; the throttle stays where the thumb leaves it
        public readonly double[] ch = { 0, 0, -1, 0, -1, 1, 0, 0 };
        public bool armSwitch;
        public bool padActive;
        double _kbThrottle;
        bool _prevSpace, _prevPadArm, _prevPadLow = true;
        public FlightMode mode = FlightMode.Angle;

        public double Throttle01 => (ch[2] + 1) * 0.5;

        public void Reset() { armSwitch = false; _kbThrottle = 0; _prevPadLow = true; _prevSpace = true; ch[2] = -1; ch[4] = -1; }

        static float Key(string k) => InputDevices.KeyValue(k);

        /// <summary>Read the devices once per frame; <paramref name="dt"/> is the unscaled frame time.</summary>
        public void Update(float dt, bool blocked, CarInput ci = null)
        {
            if (blocked) { ch[0] = ch[1] = ch[3] = 0; return; }
            dt = Mathf.Min(dt, 0.2f);

            // ---- keyboard ----
            double kRoll = (Key("RightArrow") > 0 ? 1 : 0) - (Key("LeftArrow") > 0 ? 1 : 0);
            double kPitch = (Key("UpArrow") > 0 ? 1 : 0) - (Key("DownArrow") > 0 ? 1 : 0);
            double kYaw = (Key("D") > 0 ? 1 : 0) - (Key("A") > 0 ? 1 : 0);
            if (Key("W") > 0) _kbThrottle = System.Math.Min(1, _kbThrottle + dt / ThrUpSec);
            if (Key("S") > 0) _kbThrottle = System.Math.Max(0, _kbThrottle - dt / ThrDownSec);
            bool space = Key("Space") > 0;
            if (space && !_prevSpace) armSwitch = !armSwitch;
            _prevSpace = space;

            // ---- touch (drone layout): left stick up/down = throttle that stays, left/right = yaw; right stick = pitch / roll; ARM button ----
            bool touch = ci != null && ci.TouchControlsActive && CarInput.Profile == TouchProfile.Drone;
            Vector2 tl = touch ? ci.TouchLeft : Vector2.zero, tr = touch ? ci.TouchRight : Vector2.zero;
            if (touch)
            {
                if (tl.y > 0f) _kbThrottle = System.Math.Min(1, _kbThrottle + tl.y * dt / TouchThrUpSec);
                else if (tl.y < 0f) _kbThrottle = System.Math.Max(0, _kbThrottle + tl.y * dt / TouchThrDownSec);
                if (ci.ArmPressed) armSwitch = !armSwitch;
            }

            double roll = kRoll * TiltStick, pitch = kPitch * TiltStick, yaw = kYaw * YawStick, thr = _kbThrottle * 2 - 1;
            if (touch)
            {
                if (tl.x != 0f) yaw = tl.x;
                if (tr.sqrMagnitude > 0f) { roll = tr.x; pitch = tr.y; }
            }

            // ---- gamepad (wins while a stick is off centre) ----
            if (InputDevices.HasGamepad)
            {
                Vector2 l = InputDevices.LeftStick(), r = InputDevices.RightStick();
                bool moved = l.sqrMagnitude > 0f || r.sqrMagnitude > 0f;
                if (moved) padActive = true;
                if (padActive)
                {
                    roll = r.x; pitch = r.y; yaw = l.x; thr = l.y;
                    _kbThrottle = (thr + 1) * 0.5;            // keyboard picks up where the stick left it
                    bool low = thr <= -0.9;
                    if (low && !_prevPadLow && !armSwitch) armSwitch = true;   // stick to the bottom: motors on (acro start)
                    _prevPadLow = low;
                    bool a = InputDevices.PadValue(PadInput.ButtonSouth) > 0.5f;
                    if (a && !_prevPadArm) armSwitch = !armSwitch;
                    _prevPadArm = a;
                }
            }
            ch[0] = Mathf.Clamp((float)roll, -1, 1); ch[1] = Mathf.Clamp((float)pitch, -1, 1);
            ch[2] = Mathf.Clamp((float)thr, -1, 1); ch[3] = Mathf.Clamp((float)yaw, -1, 1);
            ch[4] = armSwitch ? 1 : -1;
            ch[5] = FpvModes.Channel(mode);
        }

        public void CycleMode() { mode = mode == FlightMode.Acro ? FlightMode.Horizon : mode == FlightMode.Horizon ? FlightMode.Angle : FlightMode.Acro; }
    }
}
