using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Runtime-safe keyboard hotkey access for projects using either the legacy Input Manager,
    /// the New Input System, or Android builds without a keyboard attached.
    /// </summary>
    public static class RuntimeHotkeyInput
    {
        public static bool GetKeyDown(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM
            if (TryGetNewInputKey(key, out Key mappedKey))
            {
                Keyboard keyboard = Keyboard.current;
                return keyboard != null && keyboard[mappedKey].wasPressedThisFrame;
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(key);
#else
            return false;
#endif
        }

        public static bool GetKey(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM
            if (TryGetNewInputKey(key, out Key mappedKey))
            {
                Keyboard keyboard = Keyboard.current;
                return keyboard != null && keyboard[mappedKey].isPressed;
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKey(key);
#else
            return false;
#endif
        }

        public static bool GetKeyUp(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM
            if (TryGetNewInputKey(key, out Key mappedKey))
            {
                Keyboard keyboard = Keyboard.current;
                return keyboard != null && keyboard[mappedKey].wasReleasedThisFrame;
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyUp(key);
#else
            return false;
#endif
        }

#if ENABLE_INPUT_SYSTEM
        private static bool TryGetNewInputKey(KeyCode keyCode, out Key key)
        {
            switch (keyCode)
            {
                case KeyCode.A:
                    key = Key.A;
                    return true;
                case KeyCode.B:
                    key = Key.B;
                    return true;
                case KeyCode.C:
                    key = Key.C;
                    return true;
                case KeyCode.D:
                    key = Key.D;
                    return true;
                case KeyCode.E:
                    key = Key.E;
                    return true;
                case KeyCode.F:
                    key = Key.F;
                    return true;
                case KeyCode.G:
                    key = Key.G;
                    return true;
                case KeyCode.H:
                    key = Key.H;
                    return true;
                case KeyCode.I:
                    key = Key.I;
                    return true;
                case KeyCode.J:
                    key = Key.J;
                    return true;
                case KeyCode.K:
                    key = Key.K;
                    return true;
                case KeyCode.L:
                    key = Key.L;
                    return true;
                case KeyCode.M:
                    key = Key.M;
                    return true;
                case KeyCode.N:
                    key = Key.N;
                    return true;
                case KeyCode.O:
                    key = Key.O;
                    return true;
                case KeyCode.P:
                    key = Key.P;
                    return true;
                case KeyCode.Q:
                    key = Key.Q;
                    return true;
                case KeyCode.R:
                    key = Key.R;
                    return true;
                case KeyCode.S:
                    key = Key.S;
                    return true;
                case KeyCode.T:
                    key = Key.T;
                    return true;
                case KeyCode.U:
                    key = Key.U;
                    return true;
                case KeyCode.V:
                    key = Key.V;
                    return true;
                case KeyCode.W:
                    key = Key.W;
                    return true;
                case KeyCode.X:
                    key = Key.X;
                    return true;
                case KeyCode.Y:
                    key = Key.Y;
                    return true;
                case KeyCode.Z:
                    key = Key.Z;
                    return true;
                case KeyCode.Alpha0:
                    key = Key.Digit0;
                    return true;
                case KeyCode.Alpha1:
                    key = Key.Digit1;
                    return true;
                case KeyCode.Alpha2:
                    key = Key.Digit2;
                    return true;
                case KeyCode.Alpha3:
                    key = Key.Digit3;
                    return true;
                case KeyCode.Alpha4:
                    key = Key.Digit4;
                    return true;
                case KeyCode.Alpha5:
                    key = Key.Digit5;
                    return true;
                case KeyCode.Alpha6:
                    key = Key.Digit6;
                    return true;
                case KeyCode.Alpha7:
                    key = Key.Digit7;
                    return true;
                case KeyCode.Alpha8:
                    key = Key.Digit8;
                    return true;
                case KeyCode.Alpha9:
                    key = Key.Digit9;
                    return true;
                case KeyCode.Space:
                    key = Key.Space;
                    return true;
                case KeyCode.LeftShift:
                    key = Key.LeftShift;
                    return true;
                case KeyCode.RightShift:
                    key = Key.RightShift;
                    return true;
                case KeyCode.LeftControl:
                    key = Key.LeftCtrl;
                    return true;
                case KeyCode.RightControl:
                    key = Key.RightCtrl;
                    return true;
                case KeyCode.LeftAlt:
                    key = Key.LeftAlt;
                    return true;
                case KeyCode.RightAlt:
                    key = Key.RightAlt;
                    return true;
                case KeyCode.Escape:
                    key = Key.Escape;
                    return true;
                case KeyCode.Return:
                    key = Key.Enter;
                    return true;
                case KeyCode.KeypadEnter:
                    key = Key.NumpadEnter;
                    return true;
                case KeyCode.Tab:
                    key = Key.Tab;
                    return true;
                case KeyCode.Backspace:
                    key = Key.Backspace;
                    return true;
                case KeyCode.Delete:
                    key = Key.Delete;
                    return true;
                case KeyCode.UpArrow:
                    key = Key.UpArrow;
                    return true;
                case KeyCode.DownArrow:
                    key = Key.DownArrow;
                    return true;
                case KeyCode.LeftArrow:
                    key = Key.LeftArrow;
                    return true;
                case KeyCode.RightArrow:
                    key = Key.RightArrow;
                    return true;
                default:
                    key = Key.None;
                    return false;
            }
        }
#endif
    }
}
