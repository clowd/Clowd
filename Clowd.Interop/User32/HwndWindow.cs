using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Interop
{
    public class HwndWindow
    {
        public string Caption => USER32EX.GetWindowCaption(Handle);

        public string ClassName => USER32EX.GetWindowClassName(Handle);

        public System.Drawing.Rectangle Bounds
        {
            get
            {
                USER32.GetWindowRect(Handle, out var rect);
                return rect;
            }
            set => USER32.SetWindowPos(Handle, SWP_HWND.HWND_NOTOPMOST, value.X, value.Y, value.Width, value.Height, SWP.NOOWNERZORDER | SWP.NOACTIVATE);
        }

        public System.Drawing.Rectangle TrueBounds => USER32EX.GetTrueWindowBounds(Handle);

        public bool IsTopmost
        {
            get => HasWindowLong(WindowLongIndex.GWL_EXSTYLE, (int)WindowStylesEx.WS_EX_TOPMOST);
            set => SetWindowLong(WindowLongIndex.GWL_EXSTYLE, (int)WindowStylesEx.WS_EX_TOPMOST, value);
        }

        public bool IsDisabled
        {
            get => HasWindowLong(WindowLongIndex.GWL_STYLE, (int)WindowStyles.WS_DISABLED);
            set => SetWindowLong(WindowLongIndex.GWL_STYLE, (int)WindowStyles.WS_DISABLED, true);
        }

        public bool IsMaximized
        {
            get => USER32.IsZoomed(Handle);
        }

        public bool IsMinimized
        {
            get => USER32.IsIconic(Handle);
        }

        public bool CanHookWndProc => Process.GetCurrentProcess().Id == ProcessId;

        public HwndWindow Owner
        {
            get => FromHandle(USER32.GetWindowLongPtr(Handle, WindowLongIndex.GWL_HWNDPARENT));
            set => USER32.SetWindowLong(Handle, WindowLongIndex.GWL_HWNDPARENT, value.Handle);
        }

        public HwndWindow Parent
        {
            get => FromHandle(USER32.GetParent(Handle));
            set => USER32.SetParent(Handle, value.Handle);
        }

        public IntPtr Handle { get; private set; }

        public HwndWindow[] Children => USER32EX.GetChildWindows(Handle).Select(s => new HwndWindow(s)).ToArray();

        public uint ProcessId
        {
            get
            {
                USER32.GetWindowThreadProcessId(Handle, out var processId);
                return processId;
            }
        }

        public uint ThreadId => USER32.GetWindowThreadProcessId(Handle, out var processId);

        private HwndWindow(IntPtr myself)
        {
            Handle = myself;
        }

        public static HwndWindow FromHandle(IntPtr handle)
        {
            if (handle == null || handle == IntPtr.Zero)
                return null;

            return new HwndWindow(handle);
        }

        public void Activate()
        {
            USER32.SetForegroundWindow(Handle);
        }

        public void Show(ShowWindowCmd cmd)
        {
            USER32.ShowWindow(Handle, cmd);
        }

        public void Minimize()
        {
            // this doesn't actually "Close" it.. see Close().
            USER32.CloseWindow(Handle);
        }

        public void Close()
        {
            // most reliable way I've found to close a window
            USER32.SendMessage(Handle, (int)WindowMessage.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        public HwndWindowHook AddWndProcHook(HwndWindowHookProc proc)
        {
            var threadId = USER32.GetWindowThreadProcessId(Handle, out var processId);

            if (Process.GetCurrentProcess().Id != processId)
                throw new InvalidOperationException("You may only hook windows within the current process. See CanHookWndProc");

            IntPtr hookPtr = IntPtr.Zero;

            USER32.CallHookProc hook = new USER32.CallHookProc((nCode, wParam, lParam) =>
            {
                // for negative ncode we must only return CallNextHookEx and do no further processing
                if (nCode < 0)
                    return USER32.CallNextHookEx(hookPtr, nCode, wParam, lParam);

                var cwp = Marshal.PtrToStructure<CWPSTRUCT>(lParam);

                if (cwp.hwnd == Handle)
                    proc(cwp.hwnd, cwp.message, cwp.wparam, cwp.lparam);

                return USER32.CallNextHookEx(hookPtr, nCode, wParam, lParam);
            });

            hookPtr = USER32.SetWindowsHookEx(HookType.WH_CALLWNDPROC, hook, IntPtr.Zero, threadId);

            if (hookPtr == IntPtr.Zero)
                throw new Win32Exception();

            return new HwndWindowHook(hookPtr);
        }

        public static implicit operator IntPtr(HwndWindow w) => w.Handle;

        public override string ToString()
        {
            return $"Window {Handle.ToString("X8")} {{'{Caption}/{ClassName}', {Bounds}}}";

        }

        public override bool Equals(object obj)
        {
            if (obj is HwndWindow wnd)
                return wnd.Handle == Handle;

            return false;
        }

        public override int GetHashCode()
        {
            return Handle.GetHashCode();
        }

        private bool HasWindowLong(WindowLongIndex index, int value)
        {
            int wlong = USER32.GetWindowLong(Handle, index);
            return (wlong & value) == value;
        }

        private void SetWindowLong(WindowLongIndex index, int value, bool set)
        {
            USER32.SetWindowLong(Handle, index, USER32.GetWindowLong(Handle, index) & ~value | (set ? value : 0));
        }

        public class HwndWindowHook : IDisposable
        {
            private readonly IntPtr _hookPtr;
            private bool _disposed;

            public HwndWindowHook(IntPtr hookPtr)
            {
                _disposed = false;
                _hookPtr = hookPtr;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                if (!USER32.UnhookWindowsHookEx(_hookPtr))
                    throw new Win32Exception();

                _disposed = true;
            }
        }

        public delegate void HwndWindowHookProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
