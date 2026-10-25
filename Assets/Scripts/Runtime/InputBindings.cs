using System;
using System.Text;
using UnityEngine;

namespace RocketWorld
{
    public enum CarAction { Accelerate, Brake, SteerLeft, SteerRight, RollLeft, RollRight, Jump, Boost, Handbrake, CameraToggle, Reset, Ball, Plane, Menu }

    /// <summary>Gamepad inputs a car action can be bound to (buttons, triggers, d-pad and the four directions of each stick).</summary>
    public enum PadInput
    {
        None = 0,
        ButtonSouth, ButtonEast, ButtonWest, ButtonNorth,
        LeftShoulder, RightShoulder, LeftTrigger, RightTrigger,
        LeftStickPress, RightStickPress, Start, Select,
        DpadUp, DpadDown, DpadLeft, DpadRight,
        LeftStickUp, LeftStickDown, LeftStickLeft, LeftStickRight,
        RightStickUp, RightStickDown, RightStickLeft, RightStickRight,
    }

    /// <summary>
    /// Rebindable control map: every <see cref="CarAction"/> has two keyboard slots and one gamepad slot. Persisted in PlayerPrefs.
    /// Keys are stored by name (the Input System's <c>Key</c> enum names; see <see cref="InputDevices"/> for the legacy-backend aliases).
    /// Pure data, no device access, so it is unit-testable.
    /// </summary>
    public class InputBindings
    {
        public const int KeySlots = 2;
        public const int SlotPad = KeySlots;            // slot index of the gamepad column
        public const int SlotCount = KeySlots + 1;
        const string P = "rw2.bind.";

        public struct Binding { public string key0, key1; public PadInput pad; }

        public static readonly CarAction[] Actions = (CarAction[])Enum.GetValues(typeof(CarAction));
        readonly Binding[] _b = new Binding[Actions.Length];

        public static string Label(CarAction a)
        {
            switch (a)
            {
                case CarAction.Accelerate: return "Accelerate";
                case CarAction.Brake: return "Brake / reverse";
                case CarAction.SteerLeft: return "Steer left";
                case CarAction.SteerRight: return "Steer right";
                case CarAction.RollLeft: return "Air roll left";
                case CarAction.RollRight: return "Air roll right";
                case CarAction.Jump: return "Jump / flip";
                case CarAction.Boost: return "Boost";
                case CarAction.Handbrake: return "Handbrake (powerslide)";
                case CarAction.CameraToggle: return "Switch camera";
                case CarAction.Reset: return "Reset car";
                case CarAction.Ball: return "Summon ball";
                case CarAction.Plane: return "Plane mode on / off";
                default: return "Settings menu";
            }
        }

        /// <summary>The menu action's keyboard keys are fixed (Esc / Tab) so you can never lock yourself out of this screen.</summary>
        public static bool KeyboardLocked(CarAction a) => a == CarAction.Menu;

        public static Binding Default(CarAction a)
        {
            switch (a)
            {
                case CarAction.Accelerate: return new Binding { key0 = "W", key1 = "UpArrow", pad = PadInput.RightTrigger };
                case CarAction.Brake: return new Binding { key0 = "S", key1 = "DownArrow", pad = PadInput.LeftTrigger };
                case CarAction.SteerLeft: return new Binding { key0 = "A", key1 = "LeftArrow", pad = PadInput.LeftStickLeft };
                case CarAction.SteerRight: return new Binding { key0 = "D", key1 = "RightArrow", pad = PadInput.LeftStickRight };
                case CarAction.RollLeft: return new Binding { key0 = "Q", pad = PadInput.LeftShoulder };
                case CarAction.RollRight: return new Binding { key0 = "E", pad = PadInput.RightShoulder };
                case CarAction.Jump: return new Binding { key0 = "Space", pad = PadInput.ButtonSouth };
                case CarAction.Boost: return new Binding { key0 = "LeftShift", key1 = "RightShift", pad = PadInput.ButtonEast };
                case CarAction.Handbrake: return new Binding { key0 = "LeftCtrl", key1 = "X", pad = PadInput.ButtonWest };
                case CarAction.CameraToggle: return new Binding { key0 = "C", pad = PadInput.ButtonNorth };
                case CarAction.Reset: return new Binding { key0 = "R", pad = PadInput.Select };
                case CarAction.Ball: return new Binding { key0 = "B", pad = PadInput.RightStickPress };
                case CarAction.Plane: return new Binding { key0 = "P", pad = PadInput.LeftStickPress };
                default: return new Binding { pad = PadInput.Start };
            }
        }

        public InputBindings() { ResetAll(); }

        public Binding Get(CarAction a) => _b[(int)a];
        public string GetKey(CarAction a, int slot) => slot == 0 ? _b[(int)a].key0 : _b[(int)a].key1;
        public PadInput GetPad(CarAction a) => _b[(int)a].pad;

        public void ResetAll() { foreach (var a in Actions) _b[(int)a] = Default(a); }
        public void Reset(CarAction a) { _b[(int)a] = Default(a); }

        public void Clear(CarAction a, int slot)
        {
            var b = _b[(int)a];
            if (slot == 0) b.key0 = ""; else if (slot == 1) b.key1 = ""; else b.pad = PadInput.None;
            _b[(int)a] = b;
        }

        /// <summary>Binds a key. The same key is removed from any other action (one key, one job). Returns the action it was taken from, if any.</summary>
        public CarAction? SetKey(CarAction a, int slot, string key)
        {
            if (string.IsNullOrEmpty(key) || KeyboardLocked(a)) return null;
            CarAction? stolen = null;
            foreach (var o in Actions)
            {
                if (KeyboardLocked(o)) continue;
                var b = _b[(int)o];
                bool hit = false;
                if (b.key0 == key && !(o == a && slot == 0)) { b.key0 = ""; hit = true; }
                if (b.key1 == key && !(o == a && slot == 1)) { b.key1 = ""; hit = true; }
                if (hit) { _b[(int)o] = b; if (o != a) stolen = o; }
            }
            var t = _b[(int)a];
            if (slot == 0) t.key0 = key; else t.key1 = key;
            _b[(int)a] = t;
            return stolen;
        }

        /// <summary>Binds a gamepad input; it is removed from any other action. Returns the action it was taken from, if any.</summary>
        public CarAction? SetPad(CarAction a, PadInput pad)
        {
            if (pad == PadInput.None) return null;
            CarAction? stolen = null;
            foreach (var o in Actions)
            {
                if (o == a) continue;
                var b = _b[(int)o];
                if (b.pad == pad) { b.pad = PadInput.None; _b[(int)o] = b; stolen = o; }
            }
            var t = _b[(int)a]; t.pad = pad; _b[(int)a] = t;
            return stolen;
        }

        // ---- persistence -------------------------------------------------------------------------------------------------
        public static string Encode(Binding b) => (b.key0 ?? "") + "|" + (b.key1 ?? "") + "|" + (int)b.pad;

        public static bool TryDecode(string s, out Binding b)
        {
            b = default;
            if (string.IsNullOrEmpty(s)) return false;
            var parts = s.Split('|');
            if (parts.Length != 3 || !int.TryParse(parts[2], out int pad) || pad < 0 || !Enum.IsDefined(typeof(PadInput), pad)) return false;
            b = new Binding { key0 = parts[0], key1 = parts[1], pad = (PadInput)pad };
            return true;
        }

        public static InputBindings Load()
        {
            var ib = new InputBindings();
            foreach (var a in Actions)
                if (TryDecode(PlayerPrefs.GetString(P + a, ""), out var b))
                {
                    if (KeyboardLocked(a)) { var d = Default(a); b.key0 = d.key0; b.key1 = d.key1; }
                    ib._b[(int)a] = b;
                }
            return ib;
        }

        public void Save()
        {
            foreach (var a in Actions) PlayerPrefs.SetString(P + a, Encode(_b[(int)a]));
            PlayerPrefs.Save();
        }

        // ---- display -----------------------------------------------------------------------------------------------------
        public static string KeyLabel(string key)
        {
            if (string.IsNullOrEmpty(key)) return "—";
            switch (key)
            {
                case "UpArrow": return "↑"; case "DownArrow": return "↓"; case "LeftArrow": return "←"; case "RightArrow": return "→";
                case "LeftShift": return "L-Shift"; case "RightShift": return "R-Shift";
                case "LeftCtrl": case "LeftControl": return "L-Ctrl"; case "RightCtrl": case "RightControl": return "R-Ctrl";
                case "LeftAlt": return "L-Alt"; case "RightAlt": return "R-Alt";
                case "Backquote": return "`"; case "Minus": return "-"; case "Equals": return "=";
                case "Comma": return ","; case "Period": return "."; case "Slash": return "/"; case "Semicolon": return ";";
            }
            if (key.Length == 6 && key.StartsWith("Digit")) return key.Substring(5);
            if (key.StartsWith("Numpad")) return "Num " + key.Substring(6);
            var sb = new StringBuilder();
            for (int i = 0; i < key.Length; i++) { if (i > 0 && char.IsUpper(key[i]) && !char.IsUpper(key[i - 1])) sb.Append(' '); sb.Append(key[i]); }
            return sb.ToString();
        }

        public static string PadLabel(PadInput p)
        {
            switch (p)
            {
                case PadInput.None: return "—";
                case PadInput.ButtonSouth: return "A / ✕";
                case PadInput.ButtonEast: return "B / ○";
                case PadInput.ButtonWest: return "X / □";
                case PadInput.ButtonNorth: return "Y / △";
                case PadInput.LeftShoulder: return "LB / L1";
                case PadInput.RightShoulder: return "RB / R1";
                case PadInput.LeftTrigger: return "LT / L2";
                case PadInput.RightTrigger: return "RT / R2";
                case PadInput.LeftStickPress: return "L3";
                case PadInput.RightStickPress: return "R3";
                case PadInput.Start: return "Start";
                case PadInput.Select: return "Back / Select";
                case PadInput.DpadUp: return "D-pad ↑"; case PadInput.DpadDown: return "D-pad ↓";
                case PadInput.DpadLeft: return "D-pad ←"; case PadInput.DpadRight: return "D-pad →";
                case PadInput.LeftStickUp: return "L-stick ↑"; case PadInput.LeftStickDown: return "L-stick ↓";
                case PadInput.LeftStickLeft: return "L-stick ←"; case PadInput.LeftStickRight: return "L-stick →";
                case PadInput.RightStickUp: return "R-stick ↑"; case PadInput.RightStickDown: return "R-stick ↓";
                case PadInput.RightStickLeft: return "R-stick ←"; default: return "R-stick →";
            }
        }
    }
}
