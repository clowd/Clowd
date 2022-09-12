using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Clowd.UI.Converters
{
    [ValueConversion(typeof(Enum), typeof(Visibility))]
    public class EnumMatchToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var matchVal = (Enum)Enum.Parse(value.GetType(), parameter as string, true);
            var result = (value as Enum)?.HasFlag(matchVal) ?? false;
            return result ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
