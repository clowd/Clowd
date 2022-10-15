using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Clowd.Util;

namespace Clowd.UI.Converters
{
    [ValueConversion(typeof(HslRgbColor), typeof(Brush))]
    public class HslColorToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            HslRgbColor color = (HslRgbColor)value;
            return new SolidColorBrush(color.ToColor());
        }

        public object ConvertBack(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            if (value is SolidColorBrush br)
                return HslRgbColor.FromColor(br.Color);

            return new NotSupportedException(this.GetType().Name + " : Convert back not supported");
        }
    }
}
