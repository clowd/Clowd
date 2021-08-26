using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RT.Serialization;

namespace Clowd.Drawing
{
    internal static class CachedBitmapLoader
    {
        static Dictionary<string, BitmapSource> _cache = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        public static BitmapSource LoadFromFile(string filePath)
        {
            if (_cache.TryGetValue(filePath, out var cb))
                return cb;

            // load this way so the file handle is not kept open
            BitmapImage bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(filePath);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();

            _cache[filePath] = bi;
            return bi;
        }

        public static void InvalidateCache()
        {
            _cache = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
