using Avalonia.Input;

namespace Clowd.Drawing
{
    /// <summary>
    /// The open/closed hand pair browsers show on a draggable handle (CSS <c>grab</c> /
    /// <c>grabbing</c>) — Windows ships no equivalent among its system cursors. The glyphs are
    /// Mozilla's <c>grab.cur</c>/<c>grabbing.cur</c> (gecko-dev <c>widget/windows/res</c>, MPL 2.0),
    /// repacked with PNG frames (32px original plus a nearest-neighbour 64px for high DPI) because
    /// <see cref="CursorResources"/> only decodes PNG-frame .cur files.
    /// </summary>
    public static class DragCursors
    {
        /// <summary>Open hand: this can be grabbed.</summary>
        public static Cursor Grab => CursorResources.GetCursor("Grab.cur");

        /// <summary>Closed hand: it is being held.</summary>
        public static Cursor Grabbing => CursorResources.GetCursor("Grabbing.cur");
    }
}
