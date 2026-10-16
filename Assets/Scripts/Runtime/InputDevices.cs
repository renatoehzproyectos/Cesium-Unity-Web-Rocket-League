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

        public static bool HasGamepad => Gamepad.current != null;
        public static string PadName() => Gamepad.current != null ? Gamepad.current.displayName : null;

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

        public static float PadValue(PadInput p)
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
        public static Vector2 LeftStick() { var g = Gamepad.current; return g == null ? Vector2.zero : new Vector2(Rescale(g.leftStick.x.ReadValue()), Rescale(g.leftStick.y.ReadValue())); }
        public static Vector2 RightStick() { var g = Gamepad.current; return g == null ? Vector2.zero : new Vector2(Rescale(g.rightStick.x.ReadValue()), Rescale(g.rightStick.y.ReadValue())); }
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

        public static bool HasGamepad { get { foreach (var n in Input.GetJoystickNames()) if (!string.IsNullOrEmpty(n)) return true; return false; } }
        public static string PadName() { foreach (var n in Input.GetJoystickNames()) if (!string.IsNullOrEmpty(n)) return n; return null; }

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
        public static float PadValue(PadInput p)
        {
            switch (p)
            {
                case PadInput.ButtonSouth: return Btn(0); case PadInput.ButtonEast: return Btn(1); case PadInput.ButtonWest: return Btn(2); case PadInput.ButtonNorth: return Btn(3);
                case PadInput.LeftShoulder: return Btn(4); case PadInput.RightShoulder: return Btn(5); case PadInput.Select: return Btn(6); case PadInput.Start: return Btn(7);
                case PadInput.LeftStickPress: return Btn(8); case PadInput.RightStickPress: return Btn(9);
                default: return 0f;   // sticks / triggers / d-pad are axes: need the Input System package
            }
        }
        public static Vector2 LeftStick() => Vector2.zero;
        public static Vector2 RightStick() => Vector2.zero;
#else
        public static bool HasGamepad => false;
        public static string PadName() => null;
        public static float KeyValue(string name) => 0f;
        public static bool AnyKeyboardKeyPressed(out string name) { name = null; return false; }
        public static float PadValue(PadInput p) => 0f;
        public static Vector2 LeftStick() => Vector2.zero;
        public static Vector2 RightStick() => Vector2.zero;
#endif

        static readonly PadInput[] AllPad = (PadInput[])Enum.GetValues(typeof(PadInput));
        /// <summary>Returns the first gamepad input currently held past the digital threshold (for "press a button to bind").</summary>
        public static bool AnyPadInputHeld(out PadInput found)
        {
            found = PadInput.None;
            float best = Digital;
            foreach (var p in AllPad)
            {
                if (p == PadInput.None) continue;
                float v = PadValue(p);
                if (v >= best) { best = v; found = p; }
            }
            return found != PadInput.None;
        }
    }
}
