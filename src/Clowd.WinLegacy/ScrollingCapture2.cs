//using System;
//using System.Collections.Concurrent;
//using System.Collections.Generic;
//using System.Diagnostics;
//using System.Drawing;
//using System.IO;
//using System.Linq;
//using System.Text;
//using System.Threading;
//using System.Threading.Tasks;
//using System.Windows.Forms;
//using Clowd.Interop;
//using Clowd.Interop.Kernel32;
//using Clowd.Interop.Shell32;
//using Newtonsoft.Json.Linq;
//using ScreenVersusWpf;

namespace Clowd.WinLegacy
{
    //namespace Clowd.Utilities
    //{
    //    class ScrollingCapture2
    //    {
    //        private readonly IntPtr _window;
    //        private readonly ScreenPoint _point;
    //        private readonly Process _process;

    //        public ScrollingCapture2(IntPtr window, ScreenPoint point)
    //        {
    //            this._window = window;
    //            this._point = point;

    //            USER32.GetWindowThreadProcessId(window, out var processId);
    //            this._process = Process.GetProcessById((int)processId);
    //        }

    //        public static void Capture(IntPtr window, ScreenPoint point)
    //        {
    //            var sc = new ScrollingCapture2(window, point);
    //            var fn = sc.GetSpecialCaptureFn();
    //            var bitmap = fn();
    //        }

    //        public static bool CanCapture(IntPtr window, ScreenPoint point)
    //        {
    //            var sc = new ScrollingCapture2(window, point);
    //            var fn = sc.GetSpecialCaptureFn();
    //            return fn != null;
    //        }

    //        private Func<Bitmap> GetSpecialCaptureFn()
    //        {
    //            if (GetChromeState() != CurrentChromeDevToolsState.NotApplicable)
    //                return CaptureChrome;

    //            return null;
    //        }

    //        class simple_window
    //        {
    //            public string Caption => USER32EX.GetWindowCaption(Handle);
    //            public string ClassName => USER32EX.GetWindowClassName(Handle);
    //            public Rectangle Bounds => USER32EX.GetTrueWindowBounds(Handle);
    //            public simple_window Parent
    //            {
    //                get
    //                {
    //                    var parent = USER32.GetParent(Handle);
    //                    if (parent == IntPtr.Zero)
    //                        return null;

    //                    return new simple_window(parent);
    //                }
    //            }
    //            public IntPtr Handle { get; private set; }
    //            public simple_window[] Children => USER32EX.GetChildWindows(Handle).Select(s => new simple_window(s)).ToArray();
    //            public uint ProcessId
    //            {
    //                get
    //                {
    //                    USER32.GetWindowThreadProcessId(Handle, out var processId);
    //                    return processId;
    //                }
    //            }

    //            public simple_window(IntPtr myself)
    //            {
    //                Handle = myself;
    //            }

    //            public override string ToString()
    //            {
    //                return $"Window pid.{ProcessId} {{'{Caption}/{ClassName}', {Bounds}}}";
    //            }

    //            public override bool Equals(object obj)
    //            {
    //                if (obj is simple_window wnd)
    //                    return wnd.Handle == Handle;

    //                return false;
    //            }

    //            public override int GetHashCode()
    //            {
    //                return Handle.GetHashCode();
    //            }
    //        }

    //        public enum CurrentChromeDevToolsState
    //        {
    //            NotApplicable = 0,
    //            Closed = 1 << 0,
    //            OpenDocked = 1 << 1,
    //            OpenPopup = 1 << 2,
    //            OpenPopupDeviceOn = 1 << 3,
    //        }

    //        private CurrentChromeDevToolsState GetChromeState()
    //        {
    //            if (_process.ProcessName != "chrome" && _process.ProcessName != "msedge")
    //                return CurrentChromeDevToolsState.NotApplicable;

    //            var rootWnd = new simple_window(USER32.GetAncestor(_window, GetAncestorFlags.GA_ROOT));
    //            var rootBounds = rootWnd.Bounds;
    //            var contentWindows = rootWnd.Children.Where(c => c.Bounds.Y - 40 > rootBounds.Y).ToArray();

    //            if (contentWindows.Length == 1)
    //                return CurrentChromeDevToolsState.Closed;

    //            if (contentWindows.Length == 2)
    //            {
    //                var clickedOn = new simple_window(_window);
    //                var other = contentWindows.Except(new[] { clickedOn }).Single();

    //                var contentBounds = clickedOn.Bounds;
    //                var devToolsBounds = other.Bounds;

    //                if (contentBounds == devToolsBounds)
    //                    return CurrentChromeDevToolsState.OpenPopup;

    //                if (devToolsBounds.Height < contentBounds.Height || devToolsBounds.Width < contentBounds.Width)
    //                {
    //                    // we got them wrong way around.. 
    //                    contentBounds = other.Bounds;
    //                    devToolsBounds = clickedOn.Bounds;
    //                }

    //                if (devToolsBounds.Height > contentBounds.Height || devToolsBounds.Width > contentBounds.Width)
    //                {
    //                    double c_cx = contentBounds.Left + (contentBounds.Width / 2);
    //                    double d_cx = devToolsBounds.Left + (devToolsBounds.Width / 2);

    //                    //double c_cy = contentBounds.Top + (contentBounds.Height / 2);
    //                    //double d_cy = devToolsBounds.Top + (devToolsBounds.Height / 2);

    //                    if (Math.Abs(d_cx - c_cx) < 4/* && Math.Abs(d_cy - c_cy) < 4*/)
    //                        return CurrentChromeDevToolsState.OpenDocked;
    //                    //return CurrentChromeDevToolsState.OpenPopupDeviceOn;

    //                    // device toggle?
    //                    return CurrentChromeDevToolsState.OpenDocked;
    //                }
    //            }

    //            return CurrentChromeDevToolsState.NotApplicable;
    //        }

    //        private Bitmap CaptureChrome()
    //        {
    //            var downloads = GetChromeDownloadPaths().Distinct().ToArray();
    //            List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
    //            SynchronizedCollection<string> newfiles = new SynchronizedCollection<string>();
    //            foreach (var d in downloads)
    //            {
    //                var w = new FileSystemWatcher(d);
    //                w.Created += (s, e) =>
    //                {
    //                    newfiles.Add(e.FullPath);
    //                };
    //                w.Renamed += (s, e) =>
    //                {
    //                    if (newfiles.Contains(e.OldFullPath))
    //                    {
    //                        newfiles.Remove(e.OldFullPath);
    //                        newfiles.Add(e.FullPath);
    //                    }
    //                };
    //                w.EnableRaisingEvents = true;
    //                watchers.Add(w);
    //            }
    //            IntPtr devtools = IntPtr.Zero;
    //            bool wndopened = false;
    //            try
    //            {
    //                var state = GetChromeState();
    //                var captured = CaptureChrome3_CommandChrome(state, ref wndopened, ref devtools);
    //                if (devtools != IntPtr.Zero)
    //                    USER32.CloseWindow(devtools);
    //                if (captured)
    //                {
    //                    for (int z = 0; z < 50; z++)
    //                    {
    //                        Thread.Sleep(200); // 10 seconds
    //                        var ss = newfiles.FirstOrDefault(c => Path.GetExtension(c).Contains("png"));
    //                        if (ss != null)
    //                        {
    //                            return (Bitmap)Bitmap.FromFile(ss);
    //                        }
    //                    }
    //                }

    //            }
    //            finally
    //            {
    //                watchers.ForEach((w) => w.Dispose());
    //                if (devtools != IntPtr.Zero && wndopened)
    //                    USER32.SendMessage(devtools, (int)WindowMessage.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    //            }

    //            return null;
    //        }

    //        private bool CaptureChrome3_CommandChrome(CurrentChromeDevToolsState state, ref bool openedNew, ref IntPtr devtools)
    //        {
    //            var rootWnd = new simple_window(USER32.GetAncestor(_window, GetAncestorFlags.GA_ROOT));

    //            Console.WriteLine(state.ToString());
    //            void SendF12(IntPtr hWnd)
    //            {
    //                USER32.SetForegroundWindow(hWnd);
    //                SendKeys.SendWait("{F12}");
    //                Thread.Sleep(200);
    //            }

    //            void SendCommand(IntPtr hWnd, string cmd)
    //            {
    //                Thread.Sleep(800);
    //                USER32.SetForegroundWindow(hWnd);
    //                SendKeys.SendWait("^+p");
    //                Thread.Sleep(100);
    //                USER32.SetForegroundWindow(hWnd);
    //                SendKeys.SendWait(cmd);
    //                Thread.Sleep(100);
    //                USER32.SetForegroundWindow(hWnd);
    //                SendKeys.SendWait("{ENTER}");
    //            }

    //            CurrentChromeDevToolsState WaitStateChange(CurrentChromeDevToolsState oldState, bool canToggle, ref IntPtr devWnd, int timeoutMs = 3000)
    //            {
    //                for (int i = 0; i < timeoutMs / 100; i++)
    //                {
    //                    Thread.Sleep(100);

    //                    var foreground = USER32.GetForegroundWindow();
    //                    var title = USER32EX.GetWindowCaption(foreground);

    //                    if (devWnd == IntPtr.Zero && title.StartsWith("DevTools"))
    //                        devWnd = foreground;

    //                    var newState = GetChromeState();
    //                    if (newState != state)
    //                    {
    //                        if (devWnd != IntPtr.Zero && canToggle && newState == CurrentChromeDevToolsState.OpenPopupDeviceOn)
    //                        {
    //                            SendCommand(devWnd, "tog dev");
    //                            return WaitStateChange(newState, false, ref devWnd);
    //                        }
    //                        if (devWnd == IntPtr.Zero && newState == CurrentChromeDevToolsState.OpenPopup || newState == CurrentChromeDevToolsState.OpenPopupDeviceOn)
    //                        {
    //                            continue;
    //                        }
    //                        return newState;
    //                    }
    //                }

    //                return CurrentChromeDevToolsState.NotApplicable;
    //            }

    //            if (state == CurrentChromeDevToolsState.NotApplicable)
    //            {
    //                return false;
    //            }

    //            if (state == CurrentChromeDevToolsState.OpenDocked)
    //            {
    //                if (openedNew)
    //                {
    //                    SendCommand(rootWnd.Handle, "undock");
    //                    var newState = WaitStateChange(state, openedNew, ref devtools);
    //                    return CaptureChrome3_CommandChrome(newState, ref openedNew, ref devtools);
    //                }
    //                else
    //                {
    //                    SendCommand(rootWnd.Handle, "full scr");
    //                    return true;
    //                }
    //            }

    //            if (state == CurrentChromeDevToolsState.OpenPopup || state == CurrentChromeDevToolsState.OpenPopupDeviceOn)
    //            {
    //                var foreground = USER32.GetForegroundWindow();
    //                var title = USER32EX.GetWindowCaption(foreground);

    //                if (devtools != IntPtr.Zero && foreground != devtools)
    //                {
    //                    USER32.SetForegroundWindow(devtools);
    //                }
    //                else if (!title.StartsWith("DevTools"))
    //                {
    //                    SendF12(rootWnd.Handle);
    //                }

    //                if (devtools == IntPtr.Zero)
    //                    devtools = USER32.GetForegroundWindow();

    //                SendCommand(devtools, "full scr");
    //                return true;
    //            }

    //            if (state == CurrentChromeDevToolsState.Closed)
    //            {
    //                SendF12(rootWnd.Handle);
    //                openedNew = true;
    //                var newState = WaitStateChange(state, openedNew, ref devtools);
    //                return CaptureChrome3_CommandChrome(newState, ref openedNew, ref devtools);
    //            }

    //            return false;
    //        }

    //        private IEnumerable<string> GetChromeDownloadPaths()
    //        {
    //            SHELL32.SHGetKnownFolderPath(KnownFolders.Downloads, 0, IntPtr.Zero, out var downloads);
    //            yield return downloads;

    //            var folder = _process.ProcessName == "chrome"
    //                ? "Google\\Chrome\\User Data"
    //                : "Microsoft\\Edge\\User Data";

    //            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    //            var chromedatafolder = Path.Combine(appdata, folder);
    //            if (!Directory.Exists(chromedatafolder))
    //                yield break;

    //            List<string> preferencePaths = new List<string>();

    //            foreach (var file in Directory.EnumerateDirectories(chromedatafolder))
    //            {
    //                var name = Path.GetFileName(file);
    //                if (name.Contains("Profile") || name.Equals("Default"))
    //                {
    //                    var pref = Path.Combine(file, "Preferences");
    //                    if (File.Exists(pref))
    //                        preferencePaths.Add(pref);
    //                }
    //            }

    //            if (preferencePaths.Count == 0)
    //                yield break;

    //            foreach (var p in preferencePaths)
    //            {
    //                var j = JObject.Parse(File.ReadAllText(p));
    //                var download = j["download"] as JObject;
    //                if (download != null)
    //                {
    //                    var default_dir = download.Value<string>("default_directory");
    //                    if (default_dir != null && default_dir != downloads)
    //                        yield return default_dir;
    //                }
    //            }
    //        }
    //    }
    //}
}