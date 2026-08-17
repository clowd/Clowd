using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// The zoom effect's focal-point <b>reticle</b>: a crosshair drawn over the composed preview at
    /// the selected zoom item's (FocusX, FocusY), draggable to move it. Arranged by
    /// <see cref="VideoPreviewControl"/> over the whole video rectangle whenever a zoom item is
    /// selected with the playhead inside its span (the same span rule as the transform gizmo), and
    /// to nothing otherwise.
    ///
    /// The control owns no geometry: the reticle position is read from the model at draw time
    /// (never a cached <see cref="Item"/> — undo replaces the project wholesale), so the
    /// inspector's Focus spinners and a drag here track each other live through
    /// <see cref="EditorSession.ProjectChanged"/>, exactly as the gizmo and the transform fields
    /// do. The drag itself follows the gizmo's gesture discipline verbatim: pointer deltas are
    /// measured in the parent preview's coordinates against a press-time snapshot, one coalesced
    /// <see cref="EditorSession.EditItem"/> per move inside an <see cref="EditGesture"/>, Esc (or
    /// a lost capture) cancels back to where the drag began.
    ///
    /// Only the reticle is hit-testable (the transparent hit disc drawn under it) — a press
    /// anywhere else never reaches this control, so the preview's click-to-select works through
    /// the "overlay" as if it were not there.
    /// </summary>
    public sealed class ZoomFocusControl : Control
    {
        /// <summary>Radius of the reticle's ring — and of its hit disc, so what looks grabbable
        /// is exactly what is (a ~24px target, the gizmo handles' hit box writ round).</summary>
        private const double RingRadius = 12;

        /// <summary>How far the four crosshair arms reach past the ring.</summary>
        private const double ArmLength = 7;

        private static readonly Cursor MoveCursor = new Cursor(StandardCursorType.SizeAll);

        private EditorSession _session;
        private Guid _itemId;

        // drag state (pointer positions are in the parent preview's coordinate space, so a layout
        // pass under the pointer mid-drag cannot corrupt the deltas)
        private bool _dragging;
        private EditGesture _gesture;
        private IPointer _dragPointer;
        private Point _dragStart;
        private double _startFocusX, _startFocusY;

        public ZoomFocusControl()
        {
            Cursor = MoveCursor; // only the reticle is hit-testable, so this is hover feedback
            Focusable = true; // Esc-to-cancel needs key events mid-drag
        }

        /// <summary>The editing session every drag writes through. Set by the preview control.</summary>
        public EditorSession Session
        {
            get => _session;
            set
            {
                if (ReferenceEquals(_session, value))
                    return;

                if (_dragging)
                    CancelDrag();

                _session = value;
                _itemId = Guid.Empty;
            }
        }

        /// <summary>The letterboxed video rectangle in the parent preview's coordinates — this
        /// control is arranged exactly onto it, and the normalized focus maps across it. Assigned
        /// on every arrange.</summary>
        public Rect CanvasRect { get; set; }

        /// <summary>True while a pointer drag owns the model (an <see cref="EditGesture"/> is open).</summary>
        public bool IsDragging => _dragging;

        /// <summary>Points the reticle at the zoom item the preview just resolved.
        /// <see cref="Guid.Empty"/> means "nothing to show" — the preview then arranges this
        /// control to an empty rect. Ignored for the item identity while a drag is in flight (a
        /// drag owns its target until it ends); always re-draws, because a focus edit moves the
        /// reticle without moving this control.</summary>
        internal void SetTarget(Guid itemId)
        {
            if (!_dragging || itemId == _itemId)
                _itemId = itemId;

            InvalidateVisual();
        }

        private Visual ParentVisual => this.GetVisualParent() as Visual ?? this;

        private Item CurrentItem()
        {
            if (_session == null || _itemId == Guid.Empty)
                return null;

            foreach (var item in _session.Project.Items)
            {
                if (item.Id == _itemId)
                    return item;
            }

            return null;
        }

        /// <summary>Where the reticle is drawn, in local coordinates: the item's normalized focus
        /// across this control's bounds (the video rect), clamped inside them — the model clamps
        /// focus to 0..1, so this only guards a mid-edit out-of-range value.</summary>
        private Point? ReticleCenter()
        {
            var size = Bounds.Size;
            if (size.Width <= 0 || size.Height <= 0 || CurrentItem()?.Content is not ZoomContent zoom)
                return null;

            return new Point(
                Math.Clamp(zoom.FocusX * size.Width, 0, size.Width),
                Math.Clamp(zoom.FocusY * size.Height, 0, size.Height));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            if (_dragging || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            // presses only arrive over the reticle's hit disc (nothing else here is hit-testable),
            // but resolve it anyway: a stale layout must not start a drag on nothing.
            if (ReticleCenter() is not { } center ||
                CanvasRect.Width <= 0 || CanvasRect.Height <= 0)
                return;

            var local = e.GetPosition(this);
            if (Math.Abs(local.X - center.X) > RingRadius || Math.Abs(local.Y - center.Y) > RingRadius)
                return;

            // gestures do not nest (the same gate the gizmo and TimelineSurface.BeginDrag apply)
            if (_session.IsGestureActive)
                return;

            var zoom = (ZoomContent)CurrentItem().Content;

            Focus();

            _dragStart = e.GetPosition(ParentVisual);
            _startFocusX = zoom.FocusX;
            _startFocusY = zoom.FocusY;
            _dragging = true;

            _gesture = _session.BeginGesture("Move zoom focus", this);
            _dragPointer = e.Pointer;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            if (!_dragging || !Equals(e.Pointer.Captured, this))
                return;

            var item = CurrentItem();
            if (item?.Content is not ZoomContent || CanvasRect.Width <= 0 || CanvasRect.Height <= 0)
            {
                CancelDrag();
                return;
            }

            var p = e.GetPosition(ParentVisual);
            var x = Clamp01(_startFocusX + (p.X - _dragStart.X) / CanvasRect.Width);
            var y = Clamp01(_startFocusY + (p.Y - _dragStart.Y) / CanvasRect.Height);

            _session.EditItem(item.Id, i =>
            {
                if (i.Content is ZoomContent z)
                {
                    z.FocusX = x;
                    z.FocusY = y;
                }
            }, $"gizmo:zoomfocus:{item.Id}", structural: false, origin: this);

            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (!_dragging || !Equals(e.Pointer.Captured, this))
                return;

            FinishDrag(commit: true); // before Capture(null): it re-enters OnPointerCaptureLost
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);

            // losing capture without a release is an abort: restore the pre-drag state.
            if (_dragging)
                FinishDrag(commit: false);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Key == Key.Escape && _dragging)
            {
                CancelDrag();
                e.Handled = true;
            }
        }

        /// <summary>Ends the drag. Commit pushes one undo entry for the whole drag (none when
        /// nothing net-changed); cancel restores the pre-drag project.</summary>
        private void FinishDrag(bool commit)
        {
            var gesture = _gesture;
            _gesture = null;
            _dragging = false;
            _dragPointer = null;

            if (gesture == null)
                return;

            if (commit)
                gesture.Commit();
            else
                gesture.Cancel();
        }

        private void CancelDrag()
        {
            var pointer = _dragPointer;
            FinishDrag(commit: false); // clears state first so the capture-lost re-entry is a no-op
            pointer?.Capture(null);
        }

        /// <summary>A NaN-proof clamp, same rule as the gizmo/inspector writes: a poisoned pointer
        /// delta must not be able to reach the project.</summary>
        private static double Clamp01(double value) =>
            Double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            if (ReticleCenter() is not { } center)
                return;

            // the hit disc: transparent but hit-testable, and exactly the ring — this is what
            // confines pointer events (and the move cursor) to the reticle, leaving the rest of
            // the preview clickable straight through this full-rect control.
            context.DrawEllipse(Brushes.Transparent, null, center, RingRadius, RingRadius);

            var accent = new SolidColorBrush(AppStyles.AccentColor);
            var halo = new Pen(new SolidColorBrush(Colors.White, 0.9), 3.5);
            var ink = new Pen(accent, 1.5);

            // white halo under the accent strokes, so the crosshair reads on any composed frame
            foreach (var pen in new[] { halo, ink })
            {
                context.DrawEllipse(null, pen, center, RingRadius, RingRadius);
                context.DrawLine(pen, center.WithX(center.X - RingRadius - ArmLength), center.WithX(center.X - RingRadius));
                context.DrawLine(pen, center.WithX(center.X + RingRadius), center.WithX(center.X + RingRadius + ArmLength));
                context.DrawLine(pen, center.WithY(center.Y - RingRadius - ArmLength), center.WithY(center.Y - RingRadius));
                context.DrawLine(pen, center.WithY(center.Y + RingRadius), center.WithY(center.Y + RingRadius + ArmLength));
            }

            context.DrawEllipse(accent, null, center, 2, 2);
        }
    }
}
