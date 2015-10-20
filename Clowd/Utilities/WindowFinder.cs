using System;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Forms;
using Clowd.Interop;
using Clowd.Utilities;
using Clowd.Interop.DwmApi;

namespace Clowd.Utilities
{
    public class WindowFinder2
    {
        private const int MaxWindowDepthToSearch = 4;
        private const int MinWinCaptureBounds = 200;

        private readonly List<CachedWindow> _cachedWindows = new List<CachedWindow>();
        private readonly List<IntPtr> _hWndsAlreadyProcessed = new List<IntPtr>();
        private readonly Stack<Rect> _parentRects = new Stack<Rect>();

        public void Capture()
        {
            this._cachedWindows.Clear();
            USER32.EnumWindowProc enumWindowsProc = new USER32.EnumWindowProc(this.EvalWindow);
            USER32.EnumWindows(enumWindowsProc, new IntPtr(0));
            this._hWndsAlreadyProcessed.Clear();

        }
        public CachedWindow GetWindowThatContainsPoint(Point point)
        {
            point.Offset(DpiScale.UpScaleX(SystemParameters.VirtualScreenLeft), DpiScale.UpScaleY(SystemParameters.VirtualScreenTop));
            foreach (var window in _cachedWindows)
            {
                if (window.WindowRect.Contains(point))
                {
                    var temp = window;
                    temp.WindowRect.Offset(-DpiScale.UpScaleX(SystemParameters.VirtualScreenLeft), -DpiScale.UpScaleY(SystemParameters.VirtualScreenTop));
                    return temp;
                }
            }
            return new CachedWindow() { WindowRect = Rect.Empty };
        }
        public bool IsWindowPartiallyCovered(CachedWindow window)
        {
            int count = 0;
            foreach (var w in _cachedWindows)
            {
                if (w == window)
                    break;
                if (window.WindowRect.IntersectsWith(w.WindowRect))
                {
                    //only return true if the covering handle is owned by a different process.
                    //this is not 100% accurate, but is satisfactory until a new method can be designed
                    if (window.ProcessID != w.ProcessID)
                        return true;
                }
                count++;
            }
            return false;
        }
        private bool EvalWindow(IntPtr hWnd, IntPtr depth)
        {
            var depthInt = depth.ToInt32();
            if (!this._hWndsAlreadyProcessed.Contains(hWnd) && USER32.IsWindowVisible(hWnd) && !USER32.IsIconic(hWnd))
            {
                Rect boundsOfScreenWorkspace = (Rect)this.GetWindowBounds(hWnd);
                string className;
                if (this.IsMetroOrPhantomWindow((IntPtr)hWnd, depthInt, out className))
                {
                    return true;
                }
                if (this.IsTopLevelMaximizedWindow(hWnd, depthInt))
                {
                    boundsOfScreenWorkspace = this.GetBoundsOfScreenContainingRect(boundsOfScreenWorkspace);
                }
                boundsOfScreenWorkspace = this.GetWindowBoundsClippedToParentWindow(boundsOfScreenWorkspace);
                boundsOfScreenWorkspace = this.GetWindowBoundsClippedToScreen(boundsOfScreenWorkspace);
                if (this.BoundsAreLargeEnoughForCapture(boundsOfScreenWorkspace, depthInt))
                {
                    if (depthInt < MaxWindowDepthToSearch)
                    {
                        this._parentRects.Push(boundsOfScreenWorkspace);
                        USER32.EnumWindowProc enumWindowsProc = new USER32.EnumWindowProc(this.EvalWindow);
                        USER32.EnumChildWindows(hWnd, enumWindowsProc, IntPtr.Add(depth, 1));
                        this._parentRects.Pop();
                    }
                    uint processID = 0;
                    USER32.GetWindowThreadProcessId(hWnd, out processID);
                    this._cachedWindows.Add(new CachedWindow()
                    {
                        Caption = USER32EX.GetWindowCaption(hWnd),
                        ClassName = className,
                        Handle = hWnd,
                        WindowRect = boundsOfScreenWorkspace,
                        ProcessID = processID
                    });
                    this._hWndsAlreadyProcessed.Add(hWnd);
                }
            }
            return true;
        }
        private RECT GetWindowBounds(IntPtr hWnd)
        {
            bool dwmSuccess = false;
            RECT normalWindowBounds = new RECT();
            try
            {
                bool flag;
                DWMAPI.DwmIsCompositionEnabled(out flag);
                if (flag)
                {
                    dwmSuccess = 0 == DWMAPI.DwmGetWindowAttribute(hWnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, out normalWindowBounds, Marshal.SizeOf(typeof(RECT)));
                }
            }
            catch (DllNotFoundException) { }
            if (!dwmSuccess)
            {
                if (!USER32.GetWindowRect(hWnd, out normalWindowBounds))
                {
                    throw new Exception(string.Format("Could not get boundary for window with handle: {0}", hWnd));
                }
            }
            return normalWindowBounds;
        }
        private bool IsMetroOrPhantomWindow(IntPtr hWnd, int depth, out string className)
        {
            StringBuilder stringBuilder = new StringBuilder(255);
            USER32.GetClassName(hWnd, stringBuilder, stringBuilder.Capacity);
            string str = stringBuilder.ToString();
            className = str;
            if (str != null)
            {
                switch (str)
                {
                    case "EdgeUiInputWndClass":
                    case "TaskListThumbnailWnd":
                    case "LauncherTipWndClass":
                    case "SearchPane":
                    case "ImmersiveLauncher":
                    case "Touch Tooltip Window":
                    case "Windows.UI.Core.CoreWindow":
                    case "Immersive Chrome Container":
                    case "ImmersiveBackgroundWindow":
                    case "NativeHWNDHost":
                    case "Snapped Desktop":
                    case "ModeInputWnd":
                    case "MetroGhostWindow":
                    case "Shell_Dim":
                    case "Shell_Dialog":
                    case "ApplicationManager_ImmersiveShellWindow":
                        {
                            return true;
                        }
                }
                //check if this is a windows 10 phantom metro window.
                //if it has the children ApplicationFrameInputSinkWindow and ApplicationFrameTitleBarWindow
                //and not Windows.UI.Core.CoreWindow, it is not a real window.
                if (str == "ApplicationFrameWindow" && depth == 0)
                {
                    var children = USER32EX.GetChildWindows(hWnd)
                        .Select(child => new
                        {
                            Handle = child,
                            Class = USER32EX.GetWindowClassName(child)
                        });
                    if (children.Any(child => child.Class == "ApplicationFrameInputSinkWindow") &&
                        children.Any(child => child.Class == "ApplicationFrameTitleBarWindow"))
                    {
                        if (!children.Any(child => child.Class == "Windows.UI.Core.CoreWindow"))
                            return true;
                    }
                }
            }
            return false;
        }
        private bool IsTopLevelMaximizedWindow(IntPtr hWnd, int depth)
        {
            if (depth != 0)
            {
                return false;
            }
            return USER32.IsZoomed(hWnd);
        }
        public Rect GetBoundsOfScreenContainingRect(Rect bounds, bool returnWorkingAreaOnly = true)
        {
            var point = new System.Drawing.Point((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
            Rect retval = Rect.Empty;
            Screen[] allScreens = Screen.AllScreens;
            for (int i = 0; i < (int)allScreens.Length; i++)
            {
                Screen screen = allScreens[i];
                if (screen.Bounds.Contains(point))
                {
                    if (returnWorkingAreaOnly)
                        retval = new Rect(screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height);
                    else
                        retval = new Rect(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
                }
            }
            return retval;
        }
        private Rect GetWindowBoundsClippedToParentWindow(Rect windowBounds)
        {
            if (this._parentRects.Count > 0)
            {
                windowBounds.Intersect(this._parentRects.Peek());
            }
            return windowBounds;
        }
        private Rect GetWindowBoundsClippedToScreen(Rect windowBounds)
        {
            Rect rect = new Rect(DpiScale.UpScaleX(SystemParameters.VirtualScreenLeft), DpiScale.UpScaleY(SystemParameters.VirtualScreenTop), DpiScale.UpScaleX(SystemParameters.VirtualScreenWidth), DpiScale.UpScaleY(SystemParameters.VirtualScreenHeight));
            windowBounds.Intersect(rect);
            return windowBounds;
        }
        private bool BoundsAreLargeEnoughForCapture(Rect windowBounds, int depth)
        {
            int minSize = (depth == 0) ? 25 : MinWinCaptureBounds;

            if (windowBounds.Size.Height <= minSize)
                return false;
            return windowBounds.Width > minSize;
        }
        public class CachedWindow
        {
            public IntPtr Handle;
            public Rect WindowRect;
            public string Caption;
            public string ClassName;
            public uint ProcessID;
            public override string ToString()
            {
                return $"{Caption} / {ClassName} [x:{WindowRect.X} y:{WindowRect.Y} w:{WindowRect.Width} h: {WindowRect.Height}]";
            }
        }
    }
}
