using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Input;

namespace Clowd
{
    [TypeConverter(typeof(SimpleKeyGestureConverter))]
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
        /// Canonical round-trippable form used by the settings file: modifier flag names joined
        /// with '+', followed by the exact <see cref="Avalonia.Input.Key"/> name — e.g.
        /// "Control+Shift+Snapshot". The pretty <see cref="ToString"/> ("Ctrl+Shift+PrtScr") is
        /// for display only and is NOT parseable.
        /// </summary>
        public string ToSerializedString()
        {
            var parts = new[] { KeyModifiers.Control, KeyModifiers.Alt, KeyModifiers.Shift, KeyModifiers.Meta }
                        .Where(m => Modifiers.HasFlag(m))
                        .Select(m => m.ToString())
                        .Append(Key.ToString());
            return string.Join("+", parts);
        }

        /// <summary>
        /// Parses the <see cref="ToSerializedString"/> form. Tolerant: null, empty or
        /// unrecognizable input yields null (the gesture is treated as not set).
        /// </summary>
        public static SimpleKeyGesture Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var parts = value.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            if (parts.Length == 0)
                return null;

            if (!Enum.TryParse<Key>(parts[^1], true, out var key))
                return null;

            var modifiers = KeyModifiers.None;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!Enum.TryParse<KeyModifiers>(parts[i], true, out var mod))
                    return null;
                modifiers |= mod;
            }

            return new SimpleKeyGesture(key, modifiers);
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
                .Replace("Delete", "Del")
                .Replace("Escape", "Esc");
        }
    }

    /// <summary>String ↔ <see cref="SimpleKeyGesture"/> for the Microsoft.Extensions.Configuration
    /// binder (settings load path). Uses the canonical "Control+Shift+Snapshot" form.</summary>
    public class SimpleKeyGestureConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType) =>
            sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

        public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value) =>
            value is string s ? SimpleKeyGesture.Parse(s) : base.ConvertFrom(context, culture, value);

        public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType) =>
            destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType) =>
            destinationType == typeof(string) && value is SimpleKeyGesture g
                ? g.ToSerializedString()
                : base.ConvertTo(context, culture, value, destinationType);
    }
}
