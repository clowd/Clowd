using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

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
                return (_current = new Windows.User32Platform());

            throw new NotImplementedException();
        }

        public static void SetPlatform(Platform platform) => _current = platform;

        private static Platform _current;

        // instance members
        protected Platform()
        {
        }

        // mouse
        public abstract ScreenPoint GetMousePosition();
        public abstract void SetMousePosition(ScreenPoint pt);

        // display
        public abstract IEnumerable<IScreen> AllScreens { get; }
        public abstract IScreen PrimaryScreen { get; }
        public abstract IScreen VirtualScreen { get; }
        public abstract IScreen GetScreenFromPoint(ScreenPoint pt);
        public abstract IScreen GetScreenFromRect(ScreenRect rect);

        // window
        public abstract IWindow GetWindowFromHandle(nint handle);
        public abstract IWindow GetForegroundWindow();

        // screen capture
        public abstract IBitmap CaptureDesktop(bool drawCursor);
        public abstract IBitmap CaptureRegion(ScreenRect region, bool drawCursor);
        public abstract IBitmap CaptureWindow(IWindow window);

        // dialogs
        public abstract void RevealFileOrFolder(string fileOrFolderPath);
        public abstract MessageBoxResult ShowMessageBox(
            nint owner,
            string messageBoxText,
            string caption,
            MessageBoxButton button,
            MessageBoxImage icon
        );
    }

    public static class BuiltInPlatformExtensions
    {
        public static MessageBoxResult ShowMessageBox(
            this Platform platform,
            IntPtr owner,
            string messageBoxText,
            string caption,
            MessageBoxButton button)
        {
            return platform.ShowMessageBox(owner, messageBoxText, caption, button, MessageBoxImage.None);
        }

        public static MessageBoxResult ShowMessageBox(
            this Platform platform,
            IntPtr owner,
            string messageBoxText,
            string caption)
        {
            return platform.ShowMessageBox(owner, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None);
        }

        public static MessageBoxResult ShowMessageBox(
            this Platform platform,
            IntPtr owner,
            string messageBoxText)
        {
            return platform.ShowMessageBox(owner, messageBoxText, "", MessageBoxButton.OK, MessageBoxImage.None);
        }
    }
}
