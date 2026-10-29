using System;
using System.Collections.Generic;

namespace Prisc.Interop
{
    /// <summary>The keyboard as the OS sees it right now (<c>prisc_keyboard_*</c>): physical key state
    /// and macOS Secure Event Input. Any thread.</summary>
    public static unsafe class NativeKeyboard
    {
        /// <summary>The most codes one <see cref="KeysDown"/> call answers.</summary>
        public const int MaxCodes = 64;

        /// <summary>
        /// Which of <paramref name="codes"/> are physically down right now, in one call: bit i is set
        /// when <c>codes[i]</c> is held. The codes are the hotkey hook's raw codes: Windows
        /// virtual-key codes (mouse buttons included) or macOS CGKeyCodes. A code the OS cannot answer
        /// for (outside its range; every code on an OS without a backend) reads as down, so nothing is
        /// released without evidence. Throws <see cref="ArgumentException"/> for more than
        /// <see cref="MaxCodes"/> codes.
        /// </summary>
        public static ulong KeysDown(IReadOnlyList<int> codes)
        {
            ArgumentNullException.ThrowIfNull(codes);
            if (codes.Count > MaxCodes)
                throw new ArgumentException($"at most {MaxCodes} codes per call, got {codes.Count}", nameof(codes));

            InteropLibrary.Require();
            uint* raw = stackalloc uint[MaxCodes];
            for (int i = 0; i < codes.Count; i++)
                raw[i] = unchecked((uint)codes[i]);

            ulong mask;
            Native.Check(NativeMethods.prisc_keyboard_keys_down(raw, (nuint)codes.Count, &mask), "prisc_keyboard_keys_down");
            return mask;
        }

        /// <summary>Whether macOS Secure Event Input is on now (a focused password field, a terminal's
        /// "Secure Keyboard Entry", in any app): while it is, the OS hides every key from event taps.
        /// False off macOS.</summary>
        public static bool SecureInputEnabled()
        {
            InteropLibrary.Require();
            byte enabled;
            Native.Check(NativeMethods.prisc_keyboard_secure_input_enabled(&enabled), "prisc_keyboard_secure_input_enabled");
            return enabled != 0;
        }
    }
}
