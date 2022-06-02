//---------------------------------------------------------
// DoubleToDecimalConverter.cs (c) 2006 by Charles Petzold
//---------------------------------------------------------

using System;
using System.Globalization;
using System.Windows.Data;

namespace Clowd.UI.Converters
{
    /// <summary>
    /// Converts double to decimal value with given number of decimal digits
    /// passed as parameter.
    /// Example of using:
    /// Binding Path=..., Converter={StaticResource converter}, ConverterParameter=2}
    /// </summary>
    [ValueConversion(typeof(double), typeof(decimal))]
    public class DoubleToDecimalConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            decimal num = new Decimal((double)value);

            if (parameter != null) // decimal digits
                num = Decimal.Round(num, Int32.Parse(parameter as string, CultureInfo.InvariantCulture));

            return num;
        }

        public object ConvertBack(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            return Decimal.ToDouble((decimal)value);
        }
    }
}
