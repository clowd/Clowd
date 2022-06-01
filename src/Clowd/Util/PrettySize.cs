using System;

namespace Clowd.Util
{
    public static class PrettySize
    {
        public static string Format(long bytes, int decimalPlaces = 2)
        {
            if (bytes < 1000) return bytes + " B";
            if (bytes < 1000000) return Math.Round(bytes / (double)1000, decimalPlaces) + " KB";
            if (bytes < 1000000000) return Math.Round(bytes / (double)1000000, decimalPlaces) + " MB";
            return Math.Round(bytes / (double)1000000000, decimalPlaces) + " GB";
        }
    }
}
