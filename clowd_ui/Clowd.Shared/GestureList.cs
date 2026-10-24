using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Input;

namespace Clowd
{
    /// <summary>
    /// The key combinations bound to one global hotkey action, in the order the user added them —
    /// the first is the one captions quote. Immutable, with value equality, and that is the whole
    /// point of the type rather than a plain list:
    /// <list type="bullet">
    /// <item>immutable, so the only way to change an action's gestures is to assign a new list to
    /// its <see cref="Config.SettingsHotkey"/> property, which raises the PropertyChanged that the
    /// tray, the standby capturer and the settings captions already listen for (an
    /// ObservableCollection mutated in place would slip past every one of them);</item>
    /// <item>value equality, so assigning an equal list is a no-op for
    /// <see cref="SimpleNotifyObject"/>'s Set, and a changed list is a changed fingerprint;</item>
    /// <item>a scalar to the configuration binder: it carries a [TypeConverter] and persists as one
    /// string ("PrintScreen; Control+Shift+D1"), so the binder converts and assigns it like any
    /// other value. Bound as a JSON array instead, the binder would append the file's items to the
    /// compiled-in defaults rather than replace them, and would skip an empty array altogether —
    /// silently bringing the defaults back after the user removed every gesture.</item>
    /// </list>
    /// An empty list persists as "None", which is also what a cleared gesture was written as before
    /// actions could hold more than one, and a single old-style gesture ("Control+Snapshot") is
    /// simply a one-item list — so settings files from before the change load unchanged.
    /// <para>Never holds null, <see cref="Key.None"/> or the same gesture twice: those are dropped
    /// on the way in.</para>
    /// </summary>
    [TypeConverter(typeof(GestureListConverter))]
    public sealed class GestureList : IReadOnlyList<SimpleKeyGesture>, IEquatable<GestureList>
    {
        /// <summary>What an empty list is written as (see the type's remarks).</summary>
        public const string EmptySerializedString = "None";

        /// <summary>Between gestures in the settings-file form. Not a comma: a build from before
        /// actions held several gestures parses the whole value as one gesture, splitting only on
        /// '+' and handing the last segment to Enum.TryParse — which reads "PrintScreen, F9" as the
        /// two keys OR'd into one made-up key and would bind that system-wide. A ';' fails to parse
        /// there wherever it lands, so an older build only clears the action instead.</summary>
        private const char Separator = ';';

        /// <summary>What this build wrote between gestures before it switched to
        /// <see cref="Separator"/>; still read, so those files keep loading.</summary>
        private const char LegacySeparator = ',';

        /// <summary>Between gestures in the display form, which nothing parses.</summary>
        private const string DisplaySeparator = ", ";

        public static GestureList Empty { get; } = new GestureList(Array.Empty<SimpleKeyGesture>());

        private readonly SimpleKeyGesture[] _items;

        public GestureList(IEnumerable<SimpleKeyGesture> gestures)
        {
            _items = (gestures ?? Enumerable.Empty<SimpleKeyGesture>())
                     .Where(g => g != null && g.Key != Key.None)
                     .Distinct()
                     .ToArray();
        }

        public static GestureList Of(params SimpleKeyGesture[] gestures) => new GestureList(gestures);

        public int Count => _items.Length;

        public SimpleKeyGesture this[int index] => _items[index];

        public bool Contains(SimpleKeyGesture gesture) => gesture != null && Array.IndexOf(_items, gesture) >= 0;

        public int IndexOf(SimpleKeyGesture gesture) => gesture == null ? -1 : Array.IndexOf(_items, gesture);

        /// <summary>This list with <paramref name="gesture"/> appended — or this same list when it
        /// already holds it, or the gesture is empty.</summary>
        public GestureList Add(SimpleKeyGesture gesture) =>
            gesture == null || gesture.Key == Key.None || Contains(gesture) ? this : new GestureList(_items.Append(gesture));

        /// <summary>This list with the gesture at <paramref name="index"/> replaced in place. An
        /// empty gesture removes it instead; one the list already holds (here or at another index)
        /// leaves the list as it is, since an action cannot hold a gesture twice.</summary>
        public GestureList SetItem(int index, SimpleKeyGesture gesture)
        {
            if ((uint)index >= (uint)_items.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (gesture == null || gesture.Key == Key.None)
                return RemoveAt(index);
            if (Contains(gesture))
                return this;

            var items = (SimpleKeyGesture[])_items.Clone();
            items[index] = gesture;
            return new GestureList(items);
        }

        public GestureList RemoveAt(int index)
        {
            if ((uint)index >= (uint)_items.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return new GestureList(_items.Where((_, i) => i != index));
        }

        /// <summary>This list without any of <paramref name="gestures"/> (this same list when it holds
        /// none of them).</summary>
        public GestureList Except(IEnumerable<SimpleKeyGesture> gestures)
        {
            var remove = gestures as ICollection<SimpleKeyGesture> ?? gestures.ToList();
            return _items.Any(remove.Contains) ? new GestureList(_items.Where(g => !remove.Contains(g))) : this;
        }

        /// <summary>The settings-file form: each gesture's
        /// <see cref="SimpleKeyGesture.ToSerializedString"/>, joined with "; " ("Control+PrintScreen; F9"),
        /// or "None" when empty. Key and modifier names never contain a ';' (or a ','), so the
        /// separator cannot be ambiguous.</summary>
        public string ToSerializedString() =>
            _items.Length == 0 ? EmptySerializedString : string.Join(Separator + " ", _items.Select(g => g.ToSerializedString()));

        /// <summary>
        /// Parses the <see cref="ToSerializedString"/> form, which includes a lone pre-multi-gesture
        /// value. Tolerant, like <see cref="SimpleKeyGesture.Parse"/>: an entry it cannot read is
        /// dropped rather than failing the whole settings file, and null, empty or "None" is the
        /// empty list.
        /// </summary>
        public static GestureList Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Empty;

            return new GestureList(value.Split(new[] { Separator, LegacySeparator }).Select(SimpleKeyGesture.Parse));
        }

        /// <summary>Display text: each gesture's pretty <see cref="SimpleKeyGesture.ToString"/>
        /// ("PrtScr, Ctrl+Shift+1"), empty when there are none. Not parseable.</summary>
        public override string ToString() => string.Join(DisplaySeparator, _items.Select(g => g.ToString()));

        public IEnumerator<SimpleKeyGesture> GetEnumerator() => ((IEnumerable<SimpleKeyGesture>)_items).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

        public bool Equals(GestureList other) => other != null && _items.SequenceEqual(other._items);

        public override bool Equals(object obj) => obj is GestureList other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var item in _items)
                hash.Add(item);
            return hash.ToHashCode();
        }
    }

    /// <summary>String ↔ <see cref="GestureList"/> for the Microsoft.Extensions.Configuration binder
    /// (settings load path), in the <see cref="GestureList.ToSerializedString"/> form.</summary>
    public class GestureListConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType) =>
            sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

        public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value) =>
            value is string s ? GestureList.Parse(s) : base.ConvertFrom(context, culture, value);

        public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType) =>
            destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType) =>
            destinationType == typeof(string) && value is GestureList list
                ? list.ToSerializedString()
                : base.ConvertTo(context, culture, value, destinationType);
    }
}
