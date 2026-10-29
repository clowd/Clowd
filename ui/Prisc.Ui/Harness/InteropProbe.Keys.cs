using System;
using System.Collections.Generic;
using Prisc.Interop;

namespace Prisc.UI.Harness
{
    internal static partial class InteropProbe
    {
        /// <summary>The wrapper-level items of this area: one success, one documented failure and one
        /// round trip per export family, keyed <c>&lt;area&gt;.&lt;item&gt;</c>. Every gesture and
        /// keyboard export is any-thread, so every item also runs from a pool thread. The secure input
        /// item is what the E2E oracle (ioreg kCGSSessionSecureInputPID) checks.</summary>
        static partial void ProbeKeys(ProbeRun run)
        {
            // gestures
            run.Item("keys.gesture.parse", () => NativeGestures.TryParse("ctrl+shift+prtscr", out var key, out var modifiers, out var error)
                ? new Dictionary<string, object> { ["key"] = ((HotkeyKey)key).ToString(), ["modifiers"] = ((HotkeyModifiers)modifiers).ToString() }
                : throw new InvalidOperationException(error), anyThread: true);
            run.Item("keys.gesture.parse_refused", () => NativeGestures.TryParse("Hyper+A", out _, out _, out var error)
                ? throw new InvalidOperationException("Hyper+A parsed")
                : error, anyThread: true);
            run.Item("keys.gesture.format", () =>
            {
                const int key = (int)HotkeyKey.D1;
                const int modifiers = (int)(HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Meta);
                return new Dictionary<string, object>
                {
                    ["serialized"] = NativeGestures.Format(key, modifiers, GestureStyle.Serialized),
                    ["display"] = NativeGestures.Format(key, modifiers, GestureStyle.Display),
                    ["display.windows"] = NativeGestures.Format(key, modifiers, GestureStyle.Display, GestureOs.Windows),
                    ["display.macos"] = NativeGestures.Format(key, modifiers, GestureStyle.Display, GestureOs.MacOS),
                    ["prefix"] = NativeGestures.Format(key, modifiers, GestureStyle.ModifiersPrefix),
                };
            }, anyThread: true);
            run.Item("keys.gesture.format_bad_key", () => KeysExpectFailure(
                () => NativeGestures.Format(100_004, 0, GestureStyle.Serialized), InteropStatus.InvalidArg), anyThread: true);
            run.Item("keys.gesture.round_trip", () =>
            {
                var display = NativeGestures.Format((int)HotkeyKey.Add, (int)HotkeyModifiers.Control, GestureStyle.Display);
                if (!NativeGestures.TryParse(display, out var key, out var modifiers, out var error))
                    throw new InvalidOperationException(error);
                return NativeGestures.Format(key, modifiers, GestureStyle.Serialized);
            }, anyThread: true);
            run.Item("keys.gesture.check", () => new Dictionary<string, object>
            {
                ["windows.alt_tab"] = NativeGestures.Check((int)HotkeyKey.Tab, (int)HotkeyModifiers.Alt, GestureOs.Windows).ToString(),
                ["macos.cmd_q"] = NativeGestures.Check((int)HotkeyKey.Q, (int)HotkeyModifiers.Meta, GestureOs.MacOS).ToString(),
                ["macos.prtscr"] = NativeGestures.Check((int)HotkeyKey.PrintScreen, 0, GestureOs.MacOS).ToString(),
                ["current.ctrl_shift_a"] = NativeGestures.Check((int)HotkeyKey.A, (int)(HotkeyModifiers.Control | HotkeyModifiers.Shift)).ToString(),
                ["current.left_shift"] = NativeGestures.Check((int)HotkeyKey.LeftShift, 0).ToString(),
            }, anyThread: true);

            // keyboard
            run.Item("keys.keyboard.keys_down", () =>
            {
                // Escape and A in this OS's numbering (VK / CGKeyCode); the probe runs with no key held
                int[] codes = OperatingSystem.IsWindows() ? [0x1B, 0x41] : [0x35, 0x00];
                return "0x" + NativeKeyboard.KeysDown(codes).ToString("X");
            }, anyThread: true);
            run.Item("keys.keyboard.keys_down_too_many", () =>
            {
                try
                {
                    NativeKeyboard.KeysDown(new int[NativeKeyboard.MaxCodes + 1]);
                }
                catch (ArgumentException)
                {
                    return "refused";
                }
                throw new InvalidOperationException("65 codes were accepted");
            }, anyThread: true);
            run.Item("keys.keyboard.secure_input", () => NativeKeyboard.SecureInputEnabled(), anyThread: true);
        }

        /// <summary>A call that must fail with <paramref name="expected"/>: ok with the status when it
        /// does, an error when it succeeds or fails differently.</summary>
        private static object KeysExpectFailure(Func<object> call, InteropStatus expected)
        {
            try
            {
                call();
            }
            catch (InteropException e) when (e.Status == expected)
            {
                return e.Status.ToString();
            }
            throw new InvalidOperationException($"expected {expected}");
        }
    }
}
