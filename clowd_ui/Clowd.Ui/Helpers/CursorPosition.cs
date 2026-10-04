using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace Clowd.UI.Helpers
{
    /// <summary>
    /// The mouse position right now, in the same units <see cref="DesktopScreens"/> reports
    /// monitors in: physical virtual-desktop pixels on Windows, CG points (top-left origin) on
    /// macOS. For placing a window on the screen the user is looking at without waiting for a
    /// pointer event.
    /// </summary>
    internal static class CursorPosition
    {
        /// <param name="anyWindow">Any live window; on macOS it is the route to the primary screen,
        /// whose height flips AppKit's bottom-left origin.</param>
        public static bool TryGet(Window anyWindow, out PixelPoint pt)
        {
            pt = default;

            if (OperatingSystem.IsWindows())
            {
                if (!GetCursorPos(out var p))
                    return false;
                pt = new PixelPoint(p.X, p.Y);
                return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                // NSEvent +mouseLocation: points, origin at the primary screen's bottom-left, y up
                var primary = DesktopScreens.Primary(anyWindow);
                var nsEvent = WindowNativeExtensions.objc_getClass("NSEvent");
                if (primary == null || nsEvent == IntPtr.Zero)
                    return false;

                var loc = WindowNativeExtensions.objc_msgSend_NSPoint(nsEvent, WindowNativeExtensions.sel_registerName("mouseLocation"));
                pt = new PixelPoint((int)Math.Floor(loc.X), (int)Math.Floor(primary.Bounds.Height - loc.Y));
                return true;
            }

            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);
    }
}
