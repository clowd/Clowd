using System;
using System.Security.Principal;
using static CsWin32.PInvoke;

namespace Clowd.PlatformUtil.Windows
{
    public static class SysInfo
    {
        private readonly static bool _isWindowsNT = Environment.OSVersion.Platform == PlatformID.Win32NT;

        public static bool IsWindowsVistaOrLater
        {
            get { return _isWindowsNT && Environment.OSVersion.Version >= new Version(6, 0, 0); }
        }
        public static bool IsWindows7OrLater
        {
            get { return _isWindowsNT && Environment.OSVersion.Version >= new Version(6, 0, 7600); }
        }
        public static bool IsWindows8OrLater
        {
            get { return _isWindowsNT && Environment.OSVersion.Version >= new Version(6, 2, 0); }
        }
        public static bool IsWindows8_1OrLater
        {
            get { return _isWindowsNT && Environment.OSVersion.Version >= new Version(6, 3, 0); }
        }
        public static bool IsWindows10OrLater
        {
            get { return _isWindowsNT && Environment.OSVersion.Version >= new Version(10, 0, 0); }
        }

        //public static bool ForegroundWindowIsFullScreen
        //{
        //    get
        //    {
        //        IntPtr foreWindow = USER32.GetForegroundWindow();

        //        RECT foreRect;
        //        USER32.GetWindowRect(foreWindow, out foreRect);

        //        Size screenSize = System.Windows.Forms.SystemInformation.PrimaryMonitorSize;

        //        return (foreRect.left <= 0 && foreRect.top <= 0 &&
        //            foreRect.right >= screenSize.Width && foreRect.bottom >= screenSize.Height);
        //    }
        //}

        //public static bool IsRemoteSession
        //{
        //    get
        //    {
        //        //return System.Windows.Forms.SystemInformation.TerminalServerSession;
        //        return (System.Windows.SystemParameters.IsRemoteSession || System.Windows.SystemParameters.IsRemotelyControlled);
        //        //above were introduced with .net 4.0
        //    }
        //}

        public static bool IsProcessElevated
        {
            get
            {
                return WindowsIdentity.GetCurrent().Owner
                  .IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
            }
        }

        public static bool IsDWMEnabled
        {
            get
            {
                if (!SysInfo.IsWindowsVistaOrLater)
                    return false;
                DwmIsCompositionEnabled(out var result);
                return result;
            }
        }
    }
}
