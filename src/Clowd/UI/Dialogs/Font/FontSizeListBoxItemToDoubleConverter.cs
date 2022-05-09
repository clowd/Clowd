using System;
using System.Globalization;
using System.Windows.Data;

namespace Clowd.UI.Dialogs.Font
{
    public class FontSizeListBoxItemToDoubleConverter : IValueConverter
    {
        public FontSizeListBoxItemToDoubleConverter()
        {
        }

        object System.Windows.Data.IValueConverter.Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string str = value.ToString();
            try
            {
                return double.Parse(value.ToString());
            }
            catch (FormatException)
            {
                return 0;
            }

        }

        object System.Windows.Data.IValueConverter.ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
