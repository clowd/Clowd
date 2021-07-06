using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using CsWin32;
using CsWin32.Foundation;
using CsWin32.UI.WindowsAndMessaging;
using CsWin32.UI.Shell;
using CsWin32.Graphics.Dwm;
using static CsWin32.Constants;
using static CsWin32.PInvoke;

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
                char* caption = stackalloc char[MAX_STRING_LENGTH];
                GetWindowText(Handle, caption, MAX_STRING_LENGTH);
                return new string(caption);
            }
        }

        public string ClassName
        {
            get
            {
                char* className = stackalloc char[MAX_STRING_LENGTH];
                GetClassName(Handle, className, MAX_STRING_LENGTH);
                return new string(className);
            }
        }

        public ScreenRect WindowBounds
        {
            get
            {
                GetWindowRect(Handle, out var rect);
                return ScreenRect.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
            }
            set => SetWindowPos(Handle, HWND_NOTOPMOST, value.X, value.Y, value.Width, value.Height, 
                SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
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
                        if (0 == DwmGetWindowAttribute(Handle, (uint)DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &trueRect, (uint)Marshal.SizeOf<RECT>()))
                            btrue = ScreenRect.FromLTRB(trueRect.left, trueRect.top, trueRect.right, trueRect.bottom);
                }
                catch (DllNotFoundException) { }

                // only return btrue if it is smaller, and fully contained within bnormal
                // this is an attmept to fix misbehaving windows like VS
                if (btrue.Left > bnormal.Left && btrue.Top > bnormal.Top && btrue.Right < bnormal.Right && btrue.Bottom < bnormal.Bottom)
                    return btrue;

                return bnormal;
            }
        }

        public int ZPosition
        {
            get
            {
                var hwndZero = new HWND(0);
                var z = 0;
                for (var h = Handle; !h.Equals(hwndZero); h = GetWindow(h, GET_WINDOW_CMD.GW_HWNDPREV)) z++;
                return z;
            }
        }

        public ScrollVisibility ScrollBars
        {
            get
            {
                int wndStyle = GetWindowLong(Handle, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
                bool hsVisible = (wndStyle & (int)WINDOW_STYLE.WS_HSCROLL) > 0;
                bool vsVisible = (wndStyle & (int)WINDOW_STYLE.WS_VSCROLL) > 0;

                if (hsVisible)
                    return vsVisible ? ScrollVisibility.Both : ScrollVisibility.Horizontal;
                else
                    return vsVisible ? ScrollVisibility.Vertical : ScrollVisibility.None;
            }
        }

        public bool IsTopmost
        {
            get => HasStyle(WINDOW_EX_STYLE.WS_EX_TOPMOST);
            set => SetStyle(WINDOW_EX_STYLE.WS_EX_TOPMOST, value);
        }

        public bool IsDisabled
        {
            get => HasStyle(WINDOW_STYLE.WS_DISABLED);
            set => SetStyle(WINDOW_STYLE.WS_DISABLED, value);
        }

        public bool IsMaximized => IsZoomed(Handle);

        public bool IsMinimized => IsIconic(Handle);

        public bool IsCurrentVirtualDesktop
        {
            get
            {
                _desktopManager.IsWindowOnCurrentVirtualDesktop(Handle, out var iscurrent);
                return iscurrent;
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
                WNDENUMPROC childProc = (HWND hWnd, LPARAM _) =>
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

        nint IWindow.Handle => Handle;

        private void EnsureProcessId()
        {
            // this will never change and can be cached
            if (processId == 0)
            {
                uint ppid;
                threadId = (int)GetWindowThreadProcessId(Handle, &ppid);
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
            SetForegroundWindow(Handle);
            SetActiveWindow(Handle);
            return true;
        }

        public bool Show()
        {
            return Show(WindowShowCommand.Show);
        }

        public bool Show(bool activate)
        {
            return Show(WindowShowCommand.ShowNA);
        }

        public bool Show(WindowShowCommand cmd)
        {
            return ShowWindow(Handle, (SHOW_WINDOW_CMD)(uint)cmd);
        }

        public bool Hide()
        {
            return Show(WindowShowCommand.Hide);
        }

        public bool Minimize()
        {
            // this doesn't actually "Close" it.. see Close().
            return CloseWindow(Handle);
        }

        public void Close()
        {
            // most reliable way I've found to close a window, could also try WM_DESTROY...
            SendMessage(Handle, WM_CLOSE, default, default);
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
            var exs = GetWindowLong(Handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

            if (neverActivate)
                exs |= (int)WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
            else
                exs &= ~(int)WINDOW_EX_STYLE.WS_EX_NOACTIVATE;

            SetWindowLong(Handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, exs);
        }

        public void DwmSetTransitionsDisabled(bool transitionsDisabled)
        {
            int disabled = transitionsDisabled ? 1 : 0;
            DwmSetWindowAttribute(Handle, (uint)DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED, &disabled, sizeof(int));
        }

        public override string ToString()
        {
            return $"Window {((nint)Handle).ToString("X8")} {{'{Caption}/{ClassName}', {WindowBounds}}}";
        }

        private bool HasStyle(WINDOW_STYLE style) => (GetWindowLong(Handle, WINDOW_LONG_PTR_INDEX.GWL_STYLE) & (int)style) > 0;
        private bool HasStyle(WINDOW_EX_STYLE style) => (GetWindowLong(Handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE) & (int)style) > 0;
        private void SetStyle(WINDOW_STYLE style, bool set) => SetWindowLong(Handle, WINDOW_LONG_PTR_INDEX.GWL_STYLE, GetWindowLong(Handle, WINDOW_LONG_PTR_INDEX.GWL_STYLE) & ~((int)style) | (set ? ((int)style) : 0));
        private void SetStyle(WINDOW_EX_STYLE style, bool set) => SetWindowLong(Handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, GetWindowLong(Handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE) & ~((int)style) | (set ? ((int)style) : 0));

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
