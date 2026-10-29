using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Input;
using Prisc.Interop;

namespace Prisc
{
    /// <summary>
    /// A global hotkey: a <see cref="HotkeyKey"/> and the <see cref="HotkeyModifiers"/> held with
    /// it. The key names and the serialized form are the prisc_hotkeys crate's, so the settings
    /// file, the standby capturer's <c>--hk-*</c> flags and its status echo all carry the same
    /// text, and nothing translates between them. In-app shortcuts are a different type
    /// (<see cref="SimpleKeyGesture"/>, on Avalonia keys); the only bridge is the display-only
    /// <see cref="ToKeyGestureOrNull"/>.
    /// </summary>
    public sealed class HotkeyGesture : IEquatable<HotkeyGesture>
    {
        private static readonly HotkeyModifiers[] ModifierOrder =
        {
            HotkeyModifiers.Control, HotkeyModifiers.Alt, HotkeyModifiers.Shift, HotkeyModifiers.Meta,
        };

        public HotkeyKey Key { get; }

        public HotkeyModifiers Modifiers { get; }

        public HotkeyGesture(HotkeyKey key, HotkeyModifiers modifiers = HotkeyModifiers.None)
        {
            Key = key;
            Modifiers = modifiers;
        }

        /// <summary>
        /// Parses with the prisc_hotkeys crate's grammar (<see cref="NativeGestures.TryParse"/>):
        /// '+'-separated parts, trimmed, empty parts dropped; the last is a key name, alias or label
        /// ("PrintScreen", "Snapshot", "PrtScr"), every earlier one a modifier name or shorthand
        /// ("Control", "Ctrl", "Cmd", ...), all ASCII case-insensitive; a trailing '+' after "Num" is
        /// the keypad plus ("Ctrl+Num+"). So both <see cref="ToSerializedString"/> and
        /// <see cref="ToString"/> read back. Null for anything it cannot read: the gesture is then
        /// treated as not set. "None" is a gesture with no key.
        /// </summary>
        public static HotkeyGesture Parse(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return null;

            if (!NativeGestures.TryParse(value, out var key, out var modifiers, out var error))
            {
                Debug.WriteLine($"[Hotkeys] '{value}' is not a gesture: {error}");
                return null;
            }

            return new HotkeyGesture((HotkeyKey)key, (HotkeyModifiers)modifiers);
        }

        /// <summary>
        /// The settings-file form, identical to the crate's <c>Gesture</c> Display: the modifiers
        /// held, in the order Control, Alt, Shift, Meta, then the key's canonical name, joined with
        /// '+': "Control+Shift+PrintScreen".
        /// </summary>
        public string ToSerializedString() => NativeGestures.Format((int)Key, (int)Modifiers, GestureStyle.Serialized);

        /// <summary>
        /// Display text with the crate's short labels ("Ctrl+Shift+PrtScr", "Win+1"), empty for a
        /// gesture with no key. <see cref="Parse"/> reads it back, but settings store
        /// <see cref="ToSerializedString"/>. Meta reads as the key it is on this OS: Win on Windows,
        /// Cmd on macOS, Super elsewhere.
        /// </summary>
        public override string ToString() => NativeGestures.Format((int)Key, (int)Modifiers, GestureStyle.Display);

        /// <summary>
        /// This gesture as an Avalonia <see cref="KeyGesture"/>, for display surfaces that only take
        /// one (the tray menu's <c>NativeMenuItem.Gesture</c>) — never for matching keys. Today
        /// <see cref="HotkeyKey"/> mirrors Avalonia's <see cref="Avalonia.Input.Key"/> one to one, so
        /// this goes by name; the two may drift apart, and then it returns null and the caller shows
        /// no shortcut. <c>HotkeyAvaloniaDriftTests</c> fails when they do.
        /// </summary>
        public KeyGesture ToKeyGestureOrNull()
        {
            if (Key == HotkeyKey.None
                || !Enum.TryParse<Avalonia.Input.Key>(HotkeyKeys.Name(Key), out var key)
                || !Enum.IsDefined(key))
                return null;

            var modifiers = KeyModifiers.None;
            foreach (var modifier in ModifierOrder)
            {
                if ((Modifiers & modifier) == 0)
                    continue;
                if (!Enum.TryParse<KeyModifiers>(modifier.ToString(), out var avaloniaModifier))
                    return null;
                modifiers |= avaloniaModifier;
            }

            return new KeyGesture(key, modifiers);
        }

        /// <summary>The held modifiers as a display prefix ("Ctrl+Alt+Shift+Win+"), for the
        /// recording preview; empty for none.</summary>
        public static string ModifiersText(HotkeyModifiers modifiers) =>
            NativeGestures.Format((int)HotkeyKey.None, (int)modifiers, GestureStyle.ModifiersPrefix);

        /// <summary>
        /// True for the combinations the OS needs for itself, which are never bound: recording one
        /// is treated as a cancel (<c>GestureField</c>), and one already in the settings is refused
        /// at registration (<c>HotkeyManager</c>) and left off the standby capturer's command line.
        /// The recorder's hook swallows the key it records, so these are exactly what a user
        /// presses to get away from a field that is listening, and a binding would then swallow
        /// them in every app. On Windows that is task switching (Alt+Tab, Alt+Shift+Tab, Alt+Esc,
        /// Ctrl+Esc) and Task Manager (Ctrl+Shift+Esc); on macOS app and window switching (Cmd+Tab,
        /// Cmd+Shift+Tab, Cmd+`, Cmd+Shift+`), Spotlight (Cmd+Space), leaving the window (Cmd+Q,
        /// Cmd+W, Cmd+H, Cmd+M), and the ways out of a hung app (Force Quit, Cmd+Opt+Esc; Lock
        /// Screen, Ctrl+Cmd+Q). Cmd+Shift+3/4/5 stay bindable: taking over the screenshot keys is a
        /// screenshot tool's business. False on every other OS. The table is the prisc_hotkeys
        /// crate's (<c>reserved.rs</c>), asked through <see cref="NativeGestures.Check"/>.
        /// </summary>
        public static bool IsSystemReserved(HotkeyGesture gesture) => IsSystemReserved(gesture, CurrentPlatform);

        /// <summary>The OS the per-platform answers (reserved gestures, default hotkeys) are given
        /// for at run time.</summary>
        internal static OSPlatform CurrentPlatform =>
            OperatingSystem.IsWindows() ? OSPlatform.Windows
            : OperatingSystem.IsMacOS() ? OSPlatform.OSX
            : OSPlatform.Linux;

        /// <summary><see cref="IsSystemReserved(HotkeyGesture)"/> as it answers on
        /// <paramref name="platform"/>, so every OS's list can be tested on any one.</summary>
        public static bool IsSystemReserved(HotkeyGesture gesture, OSPlatform platform)
        {
            if (gesture == null)
                return false;

            var os = platform == OSPlatform.Windows ? GestureOs.Windows
                : platform == OSPlatform.OSX ? GestureOs.MacOS
                : GestureOs.Linux;
            return NativeGestures.Check((int)gesture.Key, (int)gesture.Modifiers, os) == GestureRefusal.Reserved;
        }

        public bool Equals(HotkeyGesture other) => other != null && other.Key == Key && other.Modifiers == Modifiers;

        public override bool Equals(object obj) => obj is HotkeyGesture other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Key, Modifiers);
    }
}
