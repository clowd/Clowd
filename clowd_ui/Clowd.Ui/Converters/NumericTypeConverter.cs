using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Clowd.UI.Converters
{
    /// <summary>
    /// Bridges Avalonia NumericUpDown (decimal? Value) and the int/double settings properties
    /// (decision table #53). Convert: numeric setting → decimal?; ConvertBack: decimal? → the
    /// setting's type (per Type.GetTypeCode, as used by SettingsControlFactory).
    /// </summary>
    public class NumericTypeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return null;

            try
            {
                return (decimal?)System.Convert.ToDecimal(value, culture);
            }
            catch
            {
                return AvaloniaProperty.UnsetValue;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return AvaloniaProperty.UnsetValue;

            var type = Nullable.GetUnderlyingType(targetType) ?? targetType;

            try
            {
                return System.Convert.ChangeType(value, type, culture);
            }
            catch
            {
                return AvaloniaProperty.UnsetValue;
            }
        }
    }
}
