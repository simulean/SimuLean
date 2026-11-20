using UnityEngine;
using UnityEngine.InputSystem;

namespace SimuLean.Unity
{
    /// <summary>
    /// The run-time tools' keyboard and mouse, read through the Input System package (the project's active input
    /// handling): the one place that talks to it, as <see cref="UnityCompat"/> is for object lookup. The tools keep their
    /// keys as <see cref="KeyCode"/> fields (scenes and presets store them), mapped here to the Input System's keys; a key
    /// with no counterpart, or no keyboard / mouse, reads as not pressed. Positions are screen pixels with the origin at
    /// the bottom left, and the wheel is in notches, as the old <c>Input</c> class gave them.
    /// </summary>
    public static class SimInput
    {
        /// <summary>The pointer's position in screen pixels (bottom-left origin); zero without a mouse.</summary>
        public static Vector3 MousePosition
        {
            get
            {
                var mouse = Mouse.current;
                return mouse == null ? Vector3.zero : (Vector3)mouse.position.ReadValue();
            }
        }

        /// <summary>The wheel this frame in notches (one detent ≈ 1, as the old <c>Input.mouseScrollDelta</c>).</summary>
        public static Vector2 MouseScrollDelta
        {
            get
            {
                var mouse = Mouse.current;
                if (mouse == null) return Vector2.zero;
                Vector2 raw = mouse.scroll.ReadValue();
                // the Input System reports the platform's raw delta (120 per detent on Windows, ~1 elsewhere)
                return Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer
                    ? raw / 120f : raw;
            }
        }

        /// <summary>Whether mouse button <paramref name="button"/> (0 left, 1 right, 2 middle) is held.</summary>
        public static bool GetMouseButton(int button) => Button(button)?.isPressed ?? false;

        /// <summary>Whether mouse button <paramref name="button"/> went down this frame.</summary>
        public static bool GetMouseButtonDown(int button) => Button(button)?.wasPressedThisFrame ?? false;

        /// <summary>Whether mouse button <paramref name="button"/> went up this frame.</summary>
        public static bool GetMouseButtonUp(int button) => Button(button)?.wasReleasedThisFrame ?? false;

        /// <summary>Whether <paramref name="key"/> is held.</summary>
        public static bool GetKey(KeyCode key) => Control(key)?.isPressed ?? false;

        /// <summary>Whether <paramref name="key"/> went down this frame.</summary>
        public static bool GetKeyDown(KeyCode key) => Control(key)?.wasPressedThisFrame ?? false;

        private static UnityEngine.InputSystem.Controls.ButtonControl Button(int button)
        {
            var mouse = Mouse.current;
            if (mouse == null) return null;
            switch (button)
            {
                case 0: return mouse.leftButton;
                case 1: return mouse.rightButton;
                case 2: return mouse.middleButton;
                default: return null;
            }
        }

        private static UnityEngine.InputSystem.Controls.KeyControl Control(KeyCode code)
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return null;
            Key key = ToKey(code);
            return key == Key.None ? null : keyboard[key];
        }

        /// <summary>The Input System key of <paramref name="code"/> (<see cref="Key.None"/> when there is none).</summary>
        public static Key ToKey(KeyCode code)
        {
            if (code >= KeyCode.A && code <= KeyCode.Z) return Key.A + (code - KeyCode.A);
            if (code >= KeyCode.Alpha0 && code <= KeyCode.Alpha9) return code == KeyCode.Alpha0 ? Key.Digit0 : Key.Digit1 + (code - KeyCode.Alpha1);
            if (code >= KeyCode.Keypad0 && code <= KeyCode.Keypad9) return Key.Numpad0 + (code - KeyCode.Keypad0);
            if (code >= KeyCode.F1 && code <= KeyCode.F12) return Key.F1 + (code - KeyCode.F1);
            switch (code)
            {
                case KeyCode.Space: return Key.Space;
                case KeyCode.Return: return Key.Enter;
                case KeyCode.KeypadEnter: return Key.NumpadEnter;
                case KeyCode.Escape: return Key.Escape;
                case KeyCode.Tab: return Key.Tab;
                case KeyCode.Backspace: return Key.Backspace;
                case KeyCode.Delete: return Key.Delete;
                case KeyCode.Insert: return Key.Insert;
                case KeyCode.Home: return Key.Home;
                case KeyCode.End: return Key.End;
                case KeyCode.PageUp: return Key.PageUp;
                case KeyCode.PageDown: return Key.PageDown;
                case KeyCode.UpArrow: return Key.UpArrow;
                case KeyCode.DownArrow: return Key.DownArrow;
                case KeyCode.LeftArrow: return Key.LeftArrow;
                case KeyCode.RightArrow: return Key.RightArrow;
                case KeyCode.LeftShift: return Key.LeftShift;
                case KeyCode.RightShift: return Key.RightShift;
                case KeyCode.LeftControl: return Key.LeftCtrl;
                case KeyCode.RightControl: return Key.RightCtrl;
                case KeyCode.LeftAlt: return Key.LeftAlt;
                case KeyCode.RightAlt: return Key.RightAlt;
                case KeyCode.Minus: return Key.Minus;
                case KeyCode.Equals: return Key.Equals;
                case KeyCode.Plus: return Key.Equals;          // the '+' of the main row is Shift + '='
                case KeyCode.KeypadPlus: return Key.NumpadPlus;
                case KeyCode.KeypadMinus: return Key.NumpadMinus;
                case KeyCode.KeypadMultiply: return Key.NumpadMultiply;
                case KeyCode.KeypadDivide: return Key.NumpadDivide;
                case KeyCode.KeypadPeriod: return Key.NumpadPeriod;
                case KeyCode.Comma: return Key.Comma;
                case KeyCode.Period: return Key.Period;
                case KeyCode.Slash: return Key.Slash;
                case KeyCode.Backslash: return Key.Backslash;
                case KeyCode.Semicolon: return Key.Semicolon;
                case KeyCode.Quote: return Key.Quote;
                case KeyCode.BackQuote: return Key.Backquote;
                case KeyCode.LeftBracket: return Key.LeftBracket;
                case KeyCode.RightBracket: return Key.RightBracket;
                default: return Key.None;
            }
        }
    }
}
