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
using System.Drawing;
using System.ComponentModel;
using System.Threading.Tasks;
using Clowd.Interop.Com;

namespace Clowd.Utilities
{
    public class WindowFinder2 : IDisposable
    {
        private const int MaxWindowDepthToSearch = 4;
        private const int MinWinCaptureBounds = 200;

        private readonly List<CachedWindow> _cachedWindows = new List<CachedWindow>();
        private readonly HashSet<IntPtr> _hWndsAlreadyProcessed = new HashSet<IntPtr>();
        private readonly Stack<CachedWindow> _parentStack = new Stack<CachedWindow>();
        private readonly IVirtualDesktopManager _virtualDesktop = VirtualDesktopManager.CreateNew();

        ~WindowFinder2()
        {
            this.Dispose();
        }

        public void NewCapture()
        {
            this._cachedWindows.Clear();
            USER32.EnumWindowProc enumWindowsProc = new USER32.EnumWindowProc(this.EvalWindow);
            USER32.EnumWindows(enumWindowsProc, IntPtr.Zero);
            this._hWndsAlreadyProcessed.Clear();
            PopulateAllMetadata();
        }
        public void PopulateWindowBitmaps()
        {
            foreach (var c in _cachedWindows.Where(w => w.IsVisible && w.Depth == 0 && w.IsPartiallyCovered))
                c.PopulateBitmaps();
        }
        public CachedWindow GetWindowThatContainsPoint(ScreenPoint point)
        {
            foreach (var window in _cachedWindows)
                if (window.ImageBoundsRect.Contains(point))
                    return window;

            return new CachedWindow();
        }
        public CachedWindow GetTopLevel(CachedWindow child)
        {
            if (child.Parent != null) return GetTopLevel(child.Parent);
            return child;
        }
        public void Dispose()
        {
            _cachedWindows.ForEach(c => c.Dispose());
        }

        private bool EvalWindow(IntPtr hWnd, IntPtr depth)
        {
            var depthInt = depth.ToInt32();
            var parent = depthInt > 0 ? _parentStack.Peek() : null;

            // if the window is not on the current virtual desktop we want to ignore it and it's children
            if (depthInt == 0 && !_virtualDesktop.IsWindowOnCurrentVirtualDesktop(hWnd))
                return true;

            if (!this._hWndsAlreadyProcessed.Contains(hWnd) && USER32.IsWindowVisible(hWnd) && !USER32.IsIconic(hWnd))
            {
                ScreenRect windowRect = this.GetWindowBounds(hWnd);
                ScreenRect boundsOfScreenWorkspace = windowRect;

                string className;
                if (this.IsMetroOrPhantomWindow((IntPtr)hWnd, depthInt, out className))
                    return true;

                // clip to workspace
                if (this.IsTopLevelMaximizedWindow(hWnd, depthInt))
                    boundsOfScreenWorkspace = ScreenTools.GetScreenContaining(boundsOfScreenWorkspace).WorkingArea;

                // clip to parent window
                if (parent != null)
                    boundsOfScreenWorkspace = boundsOfScreenWorkspace.Intersect(parent.ImageBoundsRect);

                // clip to screen
                boundsOfScreenWorkspace = ScreenTools.VirtualScreen.Bounds.Intersect(boundsOfScreenWorkspace);

                if (this.BoundsAreLargeEnoughForCapture(boundsOfScreenWorkspace, depthInt))
                {
                    //uint processID = 0;
                    //USER32.GetWindowThreadProcessId(hWnd, out processID);
                    var caption = USER32EX.GetWindowCaption(hWnd);
                    var cwin = new CachedWindow(hWnd, depthInt, className, caption, windowRect, boundsOfScreenWorkspace, parent);

                    if (depthInt < MaxWindowDepthToSearch)
                    {
                        this._parentStack.Push(cwin);
                        USER32.EnumWindowProc enumWindowsProc = new USER32.EnumWindowProc(this.EvalWindow);
                        USER32.EnumChildWindows(hWnd, enumWindowsProc, IntPtr.Add(depth, 1));
                        this._parentStack.Pop();
                    }

                    this._cachedWindows.Add(cwin);
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
                    case "Intermediate D3D Window": // Chrome / Chromium intermediate window
                    case "CEF-OSC-WIDGET": // NVIDIA GeForce Overlay
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
        private bool BoundsAreLargeEnoughForCapture(ScreenRect windowBounds, int depth)
        {
            int minSize = (depth == 0) ? 25 : MinWinCaptureBounds;

            if (windowBounds.Height <= minSize)
                return false;
            return windowBounds.Width > minSize;
        }
        private void PopulateAllMetadata()
        {
            Region screen = new Region(ScreenTools.VirtualScreen.Bounds.ToSystem());
            for (int i = 0; i < _cachedWindows.Count; i++)
            {
                var w = _cachedWindows[i];

                var rect = w.WindowRect.ToSystem();
                w.IsVisible = screen.IsVisible(rect);

                if (w.IsVisible)
                {
                    for (int z = i; z >= 0; z--)
                    {
                        var whTest = _cachedWindows[z];
                        if (w.WindowRect.IntersectsWith(whTest.WindowRect))
                        {
                            if (GetTopLevel(whTest) != GetTopLevel(w))
                            {
                                w.IsPartiallyCovered = true;
                                break;
                            }
                        }
                    }
                }

                if (w.Depth == 0)
                    screen.Exclude(rect);
            }
        }

        public class CachedWindow : IDisposable
        {
            public IntPtr Handle { get; private set; }
            public ScreenRect ImageBoundsRect { get; private set; } = ScreenRect.Empty;
            public ScreenRect WindowRect { get; private set; } = ScreenRect.Empty;
            public string Caption { get; private set; }
            public string ClassName { get; private set; }
            public bool IsVisible { get; set; }
            public bool IsPartiallyCovered { get; set; }
            public int Depth { get; private set; }
            public Bitmap WindowBitmap { get; private set; }
            public System.Windows.Media.Imaging.BitmapSource WindowBitmapWpf { get; private set; }
            public CachedWindow Parent { get; private set; }

            public CachedWindow()
            {
            }

            public CachedWindow(IntPtr handle, int depth, string className, string caption, ScreenRect windowRect, ScreenRect croppedRect, CachedWindow parent)
            {
                this.Handle = handle;
                this.ImageBoundsRect = croppedRect;
                this.WindowRect = windowRect;
                this.Caption = caption;
                this.ClassName = className;
                //this.ProcessID = processId;
                this.Depth = depth;
                this.Parent = parent;
            }

            ~CachedWindow()
            {
                this.Dispose();
            }

            public void PopulateBitmaps()
            {
                if (Depth > 0)
                    throw new InvalidOperationException("Must only call GetBitmap on a top-level window");

                if (WindowBitmap != null)
                    return;

                RECT normalWindowBounds;
                if (!USER32.GetWindowRect(this.Handle, out normalWindowBounds))
                    throw new Win32Exception();

                var basicBounds = ScreenRect.FromSystem(normalWindowBounds);

                Bitmap initialBmp = new Bitmap(basicBounds.Width, basicBounds.Height);
                using (Graphics g = Graphics.FromImage(initialBmp))
                {
                    IntPtr dc = g.GetHdc();
                    try
                    {
                        bool success = USER32.PrintWindow(this.Handle, dc, PrintWindowDrawingOptions.PW_RENDERFULLCONTENT);
                        if (!success)
                            throw new Win32Exception();
                    }
                    finally
                    {
                        g.ReleaseHdc(dc);
                    }
                }

                // windows print outside their bounds (drop shadows, etc), GetWindowRect returns the true size (thus also the size of rectangle that PrintWindow needs
                // but typically we want to omit the drop shadow and blending area and just show the logical window size. 
                // To achieve this we need to print the window at full size and then crop away the margins
                // Additionally, when a window is full screen, it extends beyond the screen boundary 

                var xoffset = this.WindowRect.Left - basicBounds.Left;
                var yoffset = this.WindowRect.Top - basicBounds.Top;

                if (xoffset < 1 && yoffset < 1)
                {
                    WindowBitmap = initialBmp;
                    WindowBitmapWpf = initialBmp.ToBitmapSource();
                }
                else
                {
                    var croppingRectangle = new Rectangle(xoffset, yoffset, WindowRect.Width, WindowRect.Height);
                    var newBmp = initialBmp.Crop(croppingRectangle);
                    WindowBitmap = newBmp;
                    WindowBitmapWpf = newBmp.ToBitmapSource();
                    initialBmp.Dispose();
                }
            }

            public override string ToString()
            {
                return $"{Caption} / {ClassName} [x:{ImageBoundsRect.Left} y:{ImageBoundsRect.Top} w:{ImageBoundsRect.Width} h:{ImageBoundsRect.Height}]";
            }

            public void Dispose()
            {
                if (WindowBitmap != null)
                    WindowBitmap.Dispose();
            }

            public override bool Equals(object obj)
            {
                var other = obj as CachedWindow;
                if (other == null)
                    return false;

                return this.Handle == other.Handle;
            }

            public override int GetHashCode()
            {
                return this.Handle.GetHashCode();
            }
        }
    }
}
