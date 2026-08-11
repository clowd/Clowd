using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Clowd.VideoSDK;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// The webcam picture-in-picture <b>gizmo</b>: a transparent, hit-testable rectangle sitting
    /// over the composed preview, drawing only the selection chrome (an outline following the mask
    /// shape, the true rectangular extent, and four corner handles). The picture itself is composed
    /// by the SDK's <c>FrameComposer</c> underneath — that is the deliberate cost of WYSIWYG: the
    /// gizmo can no longer disagree with the render, because it does not draw the camera at all.
    ///
    /// Interaction mutates the <see cref="Document"/>'s webcam geometry only — body drag moves the
    /// normalized centre, the four corner handles resize (opposite corner anchored, aspect always
    /// uniform because the height is derived) — and the parent <see cref="VideoPreviewControl"/>
    /// positions this control back FROM the document on every change, while
    /// <see cref="EditorProject"/> writes the same numbers into the webcam items'
    /// <c>Transform</c> (normalized centre + width fraction — the identical convention, see
    /// <c>Clowd.VideoSDK.Model.Transform</c>). So a drag, the sidebar numerics and the composed
    /// picture can never disagree.
    /// </summary>
    public sealed class WebcamOverlayControl : Panel
    {
        private const double HandleSize = 8;
        private const double HandleHitSize = 14;

        private readonly ChromeControl _chrome;

        private VideoEditDocument _document;
        private bool _selected;

        // drag state (pointer positions are in the parent preview's coordinate space, so the
        // gizmo moving under the pointer mid-drag cannot corrupt the deltas)
        private enum DragKind { None, Move, Resize }

        private DragKind _drag;
        private Point _dragStart;
        private double _startCenterX, _startCenterY;
        private int _resizeCorner; // 0=TL 1=TR 2=BL 3=BR
        private Point _resizeAnchor; // opposite corner, parent coords

        public WebcamOverlayControl()
        {
            // transparent, but hit-testable: the whole rectangle is the drag target.
            Background = Brushes.Transparent;

            _chrome = new ChromeControl(this) { IsHitTestVisible = false };
            Children.Add(_chrome);

            Cursor = new Cursor(StandardCursorType.SizeAll);
        }

        /// <summary>The document whose <see cref="VideoEditDocument.Webcam"/> geometry this
        /// control reads and mutates. Set once by the window.</summary>
        public VideoEditDocument Document
        {
            get => _document;
            set => _document = value;
        }

        /// <summary>The letterboxed screen-frame rectangle, in the parent preview's coordinates.
        /// Assigned by <see cref="VideoPreviewControl"/> on every arrange — all pointer-delta →
        /// normalized-value math is relative to this rect.</summary>
        public Rect VideoRect { get; set; }

        /// <summary>Webcam frame aspect as height/width (used for anchored corner resizes).</summary>
        public double WebcamAspect { get; set; } = 9.0 / 16.0;

        /// <summary>Whether the selection chrome (accent outline + corner handles) is shown.</summary>
        public bool IsSelected
        {
            get => _selected;
            set
            {
                if (_selected == value)
                    return;

                _selected = value;
                _chrome.InvalidateVisual();
            }
        }

        /// <summary>Re-draws the chrome for the current document shape/radius. Called by the
        /// preview on document changes — a shape change alone does not move the control, so its
        /// own arrange would be skipped.</summary>
        public void RefreshShape()
        {
            _chrome.InvalidateVisual();
        }

        // ====================================================================
        // Pointer interaction
        // ====================================================================

        private Visual ParentVisual => this.GetVisualParent() as Visual ?? this;

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            if (_document == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            IsSelected = true;

            var local = e.GetPosition(this);
            _dragStart = e.GetPosition(ParentVisual);
            _startCenterX = _document.Webcam.CenterX;
            _startCenterY = _document.Webcam.CenterY;

            var corner = HitCorner(local);
            if (corner >= 0)
            {
                _drag = DragKind.Resize;
                _resizeCorner = corner;
                // the opposite corner stays put; everything is computed from it
                var b = Bounds;
                _resizeAnchor = corner switch
                {
                    0 => b.BottomRight,
                    1 => b.BottomLeft,
                    2 => b.TopRight,
                    _ => b.TopLeft,
                };
            }
            else
            {
                _drag = DragKind.Move;
            }

            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            if (_drag == DragKind.None)
            {
                UpdateHoverCursor(e.GetPosition(this));
                return;
            }

            if (_document == null || VideoRect.Width <= 0 || VideoRect.Height <= 0)
                return;

            var p = e.GetPosition(ParentVisual);

            if (_drag == DragKind.Move)
            {
                _document.Webcam.CenterX = _startCenterX + (p.X - _dragStart.X) / VideoRect.Width;
                _document.Webcam.CenterY = _startCenterY + (p.Y - _dragStart.Y) / VideoRect.Height;
            }
            else
            {
                ApplyResize(p);
            }

            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (_drag != DragKind.None)
            {
                _drag = DragKind.None;
                e.Pointer.Capture(null);
                e.Handled = true;
            }
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            _drag = DragKind.None;
        }

        /// <summary>Anchored uniform resize: the dragged corner follows the pointer, the opposite
        /// corner stays fixed, and the aspect stays the webcam's own (height is derived). Width
        /// then centre are written to the document; the document clamps.</summary>
        private void ApplyResize(Point p)
        {
            var aspect = WebcamAspect > 0 ? WebcamAspect : 9.0 / 16.0;

            // candidate width from both axes; take the larger so the shape tracks whichever
            // direction the user is actually pulling.
            var wFromX = Math.Abs(p.X - _resizeAnchor.X);
            var wFromY = Math.Abs(p.Y - _resizeAnchor.Y) / aspect;
            var widthPx = Math.Max(wFromX, wFromY);

            _document.Webcam.Width = widthPx / VideoRect.Width;

            // read back the clamped width for the centre so the anchor really stays anchored
            var effectiveWidthPx = _document.Webcam.Width * VideoRect.Width;
            var effectiveHeightPx = effectiveWidthPx * aspect;

            var sx = _resizeCorner is 1 or 3 ? 1 : -1; // dragged corner right of the anchor?
            var sy = _resizeCorner is 2 or 3 ? 1 : -1; // below it?

            var centerPx = new Point(
                _resizeAnchor.X + sx * effectiveWidthPx / 2,
                _resizeAnchor.Y + sy * effectiveHeightPx / 2);

            _document.Webcam.CenterX = (centerPx.X - VideoRect.X) / VideoRect.Width;
            _document.Webcam.CenterY = (centerPx.Y - VideoRect.Y) / VideoRect.Height;
        }

        /// <summary>Which corner handle (0=TL 1=TR 2=BL 3=BR) the point is over, or -1.</summary>
        private int HitCorner(Point local)
        {
            var b = Bounds;
            var corners = new[]
            {
                new Point(0, 0),
                new Point(b.Width, 0),
                new Point(0, b.Height),
                new Point(b.Width, b.Height),
            };

            for (int i = 0; i < corners.Length; i++)
            {
                if (Math.Abs(local.X - corners[i].X) <= HandleHitSize / 2 &&
                    Math.Abs(local.Y - corners[i].Y) <= HandleHitSize / 2)
                    return i;
            }

            return -1;
        }

        private void UpdateHoverCursor(Point local)
        {
            var corner = HitCorner(local);
            Cursor = corner switch
            {
                0 or 3 => new Cursor(StandardCursorType.TopLeftCorner),
                1 or 2 => new Cursor(StandardCursorType.TopRightCorner),
                _ => new Cursor(StandardCursorType.SizeAll),
            };
        }

        /// <summary>Selection chrome drawn over the composed picture: an accent outline following
        /// the mask shape plus four square corner handles on the rectangle corners.</summary>
        private sealed class ChromeControl : Control
        {
            private readonly WebcamOverlayControl _owner;

            public ChromeControl(WebcamOverlayControl owner)
            {
                _owner = owner;
            }

            public override void Render(DrawingContext context)
            {
                base.Render(context);

                if (!_owner.IsSelected)
                    return;

                // the chrome is arranged to the whole gizmo, so its own bounds are the rectangle
                // the outline and handles belong on.
                var size = Bounds.Size;
                if (size.Width <= 0 || size.Height <= 0)
                    return;

                var bounds = new Rect(size);
                var accent = new SolidColorBrush(AppStyles.AccentColor);
                var outline = new Pen(accent, 1.5);
                var handleBorder = new Pen(Brushes.White, 1);

                var webcam = _owner._document?.Webcam;
                if (webcam == null || webcam.Shape == WebcamOverlayShape.Circle)
                {
                    context.DrawEllipse(null, outline, bounds.Center, bounds.Width / 2, bounds.Height / 2);
                }
                else
                {
                    var radius = Math.Clamp(webcam.CornerRadius, 0, 0.5) * size.Height;
                    radius = Math.Min(radius, Math.Min(size.Width, size.Height) / 2.0);
                    context.DrawRectangle(null, outline, new RoundedRect(bounds, radius));
                }

                // faint full-rect outline so the true (rectangular) extent is visible even for
                // circular masks — the corner handles sit on this rectangle.
                context.DrawRectangle(null, new Pen(new SolidColorBrush(Colors.White, 0.35), 1), bounds);

                foreach (var corner in new[]
                         {
                             new Point(0, 0),
                             new Point(size.Width, 0),
                             new Point(0, size.Height),
                             new Point(size.Width, size.Height),
                         })
                {
                    var handle = new Rect(
                        corner.X - HandleSize / 2, corner.Y - HandleSize / 2,
                        HandleSize, HandleSize);
                    context.DrawRectangle(accent, handleBorder, handle);
                }
            }
        }
    }
}
