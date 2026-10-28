using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.VisualTree;
using Prisc.Geometry2D;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Model;

namespace Prisc.UI.VideoEditor
{
    /// <summary>
    /// The preview overlay of a selected line, arrow or double arrow, in place of the transform
    /// gizmo: the image editor's three line handles (start, end, and the bend handle on the ink at
    /// t = 0.5), a faint accent spine so the line reads as selected, and a body drag that moves it.
    /// A line's geometry is its endpoints, so a box with corner handles would mean nothing here.
    ///
    /// Arranged by <see cref="VideoPreviewControl"/> over the <b>whole preview control</b>, not the
    /// video rect: a control only receives presses inside its own bounds, and an arrow pointing at
    /// something near the frame edge (or dragged partly off it) has handles on or past that edge.
    /// Everything is worked out in canvas pixels, the pointer offset by
    /// <see cref="CanvasRect"/>'s top-left.
    ///
    /// The control owns no geometry: the line is read from the model at draw time
    /// (<see cref="ShapeLineLayout.TryResolve"/>, never a cached <see cref="Item"/>), and a drag
    /// follows the gizmo's gesture discipline verbatim (<see cref="ShapeHandleDrag"/> computes
    /// every move from the press-time snapshot, one coalesced <see cref="EditorSession.EditItem"/>
    /// per move inside an <see cref="EditGesture"/>, Esc or a lost capture cancels). The commit
    /// raises <see cref="EditorSession.ProjectChanged"/> with this control as its origin, so the
    /// inspector re-reads the re-fitted box.
    ///
    /// Only the handles and the line's ink are hit-testable (<see cref="ICustomHitTest"/> over
    /// <see cref="ShapeHandleMath.ResolvePress"/>, the very test a press is resolved by), so a
    /// press anywhere else, or on the ink where another item is composed on top, falls through to
    /// the preview's click-to-select as if this full-size overlay were not there.
    /// </summary>
    public sealed class ShapeHandlesControl : Control, ICustomHitTest
    {
        private static readonly Cursor MoveCursor = new Cursor(StandardCursorType.SizeAll);

        private EditorSession _session;
        private Guid _itemId;

        // drag state (pointer positions are read in the parent preview's coordinates, so a layout
        // pass under the pointer mid-drag cannot corrupt them)
        private ShapeHandleDrag _drag;
        private EditGesture _gesture;
        private IPointer _dragPointer;

        public ShapeHandlesControl()
        {
            Cursor = MoveCursor; // only the handles and the ink are hit-testable, as GraphicLine's
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

                if (_drag != null)
                    CancelDrag();

                _session = value;
                _itemId = Guid.Empty;
            }
        }

        /// <summary>The letterboxed video rectangle in the parent preview's coordinates: the canvas
        /// the line is resolved on. Assigned on every arrange.</summary>
        public Rect CanvasRect { get; set; }

        /// <summary>The instant the preview is composing, in timeline ticks: a body press asks
        /// whether another item is composed over the line there. Assigned on every arrange.</summary>
        public long ComposedTicks { get; set; }

        /// <summary>True while a pointer drag owns the model (an <see cref="EditGesture"/> is open).</summary>
        public bool IsDragging => _drag != null;

        /// <summary>Points the handles at the line the preview just resolved.
        /// <see cref="Guid.Empty"/> means "nothing to show": the preview then arranges this control
        /// to an empty rect. Ignored for the item identity while a drag is in flight (a drag owns
        /// its target until it ends); always re-draws, because an edit moves the line without
        /// moving this control.</summary>
        internal void SetTarget(Guid itemId)
        {
            if (_drag == null || itemId == _itemId)
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

        /// <summary>A point in the parent preview's coordinates, in canvas pixels.</summary>
        private Vec2 ToCanvas(Point parentPoint) =>
            new Vec2(parentPoint.X - CanvasRect.X, parentPoint.Y - CanvasRect.Y);

        /// <summary>What a press at <paramref name="local"/> would grab.</summary>
        private ShapeHandlePart PartAt(Point local)
        {
            var item = CurrentItem();
            if (item == null || CanvasRect.Width <= 0 || CanvasRect.Height <= 0)
                return ShapeHandlePart.None;

            return ShapeHandleMath.ResolvePress(_session.Project, item, ComposedTicks,
                ToCanvas(local + Bounds.Position), CanvasRect.Width, CanvasRect.Height);
        }

        /// <summary>Hit-testable exactly where a press is taken; everything else is see-through.</summary>
        public bool HitTest(Point point) => _drag != null || PartAt(point) != ShapeHandlePart.None;

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            if (_drag != null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            // gestures do not nest (the same gate the gizmo and TimelineSurface.BeginDrag apply)
            if (_session == null || _session.IsGestureActive)
                return;

            // presses only arrive where HitTest said yes, but resolve again: a stale layout must
            // not start a drag on nothing, and a declined body press must reach the preview
            var part = PartAt(e.GetPosition(this));
            var item = CurrentItem();
            if (part == ShapeHandlePart.None || item == null)
                return;

            var drag = ShapeHandleDrag.Begin(item, part, ToCanvas(e.GetPosition(ParentVisual)),
                CanvasRect.Width, CanvasRect.Height,
                FrameComposer.PixelScale(_session.Project, CanvasRect.Height));
            if (drag == null)
                return;

            Focus();

            _drag = drag;
            _gesture = _session.BeginGesture(drag.Label, this);
            _dragPointer = e.Pointer;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            if (_drag == null || !Equals(e.Pointer.Captured, this))
                return;

            var item = CurrentItem();
            if (item?.Content is not ShapeContent { IsLine: true })
            {
                CancelDrag();
                return;
            }

            var drag = _drag;
            var pointer = ToCanvas(e.GetPosition(ParentVisual));
            var snap = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

            _session.EditItem(item.Id, i => drag.Apply(i, pointer, snap),
                $"handles:shape:{item.Id}", structural: false, origin: this);

            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (_drag == null || !Equals(e.Pointer.Captured, this))
                return;

            FinishDrag(commit: true); // before Capture(null): it re-enters OnPointerCaptureLost
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);

            // losing capture without a release is an abort: restore the pre-drag state.
            if (_drag != null)
                FinishDrag(commit: false);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Key == Key.Escape && _drag != null)
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
            _drag = null;
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

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var item = CurrentItem();
            if (item?.Content is not ShapeContent shape || CanvasRect.Width <= 0 || CanvasRect.Height <= 0 ||
                !ShapeLineLayout.TryResolve(item.Transform, shape, CanvasRect.Width, CanvasRect.Height, out var line))
                return;

            // canvas pixels to local coordinates
            var origin = CanvasRect.Position - Bounds.Position;
            Point Local(Vec2 v) => new Point(v.X + origin.X, v.Y + origin.Y);

            var accent = new SolidColorBrush(AppStyles.AccentColor);

            // the selection spine: a hairline along the curve, so the selected line reads as
            // selected even where its own ink is thin or dashed
            var spine = new Pen(new SolidColorBrush(AppStyles.AccentColor, 0.6), 1);
            if (line.Control is { } control)
            {
                var geometry = new StreamGeometry();
                using (var gctx = geometry.Open())
                {
                    gctx.BeginFigure(Local(line.Start), false);
                    gctx.QuadraticBezierTo(Local(control), Local(line.End));
                    gctx.EndFigure(false);
                }

                context.DrawGeometry(null, spine, geometry);
            }
            else
            {
                context.DrawLine(spine, Local(line.Start), Local(line.End));
            }

            // the bend handle last, on top: it is the one an exact tie grabs
            PreviewHandles.Draw(context, Local(line.Start), accent);
            PreviewHandles.Draw(context, Local(line.End), accent);
            PreviewHandles.Draw(context, Local(line.Handle), accent);
        }
    }
}
