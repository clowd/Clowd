using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using static Vanara.PInvoke.User32;
using static Vanara.PInvoke.Kernel32;

namespace Clowd.PlatformUtil.Windows
{
    public class User32Platform : Platform
    {
        public User32Platform() : base()
        {
            if (ThreadDpiScalingContext.GetCurrentThreadScalingMode() == ThreadScalingMode.Unaware)
            {
                throw new InvalidOperationException(
                    "Can only be used from a DPI-Aware process thread. " +
                    "Please include an application manifest to set the dpi awarenes of this process.");
            }
        }

        public override IEnumerable<IScreen> AllScreens => User32Screen.AllScreens;

        public override IScreen VirtualScreen => User32Screen.VirtualScreen;

        public override IScreen PrimaryScreen => User32Screen.PrimaryScreen;

        public override IBitmap CaptureDesktop(bool drawCursor)
        {
            return GdiScreenCapture.CaptureScreen(VirtualScreen.Bounds, drawCursor);
        }

        public override IBitmap CaptureRegion(ScreenRect region, bool drawCursor)
        {
            return GdiScreenCapture.CaptureScreen(region, drawCursor);
        }

        public override IBitmap CaptureWindow(IWindow window)
        {
            return GdiScreenCapture.CaptureWindow(window);
        }

        public override IWindow GetForegroundWindow()
        {
            return GetWindowFromHandle((IntPtr)Vanara.PInvoke.User32.GetForegroundWindow());
        }

        public override ScreenPoint GetMousePosition()
        {
            if (!GetCursorPos(out var pt))
                throw new Win32Exception();
            return new ScreenPoint(pt.X, pt.Y);
        }

        public override IScreen GetScreenFromPoint(ScreenPoint pt)
        {
            return User32Screen.FromPoint(pt);
        }

        public override IScreen GetScreenFromRect(ScreenRect rect)
        {
            return User32Screen.FromRect(rect);
        }

        public override TimeSpan GetSystemIdleTime()
        {
            var info = new LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>();
            if (!GetLastInputInfo(ref info))
            {
                var err = GetLastError();
                err.ThrowIfFailed();
            }

            long now = GetTickCount();
            long idleTime = now - info.dwTime;
            return idleTime > 0 ? TimeSpan.FromMilliseconds(idleTime) : TimeSpan.FromMilliseconds(1);
        }

        public override IWindow GetWindowFromHandle(nint handle)
        {
            return User32Window.FromHandle(handle);
        }

        public override void RevealFileOrFolder(string fileOrFolderPath)
        {
            Explorer.SelectSingleItem(fileOrFolderPath, false);
        }

        public override void SetMousePosition(ScreenPoint pt)
        {
            if (!SetCursorPos(pt.X, pt.Y))
                throw new Win32Exception();
        }

        public override MessageBoxResult ShowMessageBox(nint owner, string messageBoxText, string caption, MessageBoxButtons button, MessageBoxIcon icon)
        {
            return User32MessageBox.ShowCore(owner, messageBoxText, caption, button, icon, MessageBoxResult.None, MessageBoxOptions.None);
        }
    }
}
