using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;

namespace Clowd.Converters
{
    [ValueConversion(typeof(double), typeof(string))]
    public class ZoomScaleConverter : ValidationRule, IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Math.Round((double)value * 100, 2).ToString() + " %";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                return DrawingCanvasEditConverter.ConvertString(value, "%") / 100;
            }
            catch
            {
                return value;
            }
        }

        public override ValidationResult Validate(object value, CultureInfo cultureInfo)
        {
            throw new NotImplementedException();
        }
    }

    [ValueConversion(typeof(double), typeof(string))]
    public class StringPixelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value.ToString() + " px";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                return DrawingCanvasEditConverter.ConvertString(value, "px");
            }
            catch
            {
                return value;
            }
        }
    }

    [ValueConversion(typeof(double), typeof(string))]
    public class AngleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Math.Round((double)value, 2).ToString() + " °";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                return DrawingCanvasEditConverter.ConvertString(value, "°");
            }
            catch
            {
                return value;
            }
        }
    }

    class DrawingCanvasEditConverter
    {
        public static double ConvertString(object value, string suffix)
        {
            double v = Double.NaN;

            if (value is string str)
            {
                str = str.Trim();
                if (str.EndsWith(suffix))
                    str = str.Substring(0, str.Length - suffix.Length);

                str = str.Trim();

                v = System.Convert.ToDouble(str);
            }
            else
            {
                v = System.Convert.ToDouble(value);
            }

            return v;
        }
    }
}
