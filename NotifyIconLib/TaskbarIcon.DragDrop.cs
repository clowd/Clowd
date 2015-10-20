using Clowd.Interop;
using Clowd.Interop.Kernel32;
using Clowd.Interop.Shell32;
using Clowd.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace NotifyIconLib
{
    partial class TaskbarIcon
    {
        /// <summary>
        /// The value of this property determins whether the nessesary hooks will be installed to handle TrayDrop notifications. 
        /// For <see cref="TrayDrop"/> to work, this must be true and the taskbar icon must be pinned to the task bar.
        /// </summary>
        public bool TrayDropEnabled
        {
            get
            {
                return dragDropStatus;
            }
            set
            {
                if (value != dragDropStatus)
                {
                    if (value)
                        StartDragDrop();
                    else
                        StopDragDrop();
                }

            }
        }
        /// <summary>
        /// Occurs when a drop event is triggered on this notification icon. <see cref="TrayDropEnabled"/> must be
        /// set to true, and the taskbar icon must be pinned to the task bar for events to occur.
        /// </summary>
        public event DragEventHandler TrayDrop = delegate { };

        private bool dragDropStatus = false;
        private MouseHook dragHook = null;
        private Clowd.DropWindow dropWindow = null;
        private Point mouseDownPoint = default(Point);
        private DateTime mouseDownTime;
        private bool leftMouseDown = false;
        private DateTime lastStateRefresh;
        private TaskbarIconData stateCache;
        private APPBARDATA taskbarCache;

        /// <summary>
        /// Returns a struct containing information about the current taskbar icon, such as location,
        /// and also if the icon is hidden, in the flyout, or pinned to the taskbar.
        /// </summary>
        public TaskbarIconData GetNotifyIconState()
        {
            TaskbarIconData data;
            Rect rect;
            bool inFlyout;

            if ((Environment.OSVersion.Version >= new Version(6, 0, 7600) && GetNotifyIconRectangleShell(out rect, out inFlyout))
                || GetNotifyIconRectangleLegacy(out rect, out inFlyout))
            {
                data.Location = rect;
                data.State = inFlyout ? TaskbarIconState.InFlyout : TaskbarIconState.Pinned;
            }
            else
            {
                data.Location = Rect.Empty;
                data.State = TaskbarIconState.Hidden;
            }
            return data;
        }

        private Rect GetNotifyAreaRectangle()
        {
            // find the handle of the task bar
            IntPtr taskbarparenthandle = USER32.FindWindow("Shell_TrayWnd", null);

            if (taskbarparenthandle == IntPtr.Zero)
                return Rect.Empty;

            // find the handle of the notification area
            IntPtr naparenthandle = USER32.FindWindowEx(taskbarparenthandle, IntPtr.Zero, "TrayNotifyWnd", null);

            if (naparenthandle == IntPtr.Zero)
                return Rect.Empty;
            var iconLocation = new RECT();
            USER32.GetWindowRect(naparenthandle, out iconLocation);
            return iconLocation;
        }
        private void StartDragDrop()
        {
            if (dragDropStatus)
                return;

            dragDropStatus = true;

            // we want to be notified of global mouse button changes so we can see where the user has 
            // started dragging from and when they are over our icon. So we install a global mouse hook. 
            // Not super ideal but no better ideas atm.
            dragHook = new MouseHook();
            dragHook.MouseDown += DragHook_MouseDown;
            dragHook.MouseUp += DragHook_MouseUp;
            dragHook.MouseMove += DragHook_MouseMove;

            stateCache = GetNotifyIconState();
            dropWindow = new Clowd.DropWindow();
            UpdateDropWindowFromCache();
            //we want to make sure the window is loaded completely.
            dropWindow.Show();
            dropWindow.Hide();
            dropWindow.Topmost = true;
            dropWindow.Drop += DropWindow_Drop;

            dragHook.Start();
        }
        private void StopDragDrop()
        {
            if (!dragDropStatus)
                return;

            dragDropStatus = false;

            dragHook.MouseDown -= DragHook_MouseDown;
            dragHook.MouseUp -= DragHook_MouseUp;
            dragHook.MouseMove -= DragHook_MouseMove;
            dropWindow.Drop -= DropWindow_Drop;
            dragHook.Stop();
            dragHook = null;
            dropWindow.Close();
            dropWindow = null;
        }
        private void UpdateDropWindowFromCache()
        {
            if (stateCache.State != TaskbarIconState.Pinned)
                return;
            var scale = Clowd.Utilities.DpiScale.TranslateDownScaleRect(stateCache.Location);
            dropWindow.Left = scale.Left;
            dropWindow.Top = scale.Top;
            dropWindow.Width = scale.Width;
            dropWindow.Height = scale.Height;
        }
        private void DropWindow_Drop(object sender, DragEventArgs e)
        {
            if (dropWindow.IsVisible)
                dropWindow.Hide();
            TrayDrop(this, e);
        }
        private void DragHook_MouseMove(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            var point = new Point(e.Location.X, e.Location.Y);
            var mouseDown = leftMouseDown;
            var dragging = leftMouseDown && DateTime.Now - mouseDownTime > TimeSpan.FromMilliseconds(300);
            if (dragging && DateTime.Now - lastStateRefresh > TimeSpan.FromSeconds(2))
            {
                stateCache = GetNotifyIconState();
                taskbarCache = GetTaskBarData();
                UpdateDropWindowFromCache();
                lastStateRefresh = DateTime.Now;
            }
            if (dragging && stateCache.Location.Contains(point) && stateCache.State == TaskbarIconState.Pinned)
            {
                if (leftMouseDown && !((Rect)taskbarCache.rc).Contains(mouseDownPoint) && !dropWindow.IsVisible)
                {
                    dropWindow.Show();
                }
            }
            else if (dropWindow.IsVisible)
            {
                dropWindow.Hide();
            }
        }
        private void DragHook_MouseUp(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                leftMouseDown = false;
            }
        }
        private void DragHook_MouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                POINT p;
                p.x = e.Location.X;
                p.y = e.Location.Y;
                //we dont want to interfere with drag operations that originate from the tray area (could be  starting in the flyout).
                var hWnd = Clowd.Interop.USER32.WindowFromPoint(p);
                var wndClass = Clowd.Interop.USER32EX.GetWindowClassName(hWnd);
                if (wndClass != "ToolbarWindow32")
                {
                    mouseDownTime = DateTime.Now;
                    mouseDownPoint = new Point(e.Location.X, e.Location.Y);
                    leftMouseDown = true;
                }
            }
        }
        private bool GetNotifyIconRectangleShell(out Rect rectangle, out bool inFlyout)
        {
            var identifier = new NOTIFYICONIDENTIFIER();
            identifier.uID = iconData.TaskbarIconId;
            identifier.hWnd = iconData.WindowHandle;
            identifier.cbSize = (uint)Marshal.SizeOf(identifier);

            var iconLocation = new RECT();
            int result = SHELL32.Shell_NotifyIconGetRect(ref identifier, out iconLocation);
            Rect rect = iconLocation;

            // 0 means success, 1 means the notify icon is in the fly-out - either is fine
            // if the value is 1, the rectangle will be the location of the flyout expansion button
            inFlyout = result == 1;
            rectangle = rect;
            return ((result == 0 || (result == 1)) && rect.Width > 0 && rect.Height > 0);
        }
        private bool GetNotifyIconRectangleLegacy(out Rect rectangle, out bool inFlyout)
        {
            rectangle = Rect.Empty;
            inFlyout = false;

            var identifier = new NOTIFYICONIDENTIFIER();
            identifier.uID = iconData.TaskbarIconId;
            identifier.hWnd = iconData.WindowHandle;
            identifier.cbSize = (uint)Marshal.SizeOf(identifier);

            // find the handle of the task bar
            IntPtr taskbarparenthandle = USER32.FindWindow("Shell_TrayWnd", null);

            if (taskbarparenthandle == IntPtr.Zero)
                return false;

            // find the handle of the notification area
            IntPtr naparenthandle = USER32.FindWindowEx(taskbarparenthandle, IntPtr.Zero, "TrayNotifyWnd", null);

            if (naparenthandle == IntPtr.Zero)
                return false;

            // make a list of toolbars in the notification area (one of them should contain the icon)
            List<IntPtr> natoolbarwindows = USER32EX.GetChildToolbarWindows(naparenthandle);

            bool found = false;

            for (int i = 0; !found && i < natoolbarwindows.Count; i++)
            {
                IntPtr natoolbarhandle = natoolbarwindows[i];

                // retrieve the number of toolbar buttons (i.e. notify icons)
                int buttoncount = USER32.SendMessage(natoolbarhandle, TOOLBAR_CTRL.TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();

                // get notification area's process id
                uint naprocessid;
                USER32.GetWindowThreadProcessId(natoolbarhandle, out naprocessid);

                // get handle to notification area's process
                IntPtr naprocesshandle = KERNEL32.OpenProcess(ProcessAccessFlags.All, false, naprocessid);

                if (naprocesshandle == IntPtr.Zero)
                    return false;

                // allocate enough memory within the notification area's process to store the button info we want
                IntPtr toolbarmemoryptr = KERNEL32.VirtualAllocEx(naprocesshandle, (IntPtr)null, (uint)Marshal.SizeOf(typeof(TBBUTTON)), AllocationType.Commit, MemoryProtection.ReadWrite);

                if (toolbarmemoryptr == IntPtr.Zero)
                    return false;

                try
                {
                    // loop through the toolbar's buttons until we find our notify icon
                    for (int j = 0; !found && j < buttoncount; j++)
                    {
                        int bytesread = -1;

                        // ask the notification area to give us information about the current button
                        USER32.SendMessage(natoolbarhandle, TOOLBAR_CTRL.TB_GETBUTTON, new IntPtr(j), toolbarmemoryptr);

                        // retrieve that information from the notification area's process
                        TBBUTTON buttoninfo = new TBBUTTON();
                        KERNEL32.ReadProcessMemory(naprocesshandle, toolbarmemoryptr, out buttoninfo, Marshal.SizeOf(buttoninfo), out bytesread);

                        if (bytesread != Marshal.SizeOf(buttoninfo) || buttoninfo.dwData == IntPtr.Zero)
                            return false;

                        // the dwData field contains a pointer to information about the notify icon:
                        // the handle of the notify icon (an 4/8 bytes) and the id of the notify icon (4 bytes)
                        IntPtr niinfopointer = buttoninfo.dwData;

                        // read the notify icon handle
                        IntPtr nihandlenew;
                        KERNEL32.ReadProcessMemory(naprocesshandle, niinfopointer, out nihandlenew, Marshal.SizeOf(typeof(IntPtr)), out bytesread);

                        if (bytesread != Marshal.SizeOf(typeof(IntPtr)))
                            return false;

                        // read the notify icon id
                        uint niidnew;
                        KERNEL32.ReadProcessMemory(naprocesshandle, (IntPtr)((int)niinfopointer + (int)Marshal.SizeOf(typeof(IntPtr))), out niidnew, Marshal.SizeOf(typeof(uint)), out bytesread);

                        if (bytesread != Marshal.SizeOf(typeof(uint)))
                            return false;

                        // if we've found a match
                        if (nihandlenew == identifier.hWnd && niidnew == identifier.uID)
                        {
                            // check if the button is hidden: if it is, return the rectangle of the 'show hidden icons' button
                            if ((byte)(buttoninfo.fsState & 0x08 /*TBSTATE_HIDDEN*/) != 0)
                            {
                                inFlyout = true;
                                return true;
                            }
                            else
                            {
                                inFlyout = false;
                                RECT result = new RECT();

                                // get the relative rectangle of the toolbar button (notify icon)
                                USER32.SendMessage(natoolbarhandle, TOOLBAR_CTRL.TB_GETITEMRECT, new IntPtr(j), toolbarmemoryptr);

                                KERNEL32.ReadProcessMemory(naprocesshandle, toolbarmemoryptr, out result, Marshal.SizeOf(result), out bytesread);

                                if (bytesread != Marshal.SizeOf(result))
                                    return false;

                                // find where the rectangle lies in relation to the screen
                                USER32.MapWindowPoints(natoolbarhandle, (IntPtr)null, ref result, 2);

                                rectangle = result;
                            }

                            found = true;
                        }
                    }
                }
                finally
                {
                    // free resources
                    KERNEL32.VirtualFreeEx(naprocesshandle, toolbarmemoryptr, 0, FreeType.Release);
                    KERNEL32.CloseHandle(naprocesshandle);
                }
            }
            return found;
        }
        private APPBARDATA GetTaskBarData()
        {
            var appBar = new APPBARDATA();
            appBar.hWnd = USER32.FindWindow("Shell_TrayWnd", "");
            appBar.cbSize = (uint)Marshal.SizeOf(appBar);
            System.IntPtr ret = SHELL32.SHAppBarMessage(ABMsg.ABM_GETTASKBARPOS, ref appBar);
            return appBar;
        }
    }
    /// <summary>
    /// Contains information about the current taskbar icon location,
    /// and also if the icon is hidden, in the flyout, or pinned to the taskbar.
    /// </summary>
    public struct TaskbarIconData
    {
        /// <summary>
        /// The current location of the taskbar icon. This property is not DPI aware.
        /// </summary>
        public Rect Location;
        /// <summary>
        /// Contains information about the current state of the taskbar icon
        /// </summary>
        public TaskbarIconState State;
    }
    /// <summary>
    /// Defines the different states the taskbar icon can be in at any given time.
    /// </summary>
    public enum TaskbarIconState
    {
        Pinned,
        InFlyout,
        Hidden
    }
}
