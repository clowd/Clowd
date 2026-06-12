using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Clowd.UI.Converters
{
    /// <summary>
    /// Returns true when the bound enum value has the flag named by the converter parameter.
    /// Replaces all of the WPF enum→Visibility converter variants (decision table #34) — bind
    /// the result to IsVisible.
    /// </summary>
    public class EnumMatchToBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not Enum enumValue || parameter is not string parameterString)
                return false;

            var matchVal = (Enum)Enum.Parse(value.GetType(), parameterString, true);
            return enumValue.HasFlag(matchVal);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
