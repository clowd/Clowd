namespace Prisc.Interop
{
    /// <summary>The cursor position (<c>prisc_cursor_position</c>).</summary>
    public static unsafe class NativeCursor
    {
        /// <summary>
        /// The cursor position right now, in the space <c>prisc_screens_*</c> reports screens in:
        /// physical virtual-desktop pixels on Windows, CoreGraphics global points with a top-left
        /// origin on macOS (floored). False when the OS will not say (a secure desktop, no window
        /// server event) or on a platform without a cursor backend. Needs no window; any thread.
        /// </summary>
        public static bool TryGetPosition(out int x, out int y)
        {
            InteropLibrary.Require();
            PriscPoint point;
            int status = NativeMethods.prisc_cursor_position(&point);
            if (status == (int)InteropStatus.Ok)
            {
                x = point.x;
                y = point.y;
                return true;
            }

            x = y = 0;
            if (status != (int)InteropStatus.None && status != (int)InteropStatus.Unsupported)
                Native.Check(status, "prisc_cursor_position");
            return false;
        }
    }
}
