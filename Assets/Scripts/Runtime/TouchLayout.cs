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
        static readonly TouchWidget Rudder = new TouchWidget("rudder", "RUDDER  ◀  ▶", WidgetKind.SliderH, 0.40f, 0.04f, 0.24f, 0.13f);

        static readonly TouchWidget[] Car =
        {
            new TouchWidget("jump", "JUMP", H, 0.80f, 0.05f, 0.17f, 0.20f), new TouchWidget("boost", "BOOST", H, 0.80f, 0.27f, 0.17f, 0.20f),
            new TouchWidget("rollL", "ROLL ◀", H, 0.61f, 0.05f, 0.17f, 0.20f), new TouchWidget("rollR", "ROLL ▶", H, 0.61f, 0.27f, 0.17f, 0.20f),
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
            new TouchWidget("up", "LIFT ▲", H, 0.80f, 0.46f, 0.11f, 0.16f), new TouchWidget("down", "LIFT ▼", H, 0.80f, 0.27f, 0.11f, 0.16f),
            new TouchWidget("boost", "AFTER-\nBURNER", H, 0.80f, 0.08f, 0.11f, 0.16f), new TouchWidget("mode", "VTOL", T, 0.67f, 0.46f, 0.11f, 0.16f),
            new TouchWidget("hyper", "HYPER ×6", L, 0.67f, 0.27f, 0.11f, 0.16f), new TouchWidget("brake", "BRAKE", H, 0.67f, 0.08f, 0.11f, 0.16f),
        };
        static readonly TouchWidget[] Heli =
        {
            Collective, Rudder, new TouchWidget("hyper", "HYPER ×6", L, 0.80f, 0.06f, 0.11f, 0.18f), new TouchWidget("brake", "BRAKE", H, 0.67f, 0.06f, 0.11f, 0.18f),
        };
        static readonly TouchWidget[] Airliner =
        {
            Throttle, Rudder,
            new TouchWidget("brake", "BRAKE", H, 0.80f, 0.06f, 0.11f, 0.18f), new TouchWidget("boost", "REVERSE", H, 0.67f, 0.06f, 0.11f, 0.18f),
            new TouchWidget("gear", "GEAR", T, 0.80f, 0.27f, 0.11f, 0.18f), new TouchWidget("flaps", "FLAPS", T, 0.67f, 0.27f, 0.11f, 0.18f),
            new TouchWidget("hyper", "HYPER ×6", L, 0.67f, 0.48f, 0.24f, 0.14f),
        };
        static readonly TouchWidget[] DeLorean =
        {
            new TouchWidget("up", "UP ▲", H, 0.80f, 0.46f, 0.11f, 0.16f), new TouchWidget("down", "DOWN ▼", H, 0.80f, 0.27f, 0.11f, 0.16f),
            new TouchWidget("boost", "BOOST", H, 0.80f, 0.08f, 0.11f, 0.16f), new TouchWidget("mode", "HOVER", T, 0.67f, 0.46f, 0.11f, 0.16f),
            new TouchWidget("hyper", "HYPER ×6", L, 0.67f, 0.27f, 0.11f, 0.16f), new TouchWidget("brake", "BRAKE", H, 0.67f, 0.08f, 0.11f, 0.16f),
        };

        public static TouchWidget[] For(TouchProfile p)
        {
            switch (p)
            {
                case TouchProfile.Cessna: return Cessna;
                case TouchProfile.Jet: return Jet;
                case TouchProfile.Vtol: return Vtol;
                case TouchProfile.Helicopter: return Heli;
                case TouchProfile.Airliner: return Airliner;
                case TouchProfile.DeLorean: return DeLorean;
                default: return Car;
            }
        }
    }
}
