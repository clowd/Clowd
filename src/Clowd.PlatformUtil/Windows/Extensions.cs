using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Win32;

namespace Clowd.PlatformUtil.Windows
{
    internal static class WindowsExtensions
    {
        public static RegistryKey CreateOrOpenExisting(this RegistryKey key, string name)
        {
            return key.OpenSubKey(name, true) ?? key.CreateSubKey(name);
        }
    }
}
