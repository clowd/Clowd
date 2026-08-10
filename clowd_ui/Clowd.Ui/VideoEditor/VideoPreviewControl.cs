using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Clowd.VideoSDK;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// The letterboxed preview surface. It computes the track-0 video rectangle by hand (the
    /// Stretch.Uniform math) rather than letting an Image do it, because the webcam overlay must
    /// be positioned against the *video* rectangle, not the control bounds. Hosts, in z-order: an
    /// optional poster image (shown until the first decoded frame lands), the screen-track image
    /// (arranged to exactly the video rect), and the <see cref="WebcamOverlayControl"/>, which is
    /// re-positioned FROM the document on every layout pass and every document change — that is
    /// what keeps overlay drags and the sidebar numerics in lockstep.
    /// </summary>
    public sealed class VideoPreviewControl : Panel
    {
        private VideoEditDocument _document;
        private Size _videoPixelSize;
        private bool _hasWebcam;

        public VideoPreviewControl()
        {
            // hit-testable background so a click on the letterbox (not the overlay) can deselect
            Background = Brushes.Transparent;

            PosterImage = new Image { Stretch = Stretch.Uniform };
            ScreenImage = new Image { Stretch = Stretch.Fill }; // arranged to the exact video rect
            Overlay = new WebcamOverlayControl { IsVisible = false };

            Children.Add(PosterImage);
            Children.Add(ScreenImage);
            Children.Add(Overlay);

            // the first presented frame replaces the poster
            ScreenImage.PropertyChanged += (_, e) =>
            {
                if (e.Property == Image.SourceProperty)
                    PosterImage.IsVisible = ScreenImage.Source == null;
            };

            // a press that reaches the panel itself did not hit the overlay (it handles its own)
            PointerPressed += (_, _) => Overlay.IsSelected = false;
        }

        /// <summary>The screen-track presenter (hand to a WriteableBitmapFrameSink).</summary>
        public Image ScreenImage { get; }

        /// <summary>Poster/loading image shown until the first decoded frame lands.</summary>
        public Image PosterImage { get; }

        /// <summary>The webcam overlay (its inner Image is the webcam sink target).</summary>
        public WebcamOverlayControl Overlay { get; }

        /// <summary>The letterboxed video rectangle from the last arrange, in local coordinates.</summary>
        public Rect VideoRect { get; private set; }

        /// <summary>The edit document; overlay geometry/visibility follows it. Set once.</summary>
        public VideoEditDocument Document
        {
            get => _document;
            set
            {
                if (ReferenceEquals(_document, value))
                    return;

                if (_document != null)
                {
                    _document.PropertyChanged -= Document_PropertyChanged;
                    _document.Webcam.PropertyChanged -= Document_PropertyChanged;
                }

                _document = value;
                Overlay.Document = value;

                if (_document != null)
                {
                    _document.PropertyChanged += Document_PropertyChanged;
                    _document.Webcam.PropertyChanged += Document_PropertyChanged;
                }

                UpdateOverlayVisibility();
                InvalidateArrange();
            }
        }

        /// <summary>Sets the track-0 frame size in pixels once the media is open.</summary>
        public void SetVideo(Size pixelSize)
        {
            _videoPixelSize = pixelSize;
            InvalidateArrange();
        }

        /// <summary>Declares whether the media has a usable webcam track and its aspect (h/w).</summary>
        public void SetWebcam(bool hasWebcam, double aspectHeightOverWidth)
        {
            _hasWebcam = hasWebcam;
            if (aspectHeightOverWidth > 0)
                Overlay.WebcamAspect = aspectHeightOverWidth;

            UpdateOverlayVisibility();
            InvalidateArrange();
        }

        private void Document_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WebcamOverlay.Enabled))
                UpdateOverlayVisibility();

            // shape/radius changes do not move the control, so its arrange (where the clip is
            // refreshed) would be skipped — refresh the clip explicitly
            if (e.PropertyName is nameof(WebcamOverlay.Shape) or nameof(WebcamOverlay.CornerRadius))
                Overlay.RefreshShape();

            // any webcam geometry (or trim/cut, harmlessly) change re-positions the overlay
            InvalidateArrange();
        }

        private void UpdateOverlayVisibility()
        {
            Overlay.IsVisible = _hasWebcam && _document?.Webcam.Enabled == true;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (var child in Children)
                child.Measure(availableSize);

            return new Size(
                Double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
                Double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var full = new Rect(finalSize);
            PosterImage.Arrange(full);

            var videoRect = ComputeVideoRect(finalSize);
            VideoRect = videoRect;
            ScreenImage.Arrange(videoRect);

            Overlay.VideoRect = videoRect;
            Overlay.Arrange(ComputeOverlayRect(videoRect));

            return finalSize;
        }

        /// <summary>Stretch.Uniform of the video frame into the control bounds, centred.</summary>
        private Rect ComputeVideoRect(Size finalSize)
        {
            if (_videoPixelSize.Width <= 0 || _videoPixelSize.Height <= 0 ||
                finalSize.Width <= 0 || finalSize.Height <= 0)
                return new Rect(finalSize);

            var scale = Math.Min(
                finalSize.Width / _videoPixelSize.Width,
                finalSize.Height / _videoPixelSize.Height);

            var w = _videoPixelSize.Width * scale;
            var h = _videoPixelSize.Height * scale;

            return new Rect((finalSize.Width - w) / 2, (finalSize.Height - h) / 2, w, h);
        }

        /// <summary>
        /// The overlay rectangle inside <paramref name="videoRect"/>, mirroring
        /// <see cref="Clowd.UI.Services.VideoRenderManager.ComputeWebcamRect"/>: width is the
        /// document fraction of the video width, height follows the webcam aspect, and the rect is
        /// nudged fully inside the frame rather than clipped.
        /// </summary>
        private Rect ComputeOverlayRect(Rect videoRect)
        {
            var webcam = _document?.Webcam;
            if (webcam == null || videoRect.Width <= 0 || videoRect.Height <= 0)
                return new Rect(0, 0, 0, 0);

            var aspect = Overlay.WebcamAspect > 0 ? Overlay.WebcamAspect : 9.0 / 16.0;

            var w = Math.Clamp(webcam.Width * videoRect.Width, 2, videoRect.Width);
            var h = Math.Clamp(w * aspect, 2, videoRect.Height);

            var x = webcam.CenterX * videoRect.Width - w / 2;
            var y = webcam.CenterY * videoRect.Height - h / 2;
            x = Math.Clamp(x, 0, videoRect.Width - w);
            y = Math.Clamp(y, 0, videoRect.Height - h);

            return new Rect(videoRect.X + x, videoRect.Y + y, w, h);
        }
    }
}
