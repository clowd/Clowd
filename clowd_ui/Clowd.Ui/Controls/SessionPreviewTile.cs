using System;
using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Clowd.UI.Preview;
using Clowd.UI.Preview.Producers;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// The 110x75 thumbnail in a Recent list row. Draws whatever <see cref="SessionPreviewEngine"/>
    /// has for the session — a real picture on a checkerboard, or a file-type icon — and asks the
    /// engine for one at a priority that tracks how close the row is to the viewport.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaces a <c>Panel</c> + two <c>Border</c>s + <c>Image</c> + <c>Viewbox</c> + <c>Path</c>
    /// stack driven by two value converters. The reason it is a control with a real
    /// <see cref="SessionProperty"/> rather than a template is identity: the page's
    /// <c>RebuildGroups</c> destroys and recreates every container on any property change of any
    /// session, so nothing may be keyed on a container's lifetime. A styled property re-evaluates
    /// when the DataContext changes, and the engine is keyed on the session directory, which never
    /// moves.
    /// </para>
    /// <para>
    /// The tile touches no disk on any path, and <see cref="Render"/> never calls into the engine —
    /// it only draws a bitmap somebody else already decoded.
    /// </para>
    /// </remarks>
    public sealed class SessionPreviewTile : Control
    {
        /// <summary>Logical size of the file-type icon. Deliberately larger than the 36x36 Viewbox
        /// this replaced: that size was chosen for a flat monochrome glyph standing in for a
        /// missing preview, and it leaves a colour illustration marooned in the middle of a 110x75
        /// tile. These icons ARE the content of the row, not an apology for its absence, so they
        /// fill the tile's height the way a real preview does, minus a little air.
        /// Lives on <see cref="PreviewFormat"/> because FileIconPreviewProducer quantizes its
        /// raster size off this number — drawing larger than it rasterizes is how icons go soft.</summary>
        private const double IconLogicalSize = PreviewFormat.IconLogicalPx;

        public static readonly StyledProperty<SessionInfo> SessionProperty =
            AvaloniaProperty.Register<SessionPreviewTile, SessionInfo>(nameof(Session));

        /// <summary>AppStyles builds this once and hands the same instance back from behind a lock.
        /// The brush is immutable and this is only ever read on the UI thread, so the tile keeps the
        /// reference rather than taking that lock once per photo row per frame.</summary>
        private static IBrush _checkerboard;

        public SessionInfo Session
        {
            get => GetValue(SessionProperty);
            set => SetValue(SessionProperty, value);
        }

        private SessionInfo _hooked;
        private bool _attached;

        private PreviewKey _key;
        private bool _hasKey;

        /// <summary>
        /// A tile that has been realized but never given a viewport sits in the buffer band below the
        /// fold: it is worth producing, but it must never outrank a row the user is actually looking
        /// at, and it must never be classified as something to drop.
        /// </summary>
        private PreviewPriority _band = PreviewPriority.BufferBelow;

        /// <summary>True once the current key either has a bitmap or has live work behind it. This is
        /// what makes the viewport handler's early-out safe — see <see cref="OnViewportChanged"/>.</summary>
        private bool _settled;

        private Bitmap _bitmap;
        private PreviewKind _kind;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _attached = true;

            // Icons rasterize to one process-wide pixel size, and the tile is the only participant
            // that can read RenderScaling (a UI-thread property on the top level). Doing it here
            // rather than once at startup also covers a window dragged between monitors.
            var top = TopLevel.GetTopLevel(this);
            if (top != null)
                FileIconPreviewProducer.RenderScale = top.RenderScaling;

            EffectiveViewportChanged += OnViewportChanged;
            SessionPreviewEngine.Current.PreviewReady += OnPreviewReady;
            HookSession();

            // Adopt the key and take whatever is already decoded, but start no work: there is no
            // viewport yet, so there is no honest band to ask at. The first layout pass raises
            // EffectiveViewportChanged and that is what requests.
            ResetKey();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _attached = false;

            EffectiveViewportChanged -= OnViewportChanged;
            SessionPreviewEngine.Current.PreviewReady -= OnPreviewReady;
            UnhookSession();

            // Releasing is what lets a job with no remaining subscriber reach the engine's grace
            // reaper. Leaking it would pin the work forever behind a tile that no longer exists —
            // and this page recreates every container on a 250 ms debounce, so a leak here compounds
            // very quickly.
            ReleaseKey();

            _bitmap = null;
            _kind = PreviewKind.None;
            _band = PreviewPriority.BufferBelow;
            _settled = false;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property != SessionProperty)
                return;

            UnhookSession();
            if (_attached)
                HookSession();

            ResetKey();

            // Unlike attach, a session swap on a live tile keeps the classification it already has:
            // the row did not move, only its content did.
            if (_attached)
                Sync();

            InvalidateVisual();
        }

        /// <summary>
        /// Fires on every layout pass, for every listener in the tree, so this is a hot path. It must
        /// stay pure rectangle arithmetic with no allocation and no disk, and it must bail before
        /// doing anything else when the band has not moved.
        /// </summary>
        private void OnViewportChanged(object sender, EffectiveViewportChangedEventArgs e)
        {
            // Both rectangles are in this control's own coordinate space, so a row below the fold
            // sees a viewport whose Bottom is negative and a row above it sees one whose Bottom is
            // past our own height.
            var viewport = e.EffectiveViewport;
            var self = new Rect(Bounds.Size);

            var band = viewport.Intersects(self)
                ? PreviewPriority.Visible
                : self.Top >= viewport.Bottom
                    ? PreviewPriority.BufferBelow
                    : PreviewPriority.BufferAbove;

            if (band == _band && _settled)
                return;

            _band = band;
            Sync();
        }

        /// <summary>
        /// Only <see cref="SessionInfo.ContentModifiedUtc"/> matters here: it is the stamp half of the
        /// cache key, so a change to it means the picture this row is drawing is of the old content.
        /// The re-request is per visible row — there is deliberately no global fan-out.
        /// </summary>
        private void OnSessionPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SessionInfo.ContentModifiedUtc))
                return;

            ResetKey();
            Sync();
            InvalidateVisual();
        }

        /// <summary>The engine hands out a whole drain batch at once; ours is in it or it is not.</summary>
        private void OnPreviewReady(object sender, PreviewReadyEventArgs e)
        {
            if (!_hasKey || !e.Contains(_key))
                return;

            if (!SessionPreviewEngine.Current.TryGetHot(_key, out var bmp, out var kind))
                return;

            _bitmap = bmp;
            _kind = kind;
            _settled = true;
            InvalidateVisual();
        }

        private void HookSession()
        {
            var session = Session;
            if (session == null || ReferenceEquals(session, _hooked))
                return;

            _hooked = session;
            _hooked.PropertyChanged += OnSessionPropertyChanged;
        }

        private void UnhookSession()
        {
            if (_hooked == null)
                return;

            _hooked.PropertyChanged -= OnSessionPropertyChanged;
            _hooked = null;
        }

        private void ReleaseKey()
        {
            if (!_hasKey)
                return;

            // The cached key, never the SessionInfo: SessionManager disposes a deleted session
            // immediately and the container outlives it until the next rebuild. Release is a no-op
            // for a key the engine has already finished, which is the common case.
            SessionPreviewEngine.Current.Release(_key, this);
            _hasKey = false;
        }

        /// <summary>Drops the old key and adopts the session's current one, taking a decoded preview
        /// with it if the hot cache happens to have one. Starts no work.</summary>
        private void ResetKey()
        {
            ReleaseKey();

            _bitmap = null;
            _kind = PreviewKind.None;
            _settled = false;

            var session = Session;
            if (session == null)
                return;

            try
            {
                var dir = Path.GetDirectoryName(session.FilePath);
                if (String.IsNullOrEmpty(dir))
                    return;

                _key = PreviewKey.For(dir, session.ContentModifiedUtc);
                _hasKey = true;
            }
            catch (ObjectDisposedException)
            {
                // A session deleted a moment ago and not yet swept out of the list. Its properties
                // throw; there is nothing to draw and nothing to ask for.
                return;
            }

            if (SessionPreviewEngine.Current.TryGetHot(_key, out var bmp, out var kind))
            {
                _bitmap = bmp;
                _kind = kind;
                _settled = true;
            }
        }

        /// <summary>Hot cache first, then a request at the current band. Both are cheap enough to run
        /// from a layout callback; neither touches disk.</summary>
        private void Sync()
        {
            if (!_hasKey)
                return;

            var engine = SessionPreviewEngine.Current;

            if (_bitmap == null && engine.TryGetHot(_key, out var bmp, out var kind))
            {
                _bitmap = bmp;
                _kind = kind;
                _settled = true;
                InvalidateVisual();
            }

            if (_bitmap != null)
                return;

            var session = Session;
            if (session == null)
                return;

            PreviewRequest request;
            try
            {
                request = SessionContentResolver.Snapshot(session);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            // Idempotent per (key, subscriber): the subscriber set is reference-identity, so
            // re-requesting on every band change promotes the job without drifting its refcount.
            engine.Request(_key, request, _band, this);
            _settled = true;
        }

        public override void Render(DrawingContext context)
        {
            var bitmap = _bitmap;
            if (bitmap == null)
                return;

            var bounds = new Rect(Bounds.Size);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            var source = new Rect(bitmap.Size);
            if (source.Width <= 0 || source.Height <= 0)
                return;

            if (_kind == PreviewKind.Photo)
            {
                // The checkerboard only ever backs a real picture — it is there to show a
                // screenshot's transparency, and behind an icon it would just be noise.
                context.FillRectangle(_checkerboard ??= AppStyles.CheckerboardBrushSmall, bounds);
                context.DrawImage(bitmap, source, FitUniform(bitmap.Size, bounds));
            }
            else if (_kind == PreviewKind.Icon)
            {
                // Centred on a transparent tile at full opacity. The old 0.5 was tuned for a
                // monochrome theme-coloured glyph; these are colour illustrations and dimming
                // them reads as a disabled row.
                var side = Math.Min(IconLogicalSize, Math.Min(bounds.Width, bounds.Height));
                var box = new Rect(
                    bounds.X + (bounds.Width - side) / 2,
                    bounds.Y + (bounds.Height - side) / 2,
                    side,
                    side);
                context.DrawImage(bitmap, source, FitUniform(bitmap.Size, box));
            }
        }

        private static Rect FitUniform(Size source, Rect box)
        {
            var scale = Math.Min(box.Width / source.Width, box.Height / source.Height);
            var width = source.Width * scale;
            var height = source.Height * scale;

            return new Rect(
                box.X + (box.Width - width) / 2,
                box.Y + (box.Height - height) / 2,
                width,
                height);
        }
    }
}
