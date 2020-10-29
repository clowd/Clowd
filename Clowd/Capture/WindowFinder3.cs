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
using ScreenVersusWpf;
using System.Drawing;
using System.ComponentModel;
using System.Threading.Tasks;
using Clowd.Interop.Com;
using System.Threading;
using System.Diagnostics;
using System.Windows.Media.Imaging;

namespace Clowd.Utilities
{
    class WindowFinder3 : INotifyPropertyChanged
    {
        public int DepthReady
        {
            get => _depthReady;
            private set
            {
                _depthReady = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DepthReady)));
            }
        }

        public bool BitmapsReady
        {
            get => _bitmapsReady;
            private set
            {
                _bitmapsReady = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BitmapsReady)));
            }
        }

        private const int MaxWindowDepthToSearch = 4;
        private const int MinWinCaptureBounds = 200;

        private static readonly IVirtualDesktopManager _virtualDesktop = VirtualDesktopManager.CreateNew();
        private WINDOWINFO _winInfo = new WINDOWINFO(true);
        private Region _excludedArea = new Region(ScreenTools.VirtualScreen.Bounds.ToSystem());
        private List<CachedWindow> _cachedWindows = new List<CachedWindow>();

        //private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        private int _depthReady = -1;
        private bool _bitmapsReady = false;

        public event PropertyChangedEventHandler PropertyChanged;

        public WindowFinder3()
        {
        }

        public void CapturePart1(TimedConsoleLogger timer)
        {
            timer.Log("FinderDepth0", "Start");
            CaptureTopLevelWindows();
            timer.Log("FinderDepth0", "Complete");
            DepthReady = 0;
        }

        public void CapturePart2(TimedConsoleLogger timer)
        {
            timer.Log("FinderDepthAll", "Start");
            Parallel.ForEach(_cachedWindows, new ParallelOptions() { MaxDegreeOfParallelism = 4 }, (w) => w.PopulateChildren());
            timer.Log("FinderDepthAll", "Complete");
            DepthReady = MaxWindowDepthToSearch;
        }

        //public async Task StartCaptureAsync(int timeoutMs)
        //{
        //    var capture = Task.Run(CaptureTopLevelWindows);
        //    capture.ContinueWith((t) => Task.Run(FinishCaptureBackground));
        //    await Task.WhenAny(Task.Delay(timeoutMs), capture);
        //    if (!capture.IsCompleted)
        //        Logger.Warn($"Window enumeration wait timed out (> {timeoutMs}ms)");
        //}

        public CachedWindow HitTest(ScreenPoint point)
        {
            foreach (var wnd in _cachedWindows)
            {
                var test = wnd.HitTest(point);
                if (test != null)
                    return test;
            }

            return null;
        }

        public void CapturePart3(TimedConsoleLogger timer)
        {
            //DepthReady = 0;
            //Parallel.ForEach(_cachedWindows, new ParallelOptions() { MaxDegreeOfParallelism = 4 }, (w) => w.PopulateChildren());
            //DepthReady = MaxWindowDepthToSearch;

            // this code feels terrible, but we are blocking on the WndProc of other processes, and if one of those processes is locked or acting poorly
            // we don't want it to break Clowd.
            timer.Log("FinderHiddenBitmaps", "Start");
            var windows = _cachedWindows.Where(w => w.Depth == 0 && w.IsPartiallyCovered);
            Parallel.ForEach(windows, new ParallelOptions { MaxDegreeOfParallelism = 4 }, (c) =>
            {
                try
                {
                    Thread t = new Thread(new ThreadStart(() =>
                    {
                        try
                        {
                            c.PopulateBitmap();
                            timer.Log("FinderHiddenBitmaps", $"Captured: {c.Caption} / {c.ClassName}");
                        }
                        catch (Exception e)
                        {
                            timer.Log("FinderHiddenBitmaps", $"FAILED TO CAPTURE: {c.Caption} / {c.ClassName}");
                        }
                    }));

                    t.Start();
                    t.Join(1000);
                    if (!t.IsAlive)
                        return;
                    // the thread is taking too long, ie, stuck in a blocking operation that will never return (perhaps if the window never responds to our WM_PAINT message)
                    t.Interrupt();
                    t.Join(200);

                    if (!t.IsAlive)
                        return;

                    // the thread is _still_ alive, lets abort it.
                    t.Abort();
                }
                catch
                {
                    // who cares?
                }
            });
            BitmapsReady = true;
        }

        private void CaptureTopLevelWindows(TimedConsoleLogger timer = null)
        {
            var toplv = USER32EX.GetChildWindows(IntPtr.Zero);
            timer?.Log("FinderDepth0", $"Got list of windows (count {toplv.Count})");

            for (int i = 0; i < toplv.Count; i++)
            //foreach (var hWnd in toplv)
            {
                var hWnd = toplv[i];

                if (!USER32.GetWindowInfo(hWnd, ref _winInfo))
                    throw new Win32Exception();

                timer?.Log("FinderDepth0", $"{hWnd} window info");


                var winVisible = _winInfo.dwStyle.HasFlag(WindowStyles.WS_VISIBLE);
                var winMinimized = _winInfo.dwStyle.HasFlag(WindowStyles.WS_MINIMIZE);

                // ignore: not shown windows
                if (winMinimized || !winVisible)
                    continue;

                // ignore: 0 size windows
                if (_winInfo.rcWindow.left == _winInfo.rcWindow.right || _winInfo.rcWindow.top == _winInfo.rcWindow.bottom)
                    continue;

                timer?.Log("FinderDepth0", $"{hWnd} style stuff");


                // ignore: top level windows which are not visible to the user
                ScreenRect windowRect = ScreenRect.FromSystem(USER32EX.GetTrueWindowBounds(hWnd));
                timer?.Log("FinderDepth0", $"{hWnd} BOUNDS");
                if (!_virtualDesktop.IsWindowOnCurrentVirtualDesktop(hWnd))
                {
                    // if the window is not on the current virtual desktop we want to ignore it and it's children
                    continue;
                }


                timer?.Log("FinderDepth0", $"{hWnd} virutal desktop");


                //var winIsTool = _winInfo.dwExStyle.HasFlag(WindowStylesEx.WS_EX_TOOLWINDOW);
                //var winIsLayered = _winInfo.dwExStyle.HasFlag(WindowStylesEx.WS_EX_LAYERED);
                //var winIsTopMost = _winInfo.dwExStyle.HasFlag(WindowStylesEx.WS_EX_TOPMOST);

                //// if this is a top-level layered tool window, it's probably a transparent overlay and we don't want to capture it
                //if (winIsTool && winIsLayered && winIsTopMost)
                //    return true;

                // ignore: windows that are fully hidden by other windows higher on the z order
                var checkRect = windowRect.ToSystem();
                if (!_excludedArea.IsVisible(checkRect))
                    continue;

                _excludedArea.Exclude(checkRect);

                timer?.Log("FinderDepth0", $"region stuff");


                // ignore: window classes we know are garbage
                string className;
                if (this.IsMetroOrPhantomWindow((IntPtr)hWnd, 0, out className))
                    continue;

                ScreenRect clippedBounds = windowRect;

                // clip to workspace if top level maximized window
                var winMaximized = _winInfo.dwStyle.HasFlag(WindowStyles.WS_MAXIMIZE);
                if (winMaximized)
                    clippedBounds = ScreenTools.GetScreenContaining(clippedBounds).WorkingArea;

                // clip to screen
                clippedBounds = ScreenTools.VirtualScreen.Bounds.Intersect(clippedBounds);

                // ignore: windows that are too small, especially child windows that are small as they're usually buttons etc
                if (!this.BoundsAreLargeEnoughForCapture(clippedBounds, 0))
                    continue;

                timer?.Log("FinderDepth0", $"clipping");


                var caption = USER32EX.GetWindowCaption(hWnd);
                var cwin = new CachedWindow(hWnd, 0, className, caption, windowRect, null);

                timer?.Log("FinderDepth0", $"caption");


                // enumerate windows on top of this one in the z-order to find out of any of them intersect
                for (int z = _cachedWindows.Count - 1; z >= 0; z--)
                {
                    var whTest = _cachedWindows[z];
                    if (cwin.WindowRect.IntersectsWith(whTest.WindowRect))
                    {
                        cwin.IsPartiallyCovered = true;
                        break;
                    }
                }


                timer?.Log("FinderDepth0", $"Captured: {cwin.Caption} / {cwin.ClassName}");
                _cachedWindows.Add(cwin);
            }
        }

        public class CachedWindow
        {
            public IntPtr Handle { get; }
            public CachedWindow Parent { get; }
            public List<CachedWindow> Children { get; private set; }
            public int Depth { get; }
            public string ClassName { get; }
            public string Caption { get; }
            public ScreenRect WindowRect { get; }
            public bool IsPartiallyCovered { get; set; }

            private static ScreenUtil _screen = new ScreenUtil();

            private BitmapSource _bitmap;

            public CachedWindow(IntPtr hWnd, int depth, CachedWindow parent)
                : this(hWnd, depth, USER32EX.GetWindowClassName(hWnd), USER32EX.GetWindowCaption(hWnd), ScreenRect.FromSystem(USER32EX.GetTrueWindowBounds(hWnd)), parent)
            {

            }

            public CachedWindow(IntPtr hWnd, int depth, string className, string caption, ScreenRect windowRect, CachedWindow parent)
            {
                Handle = hWnd;
                Depth = depth;
                ClassName = className;
                Caption = caption;
                WindowRect = windowRect;
                Parent = parent;
                Children = new List<CachedWindow>();

                // clip bounds to parent window
                if (parent != null)
                {
                    WindowRect = WindowRect.Intersect(parent.WindowRect);
                    IsPartiallyCovered = parent.IsPartiallyCovered;
                }
            }

            public void PopulateChildren()
            {
                if (Depth > MaxWindowDepthToSearch)
                    return;

                Children = USER32EX.GetChildWindows(Handle)
                    .Select(k => new CachedWindow(k, Depth + 1, this))
                    .Where(c => c.WindowRect.Width >= MinWinCaptureBounds && c.WindowRect.Height >= MinWinCaptureBounds)
                    .ToList();

                foreach (var child in Children)
                    child.PopulateChildren();
            }

            public void PopulateBitmap()
            {
                if (Depth > 0)
                    return;

                _bitmap = _screen.PrintWindowWpf(Handle);
            }

            public CachedWindow GetTopLevel()
            {
                if (Parent == null)
                    return this;

                var topmost = Parent;
                while (topmost.Parent != null)
                    topmost = topmost.Parent;

                return topmost;
            }

            public CachedWindow HitTest(ScreenPoint screenPoint)
            {
                if (!WindowRect.Contains(screenPoint))
                    return null;

                foreach (var child in Children)
                {
                    var cht = child.HitTest(screenPoint);
                    if (cht != null)
                        return cht;
                }

                return this;
            }

            public BitmapSource GetBitmap()
            {
                // crop parent bitmap, if any
                if (_bitmap == null && Parent != null)
                {
                    var topmost = GetTopLevel();
                    var topBitmap = topmost.GetBitmap();
                    if (topBitmap == null)
                        return null;

                    var cropOffsetX = WindowRect.Left - topmost.WindowRect.Left;
                    var cropOffsetY = WindowRect.Top - topmost.WindowRect.Top;
                    var croppingRectangle = new Int32Rect(cropOffsetX, cropOffsetY, WindowRect.Width, WindowRect.Height);

                    _bitmap = new CroppedBitmap(topBitmap, croppingRectangle);
                }

                return _bitmap;
            }
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
                    case "WorkerW":
                    case "Progman":
                    case "Shell_TrayWnd": // taskbar tray (has lots of tiny windows)
                    //case "SHELLDLL_DefView": // windows desktop
                    //case "SysListView32": // windows desktop
                    //case "Intermediate D3D Window": // Chrome / Chromium intermediate window
                    //case "CEF-OSC-WIDGET": // NVIDIA GeForce Overlay
                    //case "DUIViewWndClassName": // inner explorer.exe window
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
        private bool BoundsAreLargeEnoughForCapture(ScreenRect windowBounds, int depth)
        {
            int minSize = (depth == 0) ? 25 : MinWinCaptureBounds;

            if (windowBounds.Height <= minSize)
                return false;
            return windowBounds.Width > minSize;
        }
    }
}
