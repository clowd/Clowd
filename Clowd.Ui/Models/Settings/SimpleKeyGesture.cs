using System;
using System.Text;
using Avalonia.Input;

namespace Clowd.Ui.Models.Settings;

/// <summary>
/// Plain serializable key gesture: a single key plus modifier flags.
/// Avalonia has KeyGesture but it's not friendly for our needs (no parameterless ctor for STJ).
/// </summary>
public sealed class SimpleKeyGesture : IEquatable<SimpleKeyGesture>
{
    public Key Key { get; set; }
    public KeyModifiers Modifiers { get; set; }

    public SimpleKeyGesture()
    {
    }

    public SimpleKeyGesture(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        Key = key;
        Modifiers = modifiers;
    }

    public override string ToString()
    {
        if (Key == Key.None) return string.Empty;

        var sb = new StringBuilder();
        if ((Modifiers & KeyModifiers.Control) != 0) sb.Append("Ctrl+");
        if ((Modifiers & KeyModifiers.Shift) != 0) sb.Append("Shift+");
        if ((Modifiers & KeyModifiers.Alt) != 0) sb.Append("Alt+");
        if ((Modifiers & KeyModifiers.Meta) != 0) sb.Append("Win+");
        sb.Append(Key);
        return sb.ToString();
    }

    public bool Equals(SimpleKeyGesture? other)
    {
        if (other is null) return false;
        return Key == other.Key && Modifiers == other.Modifiers;
    }

    public override bool Equals(object? obj) => Equals(obj as SimpleKeyGesture);

    public override int GetHashCode() => HashCode.Combine(Key, Modifiers);
}
