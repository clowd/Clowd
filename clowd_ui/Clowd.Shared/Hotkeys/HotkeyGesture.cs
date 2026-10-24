using System;
using System.Linq;
using System.Text;
using Avalonia.Input;

namespace Clowd
{
    /// <summary>
    /// A global hotkey: a <see cref="HotkeyKey"/> and the <see cref="HotkeyModifiers"/> held with
    /// it. The key names and the serialized form are the clowd_hotkeys crate's, so the settings
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
        /// True for the Windows task-switching combinations — Alt+Tab, Alt+Shift+Tab, Alt+Esc and
        /// Ctrl+Esc — which are never bound: the hook would swallow them system-wide. Recording one
        /// is treated as a cancel. False on every other OS.
        /// </summary>
        public static bool IsSystemReserved(HotkeyGesture gesture)
        {
            if (gesture == null || !OperatingSystem.IsWindows())
                return false;

            return (gesture.Key, gesture.Modifiers) switch
            {
                (HotkeyKey.Tab, HotkeyModifiers.Alt) => true,
                (HotkeyKey.Tab, HotkeyModifiers.Alt | HotkeyModifiers.Shift) => true,
                (HotkeyKey.Escape, HotkeyModifiers.Alt) => true,
                (HotkeyKey.Escape, HotkeyModifiers.Control) => true,
                _ => false,
            };
        }

        public bool Equals(HotkeyGesture other) => other != null && other.Key == Key && other.Modifiers == Modifiers;

        public override bool Equals(object obj) => obj is HotkeyGesture other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Key, Modifiers);
    }
}
