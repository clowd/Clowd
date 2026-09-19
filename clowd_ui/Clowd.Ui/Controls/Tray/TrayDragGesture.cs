using System;
using Avalonia;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// The press → threshold → drag → release state machine behind the tray's grip, lifted out of the
    /// window so it can be reasoned about (and tested) without a pointer, a visual tree or a screen.
    /// All coordinates are screen pixels: the grip converts with <c>PointToScreen</c> before handing
    /// them over, because the window itself moves under the pointer during a drag and client-space
    /// deltas would feed that motion back into themselves.
    /// <para>
    /// Three rules are load-bearing and all three were bugs in the old code's neighbourhood:
    /// </para>
    /// <para>
    /// 1. The threshold is compared with a strict <c>&gt;</c> (the old <c>DragHandleMoved</c>), so a
    /// move of exactly the threshold is still a click. The grip supplies the value already scaled
    /// (<c>5 · RenderScaling</c>, unscaled on macOS), which is why this type takes a plain number of
    /// pixels and knows nothing about DPI.
    /// </para>
    /// <para>
    /// 2. <see cref="IsDragging"/> LATCHES: once the pointer has travelled far enough the gesture is a
    /// drag for the rest of its life, even if the pointer comes back inside the threshold box. Without
    /// the latch a slow drag that wanders back through its origin would end as a click, and the owner
    /// would treat a move as if nothing had happened (the window snapping back to where the drag began).
    /// </para>
    /// <para>
    /// 3. The delta reported by <see cref="Move"/> is always measured from the press point, never
    /// accumulated between moves. The owner adds it to the position it recorded at press time, so a
    /// dropped or coalesced move event cannot make the window drift away from the pointer.
    /// </para>
    /// </summary>
    public sealed class TrayDragGesture
    {
        private readonly double _thresholdPx;
        private PixelPoint _pressedAt;
        private bool _pressed;
        private bool _dragging;

        /// <param name="thresholdPx">
        /// Travel, in screen pixels, that must be EXCEEDED before the gesture becomes a drag. The grip
        /// passes <c>TrayTokens.DragThreshold * (OperatingSystem.IsMacOS() ? 1 : RenderScaling)</c>.
        /// </param>
        public TrayDragGesture(double thresholdPx)
        {
            _thresholdPx = thresholdPx;
        }

        /// <summary>True between <see cref="Press"/> and <see cref="Release"/>/<see cref="Cancel"/>.</summary>
        public bool IsPressed => _pressed;

        /// <summary>True once the threshold has been exceeded; latched for the rest of the gesture.</summary>
        public bool IsDragging => _dragging;

        /// <summary>Records the origin of a new gesture. Any previous gesture is abandoned.</summary>
        public void Press(PixelPoint at)
        {
            _pressed = true;
            _dragging = false;
            _pressedAt = at;
        }

        /// <summary>
        /// Feeds a pointer position in. Returns true while the gesture is a drag (i.e. from the first
        /// move that exceeds the threshold onwards); <paramref name="delta"/> is the total offset from
        /// the press point. Moves arriving without a press are ignored.
        /// </summary>
        public bool Move(PixelPoint at, out PixelPoint delta)
        {
            if (!_pressed)
            {
                delta = default;
                return false;
            }

            var deltaX = at.X - _pressedAt.X;
            var deltaY = at.Y - _pressedAt.Y;

            // strict >, exactly as the old DragHandleMoved: a move of precisely the threshold is a click
            if (Math.Abs(deltaX) > _thresholdPx || Math.Abs(deltaY) > _thresholdPx)
                _dragging = true;

            delta = new PixelPoint(deltaX, deltaY);
            return _dragging;
        }

        /// <summary>
        /// Ends the gesture and reports whether it had become a drag, so the caller can tell a drag
        /// (commit the position) from a click (ignored: the grip has no click action) with one call.
        /// Resets.
        /// </summary>
        public bool Release()
        {
            var wasDragging = _dragging;
            _pressed = false;
            _dragging = false;
            return wasDragging;
        }

        /// <summary>
        /// Capture was lost without a release — the window hidden mid-drag, another window taking the
        /// pointer. Resets unconditionally and reports nothing: there is no click to infer from a
        /// gesture the user never finished.
        /// </summary>
        public void Cancel()
        {
            _pressed = false;
            _dragging = false;
        }
    }
}
