using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Clowd.UI.Converters
{
    [ValueConversion(typeof(bool), typeof(Brush))]
    class OnOffBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var b = (bool)value;
            return b ? Brushes.LimeGreen : Brushes.IndianRed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
