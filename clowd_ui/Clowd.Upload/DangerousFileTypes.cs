using System;
using System.Collections.Generic;

namespace Clowd.Upload
{
    /// <summary>
    /// File extensions that browsers and mail scanners commonly block or warn on when downloaded
    /// directly. Uploads of these types are wrapped in a zip archive (when the setting is on) so
    /// the shared link stays usable.
    /// </summary>
    internal static class DangerousFileTypes
    {
        private static readonly HashSet<string> _extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "exe", "dll", "msi", "msix", "msixbundle", "appx", "appxbundle", "bat", "cmd", "com",
            "scr", "pif", "cpl", "msc", "jar", "js", "jse", "vbs", "vbe", "ws", "wsf", "wsh",
            "hta", "ps1", "psm1", "psd1", "reg", "lnk", "url", "application", "gadget", "diagcab",
            "iso", "img", "vhd", "vhdx",
        };

        /// <summary>Whether the extension (with or without a leading dot, any casing) is a type
        /// that browsers typically refuse to download directly.</summary>
        public static bool IsDangerous(string extension)
        {
            if (String.IsNullOrWhiteSpace(extension))
                return false;

            return _extensions.Contains(extension.Trim().TrimStart('.'));
        }
    }
}
