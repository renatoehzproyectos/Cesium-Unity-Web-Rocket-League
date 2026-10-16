using System;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace RocketWorld
{
    /// <summary>Rising-edge helper for polled inputs.</summary>
    public struct Edge
    {
        bool _prev;
        public bool Update(bool now) { bool r = now && !_prev; _prev = now; return r; }
    }

    /// <summary>
    /// Device access for the rebindable controls. Full support (any gamepad: Xbox, PlayStation, Switch Pro, generic; analog sticks and triggers)
    /// needs the Input System package (Project Settings > Player > Active Input Handling). With only the legacy Input Manager, the keyboard
    /// and gamepad *buttons* still work; analog sticks / triggers cannot be read without hand-made Input Manager axes.
    /// </summary>
    public static class InputDevices
    {
        public const float Dead = 0.15f;       // stick dead zone
        public const float Digital = 0.55f;    // analog value that counts as "pressed" for capture / edges

        static float Rescale(float v) { float a = Mathf.Abs(v); return a < Dead ? 0f : Mathf.Sign(v) * Mathf.Min(1f, (a - Dead) / (1f - Dead)); }

#if ENABLE_INPUT_SYSTEM
        static readonly Dictionary<string, Key> _keys = new Dictionary<string, Key>();
        static bool Resolve(string name, out Key key)
        {
            if (_keys.TryGetValue(name, out key)) return key != Key.None;
            if (!Enum.TryParse(name, out key) || key == Key.None || key == Key.IMESelected) key = Key.None;
            _keys[name] = key;
            return key != Key.None;
        }

        public static bool HasGamepadNative => Gamepad.current != null;
        public static string PadNameNative() => Gamepad.current != null ? Gamepad.current.displayName : null;

        public static float KeyValue(string name)
        {
            var k = Keyboard.current;
            if (k == null || string.IsNullOrEmpty(name) || !Resolve(name, out var key)) return 0f;
            return k[key].isPressed ? 1f : 0f;
        }

        public static bool AnyKeyboardKeyPressed(out string name)
        {
            name = null;
            var k = Keyboard.current;
            if (k == null) return false;
            foreach (var kc in k.allKeys)
                if (kc.wasPressedThisFrame && kc.keyCode != Key.None && kc.keyCode != Key.IMESelected) { name = kc.keyCode.ToString(); return true; }
            return false;
        }

        public static float PadValueNative(PadInput p)
        {
            var g = Gamepad.current;
            if (g == null || p == PadInput.None) return 0f;
            switch (p)
            {
                case PadInput.ButtonSouth: return g.buttonSouth.isPressed ? 1f : 0f;
                case PadInput.ButtonEast: return g.buttonEast.isPressed ? 1f : 0f;
                case PadInput.ButtonWest: return g.buttonWest.isPressed ? 1f : 0f;
                case PadInput.ButtonNorth: return g.buttonNorth.isPressed ? 1f : 0f;
                case PadInput.LeftShoulder: return g.leftShoulder.isPressed ? 1f : 0f;
                case PadInput.RightShoulder: return g.rightShoulder.isPressed ? 1f : 0f;
                case PadInput.LeftTrigger: return g.leftTrigger.ReadValue();
                case PadInput.RightTrigger: return g.rightTrigger.ReadValue();
                case PadInput.LeftStickPress: return g.leftStickButton.isPressed ? 1f : 0f;
                case PadInput.RightStickPress: return g.rightStickButton.isPressed ? 1f : 0f;
                case PadInput.Start: return g.startButton.isPressed ? 1f : 0f;
                case PadInput.Select: return g.selectButton.isPressed ? 1f : 0f;
                case PadInput.DpadUp: return g.dpad.up.isPressed ? 1f : 0f;
                case PadInput.DpadDown: return g.dpad.down.isPressed ? 1f : 0f;
                case PadInput.DpadLeft: return g.dpad.left.isPressed ? 1f : 0f;
                case PadInput.DpadRight: return g.dpad.right.isPressed ? 1f : 0f;
                case PadInput.LeftStickUp: return Mathf.Max(0f, Rescale(g.leftStick.y.ReadValue()));
                case PadInput.LeftStickDown: return Mathf.Max(0f, -Rescale(g.leftStick.y.ReadValue()));
                case PadInput.LeftStickLeft: return Mathf.Max(0f, -Rescale(g.leftStick.x.ReadValue()));
                case PadInput.LeftStickRight: return Mathf.Max(0f, Rescale(g.leftStick.x.ReadValue()));
                case PadInput.RightStickUp: return Mathf.Max(0f, Rescale(g.rightStick.y.ReadValue()));
                case PadInput.RightStickDown: return Mathf.Max(0f, -Rescale(g.rightStick.y.ReadValue()));
                case PadInput.RightStickLeft: return Mathf.Max(0f, -Rescale(g.rightStick.x.ReadValue()));
                default: return Mathf.Max(0f, Rescale(g.rightStick.x.ReadValue()));
            }
        }

        /// <summary>Raw (dead-zoned) stick position, for camera orbit and menu navigation.</summary>
        public static Vector2 LeftStickNative() { var g = Gamepad.current; return g == null ? Vector2.zero : new Vector2(Rescale(g.leftStick.x.ReadValue()), Rescale(g.leftStick.y.ReadValue())); }
        public static Vector2 RightStickNative() { var g = Gamepad.current; return g == null ? Vector2.zero : new Vector2(Rescale(g.rightStick.x.ReadValue()), Rescale(g.rightStick.y.ReadValue())); }
#elif ENABLE_LEGACY_INPUT_MANAGER
        static readonly Dictionary<string, KeyCode> _keys = new Dictionary<string, KeyCode>();
        static readonly Dictionary<string, string> Alias = new Dictionary<string, string>
        {
            { "LeftCtrl", "LeftControl" }, { "RightCtrl", "RightControl" }, { "Enter", "Return" }, { "Backquote", "BackQuote" },
            { "Digit0", "Alpha0" }, { "Digit1", "Alpha1" }, { "Digit2", "Alpha2" }, { "Digit3", "Alpha3" }, { "Digit4", "Alpha4" },
            { "Digit5", "Alpha5" }, { "Digit6", "Alpha6" }, { "Digit7", "Alpha7" }, { "Digit8", "Alpha8" }, { "Digit9", "Alpha9" },
            { "Quote", "Quote" }, { "Semicolon", "Semicolon" },
        };
        static KeyCode[] _all;
        static bool Resolve(string name, out KeyCode key)
        {
            if (_keys.TryGetValue(name, out key)) return key != KeyCode.None;
            if (Alias.TryGetValue(name, out var a)) name = a;
            if (!Enum.TryParse(name, out key)) key = KeyCode.None;
            _keys[name] = key;
            return key != KeyCode.None;
        }
        static string FromKeyCode(KeyCode kc)
        {
            switch (kc) { case KeyCode.LeftControl: return "LeftCtrl"; case KeyCode.RightControl: return "RightCtrl"; case KeyCode.Return: return "Enter"; case KeyCode.BackQuote: return "Backquote"; }
            if (kc >= KeyCode.Alpha0 && kc <= KeyCode.Alpha9) return "Digit" + (kc - KeyCode.Alpha0);
            return kc.ToString();
        }

        public static bool HasGamepadNative { get { foreach (var n in Input.GetJoystickNames()) if (!string.IsNullOrEmpty(n)) return true; return false; } }
        public static string PadNameNative() { foreach (var n in Input.GetJoystickNames()) if (!string.IsNullOrEmpty(n)) return n; return null; }

        public static float KeyValue(string name) => !string.IsNullOrEmpty(name) && Resolve(name, out var k) && Input.GetKey(k) ? 1f : 0f;

        public static bool AnyKeyboardKeyPressed(out string name)
        {
            name = null;
            if (!Input.anyKeyDown) return false;
            _all ??= (KeyCode[])Enum.GetValues(typeof(KeyCode));
            foreach (var kc in _all)
                if (kc != KeyCode.None && kc < KeyCode.Mouse0 && Input.GetKeyDown(kc)) { name = FromKeyCode(kc); return true; }
            return false;
        }

        static float Btn(int i) => Input.GetKey((KeyCode)((int)KeyCode.JoystickButton0 + i)) ? 1f : 0f;
        static bool IsPs()
        {
            var n = PadNameNative() ?? "";
            return n.IndexOf("Wireless Controller", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("PLAYSTATION", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("DualShock", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        public static float PadValueNative(PadInput p)
        {
            if (IsPs())   // legacy PS4 numbering: 0 square, 1 cross, 2 circle, 3 triangle, 4 L1, 5 R1, 6 L2, 7 R2, 8 share, 9 options, 10 L3, 11 R3
                switch (p)
                {
                    case PadInput.ButtonSouth: return Btn(1); case PadInput.ButtonEast: return Btn(2); case PadInput.ButtonWest: return Btn(0); case PadInput.ButtonNorth: return Btn(3);
                    case PadInput.LeftShoulder: return Btn(4); case PadInput.RightShoulder: return Btn(5); case PadInput.LeftTrigger: return Btn(6); case PadInput.RightTrigger: return Btn(7);
                    case PadInput.Select: return Btn(8); case PadInput.Start: return Btn(9); case PadInput.LeftStickPress: return Btn(10); case PadInput.RightStickPress: return Btn(11);
                    default: return 0f;
                }
            switch (p)
            {
                case PadInput.ButtonSouth: return Btn(0); case PadInput.ButtonEast: return Btn(1); case PadInput.ButtonWest: return Btn(2); case PadInput.ButtonNorth: return Btn(3);
                case PadInput.LeftShoulder: return Btn(4); case PadInput.RightShoulder: return Btn(5); case PadInput.Select: return Btn(6); case PadInput.Start: return Btn(7);
                case PadInput.LeftStickPress: return Btn(8); case PadInput.RightStickPress: return Btn(9);
                default: return 0f;   // sticks / triggers / d-pad are axes: need the Input System package
            }
        }
        public static Vector2 LeftStickNative() => Vector2.zero;
        public static Vector2 RightStickNative() => Vector2.zero;
#else
        public static bool HasGamepadNative => false;
        public static string PadNameNative() => null;
        public static float KeyValue(string name) => 0f;
        public static bool AnyKeyboardKeyPressed(out string name) { name = null; return false; }
        public static float PadValueNative(PadInput p) => 0f;
        public static Vector2 LeftStickNative() => Vector2.zero;
        public static Vector2 RightStickNative() => Vector2.zero;
#endif


        // ---- gamepad entry points: on the WebGL player the browser Gamepad API is used directly; elsewhere the active Unity backend ----
#if UNITY_WEBGL && !UNITY_EDITOR
        public static bool HasGamepad => WebPad.Connected;
        public static string PadName() => WebPad.Name();
        public static float PadValue(PadInput p) => WebPad.Value(p);
        public static Vector2 LeftStick() => WebPad.Left();
        public static Vector2 RightStick() => WebPad.Right();
#else
        public static bool HasGamepad => HasGamepadNative;
        public static string PadName() => PadNameNative();
        public static float PadValue(PadInput p) => PadValueNative(p);
        public static Vector2 LeftStick() => LeftStickNative();
        public static Vector2 RightStick() => RightStickNative();
#endif

        static readonly PadInput[] AllPad = (PadInput[])Enum.GetValues(typeof(PadInput));
        /// <summary>Returns the first gamepad input currently held past the digital threshold (for "press a button to bind").</summary>
        public static bool AnyPadInputHeld(out PadInput found) => AnyPadInputRaised(null, out found);

        /// <summary>Snapshot of every pad input's current value (so resting / drifting axes can be ignored while binding).</summary>
        public static float[] PadBaseline() { var b = new float[AllPad.Length]; for (int i = 0; i < b.Length; i++) b[i] = PadValue(AllPad[i]); return b; }

        /// <summary>First pad input that is past the threshold AND has risen clearly above its baseline value.</summary>
        public static bool AnyPadInputRaised(float[] baseline, out PadInput found)
        {
            found = PadInput.None;
            float best = 0f;
            for (int i = 0; i < AllPad.Length; i++)
            {
                if (AllPad[i] == PadInput.None) continue;
                float v = PadValue(AllPad[i]);
                float rise = v - (baseline != null ? baseline[i] : 0f);
                if (v >= Digital && rise >= 0.4f && v > best) { best = v; found = AllPad[i]; }
            }
            return found != PadInput.None;
        }

        /// <summary>Human-readable list of pad inputs currently active, for the live readout in the controls panel.</summary>
        public static string ActivePadInputs()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in AllPad) if (p != PadInput.None && PadValue(p) >= 0.3f) { if (sb.Length > 0) sb.Append(", "); sb.Append(InputBindings.PadLabel(p)); }
            return sb.ToString();
        }
    }
}
