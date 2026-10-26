using System;
using System.Linq;
using Avalonia.Input;

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

        public override string ToString()
        {
            if (Key == Key.None)
                return string.Empty;

            var strBinding = "";
            var strKey = Key.ToString();
            if (strKey != string.Empty)
            {
                if (Modifiers != KeyModifiers.None)
                {
                    strBinding += Modifiers.ToString();
                    if (strBinding != string.Empty)
                    {
                        strBinding += '+';
                    }
                }

                strBinding += strKey;
            }

            return string.Join("+", strBinding.Split('+', ',').Select(c => c.Trim()))
                .Replace("Snapshot", "PrtScr")
                .Replace("Control", "Ctrl")
                // what ⌘ is called in text; only ForApp() ever puts it here.
                .Replace("Meta", "Cmd")
                .Replace("Delete", "Del")
                .Replace("Escape", "Esc");
        }
    }
}
