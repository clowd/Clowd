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
using System.Threading;
using System.Diagnostics;

namespace Clowd.Utilities
{
    public class WindowFinder2 : IDisposable, INotifyPropertyChanged
    {
        public bool BitmapsReady
        {
            get => _bitmapsReady;
            set
            {
                _bitmapsReady = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BitmapsReady)));
            }
        }

        private bool _bitmapsReady = false;

        private const int MaxWindowDepthToSearch = 4;
        private const int MinWinCaptureBounds = 200;

        // this list will be ordered by topmost window handles first, so it can be enumerated easily to find the topmost window at a specific point
        // sub-handles (child windows) will come before their parents, but the Depth property can be used to find the top level window
        private readonly List<CachedWindow> _cachedWindows = new List<CachedWindow>();
        private readonly Stack<CachedWindow> _parentStack = new Stack<CachedWindow>();
        private readonly IVirtualDesktopManager _virtualDesktop = VirtualDesktopManager.CreateNew();
        private WINDOWINFO _winInfo = new WINDOWINFO(true);
        private Region _excludedArea = new Region(ScreenTools.VirtualScreen.Bounds.ToSystem());

        public event PropertyChangedEventHandler PropertyChanged;


        ~WindowFinder2()
        {
            this.Dispose();
        }
        private WindowFinder2()
        {
        }

        public static WindowFinder2 NewCapture()
        {
            var ths = new WindowFinder2();

            ths._cachedWindows.Clear();
            USER32.EnumWindowProc enumWindowsProc = new USER32.EnumWindowProc(ths.EvalWindow);
            USER32.EnumWindows(enumWindowsProc, IntPtr.Zero);

            return ths;
        }

        public static Task<WindowFinder2> NewCaptureAsync()
        {
            return Task.Run(NewCapture);
        }

        public void PopulateWindowBitmaps()
        {
            // this code feels terrible, but we are blocking on the WndProc of other processes, and if one of those processes is locked or acting poorly
            // we don't want it to break Clowd.
            var windows = _cachedWindows.Where(w => w.Depth == 0 && w.IsPartiallyCovered);
            Parallel.ForEach(windows, new ParallelOptions { MaxDegreeOfParallelism = 4 }, (c) =>
            {
                try
                {
                    Thread t = new Thread(new ThreadStart(() => { c.CaptureWindowBitmap(); }));
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

        public Task PopulateWindowBitmapsAsync()
        {
            return Task.Run(PopulateWindowBitmaps);
        }

        public CachedWindow GetWindowThatContainsPoint(ScreenPoint point)
        {
            foreach (var window in _cachedWindows)
                if (window.ImageBoundsRect.Contains(point))
                    return window;

            return null;
        }

        public CachedWindow GetTopLevelWindow(CachedWindow child)
        {
            if (child?.Parent != null) 
                return GetTopLevelWindow(child.Parent);
            return child;
        }

        public void Dispose()
        {
            _cachedWindows.ForEach(c => c.Dispose());
        }

        private bool EvalWindow(IntPtr hWnd, IntPtr depthPtr)
        {
            var depthInt = depthPtr.ToInt32();
            var parent = depthInt > 0 ? _parentStack.Peek() : null;

            if (!USER32.GetWindowInfo(hWnd, ref _winInfo))
                throw new Win32Exception();

            var winVisible = _winInfo.dwStyle.HasFlag(WindowStyles.WS_VISIBLE);
            var winMinimized = _winInfo.dwStyle.HasFlag(WindowStyles.WS_MINIMIZE);

            // ignore: not shown windows
            if (winMinimized || !winVisible)
                return true;

            // ignore: 0 size windows
            if (_winInfo.rcWindow.left == _winInfo.rcWindow.right || _winInfo.rcWindow.top == _winInfo.rcWindow.bottom)
                return true;

            // ignore: full screen windows created by this process
            if (hWnd == CaptureWindow2.Current?.Handle)
                return true;

            // ignore: top level windows which are not visible to the user
            ScreenRect windowRect = ScreenRect.FromSystem(USER32EX.GetTrueWindowBounds(hWnd));
            if (depthInt == 0)
            {
                if (!_virtualDesktop.IsWindowOnCurrentVirtualDesktop(hWnd))
                {
                    // if the window is not on the current virtual desktop we want to ignore it and it's children
                    return true;
                }

                //var winIsTool = _winInfo.dwExStyle.HasFlag(WindowStylesEx.WS_EX_TOOLWINDOW);
                //var winIsLayered = _winInfo.dwExStyle.HasFlag(WindowStylesEx.WS_EX_LAYERED);
                //var winIsTopMost = _winInfo.dwExStyle.HasFlag(WindowStylesEx.WS_EX_TOPMOST);

                //// if this is a top-level layered tool window, it's probably a transparent overlay and we don't want to capture it
                //if (winIsTool && winIsLayered && winIsTopMost)
                //    return true;

                // ignore: windows that are fully hidden by other windows higher on the z order
                var checkRect = windowRect.ToSystem();
                if (!_excludedArea.IsVisible(checkRect))
                    return true;

                _excludedArea.Exclude(checkRect);
            }

            // ignore: window classes we know are garbage
            string className;
            if (this.IsMetroOrPhantomWindow((IntPtr)hWnd, depthInt, out className))
                return true;

            ScreenRect clippedBounds = windowRect;

            // clip to workspace if top level maximized window
            var winMaximized = _winInfo.dwStyle.HasFlag(WindowStyles.WS_MAXIMIZE);
            if (depthInt == 0 && winMaximized)
                clippedBounds = ScreenTools.GetScreenContaining(clippedBounds).WorkingArea;

            // clip to parent window
            if (parent != null)
                clippedBounds = clippedBounds.Intersect(parent.ImageBoundsRect);

            // clip to screen
            clippedBounds = ScreenTools.VirtualScreen.Bounds.Intersect(clippedBounds);

            // ignore: windows that are too small, especially child windows that are small as they're usually buttons etc
            if (!this.BoundsAreLargeEnoughForCapture(clippedBounds, depthInt))
                return true;

            var caption = USER32EX.GetWindowCaption(hWnd);
            var cwin = new CachedWindow(hWnd, depthInt, className, caption, windowRect, clippedBounds, parent);

            // enumerate windows on top of this one in the z-order to find out of any of them intersect
            for (int z = _cachedWindows.Count - 1; z >= 0; z--)
            {
                var whTest = _cachedWindows[z];
                if (cwin.WindowRect.IntersectsWith(whTest.WindowRect))
                {
                    if (GetTopLevelWindow(whTest) != GetTopLevelWindow(cwin))
                    {
                        cwin.IsPartiallyCovered = true;
                        break;
                    }
                }
            }

            // add child windows before we add the parent one (so topmost windows come first in the list)
            if (depthInt < MaxWindowDepthToSearch)
            {
                this._parentStack.Push(cwin);
                USER32.EnumWindowProc enumWindowsProc = new USER32.EnumWindowProc(this.EvalWindow);
                USER32.EnumChildWindows(hWnd, enumWindowsProc, IntPtr.Add(depthPtr, 1));
                this._parentStack.Pop();
            }

            this._cachedWindows.Add(cwin);
            return true;
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

        public class CachedWindow : IDisposable
        {
            public IntPtr Handle { get; private set; }
            public ScreenRect ImageBoundsRect { get; private set; } = ScreenRect.Empty;
            public ScreenRect WindowRect { get; private set; } = ScreenRect.Empty;
            public string Caption { get; private set; }
            public string ClassName { get; private set; }
            public bool IsPartiallyCovered { get; set; }
            public int Depth { get; private set; }
            public Bitmap WindowBitmap { get; private set; }
            public System.Windows.Media.Imaging.BitmapSource WindowBitmapWpf
            {
                get
                {
                    if (_wpfBitmap == null && WindowBitmap != null)
                        _wpfBitmap = WindowBitmap.ToBitmapSource();

                    return _wpfBitmap;
                }
            }
            public CachedWindow Parent { get; private set; }

            private System.Windows.Media.Imaging.BitmapSource _wpfBitmap = null;

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

            public void CaptureWindowBitmap()
            {
                if (Depth > 0)
                    throw new InvalidOperationException("Must only call GetBitmap on a top-level window");

                if (WindowBitmap != null)
                    return;

                Bitmap initialBmp;
                ScreenRect basicBounds;
                try
                {

                    RECT normalWindowBounds;
                    if (!USER32.GetWindowRect(this.Handle, out normalWindowBounds))
                        throw new Win32Exception();

                    basicBounds = ScreenRect.FromSystem(normalWindowBounds);

                    initialBmp = new Bitmap(basicBounds.Width, basicBounds.Height);
                    try
                    {
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
                    }
                    catch (Exception e)
                    {
                        // this most often occurs if the window is elevated or kernal mode and we do not have permission to capture it
                        initialBmp.Dispose();
                        return;
                    }
                }
                catch (ThreadInterruptedException e)
                {
                    // if we're inturrupted here we should exit, as we were stuck in a blocking non-returning function call.
                    return;
                }
                catch (Win32Exception e)
                {
                    // this usually happens if the window closed while enumerating
                    return;
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
                }
                else
                {
                    var croppingRectangle = new Rectangle(xoffset, yoffset, WindowRect.Width, WindowRect.Height);
                    var newBmp = initialBmp.Crop(croppingRectangle);
                    WindowBitmap = newBmp;
                    initialBmp.Dispose();
                }
            }

            public void ShowDebug()
            {
                var info = new WINDOWINFO(true);
                USER32.GetWindowInfo(Handle, ref info);

                List<string> exStyles = new List<string>();
                foreach (var env in Enum.GetValues(typeof(WindowStylesEx)).Cast<WindowStylesEx>())
                {
                    if (env == WindowStylesEx.WS_EX_LEFT)
                        continue;
                    if (info.dwExStyle.HasFlag(env))
                        exStyles.Add(Enum.GetName(typeof(WindowStylesEx), env));
                }

                System.Windows.MessageBox.Show(
$@"Window Debug Info:
Title: '{Caption}'
Class: '{ClassName}'
Depth: '{Depth}'
Borders: 'X {info.cxWindowBorders}, Y {info.cyWindowBorders}'
ActualBounds: '{ScreenRect.FromSystem(info.rcWindow)}'
CalcBounds: '{WindowRect.ToString()}'
Style: '{info.dwStyle.ToString()}'
Ex Style: '{String.Join(" | ", exStyles)}'
Partially Covered: '{IsPartiallyCovered}'
");
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
