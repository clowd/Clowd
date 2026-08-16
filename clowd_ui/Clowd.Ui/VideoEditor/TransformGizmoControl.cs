using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Clowd.Drawing;
using Clowd.UI.VideoEditor.Inspector;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using ModelTransform = Clowd.VideoSDK.Model.Transform;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// The on-preview transform <b>gizmo</b>: a transparent, hit-testable rectangle sitting over the
    /// composed preview on whatever item is selected, drawing only the selection chrome (an outline
    /// following the item's mask, the true rectangular extent, and the resize handles — four
    /// corners, plus the four edge midpoints once the item's aspect ratio is unlocked, plus the
    /// rotate handle floating off the right edge, exactly as the image editor's). The picture
    /// itself is composed by the SDK's <c>FrameComposer</c> underneath — that is the deliberate cost
    /// of WYSIWYG: the gizmo can no longer disagree with the render, because it does not draw the
    /// item at all.
    ///
    /// <b>Data flow.</b> The gizmo owns no geometry. <see cref="VideoPreviewControl"/> resolves the
    /// selected item's composed rect (<see cref="ItemPlacement.TryResolve"/>) on every layout pass —
    /// which it re-runs on every project change, selection change and playhead move — and hands it
    /// here through <see cref="SetTarget"/> before arranging this control onto it. A drag therefore
    /// reads its own position back <i>from the model</i>: pointer deltas are measured in the parent
    /// preview's coordinate space against the values captured at press time, so the control moving
    /// under the pointer mid-drag cannot corrupt them, and the inspector's spinners (which re-read on
    /// the same <see cref="EditorSession.ProjectChanged"/>) track the drag live. There is no feedback
    /// loop in either direction: both writers go through the session, neither caches an
    /// <see cref="Item"/>, and each drag move recomputes an absolute value from the press-time
    /// snapshot rather than accumulating.
    ///
    /// <b>Presses it declines.</b> The gizmo is exactly the size of its item, which for a full-frame
    /// background is the whole preview — so a body press is only taken when this item is the one
    /// composed on top there. Otherwise the press is left unhandled and falls through to the
    /// preview's own hit-test, which selects the overlay the click was actually aimed at.
    ///
    /// <b>Writes.</b> One <see cref="EditorSession.EditItems"/> per pointer move over the selected
    /// item's whole linked row (<see cref="ItemRowScope"/> — placement belongs to the feed, not the
    /// cut, the same rule the inspector's transform fields follow), bracketed by an
    /// <see cref="EditGesture"/> so the entire drag is one undo entry; Esc (or a lost capture)
    /// cancels it back to where the drag began.
    /// </summary>
    public sealed class TransformGizmoControl : Panel
    {
        /// <summary>Diameter of a handle, matching the image editor's
        /// <c>GraphicBase.UnscaledControlSize</c> — the two editors draw the same control, so they
        /// are the same size as well as the same shape.</summary>
        private const double HandleSize = 12;

        private const double HandleHitSize = 16;

        /// <summary>Handle indices. 0-3 are the corners (the only ones an aspect-locked item has);
        /// 4-7 are the edge midpoints, which appear once the aspect ratio is unlocked and there is
        /// a single axis worth resizing on its own; 8 is the rotate handle floating off the right
        /// edge (the image editor's handle 9).</summary>
        private const int HandleTopLeft = 0, HandleTopRight = 1, HandleBottomLeft = 2, HandleBottomRight = 3;
        private const int HandleLeft = 4, HandleTop = 5, HandleRight = 6, HandleBottom = 7;
        private const int HandleRotate = 8;

        /// <summary>How far past the right edge the rotate handle's centre floats — the image
        /// editor's 32px (<c>GraphicRectangle.GetHandle</c> case 9).</summary>
        private const double RotateHandleOffset = 32;

        /// <summary>How far outside the item's rect this control is arranged, so a corner handle is
        /// grabbable on <i>both</i> sides of the corner. Avalonia routes a press only to a control
        /// whose bounds contain it: arranged exactly onto the item, the outer half of every corner
        /// handle would fall outside and the press would miss the gizmo entirely.
        /// <see cref="VideoPreviewControl"/> inflates by this; everything here works off
        /// <see cref="ItemRect"/>, the deflated rectangle that is the item.</summary>
        internal const double HandlePad = HandleHitSize / 2;

        /// <summary>Extra arrange room on the right side only, so the floating rotate handle also
        /// falls inside this control's bounds and can be pressed.</summary>
        internal const double RotatePad = RotateHandleOffset + HandleHitSize / 2;

        // cached: the cursor is re-evaluated on every pointer move, and each Cursor is a native
        // handle. The resize cursors come from the image editor's angle-aware set
        // (CursorResources.GetResizeCursor, cached there) so a rotated item shows a cursor whose
        // angle matches the edge it will drag.
        private static readonly Cursor MoveCursor = new Cursor(StandardCursorType.SizeAll);
        private static readonly Cursor ArrowCursor = new Cursor(StandardCursorType.Arrow);

        private readonly ChromeControl _chrome;
        private readonly RotateTransform _rotate = new RotateTransform();

        private EditorSession _session;
        private Guid _itemId;
        private double _aspect = 9.0 / 16.0;
        private double _scaleDenominatorPx;
        private double _scaleDenominatorYPx;
        private double _rotation;

        private enum DragKind
        {
            None,
            Move,
            Resize,
            Rotate,
        }

        // drag state (pointer positions are in the parent preview's coordinate space, so the gizmo
        // moving under the pointer mid-drag cannot corrupt the deltas)
        private DragKind _drag;
        private EditGesture _gesture;
        private IPointer _dragPointer;
        private Point _dragStart;
        private double _startX, _startY;
        private Point _resizeAnchor; // opposite corner (or edge midpoint), unrotated parent coords
        private Point _anchorVis; // where that anchor is actually drawn (rotated), parent coords
        private Point _rotationCenter; // item centre at press time, parent coords
        private double _rotationAtPress;
        private int _dragHandle = -1;
        private bool _dragRight, _dragDown;

        public TransformGizmoControl()
        {
            // transparent, but hit-testable: the whole rectangle is the drag target.
            Background = Brushes.Transparent;

            _chrome = new ChromeControl(this) { IsHitTestVisible = false };
            Children.Add(_chrome);

            Cursor = MoveCursor;
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

                if (_drag != DragKind.None)
                    CancelDrag();

                _session = value;
                _itemId = Guid.Empty;
            }
        }

        /// <summary>The letterboxed video rectangle in the parent preview's coordinates — the canvas
        /// all the normalized transform math is relative to. Assigned on every arrange.</summary>
        public Rect CanvasRect { get; set; }

        /// <summary>The instant the preview is composing, in timeline ticks — the gizmo needs it to
        /// answer "is something drawn over me here?" on a press. Assigned on every arrange.</summary>
        public long ComposedTicks { get; set; }

        /// <summary>True while a pointer drag owns the model (an <see cref="EditGesture"/> is open).</summary>
        public bool IsDragging => _drag != DragKind.None;

        /// <summary>
        /// Points the gizmo at the item the preview just placed, with the geometry a resize needs.
        /// <see cref="Guid.Empty"/> means "nothing to show" — the preview then arranges this control
        /// to an empty rect, which is what makes it invisible and unhittable. Ignored for the item
        /// identity while a drag is in flight: a drag owns its target until it ends.
        /// </summary>
        internal void SetTarget(Guid itemId, PlacedItem placed)
        {
            if (_drag != DragKind.None && itemId != _itemId)
                return;

            _itemId = itemId;
            if (itemId != Guid.Empty)
            {
                _aspect = placed.Aspect;
                _scaleDenominatorPx = placed.ScaleDenominatorPx;
                _scaleDenominatorYPx = placed.ScaleDenominatorYPx;
                _rotation = CurrentItem()?.Transform?.Rotation ?? 0;
            }
            else
            {
                _rotation = 0;
            }

            // The whole control (outline, handles, hit area) rotates the way the composer rotates
            // the picture: about the item's centre — which is not this control's centre, because
            // the arrange rect carries the asymmetric rotate-handle pad. Avalonia hit-tests through
            // render transforms, so local coordinates stay unrotated and all the handle math below
            // is untouched by this.
            if (_rotation != 0)
            {
                _rotate.Angle = _rotation;
                RenderTransform = _rotate;
                RenderTransformOrigin = new RelativePoint(
                    HandlePad + placed.W / 2, HandlePad + placed.H / 2, RelativeUnit.Absolute);
            }
            else
            {
                RenderTransform = null;
            }

            _chrome.InvalidateVisual();
        }

        /// <summary>Re-draws the chrome (the mask shape is read from the model at draw time, and a
        /// mask change alone does not move the control, so its own arrange would be skipped).</summary>
        public void RefreshChrome() => _chrome.InvalidateVisual();

        /// <summary>The item's own rectangle inside this padded control, in local coordinates —
        /// where the outline, the handles and the body live. The pad is <see cref="HandlePad"/> all
        /// round plus <see cref="RotatePad"/> on the right, where the rotate handle floats.
        /// Collapses to empty for the zero-sized "no gizmo" arrange, which is what makes that state
        /// draw and hit nothing.</summary>
        private static Rect ItemRect(Size size) => new Rect(
            HandlePad, HandlePad,
            Math.Max(0, size.Width - 2 * HandlePad - RotatePad),
            Math.Max(0, size.Height - 2 * HandlePad));

        /// <summary>Same rectangle in the parent preview's coordinates (unrotated — the arrange
        /// rect; the render transform is what the eye sees). All press-time geometry is captured
        /// off this.</summary>
        private Rect ItemRectInParent()
        {
            var r = ItemRect(Bounds.Size);
            return new Rect(Bounds.X + r.X, Bounds.Y + r.Y, r.Width, r.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _chrome.Arrange(ItemRect(finalSize));
            return finalSize;
        }

        // ====================================================================
        // Pointer interaction
        // ====================================================================

        private Visual ParentVisual => this.GetVisualParent() as Visual ?? this;

        private Item CurrentItem() =>
            _session == null || _itemId == Guid.Empty ? null : FindItem(_session, _itemId);

        private static Item FindItem(EditorSession session, Guid id)
        {
            foreach (var item in session.Project.Items)
            {
                if (item.Id == id)
                    return item;
            }

            return null;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            if (_drag != DragKind.None || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            var item = CurrentItem();
            if (item == null || CanvasRect.Width <= 0 || CanvasRect.Height <= 0)
                return;

            // a second pointer (touch/pen) can land here while a timeline drag owns the session;
            // gestures do not nest, so this press does nothing rather than throwing out of a
            // pointer handler (the same gate TimelineSurface.BeginDrag applies).
            if (_session.IsGestureActive)
                return;

            var origin = e.GetPosition(ParentVisual);
            var local = e.GetPosition(this);
            var handle = HitHandle(local);

            // The pad around the item is handle room only — a press in it that missed a handle is
            // outside the item, so it falls through to the preview's hit-test like any other miss.
            if (handle < 0 && !ItemRect(Bounds.Size).Contains(local))
                return;

            // A body press is only ours while nothing is composed on top of us there: a full-frame
            // background item's gizmo covers the whole preview, and it must not swallow the click
            // that was aimed at the overlay drawn over it. Leaving the press unhandled drops it
            // through to the preview panel, whose top-down hit-test then selects that overlay.
            // (Resize handles are chrome of their own and always win.)
            if (handle < 0 && !IsTopmostAt(origin))
                return;

            Focus();

            var transform = item.Transform ?? new ModelTransform();
            _dragStart = origin;
            _startX = transform.X;
            _startY = transform.Y;

            // Rotation is about the item centre, which a rotation cannot move — so this centre is
            // stable for a rotate drag, and for a resize it is the fixed frame the pointer is
            // unrotated in (rotation is rigid, so distances to the press-time anchor survive the
            // centre itself moving mid-drag).
            _rotationAtPress = _rotation;
            _rotationCenter = ItemRectInParent().Center;

            if (handle == HandleRotate)
            {
                _drag = DragKind.Rotate;
                _dragHandle = handle;
            }
            else if (handle >= 0)
            {
                _drag = DragKind.Resize;
                _dragHandle = handle;

                // the opposite corner (or edge) stays put; everything is computed from it
                var b = ItemRectInParent(); // unrotated parent coordinates
                _resizeAnchor = handle switch
                {
                    HandleTopLeft => b.BottomRight,
                    HandleTopRight => b.BottomLeft,
                    HandleBottomLeft => b.TopRight,
                    HandleBottomRight => b.TopLeft,
                    HandleLeft => new Point(b.Right, b.Center.Y),
                    HandleTop => new Point(b.Center.X, b.Bottom),
                    HandleRight => new Point(b.Left, b.Center.Y),
                    _ => new Point(b.Center.X, b.Top),
                };
                _dragRight = handle is HandleTopRight or HandleBottomRight or HandleRight;
                _dragDown = handle is HandleBottomLeft or HandleBottomRight or HandleBottom;

                // where that anchor is actually drawn — what a rotated resize holds fixed on screen
                var (avx, avy) = GizmoMath.RotateAbout(_resizeAnchor.X, _resizeAnchor.Y,
                    _rotationCenter.X, _rotationCenter.Y, _rotationAtPress);
                _anchorVis = new Point(avx, avy);
            }
            else
            {
                _drag = DragKind.Move;
                _dragHandle = -1;
            }

            _gesture = _session.BeginGesture(_drag switch
            {
                DragKind.Resize => "Resize item",
                DragKind.Rotate => "Rotate item",
                _ => "Move item",
            }, this);
            _dragPointer = e.Pointer;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            if (_drag == DragKind.None)
            {
                UpdateHoverCursor(e.GetPosition(this), e.GetPosition(ParentVisual));
                return;
            }

            if (!Equals(e.Pointer.Captured, this))
                return;

            var item = CurrentItem();
            if (item == null || CanvasRect.Width <= 0 || CanvasRect.Height <= 0)
            {
                CancelDrag();
                return;
            }

            var p = e.GetPosition(ParentVisual);
            if (_drag == DragKind.Move)
            {
                var (x, y) = GizmoMath.Move(_startX, _startY, p.X - _dragStart.X, p.Y - _dragStart.Y,
                    CanvasRect.Width, CanvasRect.Height);
                WriteRow(item, "gizmo:move", t =>
                {
                    t.X = x;
                    t.Y = y;
                });
                e.Handled = true;
                return;
            }

            if (_drag == DragKind.Rotate)
            {
                // the handle sits on the item's 0° axis (right-edge midpoint), so the pointer's
                // bearing from the centre *is* the rotation — the image editor's handle-9 rule.
                var degrees = Math.Atan2(p.Y - _rotationCenter.Y, p.X - _rotationCenter.X)
                    * 180 / Math.PI;
                WriteRow(item, "gizmo:rotate", t => t.Rotation = degrees);
                e.Handled = true;
                return;
            }

            // Resize. The axis-aligned math below is only valid in the item's own unrotated space,
            // so the pointer is unrotated about the press-time centre first (rotation is rigid, so
            // the anchor distances survive the centre itself moving mid-drag). For a rotated item
            // the centre those helpers return is then discarded: the composer rotates about the
            // centre a resize moves, so the anchored corner would orbit — AnchoredCenter solves for
            // the centre that pins the anchor's *drawn* position instead.
            var (pux, puy) = GizmoMath.RotateAbout(p.X, p.Y,
                _rotationCenter.X, _rotationCenter.Y, -_rotationAtPress);
            var rotated = _rotationAtPress != 0;

            // a horizontal edge handle: width only, the height untouched.
            if (_dragHandle is HandleLeft or HandleRight)
            {
                var (scaleX, x) = GizmoMath.ResizeAxis(pux, _resizeAnchor.X, _dragRight,
                    _scaleDenominatorPx, CanvasRect.X, CanvasRect.Width,
                    SelectedItemViewModel.MinScale, SelectedItemViewModel.MaxScale);
                if (rotated)
                {
                    var w = scaleX * _scaleDenominatorPx;
                    var (cx, cy) = AnchoredCenter(_dragRight ? -w / 2 : w / 2, 0);
                    WriteRow(item, "gizmo:resize", t =>
                    {
                        t.Scale = scaleX;
                        t.X = cx;
                        t.Y = cy;
                    });
                }
                else
                {
                    WriteRow(item, "gizmo:resize", t =>
                    {
                        t.Scale = scaleX;
                        t.X = x;
                    });
                }
            }
            // …and a vertical one: height only.
            else if (_dragHandle is HandleTop or HandleBottom)
            {
                var (scaleY, y) = GizmoMath.ResizeAxis(puy, _resizeAnchor.Y, _dragDown,
                    _scaleDenominatorYPx, CanvasRect.Y, CanvasRect.Height,
                    SelectedItemViewModel.MinScale, SelectedItemViewModel.MaxScale);
                if (rotated)
                {
                    var h = scaleY * _scaleDenominatorYPx;
                    var (cx, cy) = AnchoredCenter(0, _dragDown ? -h / 2 : h / 2);
                    WriteRow(item, "gizmo:resize", t =>
                    {
                        t.ScaleY = scaleY;
                        t.X = cx;
                        t.Y = cy;
                    });
                }
                else
                {
                    WriteRow(item, "gizmo:resize", t =>
                    {
                        t.ScaleY = scaleY;
                        t.Y = y;
                    });
                }
            }
            // a corner on an item whose aspect ratio the user unlocked resizes each axis on its
            // own; one that still has the lock keeps the content's aspect and derives its height.
            else if (item.Transform?.ScaleY is not null)
            {
                var (scaleX, scaleY, x, y) = GizmoMath.ResizeFree(pux, puy, _resizeAnchor.X, _resizeAnchor.Y,
                    _dragRight, _dragDown, _scaleDenominatorPx, _scaleDenominatorYPx,
                    CanvasRect.X, CanvasRect.Y, CanvasRect.Width, CanvasRect.Height,
                    SelectedItemViewModel.MinScale, SelectedItemViewModel.MaxScale);
                if (rotated)
                {
                    var w = scaleX * _scaleDenominatorPx;
                    var h = scaleY * _scaleDenominatorYPx;
                    var (cx, cy) = AnchoredCenter(_dragRight ? -w / 2 : w / 2, _dragDown ? -h / 2 : h / 2);
                    (x, y) = (cx, cy);
                }

                WriteRow(item, "gizmo:resize", t =>
                {
                    t.Scale = scaleX;
                    t.ScaleY = scaleY;
                    t.X = x;
                    t.Y = y;
                });
            }
            else
            {
                var (scale, x, y) = GizmoMath.Resize(pux, puy, _resizeAnchor.X, _resizeAnchor.Y,
                    _dragRight, _dragDown, _aspect, _scaleDenominatorPx,
                    CanvasRect.X, CanvasRect.Y, CanvasRect.Width, CanvasRect.Height,
                    SelectedItemViewModel.MinScale, SelectedItemViewModel.MaxScale);
                if (rotated)
                {
                    var w = scale * _scaleDenominatorPx;
                    var h = w * _aspect;
                    var (cx, cy) = AnchoredCenter(_dragRight ? -w / 2 : w / 2, _dragDown ? -h / 2 : h / 2);
                    (x, y) = (cx, cy);
                }

                WriteRow(item, "gizmo:resize", t =>
                {
                    t.Scale = scale;
                    t.X = x;
                    t.Y = y;
                });
            }

            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (_drag == DragKind.None || !Equals(e.Pointer.Captured, this))
                return;

            FinishDrag(commit: true); // before Capture(null): it re-enters OnPointerCaptureLost
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);

            // losing capture without a release is an abort: restore the pre-drag state.
            if (_drag != DragKind.None)
                FinishDrag(commit: false);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Key == Key.Escape && _drag != DragKind.None)
            {
                CancelDrag();
                e.Handled = true;
            }
        }

        /// <summary>The centre that keeps the resize anchor's drawn position fixed for a rotated
        /// item — press-time capture and canvas plumbed in, the maths in
        /// <see cref="GizmoMath.AnchoredCenter"/>.</summary>
        private (double X, double Y) AnchoredCenter(double toAnchorX, double toAnchorY) =>
            GizmoMath.AnchoredCenter(_anchorVis.X, _anchorVis.Y, toAnchorX, toAnchorY,
                _rotationAtPress, CanvasRect.X, CanvasRect.Y, CanvasRect.Width, CanvasRect.Height);

        /// <summary>One mutation for the whole row: a drag writes at pointer-move rate, and a
        /// per-item call would pay the serialize/validate/notify pipeline once per segment per
        /// move.</summary>
        private void WriteRow(Item item, string coalesceKey, Action<ModelTransform> apply) =>
            _session.EditItems(ItemRowScope.RowItemIds(_session, item),
                i => apply(i.Transform ??= new ModelTransform()),
                coalesceKey, structural: false, origin: this);

        /// <summary>Ends the drag. Commit pushes one undo entry for the whole drag (none when
        /// nothing net-changed — a no-op drag costs nothing); cancel restores the pre-drag
        /// project.</summary>
        private void FinishDrag(bool commit)
        {
            var gesture = _gesture;
            _gesture = null;
            _drag = DragKind.None;
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

        /// <summary>Whether the gizmo's own item is the one composed on top at this point (parent
        /// coordinates) — i.e. whether a press there belongs to this item or to something above it.
        /// Unknown geometry answers "yes": the gizmo is on screen, so it keeps its press.</summary>
        private bool IsTopmostAt(Point parentPoint)
        {
            if (_session == null || CanvasRect.Width <= 0 || CanvasRect.Height <= 0)
                return true;

            var hit = ItemPlacement.HitTest(_session.Project, ComposedTicks,
                parentPoint.X - CanvasRect.X, parentPoint.Y - CanvasRect.Y,
                CanvasRect.Width, CanvasRect.Height);

            return hit == null || hit.Id == _itemId;
        }

        /// <summary>Whether this item sizes its axes apart, which is what earns it edge handles: an
        /// aspect-locked item derives its height, so an edge drag would have nothing to write.</summary>
        private bool AspectUnlocked => CurrentItem()?.Transform?.ScaleY is not null;

        /// <summary>The handle positions in this control's coordinates, indexed by the Handle*
        /// constants. Edge handles are included only when the item has them.</summary>
        private static Point[] HandlePoints(Rect b, bool withEdges)
        {
            var points = new Point[withEdges ? 8 : 4];
            points[HandleTopLeft] = b.TopLeft;
            points[HandleTopRight] = b.TopRight;
            points[HandleBottomLeft] = b.BottomLeft;
            points[HandleBottomRight] = b.BottomRight;

            if (withEdges)
            {
                points[HandleLeft] = new Point(b.Left, b.Center.Y);
                points[HandleTop] = new Point(b.Center.X, b.Top);
                points[HandleRight] = new Point(b.Right, b.Center.Y);
                points[HandleBottom] = new Point(b.Center.X, b.Bottom);
            }

            return points;
        }

        /// <summary>The rotate handle's centre: floating <see cref="RotateHandleOffset"/> past the
        /// right-edge midpoint (the image editor's handle 9). Rotates with the whole control, so it
        /// orbits the item exactly as the image editor's does.</summary>
        private static Point RotateHandleCenter(Rect b) =>
            new Point(b.Right + RotateHandleOffset, b.Center.Y);

        /// <summary>Which handle the point is over, or -1. Measured against the item's own rect,
        /// which the pad keeps a full <see cref="HandleHitSize"/> box away from this control's
        /// edges. Corners are tested first, so they win where an edge handle would overlap one on a
        /// very small item.</summary>
        private int HitHandle(Point local)
        {
            var b = ItemRect(Bounds.Size);
            if (b.Width <= 0 || b.Height <= 0)
                return -1;

            var points = HandlePoints(b, AspectUnlocked);
            for (int i = 0; i < points.Length; i++)
            {
                if (Math.Abs(local.X - points[i].X) <= HandleHitSize / 2 &&
                    Math.Abs(local.Y - points[i].Y) <= HandleHitSize / 2)
                    return i;
            }

            var rotate = RotateHandleCenter(b);
            if (Math.Abs(local.X - rotate.X) <= HandleHitSize / 2 &&
                Math.Abs(local.Y - rotate.Y) <= HandleHitSize / 2)
                return HandleRotate;

            return -1;
        }

        /// <summary>Hover feedback that tells the truth about what a press will do: the resize
        /// handles (angled to match the item's rotation), the rotate handle, the move body, and the
        /// plain arrow where the press will fall through to select whatever is drawn over this
        /// item.</summary>
        private void UpdateHoverCursor(Point local, Point parentPoint)
        {
            var handle = HitHandle(local);
            Cursor = handle switch
            {
                HandleRotate => CursorResources.Rotate,
                >= 0 => ResizeCursor(handle),
                _ when !ItemRect(Bounds.Size).Contains(local) => ArrowCursor,
                _ => IsTopmostAt(parentPoint) ? MoveCursor : ArrowCursor,
            };
        }

        /// <summary>The image editor's angle-aware resize cursor for this handle
        /// (<c>GraphicRectangle.GetHandleCursor</c>): its 36-step set is indexed by the handle's
        /// compass direction plus the item's rotation, so the cursor's angle matches the edge it
        /// drags. The handle index is first mapped onto the image editor's clockwise-from-top-left
        /// numbering the formula was written for.</summary>
        private Cursor ResizeCursor(int handle)
        {
            int imageEditorHandle = handle switch
            {
                HandleTopLeft => 1,
                HandleTop => 2,
                HandleTopRight => 3,
                HandleRight => 4,
                HandleBottomRight => 5,
                HandleBottom => 6,
                HandleBottomLeft => 7,
                _ => 8, // HandleLeft
            };

            var angle = (45 * imageEditorHandle + _rotation + 272.5) % 360;
            if (angle < 0)
                angle += 360;

            return CursorResources.GetResizeCursor((int)(angle / 5) % 36);
        }

        /// <summary>Selection chrome drawn over the composed picture: an accent outline following
        /// the item's mask plus the resize handles on the rectangle it is inscribed in — drawn as
        /// the image editor draws its own, so a selection looks the same in both editors.</summary>
        private sealed class ChromeControl : Control
        {
            private readonly TransformGizmoControl _owner;

            public ChromeControl(TransformGizmoControl owner)
            {
                _owner = owner;
            }

            public override void Render(DrawingContext context)
            {
                base.Render(context);

                // the chrome is arranged to the whole gizmo, so its own bounds are the rectangle
                // the outline and handles belong on. No target => zero size => nothing to draw.
                var size = Bounds.Size;
                if (size.Width <= 0 || size.Height <= 0)
                    return;

                var bounds = new Rect(size);
                var accent = new SolidColorBrush(AppStyles.AccentColor);
                var outline = new Pen(accent, 1.5);

                var mask = _owner.CurrentItem()?.Transform?.Mask;
                if (mask == null)
                {
                    context.DrawRectangle(null, outline, bounds);
                }
                else if (mask.Shape == MaskShape.Circle)
                {
                    // the ellipse inscribed in the item rect — FrameComposer.ApplyClips' own quirk.
                    context.DrawEllipse(null, outline, bounds.Center, bounds.Width / 2, bounds.Height / 2);
                }
                else if (mask.Shape == MaskShape.Squircle)
                {
                    context.DrawGeometry(null, outline, MaskOutlines.Squircle(bounds));
                }
                else
                {
                    var radius = Math.Clamp(mask.CornerRadius, 0, 0.5) * size.Height;
                    radius = Math.Min(radius, Math.Min(size.Width, size.Height) / 2.0);
                    context.DrawRectangle(null, outline, new RoundedRect(bounds, radius));
                }

                // faint full-rect outline so the true (rectangular) extent is visible even for
                // masked items — the corner handles sit on this rectangle.
                if (mask != null)
                    context.DrawRectangle(null, new Pen(new SolidColorBrush(Colors.White, 0.35), 1), bounds);

                // Edge handles only where an edge drag has something to write: an aspect-locked
                // item derives its height, so the four corners are the whole of its resize.
                foreach (var point in HandlePoints(bounds, _owner.AspectUnlocked))
                    DrawHandle(context, point, accent);

                // The rotate handle, drawn as the image editor draws its own
                // (GraphicRectangle.DrawRotationTracker): a line from the right-edge midpoint out
                // to a green circle. The whole control render-rotates with the item, so this
                // orbits exactly as the image editor's does.
                var rotateCenter = RotateHandleCenter(bounds);
                context.DrawLine(new Pen(Brushes.Green, 1),
                    new Point(bounds.Right, bounds.Center.Y), rotateCenter);
                context.DrawEllipse(Brushes.Green, null, rotateCenter,
                    HandleSize / 2 - 1, HandleSize / 2 - 1);
            }

            /// <summary>The image editor's tracker, so the two editors' selection chrome reads as
            /// one control: three concentric circles — accent, a white ring 1px in, accent again
            /// (see <c>GraphicBase.DrawSingleTracker</c>).</summary>
            private static void DrawHandle(DrawingContext context, Point center, IBrush accent)
            {
                var radius = HandleSize / 2;
                context.DrawEllipse(accent, null, center, radius, radius);
                context.DrawEllipse(Brushes.White, null, center, radius - 1, radius - 1);
                context.DrawEllipse(accent, null, center, radius - 3, radius - 3);
            }
        }
    }
}
