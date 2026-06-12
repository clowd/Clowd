using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Clowd.UI.Converters
{
    public class OnOffBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var b = value is bool v && v;
            return b ? Brushes.LimeGreen : Brushes.IndianRed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
