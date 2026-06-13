using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Clowd.UI.Converters
{
    /// <summary>
    /// Decodes a Stream (e.g. IUploadProvider.Icon) into a Bitmap for an Image source.
    /// Replaces the WPF StreamToBitmapSourceConverter.
    /// </summary>
    public class StreamToBitmapConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not Stream stream)
                return null;

            try
            {
                using (stream)
                {
                    return new Bitmap(stream);
                }
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
