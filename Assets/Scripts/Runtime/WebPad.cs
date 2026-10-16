using System.Runtime.InteropServices;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Gamepad straight from the browser Gamepad API (WebGL player only). Unity's own gamepad support on WebGL depends on the Input System
    /// package being the active handler in the build; polling the browser directly works regardless, for any controller the browser exposes.
    /// </summary>
    public static class WebPad
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int RW_PollGamepad(float[] buf, int len);
        [DllImport("__Internal")] static extern string RW_GamepadName();
        static readonly float[] _s = new float[22];
        static int _frame = -1;
        static bool _connected;
        static void Poll()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            _connected = RW_PollGamepad(_s, _s.Length) == 1;
        }
        public static bool Connected { get { Poll(); return _connected; } }
        public static string Name() => Connected ? RW_GamepadName() : null;
        static float Rescale(float v) { float a = Mathf.Abs(v); return a < InputDevices.Dead ? 0f : Mathf.Sign(v) * Mathf.Min(1f, (a - InputDevices.Dead) / (1f - InputDevices.Dead)); }
        public static Vector2 Left() { Poll(); return _connected ? new Vector2(Rescale(_s[17]), -Rescale(_s[18])) : Vector2.zero; }
        public static Vector2 Right() { Poll(); return _connected ? new Vector2(Rescale(_s[19]), -Rescale(_s[20])) : Vector2.zero; }
        static float B(int i) => _s[1 + i];
        public static float Value(PadInput p)
        {
            Poll();
            if (!_connected) return 0f;
            switch (p)
            {
                case PadInput.ButtonSouth: return B(0); case PadInput.ButtonEast: return B(1); case PadInput.ButtonWest: return B(2); case PadInput.ButtonNorth: return B(3);
                case PadInput.LeftShoulder: return B(4); case PadInput.RightShoulder: return B(5); case PadInput.LeftTrigger: return B(6); case PadInput.RightTrigger: return B(7);
                case PadInput.Select: return B(8); case PadInput.Start: return B(9); case PadInput.LeftStickPress: return B(10); case PadInput.RightStickPress: return B(11);
                case PadInput.DpadUp: return B(12); case PadInput.DpadDown: return B(13); case PadInput.DpadLeft: return B(14); case PadInput.DpadRight: return B(15);
                case PadInput.LeftStickUp: return Mathf.Max(0f, Left().y); case PadInput.LeftStickDown: return Mathf.Max(0f, -Left().y);
                case PadInput.LeftStickLeft: return Mathf.Max(0f, -Left().x); case PadInput.LeftStickRight: return Mathf.Max(0f, Left().x);
                case PadInput.RightStickUp: return Mathf.Max(0f, Right().y); case PadInput.RightStickDown: return Mathf.Max(0f, -Right().y);
                case PadInput.RightStickLeft: return Mathf.Max(0f, -Right().x); case PadInput.RightStickRight: return Mathf.Max(0f, Right().x);
            }
            return 0f;
        }
#endif
    }
}
