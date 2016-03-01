using Clowd.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Hardcodet.Wpf.TaskbarNotification
{
    public class NotifyArea
    {
        public static Rect GetRectangle()
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

            return Rect.Empty;
        }

        /// <summary>
        /// Retrieves the rectangle of the 'Show Hidden Icons' button, or null if it can't be found.
        /// </summary>
        /// <returns>Rectangle containing bounds of 'Show Hidden Icons' button, or null if it can't be found.</returns>
        public static Rect GetButtonRectangle()
        {
            // find the handle of the taskbar
            IntPtr taskbarparenthandle = USER32.FindWindow("Shell_TrayWnd", null);

            if (taskbarparenthandle == (IntPtr)null)
                return Rect.Empty;

            // find the handle of the notification area
            IntPtr naparenthandle = USER32.FindWindowEx(taskbarparenthandle, IntPtr.Zero, "TrayNotifyWnd", null);

            if (naparenthandle == (IntPtr)null)
                return Rect.Empty;

            List<IntPtr> nabuttonwindows = USER32EX.GetChildButtonWindows(naparenthandle);

            if (nabuttonwindows.Count == 0)
                return Rect.Empty; // found no buttons

            IntPtr buttonpointer = nabuttonwindows[0]; // just take the first button

            RECT result;

            if (!USER32.GetWindowRect(buttonpointer, out result))
                return Rect.Empty; // return null if we can't find the button

            return result;
        }
    }
}
