using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Clowd.UI.Converters
{
    [ValueConversion(typeof(bool), typeof(Visibility))]
    class BoolToInverseVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var hiddenVis = (parameter as string) == "Hidden" ? Visibility.Hidden : Visibility.Collapsed;
            var b = (bool)value;
            return b ? hiddenVis : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
