using System;
using System.Globalization;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// The value logic behind <see cref="ResetDefaultButton"/>, kept free of Avalonia so it can be
    /// tested without a platform. A default written in XAML arrives as the literal string ("0.5")
    /// against a bound number, and both the comparison and the reset have to read that text as
    /// source, never in the user's culture: under a comma-decimal locale
    /// <c>Convert.ToDouble("0.5")</c> takes the dot for a thousands separator and yields 5, so
    /// the dot never matched 0.5 and never hid, and a reset that handed the raw string to a double
    /// binding went through the same parse and landed on 5 — clamped to 1, "Center X resets to
    /// 100%" (GitHub #102).
    /// </summary>
    public static class ResetDefaultValue
    {
        /// <summary>Whether <paramref name="current"/> is at <paramref name="defaultValue"/>: the
        /// WPF equality cascade, with the first step upgraded from reference equality to Equals so
        /// a boxed value set from code compares by value; then string equality; then numeric
        /// equality (swallowing conversion failures). The string step only runs when a string is
        /// actually involved — two non-string values would both cast to null and compare "equal",
        /// permanently hiding the dot (bit every enum-valued binding).</summary>
        public static bool IsDefault(object current, object defaultValue)
        {
            if (Equals(current, defaultValue))
                return true;

            if (current is string || defaultValue is string)
            {
                if ((current as string) == (defaultValue as string))
                    return true;
            }

            try
            {
                return ToDouble(current) == ToDouble(defaultValue);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The value a reset writes: <paramref name="defaultValue"/>, typed like
        /// <paramref name="current"/> when the two differ only in representation (a XAML string
        /// against a bound number or enum), so the binding receives a finished value rather than
        /// text it would parse in the current culture. Anything that cannot be converted is handed
        /// back as it is.</summary>
        public static object ForReset(object current, object defaultValue)
        {
            if (defaultValue is not string text || current is null || current is string)
                return defaultValue;

            try
            {
                if (current is Enum)
                    return Enum.Parse(current.GetType(), text, ignoreCase: true);
                if (current is IConvertible)
                    return Convert.ChangeType(text, current.GetType(), CultureInfo.InvariantCulture);
            }
            catch { }

            return defaultValue;
        }

        private static double ToDouble(object value) => value is string s
            ? Double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture)
            : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }
}
