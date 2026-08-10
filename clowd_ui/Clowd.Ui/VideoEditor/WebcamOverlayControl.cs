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
    /// The webcam picture-in-picture overlay shown on top of the letterboxed preview. Visuals are
    /// a WYSIWYG mirror of the render mask (<see cref="Clowd.UI.Services.WebcamMaskRenderer"/>):
    /// the webcam frame fills the overlay rectangle (whose height follows the webcam track's own
    /// aspect ratio, exactly like <c>ComputeWebcamRect</c>), clipped by an inscribed ellipse for
    /// <see cref="WebcamOverlayShape.Circle"/> or a rounded rectangle whose corner radius is the
    /// document's height-fraction for <see cref="WebcamOverlayShape.RoundedRect"/>.
    ///
    /// Interaction mutates the <see cref="Document"/> only — body drag moves the normalized
    /// centre, the four corner handles resize (opposite corner anchored, aspect always uniform
    /// because the height is derived) — and the parent <see cref="VideoPreviewControl"/> positions
    /// this control back FROM the document on every change, so a drag and the sidebar numerics can
    /// never disagree.
    /// </summary>
    public sealed class WebcamOverlayControl : Panel
    {
        private const double HandleSize = 8;
        private const double HandleHitSize = 14;

        private readonly Panel _imageHost;
        private readonly ChromeControl _chrome;

        private VideoEditDocument _document;
        private bool _selected;

        // drag state (pointer positions are in the parent preview's coordinate space, so the
        // overlay moving under the pointer mid-drag cannot corrupt the deltas)
        private enum DragKind { None, Move, Resize }

        private DragKind _drag;
        private Point _dragStart;
        private double _startCenterX, _startCenterY;
        private int _resizeCorner; // 0=TL 1=TR 2=BL 3=BR
        private Point _resizeAnchor; // opposite corner, parent coords

        public WebcamOverlayControl()
        {
            Image = new Image { Stretch = Stretch.Fill };
            _imageHost = new Panel { Background = Brushes.Black };
            _imageHost.Children.Add(Image);
            _chrome = new ChromeControl(this) { IsHitTestVisible = false };

            Children.Add(_imageHost);
            Children.Add(_chrome);

            Cursor = new Cursor(StandardCursorType.SizeAll);
        }

        /// <summary>The image the player's webcam frame sink presents into.</summary>
        public Image Image { get; }

        /// <summary>The document whose <see cref="VideoEditDocument.Webcam"/> geometry this
        /// control reads and mutates. Set once by the window.</summary>
        public VideoEditDocument Document
        {
            get => _document;
            set => _document = value;
        }

        /// <summary>The letterboxed track-0 video rectangle, in the parent preview's coordinates.
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

        protected override Size ArrangeOverride(Size finalSize)
        {
            var result = base.ArrangeOverride(finalSize);
            UpdateClip(finalSize);
            return result;
        }

        /// <summary>Re-applies the clip for the current document shape/radius. Called by the
        /// preview on document changes — a shape change alone does not move the control, so its
        /// own arrange (where the clip is normally refreshed) would be skipped.</summary>
        public void RefreshShape()
        {
            UpdateClip(Bounds.Size);
        }

        /// <summary>Re-applies the mask-shaped clip (on the image host only, so the selection
        /// chrome can still draw its handles at the rectangle corners).</summary>
        private void UpdateClip(Size size)
        {
            if (size.Width <= 0 || size.Height <= 0)
                return;

            var bounds = new Rect(size);
            var webcam = _document?.Webcam;

            if (webcam == null || webcam.Shape == WebcamOverlayShape.Circle)
            {
                // the render mask inscribes an ellipse in the whole rect; mirror it exactly.
                _imageHost.Clip = new EllipseGeometry(bounds);
            }
            else
            {
                // radius is a fraction of the *height* (see WebcamMaskRenderer), capped at half
                // the shorter side so the shape cannot degenerate.
                var radius = Math.Clamp(webcam.CornerRadius, 0, 0.5) * size.Height;
                radius = Math.Min(radius, Math.Min(size.Width, size.Height) / 2.0);
                _imageHost.Clip = new RectangleGeometry(bounds, radius, radius);
            }

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

        /// <summary>Selection chrome drawn above the clipped image: an accent outline following the
        /// mask shape plus four square corner handles on the rectangle corners.</summary>
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

                var size = _owner.Bounds.Size;
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
