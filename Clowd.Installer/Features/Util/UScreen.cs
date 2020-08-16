using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer.Features
{
    public static class UScreen
    {
        public const string UScreenRegistryPath = "SOFTWARE\\UNREAL\\Live\\UScreenCapture";
        public static void SetProperties(System.Drawing.Rectangle bounds, int framerate, bool showCursor, bool captureLayeredWindows)
        {
            RegistryKey k64 = null, k32 = RegistryEx.CreateKeyFromRootPath(UScreenRegistryPath, InstallMode.System, RegistryView.Registry32);

            if (Environment.Is64BitOperatingSystem)
                k64 = RegistryEx.CreateKeyFromRootPath(UScreenRegistryPath, InstallMode.System, RegistryView.Registry64);

            k32?.SetValue("MonitorNum", 0);
            k64?.SetValue("MonitorNum", 0);

            k32?.SetValue("Left", bounds.Left);
            k64?.SetValue("Left", bounds.Left);

            k32?.SetValue("Right", bounds.Left + bounds.Width);
            k64?.SetValue("Right", bounds.Left + bounds.Width);

            k32?.SetValue("Top", bounds.Top);
            k64?.SetValue("Top", bounds.Top);

            k32?.SetValue("Bottom", bounds.Top + bounds.Height);
            k64?.SetValue("Bottom", bounds.Top + bounds.Height);

            k32?.SetValue("FrameRate", framerate);
            k64?.SetValue("FrameRate", framerate);

            k32?.SetValue("ShowCursor", showCursor ? 1 : 0);
            k64?.SetValue("ShowCursor", showCursor ? 1 : 0);

            k32?.SetValue("CaptureLayeredWindows", captureLayeredWindows ? 1 : 0);
            k64?.SetValue("CaptureLayeredWindows", captureLayeredWindows ? 1 : 0);
        }

        public static void DeleteProperties()
        {
            foreach (var root in RegistryEx.OpenKeysFromRootPath("SOFTWARE", RegistryQuery.System))
                using (root)
                    root.DeleteSubKey("UNREAL", false);
        }
    }
}
