using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Clowd.UI.Converters
{
    /// <summary>
    /// The two-way converter behind a frame-rate preset box, where an empty box and a stored 0 are
    /// the same thing: "no preset here". <see cref="NumericTypeConverter"/> cannot be used for these
    /// because it answers a cleared box with <see cref="AvaloniaProperty.UnsetValue"/>, which leaves
    /// the old number in the settings — the box looks empty until the page is reopened and the
    /// preset comes back.
    /// </summary>
    public class FpsPresetConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                // 0 shows as an empty box rather than a literal "0", so the state the user typed
                // and the state they see agree.
                var fps = System.Convert.ToDouble(value, culture);
                return fps == 0 ? (double?)null : fps;
            }
            catch
            {
                return AvaloniaProperty.UnsetValue;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return 0;

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
