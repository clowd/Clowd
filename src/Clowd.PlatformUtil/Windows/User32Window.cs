using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Linq;
using System.Runtime.InteropServices;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;
using static Vanara.PInvoke.DwmApi;
using static Vanara.PInvoke.Shell32;

namespace Clowd.PlatformUtil.Windows
{
    public delegate int WindowProcedure(nint hWnd, WindowMessage msg, nuint wParam, nint lParam, out bool handled);

    public unsafe partial record User32Window : IWindow
    {
        private const int MAX_STRING_LENGTH = 1024;

        public string Caption
        {
            get
            {
                var sb = new StringBuilder(MAX_STRING_LENGTH);
                GetWindowText(Handle, sb, MAX_STRING_LENGTH);
                return sb.ToString().Trim();
            }
        }

        public string ClassName
        {
            get
            {
                var sb = new StringBuilder(MAX_STRING_LENGTH);
                GetClassName(Handle, sb, MAX_STRING_LENGTH);
                return sb.ToString().Trim();
            }
        }

        public ScreenRect WindowBounds
        {
            get
            {
                GetWindowRect(Handle, out var rect);
                return ScreenRect.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
            }
            set => SetWindowPos(Handle, HWND.HWND_NOTOPMOST, value.X, value.Y, value.Width, value.Height,
                SetWindowPosFlags.SWP_NOOWNERZORDER | SetWindowPosFlags.SWP_NOACTIVATE | SetWindowPosFlags.SWP_NOZORDER);
        }

        public ScreenRect DwmRenderBounds
        {
            get
            {
                if (IsMaximized)
                {
                    // windows do not report the correct size if maximized.
                    var hmon = User32Screen.FromWindow(this);
                    return hmon.WorkingArea;
                }

                ScreenRect bnormal = WindowBounds, btrue = bnormal;
                try
                {
                    RECT trueRect;
                    if (0 == DwmIsCompositionEnabled(out var dwmIsEnabled) && dwmIsEnabled)
                        if (0 == DwmGetWindowAttribute(Handle, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, (IntPtr)(&trueRect), Marshal.SizeOf<RECT>()))
                            btrue = ScreenRect.FromLTRB(trueRect.left, trueRect.top, trueRect.right, trueRect.bottom);
                }
                catch (DllNotFoundException) { }

                // only return btrue if it is smaller, and fully contained within bnormal
                // this is an attempt to fix misbehaving windows like VS
                if (btrue.Left >= bnormal.Left && btrue.Top >= bnormal.Top && btrue.Right <= bnormal.Right && btrue.Bottom <= bnormal.Bottom)
                    return btrue;

                return bnormal;
            }
        }

        public int ZPosition
        {
            get
            {
                var hwndZero = new HWND(IntPtr.Zero);
                var z = 0;
                for (var h = Handle; !h.Equals(hwndZero); h = GetWindow(h, GetWindowCmd.GW_HWNDPREV)) z++;
                return z;
            }
        }

        public ScrollVisibility ScrollBars
        {
            get
            {
                int wndStyle = GetWindowLong(Handle, WindowLongFlags.GWL_STYLE);
                bool hsVisible = (wndStyle & (int)WindowStyles.WS_HSCROLL) > 0;
                bool vsVisible = (wndStyle & (int)WindowStyles.WS_VSCROLL) > 0;

                if (hsVisible)
                    return vsVisible ? ScrollVisibility.Both : ScrollVisibility.Horizontal;
                else
                    return vsVisible ? ScrollVisibility.Vertical : ScrollVisibility.None;
            }
        }

        public bool IsTopmost
        {
            get => HasStyle(WindowStylesEx.WS_EX_TOPMOST);
            set => SetStyle(WindowStylesEx.WS_EX_TOPMOST, value);
        }

        public bool IsDisabled
        {
            get => HasStyle(WindowStyles.WS_DISABLED);
            set => SetStyle(WindowStyles.WS_DISABLED, value);
        }

        public bool IsMaximized => IsZoomed(Handle);

        public bool IsMinimized => IsIconic(Handle);

        public bool IsCurrentVirtualDesktop
        {
            get
            {
                return _desktopManager.IsWindowOnCurrentVirtualDesktop(Handle);
            }
        }

        public Guid? VirtualDesktopId
        {
            get
            {
                try
                {
                    return _desktopManager.GetWindowDesktopId(Handle);
                }
                catch
                {
                    return null;
                }
            }
        }

        public bool IsCurrentProcess => _currentProcessId == ProcessId;

        //public HwndWindow Owner
        //{
        //    //get => GetWindowLong FromHandle(GetWindowLongPtr(Handle, WindowLongIndex.GWL_HWNDPARENT));
        //    //set => SetWindowLong(Handle, WindowLongIndex.GWL_HWNDPARENT, value.Handle);
        //}

        public User32Window Parent
        {
            get => new User32Window(GetParent(Handle));
            set => SetParent(Handle, value.Handle);
        }

        internal HWND Handle { get; init; }

        public IEnumerable<User32Window> Children
        {
            get
            {
                List<HWND> children = new List<HWND>();
                EnumWindowsProc childProc = (HWND hWnd, IntPtr _) =>
                {
                    children.Add(hWnd);
                    return true;
                };
                EnumChildWindows(Handle, childProc, default);
                return children.Select(c => new User32Window(c));
            }
        }

        public int ProcessId
        {
            get
            {
                EnsureProcessId();
                return processId;
            }
        }

        public int ThreadId
        {
            get
            {
                EnsureProcessId();
                return threadId;
            }
        }

        IWindow IWindow.Parent => Parent;

        IEnumerable<IWindow> IWindow.Children => Children;

        nint IWindow.Handle => (IntPtr)Handle;

        private void EnsureProcessId()
        {
            // this will never change and can be cached
            if (processId == 0)
            {
                threadId = (int)GetWindowThreadProcessId(Handle, out var ppid);
                processId = (int)ppid;
            }
        }

        private int processId;
        private int threadId;

        private readonly static IVirtualDesktopManager _desktopManager;
        private readonly static int _currentProcessId;

        static User32Window()
        {
            _currentProcessId = Process.GetCurrentProcess().Id;
            var clsid = typeof(VirtualDesktopManager).GUID;
            _desktopManager = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid));
        }

        internal User32Window(HWND handle)
        {
            Handle = handle;
        }

        public static User32Window FromHandle(IntPtr handle)
        {
            if (handle == null || handle == IntPtr.Zero)
                return null;

            return new User32Window(new HWND(handle));
        }

        public bool Activate()
        {
            // show window if minimized
            if (IsIconic(Handle))
                ShowWindow(Handle, ShowWindowCommand.SW_RESTORE);

            SetForegroundWindow(Handle);
            SetActiveWindow(Handle);
            return true;
        }

        public bool Show()
        {
            return Show(ShowWindowCommand.SW_SHOW);
        }

        public bool Show(bool activate)
        {
            return Show(ShowWindowCommand.SW_SHOWNA);
        }

        public bool Show(ShowWindowCommand cmd)
        {
            return ShowWindow(Handle, (ShowWindowCommand)(uint)cmd);
        }

        public bool Hide()
        {
            SetWindowPos(Handle, HWND.HWND_BOTTOM, 0, 0, 0, 0,
                SetWindowPosFlags.SWP_HIDEWINDOW | SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOZORDER);
            return Show(ShowWindowCommand.SW_HIDE);
        }

        public bool Minimize()
        {
            // this doesn't actually "Close" it.. see Close().
            return CloseWindow(Handle);
        }

        public void Close()
        {
            // most reliable way I've found to close a window, could also try WM_DESTROY...
            SendMessage(Handle, (uint)WindowMessage.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        public void KillProcess()
        {
            try
            {
                Process.GetProcessById(ProcessId).Kill();
            }
            catch { }
        }

        public void SetNeverActivateStyle(bool neverActivate)
        {
            var exs = GetWindowLong(Handle, WindowLongFlags.GWL_EXSTYLE);

            if (neverActivate)
                exs |= (int)WindowStylesEx.WS_EX_NOACTIVATE;
            else
                exs &= ~(int)WindowStylesEx.WS_EX_NOACTIVATE;

            SetWindowLong(Handle, WindowLongFlags.GWL_EXSTYLE, exs);
        }

        public void DwmSetTransitionsDisabled(bool transitionsDisabled)
        {
            int disabled = transitionsDisabled ? 1 : 0;
            DwmSetWindowAttribute(Handle, DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED, (IntPtr)(&disabled), sizeof(int));
        }

        public void MoveToDesktop(Guid desktop)
        {
            _desktopManager.MoveWindowToDesktop(Handle, desktop);
        }
        
        public override string ToString()
        {
            return $"Window {((nint)Handle).ToString("X8")} {{'{Caption}/{ClassName}', {WindowBounds}}}";
        }

        private bool HasStyle(WindowStyles style)
            => (GetWindowLong(Handle, WindowLongFlags.GWL_STYLE) & (int)style) > 0;

        private bool HasStyle(WindowStylesEx style)
            => (GetWindowLong(Handle, WindowLongFlags.GWL_EXSTYLE) & (int)style) > 0;

        private void SetStyle(WindowStyles style, bool set)
            => SetWindowLong(Handle, WindowLongFlags.GWL_STYLE, GetWindowLong(Handle, WindowLongFlags.GWL_STYLE) & ~((int)style) | (set ? ((int)style) : 0));

        private void SetStyle(WindowStylesEx style, bool set)
            => SetWindowLong(Handle, WindowLongFlags.GWL_EXSTYLE,
                GetWindowLong(Handle, WindowLongFlags.GWL_EXSTYLE) & ~((int)style) | (set ? ((int)style) : 0));

        public IScreen GetCurrentScreen()
        {
            return User32Screen.FromRect(WindowBounds);
        }

        public bool Equals(IWindow other)
        {
            return Equals(other as User32Window);
        }

        public void SetPosition(ScreenRect newPosition)
        {
            WindowBounds = newPosition;
        }

        public void SetEnabled(bool enabled)
        {
            IsDisabled = !enabled;
        }
    }
}
