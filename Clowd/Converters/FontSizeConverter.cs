using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Utilities
{
    /// <summary>
    /// Convert font size to string making the same conversion
    /// as FontDialog with FontSize parameter.
    /// Used to show the same font size value as in FontDialog
    /// Size box.
    /// </summary>
    [ValueConversion(typeof(double), typeof(string))]
    public class FontSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
                              object parameter, CultureInfo culture)
        {
            double d = (double)value * 0.75;

            return ((int)(d + 0.5)).ToString(CultureInfo.InvariantCulture);
        }

        public object ConvertBack(object value, Type targetType,
                                  object parameter, CultureInfo culture)
        {
            return new NotSupportedException(this.GetType().Name + " : Convert back not supported");
        }
    }
}
