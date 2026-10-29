using Avalonia;
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
        public static bool TryGet(out PixelPoint pt)
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
