using System;
using System.Globalization;

namespace Clowd.Drawing
{
    /// <summary>
    /// A real-world unit for measure labels: <see cref="PixelsPerUnit"/> canvas pixels make one
    /// <see cref="Unit"/>. Immutable, so value equality is all the history engine needs to diff it.
    /// </summary>
    public sealed record MeasureUnits(string Unit, double PixelsPerUnit)
    {
        /// <summary>The longest unit name a label will show.</summary>
        public const int MaxUnitLength = 12;

        /// <summary>Trims a unit name and caps it at <see cref="MaxUnitLength"/>; blank is null.</summary>
        public static string NormalizeUnit(string unit)
        {
            unit = unit?.Trim();
            if (string.IsNullOrEmpty(unit))
                return null;
            return unit.Length > MaxUnitLength ? unit.Substring(0, MaxUnitLength) : unit;
        }

        /// <summary>The valid form of <paramref name="units"/>, or null (canvas pixels) when it has
        /// no usable name or scale — a hand-edited or corrupt session must not break the labels.</summary>
        public static MeasureUnits Normalize(MeasureUnits units)
        {
            var unit = NormalizeUnit(units?.Unit);
            if (unit == null || !double.IsFinite(units.PixelsPerUnit) || units.PixelsPerUnit <= 0)
                return null;
            return unit == units.Unit ? units : units with { Unit = unit };
        }

        /// <summary>A length with up to two decimals, as labels and the units editor show it.</summary>
        public static string FormatLength(double length)
        {
            var rounded = Math.Round(length, 2);
            if (rounded == 0) rounded = 0; // never "-0"
            return rounded.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// The canvas's measure unit override. Measures read <see cref="Current"/> whenever they lay
    /// out their label; null means they read in canvas pixels. It is document state — saved with
    /// the session beside the background color and undone like it — so only the canvas (and its
    /// history engine) writes it.
    /// </summary>
    public sealed class MeasureUnitService
    {
        /// <summary>The unit every measure reads in, or null for canvas pixels.</summary>
        public MeasureUnits Current { get; private set; }

        /// <summary>Raised after <see cref="Current"/> changes.</summary>
        public event EventHandler Changed;

        /// <summary>Converts a length in canvas pixels to <see cref="Current"/>.</summary>
        public double ToUnits(double pixels) => Current is { } u ? pixels / u.PixelsPerUnit : pixels;

        internal bool Set(MeasureUnits units)
        {
            units = MeasureUnits.Normalize(units);
            if (Equals(units, Current))
                return false;
            Current = units;
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
    }
}
