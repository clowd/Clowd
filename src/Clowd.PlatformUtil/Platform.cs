using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Clowd.PlatformUtil
{
    public abstract class Platform
    {
        // static members
        public static Platform Current => GetCurrentPlatform();

        private static Platform GetCurrentPlatform()
        {
            if (_current != null)
                return _current;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return _current = new Windows.User32Platform();

            throw new NotImplementedException();
        }

        public static void SetPlatform(Platform platform) => _current = platform;

        private static Platform _current;

        // instance members
        protected Platform()
        { }

        // mouse
        public abstract ScreenPoint GetMousePosition();
        public abstract void SetMousePosition(ScreenPoint pt);

        // display
        public abstract IEnumerable<IScreen> AllScreens { get; }
        public abstract IScreen PrimaryScreen { get; }
        public abstract IScreen VirtualScreen { get; }
        public abstract IScreen GetScreenFromPoint(ScreenPoint pt);
        public abstract IScreen GetScreenFromRect(ScreenRect rect);

        // window / general
        public abstract IWindow GetWindowFromHandle(nint handle);
        public abstract IWindow GetForegroundWindow();
        public abstract TimeSpan GetSystemIdleTime();

        // screen capture
        public abstract IBitmap CaptureDesktop(bool drawCursor);
        public abstract IBitmap CaptureRegion(ScreenRect region, bool drawCursor);
        public abstract IBitmap CaptureWindow(IWindow window);

        // dialogs
        public abstract void RevealFileOrFolder(string fileOrFolderPath);

        public abstract MessageBoxResult ShowMessageBox(
            nint owner,
            string messageBoxText,
            string title,
            MessageBoxButtons button,
            MessageBoxIcon icon
        );
    }

    public static class BuiltInPlatformExtensions
    {
        public static DpiContext ToDpiContext(this IScreen screen)
        {
            return new DpiContext((int)(screen.PixelDensity * 96.0), (int)(screen.PixelDensity * 96.0));
        }

        public static MessageBoxResult ShowMessageBox(
            this Platform platform,
            nint owner,
            string messageBoxText,
            string title,
            MessageBoxButtons button)
        {
            return platform.ShowMessageBox(owner, messageBoxText, title, button, MessageBoxIcon.None);
        }

        public static MessageBoxResult ShowMessageBox(
            this Platform platform,
            nint owner,
            string messageBoxText,
            string title)
        {
            return platform.ShowMessageBox(owner, messageBoxText, title, MessageBoxButtons.OK, MessageBoxIcon.None);
        }

        public static MessageBoxResult ShowMessageBox(
            this Platform platform,
            nint owner,
            string messageBoxText)
        {
            return platform.ShowMessageBox(owner, messageBoxText, "", MessageBoxButtons.OK, MessageBoxIcon.None);
        }

        public static MessageBoxResult ShowMessageBox(
            this Platform platform,
            string messageBoxText,
            string title,
            MessageBoxButtons button,
            MessageBoxIcon icon)
        {
            return platform.ShowMessageBox(0, messageBoxText, title, button, icon);
        }

        public static MessageBoxResult ShowMessageBox(
            this Platform platform,
            string messageBoxText,
            string title,
            MessageBoxButtons button)
        {
            return platform.ShowMessageBox(0, messageBoxText, title, button, MessageBoxIcon.None);
        }

        public static MessageBoxResult ShowMessageBox(
            this Platform platform,
            string messageBoxText,
            string title)
        {
            return platform.ShowMessageBox(0, messageBoxText, title, MessageBoxButtons.OK, MessageBoxIcon.None);
        }

        public static MessageBoxResult ShowMessageBox(
            this Platform platform,
            string messageBoxText)
        {
            return platform.ShowMessageBox(0, messageBoxText, "", MessageBoxButtons.OK, MessageBoxIcon.None);
        }
    }
}
