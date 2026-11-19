using System;
using System.Text;
using UnityEngine;

namespace RocketWorld
{
    public enum CarAction { Accelerate, Brake, SteerLeft, SteerRight, RollLeft, RollRight, Jump, Boost, Handbrake, CameraToggle, Reset, Ball, Plane, Vehicle, HyperBoost, VehicleMode, Gear, Flaps, Menu, Fpv, Door }

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
                case CarAction.Accelerate: return "Acelerar";
                case CarAction.Brake: return "Frenar / marcha atrás";
                case CarAction.SteerLeft: return "Girar a la izquierda";
                case CarAction.SteerRight: return "Girar a la derecha";
                case CarAction.RollLeft: return "Rodar en el aire (izq.)";
                case CarAction.RollRight: return "Rodar en el aire (der.)";
                case CarAction.Jump: return "Saltar / voltereta";
                case CarAction.Boost: return "Turbo";
                case CarAction.Handbrake: return "Freno de mano (derrapar)";
                case CarAction.CameraToggle: return "Cambiar de cámara";
                case CarAction.Reset: return "Rescatar el coche";
                case CarAction.Ball: return "Invocar la pelota";
                case CarAction.Plane: return "Modo avión: sí / no";
                case CarAction.Vehicle: return "Siguiente vehículo";
                case CarAction.HyperBoost: return "Hiper turbo (+500 % de velocidad)";
                case CarAction.VehicleMode: return "Modo VTOL / flotar";
                case CarAction.Gear: return "Tren de aterrizaje";
                case CarAction.Flaps: return "Flaps (alerones de aterrizaje)";
                case CarAction.Fpv: return "Dron FPV: sí / no";
                case CarAction.Door: return "Abrir / cerrar rampa (Antonov)";
                default: return "Menú de ajustes";
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
                case CarAction.Vehicle: return new Binding { key0 = "V", pad = PadInput.DpadRight };
                case CarAction.HyperBoost: return new Binding { key0 = "H", pad = PadInput.DpadUp };
                case CarAction.VehicleMode: return new Binding { key0 = "M", pad = PadInput.DpadDown };
                case CarAction.Gear: return new Binding { key0 = "G", pad = PadInput.DpadLeft };
                case CarAction.Flaps: return new Binding { key0 = "F" };
                case CarAction.Fpv: return new Binding { key0 = "K" };
                case CarAction.Door: return new Binding { key0 = "O" };
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

        /// <summary>Binds a key. The same key may be bound to several actions at once. Returns null (kept for API compatibility).</summary>
        public CarAction? SetKey(CarAction a, int slot, string key)
        {
            if (string.IsNullOrEmpty(key) || KeyboardLocked(a)) return null;
            var t = _b[(int)a];
            if (slot == 0) t.key0 = key; else t.key1 = key;
            _b[(int)a] = t;
            return null;
        }

        /// <summary>Binds a gamepad input. The same input may be bound to several actions at once. Returns null.</summary>
        public CarAction? SetPad(CarAction a, PadInput pad)
        {
            if (pad == PadInput.None) return null;
            var t = _b[(int)a]; t.pad = pad; _b[(int)a] = t;
            return null;
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
                case "UpArrow": return "Flecha arriba"; case "DownArrow": return "Flecha abajo"; case "LeftArrow": return "Flecha izq."; case "RightArrow": return "Flecha der.";
                case "Space": return "Espacio"; case "Enter": case "Return": return "Intro"; case "Backspace": return "Retroceso"; case "Escape": return "Esc";
                case "Delete": return "Supr"; case "CapsLock": return "Bloq Mayús"; case "PageUp": return "Re Pág"; case "PageDown": return "Av Pág"; case "Home": return "Inicio"; case "End": return "Fin";
                case "LeftShift": return "Shift izq."; case "RightShift": return "Shift der.";
                case "LeftCtrl": case "LeftControl": return "Ctrl izq."; case "RightCtrl": case "RightControl": return "Ctrl der.";
                case "LeftAlt": return "Alt izq."; case "RightAlt": return "Alt der.";
                case "Backquote": return "`"; case "Minus": return "-"; case "Equals": return "=";
                case "Comma": return ","; case "Period": return "."; case "Slash": return "/"; case "Semicolon": return ";";
            }
            if (key.Length == 6 && key.StartsWith("Digit")) return key.Substring(5);
            if (key.StartsWith("Numpad")) return "Teclado num. " + key.Substring(6);
            var sb = new StringBuilder();
            for (int i = 0; i < key.Length; i++) { if (i > 0 && char.IsUpper(key[i]) && !char.IsUpper(key[i - 1])) sb.Append(' '); sb.Append(key[i]); }
            return sb.ToString();
        }

        public static string PadLabel(PadInput p)
        {
            switch (p)
            {
                case PadInput.None: return "—";
                case PadInput.ButtonSouth: return "A / Cross";
                case PadInput.ButtonEast: return "B / Circle";
                case PadInput.ButtonWest: return "X / Square";
                case PadInput.ButtonNorth: return "Y / Triangle";
                case PadInput.LeftShoulder: return "LB / L1";
                case PadInput.RightShoulder: return "RB / R1";
                case PadInput.LeftTrigger: return "LT / L2";
                case PadInput.RightTrigger: return "RT / R2";
                case PadInput.LeftStickPress: return "L3";
                case PadInput.RightStickPress: return "R3";
                case PadInput.Start: return "Start";
                case PadInput.Select: return "Atrás / Select";
                case PadInput.DpadUp: return "Cruceta arriba"; case PadInput.DpadDown: return "Cruceta abajo";
                case PadInput.DpadLeft: return "Cruceta izq."; case PadInput.DpadRight: return "Cruceta der.";
                case PadInput.LeftStickUp: return "Stick izq. arriba"; case PadInput.LeftStickDown: return "Stick izq. abajo";
                case PadInput.LeftStickLeft: return "Stick izq. izq."; case PadInput.LeftStickRight: return "Stick izq. der.";
                case PadInput.RightStickUp: return "Stick der. arriba"; case PadInput.RightStickDown: return "Stick der. abajo";
                case PadInput.RightStickLeft: return "Stick der. izq."; default: return "Stick der. der.";
            }
        }
    }
}
