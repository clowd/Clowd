﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;

namespace Clowd.Converters
{
    [ValueConversion(typeof(double), typeof(double))]
    public class SnapToPixelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (targetType != typeof(double))
                throw new InvalidOperationException("The target must be a boolean");

            double extraOffset = 0;
            if (parameter is string)
            {
                extraOffset = ScreenVersusWpf.ScreenTools.ScreenToWpf(Double.Parse((string)parameter));
            }

            return ScreenVersusWpf.ScreenTools.WpfSnapToPixelsFloor((double)value) + extraOffset;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}