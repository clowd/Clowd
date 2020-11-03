using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;


namespace Clowd.UI.Converters
{
    /// <summary>
    /// Double to integer converter.
    /// </summary>
    [ValueConversion(typeof(Color), typeof(Brush))]
    public class ColorToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
                              object parameter, CultureInfo culture)
        {
            Color color = (Color)value;

            return new SolidColorBrush(color);
        }

        public object ConvertBack(object value, Type targetType,
                                  object parameter, CultureInfo culture)
        {
            return new NotSupportedException(this.GetType().Name + " : Convert back not supported");

        }

    }
}
