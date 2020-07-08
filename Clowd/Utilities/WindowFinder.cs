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
using Clowd.Interop.Shcore;
using CS.Wpf;
using ScreenVersusWpf;

namespace Clowd.Utilities
{
    public class WindowFinder2
    {
        private const int MaxWindowDepthToSearch = 4;
        private const int MinWinCaptureBounds = 200;

        private readonly List<CachedWindow> _cachedWindows = new List<CachedWindow>();
        private readonly List<IntPtr> _hWndsAlreadyProcessed = new List<IntPtr>();
        private readonly Stack<ScreenRect> _parentRects = new Stack<ScreenRect>();
        private readonly IVirtualDesktopManager _virtualDesktop = VirtualDesktopManager.CreateNew();

        public void Capture()
        {
            this._cachedWindows.Clear();
            USER32.EnumWindowProc enumWindowsProc = new USER32.EnumWindowProc(this.EvalWindow);
            USER32.EnumWindows(enumWindowsProc, IntPtr.Zero);
            this._hWndsAlreadyProcessed.Clear();

        }
        public CachedWindow GetWindowThatContainsPoint(ScreenPoint point)
        {
            foreach (var window in _cachedWindows)
            {
                if (window.WindowRect.Contains(point))
                {
                    var temp = window;
                    return temp;
                }
            }
            return new CachedWindow() { WindowRect = ScreenRect.Empty };
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
            // if the window is not on the current virtual desktop we want to ignore it and it's children
            if (depthInt == 0 && !_virtualDesktop.IsWindowOnCurrentVirtualDesktop(hWnd))
                return true;

            if (!this._hWndsAlreadyProcessed.Contains(hWnd) && USER32.IsWindowVisible(hWnd) && !USER32.IsIconic(hWnd))
            {
                ScreenRect boundsOfScreenWorkspace = this.GetWindowBounds(hWnd);
                string className;
                if (this.IsMetroOrPhantomWindow((IntPtr)hWnd, depthInt, out className))
                {
                    return true;
                }
                if (this.IsTopLevelMaximizedWindow(hWnd, depthInt))
                {
                    boundsOfScreenWorkspace = ScreenTools.GetScreenContaining(boundsOfScreenWorkspace).WorkingArea;
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
        private ScreenRect GetWindowBounds(IntPtr hWnd)
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
            return ScreenRect.FromSystem(normalWindowBounds);
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
        private ScreenRect GetWindowBoundsClippedToParentWindow(ScreenRect windowBounds)
        {
            if (this._parentRects.Count > 0)
            {
                windowBounds = windowBounds.Intersect(this._parentRects.Peek());
            }
            return windowBounds;
        }
        private ScreenRect GetWindowBoundsClippedToScreen(ScreenRect windowBounds)
        {
            return ScreenTools.VirtualScreen.Bounds.Intersect(windowBounds);
        }
        private bool BoundsAreLargeEnoughForCapture(ScreenRect windowBounds, int depth)
        {
            int minSize = (depth == 0) ? 25 : MinWinCaptureBounds;

            if (windowBounds.Height <= minSize)
                return false;
            return windowBounds.Width > minSize;
        }
        public class CachedWindow
        {
            public IntPtr Handle;
            public ScreenRect WindowRect;
            public string Caption;
            public string ClassName;
            public uint ProcessID;
            public override string ToString()
            {
                return $"{Caption} / {ClassName} [x:{WindowRect.Left} y:{WindowRect.Top} w:{WindowRect.Width} h: {WindowRect.Height}]";
            }
        }
    }
}
