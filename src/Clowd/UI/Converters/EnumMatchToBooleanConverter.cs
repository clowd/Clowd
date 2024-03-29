﻿using System;
using System.Globalization;
using System.Windows.Data;

namespace Clowd.UI.Converters
{
    [ValueConversion(typeof(Enum), typeof(bool))]
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
