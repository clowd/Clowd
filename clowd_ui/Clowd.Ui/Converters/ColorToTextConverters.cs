using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Clowd.Util;

namespace Clowd.UI.Converters
{
    internal class ColorToHexConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Color c)
                return ColorTextHelper.GetHex(c);

            if (value is HslRgbColor c2)
                return ColorTextHelper.GetHex(c2);

            if (value is null)
                return AvaloniaProperty.UnsetValue;

            throw new InvalidOperationException("Must be type Color");
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string s)
                throw new InvalidOperationException("Must be type string");

            try
            {
                var rgb = ColorTextHelper.FromHex(s);

                if (targetType == typeof(Color))
                    return rgb;

                if (targetType == typeof(HslRgbColor))
                    return HslRgbColor.FromColor(rgb);

                throw new InvalidOperationException("Target type must be Color or HslRgbColor");
            }
            catch
            {
                return BindingOperations.DoNothing;
            }
        }
    }

    internal class ColorToRgbConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Color c)
                return ColorTextHelper.GetRgb(c);

            if (value is HslRgbColor c2)
                return ColorTextHelper.GetRgb(c2);

            if (value is null)
                return AvaloniaProperty.UnsetValue;

            throw new InvalidOperationException("Must be type Color");
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    internal class ColorToHslConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Color c)
                return ColorTextHelper.GetHsl(c);

            if (value is HslRgbColor c2)
                return ColorTextHelper.GetHsl(c2);

            if (value is null)
                return AvaloniaProperty.UnsetValue;

            throw new InvalidOperationException("Must be type Color");
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Notation shown in a color picker's editable text field. Not persisted — a picker always
    /// opens on <see cref="Hex"/> and the user cycles from there, the way devtools behaves.
    /// </summary>
    public enum ColorTextFormat
    {
        Hex,
        Rgb,
        Hsl,
    }

    public class ColorTextHelper
    {
        public static string GetHsl(Color color)
        {
            return GetHsl(HslRgbColor.FromColor(color));
        }

        public static string GetHsl(HslRgbColor hsl)
        {
            if (hsl.Alpha >= 1)
                return $"hsl({hsl.Hue:0}, {hsl.Saturation * 100:0}%, {hsl.Lightness * 100:0}%)";
            else
                return $"hsla({hsl.Hue:0}, {hsl.Saturation * 100:0}%, {hsl.Lightness * 100:0}%, {Math.Round(hsl.Alpha, 2)})";
        }

        public static string GetRgb(Color color)
        {
            if (color.A == 255)
                return $"rgb({color.R}, {color.G}, {color.B})";
            else
                return $"rgba({color.R}, {color.G}, {color.B}, {Math.Round(color.A / 255d, 2)})";
        }

        public static string GetRgb(HslRgbColor color)
        {
            if (color.Alpha >= 1d)
                return $"rgb({color.R}, {color.G}, {color.B})";
            else
                return $"rgba({color.R}, {color.G}, {color.B}, {Math.Round(color.Alpha, 2)})";
        }

        public static string GetHex(Color color)
        {
            if (color.A == 255)
                return string.Format("#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
            else
                return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", color.R, color.G, color.B, color.A);
        }

        public static string GetHex(HslRgbColor color)
        {
            if (color.Alpha >= 1d)
                return string.Format("#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
            else
                return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", color.R, color.G, color.B, (int)Math.Round(color.Alpha * 255d));
        }

        /// <summary>
        /// Parses a hex color string in any of the CSS forms RGB, RGBA, RRGGBB or RRGGBBAA,
        /// with or without a leading '#'. Shorthand digits are expanded by duplicating each
        /// digit, so "#dfd" is the same color as "#ddffdd".
        /// </summary>
        public static Color FromHex(string s)
        {
            s = (s ?? "").Trim().TrimStart('#');

            if (s.Length is not 3 and not 4 and not 6 and not 8)
            {
                throw new InvalidOperationException("Invalid hex color");
            }

            // Convert.ToByte(_, 16) accepts leading whitespace and a sign, which would let
            // strings like "+f fff" through, so the digits are validated up front instead
            foreach (var ch in s)
            {
                if (!Uri.IsHexDigit(ch))
                    throw new InvalidOperationException("Invalid hex color");
            }

            var shorthand = s.Length is 3 or 4;

            byte Component(int index) => shorthand
                ? System.Convert.ToByte(new string(s[index], 2), 16)
                : System.Convert.ToByte(s.Substring(index * 2, 2), 16);

            byte r = Component(0), g = Component(1), b = Component(2);
            byte a = s.Length is 4 or 8 ? Component(3) : (byte)255;

            return Color.FromArgb(a, r, g, b);
        }

        /// <summary>
        /// Parses any notation the pickers can display — <c>#RGB[A]</c>/<c>#RRGGBB[AA]</c> (with or
        /// without the '#'), <c>rgb(r, g, b)</c>/<c>rgba(r, g, b, a)</c> and
        /// <c>hsl(h, s%, l%)</c>/<c>hsla(h, s%, l%, a)</c> — independently of the currently
        /// selected format, so pasting a value in any notation works. HSL is returned as an
        /// <see cref="HslRgbColor"/> directly rather than round-tripped through RGB, which would
        /// quantize the hue and lose it entirely for grays.
        /// </summary>
        /// <param name="detected">Which notation the text was recognized as. Callers use this to
        /// decide how to compare the result against the color they already hold: only an HSL input
        /// carries a hue of its own, so hex and rgb inputs are equivalent whenever their RGB
        /// matches, while an HSL input differs as soon as any of H/S/L does.</param>
        public static bool TryParse(string text, out HslRgbColor color, out ColorTextFormat detected)
        {
            color = null;
            detected = ColorTextFormat.Hex;

            var s = (text ?? "").Trim();
            if (s.Length == 0)
                return false;

            try
            {
                if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
                {
                    var v = ParseFunctionArgs(s, "rgb");
                    if (v == null)
                        return false;

                    detected = ColorTextFormat.Rgb;
                    color = new HslRgbColor(ClampByte(v[0]), ClampByte(v[1]), ClampByte(v[2]),
                                            v.Length > 3 ? Math.Clamp(v[3], 0, 1) : 1);
                    return true;
                }

                if (s.StartsWith("hsl", StringComparison.OrdinalIgnoreCase))
                {
                    var v = ParseFunctionArgs(s, "hsl");
                    if (v == null)
                        return false;

                    // hue wraps rather than clamping, so "hsl(370, ...)" is red-orange not red
                    var hue = v[0] % 360;
                    if (hue < 0) hue += 360;

                    detected = ColorTextFormat.Hsl;
                    color = new HslRgbColor(hue, Math.Clamp(v[1] / 100d, 0, 1), Math.Clamp(v[2] / 100d, 0, 1),
                                            v.Length > 3 ? Math.Clamp(v[3], 0, 1) : 1);
                    return true;
                }

                color = HslRgbColor.FromColor(FromHex(s));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Splits the arguments of a CSS-style <c>name(...)</c> call into 3 or 4 numbers. The
        /// trailing 'a' of rgba/hsla is not required to match the argument count (CSS treats the
        /// two spellings as synonyms), and '%' suffixes are dropped. Returns null if the shape is
        /// anything else.
        /// </summary>
        private static double[] ParseFunctionArgs(string s, string name)
        {
            var open = s.IndexOf('(');
            var close = s.LastIndexOf(')');
            if (open < 0 || close < open)
                return null;

            // only "rgb(" or "rgba(" — reject "rgbx(" and similar
            var prefix = s.Substring(0, open).Trim();
            if (!prefix.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                !prefix.Equals(name + "a", StringComparison.OrdinalIgnoreCase))
                return null;

            var parts = s.Substring(open + 1, close - open - 1).Split(',', '/');
            if (parts.Length is not 3 and not 4)
                return null;

            var result = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Trim().TrimEnd('%').Trim();
                if (!Double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                    return null;
            }

            return result;
        }

        private static int ClampByte(double value) => (int)Math.Clamp(Math.Round(value), 0, 255);
    }
}
