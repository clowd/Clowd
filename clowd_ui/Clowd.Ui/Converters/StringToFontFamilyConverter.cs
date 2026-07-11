using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Clowd.UI.Converters
{
    /// <summary>
    /// string → FontFamily for runtime bindings. The XAML compiler converts literal strings to
    /// FontFamily, but the binding engine does not — binding a family-name string straight to a
    /// FontFamily property fails ("Could not convert ... to Avalonia.Media.FontFamily").
    /// </summary>
    public sealed class StringToFontFamilyConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is FontFamily family)
                return family;

            if (value is string name && !string.IsNullOrWhiteSpace(name))
            {
                try
                {
                    return new FontFamily(name);
                }
                catch
                {
                    return FontFamily.Default;
                }
            }

            return FontFamily.Default;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value as FontFamily)?.Name;
        }
    }
}
