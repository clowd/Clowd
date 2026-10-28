using Avalonia.Input;

namespace Prisc.Drawing
{
    /// <summary>
    /// Drag cursors browsers provide but Windows does not ship among its system cursors. All
    /// three are drawn by tools/cursor-generator (<c>DrawGrabHand</c>, <c>DrawGrabbingHand</c>
    /// and <c>DrawColResize</c> there) into the PNG-frame .cur form <see cref="CursorResources"/>
    /// decodes.
    /// </summary>
    public static class DragCursors
    {
        /// <summary>Open hand: this can be grabbed.</summary>
        public static Cursor Grab => CursorResources.Grab;

        /// <summary>Closed hand: it is being held.</summary>
        public static Cursor Grabbing => CursorResources.Grabbing;

        /// <summary>Two parallel bars with an arrow out each side (CSS <c>col-resize</c>): this
        /// edge resizes the columns either side of it.</summary>
        public static Cursor ColResize => CursorResources.ColResize;
    }
}
