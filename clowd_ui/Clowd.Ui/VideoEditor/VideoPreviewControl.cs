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
    /// gizmo, which is re-positioned FROM the document on every layout pass and every document
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
        /// The gizmo rectangle inside <paramref name="videoRect"/>, mirroring
        /// <see cref="Clowd.UI.Services.VideoRenderManager.ComputeWebcamRect"/> (which is also what
        /// <see cref="EditorProject"/> normalizes into the webcam items' transform): width is the
        /// document fraction of the video width, height follows the webcam aspect, and the rect is
        /// nudged fully inside the frame rather than clipped. So the gizmo outline lands exactly on
        /// the composed picture.
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
