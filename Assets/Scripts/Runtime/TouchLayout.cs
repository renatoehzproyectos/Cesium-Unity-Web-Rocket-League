using UnityEngine;

namespace RocketWorld
{
    public enum WidgetKind
    {
        Hold,            // pressed while a finger is on it
        Tap,             // fires once per touch
        Latch,           // tap on / tap off (stays lit)
        SliderSticky,    // vertical, bottom 0 .. top 1, keeps the value it was left at (a throttle lever)
        SliderSpring,    // vertical, -1 .. 1, springs back to 0 (collective / vertical throttle)
        SliderH,         // horizontal, -1 .. 1, springs back to 0 (rudder pedals)
    }

    public readonly struct TouchWidget
    {
        public readonly string id, label; public readonly WidgetKind kind; public readonly Rect zone;   // zone: normalised screen coordinates, origin bottom-left
        public TouchWidget(string id, string label, WidgetKind kind, float x, float y, float w, float h) { this.id = id; this.label = label; this.kind = kind; zone = new Rect(x, y, w, h); }
    }

    /// <summary>
    /// On-screen touch controls per vehicle (besides the analog stick, which is always at the lower left). Widget ids: jump, boost, brake, rollL, rollR,
    /// hyper, gear, flaps, mode, up, down, throttle (sticky lever slider), vert (spring slider) and rudder (spring slider).
    /// </summary>
    public static class TouchLayout
    {
        const WidgetKind H = WidgetKind.Hold, T = WidgetKind.Tap, L = WidgetKind.Latch;
        static readonly TouchWidget Throttle = new TouchWidget("throttle", "THROTTLE", WidgetKind.SliderSticky, 0.925f, 0.06f, 0.07f, 0.60f);
        static readonly TouchWidget Collective = new TouchWidget("vert", "COLLECTIVE", WidgetKind.SliderSpring, 0.925f, 0.06f, 0.07f, 0.60f);
        static readonly TouchWidget Rudder = new TouchWidget("rudder", "RUDDER   L  |  R", WidgetKind.SliderH, 0.40f, 0.04f, 0.24f, 0.13f);

        static readonly TouchWidget[] Car =
        {
            new TouchWidget("jump", "JUMP", H, 0.80f, 0.05f, 0.17f, 0.20f), new TouchWidget("boost", "BOOST", H, 0.80f, 0.27f, 0.17f, 0.20f),
            new TouchWidget("rollL", "ROLL L", H, 0.61f, 0.05f, 0.17f, 0.20f), new TouchWidget("rollR", "ROLL R", H, 0.61f, 0.27f, 0.17f, 0.20f),
            new TouchWidget("hyper", "HYPER ×6", L, 0.61f, 0.49f, 0.36f, 0.15f),
        };
        static readonly TouchWidget[] Cessna =
        {
            Throttle, Rudder, new TouchWidget("brake", "BRAKE", H, 0.80f, 0.06f, 0.11f, 0.18f), new TouchWidget("hyper", "HYPER ×6", L, 0.80f, 0.27f, 0.11f, 0.18f),
        };
        static readonly TouchWidget[] Jet =
        {
            Throttle, Rudder,
            new TouchWidget("boost", "AFTER-\nBURNER", H, 0.80f, 0.06f, 0.11f, 0.18f), new TouchWidget("brake", "BRAKE", H, 0.67f, 0.06f, 0.11f, 0.18f),
            new TouchWidget("gear", "GEAR", T, 0.80f, 0.27f, 0.11f, 0.18f), new TouchWidget("hyper", "HYPER ×6", L, 0.67f, 0.27f, 0.11f, 0.18f),
        };
        static readonly TouchWidget[] Vtol =
        {
            Throttle, Rudder,
            new TouchWidget("up", "LIFT UP", H, 0.80f, 0.46f, 0.11f, 0.16f), new TouchWidget("down", "LIFT DOWN", H, 0.80f, 0.27f, 0.11f, 0.16f),
            new TouchWidget("boost", "AFTER-\nBURNER", H, 0.80f, 0.08f, 0.11f, 0.16f), new TouchWidget("mode", "VTOL", T, 0.67f, 0.46f, 0.11f, 0.16f),
            new TouchWidget("hyper", "HYPER ×6", L, 0.67f, 0.27f, 0.11f, 0.16f), new TouchWidget("brake", "BRAKE", H, 0.67f, 0.08f, 0.11f, 0.16f),
        };
        static readonly TouchWidget[] Heli =
        {
            Collective, Rudder, new TouchWidget("hyper", "HYPER ×6", L, 0.80f, 0.06f, 0.11f, 0.18f), new TouchWidget("brake", "BRAKE", H, 0.67f, 0.06f, 0.11f, 0.18f),
        };
        static readonly TouchWidget[] DeLorean =
        {
            new TouchWidget("up", "UP", H, 0.80f, 0.46f, 0.11f, 0.16f), new TouchWidget("down", "DOWN", H, 0.80f, 0.27f, 0.11f, 0.16f),
            new TouchWidget("boost", "BOOST", H, 0.80f, 0.08f, 0.11f, 0.16f), new TouchWidget("mode", "HOVER", T, 0.67f, 0.46f, 0.11f, 0.16f),
            new TouchWidget("hyper", "HYPER ×6", L, 0.67f, 0.27f, 0.11f, 0.16f), new TouchWidget("brake", "BRAKE", H, 0.67f, 0.08f, 0.11f, 0.16f),
        };

        // ---- FPV drone: two sticks (left = throttle + yaw, right = pitch + roll, both drawn by DebugHud / read by CarInput) and a band of buttons between them.
        // The layout depends on the screen shape (the sticks are square in pixels), so it is rebuilt when the screen size changes.
        static TouchWidget[] _drone; static int _droneW, _droneH;
        static TouchWidget[] Drone()
        {
            int w = Mathf.Max(1, Screen.width), h = Mathf.Max(1, Screen.height);
            if (_drone != null && w == _droneW && h == _droneH) return _drone;
            _droneW = w; _droneH = h;
            float u = CarInput.DesignHeight, band = u / h;
            float sz = u * 0.46f, m = u * 0.04f;
            float x0 = (m + sz) / w + 0.012f, x1 = 1f - x0;                       // free band between the two sticks
            float gap = 0.010f, cw = Mathf.Clamp((x1 - x0 - 2f * gap) / 3f, 0.06f, 0.14f);
            float cx = 0.5f - (cw * 3f + gap * 2f) * 0.5f, c1 = cx + cw + gap, c2 = c1 + cw + gap;
            float r0 = 0.05f * band, r1 = 0.24f * band, r2 = 0.43f * band, bh = 0.17f * band, th = 0.12f * band;
            _drone = new[]
            {
                new TouchWidget("arm", "ARM", T, cx, r0, cw, bh), new TouchWidget("mode", "MODE", T, c1, r0, cw, bh), new TouchWidget("reset", "RESET", T, c2, r0, cw, bh),
                new TouchWidget("cam", "CAM", T, cx, r1, cw, bh), new TouchWidget("gear", "DRONE", T, c1, r1, cw, bh), new TouchWidget("exit", "EXIT", T, c2, r1, cw, bh),
                new TouchWidget("tiltUp", "TILT UP", T, cx, r2, cw, th), new TouchWidget("tiltDown", "TILT DOWN", T, c1, r2, cw, th),
            };
            return _drone;
        }

        // Narrow screens (phone held upright): the landscape layout is squeezed into a 16:9 band along the bottom, as wide as the screen,
        // instead of being stretched over the whole tall screen (which made huge tall buttons).
        static TouchWidget[] _band; static TouchProfile _bandFor = (TouchProfile)(-1); static int _bandW, _bandH;

        public static TouchWidget[] For(TouchProfile p)
        {
            var raw = Raw(p);
            if (p == TouchProfile.Drone) return raw;
            int w = Mathf.Max(1, Screen.width), h = Mathf.Max(1, Screen.height);
            float band = CarInput.DesignHeight / h;
            if (band >= 0.999f) return raw;
            if (_band != null && _bandFor == p && _bandW == w && _bandH == h) return _band;
            var m = new TouchWidget[raw.Length];
            for (int i = 0; i < raw.Length; i++) { var z = raw[i].zone; m[i] = new TouchWidget(raw[i].id, raw[i].label, raw[i].kind, z.x, z.y * band, z.width, z.height * band); }
            _band = m; _bandFor = p; _bandW = w; _bandH = h;
            return m;
        }

        static TouchWidget[] Raw(TouchProfile p)
        {
            switch (p)
            {
                case TouchProfile.Drone: return Drone();
                case TouchProfile.Cessna: return Cessna;
                case TouchProfile.Jet: return Jet;
                case TouchProfile.Vtol: return Vtol;
                case TouchProfile.Helicopter: return Heli;
                case TouchProfile.DeLorean: return DeLorean;
                default: return Car;
            }
        }
    }
}
