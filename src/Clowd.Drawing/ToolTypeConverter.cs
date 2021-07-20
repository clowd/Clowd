using System;
using System.Globalization;
using System.Windows.Data;

namespace Clowd.Drawing
{
    /// <summary>
    /// Convert a ToolType and a string paramater to bool.
    /// Can be used to check active tool button/menu item in client application.
    /// This returns true if the string paramater matches the name of the current ToolType
    /// </summary>
    [ValueConversion(typeof(ToolType), typeof(bool))]
    public class ToolTypeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string name = Enum.GetName(typeof(ToolType), value);
            return (name == (string)parameter);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return new NotSupportedException(this.GetType().Name + " ConvertBackNotSupported");
        }
    }
}
