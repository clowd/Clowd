using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Prisc.Config;
using Prisc.VideoSDK.Composition;
using SkiaSharp;

namespace Prisc.UI.DrawOnScreen
{
    /// <summary>
    /// The keystroke rows over one screen's drawing: a sibling layered above the
    /// <see cref="Prisc.Drawing.DrawingCanvas"/> in <see cref="DrawOnScreenWindow"/>, not a graphic
    /// on it, so the keys never enter the ink's history, are untouched by hiding or clearing the
    /// ink, and never take the mouse. The drawing is the video editor's own keyboard-track
    /// pipeline (<see cref="LiveKeystrokes"/> over <c>KeystrokeRenderer</c>), handed Avalonia's
    /// Skia canvas.
    /// <para>
    /// <see cref="Keys"/> is UI-thread state, and a custom draw operation renders on the render
    /// thread, so each frame is recorded into an <see cref="SKPicture"/> here and only the picture
    /// crosses over. Frames are requested only while something changes: every frame while a row
    /// animates, a single timer to the moment a lingering row starts to leave, nothing at rest.
    /// </para>
    /// </summary>
    internal sealed class KeystrokeOverlay : Control
    {
        private DispatcherTimer _wake;
        private bool _frameRequested;

        public KeystrokeOverlay()
        {
            IsHitTestVisible = false;
            ClipToBounds = true;
        }

        /// <summary>The rows to draw, or null to draw nothing (keys off, or another screen is
        /// showing them).</summary>
        public LiveKeystrokes Keys { get; private set; }

        /// <summary>The clock <see cref="Keys"/> is fed on.</summary>
        public Func<double> Clock { get; private set; }

        /// <summary>Where on the screen the block sits, and how wide and opaque it is.</summary>
        public SettingsDrawOnScreen Settings { get; private set; }

        /// <summary>The screen's working area in this control's coordinates (DIPs), so the block
        /// keeps clear of the taskbar or the menu bar and Dock. Empty means the whole control.</summary>
        public Rect WorkArea { get; private set; }

        /// <summary>Points the overlay at a key source (null to blank it).</summary>
        public void SetSource(LiveKeystrokes keys, Func<double> clock, SettingsDrawOnScreen settings)
        {
            Keys = keys;
            Clock = clock;
            Settings = settings;
            Refresh();
        }

        public void SetWorkArea(Rect workArea)
        {
            if (WorkArea == workArea)
                return;
            WorkArea = workArea;
            Refresh();
        }

        /// <summary>Something the drawing depends on changed (a key, a setting): redraw now and
        /// reschedule.</summary>
        public void Refresh()
        {
            _wake?.Stop();
            InvalidateVisual();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _wake?.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        public override void Render(DrawingContext context)
        {
            var keys = Keys;
            var settings = Settings;
            if (keys == null || settings == null || Clock == null || Bounds.Width <= 0 || Bounds.Height <= 0)
                return;

            double now = Clock();
            var area = WorkArea.Width > 0 && WorkArea.Height > 0 ? WorkArea : new Rect(Bounds.Size);
            var (x, y, alignment, stacking) = Anchor(settings.Region, area, Math.Max(0, settings.Margin), settings.FontSize);

            using (var recorder = new SKPictureRecorder())
            {
                var canvas = recorder.BeginRecording(new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));
                keys.Draw(canvas, now, 1f, (float)x, (float)y, (float)(Math.Clamp(settings.Width, 0, 1) * area.Width),
                    alignment, stacking, Math.Clamp(settings.Opacity, 0, 1));
                var picture = recorder.EndRecording();
                context.Custom(new PictureDrawOperation(new Rect(Bounds.Size), picture));
            }

            Schedule(keys.NextChangeMs(now), now);
        }

        /// <summary>
        /// Where a region puts the block: the column picks the anchor x and the line alignment, the
        /// row picks the anchor y and which way rows stack. Top rows hang from the top and stack
        /// downward, so the newest row is always the one nearest its edge; the middle row stacks
        /// upward from roughly half a row below the center, so a lone row sits centered.
        /// <paramref name="margin"/> insets the block from whichever edges it is against; a
        /// centered axis has no edge and ignores it.
        /// </summary>
        private static (double X, double Y, KeystrokeAlignment Alignment, KeystrokeStacking Stacking) Anchor(
            DrawKeystrokeRegion region, Rect area, double margin, double fontSize)
        {
            var (x, alignment) = region switch
            {
                DrawKeystrokeRegion.TopLeft or DrawKeystrokeRegion.Left or DrawKeystrokeRegion.BottomLeft
                    => (area.Left + margin, KeystrokeAlignment.Left),
                DrawKeystrokeRegion.TopRight or DrawKeystrokeRegion.Right or DrawKeystrokeRegion.BottomRight
                    => (area.Right - margin, KeystrokeAlignment.Right),
                _ => (area.Center.X, KeystrokeAlignment.Center),
            };

            var (y, stacking) = region switch
            {
                DrawKeystrokeRegion.TopLeft or DrawKeystrokeRegion.Top or DrawKeystrokeRegion.TopRight
                    => (area.Top + margin, KeystrokeStacking.Down),
                DrawKeystrokeRegion.Left or DrawKeystrokeRegion.Center or DrawKeystrokeRegion.Right
                    // a row is ~1.8 font sizes tall (line pitch plus the pill's padding)
                    => (area.Center.Y + 0.9 * fontSize, KeystrokeStacking.Up),
                _ => (area.Bottom - margin, KeystrokeStacking.Up),
            };

            return (x, y, alignment, stacking);
        }

        private void Schedule(double? nextMs, double nowMs)
        {
            if (nextMs is not { } next)
                return;

            if (next <= nowMs)
            {
                if (_frameRequested)
                    return;
                var top = TopLevel.GetTopLevel(this);
                if (top == null)
                    return;
                _frameRequested = true;
                top.RequestAnimationFrame(_ =>
                {
                    _frameRequested = false;
                    InvalidateVisual();
                });
                return;
            }

            _wake ??= new DispatcherTimer();
            _wake.Stop();
            _wake.Interval = TimeSpan.FromMilliseconds(Math.Max(1, next - nowMs));
            _wake.Tick -= OnWake;
            _wake.Tick += OnWake;
            _wake.Start();
        }

        private void OnWake(object sender, EventArgs e)
        {
            _wake.Stop();
            InvalidateVisual();
        }

        /// <summary>Plays a recorded frame onto Avalonia's Skia canvas, which already carries the
        /// window's DIP-to-pixel scale, so the text rasterizes at the screen's real
        /// resolution.</summary>
        private sealed class PictureDrawOperation : ICustomDrawOperation
        {
            private readonly SKPicture _picture;

            public PictureDrawOperation(Rect bounds, SKPicture picture)
            {
                Bounds = bounds;
                _picture = picture;
            }

            public Rect Bounds { get; }

            public bool HitTest(Point p) => false;

            public bool Equals(ICustomDrawOperation other) => false;

            public void Dispose() => _picture.Dispose();

            public void Render(ImmediateDrawingContext context)
            {
                var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (feature == null)
                    return;

                using var lease = feature.Lease();
                lease.SkCanvas?.DrawPicture(_picture);
            }
        }
    }
}
