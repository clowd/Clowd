using System;
using System.Linq;
using System.Windows.Input;

namespace Clowd
{
    public class SimpleKeyGesture : IEquatable<SimpleKeyGesture>
    {
        public Key Key { get; }

        public ModifierKeys Modifiers { get; }

        public SimpleKeyGesture()
        { }

        public SimpleKeyGesture(Key key)
        {
            Key = key;
        }

        public SimpleKeyGesture(Key key, ModifierKeys modifiers)
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

        public KeyGesture ToWpfGesture()
        {
            return new KeyGesture(Key, Modifiers);
        }

        public override string ToString()
        {
            if (Key == Key.None)
                return string.Empty;

            var strBinding = "";
            var strKey = Key.ToString();
            if (strKey != string.Empty)
            {
                if (Modifiers != ModifierKeys.None)
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
                .Replace("Delete", "Del")
                .Replace("Escape", "Esc");
        }
    }
}
