using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Clowd.Drawing
{
    /// <summary>
    /// Convert a ToolType and a string paramater to bool.
    /// Can be used to check active tool button/menu item in client application.
    /// This returns true if the string paramater matches the name of the current ToolType
    /// </summary>
    public class ToolTypeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string name = value == null ? null : Enum.GetName(typeof(ToolType), value);
            return (name == (string)parameter);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return AvaloniaProperty.UnsetValue;
        }
    }
}
