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
    }
}
