using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// The letterboxed preview surface. It computes the video rectangle by hand (the
    /// Stretch.Uniform math) rather than letting an Image do it, because the composed picture and
    /// the transform gizmo must both be positioned against the *video* rectangle, not the control
    /// bounds. The picture itself is drawn by <see cref="PreviewDrawOperation"/> — the whole
    /// project composed by the SDK's <c>FrameComposer</c>, which is the same code the render runs,
    /// so the preview is WYSIWYG by construction.
    ///
    /// Hosts, in z-order: the composed video (this control's own render), an optional poster image
    /// (shown until the first decoded frame lands) and the <see cref="Gizmo"/>, which is
    /// re-positioned on every layout pass — and the preview re-runs that pass on every project
    /// change, selection change and playhead move. That is what keeps gizmo drags, the inspector's
    /// numerics and the composed picture in lockstep: all three read the one model in
    /// <see cref="Session"/>, and none of them caches an item.
    ///
    /// A press that reaches the panel itself (not the gizmo) selects whatever item is composed under
    /// the pointer at the playhead, or clears the selection on bare canvas.
    /// </summary>
    public sealed class VideoPreviewControl : Panel
    {
        private readonly PreviewGpuState _gpu = new PreviewGpuState();
        private CompositionPlayer _player;
        private EditorSession _session;
        private Project _project;
        private Size _videoPixelSize;
        private long _positionTicks;
        private int _renderPending;
        private bool _sawFirstFrame;

        private readonly PreviewSurface _surface;

        public VideoPreviewControl()
        {
            // hit-testable background so a click on the letterbox (not the gizmo) can deselect
            Background = Brushes.Transparent;

            // the gizmo follows the composed picture, which the composer does NOT bound to the
            // frame (see ResolveGizmoRect), so an item taller than the frame really is arranged
            // past this control's edges — clip it here rather than letting the chrome draw over
            // the timeline and the sidebar.
            ClipToBounds = true;

            // Panel.Render is sealed, so the composed picture is drawn by a dedicated (bottom)
            // child rather than by the panel itself.
            _surface = new PreviewSurface(this) { IsHitTestVisible = false };
            PosterImage = new Image { Stretch = Stretch.Uniform };
            Gizmo = new TransformGizmoControl();

            Children.Add(_surface);
            Children.Add(PosterImage);
            Children.Add(Gizmo);

            PointerPressed += OnCanvasPressed;
        }

        /// <summary>Poster/loading image shown until the first decoded frame lands.</summary>
        public Image PosterImage { get; }

        /// <summary>The transform gizmo (outline + handles only — the picture is composed). It has
        /// no geometry of its own: this control resolves the selected item's composed rect and
        /// arranges the gizmo onto it, or onto nothing when there is no gizmo to show.</summary>
        public TransformGizmoControl Gizmo { get; }

        /// <summary>The letterboxed video rectangle from the last arrange, in local coordinates.</summary>
        public Rect VideoRect { get; private set; }

        /// <summary>
        /// The editing session the gizmo and the click hit-test read. The preview follows its
        /// project and selection changes itself (the window only feeds it composed snapshots and the
        /// playhead), because gizmo placement depends on all three.
        /// </summary>
        public EditorSession Session
        {
            get => _session;
            set
            {
                if (ReferenceEquals(_session, value))
                    return;

                if (_session != null)
                {
                    _session.ProjectChanged -= Session_ProjectChanged;
                    _session.SelectionChanged -= Session_SelectionChanged;
                }

                _session = value;

                if (_session != null)
                {
                    _session.ProjectChanged += Session_ProjectChanged;
                    _session.SelectionChanged += Session_SelectionChanged;
                }

                Gizmo.Session = value;
                InvalidateArrange();
            }
        }

        /// <summary>The playhead, in timeline ticks — the gizmo only shows while the selected item's
        /// span covers it (an item that is not on screen must not be draggable on screen). Pushed by
        /// the window's position readout, which is the one funnel every position change passes
        /// through.</summary>
        public long PositionTicks
        {
            get => _positionTicks;
            set
            {
                if (_positionTicks == value)
                    return;

                _positionTicks = value;
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

        /// <summary>Points the preview at the current project snapshot (every edit produces a
        /// fresh one).</summary>
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

        // ====================================================================
        // Session reactions
        // ====================================================================

        /// <summary>Any change can move the selected item, resize it, mask it or delete it — and an
        /// undo can do all four — so the gizmo is re-resolved from the model on every one, including
        /// mid-gesture previews (that is how a drag's own writes come back to it, and how the
        /// inspector and the gizmo stay in step during either one's drag).</summary>
        private void Session_ProjectChanged(object sender, ProjectChangedEventArgs e)
        {
            Gizmo.RefreshChrome(); // a mask change alone does not move the gizmo
            InvalidateArrange();
        }

        private void Session_SelectionChanged(object sender, EventArgs e) => InvalidateArrange();

        /// <summary>Click-to-select: a press reaches the panel when the gizmo did not claim it —
        /// either it landed outside the gizmo, or the gizmo declined it because another item is
        /// composed over it there (a full-frame selection must not swallow a click aimed at an
        /// overlay). It then selects the topmost item composed under the pointer at the playhead,
        /// and clears the selection when it lands on bare canvas.</summary>
        private void OnCanvasPressed(object sender, PointerPressedEventArgs e)
        {
            if (_session == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            var videoRect = VideoRect;
            if (videoRect.Width <= 0 || videoRect.Height <= 0)
                return;

            var p = e.GetPosition(this);
            var hit = ItemPlacement.HitTest(_session.Project, ComposedTicks(_session.Project),
                p.X - videoRect.X, p.Y - videoRect.Y, videoRect.Width, videoRect.Height);

            if (hit != null)
                _session.Select(hit.Id);
            else
                _session.ClearSelection();
        }

        /// <summary>The instant the preview is composing: the playhead, held one tick inside the
        /// last item at the very end of the timeline — items are half-open, so composing at the
        /// duration itself would draw nothing (see <see cref="PreviewDrawOperation"/>, which holds
        /// the same frame).</summary>
        private long ComposedTicks(Project project)
        {
            var duration = project?.GetDurationTicks() ?? 0;
            return duration > 0 && _positionTicks >= duration ? duration - 1 : _positionTicks;
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

            Session = null;

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

            Gizmo.CanvasRect = videoRect;
            Gizmo.Arrange(ResolveGizmoRect(videoRect));

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
        /// Where the gizmo belongs this pass, in local coordinates — exactly where the composer
        /// draws the primary selection (<see cref="ItemPlacement.TryResolve"/> reads the very
        /// <c>Transform</c> the picture is drawn with, so no placement math is duplicated here and
        /// the two cannot drift). An empty rect is how "no gizmo" is expressed: a zero-sized panel
        /// draws nothing and cannot be hit.
        ///
        /// Deliberately unclamped: <c>FrameComposer</c> bounds a picture to nothing (its height
        /// follows the content's aspect from a canvas-width fraction), so an item taller than the
        /// frame — a 4:3 camera at width &gt; 0.75 of a 16:9 recording — really does bleed past the
        /// edges and is merely clipped. Clamping the gizmo (as v1 did) put the outline, the mask
        /// preview and the corner handles on a rectangle shorter than the composed picture.
        /// </summary>
        private Rect ResolveGizmoRect(Rect videoRect)
        {
            var empty = new Rect(0, 0, 0, 0);
            var session = _session;
            if (session == null || videoRect.Width <= 0 || videoRect.Height <= 0)
            {
                Gizmo.SetTarget(Guid.Empty, default);
                return empty;
            }

            var project = session.Project;
            var item = session.PrimarySelectedItem;

            // visual content only, on a row that is actually being composed and editable, and only
            // while the playhead is inside the item's span — dragging a picture that is not on
            // screen would move something the user cannot see. A locked row gets no gizmo for the
            // same reason the timeline gives its items no drag affordance; clicking still selects
            // it, exactly as the timeline does.
            Track track = null;
            if (item != null)
            {
                foreach (var candidate in project.Tracks)
                {
                    if (candidate.Id == item.TrackId)
                    {
                        track = candidate;
                        break;
                    }
                }
            }

            var ticks = ComposedTicks(project);
            Gizmo.ComposedTicks = ticks;

            if (item == null || track is not { Kind: TrackKind.Video, Hidden: false, Locked: false } ||
                ticks < item.TimelineStartTicks || ticks >= item.TimelineEndTicks ||
                !ItemPlacement.TryResolve(project, item, videoRect.Width, videoRect.Height, out var placed))
            {
                Gizmo.SetTarget(Guid.Empty, default);
                return empty;
            }

            Gizmo.SetTarget(item.Id, placed);

            // inflated by the gizmo's handle pad: a control only receives presses inside its own
            // bounds, and the corner handles straddle the item's corners (the gizmo deflates this
            // again for everything it draws and hit-tests).
            return new Rect(videoRect.X + placed.X, videoRect.Y + placed.Y, placed.W, placed.H)
                .Inflate(TransformGizmoControl.HandlePad);
        }
    }
}
