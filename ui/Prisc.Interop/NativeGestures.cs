using System;

namespace Prisc.Interop
{
    /// <summary>
    /// Global hotkey gesture text and validation, in prisc_hotkeys' one grammar (<c>prisc_gesture_*</c>):
    /// the settings file, the standby capturer's <c>--hk-*</c> flags and its status echo all carry
    /// that text, so C# keeps no parser of its own. A gesture crosses as its raw values: a
    /// <c>HotkeyKey</c> value and <c>HotkeyModifiers</c> bits (Prisc.Shared's generated enums, which
    /// this assembly cannot see). Any thread.
    /// </summary>
    public static unsafe class NativeGestures
    {
        /// <summary>
        /// Parses gesture text: '+'-separated parts, trimmed, empty parts dropped; the last is a key
        /// name, alias or label ("PrintScreen", "Snapshot", "PrtScr"), every earlier one a modifier
        /// name or shorthand ("Control", "Ctrl", "Cmd"), all ASCII case-insensitive; a trailing '+'
        /// after "Num" is the keypad plus. Both the serialized and the display text read back.
        /// "None" is a gesture with no key (0). False for text that is no gesture, with the reason in
        /// <paramref name="error"/> ("unknown key 'X'"); null reads as the empty text.
        /// </summary>
        public static bool TryParse(string? text, out int key, out int modifiers, out string? error)
        {
            InteropLibrary.Require();
            var bytes = Utf8.Bytes(text ?? String.Empty);
            PriscGesture gesture;
            int status;
            fixed (byte* p = bytes)
            {
                status = NativeMethods.prisc_gesture_parse(p, (nuint)bytes.Length, &gesture);
            }

            if (status == (int)InteropStatus.InvalidArg)
            {
                key = 0;
                modifiers = 0;
                error = Native.LastErrorMessage();
                return false;
            }

            Native.Check(status, "prisc_gesture_parse");
            key = gesture.key;
            modifiers = (int)gesture.modifiers;
            error = null;
            return true;
        }

        /// <summary>
        /// The gesture as text in <paramref name="style"/>: <see cref="GestureStyle.Serialized"/> is
        /// the settings form ("Control+Shift+PrintScreen", "None" for no key, the same on every OS);
        /// <see cref="GestureStyle.Display"/> what a person reads, with short labels and
        /// <paramref name="os"/>'s word for Meta ("Ctrl+Shift+PrtScr", "Cmd+1"; empty for no key);
        /// <see cref="GestureStyle.ModifiersPrefix"/> the modifiers alone as Display writes them
        /// ("Ctrl+Alt+"), the key ignored. Throws <see cref="InteropException"/> (InvalidArg) for a
        /// key outside the key table or an unknown modifier bit.
        /// </summary>
        public static string Format(int key, int modifiers, GestureStyle style, GestureOs os = GestureOs.Current)
        {
            InteropLibrary.Require();
            var gesture = new PriscGesture { key = key, modifiers = (uint)modifiers };
            PriscString text = default;
            Native.Check(NativeMethods.prisc_gesture_format(&gesture, (int)style, (int)os, &text), "prisc_gesture_format");
            return Utf8.Take(ref text);
        }

        /// <summary>
        /// Checks the gesture the way registration does on <paramref name="os"/>, then against that
        /// OS's reserved gestures (Alt+Tab on Windows, Cmd+Q on macOS): the first refusal, or
        /// <see cref="GestureRefusal.None"/> when it is acceptable. Every OS's answer can be asked on
        /// any machine. Never <see cref="GestureRefusal.Duplicate"/>, which only registration knows.
        /// </summary>
        public static GestureRefusal Check(int key, int modifiers, GestureOs os = GestureOs.Current)
        {
            InteropLibrary.Require();
            var gesture = new PriscGesture { key = key, modifiers = (uint)modifiers };
            int refusal;
            Native.Check(NativeMethods.prisc_gesture_check(&gesture, (int)os, &refusal), "prisc_gesture_check");
            return (GestureRefusal)refusal;
        }
    }
}
