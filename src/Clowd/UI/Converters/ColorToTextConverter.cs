using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media;
using Clowd.Util;

namespace Clowd.UI.Converters
{
    [ValueConversion(typeof(Color), typeof(string))]
    internal class ColorToHexConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not Color c)
                throw new InvalidOperationException("Must be type Color");

            return ColorTextHelper.GetHex(c);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string s)
                throw new InvalidOperationException("Must be type string");

            try
            {
                s = s.Trim().TrimStart('#');

                if (s.Length is not 6 and not 8)
                {
                    throw new InvalidOperationException("Invalid hex color");
                }

                byte r = 0, g = 0, b = 0, a = 255;

                if (s.Length >= 6)
                {
                    r = System.Convert.ToByte(s.Substring(0, 2), 16);
                    g = System.Convert.ToByte(s.Substring(2, 2), 16);
                    b = System.Convert.ToByte(s.Substring(4, 2), 16);
                }

                if (s.Length == 8)
                {
                    a = System.Convert.ToByte(s.Substring(6, 2), 16);
                }

                return Color.FromArgb(a, r, g, b);
            }
            catch
            {
                return Binding.DoNothing;
            }
        }
    }

    [ValueConversion(typeof(Color), typeof(string))]
    internal class ColorToRgbConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not Color c)
                throw new InvalidOperationException("Must be type Color");

            return ColorTextHelper.GetRgb(c);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    [ValueConversion(typeof(Color), typeof(string))]
    internal class ColorToHslConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not Color c)
                throw new InvalidOperationException("Must be type Color");

            return ColorTextHelper.GetHsl(c);
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
            return GetHsl(HSLColor.FromRGB(color));
        }

        public static string GetHsl(HSLColor hsl)
        {
            if (hsl.Alpha == 1)
                return $"hsl({hsl.Hue:0}, {hsl.Saturation * 100:0}%, {hsl.Lightness * 100:0}%)";
            else
                return $"hsla({hsl.Hue:0}, {hsl.Saturation * 100:0}%, {hsl.Lightness * 100:0}%, {hsl.Alpha * 255d})";
        }

        public static string GetRgb(Color color)
        {
            if (color.A == 255)
                return $"rgb({color.R}, {color.G}, {color.B})";
            else
                return $"rgba({color.R}, {color.G}, {color.B}, {color.A})";
        }

        public static string GetHex(Color color)
        {
            if (color.A == 255)
                return string.Format("#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
            else
                return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", color.R, color.G, color.B, color.A);
        }
    }
}
