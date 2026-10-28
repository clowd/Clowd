using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Input;

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
        /// Parses with the same grammar as the crate's <c>Gesture::from_str</c>: '+'-separated
        /// parts, trimmed, empty parts dropped; the last is a key name, alias or label
        /// (<see cref="HotkeyKeys.TryParse"/>: "PrintScreen", "Snapshot", "PrtScr"), every earlier
        /// one a modifier name or shorthand (<see cref="HotkeyKeys.TryParseModifier"/>: "Control",
        /// "Ctrl", "Cmd", …), all case-insensitive; a trailing '+' after "Num" is the keypad plus
        /// ("Ctrl+Num+"). So both <see cref="ToSerializedString"/> and <see cref="ToString"/> read
        /// back. Null for anything it cannot read — the gesture is then treated as not set. "None"
        /// is a gesture with no key.
        /// </summary>
        public static HotkeyGesture Parse(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return null;

            var parts = value.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            if (parts.Length == 0)
                return null;

            if (!HotkeyKeys.TryParse(parts[^1], out var key)
                && !(value.TrimEnd().EndsWith('+') && HotkeyKeys.TryParse(parts[^1] + "+", out key)))
                return null;

            var modifiers = HotkeyModifiers.None;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!HotkeyKeys.TryParseModifier(parts[i], out var modifier))
                    return null;
                modifiers |= modifier;
            }

            return new HotkeyGesture(key, modifiers);
        }

        /// <summary>
        /// The settings-file form, identical to the crate's <c>Gesture</c> Display: the modifiers
        /// held, in the order Control, Alt, Shift, Meta, then the key's canonical name, joined with
        /// '+' — "Control+Shift+PrintScreen". The pretty <see cref="ToString"/> is not parseable.
        /// </summary>
        public string ToSerializedString()
        {
            var text = new StringBuilder();
            foreach (var modifier in ModifierOrder)
            {
                if ((Modifiers & modifier) != 0)
                    text.Append(modifier.ToString()).Append('+');
            }

            return text.Append(HotkeyKeys.Name(Key)).ToString();
        }

        /// <summary>
        /// Display text with the crate's short labels ("Ctrl+Shift+PrtScr", "Win+1"), empty for a
        /// gesture with no key. <see cref="Parse"/> reads it back, but settings store
        /// <see cref="ToSerializedString"/>. Meta reads as the key it is on this OS: Win on Windows,
        /// Cmd on macOS, Super elsewhere.
        /// </summary>
        public override string ToString()
        {
            if (Key == HotkeyKey.None)
                return string.Empty;

            return ModifiersText(Modifiers) + HotkeyKeys.Label(Key);
        }

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
        public static string ModifiersText(HotkeyModifiers modifiers)
        {
            var text = new StringBuilder();
            if ((modifiers & HotkeyModifiers.Control) != 0)
                text.Append("Ctrl+");
            if ((modifiers & HotkeyModifiers.Alt) != 0)
                text.Append("Alt+");
            if ((modifiers & HotkeyModifiers.Shift) != 0)
                text.Append("Shift+");
            if ((modifiers & HotkeyModifiers.Meta) != 0)
                text.Append(MetaText).Append('+');
            return text.ToString();
        }

        private static string MetaText =>
            OperatingSystem.IsWindows() ? "Win" : OperatingSystem.IsMacOS() ? "Cmd" : "Super";

        /// <summary>
        /// True for the combinations the OS needs for itself, which are never bound: recording one
        /// is treated as a cancel (<c>GestureField</c>), and one already in the settings is refused
        /// at registration (<c>HotkeyManager</c>) and left off the standby capturer's command line.
        /// The recorder's hook swallows the key it records, so these are exactly what a user
        /// presses to get away from a field that is listening — and a binding would then swallow
        /// them in every app. On Windows that is task
        /// switching (Alt+Tab, Alt+Shift+Tab, Alt+Esc, Ctrl+Esc) and Task Manager (Ctrl+Shift+Esc);
        /// on macOS app and window switching (Cmd+Tab, Cmd+Shift+Tab, Cmd+`, Cmd+Shift+`),
        /// Spotlight (Cmd+Space), leaving the window (Cmd+Q, Cmd+W, Cmd+H, Cmd+M), and the ways out
        /// of a hung app (Force Quit, Cmd+Opt+Esc; Lock Screen, Ctrl+Cmd+Q). Cmd+Shift+3/4/5 stay
        /// bindable: taking over the screenshot keys is a screenshot tool's business. False on
        /// every other OS.
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

            const HotkeyModifiers Cmd = HotkeyModifiers.Meta;
            var combination = (gesture.Key, gesture.Modifiers);

            if (platform == OSPlatform.Windows)
            {
                return combination switch
                {
                    (HotkeyKey.Tab, HotkeyModifiers.Alt) => true,
                    (HotkeyKey.Tab, HotkeyModifiers.Alt | HotkeyModifiers.Shift) => true,
                    (HotkeyKey.Escape, HotkeyModifiers.Alt) => true,
                    (HotkeyKey.Escape, HotkeyModifiers.Control) => true,
                    (HotkeyKey.Escape, HotkeyModifiers.Control | HotkeyModifiers.Shift) => true,
                    _ => false,
                };
            }

            if (platform == OSPlatform.OSX)
            {
                return combination switch
                {
                    // window cycling is Cmd+`, beside left Shift on an ISO keyboard (OemBackslash)
                    (HotkeyKey.Tab or HotkeyKey.Oem3 or HotkeyKey.OemBackslash, Cmd) => true,
                    (HotkeyKey.Tab or HotkeyKey.Oem3 or HotkeyKey.OemBackslash, Cmd | HotkeyModifiers.Shift) => true,
                    (HotkeyKey.Space or HotkeyKey.Q or HotkeyKey.W or HotkeyKey.H or HotkeyKey.M, Cmd) => true,
                    (HotkeyKey.Escape, Cmd | HotkeyModifiers.Alt) => true,
                    (HotkeyKey.Q, Cmd | HotkeyModifiers.Control) => true,
                    _ => false,
                };
            }

            return false;
        }

        public bool Equals(HotkeyGesture other) => other != null && other.Key == Key && other.Modifiers == Modifiers;

        public override bool Equals(object obj) => obj is HotkeyGesture other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Key, Modifiers);
    }
}
