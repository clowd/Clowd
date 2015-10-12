

using Clowd.Interop;
/**
* Copyright (c) 2010-2011, Richard Z.H. Wang <http://zhwang.me/>
* Copyright (c) 2010-2011, David Warner <http://quppa.net/>
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
    public class NotifyArea
    {
        public static Rectangle GetRectangle()
        {
            IntPtr hTaskbarHandle = USER32.FindWindow("Shell_TrayWnd", null);
            if (hTaskbarHandle != IntPtr.Zero)
            {
                IntPtr hSystemTray = USER32.FindWindowEx(hTaskbarHandle, IntPtr.Zero, "TrayNotifyWnd", null);
                if (hSystemTray != IntPtr.Zero)
                {
                    RECT rect;
                    USER32.GetWindowRect(hSystemTray, out rect);
                    if (rect.HasSize())
                        return rect;
                }
            }

            return Rectangle.Empty;
        }

        /// <summary>
        /// Retrieves the rectangle of the 'Show Hidden Icons' button, or null if it can't be found.
        /// </summary>
        /// <returns>Rectangle containing bounds of 'Show Hidden Icons' button, or null if it can't be found.</returns>
        public static Rectangle GetButtonRectangle()
        {
            // find the handle of the taskbar
            IntPtr taskbarparenthandle = USER32.FindWindow("Shell_TrayWnd", null);

            if (taskbarparenthandle == (IntPtr)null)
                return Rectangle.Empty;

            // find the handle of the notification area
            IntPtr naparenthandle = USER32.FindWindowEx(taskbarparenthandle, IntPtr.Zero, "TrayNotifyWnd", null);

            if (naparenthandle == (IntPtr)null)
                return Rectangle.Empty;

            List<IntPtr> nabuttonwindows = USER32EX.GetChildButtonWindows(naparenthandle);

            if (nabuttonwindows.Count == 0)
                return Rectangle.Empty; // found no buttons

            IntPtr buttonpointer = nabuttonwindows[0]; // just take the first button

            RECT result;

            if (!USER32.GetWindowRect(buttonpointer, out result))
                return Rectangle.Empty; // return null if we can't find the button

            return result;
        }
    }
}
