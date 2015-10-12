

using Clowd.Interop;
using Clowd.Interop.DwmApi;
/**
* Copyright (c) 2010-2011, Richard Z.H. Wang <http://zhwang.me/>
* 
* This library is free software: you can redistribute it and/or modify
* it under the terms of the GNU Lesser General Public License as
* published by the Free Software Foundation, either version 3 of the
* License, or (at your option) any later version.
* 
* This library is distributed in the hope that it will be useful,
* but WITHOUT ANY WARRANTY; without even the implied warranty of
* MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
* GNU Lesser General Public License for more details.
* 
* You should have received a copy of the GNU Lesser General Public License
* along with this license.  If not, see <http://www.gnu.org/licenses/>.
*/
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace Clowd.Helpers
{
    internal static class SysInfo
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

        public static bool ForegroundWindowIsFullScreen
        {
            get
            {
                IntPtr foreWindow = USER32.GetForegroundWindow();

                RECT foreRect;
                USER32.GetWindowRect(foreWindow, out foreRect);

                Size screenSize = System.Windows.Forms.SystemInformation.PrimaryMonitorSize;

                return (foreRect.left <= 0 && foreRect.top <= 0 &&
                    foreRect.right >= screenSize.Width && foreRect.bottom >= screenSize.Height);
            }
        }

        public static bool IsRemoteSession
        {
            get
            {
                return System.Windows.Forms.SystemInformation.TerminalServerSession;
                //return (SystemParameters.IsRemoteSession || SystemParameters.IsRemotelyControlled);
                //can't use the above since they were introduced with .net 4.0
            }
        }

        public static bool IsDWMEnabled
        {
            get
            {
                if (!SysInfo.IsWindowsVistaOrLater)
                    return false;
                bool result;
                DWMAPI.DwmIsCompositionEnabled(out result);
                return result;
            }
        }
    }
}
