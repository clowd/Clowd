using System;
using System.ComponentModel;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Clowd.VideoSDK;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;

// Avalonia.Media has a Transform of its own, and this file needs both.
using ModelTransform = Clowd.VideoSDK.Model.Transform;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// The letterboxed preview surface. It computes the video rectangle by hand (the
    /// Stretch.Uniform math) rather than letting an Image do it, because the composed picture and
    /// the webcam gizmo must both be positioned against the *video* rectangle, not the control
    /// bounds. The picture itself is drawn by <see cref="PreviewDrawOperation"/> — the whole
    /// project composed by the SDK's <c>FrameComposer</c>, which is the same code the render runs,
    /// so the preview is WYSIWYG by construction.
    ///
    /// Hosts, in z-order: the composed video (this control's own render), an optional poster image
    /// (shown until the first decoded frame lands) and the <see cref="WebcamOverlayControl"/>
    /// gizmo, which is re-positioned on every layout pass, every document change and every project
    /// change — that is what keeps gizmo drags and the sidebar numerics in lockstep.
    /// </summary>
    public sealed class VideoPreviewControl : Panel
    {
        private readonly PreviewGpuState _gpu = new PreviewGpuState();
        private VideoEditDocument _document;
        private CompositionPlayer _player;
        private Project _project;
        private Size _videoPixelSize;
        private bool _hasWebcam;
        private int _renderPending;
        private bool _sawFirstFrame;

        private readonly PreviewSurface _surface;

        public VideoPreviewControl()
        {
            // hit-testable background so a click on the letterbox (not the gizmo) can deselect
            Background = Brushes.Transparent;

            // the gizmo follows the composed picture, which the composer does NOT bound to the
            // frame (see ComputeOverlayRect), so an overlay taller than the frame really is
            // arranged past this control's edges — clip it here rather than letting the chrome
            // draw over the timeline and the sidebar.
            ClipToBounds = true;

            // Panel.Render is sealed, so the composed picture is drawn by a dedicated (bottom)
            // child rather than by the panel itself.
            _surface = new PreviewSurface(this) { IsHitTestVisible = false };
            PosterImage = new Image { Stretch = Stretch.Uniform };
            Overlay = new WebcamOverlayControl { IsVisible = false };

            Children.Add(_surface);
            Children.Add(PosterImage);
            Children.Add(Overlay);

            // a press that reaches the panel itself did not hit the gizmo (it handles its own)
            PointerPressed += (_, _) => Overlay.IsSelected = false;
        }

        /// <summary>Poster/loading image shown until the first decoded frame lands.</summary>
        public Image PosterImage { get; }

        /// <summary>The webcam placement gizmo (outline + handles only — the picture is composed).</summary>
        public WebcamOverlayControl Overlay { get; }

        /// <summary>The letterboxed video rectangle from the last arrange, in local coordinates.</summary>
        public Rect VideoRect { get; private set; }

        /// <summary>The edit document; gizmo geometry/visibility follows it. Set once.</summary>
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

        /// <summary>Attaches the player whose frames and clock the preview composes. Frame arrivals
        /// (decode threads) schedule a render pass.</summary>
        public void AttachPlayer(CompositionPlayer player)
        {
            if (ReferenceEquals(_player, player))
                return;

            if (_player != null)
                _player.FrameSource.FrameArrived -= OnFrameArrived;

            _player = player;

            if (_player != null)
                _player.FrameSource.FrameArrived += OnFrameArrived;

            RequestRender();
        }

        /// <summary>Points the preview at the current project (every edit rebuilds it).</summary>
        public void SetProject(Project project)
        {
            _project = project;
            // the gizmo is placed from the project's own webcam transform, so a new project moves
            // it even when the document did not change (a reload, or an edit that only rebuilt).
            InvalidateArrange();
            RequestRender();
        }

        /// <summary>Schedules a compose pass. Safe to call from any thread and self-coalescing —
        /// frames arrive from decode threads far faster than the display refreshes.</summary>
        public void RequestRender()
        {
            if (Interlocked.Exchange(ref _renderPending, 1) == 1)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _renderPending, 0);
                _surface.InvalidateVisual();
            }, DispatcherPriority.Render);
        }

        private void OnFrameArrived()
        {
            if (!_sawFirstFrame)
            {
                _sawFirstFrame = true;
                Dispatcher.UIThread.Post(() => PosterImage.IsVisible = false);
            }

            RequestRender();
        }

        /// <summary>Sets the screen frame size in pixels once the media is open.</summary>
        public void SetVideo(Size pixelSize)
        {
            _videoPixelSize = pixelSize;
            InvalidateArrange();
            RequestRender();
        }

        /// <summary>Declares whether the media has a usable webcam track and its aspect (h/w).</summary>
        public void SetWebcam(bool hasWebcam, double aspectHeightOverWidth)
        {
            _hasWebcam = hasWebcam;
            if (aspectHeightOverWidth > 0)
                Overlay.WebcamAspect = aspectHeightOverWidth;

            UpdateOverlayVisibility();
            InvalidateArrange();
            RequestRender();
        }

        private void Document_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WebcamOverlay.Enabled))
                UpdateOverlayVisibility();

            // shape/radius changes do not move the gizmo, so its arrange (where the outline is
            // refreshed) would be skipped — refresh it explicitly
            if (e.PropertyName is nameof(WebcamOverlay.Shape) or nameof(WebcamOverlay.CornerRadius))
                Overlay.RefreshShape();

            // any webcam geometry (or trim/cut, harmlessly) change re-positions the gizmo
            InvalidateArrange();
        }

        private void UpdateOverlayVisibility()
        {
            Overlay.IsVisible = _hasWebcam && _document?.Webcam.Enabled == true;
        }

        /// <summary>The composed picture, drawn under the poster and the gizmo. It reads the
        /// preview's current player/project/video rect at render time — the draw operation itself
        /// then carries that snapshot to the render thread.</summary>
        private sealed class PreviewSurface : Control
        {
            private readonly VideoPreviewControl _owner;

            public PreviewSurface(VideoPreviewControl owner)
            {
                _owner = owner;
            }

            public override void Render(DrawingContext context)
            {
                base.Render(context);

                var player = _owner._player;
                var project = _owner._project;
                if (player == null || project == null)
                    return;

                var videoRect = _owner.VideoRect;
                if (videoRect.Width < 1 || videoRect.Height < 1)
                    return;

                context.Custom(new PreviewDrawOperation(
                    new Rect(Bounds.Size), videoRect, _owner._gpu, player, project));
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);

            if (_player != null)
            {
                _player.FrameSource.FrameArrived -= OnFrameArrived;
                _player = null;
            }

            // drops the control's reference; the last draw operation Avalonia disposes releases
            // the textures on its own thread.
            _gpu.Shutdown();
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
            _surface.Arrange(full);
            PosterImage.Arrange(full);

            var videoRect = ComputeVideoRect(finalSize);
            VideoRect = videoRect;

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
        /// The gizmo rectangle inside <paramref name="videoRect"/>: exactly where the composer
        /// draws the webcam row, taken from the very <see cref="ModelTransform"/> it draws it with
        /// (<see cref="EditorProject.WebcamTransformOf"/> — the project the preview is composing,
        /// not the document, so no placement math is duplicated here and the two cannot drift).
        ///
        /// Deliberately unclamped: <c>FrameComposer</c> bounds the picture to nothing
        /// (<c>destH = Scale * canvasWidth * imgH / imgW</c>, placed on the normalized centre), so
        /// an overlay taller than the frame — a 4:3 camera at width &gt; 0.75 of a 16:9 recording,
        /// or any wide-strip region capture — really does bleed past the top and bottom edges and
        /// is merely clipped. Clamping the gizmo's height (as this used to) put the outline, the
        /// mask preview and the corner handles on a rectangle shorter than the composed picture.
        /// </summary>
        private Rect ComputeOverlayRect(Rect videoRect)
        {
            if (videoRect.Width <= 0 || videoRect.Height <= 0)
                return new Rect(0, 0, 0, 0);

            // no project yet (or no webcam row in it) means nothing is being composed for the
            // gizmo to sit on.
            var transform = EditorProject.WebcamTransformOf(_project);
            if (transform == null)
                return new Rect(0, 0, 0, 0);

            var aspect = Overlay.WebcamAspect > 0 ? Overlay.WebcamAspect : 9.0 / 16.0;
            var placed = WebcamPlacement.Compose(transform, aspect, videoRect.Width, videoRect.Height);
            // Arrange rejects a non-finite or negative rect, and a project loaded from disk carries
            // whatever numbers the file did.
            if (!(placed.W > 0) || !(placed.H > 0) || !Double.IsFinite(placed.X) || !Double.IsFinite(placed.Y))
                return new Rect(0, 0, 0, 0);

            return new Rect(videoRect.X + placed.X, videoRect.Y + placed.Y, placed.W, placed.H);
        }
    }

    /// <summary>
    /// Where <c>FrameComposer</c> puts a picture item on a canvas — the placement half of
    /// <c>FrameComposer.DrawPicture</c>/<c>PlaceRect</c>, as pure geometry so the preview chrome
    /// can be positioned (and unit-tested against composed pixels) without a renderer.
    ///
    /// This exists because the webcam gizmo has to land on the composed picture to the pixel: the
    /// picture's placement is <b>not</b> the frame-clamped rect
    /// <c>VideoRenderManager.ComputeWebcamRect</c> computes — that rect is only normalized into the
    /// item's <see cref="ModelTransform"/> (centre + width fraction, its height discarded by
    /// <c>RecordingProject.WebcamTransform</c>), and the composer then re-derives the height from
    /// the camera's own aspect with no canvas bound at all.
    /// </summary>
    internal static class WebcamPlacement
    {
        /// <param name="transform">The item's transform — normalized centre and width fraction.</param>
        /// <param name="pictureAspect">The drawn picture's height/width (the camera's own aspect;
        /// the composer takes it from the decoded frame).</param>
        /// <param name="canvasWidth">Canvas width; for the preview, the letterboxed video rect.</param>
        /// <param name="canvasHeight">Canvas height.</param>
        /// <returns>The dest rect in canvas coordinates. Its top/bottom may fall outside the
        /// canvas — exactly as the composed picture does, which is then simply clipped.</returns>
        public static (double X, double Y, double W, double H) Compose(
            ModelTransform transform, double pictureAspect, double canvasWidth, double canvasHeight)
        {
            ArgumentNullException.ThrowIfNull(transform);

            // FrameComposer.DrawPicture: Scale is a fraction of the canvas *width*, the height
            // follows the picture's aspect …
            double w = transform.Scale * canvasWidth;
            double h = w * pictureAspect;

            // … and PlaceRect centres that size on the normalized transform.
            double cx = transform.X * canvasWidth;
            double cy = transform.Y * canvasHeight;

            return (cx - w / 2, cy - h / 2, w, h);
        }
    }
}
