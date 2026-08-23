using System;
using Clowd.VideoSDK.Playback;

namespace Clowd.UI.VideoEditor.Timeline
{
    /// <summary>
    /// The timeline's shared horizontal view state — zoom (<see cref="TicksPerPixel"/>), scroll
    /// offset (<see cref="ScrollTicks"/>), the width being drawn into and the project duration.
    /// The ruler, the drawing surface and the horizontal scroll bar all read one instance of this
    /// and redraw on <see cref="Changed"/>, which is what keeps them pixel-aligned.
    ///
    /// Every value is kept legal by <see cref="TimelineViewMath"/> — this class owns no clamping
    /// logic of its own, it only decides <i>when</i> to re-clamp (a duration or width change can
    /// invalidate a zoom/scroll that was fine a moment ago). Not thread-safe: UI thread only.
    /// </summary>
    internal sealed class TimelineViewport
    {
        private double _ticksPerPixel = TimelineViewMath.DefaultTicksPerPixel;
        private long _scrollTicks;
        private double _viewportWidth;
        private long _durationTicks;
        private TimeWarp _warp;

        /// <summary>Raised after any observable change, already coalesced per public call — one
        /// event per operation, never one per clamped field.</summary>
        public event EventHandler Changed;

        /// <summary>Zoom, as 100ns ticks per device-independent pixel. Larger = further out.</summary>
        public double TicksPerPixel => _ticksPerPixel;

        /// <summary>Timeline tick shown at x = 0.</summary>
        public long ScrollTicks => _scrollTicks;

        /// <summary>Width of the drawing area, in pixels — the surface reports it from its
        /// arrange pass.</summary>
        public double ViewportWidth => _viewportWidth;

        /// <summary>The project's length. Bounds the zoom-out limit and the scroll range.</summary>
        public long DurationTicks => _durationTicks;

        /// <summary>The speed warp the ruler labels through, or null while nothing bends time. It
        /// touches no coordinate on this axis — x stays project time, so items, the playhead and
        /// every drag are measured exactly as they were — only what the ruler <i>calls</i> each
        /// instant.</summary>
        public TimeWarp Warp => _warp;

        /// <summary>Length of the visible span, in ticks.</summary>
        public long VisibleTicks => (long)Math.Round(Math.Max(0, _viewportWidth) * _ticksPerPixel);

        /// <summary>Exclusive end of the visible span, in timeline ticks.</summary>
        public long ScrollEndTicks => _scrollTicks + VisibleTicks;

        public double TickToX(long ticks) => TimelineViewMath.TickToX(ticks, _scrollTicks, _ticksPerPixel);

        public long XToTicks(double x) => TimelineViewMath.XToTicks(x, _scrollTicks, _ticksPerPixel);

        public long XToTicksClamped(double x) =>
            TimelineViewMath.XToTicksClamped(x, _scrollTicks, _ticksPerPixel, _durationTicks);

        /// <summary>The snap/grab tolerance at the current zoom, in ticks.</summary>
        public long ToleranceTicks => TimelineViewMath.ToleranceTicks(_ticksPerPixel);

        /// <summary>Reports the drawing width. Re-clamps zoom and scroll: a wider viewport lowers
        /// the zoom-out limit (the whole project now fits at a finer scale) and can leave the
        /// current offset past the end.</summary>
        public void SetViewportWidth(double width)
        {
            if (!(width >= 0))
                width = 0;
            if (_viewportWidth == width)
                return;

            _viewportWidth = width;
            Reclamp();
        }

        /// <summary>Reports the project duration (see <c>Project.GetDurationTicks</c>).</summary>
        public void SetDuration(long durationTicks)
        {
            if (durationTicks < 0)
                durationTicks = 0;
            if (_durationTicks == durationTicks)
                return;

            _durationTicks = durationTicks;
            Reclamp();
        }

        /// <summary>Reports the project's speed warp (see <see cref="Warp"/>). Redraws only when
        /// the mapping actually moved: rebuilding the warp is part of every project change, and an
        /// edit that touches no speed item produces an equal one.</summary>
        public void SetWarp(TimeWarp warp)
        {
            var was = _warp;
            _warp = warp;

            var wasIdentity = was == null || was.IsIdentity;
            var isIdentity = warp == null || warp.IsIdentity;
            if (wasIdentity && isIdentity)
                return;
            if (!wasIdentity && !isIdentity && was.MappingEquals(warp))
                return;

            RaiseChanged();
        }

        /// <summary>
        /// Zooms so the tick currently under <paramref name="anchorX"/> stays under it — the
        /// Ctrl+wheel gesture. The requested zoom is clamped first, so the anchor stays honest even
        /// at the limits, and the resulting scroll is clamped after (an anchor near either end of a
        /// short project can push the view out of range).
        /// </summary>
        public void SetZoomAnchored(double ticksPerPixel, double anchorX)
        {
            var zoom = TimelineViewMath.ClampZoom(ticksPerPixel, _durationTicks, _viewportWidth);
            if (zoom == _ticksPerPixel)
                return;

            // a wheel event can land outside the drawing area (the track-header column maps to
            // negative surface x, the vertical scroll bar past the width); keeping an off-screen
            // tick stationary would pan the view instead of zooming it, so anchor at the nearest
            // visible edge instead.
            anchorX = Math.Clamp(anchorX, 0, Math.Max(0, _viewportWidth));

            var scroll = TimelineViewMath.ScrollForAnchoredZoom(_scrollTicks, _ticksPerPixel, zoom, anchorX);
            _ticksPerPixel = zoom;
            _scrollTicks = TimelineViewMath.ClampScroll(scroll, zoom, _durationTicks, _viewportWidth);
            RaiseChanged();
        }

        /// <summary>Zooms out until the whole project fits and returns to the origin.</summary>
        public void ZoomToFit()
        {
            var zoom = TimelineViewMath.FitTicksPerPixel(_durationTicks, _viewportWidth);
            if (zoom == _ticksPerPixel && _scrollTicks == 0)
                return;

            _ticksPerPixel = zoom;
            _scrollTicks = 0;
            RaiseChanged();
        }

        /// <summary>Returns to <see cref="TimelineViewMath.DefaultTicksPerPixel"/> (the zoom the
        /// editor opens at) around <paramref name="anchorX"/>, so whatever the user was looking at
        /// stays under that x rather than the view jumping back to the origin. Pass 0 — or nothing —
        /// to keep the left edge.</summary>
        public void ResetZoom(double anchorX = 0)
        {
            var zoom = TimelineViewMath.ClampZoom(TimelineViewMath.DefaultTicksPerPixel,
                _durationTicks, _viewportWidth);
            if (zoom == _ticksPerPixel)
                return;

            anchorX = Math.Clamp(anchorX, 0, Math.Max(0, _viewportWidth));
            var scroll = TimelineViewMath.ScrollForAnchoredZoom(_scrollTicks, _ticksPerPixel, zoom, anchorX);
            _ticksPerPixel = zoom;
            _scrollTicks = TimelineViewMath.ClampScroll(scroll, zoom, _durationTicks, _viewportWidth);
            RaiseChanged();
        }

        /// <summary>Scrolls by a tick delta (Shift+wheel, scroll bar).</summary>
        public void ScrollBy(long deltaTicks) => ScrollToTicks(_scrollTicks + deltaTicks);

        /// <summary>Scrolls by a pixel delta at the current zoom.</summary>
        public void ScrollByPixels(double deltaPx) =>
            ScrollBy((long)Math.Round(deltaPx * _ticksPerPixel));

        public void ScrollToTicks(long scrollTicks)
        {
            var clamped = TimelineViewMath.ClampScroll(scrollTicks, _ticksPerPixel, _durationTicks, _viewportWidth);
            if (clamped == _scrollTicks)
                return;

            _scrollTicks = clamped;
            RaiseChanged();
        }

        /// <summary>
        /// Scrolls the minimum amount that brings <paramref name="ticks"/> inside the viewport with
        /// <paramref name="marginPx"/> of room on the leading side — the playhead-follow path during
        /// playback, so the playhead never sits welded to the right border. A no-op when it is
        /// already comfortably visible.
        /// </summary>
        public void EnsureVisible(long ticks, double marginPx = 40)
        {
            if (!(_viewportWidth > 0) || !(_ticksPerPixel > 0))
                return;

            var margin = (long)Math.Round(Math.Max(0, marginPx) * _ticksPerPixel);
            var span = VisibleTicks;
            if (span <= 0)
                return;

            // a margin wider than half the viewport would fight itself (both sides pulling), so
            // give it at most a third of the span.
            margin = Math.Min(margin, span / 3);

            if (ticks < _scrollTicks + margin)
                ScrollToTicks(ticks - margin);
            else if (ticks > _scrollTicks + span - margin)
                ScrollToTicks(ticks - span + margin);
        }

        // A width or duration change re-clamps both: a wider viewport (or a shorter project) fits
        // the whole timeline at a finer scale, and the zoom-out limit IS that fit — a view zoomed
        // out past it would leave dead space after the end of the project. A zoom tighter than fit
        // is untouched, so an ordinary resize only changes how much is on screen.
        private void Reclamp()
        {
            var zoom = TimelineViewMath.ClampZoom(_ticksPerPixel, _durationTicks, _viewportWidth);
            var scroll = TimelineViewMath.ClampScroll(_scrollTicks, zoom, _durationTicks, _viewportWidth);

            _ticksPerPixel = zoom;
            _scrollTicks = scroll;
            RaiseChanged();
        }

        private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
