using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;
using System.Drawing;
using Clowd.Interop;
using Clowd.Interop.Shell32;

namespace NotifyIconLib
{
    // http://www.crowsprogramming.com/archives/88 used as a reference

    internal partial class Taskbar
    {
        /// <summary>
        /// Represents the edge of the screen the taskbar is docked to.
        /// </summary>
        public enum TaskbarEdge
        {
            Left = ABEdge.ABE_LEFT,
            Top = ABEdge.ABE_TOP,
            Right = ABEdge.ABE_RIGHT,
            Bottom = ABEdge.ABE_BOTTOM
        }

        /// <summary>
        /// The states the taskbar can be in.
        /// </summary>
        [Flags]
        public enum TaskbarState
        {
            /// <summary>
            /// No autohide, not always top
            /// </summary>
            None = ABState.ABS_MANUAL,

            /// <summary>
            /// Hides task bar when mouse exits task bar region
            /// </summary>
            AutoHide = ABState.ABS_AUTOHIDE,

            /// <summary>
            /// Taskbar is always on top of other windows
            /// </summary>
            AlwaysTop = ABState.ABS_ALWAYSONTOP
        }

        /// <summary>
        /// Gets the rectangle of the taskbar.
        /// </summary>
        /// <returns>The taskbar rectangle.</returns>
        public static Rectangle GetTaskbarRectangle()
        {
            var appBar = GetTaskBarData();
            return appBar.rc;
        }

        public static Rectangle GetTaskListRectangle()
        {
            IntPtr hTaskbarHandle = USER32.FindWindow("Shell_TrayWnd", null);
            if (hTaskbarHandle != IntPtr.Zero)
            {
                IntPtr hSystemTray = USER32.FindWindowEx(hTaskbarHandle, IntPtr.Zero, "ReBarWindow32", null);
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
        /// Gets the location, in screen coordinates of the taskbar.
        /// </summary>
        /// <returns>The taskbar location.</returns>
        public static Point GetTaskbarLocation()
        {
            return GetTaskbarRectangle().Location;
        }

        /// <summary>
        /// Gets the size, in pixels of the taskbar.
        /// </summary>
        /// <returns>The taskbar size.</returns>
        public static Size GetTaskbarSize()
        {
            return GetTaskbarRectangle().Size;
        }

        /// <summary>
        /// Gets the edge of the screen that the taskbar is docked to.
        /// </summary>
        /// <returns></returns>
        public static TaskbarEdge GetTaskbarEdge()
        {
            var appBar = GetTaskBarData();
            return (TaskbarEdge)appBar.uEdge;
        }

        /// <summary>
        /// Gets the current state of the taskbar.
        /// </summary>
        /// <returns></returns>
        public static TaskbarState GetTaskbarState()
        {
            var appBar = CreateAppBarData();
            return (TaskbarState)SHELL32.SHAppBarMessage(ABMsg.ABM_GETSTATE, ref appBar);
        }

        /// <summary>
        /// Sets the state of the taskbar.
        /// </summary>
        /// <param name="state">The new state.</param>
        public static void SetTaskBarState(TaskbarState state)
        {
            var appBar = CreateAppBarData();
            appBar.lParam = (IntPtr)state;
            SHELL32.SHAppBarMessage(ABMsg.ABM_SETSTATE, ref appBar);
        }

        /// <summary>
        /// Gets an APPBARDATA struct with valid location, size, and edge of the taskbar.
        /// </summary>
        /// <returns></returns>
        private static APPBARDATA GetTaskBarData()
        {
            var appBar = CreateAppBarData();
            System.IntPtr ret = SHELL32.SHAppBarMessage(ABMsg.ABM_GETTASKBARPOS, ref appBar);
            return appBar;
        }

        /// <summary>
        /// Creats an APPBARDATA struct with its hWnd member set to the task bar window.
        /// </summary>
        /// <returns></returns>
        private static APPBARDATA CreateAppBarData()
        {
            var appBar = new APPBARDATA();
            appBar.hWnd = USER32.FindWindow("Shell_TrayWnd", "");
            appBar.cbSize = (uint)Marshal.SizeOf(appBar);
            return appBar;
        }

    }
}
