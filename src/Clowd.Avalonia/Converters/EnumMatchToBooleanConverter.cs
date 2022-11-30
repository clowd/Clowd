using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Clowd.Avalonia.Converters
{
    public class EnumMatchToBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var matchVal = (Enum)Enum.Parse(value.GetType(), parameter as string, true);
            return (value as Enum)?.HasFlag(matchVal) ?? false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
