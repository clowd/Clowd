using System;
using Avalonia.Input;
using Prisc.Interop;

namespace Prisc
{
    public class SimpleKeyGesture : IEquatable<SimpleKeyGesture>
    {
        public Key Key { get; }

        public KeyModifiers Modifiers { get; }

        public SimpleKeyGesture()
        { }

        public SimpleKeyGesture(Key key, KeyModifiers modifiers = KeyModifiers.None)
        {
            Key = key;
            Modifiers = modifiers;
        }

        public override int GetHashCode()
        {
            return unchecked(Key.GetHashCode() + Modifiers.GetHashCode());
        }

        public override bool Equals(object obj)
        {
            if (obj is SimpleKeyGesture kg) return Equals(kg);
            return false;
        }

        public bool Equals(SimpleKeyGesture other)
        {
            if (other == null) return false;
            return other.Key == Key && other.Modifiers == Modifiers;
        }

        public KeyGesture ToKeyGesture()
        {
            return new KeyGesture(Key, Modifiers);
        }

        /// <summary>
        /// This gesture as an <em>in-app command</em> binding for the running OS: on macOS the
        /// Control modifier becomes Meta, because ⌘ is where every Mac app puts Undo, Redo, Split
        /// and friends, and Control there is a separate modifier with its own meanings (⌃-click is
        /// the secondary click). Everywhere else it is this gesture unchanged.
        /// <para>
        /// Global hotkeys are not this type at all (<see cref="HotkeyGesture"/>): those are the
        /// literal keys the user configured, registered and displayed as they are.
        /// </para>
        /// </summary>
        public SimpleKeyGesture ForApp()
        {
            if (!OperatingSystem.IsMacOS() || !Modifiers.HasFlag(KeyModifiers.Control))
                return this;

            return new SimpleKeyGesture(Key, (Modifiers & ~KeyModifiers.Control) | KeyModifiers.Meta);
        }

        /// <summary>
        /// The text a tooltip, menu or keycap shows ("Ctrl+Z", "Shift+1", "Del", "Cmd+K"): the
        /// global hotkeys' display form (<c>prisc_gesture_format</c>), so both read alike, with the
        /// modifiers in one order and the keys' short labels. Empty for no key. Meta reads as the
        /// key it is on this OS (Win, Cmd or Super).
        /// </summary>
        public override string ToString() => NativeGestures.Format((int)Key, (int)ToHotkeyModifiers(Modifiers), GestureStyle.Display);

        // HotkeyKey mirrors Avalonia's Key one to one (HotkeyAvaloniaDriftTests), the modifiers go by name
        private static HotkeyModifiers ToHotkeyModifiers(KeyModifiers modifiers)
        {
            var result = HotkeyModifiers.None;
            if (modifiers.HasFlag(KeyModifiers.Control)) result |= HotkeyModifiers.Control;
            if (modifiers.HasFlag(KeyModifiers.Alt)) result |= HotkeyModifiers.Alt;
            if (modifiers.HasFlag(KeyModifiers.Shift)) result |= HotkeyModifiers.Shift;
            if (modifiers.HasFlag(KeyModifiers.Meta)) result |= HotkeyModifiers.Meta;
            return result;
        }
    }
}
