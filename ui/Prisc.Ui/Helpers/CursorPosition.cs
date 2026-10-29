using Avalonia;
using Avalonia.Controls;
using Prisc.Interop;

namespace Prisc.UI.Helpers
{
    /// <summary>
    /// The mouse position right now, in the same units <see cref="DesktopScreens"/> reports
    /// monitors in: physical virtual-desktop pixels on Windows, CG points (top-left origin) on
    /// macOS. For placing a window on the screen the user is looking at without waiting for a
    /// pointer event.
    /// </summary>
    internal static class CursorPosition
    {
        /// <param name="anyWindow">Unused: the position needs no window any more (the native side
        /// reads it in top-left space directly). Kept so the callers stay as they are.</param>
        public static bool TryGet(Window anyWindow, out PixelPoint pt)
        {
            if (NativeCursor.TryGetPosition(out var x, out var y))
            {
                pt = new PixelPoint(x, y);
                return true;
            }

            pt = default;
            return false;
        }
    }
}
